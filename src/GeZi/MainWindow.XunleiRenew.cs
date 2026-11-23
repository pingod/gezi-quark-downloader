// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分） ----
// 职责：迅雷云盘令牌的**自动续期** —— 四卡实测发现令牌失效就静默换一张新的。
//
// 与阿里的自动续期（MainWindow.AliyunRenew.cs）的区别与取舍：
//   阿里新版把 refresh_token 存在服务端 session，网页 localStorage 只留两小时寿命的
//   access_token，所以"拿 refresh_token 换新令牌"在阿里走不通 —— 只能重开内嵌登录。
//   迅雷正好相反：我们手里**有** refresh_token（账号登录套 XunleiRefreshToken、
//   网页登录套 XunleiWebRefreshToken），可以直接调 OAuth 换新，不需要开 WebView2。
//
// 三条纪律（踩坑总结，与阿里那套对齐）：
//   ① **绝不切换当前账号**：迅雷在后台悄悄换令牌，不该把当前账号从夸克/阿里切走 ——
//      只有当前账号本来就是迅雷时才走完整落地 ApplyProviderLoginAsync。
//   ② **refresh_token 单次有效、用一次就轮换**：无论成败服务端都可能作废旧值下发新值，
//      所以换完**立即落盘**（成功失败都要），否则旧值随窗口消失 → 下次启动必然失效。
//   ③ **失败必须冷却**：refresh_token 本身也可能过期，那样怎么换都拿不到新令牌。
//      不冷却就会变成"每次四卡实测失败都白耗一次 refresh_token"，把"还能救"的账号
//      硬耗成"必须重新登录"。冷却期跨重启持久化（对标 AliyunRenewFailedTicks）。

using System;
using System.Threading.Tasks;
using System.Windows;
using GeZi.Core.Api;
using GeZi.Providers.Xunlei;

namespace GeZi
{
    public partial class MainWindow
    {
        /// <summary>自动续期正在进行中（防重入）。</summary>
        private bool _xunleiAutoRenewing;

        /// <summary>连续失败后的冷却截止时间。</summary>
        private DateTime _xunleiAutoRenewBlockedUntil = DateTime.MinValue;

        /// <summary>上次尝试时间（防抖：四卡校验与解析失败可能同时触发）。</summary>
        private DateTime _xunleiAutoRenewLastTry = DateTime.MinValue;

        /// <summary>失败后的冷却时长。</summary>
        private static readonly TimeSpan XunleiAutoRenewCooldown = TimeSpan.FromMinutes(10);

        /// <summary>两次自动尝试之间的最小间隔。</summary>
        private static readonly TimeSpan XunleiAutoRenewDebounce = TimeSpan.FromSeconds(30);


