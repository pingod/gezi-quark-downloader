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
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 设置窗口。直接持有 <see cref="AppSettings"/> 的引用并就地修改，
    /// 由调用方（主窗口）在 <see cref="Changed"/> 为 true 时保存 ——
    /// 这样"取消"只需丢弃一个标志，不用做深拷贝回滚。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;

        private TextBlock _dirText;
        private NumericUpDown _threadsBox;
        private NumericUpDown _concurrentBox;
        private ComboBox _themeBox;
        private ComboBox _closeBox;
        private CheckBox _miniBox;
        private TextBlock _playerText;
        private TextBlock _playerHint;

        /// <summary>用户点了保存 → true。</summary>
        public bool Changed { get; private set; }

        public SettingsWindow() : this(new AppSettings()) { }

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings;

            AvaloniaXamlLoader.Load(this);

            _dirText = this.FindControl<TextBlock>("DirText");
            _threadsBox = this.FindControl<NumericUpDown>("ThreadsBox");
            _concurrentBox = this.FindControl<NumericUpDown>("ConcurrentBox");
            _themeBox = this.FindControl<ComboBox>("ThemeBox");
            _closeBox = this.FindControl<ComboBox>("CloseBox");
            _miniBox = this.FindControl<CheckBox>("MiniBox");
            _playerText = this.FindControl<TextBlock>("PlayerText");
            _playerHint = this.FindControl<TextBlock>("PlayerHint");

            Load();
        }

        private void Load()
        {
            _dirText.Text = ShortenPath(_settings.DownloadDir);
            _threadsBox.Value = _settings.Threads;
            _concurrentBox.Value = _settings.ConcurrentTasks;
            _themeBox.SelectedIndex = (int)_settings.Theme;
            _closeBox.SelectedIndex = (int)_settings.OnCloseButton;
            _miniBox.IsChecked = _settings.ShowMiniWindow;

            UpdatePlayerDisplay();
        }

        private void UpdatePlayerDisplay()
        {
            if (!string.IsNullOrEmpty(_settings.PlayerPath) && File.Exists(_settings.PlayerPath))
            {
                _playerText.Text = _settings.PlayerPath;
                _playerHint.Text = "边下边播将使用这个播放器。";
                return;
            }

            var p = PlayerLocator.Find();
            if (p != null)
            {
                _playerText.Text = "自动： " + p.Name + "（" + p.ExePath + "）";
                _playerHint.Text = "已探测到可用播放器。边下边播会用它打开未下完的文件。";
            }
            else
            {
                _playerText.Text = "未找到播放器";
                _playerHint.Text = "没有探测到 VLC / mpv。边下边播会退化为「系统默认程序」。"
                    + "建议安装：sudo pacman -S vlc 或 mpv";
            }
        }

        private async void OnChooseDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择下载保存目录",
                    AllowMultiple = false,
                });
                var picked = folders?.FirstOrDefault();
                if (picked != null)
                {
                    string path = picked.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        _settings.DownloadDir = path;
                        _dirText.Text = ShortenPath(path);
                    }
                }
            }
            catch { }
        }

        private void OnDetectPlayerClick(object sender, RoutedEventArgs e)
        {
            _settings.PlayerPath = "";     // 清空指定 → 回到自动探测
            UpdatePlayerDisplay();
        }

        private async void OnChoosePlayerClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择播放器可执行文件",
                    AllowMultiple = false,
                });
                var picked = files?.FirstOrDefault();
                if (picked != null)
                {
                    string path = picked.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        _settings.PlayerPath = path;
                        UpdatePlayerDisplay();
                    }
                }
            }
            catch { }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            _settings.Threads = (int)(_threadsBox.Value ?? 256);
            _settings.ConcurrentTasks = (int)(_concurrentBox.Value ?? 3);
            _settings.Theme = (AppTheme)Math.Max(0, _themeBox.SelectedIndex);
            _settings.OnCloseButton = (CloseBehavior)Math.Max(0, _closeBox.SelectedIndex);
            _settings.ShowMiniWindow = _miniBox.IsChecked == true;

            Changed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

        private static string ShortenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal))
                return "~" + path.Substring(home.Length);
            return path;
        }
    }
}
