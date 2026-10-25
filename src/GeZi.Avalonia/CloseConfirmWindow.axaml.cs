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

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GeZi.Avalonia
{
    /// <summary>关闭主窗口时的确认框。返回用户的选择，取消时返回 null。</summary>
    public partial class CloseConfirmWindow : Window
    {
        public enum Action
        {
            HideToTray,
            Exit,
        }

        public struct Choice
        {
            public Action Action;
        }

        public CloseConfirmWindow()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnHideClick(object sender, RoutedEventArgs e)
            => Close(new Choice { Action = Action.HideToTray });

        private void OnExitClick(object sender, RoutedEventArgs e)
            => Close(new Choice { Action = Action.Exit });

        private void OnCancelClick(object sender, RoutedEventArgs e)
            => Close(null);
    }
}
