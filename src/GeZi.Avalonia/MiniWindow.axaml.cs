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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 悬浮小窗：无边框、置顶、可拖动，显示总体下载进度。
    ///
    /// 拖动实现：Wayland 下**不能用** <c>Window.Position</c> 手动移动窗口
    /// （协议不允许客户端设置窗口位置）。所以走平台原生的"开始移动"请求
    /// —— <c>BeginMoveDrag</c>，由合成器接管拖动，Wayland/X11 都能工作。
    /// </summary>
    public partial class MiniWindow : Window
    {
        private Action _onShowMain;
        private ProgressBar _bar;
        private TextBlock _percentText;
        private TextBlock _detailText;
        private TextBlock _titleText;
        private Grid _dragArea;

        public MiniWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _bar = this.FindControl<ProgressBar>("Bar");
            _percentText = this.FindControl<TextBlock>("PercentText");
            _detailText = this.FindControl<TextBlock>("DetailText");
            _titleText = this.FindControl<TextBlock>("TitleText");
            _dragArea = this.FindControl<Grid>("DragArea");

            // 按住标题区拖动整个窗口
            _dragArea.PointerPressed += OnDragAreaPressed;
        }

        /// <summary>设置"显示主窗口"的回调。</summary>
        public void SetShowMainCallback(Action cb) => _onShowMain = cb;

        private void OnDragAreaPressed(object sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;

            try
            {
                // 平台原生拖动：Wayland 上这是唯一正确的做法
                BeginMoveDrag(e);
            }
            catch
            {
                // 个别后端不支持 → 忽略，窗口不移动而已
            }
        }

        /// <summary>更新进度显示。</summary>
        public void UpdateProgress(long done, long total, double speed, int runningCount)
        {
            double pct = total > 0 ? Math.Min(100.0, done * 100.0 / total) : 0;

            _bar.Value = pct;
            _percentText.Text = pct.ToString("F0") + "%";

            string speedText = speed > 0 ? FormatSize((long)speed) + "/s" : "";
            _detailText.Text = string.IsNullOrEmpty(speedText)
                ? string.Format("{0} 个任务", runningCount)
                : string.Format("{0} 个任务 · {1}", runningCount, speedText);

            _titleText.Text = runningCount > 0 ? "下载中" : "已完成";
        }

        private static string FormatSize(long n)
        {
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1000 && i < units.Length - 1) { v /= 1000.0; i++; }
            return i == 0 ? n + " B" : v.ToString("F1") + " " + units[i];
        }

        private void OnShowMainClick(object sender, RoutedEventArgs e) => _onShowMain?.Invoke();

        private void OnHideClick(object sender, RoutedEventArgs e) => Hide();
    }
}
