// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分，2026-10-24） ----
// 成员按功能归组，全部成员声明在同一 partial 类里，作用域与拆分前完全一致。
// 职责：历史记录（分享/下载）

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton（续传列表的真复选框）
using System.Windows.Data;                  // Binding（同上）
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;   // RenderTargetBitmap（主题切换的双快照）
using System.Windows.Threading;
using GeZi.Core.Api;
using GeZi.DownloadEngine;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Services;
using System.Text.RegularExpressions;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Run = System.Windows.Documents.Run;
using TextTrimming = System.Windows.TextTrimming;
using DiagClock = System.Diagnostics.Stopwatch;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;
using ShapePath = System.Windows.Shapes.Path;   // 避免与 System.IO.Path 撞名

namespace GeZi
{
    public partial class MainWindow : Window
    {

        private void ShowHistoryPanel()
        {
            try
            {
                RebuildHistoryPanel();   // 每次打开都重建：这中间可能又解析过新分享
                if (HistoryOverlay == null) return;
                HistoryOverlay.Visibility = Visibility.Visible;
                // 把焦点收到浮层自己身上：否则之后随手敲的字母会钻进底下那个**看不见的**
                // 链接框（把链接改脏），或者变成文件列表的首字母跳转。
                HistoryOverlay.Focus();
            }
            catch (Exception ex)
            {
                LogDetail("打开解析历史失败: " + ex.Message);
            }
        }


        /// <summary>
        /// 解析历史面板右上角的关闭叉号。
        /// 复用既有的 HideHistoryPanel（与 Esc、点遮罩走同一条路径）。
        /// </summary>
        private void OnHistoryCloseClick(object sender, RoutedEventArgs e)
        {
            HideHistoryPanel();
        }


        private void HideHistoryPanel()
        {
            try { if (HistoryOverlay != null) HistoryOverlay.Visibility = Visibility.Collapsed; } catch { }
        }


        /// <summary>点遮罩（面板外的暗色区域）= 关闭。面板本身是它的兄弟节点，不会误触。</summary>
        private void OnHistoryOverlayClick(object sender, MouseButtonEventArgs e)
        {
            HideHistoryPanel();
        }


        private void OnHistoryClearClick(object sender, RoutedEventArgs e)
        {
            try
            {
                HistoryStore.ClearShareHistory();
                RebuildHistoryPanel();
                Log("已清空解析历史。");
            }
            catch (Exception ex)
            {
                Log("清空解析历史失败: " + ex.Message);
            }
        }


        /// <summary>重建列表：分组标题 + 可点行（都动态生成，见 MakeHistoryRow）。</summary>
        private void RebuildHistoryPanel()
        {
            if (HistoryListPanel == null) return;
            HistoryListPanel.Children.Clear();
            _historyBadges.Clear();          // 行是重新造的，旧标签引用全部作废
            int gen = ++_historyGen;

            var items = HistoryStore.GetShareHistory();
            if (HistoryCountText != null)
                HistoryCountText.Text = items.Count > 0 ? "共 " + items.Count + " 条" : "";

            if (items.Count == 0)
            {
                HistoryListPanel.Children.Add(new TextBlock
                {
                    Text = "还没有解析记录。\n解析过的分享会留在这里，下次点一下就能重新打开。",
                    Style = (Style)FindResource("HistoryEmptyText"),
                });
                return;
            }

            // 🚨 2026-11-01：改成**分批加载**（与目录列表的 RevealNextBatch 同一套思路）。
            //   原来一次性把几百条行 + 分组标题全塞进 StackPanel，记录多时打开面板
            //   会明显卡一下。现在先铺第一批，滚动到底部再追加下一批。
            _historyItems = items;
            _historyCursor = 0;
            _historyLastGroup = null;
            _historyGen = gen;
            RevealNextHistoryBatch();

            // 行造完再后台校验失效（不 await：面板先出来，标签随后自己冒出来）
            _ = ProbeHistoryValidityAsync(gen);
        }


        // ---------------- 解析历史：分批加载（2026-11-01） ----------------

        /// <summary>历史面板一次铺多少条（与目录列表的 <c>DirRevealSize</c> 同量级）。</summary>
        private const int HistoryRevealSize = 20;

        /// <summary>本次要铺的全部记录（<see cref="RebuildHistoryPanel"/> 每次重建时刷新）。</summary>
        private List<ShareHistoryEntry> _historyItems = new List<ShareHistoryEntry>();

        /// <summary>已经铺到第几条（游标）。</summary>
        private int _historyCursor;

        /// <summary>上一批最后一个分组标题 —— 跨批次续接，避免同一组出现两个标题。</summary>
        private string _historyLastGroup;

