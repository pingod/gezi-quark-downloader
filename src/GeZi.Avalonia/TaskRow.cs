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

        /// <summary>大小的人类可读形式。</summary>
        public string SizeText => Entry.IsDir ? "" : FormatSize(Size);

        public string Kind => Entry.IsDir ? "文件夹" : "文件";

        /// <summary>列表里显示的名字。文件夹加个视觉前缀，让人知道能进。</summary>
        public string DisplayName => Entry.IsDir ? "📁 " + Entry.Name : Entry.Name;

        /// <summary>只有文件能勾选下载；文件夹靠双击进入。</summary>
        public bool CanSelect => !Entry.IsDir;

        /// <summary>悬停提示。</summary>
        public string Hint => Entry.IsDir
            ? "双击进入文件夹"
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
            set { if (_state != value) { _state = value; Raise(nameof(State)); Raise(nameof(CanPause)); Raise(nameof(CanResume)); } }
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

        public bool IsRunning => _state == "下载中";

        /// <summary>下载完成后在文件管理器里打开所在目录。</summary>
        public string Dest => Job.Dest;
    }
}
