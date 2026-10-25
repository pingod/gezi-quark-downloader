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
using System.IO;
using System.Text.Json;

namespace GeZi.Avalonia
{
    /// <summary>关闭主窗口时的行为。</summary>
    public enum CloseBehavior
    {
        /// <summary>第一次关窗问一下（默认）。</summary>
        Ask = 0,
        /// <summary>最小化到托盘，下载继续。</summary>
        MinimizeToTray = 1,
        /// <summary>直接退出。</summary>
        Exit = 2,
    }

    /// <summary>
    /// 原生版的轻量设置（JSON 存在 XDG 配置目录）。
    ///
    /// 刻意**不复用** WPF 版的 <c>GeZi.Settings</c>：那套是 XML 序列化 + Windows 注册表主题检测 +
    /// MiniWindow 坐标等大量 Windows 专属状态，搬过来只会带进一堆无意义的字段。
    /// 这里只保留原生版真正需要的几项，字段名与 WPF 版保持一致，方便将来双向迁移。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>下载保存目录（空 = ~/Downloads）。</summary>
        public string DownloadDir { get; set; } = "";

        /// <summary>每个文件的最大并发连接数（与 WPF 版默认 256 一致）。</summary>
        public int Threads { get; set; } = 256;

        /// <summary>同时下载的任务数。</summary>
        public int ConcurrentTasks { get; set; } = 3;

        /// <summary>登录 Cookie（已加密存储，见 GeZi.Core.Support.SecretProtector）。</summary>
        public string Cookie { get; set; } = "";

        /// <summary>账号昵称（登录后显示，仅用于界面提示）。</summary>
        public string Who { get; set; } = "";

        /// <summary>上次使用的窗口尺寸。</summary>
        public double WindowWidth { get; set; } = 1080;
        public double WindowHeight { get; set; } = 720;

        /// <summary>主题偏好。</summary>
        public AppTheme Theme { get; set; } = AppTheme.Dark;

        /// <summary>关闭主窗口时的行为（与 WPF 版 CloseAction 对应）。</summary>
        public CloseBehavior OnCloseButton { get; set; } = CloseBehavior.Ask;

        /// <summary>是否启用悬浮小窗。</summary>
        public bool ShowMiniWindow { get; set; }

        /// <summary>悬浮小窗位置（Wayland 下合成器可能不认，仅尽力恢复）。</summary>
        public double MiniLeft { get; set; }
        public double MiniTop { get; set; }

        /// <summary>边下边播用的播放器路径（空 = 自动探测）。</summary>
        public string PlayerPath { get; set; } = "";

        /// <summary>当前账号的标识（对应 accounts/ 下的文件名），用于在账号列表里标出"当前"。</summary>
        public string CurrentAccountKey { get; set; } = "";
    }

    /// <summary>设置的读写（原子写入，避免断电写坏配置）。</summary>
    public static class SettingsStore
    {
        private static readonly string ConfigDir = ConfigRoot();

        private static string ConfigRoot()
        {
            // ① GEZI_CONFIG_DIR：启动脚本在「标准位置不可写」时用它兜底
            //    （沙箱 / 只读家目录 / 容器里很常见），优先级最高。
            string explicitDir = Environment.GetEnvironmentVariable("GEZI_CONFIG_DIR");
            if (!string.IsNullOrEmpty(explicitDir))
                return explicitDir;

            // ② 尊重 XDG
            string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "GeZi");

            // ③ 兜底 ~/.config
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "GeZi");
        }

        public static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

        /// <summary>读取设置；文件不存在或损坏时返回一份默认值（绝不因此启动失败）。</summary>
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new AppSettings();

                string json = File.ReadAllText(ConfigPath);
                if (string.IsNullOrWhiteSpace(json))
                    return new AppSettings();

                var s = JsonSerializer.Deserialize<AppSettings>(json);
                return s ?? new AppSettings();
            }
            catch
            {
                // 配置损坏 → 用默认值继续，用户还能正常下载（只是要重新设一遍）
                return new AppSettings();
            }
        }

        /// <summary>保存设置（先写临时文件再原子替换）。</summary>
        public static void Save(AppSettings s)
        {
            if (s == null) return;
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(s,
                    new JsonSerializerOptions { WriteIndented = true });

                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, ConfigPath, overwrite: true);
            }
            catch
            {
                // 存不下就算了：设置丢失不影响下载本身
            }
        }

        /// <summary>
        /// 默认下载目录：
        /// ① GEZI_DOWNLOAD_DIR（启动脚本按「是否可写」挑好的）
        /// ② ~/Downloads（存在且可写）
        /// ③ 家目录
        /// </summary>
        public static string DefaultDownloadDir()
        {
            string explicitDir = Environment.GetEnvironmentVariable("GEZI_DOWNLOAD_DIR");
            if (!string.IsNullOrEmpty(explicitDir))
                return explicitDir;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dl = Path.Combine(home, "Downloads");
            if (Directory.Exists(dl) && IsWritable(dl))
                return dl;

            // ⚠️ 不要退到 home 根目录：那会把下载文件直接倒在家目录下，
            //    看起来像程序乱丢垃圾。优先试 ~/Downloads 的创建，再退到
            //    可执行文件同级的 Downloads（发布目录内，必然可写）。
            try
            {
                if (IsWritable(dl)) return dl;
            }
            catch { }

            string local = Path.Combine(AppContext.BaseDirectory, "Downloads");
            if (IsWritable(local)) return local;

            // 最后才退到家目录（至少比抛异常好）
            return home;
        }

        /// <summary>目录是否真的可写（用临时文件实测，比只看权限位可靠）。</summary>
        public static bool IsWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".gezi-wtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