        /// <summary>把下一批记录追加到面板尾部（滚动到底部时由 <see cref="OnHistoryScrollChanged"/> 调）。</summary>
        private void RevealNextHistoryBatch()
        {
            if (HistoryListPanel == null) return;
            if (_historyItems == null || _historyCursor >= _historyItems.Count) return;

            int from = _historyCursor;
            int to = Math.Min(from + HistoryRevealSize, _historyItems.Count);
            for (int i = from; i < to; i++)
            {
                var it = _historyItems[i];
                // 置顶的单独成组排在最前（GetShareHistory 已经把置顶的排到前面了）
                string g = it.Pinned ? "置顶" : HistoryGroupOf(it.Time);
                if (!string.Equals(g, _historyLastGroup, StringComparison.Ordinal))
                {
                    HistoryListPanel.Children.Add(new TextBlock
                    {
                        Text = g,
                        Style = (Style)FindResource("HistoryGroupLabel"),
                    });
                    _historyLastGroup = g;
                }
                HistoryListPanel.Children.Add(MakeHistoryRow(it));
            }
            _historyCursor = to;
        }


        /// <summary>
        /// 历史面板滚动：快到底部时追加下一批。
        /// <para>
        /// 阈值留 2 条行高（约 120px）：等到真的贴底才加载，用户会看到一段空白停顿；
        /// 提前一点加载，滚动是连续的。
        /// </para>
        /// </summary>
        private void OnHistoryScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 只有纵向滚动范围/位置变化才关心，横向（不该出现）直接忽略。
            if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;

            var sv = sender as ScrollViewer;
            if (sv == null) return;
            // 还没铺满一屏时 ExtentHeight 可能为 0，此时也允许继续铺（避免首屏就不加载）。
            if (sv.ScrollableHeight <= 0) { RevealNextHistoryBatch(); return; }

            double remain = sv.ExtentHeight - (sv.VerticalOffset + sv.ViewportHeight);
            if (remain <= 120) RevealNextHistoryBatch();
        }


        // ---------------- 解析历史：失效校验 ----------------

        /// <summary>pwd_id → 那一行的「已失效」标签（每次重建面板重填）。</summary>
        private readonly Dictionary<string, Border> _historyBadges = new Dictionary<string, Border>();

        /// <summary>
        /// 上次校验结论是否还"够新"（1 小时内）。
        /// 结论本身已经落盘在 <c>ShareHistoryEntry.Validity</c> 里，这里只判断它是不是过期了
        /// —— 过期就重新打一次网络（分享可能中途被恢复/被删）。
        /// 时间串解析失败（老数据、手改过）一律当作"过期"，宁可多探一次。
        ///
        /// 🚨 2026-11-15 由 10 分钟放宽到 1 小时：失效探测用的是「只换凭证不列目录」的轻量请求，
        /// 一条就一个 GET，50 条历史全探也就 50 个包，没必要 10 分钟就重探一遍刷网盘。
        /// </summary>
        private static bool FreshEnough(string at)
        {
            if (string.IsNullOrEmpty(at)) return false;
            DateTime t;
            if (!DateTime.TryParse(at, out t)) return false;
            return (DateTime.Now - t).TotalMinutes < 60;
        }

        /// <summary>面板重建代次：异步校验回来时代次变了就丢弃结果（行已经不在屏幕上了）。</summary>
        private int _historyGen;

