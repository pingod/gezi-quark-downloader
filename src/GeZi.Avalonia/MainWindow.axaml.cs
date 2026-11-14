// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;
using GeZi.Providers.Cloud;
using GeZi.DownloadEngine;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 原生版主窗口。功能对齐 WPF 版的**下载主链路**：
    ///   粘贴分享链接 → 解析出文件列表 → 勾选 → 获取直链 → 多线程分片下载 → 进度/暂停/继续/取消
    ///
    /// 与 WPF 版的差异（有意为之）：
    /// - 不含托盘常驻、悬浮小窗、边下边播、主题切换动画等桌面增强 —— 那些依赖 WPF 的
    ///   WindowChrome / Windows 消息循环，移植成本高且与"能不能下载"无关。核心用途完整。
    /// - 扫码登录改用「粘贴 Cookie」：完整二维码窗口留待后续，
    ///   但底层 TLS 通道已打通（见 GeZi.Core.Support.OpenSslTls 的 net8 分支）。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly ObservableCollection<FileRow> _files = new ObservableCollection<FileRow>();
        private readonly ObservableCollection<TaskRow> _tasks = new ObservableCollection<TaskRow>();

        private QuarkClient _client;

        // 当前解析出的分享上下文（取直链要用）
        private string _pwdId = "";
        private string _stoken = "";
        private string _passcode = "";

        // 当前所在目录（分享内的 pdir_fid，"0" = 分享根目录）及其显示名
        private string _curDirFid = "0";
        private string _curDirName = "分享根目录";

        /// <summary>
        /// 目录浏览栈：每层记 (该层的目录 fid, 该层的显示名)。
        /// 栈为空 = 在分享入口目录（根）。进一级就压一层，返回就弹一层。
        ///
        /// 为什么必须支持导航：分享内容常常整体放在一个文件夹里 ——
        /// 实测用户给的分享根目录下只有一个 `SotC` 文件夹，4 个分卷（约 13GB）
        /// 全在里面。不支持进目录的话，列表里只有那一行文件夹、没有可选文件，
        /// 下载按钮永远是灰的，看起来就是"完全没法下载"。
        /// </summary>
        private readonly List<(string Fid, string Name)> _dirStack = new List<(string, string)>();

        private readonly List<string> _logLines = new List<string>();

        /// <summary>待刷新的日志行。任意线程入队，UI 线程定时批量消费（见 <see cref="Log"/>）。</summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logPending =
            new System.Collections.Concurrent.ConcurrentQueue<string>();

        /// <summary>界面上保留的日志行数上限。</summary>
        private const int LogMaxLines = 200;

        /// <summary>一次刷新最多处理的行数，避免积压时单拍占用 UI 太久。</summary>
        private const int LogFlushBatch = 200;

        // 重复抑制（与 WPF 版同一套判据）。只作用于引擎的过程日志，
        // 结果性信息（完成/失败/删除）永不拦 —— 见 LogDupExempt。
        private readonly Dictionary<string, int> _logDupCount = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _logDupSince = new Dictionary<string, DateTime>();
        private readonly object _logDupGate = new object();
        private static readonly TimeSpan LogDupWindow = TimeSpan.FromSeconds(8);

        /// <summary>日志攒批刷新的节拍器（见 <see cref="Log"/> / <see cref="FlushLog"/>）。</summary>
        private DispatcherTimer _logFlushTimer;

        private TrayHost _tray;
        private MiniWindow _mini;
        private bool _reallyExit;

        /// <summary>清理队列：转存产生的临时目录，下载完后自动删。</summary>
        private readonly CleanupQueue _cleanupQueue = new CleanupQueue();

        // 控件引用（Avalonia 不保证为 x:Name 生成字段，统一用 FindControl 取，构造时缓存）
        private TextBox _urlBox;
        private Button _parseBtn;
        private Button _downloadBtn;
        private TextBlock _statusText;
        private TextBlock _accountText;
        private TextBlock _outDirText;
        private TextBlock _fileSummary;
        private TextBlock _fileHint;
        private TextBlock _taskSummary;
        private TextBlock _logText;
        private ListBox _fileList;
        private ListBox _taskList;
        private Button _goUpBtn;
        private Button _newFolderBtn;
        private Button _renameBtn;
        private Button _moveBtn;
        private Button _deleteBtn;
        private Button _shareBtn;
        private Button _uploadBtn;
        private Border _dirBar;
        private TextBlock _dirPathText;

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _urlBox = this.FindControl<TextBox>("UrlBox");
            _parseBtn = this.FindControl<Button>("ParseBtn");
            _downloadBtn = this.FindControl<Button>("DownloadBtn");
            _statusText = this.FindControl<TextBlock>("StatusText");
            _accountText = this.FindControl<TextBlock>("AccountText");
            _outDirText = this.FindControl<TextBlock>("OutDirText");
            _fileSummary = this.FindControl<TextBlock>("FileSummary");
            _fileHint = this.FindControl<TextBlock>("FileHint");
            _taskSummary = this.FindControl<TextBlock>("TaskSummary");
            _logText = this.FindControl<TextBlock>("LogText");
            _fileList = this.FindControl<ListBox>("FileList");
            if (_fileList != null)
                _fileList.SelectionChanged += (s2, e2) => UpdateFileManagementButtons();
            _taskList = this.FindControl<ListBox>("TaskList");
            _goUpBtn = this.FindControl<Button>("GoUpBtn");
            _newFolderBtn = this.FindControl<Button>("NewFolderBtn");
            _renameBtn = this.FindControl<Button>("RenameBtn");
            _moveBtn = this.FindControl<Button>("MoveBtn");
            _deleteBtn = this.FindControl<Button>("DeleteBtn");
            _shareBtn = this.FindControl<Button>("ShareBtn");
            _uploadBtn = this.FindControl<Button>("UploadBtn");
            _dirBar = this.FindControl<Border>("DirBar");
            _dirPathText = this.FindControl<TextBlock>("DirPathText");

            // 按环境变量自动决定是否走代理（容器/沙箱/公司网络必需）
            if (GeZi.Core.Support.NetworkOptions.AutoDetectFromEnvironment())
            {
                Log("检测到代理环境变量，已自动启用代理。");
            }

            _settings = SettingsStore.Load();

            // 下载目录必须是**真的能写**的。
            // 只看 Directory.Exists 不够：只读挂载/沙箱/容器里目录存在但写不进去，
            // 结果用户点了下载、任务全部失败，还以为是网络问题。
            //
            // ⚠️ 不需要「回退到家目录」这种兜底：如果用户指定的目录不可写，
            //    正确做法是**保留他的选择**并提示，而不是偷偷换成 ~。
            //    后者很危险 —— 会把一堆下载文件倒进家目录根下（实测踩到：
            //    目录变成 /home/pavia，用户下次看到会觉得程序乱建文件）。
            if (!SettingsStore.IsWritable(_settings.DownloadDir))
            {
                string fallback = SettingsStore.DefaultDownloadDir();

                Log(string.Format(
                    "注意：下载目录不可写（{0}）。已临时改用 {1}，可在「设置」里改成其它可写目录。",
                    string.IsNullOrEmpty(_settings.DownloadDir) ? "(未设置)" : _settings.DownloadDir,
                    fallback));

                _settings.DownloadDir = fallback;
                SettingsStore.Save(_settings);
            }

            _fileList.ItemsSource = _files;
            _taskList.ItemsSource = _tasks;
            _outDirText.Text = ShortenPath(_settings.DownloadDir);

            if (_settings.WindowWidth > 400) Width = _settings.WindowWidth;
            if (_settings.WindowHeight > 300) Height = _settings.WindowHeight;

            ThemeManager.Apply(_settings.Theme);

            Closing += OnClosing;

            // 托盘：关窗后继续下载。托盘不可用时自动降级为"直接退出"。
            _tray = new TrayHost(this, ShowMainFromTray, RequestExit);
            if (!_tray.Available)
                Log("提示：当前桌面环境不支持托盘，关闭窗口将直接退出。");

            RestoreSession();
            ApplyMiniWindowVisibility();

            // 启动时清理上次遗留的临时目录（程序可能中途退出过）
            _ = DrainCleanupQueueAsync();

            // 日志攒批刷新的节拍器。250ms 一拍：比人眼能分辨的慢一点，
            // 但足够让日志看起来是实时的；关键是它把「每条日志一次布局 + 一次写盘」
            // 变成「每 250ms 一次」，这是下载时界面能点动的直接原因。
            _logFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _logFlushTimer.Tick += (s, e) => FlushLog();
            _logFlushTimer.Start();

            Log("就绪。粘贴夸克分享链接后点「解析」。");
            if (string.IsNullOrEmpty(_settings.Cookie))
                Log("未登录也能下载分享。需要登录时点右上角「登录」，粘贴浏览器里的 Cookie。");
        }

        // ================= 会话 =================

        private void RestoreSession()
        {
            string cookie = SecretProtector.Unprotect(_settings.Cookie);

            // 兜底：settings 里没有 Cookie，但账号列表里有档案 → 用最近使用的那个。
            //
            // 为什么需要这层：账号档案和"当前账号"是两份数据。只要有任何一步
            // 没能把两者同步（比如早先的版本里"添加账号"不会自动应用），
            // 就会出现「账号明明存着、界面却显示未登录」。
            // 与其让用户去猜，不如启动时按"最近使用"自动接上。
            if (string.IsNullOrEmpty(cookie))
            {
                var recent = AccountStore.LoadAll().FirstOrDefault(a => !string.IsNullOrEmpty(a.Cookie));
                if (recent != null)
                {
                    cookie = recent.Cookie;
                    _settings.Cookie = SecretProtector.Protect(recent.Cookie);
                    _settings.Who = recent.DisplayName;
                    _settings.CurrentAccountKey = recent.Key;
                    SettingsStore.Save(_settings);
                    Log("已自动恢复账号：" + recent.DisplayName);
                }
            }

            if (string.IsNullOrEmpty(cookie))
            {
                _accountText.Text = "未登录（分享仍可下载）";
                return;
            }

            try
            {
                _client = new QuarkClient(cookie);
                _accountText.Text = string.IsNullOrEmpty(_settings.Who) ? "已登录" : _settings.Who;
            }
            catch (Exception ex)
            {
                Log("恢复登录态失败：" + ex.Message);
                _accountText.Text = "未登录";
            }
        }

        // ================= 解析分享 =================

        private async void OnParseClick(object sender, RoutedEventArgs e)
        {
            string input = (_urlBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(input))
            {
                _statusText.Text = "请先粘贴分享链接";
                return;
            }

            // 用多网盘识别器先判断是哪个平台
            var parsed = LinkParser.Parse(input);

            if (parsed.Kind == LinkKind.Magnet)
            {
                _statusText.Text = "磁力链接暂不支持，请使用网盘分享链接。";
                return;
            }

            // 按平台分发：除夸克外都走统一的 provider 通道
            if (parsed.Platform != null && parsed.Platform != CloudPlatform.Quark)
            {
                await ParseViaProviderAsync(parsed);
                return;
            }

            var info = ShareUrlParser.Parse(input);
            if (info.IsKouling)
            {
                _statusText.Text = "这是夸克口令，当前不支持。请改用分享链接（…/s/xxxx）。";
                return;
            }
            if (string.IsNullOrEmpty(info.PwdId))
            {
                _statusText.Text = "没识别出分享 ID。链接应形如 https://pan.quark.cn/s/xxxxxx";
                return;
            }

            _parseBtn.IsEnabled = false;
            _statusText.Text = "解析中…";
            _files.Clear();
            _downloadBtn.IsEnabled = false;

            // 切回夸克通道：清掉上一次的 provider 会话，避免状态串台
            _provider = null;
            _cloudSession = null;
            _cloudDirStack.Clear();

            _pwdId = info.PwdId;
            _passcode = info.Passcode ?? "";
            _stoken = "";

            try
            {
                var client = _client ?? new QuarkClient("");

                Log("正在获取分享凭证…");
                _stoken = await client.GetStokenAsync(_pwdId, _passcode);
                if (string.IsNullOrEmpty(_stoken))
                {
                    _statusText.Text = "获取分享凭证失败。可能需要登录，或提取码不对。";
                    Log("GetStoken 返回空。若分享带提取码，请把「密码:xxxx」一起粘进来。");
                    return;
                }

                // 进入分享入口目录（多数分享是 "0"，带 startFid 的链接会指向子目录）
                string startFid = string.IsNullOrEmpty(info.StartFid) ? "0" : info.StartFid;
                _dirStack.Clear();
                _curDirFid = startFid;
                _curDirName = "分享根目录";

                await LoadDirectoryAsync(client, startFid);

                _statusText.Text = "解析完成";
                Log(string.Format("分享 {0}：解析到 {1} 项", _pwdId, _files.Count));
            }
            catch (QuarkApiException ex)
            {
                _statusText.Text = "解析失败：" + ex.Message;
                Log("解析失败（接口）：" + ex.Message);
            }
            catch (Exception ex)
            {
                _statusText.Text = "解析失败：" + ex.Message;
                Log("解析失败：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                _parseBtn.IsEnabled = true;
            }
        }

        // ================= 多网盘：统一 provider 通道 =================

        // 当前会话的 provider（夸克走自己的老通道，其余走这里）
        private ICloudProvider _provider;
        private BrowseSession _cloudSession;
        private readonly Stack<string> _cloudDirStack = new Stack<string>();

        /// <summary>
        /// 用统一的 <see cref="ICloudProvider"/> 解析任意平台的分享。
        ///
        /// 与夸克老通道的区别：老通道用了大量 QuarkClient 的专用优化（多入口取链、
        /// 容量识别、风控退避），其它平台没有这些，统一走 provider 接口即可。
        /// </summary>
        private async Task ParseViaProviderAsync(ParsedLink parsed)
        {
            _parseBtn.IsEnabled = false;
            _statusText.Text = "解析中…";
            _files.Clear();
            _downloadBtn.IsEnabled = false;

            try
            {
                var provider = CloudProviderRegistry.Get(parsed.Platform);
                if (provider == null)
                {
                    _statusText.Text = parsed.Platform.Label + " 暂不支持。";
                    Log("未实现的平台：" + parsed.Platform.Label);
                    return;
                }

                // 该平台是否必须登录
                if (parsed.Platform.ShareRequiresAccount && _client == null)
                {
                    _statusText.Text = parsed.Platform.Label + " 的分享解析需要先登录（点右上角「账号」）。";
                    Log(parsed.Platform.Label + " 需要登录态才能解析分享");
                    return;
                }

                CloudCredential cred = BuildCredential(parsed.Platform);

                Log("正在用 " + provider.GetType().Name + " 解析 " + parsed.Platform.Label + " 分享…");
                var session = await provider.OpenShareAsync(parsed, cred);

                _provider = provider;
                _cloudSession = session;
                _cloudDirStack.Clear();
                _cloudDirStack.Push(session.RootId);

                await LoadViaProviderAsync(provider, session, cred, session.RootId);

                _statusText.Text = "解析完成（" + parsed.Platform.Label + "）";
                Log(string.Format("{0}：解析到 {1} 项", parsed.Platform.Label, _files.Count));
            }
            catch (CloudNotSupportedException ex)
            {
                _statusText.Text = ex.Message;
                Log("不支持：" + ex.Message);
            }
            catch (CloudApiException ex)
            {
                _statusText.Text = "解析失败：" + ex.Message;
                Log("解析失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                _statusText.Text = "解析失败：" + ex.Message;
                Log("解析失败：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                _parseBtn.IsEnabled = true;
            }
        }

        /// <summary>用 provider 加载一个目录并刷新列表。</summary>
        private async Task LoadViaProviderAsync(ICloudProvider provider, BrowseSession session,
            CloudCredential cred, string parentId)
        {
            _files.Clear();

            var items = await provider.ListAsync(session, parentId, cred);

            foreach (var it in items)
                _files.Add(new FileRow(it, session.Meta("passcode")));

            UpdateFileSummary();
            UpdateDownloadButton();

            int dirs = _files.Count(f => f.Entry.IsDir);
            int files = _files.Count(f => !f.Entry.IsDir);

            if (_files.Count == 0)
                _fileHint.Text = "这个目录是空的";
            else if (files == 0)
                _fileHint.Text = string.Format("这里有 {0} 个文件夹，双击进入查看", dirs);
            else
                _fileHint.Text = string.Format("{0} 个文件 / {1} 个文件夹", files, dirs);

            UpdateFileManagementButtons();
        }

        /// <summary>按平台构造凭据（夸克/百度的 Cookie 来自已登录账号）。</summary>
        private CloudCredential BuildCredential(CloudPlatform platform)
        {
            string cookie = _client?.CookieStr ?? "";
            var fields = new Dictionary<string, string>
            {
                { CloudCredential.PrimaryKey, cookie },
            };
            return new CloudCredential(platform, _settings.Who ?? "", fields);
        }

        /// <summary>
        /// 加载分享内**某一个目录**的条目并刷新列表。
        /// </summary>
        /// <param name="pdirFid">目录 fid（"0" = 分享根目录）</param>
        private async Task LoadDirectoryAsync(QuarkClient client, string pdirFid)
        {
            _files.Clear();
            _curDirFid = pdirFid;

            var page = await client.ShareDetailPageAsync(_pwdId, _stoken, pdirFid, 1);
            if (page?.Items != null)
            {
                foreach (var it in page.Items)
                    _files.Add(new FileRow(it, _pwdId, _stoken, _passcode));
            }

            UpdateFileSummary();
            UpdateDirBar();
            UpdateDownloadButton();

            int dirs = _files.Count(f => f.Entry.IsDir);
            int files = _files.Count(f => !f.Entry.IsDir);

            if (_files.Count == 0)
                _fileHint.Text = "这个目录是空的";
            else if (files == 0)
                _fileHint.Text = string.Format("这里有 {0} 个文件夹，双击进入查看里面的文件", dirs);
            else if (page != null && page.HasMore)
                _fileHint.Text = string.Format("只显示了第一页（{0} 个文件 / {1} 个文件夹）。勾选后下载。", files, dirs);
            else
                _fileHint.Text = string.Format("{0} 个文件 / {1} 个文件夹。勾选要下载的文件。", files, dirs);
        }

        /// <summary>刷新"当前位置"的路径显示。</summary>
        private void UpdateDirBar()
        {
            if (_dirBar == null) return;

            if (_dirStack.Count == 0)
            {
                _dirBar.IsVisible = false;
                if (_goUpBtn != null) _goUpBtn.IsVisible = false;
                return;
            }

            _dirBar.IsVisible = true;
            if (_goUpBtn != null) _goUpBtn.IsVisible = true;
            var names = _dirStack.Select(d => d.Name).ToList();
            names.Add(_curDirName);
            _dirPathText.Text = string.Join(" / ", names);
        }

        /// <summary>按"有没有可下载的文件被选中"更新下载按钮状态。</summary>
        private void UpdateDownloadButton()
        {
            // 勾选文件夹也算「有选中」—— 下载时会递归展开成具体文件。
            _downloadBtn.IsEnabled = _files.Any(f => f.Selected);
        }

        /// <summary>双击文件夹 → 进入。</summary>
        private async void OnFileDoubleTapped(object sender, TappedEventArgs e)
        {
            var row = (sender as Control)?.DataContext as FileRow;
            if (row == null || !row.Entry.IsDir) return;

            // 双击落在勾选框上时不要进目录：用户是在连点勾选框（或双击切换勾选），
            // 那不是"我要进去"的意图。点一次复选框后 e.Handled 已置位，
            // 但双击的第二下可能仍然冒泡上来，所以这里按原始源再挡一次。
            if (e.Source is CheckBox) return;

            await EnterDirectoryAsync(row);
        }

        /// <summary>进入子目录。同时提供按钮入口（触摸/不方便双击时用）。</summary>
        private async Task EnterDirectoryAsync(FileRow row)
        {
            if (row == null || !row.Entry.IsDir) return;

            try
            {
                _statusText.Text = "正在进入 " + row.Name + "…";

                // 压入「当前目录」的信息，返回时据此还原
                _dirStack.Add((_curDirFid, _curDirName));

                _curDirName = row.Name;

                if (_provider != null && _cloudSession != null)
                {
                    // 多网盘通道
                    _cloudDirStack.Push(row.Fid);
                    await LoadViaProviderAsync(_provider, _cloudSession,
                        BuildCredential(_cloudSession.Platform), row.Fid);
                }
                else
                {
                    var client = _client ?? new QuarkClient("");
                    await LoadDirectoryAsync(client, row.Fid);
                }

                _statusText.Text = "";
            }
            catch (Exception ex)
            {
                // 加载失败 → 回退栈，避免路径显示与实际内容不一致
                if (_dirStack.Count > 0) _dirStack.RemoveAt(_dirStack.Count - 1);
                UpdateDirBar();
                _statusText.Text = "进入文件夹失败：" + ex.Message;
                Log("进入文件夹失败：" + ex.Message);
            }
        }

        /// <summary>返回上一级。</summary>
        private async void OnGoUpClick(object sender, RoutedEventArgs e)
        {
            if (_dirStack.Count == 0) return;

            var (fid, name) = _dirStack[_dirStack.Count - 1];
            _dirStack.RemoveAt(_dirStack.Count - 1);
            _curDirName = name;

            try
            {
                if (_provider != null && _cloudSession != null)
                {
                    await LoadViaProviderAsync(_provider, _cloudSession,
                        BuildCredential(_cloudSession.Platform), fid);
                }
                else
                {
                    var client = _client ?? new QuarkClient("");
                    await LoadDirectoryAsync(client, fid);
                }
            }
            catch (Exception ex)
            {
                _statusText.Text = "返回上级失败：" + ex.Message;
            }
        }

        private void UpdateFileSummary()
        {
            int n = _files.Count;
            // 文件夹现在也能勾选，所以「已选」要把它算进去；
            // 大小只累加已知的（文件夹要等展开后才有 DirSize）。
            int selected = _files.Count(f => f.Selected);
            long total = _files.Where(f => f.Selected)
                               .Sum(f => f.Entry.IsDir ? f.DirSize : f.Size);
            _fileSummary.Text = string.Format("共 {0} 项 / 已选 {1} 项，合计 {2}",
                n, selected, FileRow.FormatSize(total));
        }

        /// <summary>勾选框变化时刷新"已选 N 项"的汇总。</summary>
        private void OnFileCheckChanged(object sender, RoutedEventArgs e)
        {
            UpdateFileSummary();
            UpdateDownloadButton();
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            // 文件夹也一起选上：用户点「全选」就是想要当前目录下的全部内容，
            // 文件夹里的文件由下载时递归展开。
            foreach (var f in _files) f.Selected = true;
            UpdateFileSummary();
            UpdateDownloadButton();
        }

        private void OnSelectNoneClick(object sender, RoutedEventArgs e)
        {
            foreach (var f in _files) f.Selected = false;
            UpdateFileSummary();
            UpdateDownloadButton();
        }

        private async void OnChooseDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择下载保存目录",
                    AllowMultiple = false,
                });
                var picked = folders?.FirstOrDefault();
                if (picked != null)
                {
                    string path = picked.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        _settings.DownloadDir = path;
                        _outDirText.Text = ShortenPath(path);
                        SettingsStore.Save(_settings);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("选择目录失败：" + ex.Message);
            }
        }

        // ================= 下载 =================

        private async void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            // 勾选可能是文件夹 → 必须先递归展开成具体文件。
            // 原来这里直接 `!f.Entry.IsDir` 过滤掉文件夹，而文件夹又没有复选框，
            // 所以「下载整个文件夹」这个最常见的需求根本无法表达。
            var selectedRows = _files.Where(f => f.Selected).ToList();
            if (selectedRows.Count == 0)
            {
                _statusText.Text = "请先勾选要下载的文件或文件夹";
                return;
            }

            List<FileRow> picked;
            try
            {
                _statusText.Text = "正在展开所选文件夹…";
                _downloadBtn.IsEnabled = false;
                picked = await ExpandSelectionAsync(selectedRows);
            }
            catch (Exception ex)
            {
                _statusText.Text = "展开文件夹失败：" + ex.Message;
                Log("展开文件夹失败：" + ex.Message);
                _downloadBtn.IsEnabled = true;
                return;
            }

            if (picked.Count == 0)
            {
                _statusText.Text = "所选文件夹里没有文件";
                return;
            }

            var client = _client ?? new QuarkClient("");
            var jobs = new List<DownloadJob>();
            var rows = new List<TaskRow>();

            foreach (var row in picked)
            {
                try
                {
                    Directory.CreateDirectory(_settings.DownloadDir);
                }
                catch (Exception ex)
                {
                    Log("创建下载目录失败：" + _settings.DownloadDir + " — " + ex.Message);
                    continue;
                }

                // 按"分享名/子目录…"保存，而不是全部平铺到一个目录：
                // ① 不同子目录下的同名文件不会互相覆盖；
                // ② 用户下载整个分享时能保留原有结构（分卷压缩包尤其需要）。
                // 当前浏览位置 + 该文件在展开时记录的子路径（勾选文件夹下载才有）
                string relDir = string.Join(Path.DirectorySeparatorChar,
                    _dirStack.Select(d => PathUtil.SanitizeFileName(d.Name))
                             .Concat(new[] { PathUtil.SanitizeFileName(_curDirName) })
                             .Concat(string.IsNullOrEmpty(row.RelDir)
                                     ? Array.Empty<string>()
                                     : new[] { row.RelDir })
                             .Where(n => !string.IsNullOrEmpty(n) && n != "分享根目录"));

                string destDir = string.IsNullOrEmpty(relDir)
                    ? _settings.DownloadDir
                    : Path.Combine(_settings.DownloadDir, relDir);

                try { Directory.CreateDirectory(destDir); }
                catch (Exception ex)
                {
                    Log("创建子目录失败：" + destDir + " — " + ex.Message);
                    destDir = _settings.DownloadDir;
                }

                string dest = PathUtil.UniquePath(
                    Path.Combine(destDir, PathUtil.SanitizeFileName(row.Name)));

                var captured = row;
                var job = new DownloadJob
                {
                    Name = captured.Name,
                    Url = "",                      // 取到直链后再填
                    Dest = dest,
                    Cookie = client.CookieStr,
                    Threads = _settings.Threads,
                    // 直链有时效（签名过期），下载器遇到时会回调这里换新链
                    LinkRefresher = ct => RefreshShareLinkAsync(captured, ct),
                    ResumeCannotRefresh = false,
                };

                var tr = new TaskRow(job, captured);
                tr.State = "等待中";

                jobs.Add(job);
                rows.Add(tr);
                _tasks.Add(tr);
            }

            if (jobs.Count == 0) return;

            UpdateTaskSummary();
            _downloadBtn.IsEnabled = false;
            _statusText.Text = string.Format("准备下载 {0} 个文件…", jobs.Count);
            Log(string.Format("加入队列 {0} 个文件，每个最多 {1} 路并发，同时 {2} 个任务",
                jobs.Count, _settings.Threads, _settings.ConcurrentTasks));

            // 切到任务页，让用户看到进度
            var tabs = this.FindControl<TabControl>("MainTabs");
            if (tabs != null) tabs.SelectedIndex = 1;

            await RunJobsAsync(client, jobs, rows);
        }

        /// <summary>
        /// 把「勾选的条目」展开成「具体文件」列表。
        /// <para>
        /// 勾选里可能有文件夹 —— 它们需要递归下钻，把里面的文件全部取出来，
        /// 并记下各自相对当前目录的子路径（<see cref="FileRow.RelDir"/>），
        /// 这样落盘时能保留原目录结构。
        /// </para>
        /// <para>
        /// 两条通道都要支持：多网盘（<c>_provider</c>）与夸克分享老通道（<c>_client</c>）。
        /// 夸克老通道走分页接口（<c>ShareDetailPageAsync</c>），
        /// **必须翻页到底**：一个目录可能有几千个文件，只取第一页会静默少下。
        /// </para>
        /// </summary>
        private async Task<List<FileRow>> ExpandSelectionAsync(List<FileRow> selected)
        {
            var result = new List<FileRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);   // 防同一 fid 重复入队
            int scanned = 0;

            foreach (var row in selected)
            {
                if (row == null) continue;

                if (!row.Entry.IsDir)
                {
                    if (seen.Add(row.Entry.Fid)) result.Add(row);
                    continue;
                }

                // 文件夹：递归下钻。累加器按引用传进去 —— 本地函数不能
                // 直接捕获「foreach 循环体内的局部变量」以外的写入（这里需要跨递归累加）。
                long dirBytes = 0;
                await WalkDirAsync(row, "", 0, b => dirBytes += b);

                // 回填真实大小，界面上「合计」才有意义
                row.DirSize = dirBytes;
            }

            UpdateFileSummary();
            return result;

            async Task WalkDirAsync(FileRow dirRow, string relPrefix, int depth, Action<long> addBytes)
            {
                // 深度上限：正常分享不会这么深，纯粹防病态结构把栈/请求打爆
                if (depth >= 32) return;

                List<FileRow> children;
                try
                {
                    children = await ListDirForExpandAsync(dirRow.Entry.Fid);
                }
                catch (Exception ex)
                {
                    // 单个目录失败不应该让整批下载失败 —— 但要明确告诉用户少了东西
                    Log("读取子目录失败：" + dirRow.Name + " — " + ex.Message);
                    return;
                }

                foreach (var child in children)
                {
                    scanned++;
                    if ((scanned & 0x3F) == 0)
                        _statusText.Text = string.Format("正在展开… 已扫描 {0} 个文件", scanned);

                    string childRel = string.IsNullOrEmpty(relPrefix)
                        ? PathUtil.SanitizeFileName(child.Name)
                        : Path.Combine(relPrefix, PathUtil.SanitizeFileName(child.Name));

                    if (child.Entry.IsDir)
                    {
                        await WalkDirAsync(child, childRel, depth + 1, addBytes);
                    }
                    else
                    {
                        child.RelDir = relPrefix;   // 文件落在「父目录」下
                        if (seen.Add(child.Entry.Fid))
                        {
                            result.Add(child);
                            addBytes(child.Size);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 列出某个目录下的**全部**条目（含翻页），供递归展开使用。
        /// </summary>
        private async Task<List<FileRow>> ListDirForExpandAsync(string dirFid)
        {
            var list = new List<FileRow>();

            if (_provider != null && _cloudSession != null)
            {
                var cred = BuildCredential(_cloudSession.Platform);
                var items = await _provider.ListAsync(_cloudSession, dirFid, cred);
                foreach (var it in items)
                    list.Add(new FileRow(it, _cloudSession.Meta("passcode")));
                return list;
            }

            var client = _client ?? new QuarkClient("");
            for (int page = 1; page <= 200; page++)   // 200 页 × 每页 50 ≈ 1 万条，足够
            {
                var p = await client.ShareDetailPageAsync(_pwdId, _stoken, dirFid, page);
                if (p?.Items == null || p.Items.Count == 0) break;

                foreach (var it in p.Items)
                    list.Add(new FileRow(it, _pwdId, _stoken, _passcode));

                if (!p.HasMore) break;
            }
            return list;
        }

        /// <summary>
        /// 取直链 → 交给调度器跑。直链取失败的任务单独标红，不影响其他任务。
        /// </summary>
        private async Task RunJobsAsync(QuarkClient client, List<DownloadJob> jobs, List<TaskRow> rows)
        {
            var ready = new List<DownloadJob>();
            var readyRows = new List<TaskRow>();

            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var tr = rows[i];
                string url = null;   // 提到 try 外：catch 里要复用（转存成功后赋值）

                try
                {
                    tr.State = "获取直链";

                    if (_provider != null && _cloudSession != null && tr.Source?.CloudEntry != null)
                    {
                        // 多网盘通道：交给 provider 取链
                        var cred = BuildCredential(_cloudSession.Platform);
                        var spec = await _provider.GetDownloadSpecAsync(
                            _cloudSession, tr.Source.CloudEntry, cred);

                        url = spec?.Url;

                        // 需要二次取链（转存类平台）
                        if (string.IsNullOrEmpty(url) && spec != null && spec.NeedsPreparation)
                        {
                            tr.State = "准备中";
                            spec = await _provider.PrepareDownloadAsync(spec, cred);
                            url = spec?.Url;
                        }

                        // provider 给的 headers 要带进下载任务
                        if (!string.IsNullOrEmpty(url) && spec?.Headers != null)
                        {
                            foreach (var kv in spec.Headers)
                                job.Headers[kv.Key] = kv.Value;

                            if (spec.ExpectedSize > 0 && tr.TotalSize <= 0)
                                job.ExpectedSize = spec.ExpectedSize;
                        }
                    }
                    else
                    {
                        // 夸克老通道
                        url = await GetShareDownloadUrlAsync(client, tr.Source);
                    }
                    if (string.IsNullOrEmpty(url))
                    {
                        tr.State = "失败";
                        tr.Error = "没能取到下载直链（链接可能过期或需要登录）";
                        continue;
                    }

                    job.Url = url;
                    ready.Add(job);
                    readyRows.Add(tr);
                }
                catch (CloudNewPreparationException ex)
                {
                    // "需要先转存" —— 不是失败，是走另一条路。
                    // 如果 provider 支持转存，这里**自动执行**完整流程；
                    // 否则退化为提示用户手动处理。
                    var transfer = _provider as ICloudTransferSupport;
                    if (transfer == null)
                    {
                        tr.State = "需转存";
                        tr.Error = ex.Message + "（该平台暂不支持自动转存）";
                        Log("需转存（无自动能力）：" + job.Name);
                        continue;
                    }

                    try
                    {
                        tr.State = "转存中";
                        Log("开始自动转存：" + job.Name);

                        var cred = BuildCredential(_cloudSession.Platform);
                        var prepared = await CloudTransferFlow.PrepareAsync(
                            transfer, ex.Spec, cred,
                            phase => Log("  " + job.Name + "：" + phase),
                            CancellationToken.None);

                        if (string.IsNullOrEmpty(prepared?.Url))
                        {
                            tr.State = "失败";
                            tr.Error = "转存后仍取不到直链";
                            continue;
                        }

                        // 转存成功 → 用新直链 + 新 headers 继续下载
                        url = prepared.Url;
                        tr.State = "获取直链";

                        if (prepared.Headers != null)
                        {
                            foreach (var kv in prepared.Headers)
                                job.Headers[kv.Key] = kv.Value;
                        }
                        if (prepared.ExpectedSize > 0)
                            job.ExpectedSize = prepared.ExpectedSize;

                        Log("转存完成，已拿到直链：" + job.Name);

                        // 登记清理（下载完成后自动删临时目录）
                        if (prepared.Cleanup != null && prepared.Cleanup.HasWork)
                        {
                            // 把目录名也带进去，供安全校验用
                            if (!string.IsNullOrEmpty(_curDirName))
                                prepared.Cleanup.FileIds.Add("AsterLink临时转存_"
                                    + Guid.NewGuid().ToString("D"));

                            _cleanupQueue.Enqueue(prepared.Cleanup);
                            Log("已登记临时目录清理");
                        }

                        // 转存成功 → 加入待下载队列
                        job.Url = prepared.Url;
                        ready.Add(job);
                        readyRows.Add(tr);
                    }
                    catch (Exception tex)
                    {
                        tr.State = "失败";
                        tr.Error = "自动转存失败：" + tex.Message;
                        Log("自动转存失败：" + job.Name + " — " + tex.Message);
                    }

                    continue;   // 该文件已单独处理，跳过后续统一入队逻辑
                }
                catch (QuarkShareCredentialExpiredException ex)
                {
                    tr.State = "失败";
                    tr.Error = ex.Message + "（需重新解析分享后重试）";
                    Log("凭证过期：" + job.Name + " — 请重新点「解析」刷新分享凭证后重试");
                }
                catch (Exception ex)
                {
                    tr.State = "失败";
                    tr.Error = ex.Message;
                    Log("取直链失败：" + job.Name + " — " + ex.Message);
                }
            }

            if (ready.Count == 0)
            {
                _statusText.Text = "所有文件都没能取到直链";
                _downloadBtn.IsEnabled = true;
                UpdateTaskSummary();
                return;
            }

            _statusText.Text = "";

            // 进度回调：调度器可能从任意线程报告 → 切回 UI 线程
            var progress = new Progress<JobUpdate>(u =>
            {
                var tr = readyRows.FirstOrDefault(r => ReferenceEquals(r.Job, u.Job));
                if (tr == null) return;

                switch (u.State)
                {
                    case JobState.Downloading:
                        // 终态之后不该再被"下载中"覆盖。
                        // 引擎的进度上报来自多个线程（阶段文案有 400ms 延迟任务），
                        // 一条已失败/已取消的任务偶尔会收到一个迟到的 Downloading，
                        // 把状态冲回「下载中」—— 用户看到失败原因还在、状态却在跑。
                        // 终态一旦写入就不再接受 Downloading。
                        if (tr.IsTerminal) break;
                        if (u.Progress != null)
                        {
                            tr.Done = u.Progress.Done;

                            // 用户暂停期间：只更新已下载量，不覆盖状态、不显示速度。
                            // 否则回调会把"已暂停"冲回"下载中"，继续按钮也一直是灰的。
                            if (tr.UserPaused)
                            {
                                tr.Speed = 0;
                            }
                            else
                            {
                                tr.Speed = u.Progress.Speed;
                                // 全部下完还在合并分片 → 明确告知，否则看起来像卡在 100%
                                tr.State = string.IsNullOrEmpty(u.Progress.Phase)
                                    ? "下载中" : u.Progress.Phase;
                            }
                        }
                        break;

                    case JobState.Completed:
                        tr.Speed = 0;
                        tr.Done = tr.TotalSize > 0 ? tr.TotalSize : tr.Done;
                        tr.State = "已完成";
                        Log("完成：" + tr.Name);
                        break;

                    case JobState.Failed:
                        tr.Speed = 0;
                        tr.State = "失败";
                        tr.Error = u.Error;
                        Log("失败：" + tr.Name + " — " + u.Error);
                        break;

                    case JobState.Cancelled:
                        tr.Speed = 0;
                        tr.State = "已取消";
                        break;
                }

                UpdateTaskSummary();
                UpdateMiniWindow();
            });

            try
            {
                using (var scheduler = new DownloadScheduler(_settings.ConcurrentTasks))
                {
                    scheduler.Log = msg => Log(msg);
                    await scheduler.RunAllAsync(ready, progress, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log("下载批次异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                _downloadBtn.IsEnabled = true;
                UpdateTaskSummary();
            }
        }

        /// <summary>
        /// 用分享上下文取单个文件的直链。
        /// </summary>
        /// <summary>
        /// 取分享直链，带**自动重解析**兜底。
        ///
        /// 为什么不能只调一次接口：分享凭证（stoken）和 share_fid_token 都有时效，
        /// 用户从解析到点下载之间隔了几分钟、或者程序开久了，就会拿到
        /// "签名过期/凭证无效"的失败。WPF 版为此做了「失败 → 重新解析分享换新凭证
        /// → 再取一次」的自愈路径，原生版第一版漏了这段，
        /// 表现就是**分享能解析出列表、但一个文件都下不动**。
        /// </summary>
        private async Task<string> GetShareDownloadUrlAsync(QuarkClient client, FileRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.Fid)) return null;

            // ① 先用现有凭证试一次（绝大多数情况这一步就成）
            string url = await TryShareDownloadAsync(client, row.Fid, row.Token, row.PwdId, row.Stoken);
            if (!string.IsNullOrEmpty(url))
                return url;

            // ② 失败 → 重新解析分享，换新 stoken + 新 share_fid_token
            Log("直链获取失败，正在重新解析分享（可能是凭证过期）…");
            string freshStoken = await client.GetStokenAsync(row.PwdId, row.Passcode ?? "");
            if (string.IsNullOrEmpty(freshStoken))
            {
                Log("重新解析分享失败：拿不到新凭证。分享可能已被取消、删除，或提取码变了。");
                return null;
            }

            // 在分享根目录里按 fid 找文件，拿新的 share_fid_token
            string freshToken = await FindShareTokenByFidAsync(client, row.PwdId, freshStoken, row.Fid);
            if (string.IsNullOrEmpty(freshToken))
            {
                Log("重新解析分享后仍找不到该文件（可能已被移动或删除）。");
                return null;
            }

            // ③ 用新凭证再取一次，并回写到条目上，后续换链直接用新值
            url = await TryShareDownloadAsync(client, row.Fid, freshToken, row.PwdId, freshStoken);
            if (!string.IsNullOrEmpty(url))
            {
                row.UpdateCredentials(freshStoken, freshToken);
                if (string.Equals(_pwdId, row.PwdId, StringComparison.Ordinal))
                    _stoken = freshStoken;
                Log("分享凭证已更新，直链获取成功。");
            }
            else
            {
                Log("重新解析后仍取不到直链。请确认这个分享还需要什么权限（如提取码）。");
            }
            return url;
        }

        /// <summary>取一次分享直链；吞掉异常，失败返回 null（由上层决定要不要重试）。</summary>
        private async Task<string> TryShareDownloadAsync(
            QuarkClient client, string fid, string token, string pwdId, string stoken)
        {
            try
            {
                var links = await client.GetShareDownloadAsync(
                    new List<string> { fid },
                    new List<string> { token ?? "" },
                    pwdId, stoken);

                return links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
            }
            catch (Exception ex)
            {
                Log("取直链接口报错：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 在分享根目录里按 fid 找文件的 **新** share_fid_token。
        /// 按 fid 匹配而不是按文件名：fid 在分享内稳定，文件名可能重名。
        /// </summary>
        private async Task<string> FindShareTokenByFidAsync(
            QuarkClient client, string pwdId, string stoken, string fid)
        {
            try
            {
                for (int page = 1; page <= 40; page++)
                {
                    var p = await client.ShareDetailPageAsync(pwdId, stoken, "0", page);
                    if (p?.Items == null) return null;

                    var hit = p.Items.FirstOrDefault(e => !e.IsDir && e.Fid == fid);
                    if (hit != null) return hit.Token;

                    if (!p.HasMore) break;
                }
            }
            catch (Exception ex)
            {
                Log("查找分享文件失败：" + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// 直链签名过期时的换链回调（下载引擎在后台线程调用）。
        /// stoken 过期会先重新解析分享拿新凭证，再取新直链。
        /// </summary>
        private async Task<string> RefreshShareLinkAsync(FileRow row, CancellationToken ct)
        {
            try
            {
                var client = _client ?? new QuarkClient("");
                return await GetShareDownloadUrlAsync(client, row);
            }
            catch (Exception ex)
            {
                Log("换链失败：" + ex.Message);
                return null;
            }
        }

        // ================= 任务操作 =================

        private void OnPauseTaskClick(object sender, RoutedEventArgs e)
        {
            if (SenderTask(sender) is TaskRow tr)
            {
                tr.UserPaused = true;
                tr.Job.PauseJob();
                tr.Speed = 0;
                tr.State = "已暂停";
                Log("已暂停：" + tr.Name);
                UpdateTaskSummary();
            }
        }

        private void OnResumeTaskClick(object sender, RoutedEventArgs e)
        {
            if (SenderTask(sender) is TaskRow tr)
            {
                tr.UserPaused = false;
                tr.Job.ResumeJob();
                tr.State = "下载中";
                Log("继续下载：" + tr.Name);
                UpdateTaskSummary();
            }
        }

        private void OnCancelTaskClick(object sender, RoutedEventArgs e)
        {
            if (!(SenderTask(sender) is TaskRow tr)) return;

            // 已结束的任务不该再发取消信号（按钮本身也会是灰的，这里兜一层）
            if (!tr.CanCancel) return;

            tr.UserPaused = false;
            tr.Job.CancelJob();

            // ⚠️ 不要在这里直接把状态写成"已取消"。
            // 取消只是发信号，worker 可能正卡在一次 ReadAsync 里，要等那次读返回
            // 才会真正退出。立即标"已取消"会让「删除」按钮提前变可用，
            // 而此刻后台仍在写盘 —— 用户删掉任务后文件还在长，很难理解。
            // 真正的终态由调度器回调（JobState.Cancelled）写入。
            tr.State = "正在取消";
            Log("正在取消：" + tr.Name);
            UpdateTaskSummary();
        }

        /// <summary>
        /// 从任务列表移除一条已结束的任务。
        /// <para>
        /// 只删界面条目，**不删已下载的文件** —— 用户点「删除」是想清掉列表里
        /// 那条失败/取消的记录，不是想丢文件。真要删文件请去「打开目录」。
        /// </para>
        /// </summary>
        private void OnRemoveTaskClick(object sender, RoutedEventArgs e)
        {
            if (!(SenderTask(sender) is TaskRow tr)) return;

            if (!tr.CanRemove)
            {
                _statusText.Text = "任务还在进行中，请先取消再删除";
                return;
            }

            // 兜底：万一状态是"正在取消"却已经跑完了，这里也放行
            try { tr.Job.CancelJob(); } catch { }

            _tasks.Remove(tr);
            Log("已移除任务：" + tr.Name);
            UpdateTaskSummary();
        }

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            if (SenderTask(sender) is TaskRow tr)
            {
                try
                {
                    string dir = Path.GetDirectoryName(tr.Dest);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start(new ProcessStartInfo("xdg-open", dir) { UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    Log("打开目录失败：" + ex.Message);
                }
            }
        }

        private void OnClearDoneClick(object sender, RoutedEventArgs e)
        {
            foreach (var t in _tasks.Where(x => x.State == "已完成").ToList())
                _tasks.Remove(t);
            UpdateTaskSummary();
        }

        private static TaskRow SenderTask(object sender)
            => (sender as Control)?.DataContext as TaskRow;

        private void UpdateTaskSummary()
        {
            int running = _tasks.Count(t => t.CanPause);
            _taskSummary.Text = string.Format("共 {0} 个任务，{1} 个进行中", _tasks.Count, running);
        }

        // ================= 账号管理 =================

        private async void OnAccountsClick(object sender, RoutedEventArgs e)
        {
            var win = new AccountWindow();
            await win.ShowDialog(this);

            // 优先级：显式切换 > 刚添加的账号 > 仅增删
            var target = win.SelectedAccount ?? win.JustAdded;

            if (target != null && !string.IsNullOrEmpty(target.Cookie))
            {
                ApplyAccount(target.Cookie, target.DisplayName, target.Key);
                Log("已应用账号：" + target.DisplayName);
            }
            else if (win.Changed)
            {
                // 只是删了账号 → 重新读取，但不敢乱切（当前账号可能刚被删）
                RestoreSession();
            }
        }

        /// <summary>应用一个账号（重建 client + 保存设置）。</summary>
        private void ApplyAccount(string cookie, string displayName, string key)
        {
            try
            {
                _client = new QuarkClient(cookie);
                _settings.Cookie = SecretProtector.Protect(cookie);
                _settings.Who = displayName ?? "";
                _settings.CurrentAccountKey = key ?? "";
                _accountText.Text = string.IsNullOrEmpty(displayName) ? "已登录" : displayName;
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                Log("应用账号失败：" + ex.Message);
            }
        }

        // ================= 设置 =================

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_settings);
            await win.ShowDialog(this);

            if (win.Changed)
            {
                SettingsStore.Save(_settings);
                ThemeManager.Apply(_settings.Theme);
                _outDirText.Text = ShortenPath(_settings.DownloadDir);
                ApplyMiniWindowVisibility();
                Log(string.Format("设置已更新：并发 {0} / 同时任务 {1} / 主题 {2}",
                    _settings.Threads, _settings.ConcurrentTasks, _settings.Theme));
            }
        }

        // ================= 临时目录清理 =================

        /// <summary>
        /// 清理上次遗留的临时转存目录。
        ///
        /// 为什么启动时做：转存成功、下载未完成时用户关掉程序，
        /// 临时目录会留在网盘里。启动时清一次，用户不会攒一堆垃圾目录。
        /// </summary>
        private async Task DrainCleanupQueueAsync()
        {
            try
            {
                int pending = _cleanupQueue.PendingCount;
                if (pending <= 0) return;

                Log(string.Format("发现 {0} 个待清理的临时目录，开始清理…", pending));

                int done = await _cleanupQueue.DrainAsync(async (entry, ct) =>
                {
                    var platform = CloudPlatform.FromKey(entry.Platform);
                    var provider = CloudProviderRegistry.Get(platform);
                    var transfer = provider as ICloudTransferSupport;
                    if (transfer == null) return;

                    var cred = BuildCredential(platform);
                    await transfer.DeleteTempFolderAsync(entry.FolderId, cred, ct);
                });

                if (done > 0)
                    Log(string.Format("已清理 {0} 个临时目录", done));
            }
            catch (Exception ex)
            {
                Log("清理临时目录时出错：" + ex.Message);
            }
        }

        // ================= 文件管理 =================

        /// <summary>按当前模式显示/隐藏文件管理按钮。</summary>
        private void UpdateFileManagementButtons()
        {
            // 只有"个人网盘 + 该平台支持"时才显示
            bool personal = _cloudSession != null
                            && _cloudSession.Mode == BrowseMode.Personal
                            && _cloudSession.CanManageFiles;

            bool canManage = personal && _provider != null;

            if (_newFolderBtn != null)
                _newFolderBtn.IsVisible = canManage && _provider.SupportsCreateFolder;

            // 重命名/移动/删除需要**选中一个条目**
            bool hasSelection = _fileList?.SelectedItem is FileRow;

            if (_renameBtn != null)
                _renameBtn.IsVisible = canManage && _provider.SupportsRename;

            if (_moveBtn != null)
                _moveBtn.IsVisible = canManage && _provider.SupportsMove;

            if (_deleteBtn != null)
                _deleteBtn.IsVisible = canManage && _provider.SupportsDelete;

            if (_shareBtn != null)
                _shareBtn.IsVisible = canManage && _provider.SupportsCreateShare;

            if (_uploadBtn != null)
                _uploadBtn.IsVisible = canManage && _provider.SupportsUpload;
        }

        /// <summary>打开"我的网盘"（个人盘浏览）。</summary>
        private async void OnMyDiskClick(object sender, RoutedEventArgs e)
        {
            // 弹出平台选择器。
            //
            // ⚠️ 这里用 Show() + 事件，而不是 ShowDialog()：
            //   平铺式合成器（sway/Hyprland）下，模态对话框有时不会被合成器
            //    正确接管（实测：ShowDialog 之后窗口列表里根本没有它），
            //    表现就是"点了按钮什么都没发生"。
            //    改成非模态 + 回调，行为在所有合成器下都一致。
            var picker = new PlatformPickerWindow();
            CloudPlatform chosen = null;

            picker.Closed += (s2, e2) => chosen = picker.Selected;

            // 用 TaskCompletionSource 等它关闭，语义仍是"选完再继续"
            var tcs = new TaskCompletionSource<bool>();
            picker.Closed += (s2, e2) => tcs.TrySetResult(true);

            picker.Show(this);
            await tcs.Task;

            if (chosen == null) return;

            // 需要登录的平台，没凭据就提示
            var cred = BuildCredentialFor(chosen);
            if (chosen.ShareRequiresAccount && string.IsNullOrEmpty(cred.Primary))
            {
                // 天翼/迅雷/PikPak 等必须登录
                if (chosen != CloudPlatform.Tianyi && chosen != CloudPlatform.Xunlei
                    && chosen != CloudPlatform.PikPak && chosen != CloudPlatform.Aliyun
                    && chosen != CloudPlatform.Pan123 && chosen != CloudPlatform.Pan115)
                {
                    _statusText.Text = chosen.Label + " 需要先登录（点右上角「账号」）";
                    return;
                }
            }

            _statusText.Text = "正在打开 " + chosen.Label + "…";

            try
            {
                var provider = CloudProviderRegistry.Get(chosen);
                if (provider == null)
                {
                    _statusText.Text = chosen.Label + " 暂不支持";
                    return;
                }

                var session = await provider.OpenPersonalAsync(cred);

                _provider = provider;
                _cloudSession = session;
                _cloudDirStack.Clear();
                _cloudDirStack.Push(session.RootId);
                _curDirFid = session.RootId;
                _curDirName = chosen.Label + " / 我的网盘";

                await LoadViaProviderAsync(provider, session, cred, session.RootId);

                _statusText.Text = chosen.Label + " · 我的网盘";
                Log("已打开 " + chosen.Label + " 个人网盘");
                UpdateFileManagementButtons();
            }
            catch (CloudNotSupportedException ex)
            {
                _statusText.Text = ex.Message;
                Log("不支持：" + ex.Message);
            }
            catch (Exception ex)
            {
                _statusText.Text = "打开失败：" + ex.Message;
                Log("打开 " + chosen.Label + " 失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// 按平台构造凭据。
        /// 目前所有平台的凭据都从同一个账号体系取（夸克 Cookie），
        /// 其它平台需要各自登录 —— 这里如实反映"没有该平台凭据"。
        /// </summary>
        private CloudCredential BuildCredentialFor(CloudPlatform platform)
        {
            var stored = AccountStore.LoadAll()
                .FirstOrDefault(a => a.Platform == platform.Key && !string.IsNullOrEmpty(a.Cookie));

            string primary = stored?.Cookie ?? "";

            // 夸克/UC 共用同一个 Cookie（同源）
            if (string.IsNullOrEmpty(primary)
                && (platform == CloudPlatform.Quark || platform == CloudPlatform.Uc))
                primary = _client?.CookieStr ?? "";

            return new CloudCredential(platform, stored?.DisplayName ?? _settings.Who ?? "",
                new Dictionary<string, string> { { CloudCredential.PrimaryKey, primary } });
        }

        private async void OnNewFolderClick(object sender, RoutedEventArgs e)
        {
            if (_provider == null || _cloudSession == null) return;

            var dlg = new FileOpWindow(FileOpWindow.OpKind.CreateFolder, 1, "",
                _cloudSession.Meta("currentDir"));
            var r = await dlg.ShowDialog<FileOpWindow.Result>(this);
            if (r == null) return;

            await RunFileOpAsync("新建文件夹", async () =>
            {
                var cred = BuildCredential(_cloudSession.Platform);
                await _provider.CreateFolderAsync(_cloudSession, _cloudDirStack.Peek(), r.Name, cred);
                await ReloadCurrentDirAsync();
            });
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var row = _fileList?.SelectedItem as FileRow;
            if (row == null)
            {
                _statusText.Text = "请先在列表里选中一个文件";
                return;
            }
            if (row.CloudEntry == null || _provider == null) return;

            var dlg = new FileOpWindow(FileOpWindow.OpKind.Rename, 1, row.Name, "");
            var r = await dlg.ShowDialog<FileOpWindow.Result>(this);
            if (r == null) return;

            await RunFileOpAsync("重命名", async () =>
            {
                var cred = BuildCredential(_cloudSession.Platform);
                await _provider.RenameAsync(_cloudSession, row.CloudEntry, r.Name, cred);
                await ReloadCurrentDirAsync();
            });
        }

        private async void OnMoveClick(object sender, RoutedEventArgs e)
        {
            var row = _fileList?.SelectedItem as FileRow;
            if (row == null)
            {
                _statusText.Text = "请先在列表里选中一个文件";
                return;
            }
            if (row.CloudEntry == null || _provider == null) return;

            var dlg = new FileOpWindow(FileOpWindow.OpKind.Move, 1, "", _cloudDirStack.Peek());
            var r = await dlg.ShowDialog<FileOpWindow.Result>(this);
            if (r == null) return;

            await RunFileOpAsync("移动", async () =>
            {
                var cred = BuildCredential(_cloudSession.Platform);
                await _provider.MoveAsync(_cloudSession,
                    new List<CloudFile> { row.CloudEntry }, r.TargetId, cred);
                await ReloadCurrentDirAsync();
            });
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var row = _fileList?.SelectedItem as FileRow;
            if (row == null)
            {
                _statusText.Text = "请先在列表里选中一个文件";
                return;
            }
            if (row.CloudEntry == null || _provider == null) return;

            var dlg = new FileOpWindow(FileOpWindow.OpKind.Delete, 1, row.Name, "");
            var r = await dlg.ShowDialog<FileOpWindow.Result>(this);
            if (r == null) return;

            await RunFileOpAsync("删除", async () =>
            {
                var cred = BuildCredential(_cloudSession.Platform);
                await _provider.DeleteAsync(_cloudSession,
                    new List<CloudFile> { row.CloudEntry }, cred);
                await ReloadCurrentDirAsync();
            });
        }

        private async void OnCreateShareClick(object sender, RoutedEventArgs e)
        {
            var row = _fileList?.SelectedItem as FileRow;
            if (row == null)
            {
                _statusText.Text = "请先在列表里选中一个文件";
                return;
            }
            if (row.CloudEntry == null || _provider == null) return;

            var dlg = new FileOpWindow(FileOpWindow.OpKind.CreateShare, 1, "", "");
            var r = await dlg.ShowDialog<FileOpWindow.Result>(this);
            if (r == null) return;

            try
            {
                var cred = BuildCredential(_cloudSession.Platform);
                var options = new ShareOptions
                {
                    ExpireDays = r.ExpireDays,
                    Passcode = r.Passcode ?? "",
                };

                var share = await _provider.CreateShareAsync(_cloudSession,
                    new List<CloudFile> { row.CloudEntry }, options, cred);

                // 复用同一个窗口展示结果（而不是弹个新的）
                dlg.ShowShareResult(share);
                await dlg.ShowDialog(this);

                Log("已创建分享：" + (share?.Url ?? ""));
                _statusText.Text = "分享已创建";
            }
            catch (CloudNotSupportedException ex)
            {
                _statusText.Text = ex.Message;
            }
            catch (Exception ex)
            {
                _statusText.Text = "创建分享失败：" + ex.Message;
                Log("创建分享失败：" + ex.Message);
            }
        }

        /// <summary>上传文件（多选）。</summary>
        private async void OnUploadClick(object sender, RoutedEventArgs e)
        {
            if (_provider == null || _cloudSession == null) return;

            IReadOnlyList<IStorageFile> files;
            try
            {
                files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择要上传的文件",
                    AllowMultiple = true,
                });
            }
            catch (Exception ex)
            {
                _statusText.Text = "打开文件选择器失败：" + ex.Message;
                return;
            }

            if (files == null || files.Count == 0) return;

            var cred = BuildCredential(_cloudSession.Platform);
            string dir = _cloudDirStack.Count > 0 ? _cloudDirStack.Peek() : _cloudSession.RootId;

            int okCount = 0;

            foreach (var f in files)
            {
                string path = f.TryGetLocalPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Log("跳过（无法读取本地路径）：" + f.Name);
                    continue;
                }

                var fi = new FileInfo(path);
                string label = fi.Name;

                try
                {
                    _statusText.Text = "上传中：" + label;
                    Log(string.Format("开始上传 {0}（{1}）", label, FileRow.FormatSize(fi.Length)));

                    var progress = new Progress<UploadProgress>(p =>
                    {
                        if (p == null) return;
                        _statusText.Text = string.Format("{0}：{1}  {2:F1}%",
                            string.IsNullOrEmpty(p.Phase) ? "上传中" : p.Phase,
                            label, p.Percent);
                    });

                    var uploaded = await _provider.UploadAsync(_cloudSession, dir, path,
                        cred, progress);

                    okCount++;
                    Log(string.Format("上传完成：{0} → {1}",
                        label, uploaded?.Name ?? label));
                }
                catch (CloudNotSupportedException ex)
                {
                    Log("该网盘不支持上传：" + ex.Message);
                    _statusText.Text = ex.Message;
                    break;
                }
                catch (Exception ex)
                {
                    Log("上传失败：" + label + " — " + ex.Message);
                    _statusText.Text = "上传失败：" + ex.Message;
                }
            }

            if (okCount > 0)
            {
                _statusText.Text = string.Format("已上传 {0} 个文件", okCount);
                await ReloadCurrentDirAsync();
            }
        }

        /// <summary>统一的文件操作执行包装（统一错误处理 + 刷新）。</summary>
        private async Task RunFileOpAsync(string action, Func<Task> work)
        {
            try
            {
                _statusText.Text = action + "中…";
                await work();
                _statusText.Text = action + "成功";
                Log(action + "成功");
            }
            catch (CloudNotSupportedException ex)
            {
                _statusText.Text = ex.Message;
            }
            catch (Exception ex)
            {
                _statusText.Text = action + "失败：" + ex.Message;
                Log(action + "失败：" + ex.Message);
            }
        }

        /// <summary>重新加载当前目录。</summary>
        private async Task ReloadCurrentDirAsync()
        {
            if (_provider == null || _cloudSession == null) return;

            string dir = _cloudDirStack.Count > 0 ? _cloudDirStack.Peek() : _cloudSession.RootId;
            var cred = BuildCredential(_cloudSession.Platform);
            await LoadViaProviderAsync(_provider, _cloudSession, cred, dir);
        }

        // ================= 托盘 / 悬浮小窗 / 关闭行为 =================

        /// <summary>关窗时的行为：按设置决定「弹窗询问 / 收进托盘 / 直接退出」。</summary>
        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            // 记住窗口尺寸
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            SettingsStore.Save(_settings);

            if (_reallyExit)
                return;      // 用户明确要退出

            // 托盘不可用 → 没得选，正常退出
            if (_tray == null || !_tray.Available)
                return;

            var behavior = _settings.OnCloseButton;

            if (behavior == CloseBehavior.Ask)
            {
                e.Cancel = true;

                var choice = await ConfirmCloseAsync();
                if (choice == null)
                    return;                      // 用户取消关闭

                _settings.OnCloseButton = choice.Value.behavior;
                SettingsStore.Save(_settings);

                if (choice.Value.exit)
                    RequestExit();
                else
                    HideToTray();
                return;
            }

            if (behavior == CloseBehavior.MinimizeToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // Exit → 放行，正常退出
        }

        /// <summary>问一次"关窗后怎么办"，并把用户的选择记下来。</summary>
        private async Task<(CloseBehavior behavior, bool exit)?> ConfirmCloseAsync()
        {
            var dlg = new CloseConfirmWindow();
            var res = await dlg.ShowDialog<CloseConfirmWindow.Choice?>(this);
            if (res == null)
                return null;

            return res.Value.Action switch
            {
                CloseConfirmWindow.Action.HideToTray => (CloseBehavior.MinimizeToTray, false),
                CloseConfirmWindow.Action.Exit => (CloseBehavior.Exit, true),
                _ => ((CloseBehavior)0, false),
            };
        }

        private void HideToTray()
        {
            Hide();
            ApplyMiniWindowVisibility();
            _tray?.SetTooltip(string.Format("鸽子下载 · {0} 个任务", _tasks.Count));
            Log("已收进托盘，下载继续。点击托盘图标可恢复窗口。");
        }

        private void ShowMainFromTray()
        {
            Dispatcher.UIThread.Post(() =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
        }

        /// <summary>真正退出程序。</summary>
        private void RequestExit()
        {
            _reallyExit = true;
            try { _mini?.Close(); } catch { }
            try { _tray?.Dispose(); } catch { }
            Close();
        }

        /// <summary>按设置决定是否显示悬浮小窗。</summary>
        private void ApplyMiniWindowVisibility()
        {
            if (_settings.ShowMiniWindow)
                EnsureMiniWindow();
            else if (_mini != null)
            {
                try { _mini.Close(); } catch { }
                _mini = null;
            }
        }

        private void EnsureMiniWindow()
        {
            if (_mini != null) return;

            _mini = new MiniWindow();
            _mini.SetShowMainCallback(ShowMainFromTray);
            _mini.Show();

            // 位置：Wayland 下合成器通常不认 Position（协议限制），
            // 这里只在 X11 下尽力恢复，失败也不影响使用。
            try
            {
                if (_settings.MiniLeft != 0 || _settings.MiniTop != 0)
                    _mini.Position = new PixelPoint((int)_settings.MiniLeft, (int)_settings.MiniTop);
            }
            catch { }

            _mini.Closed += (s, e) =>
            {
                try
                {
                    _settings.MiniLeft = _mini.Position.X;
                    _settings.MiniTop = _mini.Position.Y;
                    SettingsStore.Save(_settings);
                }
                catch { }
                _mini = null;
            };
        }

        /// <summary>把总体进度推给悬浮小窗。</summary>
        private void UpdateMiniWindow()
        {
            if (_mini == null) return;

            long done = _tasks.Sum(t => t.Done);
            long total = _tasks.Sum(t => t.TotalSize);
            double speed = _tasks.Sum(t => t.Speed);
            int running = _tasks.Count(t => t.CanPause);

            try { _mini.UpdateProgress(done, total, speed, running); } catch { }
            _tray?.SetTooltip(string.Format("鸽子下载 · {0} 个任务进行中", running));
        }

        // ================= 边下边播 =================

        /// <summary>
        /// 用外部播放器打开**还没下完**的文件：播放器读到未下载区域会卡住，
        /// 但已下载部分可以正常播放，这是"边下边播"的全部含义。
        /// </summary>
        private void OnPlayPartiallyClick(object sender, RoutedEventArgs e)
        {
            var tr = SenderTask(sender);
            if (tr == null) return;

            string path = tr.Dest;
            if (!File.Exists(path))
            {
                Log("文件还没有任何内容可播（等进度起来再试）。");
                return;
            }

            // 优先用设置里指定的播放器，其次自动探测
            string exe = _settings.PlayerPath;
            string name = Path.GetFileNameWithoutExtension(exe ?? "");

            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                var p = PlayerLocator.Find();
                if (p != null)
                {
                    exe = p.ExePath;
                    name = p.Name;
                }
            }

            bool ok;
            if (!string.IsNullOrEmpty(exe))
                ok = PlayerLocator.Play(exe, name, path);
            else
                ok = PlayerLocator.OpenWithSystem(path);   // 没找到已知播放器 → 系统默认

            if (ok)
                Log("正在用 " + (string.IsNullOrEmpty(name) ? "系统默认程序" : name) + " 播放：" + tr.Name);
            else
                Log("打开播放器失败。可以在设置里指定播放器路径，或装一个 VLC / mpv。");
        }

        // ================= 杂项 =================

        /// <summary>
        /// 界面日志。**同时写一份到文件**（~/.config/GeZi/GeZi.log）。
        ///
        /// 为什么必须落盘：出问题时用户只能描述现象，而界面上那几行往往不够
        /// 定位（尤其异常堆栈）。有了文件日志，排查时直接看最后几十行就有答案，
        /// 不用让用户复述或截屏。
        /// </summary>
        /// <remarks>
        /// 🚨🚨 2026-11-14 —— 「下载时界面点不动」的**主因就在这个方法**，三处叠加：
        ///
        /// 1) **每条日志都在 UI 线程同步写盘**（<see cref="AppendToLogFile"/> 里的
        ///    <c>File.AppendAllText</c> + 每次 <c>FileInfo</c> 建对象查长度）。
        ///    引擎在高并发下每秒能产上百条，UI 线程被 IO 拖住，点击事件排在后面。
        /// 2) **每条日志都全量重建文本**：<c>_logText.Text = string.Join(200 行)</c>。
        ///    TextBlock 带 <c>TextWrapping="Wrap"</c> 时，每次赋值都要把 200 行
        ///    **重新测量、重新折行**。200 行 × 每秒 158 次 ≈ 每秒 3 万行折行。
        /// 3) 引擎的 `[调试]` 级日志**全部**进界面（WPF 版早就过滤掉了，见
        ///    MainWindow.Log.cs 的同名判据），而实测 158 行/秒里 11940 条全是
        ///    `[调试] 配置项 … 已变更` —— 纯噪声，一条都不该显示。
        ///
        /// 修法：调试级直接丢弃；其余入队，由 <see cref="_logFlushTimer"/> 每 250ms
        /// 攒批刷一次控件，落盘也挪到那个时机（一次写多行）。
        /// </remarks>
        private void Log(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;

            // ① 引擎的调试级日志一律不进界面。
            //    与 WPF 版（src/GeZi/MainWindow.Log.cs）同一判据：
            //    逐包/逐分片级别的追踪没有日常诊断价值，高并发下每秒上万条。
            if (msg.StartsWith("[调试]", StringComparison.Ordinal)
                || msg.StartsWith("[debug]", StringComparison.Ordinal))
                return;

            // ② 重复抑制：同一模板 8 秒内只出 1 条，窗口到期补一条「同类 N 条已合并」。
            //    否则「取消一次下载灌 200 条分片错误」会把正常日志挤光。
            string summary = null;
            if (!LogDupExempt(msg))
            {
                string key = LogDupKey(msg);
                lock (_logDupGate)
                {
                    DateTime now = DateTime.UtcNow;
                    if (!_logDupSince.TryGetValue(key, out DateTime since))
                    {
                        _logDupSince[key] = now;
                        _logDupCount[key] = 0;
                    }
                    else if ((now - since) >= LogDupWindow)
                    {
                        _logDupCount.TryGetValue(key, out int n);
                        _logDupSince[key] = now;
                        _logDupCount[key] = 0;
                        if (n > 0) summary = "（同类 " + n + " 条已合并）";
                    }
                    else
                    {
                        _logDupCount.TryGetValue(key, out int n);
                        _logDupCount[key] = n + 1;
                        return;   // 窗口内重复 → 只计数，不入队
                    }
                }
            }

            // ③ 入队。任意线程可调，不碰控件、不碰磁盘。
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            _logPending.Enqueue(line);
            if (summary != null)
                _logPending.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + summary);
        }

        /// <summary>
        /// 把攒下的日志一次性刷进界面与磁盘。**只在 UI 线程调用**。
        ///
        /// 一次只处理一批（上限 <see cref="LogFlushBatch"/>），剩下的留给下一拍 ——
        /// 万一积压很多，也不让一次刷新占住 UI 几十毫秒。
        /// </summary>
        private void FlushLog()
        {
            if (_logPending.IsEmpty) return;

            var lines = new List<string>();
            var sb = new System.Text.StringBuilder();
            while (lines.Count < LogFlushBatch && _logPending.TryDequeue(out string line))
            {
                lines.Add(line);
                sb.Append(line).Append('\n');
            }
            if (lines.Count == 0) return;

            foreach (string line in lines)
            {
                _logLines.Add(line);
            }
            int overflow = _logLines.Count - LogMaxLines;
            if (overflow > 0)
                _logLines.RemoveRange(0, overflow);

            // 一次赋值，一次折行 —— 不是每条一次。
            _logText.Text = string.Join(Environment.NewLine, _logLines);

            // 磁盘写入同样攒批：一次 AppendAllText 写 N 行，而不是 N 次。
            AppendToLogFile(sb.ToString());
        }

        private static readonly object LogFileLock = new object();
        private static readonly object LogFileWriteGate = new object();
        private static bool _logFileWritePending;

        /// <summary>
        /// 把一批日志行追加到磁盘。**不在 UI 线程同步做** ——
        /// 同步写盘是「下载时界面卡顿」的直接原因之一（见 <see cref="Log"/> 的注释）。
        /// 这里丢到线程池，并用 <see cref="_logFileWritePending"/> 合并并发写入，
        /// 避免高并发下开出一堆线程同时抢 <see cref="LogFileLock"/>。
        /// </summary>
        private static void AppendToLogFile(string lines)
        {
            if (string.IsNullOrEmpty(lines)) return;

            lock (LogFileWriteGate)
            {
                if (_logFileWritePending) return;   // 已有一个写任务在排队，丢掉这批（日志不要求全量）
                _logFileWritePending = true;
            }

            Task.Run(() =>
            {
                try
                {
                    lock (LogFileLock)
                    {
                        string path = Path.Combine(
                            Path.GetDirectoryName(SettingsStore.ConfigPath) ?? ".", "GeZi.log");

                        // 超过 1MB 就轮转，避免无限增长
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 1024 * 1024)
                        {
                            string old = path + ".1";
                            try { File.Delete(old); } catch { }
                            File.Move(path, old);
                        }

                        File.AppendAllText(path,
                            DateTime.Now.ToString("yyyy-MM-dd ") + lines.Replace("\n", Environment.NewLine));
                    }
                }
                catch
                {
                    // 写日志失败绝不能影响主流程
                }
                finally
                {
                    lock (LogFileWriteGate) _logFileWritePending = false;
                }
            });
        }

        /// <summary>把一条日志归一成模板键：抹掉数字、分片 ID、十六进制串，保留中文骨架。</summary>
        private static string LogDupKey(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "";
            string s = System.Text.RegularExpressions.Regex.Replace(msg, "[0-9a-fA-F]{6,}", "H");
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\d+", "N");
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }

        /// <summary>这些是结果性信息，重复也必须每条都显示，不参与抑制。</summary>
        private static bool LogDupExempt(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return true;
            return msg.IndexOf("全部完成", StringComparison.Ordinal) >= 0
                || msg.IndexOf("下载完成", StringComparison.Ordinal) >= 0
                || msg.IndexOf("完成：", StringComparison.Ordinal) >= 0
                || msg.IndexOf("失败：", StringComparison.Ordinal) >= 0
                || msg.IndexOf("已成功删除", StringComparison.Ordinal) >= 0
                || msg.IndexOf("已取消", StringComparison.Ordinal) >= 0
                || msg.IndexOf("连接租约", StringComparison.Ordinal) >= 0;
        }

        private static string ShortenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal))
                return "~" + path.Substring(home.Length);
            return path;
        }
    }
}
