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
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 托盘图标：关闭主窗口后程序继续在托盘里跑，下载不中断。
    ///
    /// Linux 注意：托盘依赖桌面环境的 StatusNotifierItem / AppIndicator 支持。
    /// Hyprland + waybar、KDE 一般没问题；纯 GNOME 需要装 AppIndicator 扩展。
    /// 托盘创建失败**不能让程序起不来** —— 所有异常都吞掉，退化成"关窗即退出"。
    /// </summary>
    public sealed class TrayHost : IDisposable
    {
        private TrayIcon _tray;
        private readonly Window _window;
        private readonly Action _onShow;
        private readonly Action _onExit;

        public TrayHost(Window window, Action onShow, Action onExit)
        {
            _window = window;
            _onShow = onShow;
            _onExit = onExit;

            try
            {
                _tray = new TrayIcon
                {
                    Icon = LoadIcon(),
                    ToolTipText = "鸽子下载",
                    IsVisible = true,
                };

                var menu = new NativeMenu();

                var showItem = new NativeMenuItem("显示主窗口");
                showItem.Click += (s, e) => _onShow?.Invoke();
                menu.Add(showItem);

                menu.Add(new NativeMenuItemSeparator());

                var exitItem = new NativeMenuItem("退出");
                exitItem.Click += (s, e) => _onExit?.Invoke();
                menu.Add(exitItem);

                _tray.Menu = menu;

                // 左键点击托盘 = 显示主窗口（部分桌面环境只支持菜单，不支持这个回调）
                _tray.Clicked += (s, e) => _onShow?.Invoke();
            }
            catch
            {
                // 托盘不可用（无 StatusNotifier 支持）→ 静默降级
                _tray = null;
            }
        }

        /// <summary>托盘是否真的建起来了。</summary>
        public bool Available => _tray != null;

        /// <summary>更新托盘提示（比如显示"3 个任务进行中"）。</summary>
        public void SetTooltip(string text)
        {
            if (_tray == null) return;
            try { _tray.ToolTipText = text; } catch { }
        }

        private static WindowIcon LoadIcon()
        {
            try
            {
                // 用 avares:// 从内嵌资源取，不依赖运行目录
                var uri = new Uri("avares://GeZi.Avalonia/Assets/app-logo.png");
                using (var stream = AssetLoader.Open(uri))
                    return new WindowIcon(stream);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_tray != null)
                {
                    _tray.IsVisible = false;
                    _tray.Dispose();
                }
            }
            catch { }
            _tray = null;
        }
    }
}