        /// <summary>
        /// 校验解析历史里保存的链接**是否还有效**，失效的在行尾打「已失效」标签。
        ///
        /// 🚨 2026-11-15 改成**两阶段**（用户要求「占性能机器之少并且快速检测」）：
        /// <para>
        /// 阶段一「只换凭证」—— <c>IPanProvider.ProbeShareAsync</c>：只做一次
        /// 「拿这个分享的临时凭证」的请求，**不列目录**。这一步就能判两种最常见的情况：
        /// 服务端明确拒绝（分享被删/过期/取消）= 确定失效；提取码不对 = 也是失效。
        /// 它比原来直接调 <c>ListShareAsync</c> 便宜 —— 原来每条都要「换凭证 + 列一次目录」，
        /// 而失效的链接根本列不了目录，白白多花一个请求。
        /// </para>
        /// <para>
        /// 阶段二「列一次目录」—— 只有阶段一判定**还有效**的链接才做这一步，
        /// 因为用户 2026-10-26 明确要求「分享还在但里面被清空（或者当初解析的那个子目录
        /// 已经没了）」也算失效（点进去是空列表，和失效没区别）。这一步只对"活着的"
        /// 链接发生，失效的那一批不会白白列目录。
        /// </para>
        ///
        /// 三条纪律：
        /// ① **四家都探**（2026-10-26 放开）。原来只查夸克 / UC，理由是"阿里 / 迅雷 换凭证是另一套
        ///    接口，拿夸克客户端去探会一律抛异常 → 把好好的链接标成已失效，那是说谎"。
        ///    现在四家各走自己的实现，不再有这个问题。
        /// ② 并发限 3：一次开十几条历史时不至于把界面线程/网络打满。
        /// ③ 结果按 pwd_id 缓存 1 小时：反复开关面板不重复请求。
        /// </summary>
        private async Task ProbeHistoryValidityAsync(int gen)
        {
            var todo = new List<ShareHistoryEntry>();
            foreach (var it in HistoryStore.GetShareHistory())
            {
                if (string.IsNullOrEmpty(it.PwdId)) continue;
                // 🚨 2026-11-15 去掉了「只探已经铺出来的行」的限制：
                //   原来这里有一句 `if (!_historyBadges.ContainsKey(it.PwdId)) continue;`，
                //   意思是**没滚动到的历史记录根本不会被探测** —— 这正是用户说的
                //   「记录根本不检测」。历史面板一次只铺 20 行，剩下 30 条永远不会被探，
                //   它们的「已失效」标签也就永远不会出现。
                //   现在改成**全量探测**：面板打开就把所有历史都探一遍，
                //   探到的结论落盘（ShareHistoryEntry.Validity），
                //   之后滚动铺出行时 MakeHistoryRow 会直接读 Validity==2 显示「已失效」。
                // 只探我们真的能构造出客户端的四家；别的（将来新增的）宁可不显示标签。
                if (it.PanKind < (int)PanKind.Quark || it.PanKind > (int)PanKind.Xunlei) continue;

                // 缓存有效期 1 小时：结论已经落盘（ShareHistoryEntry.Validity / ValidityAt），
                // 所以"本会话探过"和"上次运行探过"用同一套判断，不再需要单独的内存字典。
                if (it.Validity != 0 && FreshEnough(it.ValidityAt))
                    continue;
                todo.Add(it);
            }
            if (todo.Count == 0) return;

            // 同一家网盘共用一个临时客户端（复用 TLS 连接），别每条都 new 一个。
            var providers = new Dictionary<int, IPanProvider>();
            var verdicts = new Dictionary<string, int>();
            try
            {
                using (var gate = new SemaphoreSlim(3))
                {
                    var tasks = new List<Task>();
                    foreach (var it in todo)
                    {
                        var entry = it;
                        tasks.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            // 0 = 判断不了（网络问题，不落盘、不显示标签）；1 = 有效；2 = 失效。
                            int verdict = 0;
                            try
                            {
                                IPanProvider p;
                                lock (providers)
                                {
                                    if (!providers.TryGetValue(entry.PanKind, out p))
                                    {
                                        p = BuildProbeProvider((PanKind)entry.PanKind);
                                        providers[entry.PanKind] = p;
                                    }
                                }
                                if (p == null) return;

                                var shareRef = new PanShareRef
                                {
                                    Kind = (PanKind)entry.PanKind,
                                    ShareId = entry.PwdId,
                                    Passcode = entry.Passcode ?? "",
                                    StartFid = string.IsNullOrEmpty(entry.StartFid) ? "0" : entry.StartFid,
                                };

                                // 阶段一：只换凭证，不列目录。
                                var probe = await p.ProbeShareAsync(shareRef).ConfigureAwait(false);
                                if (probe == null) return;
                                if (probe.IsInvalid)
                                {
                                    verdict = 2;   // 服务端明确拒绝 = 确定失效
                                }
                                else if (probe.IsAlive)
                                {
                                    // 阶段二：只有"还活着"的才列一次目录，看是不是空分享。
                                    try
                                    {
                                        var files = await p.ListShareAsync(shareRef, shareRef.StartFid)
                                            .ConfigureAwait(false);
                                        verdict = (files != null && files.Count > 0) ? 1 : 2;
                                    }
                                    catch (QuarkApiException)
                                    {
                                        // 服务端明确拒绝（分享被删 / 子目录没了）= 失效。
                                        verdict = 2;
                                    }
                                    catch
                                    {
                                        // 网络问题 ≠ 失效，别把好好的链接标红。
                                        verdict = 0;
                                    }
                                }
                                else
                                {
                                    // Unknown：连不上或者需要提取码才能判断 —— 不下结论。
                                    verdict = 0;
                                }
                            }
                            catch (QuarkApiException)
                            {
                                // 🚨🚨 2026-10-26 修「明明失效却不显示已失效」：
                                //   QuarkApiException 是服务端**明确拒绝**（分享不存在 / 已过期 /
                                //   密码被改 / 文件被删），它恰恰是"失效"最硬的证据。
                                verdict = 2;
                            }
                            catch
                            {
                                // 剩下的才是真的网络问题（超时 / DNS / 断网 / TLS）。
                                // 这种情况不能算"失效"，否则断网时整列历史都会变红 —— 那是说谎。
                                verdict = 0;
                            }
                            finally
                            {
                                gate.Release();
                            }

                            if (verdict == 0) return;
                            lock (verdicts) verdicts[entry.PwdId] = verdict;
                            if (verdict == 1) return;

                            // 回到 UI 线程改标签；代次变了说明面板已经重建，这一行作废。
                            try
                            {
                                // 用 `_ =` 丢掉 DispatcherOperation：它在 .NET 4.5+ 是可 await 的，
                                // 不丢弃的话编译器会报 CS4014（本项目要求 0 警告）。
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (gen != _historyGen) return;
                                    Border b;
                                    if (_historyBadges.TryGetValue(entry.PwdId, out b) && b != null)
                                        b.Visibility = Visibility.Visible;
                                }));
                            }
                            catch { }
                        }));
                    }
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }

                // 一次探测得出的一批结论统一落盘（只写一次 state.json）。
                // 落盘之后重开面板/重启软件都能立刻看到「已失效」，不用再等网络。
                if (verdicts.Count > 0)
                {
                    try { HistoryStore.SetShareValidity(verdicts); } catch { }
                }
            }
            finally
            {
                foreach (var kv in providers)
                {
                    if (kv.Value == null) continue;
                    try { kv.Value.Dispose(); } catch { }
                }
            }
        }


        /// <summary>
        /// 给「解析历史失效探测」造一个该网盘的临时客户端。
        /// <para>
        /// 🚨 2026-11-15：夸克 / UC **没登录也照样造**（Cookie 空就传空串）。
        /// 因为它们的 stoken 接口（sharepage/token）匿名即可访问 ——
        /// 原来这里「Cookie 空就 return null」，等于「没登录夸克 → 所有夸克历史一条都不探」，
        /// 这也是用户说的「记录根本不检测」的原因之一。
        /// 阿里 / 迅雷仍然必须有凭证（它们的换凭证接口要登录态），拿不到就返回 null，
        /// 调用方据此跳过、不显示标签（绝不瞎标）。
        /// </para>
        /// </summary>
        private IPanProvider BuildProbeProvider(PanKind kind)
        {
            switch (kind)
            {
                case PanKind.Quark:
                    {
                        // 匿名可探：Cookie 空也造一个（QuarkSession 不会因为 Cookie 空而报错）。
                        string cookie = _settings.Cookie ?? "";
                        return new QuarkProviderAdapter(new QuarkClient(cookie, PanKind.Quark), ownsClient: true);
                    }
                case PanKind.Uc:
                    {
                        string cookie = _settings.UcCookie ?? "";
                        return new QuarkProviderAdapter(new QuarkClient(cookie, PanKind.Uc), ownsClient: true);
                    }
                case PanKind.Aliyun:
                    {
                        string at = _settings.AliyunAccessToken ?? "";
                        string rt = _settings.AliyunRefreshToken ?? "";
                        if (string.IsNullOrWhiteSpace(at) && string.IsNullOrWhiteSpace(rt)) return null;
                        return string.IsNullOrWhiteSpace(at)
                            ? new AliyunClient(rt, null, null, null,
                                _settings.AliyunDeviceId, _settings.AliyunDevicePrivateKey)
                            : new AliyunClient(null, null, null, at,
                                _settings.AliyunDeviceId, _settings.AliyunDevicePrivateKey);
                    }
                case PanKind.Xunlei:
                    {
                        string token = _settings.XunleiPanToken ?? "";
                        if (string.IsNullOrWhiteSpace(token)) return null;
                        return AttachXunleiCaptcha(
                            new XunleiClient(token, _settings.XunleiCaptchaToken, null,
                                _settings.XunleiDeviceId));
                    }
                default:
                    return null;
            }
        }


        /// <summary>分组标签：按"距今天多久"给粗粒度分组（今天 / 昨天 / 更早）。</summary>
        private static string HistoryGroupOf(string raw)
        {
            DateTime t;
            if (!DateTime.TryParse(raw, out t)) return "更早";
            DateTime today = DateTime.Today;
            if (t.Date == today) return "今天";
            if (t.Date == today.AddDays(-1)) return "昨天";
            return "更早";
        }


        /// <summary>行里的时间：「今天 11:58」/「昨天 21:03」/「10-06 17:20」。</summary>
        private static string FormatHistoryTime(string raw)
        {
            DateTime t;
            if (!DateTime.TryParse(raw, out t)) return raw ?? "";
            DateTime today = DateTime.Today;
            if (t.Date == today) return "今天 " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.ToString("MM-dd HH:mm");
        }


        /// <summary>行里的副行：「今天 11:58 · /s/1f842a86 · 提取码 4k3p」。</summary>
        private static string HistoryMetaOf(ShareHistoryEntry it)
        {
            var sb = new StringBuilder(FormatHistoryTime(it.Time));
            if (!string.IsNullOrEmpty(it.PwdId))
            {
                sb.Append(" · /s/");
                sb.Append(it.PwdId.Length > 8 ? it.PwdId.Substring(0, 8) : it.PwdId);
            }
            if (!string.IsNullOrEmpty(it.Passcode))
                sb.Append(" · 提取码 ").Append(it.Passcode);
            return sb.ToString();
        }


        /// <summary>造一行历史。整行是个 Button，点 = 重新解析那条分享。</summary>
        private UIElement MakeHistoryRow(ShareHistoryEntry it)
        {
            // 图标：文件夹用"警示色的文件夹"（与文件列表里的文件夹图标同一套语言）；
            // 没有文件夹的（根目录只有散文件）用文件图标 + 次要色。
            var icon = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                "SurfaceSunkenBrush");
            icon.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
                "BorderBrushSoft");

            var glyph = new ShapePath
            {
                Data = it.IsFolder
                    ? (Geometry)FindResource("IconFolder")
                    : FileTypeIconConverter.GeometryFor(it.Name),
                Stretch = Stretch.Uniform,
                Width = 17, Height = 17,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 用 SetResourceReference（等价 DynamicResource）而不是直接赋 Brush：
            // 换主题时 Brushes.xaml 整份重建，握在手里的旧实例不一定跟着变。
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                it.IsFolder ? "WarningBrush" : FileTypeIconConverter.BrushKeyFor(it.Name));
            icon.Child = glyph;

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // 2026-11-15 用户要求「就地改名」：名字这一块平时是文字，
            //   右键「重命名」后**原地**变成输入框（光标竖杠 + 全选，直接打字覆盖）。
            //   点名字本身也能进编辑态（不用右键）。
            var nameEditor = InlineNameEditor.Create(
                it.Name ?? "",
                it.Name ?? "",
                "HistoryNameText",
                newName =>
                {
                    // 只改「显示名」，pwd_id / 提取码 / 起始 fid 一律不动 ——
                    // 那是这条记录的身份，改了它就再也点不开了。
                    string oldName = it.Name ?? "";
                    if (!HistoryStore.RenameShareHistory(it.PwdId, newName)) return false;
                    // 同步内存里这条的名字（否则这一行的 ToolTip 一直显示旧名）。
                    it.Name = newName;
                    Log("已重命名：" + oldName + " → " + newName);
                    NoticeFavorite("已重命名为「" + newName + "」");
                    return true;
                },
                null);
            meta.Children.Add(nameEditor);
            meta.Children.Add(new TextBlock
            {
                Text = HistoryMetaOf(it),
                Style = (Style)FindResource("HistoryMetaText"),
            });

            // 右上角的「已失效」小标签：面板打开后在后台逐条校验，**失效的才显示**
            // （校验逻辑见 ProbeHistoryValidityAsync）。默认收起，避免每行先闪一下灰字。
            var badgeText = new TextBlock { Text = "已失效", FontSize = 11 };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 2, 0),
                Visibility = Visibility.Collapsed,
                Child = badgeText,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "DangerLightBrush");
            // 🚨 2026-10-26 用户反馈「只显示一次，后面再打开就不显示了」：
            //   上次校验的结论已经落盘（ShareHistoryEntry.Validity），这里**造行时就先摆上**，
            //   不用再等后台探测 —— 否则每次重开面板都要重跑一遍网络才看得到红标。
            if (it.Validity == 2) badge.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(it.PwdId))
                _historyBadges[it.PwdId] = badge;

            // ⚠️ 外层用 Grid 而不是横向 StackPanel：名称那个省略号只在**宽度被约束**时才生效，
            //    StackPanel 会用无限宽去量孩子，长名字就直接撑出去、不截断。
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            meta.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(meta, 1);
            Grid.SetColumn(badge, 2);
            row.Children.Add(icon);
            row.Children.Add(meta);
            row.Children.Add(badge);

            var btn = new Button
            {
                Content = row,
                Style = (Style)FindResource("HistoryRowButton"),
                Tag = it,     // 点击时取回这一条（见 OnHistoryItemClick）
                ToolTip = "重新解析：" + (it.Name ?? ""),
            };
            btn.Click += OnHistoryItemClick;
            btn.ContextMenu = MakeHistoryMenu(it, nameEditor);
            // 告诉名称控件：外面这层按钮就是「整行可点」的那个
            //   （改名期间要把它的点击摘掉，否则改个名会被当成「重新打开这条」）。
            nameEditor.AttachHostButton(btn, OnHistoryItemClick);
            return btn;
        }


        /// <summary>行上右键：置顶 / 取消置顶、重命名、收藏、删除这条记录。</summary>
        private ContextMenu MakeHistoryMenu(ShareHistoryEntry it, InlineNameEditor nameEditor)
        {
            // ⚠️ 菜单项是**每次重建行时新建**的，所以 Header 直接按当前状态定；
            //    操作完都会 RebuildHistoryPanel()，标签跟着刷新（不用做双向绑定）。
            var menu = new ContextMenu();

            // 🚨 2026-11-15 用户指出「这两项左边有一片空白」。
            //    根因：菜单的隐式 MenuItem 样式是**固定 22px 图标列**（给有图标的菜单用），
            //    这两项都没有图标 ⇒ 图标列空着，文字又从 22px 之后开始 ⇒ 左边空一块。
            //    这两项本来就没图标，所以直接套 TrayMenuItem（纯文字紧凑模板，不带图标列）。
            var pin = new MenuItem { Header = it.Pinned ? "取消置顶" : "置顶", Style = Application.Current.TryFindResource("TrayMenuItem") as Style };
            pin.Click += (s, e) =>
            {
                try
                {
                    HistoryStore.SetSharePinned(it.PwdId, !it.Pinned);
                    RebuildHistoryPanel();
                    Log(it.Pinned ? "已取消置顶：" + it.Name : "已置顶：" + it.Name);
                }
                catch (Exception ex) { Log("置顶操作失败: " + ex.Message); }
            };

            var del = new MenuItem { Header = "删除这条记录", Style = Application.Current.TryFindResource("TrayMenuItem") as Style };
            del.Click += (s, e) =>
            {
                try
                {
                    HistoryStore.RemoveShareHistory(it.PwdId);
                    RebuildHistoryPanel();
                    Log("已从解析历史里删除：" + it.Name);
                }
                catch (Exception ex) { Log("删除记录失败: " + ex.Message); }
            };

            // 2026-11-15 用户要求：解析历史里的条目可以右键重命名。
            //   只改「显示名」，pwd_id / 提取码 / 起始 fid 一律不动 —— 那是这条记录的身份，
            //   改了它就再也点不开了（点开靠的是 pwd_id）。
            //   ⚠️ 是**就地编辑**（名字原地变输入框、光标竖杠 + 全选），不是弹框
            //      （用户原话：「在名字那里应该会显示鼠标光标的竖杠来表示这个地方可以修改了」）。
            var rename = new MenuItem { Header = "重命名", Style = Application.Current.TryFindResource("TrayMenuItem") as Style };
            rename.Click += (s, e) =>
            {
                try
                {
                    if (nameEditor == null) return;
                    nameEditor.BeginEdit();
                }
                catch (Exception ex) { Log("重命名失败: " + ex.Message); }
            };

            // 2026-11-15 用户要求：解析历史里的条目可以直接收藏到「收藏夹」里。
            //   收藏的是**这个分享链接本身**（不是某个文件）⇒ IsFile = false。
            //   收藏夹里点它会重新解析这个分享，所以提取码 / 起始 fid 都要带上。
            var fav = new MenuItem { Header = "收藏这条分享", Style = Application.Current.TryFindResource("TrayMenuItem") as Style };
            fav.Click += (s, e) =>
            {
                try
                {
                    var entry = new FavoriteEntry
                    {
                        PanKind = it.PanKind,
                        ShareId = it.PwdId ?? "",
                        Passcode = it.Passcode ?? "",
                        StartFid = string.IsNullOrEmpty(it.StartFid) ? "0" : it.StartFid,
                        Url = "",
                        Name = it.Name ?? "",
                        IsFile = false,
                        RelDir = "",
                    };
                    if (FavoriteStore.Add(entry))
                    {
                        NoticeFavorite("已收藏这个分享");
                        Log("已收藏分享：" + it.Name);
                    }
                    else
                    {
                        NoticeFavorite("这个分享已经在收藏夹里了");
                    }
                }
                catch (Exception ex) { Log("收藏失败: " + ex.Message); }
            };

            menu.Items.Add(pin);
            menu.Items.Add(rename);
            menu.Items.Add(fav);
            menu.Items.Add(del);
            return menu;
        }


        /// <summary>
        /// 文件区工具栏上那个「解析历史」按钮（h 键之外看得见的入口）。
        /// ⚠️ 别和任务页的「下载历史」（<c>HistoryBtn</c> / OnOpenHistory）搞混 ——
        /// 那个是**本地下载记录**，这个是**解析过的分享**。
        /// </summary>
        private void OnShareHistoryButtonClick(object sender, RoutedEventArgs e)
        {
            ShowHistoryPanel();
        }


        /// <summary>点历史里的一条 = 重新解析那条分享。</summary>
        private void OnHistoryItemClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var it = btn != null ? btn.Tag as ShareHistoryEntry : null;
            if (it == null || string.IsNullOrEmpty(it.PwdId)) return;

            HideHistoryPanel();

            // 原来那条链接指向某个子目录时，把 fid 也拼回去：OnParseClick 会把它当成
            // start_fid，于是回到上次看的那个目录，而不是分享根目录。
            string url = "https://pan.quark.cn/s/" + it.PwdId;
            if (!string.IsNullOrEmpty(it.StartFid) && it.StartFid != "0")
                url += "/" + it.StartFid;

            LogDetail("从解析历史打开：「" + (it.Name ?? it.PwdId) + "」");
            FillAndParse(url, it.Passcode);
        }


        /// <summary>解析成功后记一条历史（供 h 面板）。</summary>
        private void RecordShareHistory(string pwdId, string passcode, string startFid)
        {
            try
            {
                // 名字取"解析后看到的第一个文件夹" —— 这正是用户认这条记录的依据；
                // 分享根目录只有散文件时退化成「（根目录）· N 个文件」。
                var firstDir = _files.FirstOrDefault(f => f.IsDir);
                string name = firstDir != null
                    ? firstDir.Name
                    : "（根目录）· " + _files.Count + " 个文件";
                HistoryStore.AddShareHistory(pwdId, passcode, startFid, name, firstDir != null,
                    (int)_panKind);
            }
            catch { }
        }


        /// <summary>
        /// 造一条「下载历史」列表行：文件类型图标 + 文件名 + 时间 · 大小 + 状态圆点。
        /// <para>
        /// 2026-10-26 重做。原来是一行拼接字符串
        /// （<c>时间   [状态]   大小   名称</c>），同字号同颜色、没有列对齐，
        /// 一屏十几行看下来像日志文件（用户截图反馈"丑"）。
        /// 现在照「解析历史」面板的做法：左侧图标、主标题、副标题、右侧状态，
        /// 眼睛能顺着图标和状态色扫，不用逐字读。
        /// </para>
        /// </summary>
        private Border BuildHistoryRow(HistoryRecord r)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // 图标
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 文字
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // 状态

            // ---- 左：文件类型图标（与主界面文件列表同一套）----
            var glyph = new System.Windows.Shapes.Path
            {
                Data = FileTypeIconConverter.GeometryFor(r.Name),
                Stretch = Stretch.Uniform,
                Width = 17,
                Height = 17,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                FileTypeIconConverter.BrushKeyFor(r.Name));
            System.Windows.Controls.Grid.SetColumn(glyph, 0);
            grid.Children.Add(glyph);

            // ---- 中：文件名（主）+ 时间 · 大小（次）----
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock
            {
                Text = string.IsNullOrEmpty(r.Name) ? "(未命名)" : r.Name,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            stack.Children.Add(nameText);

            var subParts = new List<string>();
            if (!string.IsNullOrEmpty(r.Time)) subParts.Add(r.Time);
            if (!string.IsNullOrEmpty(r.Size)) subParts.Add(r.Size);
            var subText = new TextBlock
            {
                Text = string.Join("  ·  ", subParts),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            subText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            stack.Children.Add(subText);

            System.Windows.Controls.Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            // ---- 右：状态（圆点 + 文字，不用色块，避免一整排实心标签）----
            string status = (r.Status ?? "").Trim();
            string dotKey = "TextTertiaryBrush";
            string textKey = "TextTertiaryBrush";
            if (status.Contains("完成") || status.Contains("成功"))
            {
                dotKey = "SuccessBrush";
                textKey = "SuccessBrush";
            }
            else if (status.Contains("失败") || status.Contains("错误"))
            {
                dotKey = "DangerBrush";
                textKey = "DangerBrush";
            }
            else if (status.Length == 0)
            {
                status = "未知";
            }

            var badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
            badge.Children.Add(dot);
            var badgeText = new TextBlock
            {
                Text = status,
                FontSize = 11,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
            badge.Children.Add(badgeText);
            System.Windows.Controls.Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);

            return new Border
            {
                Child = grid,
                Padding = new Thickness(16, 9, 16, 10),
                Background = Brushes.Transparent,
            };
        }


        // ---------------- 下载历史 ----------------

        /// <summary>
        /// 「下载历史」：用最简单的窗口列出记录，支持打开/定位/清空。
        /// 移植自 Python Flet 版 `_on_open_history`。
        /// UI 刻意保持朴素（ListBox + 按钮），只求功能可用。
        /// </summary>
        private void OnOpenHistory(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = "下载历史",
                Owner = this,
            };
            DialogChrome.StyleWindow(win, 760, 520);

            var grid = DialogChrome.DialogRoot();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 标题已画在自绘标题栏里（2026-10-26），内容区不再重复。
            var bar = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12),
            };
            // 三个按钮「模板感太浓」→ 改成透明背景的幽灵按钮。
            // 只保留文字 + 悬停反馈；「清空记录」是破坏性操作，用**红字**而不是红底
            // （红底实心块在这一排里视觉重量过重，且和旁边的白底描边不在一个体系）。
            var ghostStyle = TryFindResource("GhostButton") as Style;
            var openBtn = new Button { Content = "打开", Style = ghostStyle };
            var locateBtn = new Button { Content = "定位", Style = ghostStyle };
            var clearBtn = new Button { Content = "清空记录", Style = ghostStyle };
            var clearDanger = TryFindResource("DangerBrush") as Brush;
            if (clearDanger != null) clearBtn.Foreground = clearDanger;
            bar.Children.Add(openBtn);
            locateBtn.Margin = new Thickness(8, 0, 0, 0);
            bar.Children.Add(locateBtn);
            clearBtn.Margin = new Thickness(8, 0, 0, 0);
            bar.Children.Add(clearBtn);
            System.Windows.Controls.Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            var list = DialogChrome.StyledList();
            list.FontSize = 12;
            var listCard = DialogChrome.Card(list);
            listCard.Padding = new Thickness(0);
            System.Windows.Controls.Grid.SetRow(listCard, 2);
            grid.Children.Add(listCard);

            // 每行：图标 + 文件名（主）+ 时间 · 大小（次）+ 右侧状态徽章。
            // ⚠️ 2026-10-26 之前是一行拼接字符串
            //   "2026-10-25 17:53:36   [失败]   0 B   《你的名字》.jpg"
            //   —— 同字号同颜色，看着像日志文件而不是列表（用户反馈"丑"）。
            var records = new List<HistoryRecord>();

            // 2026-11-01：改成分批追加（与解析历史、目录列表同一套思路）。
            //   原来一次性把全部记录 Add 进去 —— ListBox 虽然虚拟化，
            //   但每个 item 都要先构造 WPF 控件对象，几百条时打开窗口会明显顿一下。
            const int HistoryBatch = 30;
            int revealed = 0;

            Action revealMore = () =>
            {
                int to = Math.Min(revealed + HistoryBatch, records.Count);
                for (int i = revealed; i < to; i++)
                    list.Items.Add(BuildHistoryRow(records[i]));
                revealed = to;
            };

            Action reload = () =>
            {
                records = HistoryStore.GetHistory();
                list.Items.Clear();
                revealed = 0;
                if (records.Count == 0)
                {
                    var empty = new TextBlock
                    {
                        Text = "还没有下载记录",
                        FontSize = 12,
                        Margin = new Thickness(16, 18, 16, 18),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                    list.Items.Add(empty);
                    return;
                }
                revealMore();
            };

            // 滚到底部时追加下一批（阈值 3 条行高，滚动才连续）。
            list.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler((s2, e2) =>
                {
                    if (e2.VerticalChange == 0 && e2.ExtentHeightChange == 0) return;
                    if (revealed >= records.Count) return;
                    var sv = e2.OriginalSource as System.Windows.Controls.ScrollViewer;
                    if (sv == null) return;
                    if (sv.ScrollableHeight <= 0) { revealMore(); return; }
                    if (sv.ExtentHeight - (sv.VerticalOffset + sv.ViewportHeight) <= 90)
                        revealMore();
                }));

            reload();

            HistoryRecord Sel()
            {
                int i = list.SelectedIndex;
                return (i >= 0 && i < records.Count) ? records[i] : null;
            }

            openBtn.Click += (s2, e2) =>
            {
                var r = Sel();
                if (r == null || string.IsNullOrEmpty(r.Path)) { Log("请先选中一条记录"); return; }
                if (!File.Exists(r.Path)) { Log("文件已不存在：" + r.Path); return; }
                OpenTaskFileByPath(r.Path);
            };
            locateBtn.Click += (s2, e2) =>
            {
                var r = Sel();
                if (r == null || string.IsNullOrEmpty(r.Path)) { Log("请先选中一条记录"); return; }
                if (!ShellReveal.Reveal(r.Path))
                    Log("定位失败：" + r.Path);
            };
            clearBtn.Click += (s2, e2) =>
            {
                var ans = AppDialog.Show(win,
                    "确定清空全部下载记录吗？（不会删除磁盘上的文件）",
                    "清空记录", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (ans != MessageBoxResult.Yes) return;
                HistoryStore.ClearHistory();
                reload();
                Log("已清空下载历史");
            };

            win.Content = grid;
            win.ShowDialog();
        }


        // ⚠️ 2026-11-01（用户拍板方案 B）：RegisterPartsDirHistory(string) 已删除。
        //    它唯一的作用是把「分片/缓存根目录」输入框的旧值记进 PartsDirsKnown，
        //    而那个输入框已整块移除，没有入口能再改分片根目录 ⇒ 记历史已无意义。
        //    PartsDirsKnown 字段本身**必须保留**：AllPartsRoots() 仍靠它扫老目录残留。
    }
}
