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
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 账号管理窗口：列出本机保存的账号，支持切换/删除/新增。
    ///
    /// 与主窗口的交互方式：通过 <see cref="SelectedCookie"/> 把选中账号的 Cookie 交回去，
    /// 主窗口拿到后重建 QuarkClient —— 窗口本身不直接改主窗口状态，
    /// 避免两个窗口互相持有引用造成的生命周期问题。
    /// </summary>
    public partial class AccountWindow : Window
    {
        private readonly ObservableCollection<AccountProfile> _accounts =
            new ObservableCollection<AccountProfile>();

        private ListBox _list;
        private TextBlock _hint;

        /// <summary>用户选定要切换到的账号（含解密后的 Cookie）。取消则为 null。</summary>
        public AccountProfile SelectedAccount { get; private set; }

        /// <summary>
        /// 本次会话里**刚刚添加/登录成功**的账号。
        ///
        /// 为什么需要它：用户点「扫码添加账号」并登录成功后，直觉上就是"我登录了，
        /// 应该已经生效"。如果只把它存进档案列表、还要用户再手动点一次「切换」，
        /// 就会变成"我登录了但界面还是未登录"——这正是实测踩到的坑。
        /// 所以添加成功后自动把该账号设为当前账号。
        /// </summary>
        public AccountProfile JustAdded { get; private set; }

        /// <summary>账号列表是否发生了变化（增/删），主窗口据此决定是否刷新显示。</summary>
        public bool Changed { get; private set; }

        public AccountWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _list = this.FindControl<ListBox>("AccountList");
            _hint = this.FindControl<TextBlock>("Hint");
            _list.ItemsSource = _accounts;

            Reload();
        }

        private void Reload()
        {
            _accounts.Clear();
            foreach (var a in AccountStore.LoadAll())
                _accounts.Add(a);

            _hint.Text = _accounts.Count == 0
                ? "还没有保存的账号。扫码或用 Cookie 添加一个。"
                : string.Format("共 {0} 个账号", _accounts.Count);
        }

        // ---------- 切换 / 删除 ----------

        private void OnSwitchClick(object sender, RoutedEventArgs e)
        {
            var acc = (sender as Control)?.DataContext as AccountProfile;
            if (acc == null) return;

            if (string.IsNullOrEmpty(acc.Cookie))
            {
                _hint.Text = "这个账号的 Cookie 解不开（可能来自别的机器），请删掉重新添加。";
                return;
            }

            SelectedAccount = acc;
            Close("switch");
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var acc = (sender as Control)?.DataContext as AccountProfile;
            if (acc == null) return;

            if (AccountStore.Delete(acc.Key))
            {
                Changed = true;
                _hint.Text = "已删除：" + acc.DisplayName;
                Reload();
            }
            else
            {
                _hint.Text = "删除失败（文件可能已被占用）";
            }
        }

        // ---------- 新增 ----------

        private async void OnAddByQrClick(object sender, RoutedEventArgs e)
        {
            var qr = new QrLoginWindow();
            await qr.ShowDialog(this);

            if (string.IsNullOrEmpty(qr.ResultCookie))
                return;   // 用户取消或失败

            await SaveNewAccountAsync(qr.ResultCookie, qr.ResultNickname);
        }

        private async void OnAddByCookieClick(object sender, RoutedEventArgs e)
        {
            var dlg = new LoginWindow();
            string cookie = await dlg.ShowDialog<string>(this);
            if (string.IsNullOrEmpty(cookie))
                return;

            await SaveNewAccountAsync(cookie, null);
        }

        private async Task SaveNewAccountAsync(string cookie, string knownNick)
        {
            _hint.Text = "正在验证并保存…";

            string nick = knownNick;
            string key = "";

            try
            {
                using (var client = new QuarkClient(cookie))
                {
                    var (ok, n) = await client.ValidateAsync();
                    if (!string.IsNullOrEmpty(n)) nick = n;

                    // 优先用夸克返回的稳定 uid 作标识；取不到再退回昵称
                    key = !string.IsNullOrEmpty(client.AccountSig) ? client.AccountSig : nick;
                    if (string.IsNullOrEmpty(key)) key = Guid.NewGuid().ToString("N").Substring(0, 12);

                    AccountStore.Save(key, nick ?? "", cookie);
                    Changed = true;
                    Reload();

                    // 自动选中刚添加的账号（用户预期"登录完就该生效"）
                    JustAdded = _accounts.FirstOrDefault(a => a.Key == key);

                    _hint.Text = ok
                        ? "已登录并保存：" + (string.IsNullOrEmpty(nick) ? key : nick)
                        : "已保存，但登录态校验未通过（Cookie 可能已过期）";
                }
            }
            catch (Exception ex)
            {
                _hint.Text = "保存失败：" + ex.Message;
            }
        }
    }
}