        /// <summary>
        /// 察觉到迅雷令牌失效时调用：用 refresh_token 静默换一张新的并落盘。
        /// <para>
        /// 调用点见 <see cref="ValidatePanSessionsAsync"/>（四卡实测）与解析分享失败分支。
        /// 全程不需要用户点任何按钮。
        /// </para>
        /// </summary>
        /// <param name="reason">失效原因（写进日志，便于事后判断是真过期还是网络问题）。</param>
        private void TryAutoRenewXunleiCredential(string reason)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => TryAutoRenewXunleiCredential(reason)));
                return;
            }

            // 启动早期 _settings 可能还是 null（LoadSettingsToUi 之前）
            if (_settings == null)
                return;
            if (_xunleiAutoRenewing || _panWebLoginOpen)
                return;

            // 从没登录过就不该偷偷去登：首次登录必须用户主动（否则等于替用户登录）。
            if (string.IsNullOrWhiteSpace(_settings.XunleiRefreshToken)
                && string.IsNullOrWhiteSpace(_settings.XunleiWebRefreshToken))
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _xunleiAutoRenewBlockedUntil)
                return;
            // 🚨 冷却期跨重启持久化（对标阿里 AliyunRenewFailedTicks）：
            //   迅雷 refresh_token 单次有效，失败后必须冷却，否则每次启动都白耗一次。
            try
            {
                long failedTicks = _settings.XunleiRenewFailedTicks;
                if (failedTicks > 0)
                {
                    var failedAt = new DateTime(failedTicks, DateTimeKind.Utc);
                    if (now - failedAt < XunleiAutoRenewCooldown)
                        return;
                }
            }
            catch { }
            if (now - _xunleiAutoRenewLastTry < XunleiAutoRenewDebounce)
                return;

            _xunleiAutoRenewing = true;
            _xunleiAutoRenewLastTry = now;

            Log("迅雷云盘令牌已失效（"
                + (string.IsNullOrEmpty(reason) ? "校验未通过" : reason)
                + "），正在后台自动续期…");
            var ignored = RunXunleiAutoRenewAsync();
        }


        /// <summary>
        /// 真正干活的那一半：拿 refresh_token 换新 access_token，再真校验一次才落地。
        /// </summary>
        private async Task RunXunleiAutoRenewAsync()
        {
            bool renewed = false;
            string newToken = null;
            string newNick = "";

            try
            {
                // 两套 refresh_token 属于两套 OAuth 客户端，刷新方式不同，依次尝试：
                // 先账号登录那套（带 client_secret 的表单），再网页登录那套（JSON + 桌面 UA）。
                // 与 ApplyProviderLoginAsync 的续期段保持同一顺序与同一套字段读写。
                using (var refresher = new XunleiLoginService(_settings.XunleiDeviceId))
                {
                    XunleiLoginResult r = null;

                    if (!string.IsNullOrEmpty(_settings.XunleiRefreshToken))
                    {
                        r = await refresher.RefreshAsync(_settings.XunleiRefreshToken)
                            .ConfigureAwait(true);
                        if (r != null && r.Ok)
                        {
                            if (!string.IsNullOrEmpty(refresher.DeviceId))
                                _settings.XunleiDeviceId = refresher.DeviceId;
                            if (!string.IsNullOrEmpty(r.RefreshToken))
                                _settings.XunleiRefreshToken = r.RefreshToken;
                            newToken = r.AccessToken;
                        }
                        else
                        {
                            Log("账号登录刷新令牌续期失败：" + refresher.LastError);
                        }
                    }

                    if (newToken == null && !string.IsNullOrEmpty(_settings.XunleiWebRefreshToken))
                    {
                        r = await refresher.RefreshWebAsync(
                            _settings.XunleiWebRefreshToken, _settings.XunleiWebDeviceId)
                            .ConfigureAwait(true);
                        if (r != null && r.Ok)
                        {
                            if (!string.IsNullOrEmpty(r.RefreshToken))
                                _settings.XunleiWebRefreshToken = r.RefreshToken;
                            newToken = r.AccessToken;
                        }
                        else
                        {
                            Log("网页登录刷新令牌续期失败：" + refresher.LastError);
                        }
                    }

                    // 🚨 刷新令牌单次有效、用一次就轮换：无论成功失败都可能已作废旧值，
                    //    这里立刻落盘，否则下次启动拿到的还是已作废的旧 refresh_token。
                    try { SettingsStore.Save(_settings); } catch { }
                }

                if (newToken != null)
                {
                    _settings.XunleiPanToken = newToken;

                    // 设备标识必须跟着 token 一起用（迅雷把登录态和设备绑定，用错字段 = 400 captcha_required）。
                    string deviceId = !string.IsNullOrEmpty(_settings.XunleiWebDeviceId)
                        ? _settings.XunleiWebDeviceId
                        : _settings.XunleiDeviceId;
                    var probe = AttachXunleiCaptcha(
                        new XunleiClient(newToken, _settings.XunleiCaptchaToken, null, deviceId));

                    var again = await probe.ValidateAsync().ConfigureAwait(true);
                    bool valid = again.Item1;
                    newNick = again.Item2 ?? "";

                    if (valid)
                    {
                        renewed = true;
                        if (_activeKind == PanKind.Xunlei)
                        {
                            // 当前账号就是迅雷 → 走完整落地：重建 provider、刷新容量与列表。
                            // probe 只是用来验证新令牌，落地会另建一个 provider，这里弃掉。
                            try { probe.Dispose(); } catch { }
                            await ApplyProviderLoginAsync(PanKind.Xunlei, newToken, false)
                                .ConfigureAwait(true);
                        }
                        else
                        {
                            // 当前账号是别家 → 只静默重建缓存 provider，**绝不切换当前账号**。
                            PrewarmProvider(PanKind.Xunlei, probe);
                        }

                        _panLive[PanKind.Xunlei] = true;
                        _panNick[PanKind.Xunlei] = newNick;
                        // 续期成功 → 清掉跨重启冷却标记。
                        try { _settings.XunleiRenewFailedTicks = 0; SettingsStore.Save(_settings); } catch { }
                        Log("迅雷云盘令牌自动续期成功。");
                    }
                    else
                    {
                        try { probe.Dispose(); } catch { }
                        Log("迅雷云盘令牌续期后仍无效：" + probe.LastError);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("迅雷自动续期异常：" + ex.Message);
            }
            finally
            {
                _xunleiAutoRenewing = false;
            }

            if (!renewed)
            {
                _xunleiAutoRenewBlockedUntil = DateTime.UtcNow + XunleiAutoRenewCooldown;
                // 失败时刻落盘，重启后仍在冷却期内直接跳过，不再白耗 refresh_token。
                try { _settings.XunleiRenewFailedTicks = DateTime.UtcNow.Ticks; SettingsStore.Save(_settings); } catch { }
                _panLive[PanKind.Xunlei] = false;
                Log("迅雷云盘自动续期未成功，"
                    + (int)XunleiAutoRenewCooldown.TotalMinutes
                    + " 分钟内不再自动尝试；需要时在设置里重新登录迅雷。");
            }

            RefreshPanCards();
            RefreshPanStateList(force: true);
        }
    }
}
