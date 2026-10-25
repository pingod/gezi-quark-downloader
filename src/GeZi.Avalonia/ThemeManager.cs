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
using Avalonia.Styling;

namespace GeZi.Avalonia
{
    /// <summary>主题偏好（与 WPF 版 <c>AppTheme</c> 语义一致）。</summary>
    public enum AppTheme
    {
        /// <summary>跟随系统。</summary>
        System = 0,
        /// <summary>浅色。</summary>
        Light = 1,
        /// <summary>深色。</summary>
        Dark = 2,
    }

    /// <summary>
    /// 主题应用。
    ///
    /// 与 WPF 版的关键差异：WPF 版要手动维护两套 Colors.*.xaml 资源字典并在切换时替换，
    /// 而 Avalonia 的 FluentTheme 自带 ThemeVariant 机制 —— 只要设
    /// <c>Application.RequestedThemeVariant</c>，系统色板会自动跟着切。
    ///
    /// ⚠️ 但窗口里那些**硬编码**的 `Background="#1B1D21"` 不会跟着变。
    ///    这是原生版目前的取舍：主界面配色写死在 XAML 里（深色为主）。
    ///    要做到完全跟随，需要把颜色抽成资源字典 —— 已在下面的
    ///    <see cref="WindowBackground"/> 里留好了统一的色值入口，
    ///    后续要补浅色主题只需替换这一处 + XAML 里的字面量。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>窗口底色（与 XAML 里写死的值保持一致）。</summary>
        public static string WindowBackground(AppTheme theme)
            => theme == AppTheme.Light ? "#F5F6F8" : "#1B1D21";

        /// <summary>主文字色。</summary>
        public static string Foreground(AppTheme theme)
            => theme == AppTheme.Light ? "#1F2328" : "#E8EAED";

        /// <summary>次要文字色。</summary>
        public static string DimForeground(AppTheme theme)
            => theme == AppTheme.Light ? "#6A7280" : "#98A0AA";

        /// <summary>卡片/输入框底色。</summary>
        public static string Surface(AppTheme theme)
            => theme == AppTheme.Light ? "#FFFFFF" : "#22262B";

        /// <summary>控件底色。</summary>
        public static string Control(AppTheme theme)
            => theme == AppTheme.Light ? "#ECEEF1" : "#2A2E35";

        /// <summary>
        /// 把主题作用到应用上。
        /// </summary>
        public static void Apply(AppTheme theme)
        {
            if (Application.Current == null)
                return;

            Application.Current.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,      // Default = 跟随系统
            };
        }
    }
}
