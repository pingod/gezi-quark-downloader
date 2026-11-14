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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;
using GeZi.Core.Support;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 自检：不打开界面，把各功能链路跑一遍并打印结果。
    ///
    /// 存在的理由：图形界面"能启动"不等于"功能可用"——
    /// 窗口里某个 XAML 绑定写错、某个依赖库缺失，往往要点到那个按钮才报错。
    /// 有了自检，改完代码跑一次就知道有没有踩坏东西，不用靠手点。
    ///
    /// 用法：<c>GeZi.Avalonia --selftest</c>
    /// </summary>
    internal static class SelfTest
    {
        private static int _failed;
        private static int _passed;

        public static int Run()
        {
            Console.WriteLine("鸽子下载 · 原生版自检");
            Console.WriteLine(new string('-', 52));

            CheckEnvironment();
            CheckSettings();
            CheckAccounts();
            CheckQrBitmap();
            CheckPlayerLocator();
            CheckWindows();
            CheckOpenSsl();

            Console.WriteLine(new string('-', 52));
            Console.WriteLine(string.Format("通过 {0} 项，失败 {1} 项", _passed, _failed));
            Console.WriteLine(_failed == 0 ? "全部通过 ✅" : "有失败项 ❌");
            return _failed == 0 ? 0 : 1;
        }

        private static void CheckEnvironment()
        {
            Section("运行环境");

            Ok(".NET " + Environment.Version, true);

            string session = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            string display = Environment.GetEnvironmentVariable("DISPLAY");
            string backend = !string.IsNullOrEmpty(session) ? "Wayland (" + session + ")"
                : !string.IsNullOrEmpty(display) ? "X11 (" + display + ")"
                : "无图形会话";
            Ok("图形后端：" + backend, !string.IsNullOrEmpty(session) || !string.IsNullOrEmpty(display));

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Ok("家目录：" + home, !string.IsNullOrEmpty(home));
        }

        private static void CheckSettings()
        {
            Section("设置读写");

            var s = SettingsStore.Load();
            Ok("读取设置（下载目录：" + (string.IsNullOrEmpty(s.DownloadDir) ? "未设置" : s.DownloadDir) + "）", s != null);
            Ok("默认下载目录：" + SettingsStore.DefaultDownloadDir(), true);
            Ok("配置路径：" + SettingsStore.ConfigPath, true);

            // 往返测试：写一个标记值，读回来确认一致，再还原
            string originalTheme = s.Theme.ToString();
            bool origMini = s.ShowMiniWindow;
            try
            {
                s.Theme = s.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
                s.ShowMiniWindow = !s.ShowMiniWindow;
                SettingsStore.Save(s);

                var back = SettingsStore.Load();
                Ok("设置往返一致", back.Theme == s.Theme && back.ShowMiniWindow == s.ShowMiniWindow);
            }
            catch (Exception ex)
            {
                Ok("设置往返一致（异常：" + ex.Message + "）", false);
            }
            finally
            {
                // 还原，别把用户的真实设置改掉
                s.Theme = (AppTheme)Enum.Parse(typeof(AppTheme), originalTheme);
                s.ShowMiniWindow = origMini;
                SettingsStore.Save(s);
            }
        }

        private static void CheckAccounts()
        {
            Section("多账号");

            var list = AccountStore.LoadAll();
            Ok("读取账号列表（" + list.Count + " 个）", list != null);

            Ok("文件名安全化", AccountStore.SafeKey("a/b:c*d") == "abcd"
                && AccountStore.SafeKey("") == "account");

            // 加一个临时账号再删掉，不污染真实数据
            string key = "__selftest__" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                AccountStore.Save(key, "自检账号", "__puus=test-cookie");
                var after = AccountStore.LoadAll();
                var mine = after.FirstOrDefault(a => a.Key == key);
                Ok("保存账号并读回", mine != null && mine.Nick == "自检账号");
                Ok("Cookie 加解密往返", mine != null && mine.Cookie == "__puus=test-cookie");

                bool deleted = AccountStore.Delete(key);
                Ok("删除账号", deleted && AccountStore.LoadAll().All(a => a.Key != key));
            }
            catch (Exception ex)
            {
                Ok("账号读写（异常：" + ex.Message + "）", false);
            }
        }

        private static void CheckQrBitmap()
        {
            Section("二维码渲染");

            try
            {
                var bmp = QrBitmap.Render("https://su.quark.cn/4_eMHBJ?token=selftest", 8, 4);
                Ok("渲染位图 " + bmp.PixelSize.Width + "×" + bmp.PixelSize.Height,
                    bmp.PixelSize.Width > 100);
            }
            catch (Exception ex)
            {
                Ok("渲染位图（异常：" + ex.Message + "）", false);
            }

            // 编码器本身
            try
            {
                var m = QrEncoder.Encode("hello");
                Ok("编码矩阵 " + m.GetLength(0) + "×" + m.GetLength(0), m.GetLength(0) >= 21);
            }
            catch (Exception ex)
            {
                Ok("编码矩阵（异常：" + ex.Message + "）", false);
            }
        }

        private static void CheckPlayerLocator()
        {
            Section("播放器探测（边下边播）");

            var p = PlayerLocator.Find();
            if (p != null)
                Ok("找到播放器：" + p.Name + "（" + p.ExePath + "）", true);
            else
                Ok("未找到 VLC/mpv（边下边播会退化为系统默认程序，不影响下载）", true);
        }

        private static void CheckWindows()
        {
            Section("窗口构造（XAML 加载）");

            // 只构造不显示：能验证 XAML 全部解析成功、控件都能找到
            TryWindow("主窗口", () => new MainWindow());
            TryWindow("扫码登录", () => new QrLoginWindow());
            TryWindow("Cookie 登录", () => new LoginWindow());
            TryWindow("账号管理", () => new AccountWindow());
            TryWindow("设置", () => new SettingsWindow(new AppSettings()));
            TryWindow("关闭确认", () => new CloseConfirmWindow());
            TryWindow("悬浮小窗", () => new MiniWindow());
            TryWindow("平台选择器", () => new PlatformPickerWindow());
            TryWindow("文件操作", () => new FileOpWindow());
        }

        /// <summary>
        /// 构造一个窗口，确认它的 XAML 真的被解析了。
        ///
        /// ⚠️ 不能用 <c>GetVisualDescendants()</c> 判断：窗口**未 Show 之前**
        /// 可视化树还没建立，任何窗口都会返回 0 个子元素 —— 那会让你误判成"全部失败"。
        /// （第一版自检就踩了这个坑。）
        /// 改为检查 <c>Content</c> 是否非空：XAML 解析成功后根元素必然被赋值，
        /// 解析失败则会抛异常或留下 null。
        /// </summary>
        private static void TryWindow(string name, Func<Window> factory)
        {
            try
            {
                var w = factory();

                // XAML 解析成功 → Content 一定是一个具体的根控件（Grid/Border/…）
                var root = w.Content as Control;
                Ok(name + "（根控件：" + (root?.GetType().Name ?? "null") + "）", root != null);
                try { w.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Ok(name + "：加载失败 " + ex.GetType().Name + " " + ex.Message, false);
            }
        }

        private static void CheckOpenSsl()
        {
            Section("OpenSSL 通道（扫码登录前提）");

            Ok("库可加载：" + OpenSslTls.IsAvailable, OpenSslTls.IsAvailable);
            Ok("版本：" + OpenSslTls.VersionString, !string.IsNullOrEmpty(OpenSslTls.VersionString));

            if (!OpenSslTls.IsAvailable)
            {
                Ok("探测失败原因：" + OpenSslTls.ProbeError, false);
                return;
            }

            try
            {
                var r = OpenSslTls.Get("pan.quark.cn", "/account/info?fr=pc",
                    new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36" },
                        { "Accept", "application/json" },
                    }, 15000);
                Ok("pan.quark.cn 连通（HTTP " + r.StatusCode + "）", r.StatusCode > 0);
            }
            catch (Exception ex)
            {
                Ok("pan.quark.cn 连通（失败：" + ex.Message + "）", false);
            }
        }

        // ---------- 输出 ----------

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("▌ " + title);
        }

        private static void Ok(string text, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine("  ✓ " + text); }
            else { _failed++; Console.WriteLine("  ✗ " + text); }
        }
    }
}
