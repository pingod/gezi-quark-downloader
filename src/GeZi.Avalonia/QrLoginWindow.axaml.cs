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
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GeZi.Core.Api;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 扫码登录窗口。
    ///
    /// 流程（与 WPF 版一致，走 GeZi.Core 的 LoginQrService）：
    ///   ① 取二维码 token → 本地把 token 渲染成二维码显示
    ///   ② 每 1.2 秒轮询一次，直到拿到 service_ticket
    ///   ③ 用 ticket 换 Cookie（这一段必须过 pan.quark.cn 的 TLS 指纹墙，
    ///      由 PanAccountBridge → OpenSslTls → 系统 OpenSSL 3.6 完成）
    ///
    /// 关闭窗口 = 取消（CancellationTokenSource 一并 cancel，轮询立刻停）。
    /// </summary>
    public partial class QrLoginWindow : Window
    {
        private Image _qrImage;
        private TextBlock _qrPlaceholder;
        private TextBlock _stateText;
        private TextBlock _hintText;
        private Button _refreshBtn;

        private LoginQrService _service;
        private CancellationTokenSource _cts;

        /// <summary>登录成功后的完整 Cookie；失败或取消时为 null。</summary>
        public string ResultCookie { get; private set; }

        /// <summary>登录成功后的昵称（可能为空）。</summary>
        public string ResultNickname { get; private set; }

        public QrLoginWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _qrImage = this.FindControl<Image>("QrImage");
            _qrPlaceholder = this.FindControl<TextBlock>("QrPlaceholder");
            _stateText = this.FindControl<TextBlock>("StateText");
            _hintText = this.FindControl<TextBlock>("HintText");
            _refreshBtn = this.FindControl<Button>("RefreshBtn");

            Opened += (s, e) => _ = StartAsync();
            Closed += (s, e) => Stop();
        }

        private void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _service?.Dispose(); } catch { }
            _cts = null;
            _service = null;
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = StartAsync();

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

        /// <summary>改用 Cookie 登录：关掉本窗口，由主窗口弹 Cookie 对话框。</summary>
        private void OnUseCookieClick(object sender, RoutedEventArgs e)
        {
            ResultCookie = null;
            Close("cookie");
        }

        private async Task StartAsync()
        {
            Stop();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _service = new LoginQrService();

            _refreshBtn.IsEnabled = false;
            _qrPlaceholder.IsVisible = true;
            _qrPlaceholder.Text = "正在获取二维码…";
            _qrImage.Source = null;
            _stateText.Text = "正在获取二维码…";
            _hintText.Text = "";

            try
            {
                var (ok, token, qrUrl, error) = await _service.GetQrCodeAsync();
                if (ct.IsCancellationRequested) return;

                if (!ok || string.IsNullOrEmpty(token))
                {
                    _stateText.Text = "获取二维码失败";
                    _qrPlaceholder.Text = error ?? "未知错误";
                    _hintText.Text = "可以点「刷新二维码」重试，或改用 Cookie 登录。";
                    _refreshBtn.IsEnabled = true;
                    return;
                }

                // 本地渲染二维码（内容就是扫码 URL）
                try
                {
                    _qrImage.Source = QrBitmap.Render(qrUrl, modulePixels: 8, quietZone: 4);
                    _qrPlaceholder.IsVisible = false;
                }
                catch (Exception ex)
                {
                    _qrPlaceholder.Text = "二维码渲染失败：" + ex.Message;
                    _stateText.Text = "渲染失败";
                    _refreshBtn.IsEnabled = true;
                    return;
                }

                _stateText.Text = "等待扫码…";
                _hintText.Text = "打开手机夸克 App → 右上角扫一扫 → 确认登录";

                // 轮询直到拿到 ticket
                var progress = new Progress<QrPollResult>(r =>
                {
                    switch (r.Stage)
                    {
                        case QrLoginStage.WaitingScan:
                            _stateText.Text = "等待扫码…";
                            break;
                        case QrLoginStage.ScannedWaitConfirm:
                            _stateText.Text = "已扫码，请在手机上确认登录";
                            break;
                        case QrLoginStage.Expired:
                            _stateText.Text = "二维码已失效";
                            _hintText.Text = "点「刷新二维码」重新获取。";
                            break;
                    }
                });

                var poll = await _service.PollForTicketWithStatusAsync(token, 300, progress, ct);
                if (ct.IsCancellationRequested) return;

                if (poll.Stage != QrLoginStage.GotTicket || string.IsNullOrEmpty(poll.Ticket))
                {
                    if (poll.Stage == QrLoginStage.Expired)
                    {
                        _qrPlaceholder.IsVisible = true;
                        _qrPlaceholder.Text = "二维码已失效，请刷新";
                        _stateText.Text = "已失效";
                        _hintText.Text = string.IsNullOrEmpty(poll.Message) ? "点「刷新二维码」重试。" : poll.Message;
                    }
                    _refreshBtn.IsEnabled = true;
                    return;
                }

                // 拿到 ticket → 换 Cookie
                _stateText.Text = "登录中…";
                _hintText.Text = "正在换取登录态";

                var (exOk, cookie, exErr) = await _service.ExchangeTicketAsync(poll.Ticket);
                if (ct.IsCancellationRequested) return;

                if (!exOk || string.IsNullOrEmpty(cookie))
                {
                    _stateText.Text = "换取登录态失败";
                    _hintText.Text = string.IsNullOrEmpty(exErr)
                        ? "可尝试改用 Cookie 登录。"
                        : exErr + "\n（可点「改用 Cookie 登录」手动粘贴）";
                    _refreshBtn.IsEnabled = true;
                    return;
                }

                ResultCookie = cookie;

                // 顺便取昵称（失败不影响登录）
                try
                {
                    var info = PanAccountBridge.FetchAccountInfo(cookie);
                    if (info.HasValue && info.Value.Ok)
                        ResultNickname = info.Value.Nickname;
                }
                catch { }

                _stateText.Text = "登录成功";
                _hintText.Text = string.IsNullOrEmpty(ResultNickname)
                    ? "正在关闭窗口…" : "欢迎，" + ResultNickname;
                await Task.Delay(400);
                Close("ok");
            }
            catch (OperationCanceledException)
            {
                // 用户关闭窗口，静默退出
            }
            catch (Exception ex)
            {
                _stateText.Text = "出错了";
                _qrPlaceholder.Text = ex.Message;
                _hintText.Text = "可以点「刷新二维码」重试，或改用 Cookie 登录。";
                _refreshBtn.IsEnabled = true;
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    _refreshBtn.IsEnabled = true;
            }
        }
    }
}
