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
using System.Diagnostics;
using System.IO;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 本机视频播放器探测与启动（边下边播用）。
    ///
    /// 为什么必须探测：边下边播要**外部播放器**打开一个还没下完的文件。
    /// 能不能播起来取决于播放器的文件共享模式 —— VLC 用 FileShare.ReadWrite
    /// 打开文件，和我们正在写盘互不干扰；有些播放器只要只读共享，
    /// 会让我们后续写盘直接失败。所以只推荐**确认兼容**的播放器。
    ///
    /// 与 WPF 版的差异：WPF 版只认 VLC，且探测顺序是
    /// "注册表 App Paths → 安装目录 → PATH"。Linux 没有注册表，
    /// 改为 "PATH → 常见安装路径"，并把探测范围扩到 mpv
    /// （mpv 同样以读写共享方式打开文件，且是 Linux 上最常见的选择）。
    /// </summary>
    public static class PlayerLocator
    {
        public sealed class Player
        {
            public string Name;
            public string ExePath;
        }

        /// <summary>候选播放器，按优先级排列。</summary>
        private static readonly (string name, string[] names, string[] paths)[] Candidates =
        {
            ("VLC", new[] { "vlc" },
                new[] { "/usr/bin/vlc", "/usr/local/bin/vlc", "/snap/bin/vlc", "/var/lib/flatpak/exports/bin/org.videolan.VLC" }),
            ("mpv", new[] { "mpv" },
                new[] { "/usr/bin/mpv", "/usr/local/bin/mpv", "/var/lib/flatpak/exports/bin/io.mpv.Mpv" }),
            ("Haruna", new[] { "haruna" }, new[] { "/usr/bin/haruna" }),
            ("Celluloid", new[] { "celluloid" }, new[] { "/usr/bin/celluloid" }),
        };

        /// <summary>探测可用的播放器；一个都没有返回 null。</summary>
        public static Player Find()
        {
            foreach (var c in Candidates)
            {
                // ① PATH
                foreach (var exe in c.names)
                {
                    string p = Which(exe);
                    if (p != null)
                        return new Player { Name = c.name, ExePath = p };
                }

                // ② 常见安装路径
                foreach (var p in c.paths)
                {
                    if (File.Exists(p))
                        return new Player { Name = c.name, ExePath = p };
                }
            }
            return null;
        }

        /// <summary>
        /// 用播放器打开文件（**只读**方式，不预读整个文件，
        /// 让播放器自己边读边播已下载的部分）。
        /// </summary>
        public static bool Play(string playerPath, string name, string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = playerPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };

                // VLC 需要 --started-from-file 才能按"打开媒体"的正常行为工作；
                // mpv 直接传路径即可。
                if (name != null && name.Contains("VLC", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add("--started-from-file");

                psi.ArgumentList.Add(filePath);

                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>用系统默认程序打开（xdg-open），播放器都不在时的兜底。</summary>
        public static bool OpenWithSystem(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", filePath) { UseShellExecute = false });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>在 PATH 里找可执行文件（不依赖 shell 的 which）。</summary>
        private static string Which(string exe)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string full = Path.Combine(dir, exe);
                    if (File.Exists(full))
                        return full;
                }
                catch
                {
                    // 路径非法（含非法字符等）→ 跳过
                }
            }
            return null;
        }
    }
}
