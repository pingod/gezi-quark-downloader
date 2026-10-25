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
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Wayland;

namespace GeZi.Avalonia
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // 自检模式：不开界面，把各功能链路跑一遍。
            // 必须先初始化 Avalonia（构造窗口需要平台服务），但不需要显示。
            if (args != null && args.Contains("--selftest"))
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                return SelfTest.Run();
            }

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return 0;
            }
            catch (Exception ex)
            {
                // 图形栈起不来时给一条能看懂的提示，而不是一堆堆栈
                Console.Error.WriteLine("启动失败: " + ex.Message);
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        /// <summary>
        /// Avalonia 的启动配置。AppBuilder 的名字/约定是这个方法名，别改名。
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
        {
            var builder = AppBuilder.Configure<App>();

            // Linux：**优先走原生 Wayland**，没有 Wayland 再退回 X11。
            //
            // 为什么不用 UsePlatformDetect()：它会先看 $DISPLAY 是否存在，存在就选 X11，
            // 完全不看 Wayland。而 Wayland 会话里 $DISPLAY 常常仍然指向一个**并不存在**的
            // Xwayland（本机就是：DISPLAY=:0，但 /tmp/.X11-unix 是空的）→ 结果
            // "XOpenDisplay failed" 直接起不来，而系统明明有可用的 wayland-1 socket。
            // UseWaylandWithFallback 的判定顺序是对的：先试 Wayland，失败才退 X11。
            // 注意顺序：必须**先**把回退后端（X11）配上，再声明 Wayland 优先。
            // 反过来会抛 "A fallback windowing backend must be configured before ..."。
            if (OperatingSystem.IsLinux())
                builder = builder.UsePlatformDetect().UseWaylandWithFallback();
            else
                builder = builder.UsePlatformDetect();

            return builder.LogToTrace();
        }
    }
}
