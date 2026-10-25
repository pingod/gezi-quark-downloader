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

using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GeZi.Core.Support;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 登录窗口：粘贴 Cookie。
    ///
    /// 为什么原生版先不做二维码扫码：
    /// 扫码登录要走 pan.quark.cn 的 TLS 指纹通道（见 OpenSslTls），登录成功后还要用
    /// 换到的 ticket 再换 Cookie —— 这条链路可以移植，但需要把
    /// LoginQrService + PanAccountBridge 整条搬过来并接上界面。
    /// 对"先把项目跑起来能用"这个目标，粘贴 Cookie 已经能覆盖全部下载场景，
    /// 且不依赖任何 Windows 特性，所以作为第一版实现。
    /// </summary>
    public partial class LoginWindow : Window
    {
        private TextBox _cookieBox;
        private TextBlock _hint;

        public LoginWindow()
        {
            AvaloniaXamlLoader.Load(this);
            _cookieBox = this.FindControl<TextBox>("CookieBox");
            _hint = this.FindControl<TextBlock>("Hint");
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            string raw = (_cookieBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(raw))
            {
                _hint.Text = "请先粘贴 Cookie";
                return;
            }

            // 容错：用户可能连 "Cookie: " 前缀一起复制了
            if (raw.StartsWith("Cookie:", System.StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("Cookie:".Length).Trim();

            // 必须含有夸克的关键 Cookie，否则一定是贴错了
            var parsed = QuarkSession.ParseCookies(raw);
            if (parsed.Count == 0)
            {
                _hint.Text = "格式不对：应该是一行 a=b; c=d 形式的 Cookie";
                return;
            }
            if (!parsed.Keys.Any(k => k.StartsWith("__pu") || k == "__pus" || k == "__puus"))
                _hint.Text = "提示：没找到 __puus 等登录 Cookie，可能不是已登录状态的 Cookie。仍可继续尝试。";

            Close(raw);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close(null);
    }
}
