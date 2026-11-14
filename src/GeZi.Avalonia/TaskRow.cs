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
using System.ComponentModel;
using Avalonia.Media.Imaging;
using GeZi.Providers.Cloud;
using GeZi.Core.Download;
using GeZi.Core.Models;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 界面上的一条分享文件（解析出来待选，或正在下载）。
    ///
    /// 实现 INotifyPropertyChanged 让进度/状态变化能自动刷新到界面 ——
    /// Avalonia 的绑定和 WPF 一样靠这个接口，不需要额外框架。
    /// </summary>
    public sealed class FileRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>来源条目（含 fid / token / 大小）。</summary>
        public QuarkFileEntry Entry { get; }

        /// <summary>
        /// 统一的云盘条目（多网盘通道用）。
        /// 与 <see cref="Entry"/> 二选一：夸克老通道填 Entry，其它 platform 填 CloudEntry。
        /// </summary>
        public CloudFile CloudEntry { get; }

        /// <summary>
        /// 相对于**当前浏览目录**的子路径（仅递归展开出来的条目才有值）。
        /// <para>
        /// 勾选一个文件夹下载时，里面的文件要按原目录结构落盘，
        /// 否则「a/1.zip」和「b/1.zip」会撞名成「1.zip」和「1 (1).zip」，
        /// 分卷压缩包这类场景直接就废了。
        /// </para>
        /// <para>空串 = 就在当前目录下（用户直接勾的文件）。</para>
        /// </summary>
        public string RelDir { get; set; } = "";

        /// <summary>所属分享的 pwd_id（下载时取直链要用）。</summary>
        public string PwdId { get; }

        /// <summary>取到的分享凭证 stoken（会随重解析更新）。</summary>
        public string Stoken { get; private set; }

        /// <summary>提取码（stoken 过期重解析时要带上）。</summary>
        public string Passcode { get; }

        public string Fid => Entry.Fid;
        public string Token => Entry.Token;
        public string Name => Entry.Name;
        public long Size => Entry.Size;

        public FileRow(QuarkFileEntry entry, string pwdId, string stoken, string passcode)
        {
            Entry = entry;
            PwdId = pwdId;
            Stoken = stoken;
            Passcode = passcode ?? "";
        }

        /// <summary>用统一的 <see cref="CloudFile"/> 构造（多网盘通道）。</summary>
        public FileRow(CloudFile entry, string passcode = "")
        {
            CloudEntry = entry;
            PwdId = "";
            Stoken = "";
            Passcode = passcode ?? "";

            // 同时构造一份 QuarkFileEntry，让所有既有界面逻辑（Name/Size/IsDir/Token）
            // 不需要区分来源 —— 两套模型字段几乎一一对应。
            Entry = new QuarkFileEntry
            {
                Fid = entry?.Id ?? "",
                Name = entry?.Name ?? "",
                Size = entry?.Size ?? 0,
                IsDir = entry?.IsDirectory ?? false,
                Token = entry?.Token ?? "",
            };
        }

        /// <summary>
        /// 分享凭证过期、重新解析成功后回写。
        /// 不回写的话，后续每次换链都会拿着过期凭证再失败一次。
        /// </summary>
        public void UpdateCredentials(string stoken, string token)
        {
            if (!string.IsNullOrEmpty(stoken))
                Stoken = stoken;

            // ⚠️ Entry.Token 是可变属性，这里直接改它（QuarkFileEntry 的 Token 是 set 的）
            if (!string.IsNullOrEmpty(token))
                Entry.Token = token;
        }

        // ---------------- 界面状态 ----------------

        private bool _selected = true;
        /// <summary>是否勾选下载。</summary>
        public bool Selected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; Raise(nameof(Selected)); } }
        }

        /// <summary>
        /// 大小的人类可读形式。
        /// 文件夹的大小要递归求和才知道（见 <see cref="DirSize"/>），
        /// 列表里不为此额外发请求，所以这里留空。
        /// </summary>
        public string SizeText => Entry.IsDir
            ? (DirSize > 0 ? FormatSize(DirSize) : "")
            : FormatSize(Size);

        /// <summary>文件夹勾选后由下载流程回填的真实总大小（0 = 未知/未计算）。</summary>
        private long _dirSize;
        public long DirSize
        {
            get => _dirSize;
            set
            {
                if (_dirSize == value) return;
                _dirSize = value;
                Raise(nameof(DirSize));
                Raise(nameof(SizeText));
            }
        }

        public string Kind => Entry.IsDir ? "文件夹" : "文件";

        /// <summary>列表里显示的名字。文件夹加个视觉前缀，让人知道能进。</summary>
        public string DisplayName => Entry.IsDir ? "📁 " + Entry.Name : Entry.Name;

        /// <summary>
        /// 是否显示勾选框。
        /// <para>
        /// **文件夹也要能勾选**（2026-11 改）。原来这里是 <c>!Entry.IsDir</c>，
        /// 导致文件夹前面没有复选框 —— 用户想「整个文件夹一起下载」时无从下手，
        /// 只能双击进去、逐个勾文件，几百个文件的目录基本不可用。
        /// </para>
        /// <para>
        /// 勾上文件夹后由下载流程递归展开成具体文件（见
        /// <c>MainWindow.ExpandSelectionAsync</c>），所以这里放开不会有副作用。
        /// 双击进入的交互仍然保留，两者互不冲突。
        /// </para>
        /// </summary>
        public bool CanSelect => true;

        /// <summary>悬停提示。</summary>
        public string Hint => Entry.IsDir
            ? "双击进入文件夹；勾选则连同其中所有文件一起下载"
            : Entry.Name;

        public static string FormatSize(long n)
        {
            // 与 WPF 版一致用 1000 进制（1 MB = 10^6 B），避免"夸克页面 9.54GB / 本地 8.89GB"的困惑
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1000 && i < units.Length - 1) { v /= 1000.0; i++; }
            return i == 0 ? n + " B" : v.ToString("F2") + " " + units[i];
        }
    }

    /// <summary>下载中的一条任务（包住核心的 DownloadJob，附加界面状态）。</summary>
    public sealed class TaskRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public DownloadJob Job { get; }
        public string Name => Job.Name;
        public long TotalSize { get; }

        /// <summary>
        /// 对应的源分享条目（取直链、换链都要用它）。
        /// 直接持有引用，**不要**用「文件名 + 大小」去列表里反查 ——
        /// 同一个分享里完全可能有同名同大小的文件（不同子目录下），
        /// 反查会取错条目，进而拿到别人的 fid 去下错文件。
        /// </summary>
        public FileRow Source { get; }

        public TaskRow(DownloadJob job, FileRow source)
        {
            Job = job;
            Source = source;
            TotalSize = source?.Size ?? 0;
        }

        private long _done;
        /// <summary>已下载字节数。</summary>
        public long Done
        {
            get => _done;
            set
            {
                if (_done == value) return;
                _done = value;
                Raise(nameof(Done));
                Raise(nameof(Percent));
                Raise(nameof(ProgressText));
                Raise(nameof(SizeText));
                Raise(nameof(CanPlayPartial));
            }
        }

        private double _speed;
        public double Speed
        {
            get => _speed;
            set
            {
                if (Math.Abs(_speed - value) < 1) return;
                _speed = value;
                Raise(nameof(SpeedText));
            }
        }

        /// <summary>0..100，给 ProgressBar 用。</summary>
        public double Percent => TotalSize > 0 ? Math.Min(100.0, Done * 100.0 / TotalSize) : 0;

        public string ProgressText => TotalSize > 0
            ? Percent.ToString("F1") + "%"
            : FileRow.FormatSize(Done);

        /// <summary>「已下 / 总量」。</summary>
        public string SizeText => FileRow.FormatSize(Done) + " / " + FileRow.FormatSize(TotalSize);

        public string SpeedText => Speed > 0 ? FileRow.FormatSize((long)Speed) + "/s" : "";

        /// <summary>
        /// 用户主动点了「暂停」。
        ///
        /// 为什么必须有这个独立标志：下载器的进度回调会持续上报
        /// <c>JobState.Downloading</c>（已经阻塞住的 worker 在暂停期间仍会完成
        /// 手上那一次写入并上报）。如果回调无条件把状态写成"下载中"，
        /// 用户刚点的「暂停」会被瞬间覆盖 → 界面上看起来**暂停按了没反应**，
        /// 而且「继续」按钮因为状态不是"已暂停"而始终是灰的。
        /// 实测就是这样：暂停和继续都用不了。
        /// </summary>
        public bool UserPaused { get; set; }

        private string _state = "等待中";
        /// <summary>状态文案：等待中 / 下载中 / 已暂停 / 已完成 / 失败。</summary>
        public string State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                Raise(nameof(State));
                Raise(nameof(CanPause));
                Raise(nameof(CanResume));
                Raise(nameof(CanCancel));
                Raise(nameof(CanRemove));
                Raise(nameof(CanPlayPartial));
            }
        }

        private string _error = "";
        public string Error
        {
            get => _error;
            set { if (_error != value) { _error = value; Raise(nameof(Error)); Raise(nameof(HasError)); } }
        }

        public bool HasError => !string.IsNullOrEmpty(Error);

        /// <summary>是否处于可暂停状态（下载中/等待中）。</summary>
        public bool CanPause => _state == "下载中" || _state == "等待中";

        /// <summary>是否处于可继续状态（已暂停）。</summary>
        public bool CanResume => _state == "已暂停";

        /// <summary>
        /// 是否还能取消。
        /// <para>
        /// 已结束的任务（失败/已取消/已完成）取消按钮必须是灰的 ——
        /// 对已经不在跑的任务发取消信号没有任何效果，按钮却可点，
        /// 用户会以为「点了没反应 = 程序卡死」。这正是反馈里的现象。
        /// </para>
        /// </summary>
        public bool CanCancel =>
            _state == "下载中" || _state == "等待中" || _state == "已暂停"
            || _state == "获取直链" || _state == "需转存" || _state == "准备中"
            || _state == "转存中" || _state == "正在取消";

        /// <summary>
        /// 是否可以从列表移除。
        /// <para>
        /// 只有**已结束**的任务才能删：还在跑的任务如果直接从列表拿掉，
        /// 它就再也没法被取消/查看了（后台仍在写盘）。想中止请先「取消」。
        /// </para>
        /// </summary>
        public bool CanRemove =>
            _state == "失败" || _state == "已取消" || _state == "已完成"
            // 「需转存」「获取直链」这类中间态在取链阶段失败后会停住（它们不进入
            // 下载调度器，也就没有回调来推进状态）。这些也必须能删，
            // 否则列表里会永久残留几条动不了的记录。
            || _state == "需转存" || _state == "获取直链" || _state == "准备中"
            || _state == "转存中";

        /// <summary>
        /// 是否能「边下边播」：只有正在下载且已经写了数据才有意义。
        /// </summary>
        public bool CanPlayPartial => _state == "下载中" && Done > 0;

        public bool IsRunning => _state == "下载中";

        /// <summary>下载完成后在文件管理器里打开所在目录。</summary>
        public string Dest => Job.Dest;
    }
}
