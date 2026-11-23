// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Models;
using GeZi.Core.Api;
using GeZi.Core.Support;

namespace GeZi.Providers.Quark
{
    /// <summary>
    /// 把已验证的 <see cref="QuarkClient"/> 适配成统一的 <see cref="IPanProvider"/>。
    /// <para>
    /// <b>为什么用适配器而不是重写：</b>夸克这条链路（TLS 桥接、取链三入口择优、
    /// 哈希校验、限流降档）是项目里被真实用户长期验证过的核心资产。
    /// 为了"接口统一"去重写它，等于把最稳的部分换成最生的部分 —— 不划算。
    /// 适配器只做字段翻译，一行网络逻辑都不碰。
    /// </para>
    /// </summary>
    public sealed class QuarkProviderAdapter : IPanProvider
    {
        private readonly QuarkClient _client;
        private readonly bool _ownsClient;

        public QuarkProviderAdapter(QuarkClient client, bool ownsClient = false)
        {
            if (client == null)
                throw new ArgumentNullException("client");
            _client = client;
            _ownsClient = ownsClient;
        }

        public QuarkProviderAdapter(string cookie)
            : this(new QuarkClient(cookie), ownsClient: true)
        {
        }

        /// <summary>底层夸克客户端（UI 需要夸克特有功能时可直接用）。</summary>
        public QuarkClient Inner
        {
            get { return _client; }
        }

        public PanKind Kind
        {
            get { return PanKind.Quark; }
        }

        public string DisplayName
        {
            get { return "夸克网盘"; }
        }

        public PanCapabilities Capabilities
        {
            get
            {
                return new PanCapabilities
                {
                    TransferFree = true,
                    NeedsCaptcha = false,
                    NeedsSignature = false,
                    SupportsPasscode = true,
                    SupportsSubFolder = true,
                    RequiresLogin = true,
                    Note = "本项目的主力网盘，取链链路已长期验证。",
                };
            }
        }

        public bool IsLoggedIn
        {
            get { return !string.IsNullOrEmpty(_client.CookieStr); }
        }

        /// <summary>
        /// 账号展示名。夸克客户端把昵称放在 <c>ValidateAsync</c> 的返回值里、
        /// 不自己存字段，所以由调用方在校验成功后写回这里。
        /// </summary>
        public string AccountName { get; set; } = "";

        /// <summary>头像直接读底层夸克客户端的探测结果（<c>avatarUri</c>），没有就是空串。</summary>
        public string AvatarUrl
        {
            get { return _client.AvatarUrl ?? ""; }
        }

        public async Task<IReadOnlyList<PanFileEntry>> ListShareAsync(
            PanShareRef shareRef, string parentFid, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");

            string stoken = await _client.GetStokenAsync(shareRef.ShareId, shareRef.Passcode ?? "")
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(stoken))
                throw new QuarkApiException("分享链接无效或提取码错误（未取到分享凭证）。");

            string pdir = string.IsNullOrEmpty(parentFid) ? "0" : parentFid;
            var entries = await _client.ShareDetailAsync(shareRef.ShareId, stoken, pdir)
                .ConfigureAwait(false);

            var result = new List<PanFileEntry>();
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    result.Add(new PanFileEntry
                    {
                        Fid = e.Fid,
                        Name = e.Name,
                        Size = e.Size,
                        IsDir = e.IsDir,
                        Token = e.Token,
                    });
                }
            }
            return result;
        }

        public async Task<IReadOnlyList<DownloadLink>> GetShareDownloadLinksAsync(
            PanShareRef shareRef, IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (shareRef == null)
                throw new QuarkApiException("分享引用为空。");
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            string stoken = await _client.GetStokenAsync(shareRef.ShareId, shareRef.Passcode ?? "")
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(stoken))
                throw new QuarkApiException("分享链接无效或提取码错误（未取到分享凭证）。");

            var fids = new List<string>();
            var tokens = new List<string>();
            foreach (var f in files)
            {
                fids.Add(f.Fid);
                tokens.Add(f.Token ?? "");
            }

            var links = await _client.GetShareDownloadAsync(fids, tokens, shareRef.ShareId, stoken)
                .ConfigureAwait(false);

            var result = new List<DownloadLink>();
            if (links != null)
                result.AddRange(links);
            return result;
        }

        public async Task<IReadOnlyList<DownloadLink>> GetOwnDownloadLinksAsync(
            IList<PanFileEntry> files, CancellationToken cancel = default)
        {
            if (files == null || files.Count == 0)
                return new List<DownloadLink>();

            var fids = new List<string>();
            foreach (var f in files)
                fids.Add(f.Fid);

            var links = await _client.GetDownloadAsync(fids).ConfigureAwait(false);
            var result = new List<DownloadLink>();
            if (links != null)
                result.AddRange(links);
            return result;
        }

        // ---------------- 自有网盘浏览 ----------------

        /// <summary>
        /// 最近一次校验失败的原因。
        /// 夸克/UC 的失败文案由 <c>MainWindow.DescribeLoginFailure</c> 从 Cookie
        /// 结构里推断（缺 __pus / 缺 __puus / 服务端作废），比异常消息更具体，
        /// 所以这里保持空串，让上层继续用那套推断。
        /// </summary>
        public string LastError
        {
            get { return ""; }
        }

        /// <summary>校验登录态并取昵称（直接复用夸克客户端的实现）。</summary>
        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            var (ok, nick) = await _client.ValidateAsync().ConfigureAwait(false);
            if (ok && !string.IsNullOrEmpty(nick))
                AccountName = nick;
            return (ok, nick);
        }

        /// <summary>列自己网盘目录（委托 <see cref="QuarkClient.ListDirAsync"/>）。</summary>
        public async Task<IReadOnlyList<QuarkFileEntry>> ListOwnDirAsync(
            string parentId, CancellationToken cancel = default)
        {
            var entries = await _client.ListDirAsync(
                string.IsNullOrEmpty(parentId) ? "0" : parentId).ConfigureAwait(false);
            var result = new List<QuarkFileEntry>();
            if (entries != null)
                result.AddRange(entries);
            return result;
        }

        /// <summary>查容量用量。</summary>
        public async Task<PanSpaceInfo> GetSpaceAsync(CancellationToken cancel = default)
        {
            try
            {
                var (total, used) = await _client.GetCapacityAsync().ConfigureAwait(false);
                return new PanSpaceInfo { Total = total, Used = used };
            }
            catch
            {
                return new PanSpaceInfo();
            }
        }

        /// <summary>
        /// 夸克分享**免转存**即可取链，不会在用户网盘里留下副本 —— 这里无事可做。
        /// 保留空实现是为了满足接口，让上层清理逻辑对四家网盘一视同仁。
        /// </summary>
        public Task CleanupStoredAsync(IList<string> storedIds, CancellationToken cancel = default(CancellationToken))
        {
            return Task.FromResult(0);
        }

        /// <summary>
        /// 轻量探测分享是否还有效：**只换 stoken**，不列目录。
        /// <para>
        /// 夸克的 stoken 接口（<c>sharepage/token</c>）匿名即可访问，
        /// 所以没登录也能探；服务端对「链接无效 / 提取码错」会直接报错，
        /// 对应 <see cref="QuarkApiException"/> ⇒ 判定为确定失效。
        /// </para>
        /// </summary>
        public async Task<ShareProbeResult> ProbeShareAsync(
            PanShareRef shareRef, CancellationToken cancel = default)
        {
            if (shareRef == null || string.IsNullOrEmpty(shareRef.ShareId))
                return new ShareProbeResult { Status = ShareProbeStatus.Unknown, Reason = "分享 ID 为空" };

            try
            {
                string stoken = await _client.GetStokenAsync(shareRef.ShareId, shareRef.Passcode ?? "")
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(stoken))
                {
                    return new ShareProbeResult
                    {
                        Status = ShareProbeStatus.Invalid,
                        Reason = "链接已失效，或提取码不对",
                    };
                }
                return new ShareProbeResult { Status = ShareProbeStatus.Alive };
            }
            catch (QuarkApiException ex)
            {
                // 服务端明确说了「链接无效 / 提取码错」这一类 ⇒ 确定失效。
                return new ShareProbeResult
                {
                    Status = ShareProbeStatus.Invalid,
                    Reason = DescribeProbeError(ex),
                };
            }
            catch (Exception)
            {
                // 网络故障、TLS 失败等 —— **不能**当失效。
                return new ShareProbeResult
                {
                    Status = ShareProbeStatus.Unknown,
                    Reason = "网络没通，暂时判断不了",
                };
            }
        }

        /// <summary>把夸克接口的报错翻成一句人话。</summary>
        private static string DescribeProbeError(QuarkApiException ex)
        {
            string msg = ex != null && ex.Message != null ? ex.Message : "";
            if (msg.IndexOf("passcode", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("提取码", StringComparison.Ordinal) >= 0
                || msg.IndexOf("密码", StringComparison.Ordinal) >= 0)
                return "提取码不对";
            if (msg.IndexOf("失效", StringComparison.Ordinal) >= 0
                || msg.IndexOf("不存在", StringComparison.Ordinal) >= 0
                || msg.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "链接已失效";
            return "链接已失效或提取码不对";
        }

        /// <summary>删除用户网盘里的文件（委托给内部的 QuarkClient）。</summary>
        public async Task DeleteFilesAsync(IList<string> fileIds, CancellationToken cancel = default(CancellationToken))
        {
            if (fileIds == null || fileIds.Count == 0)
                return;
            if (_client == null)
                throw new QuarkApiException("夸克客户端未就绪，无法删除。");
            await _client.DeleteAsync(fileIds).ConfigureAwait(false);
            try { await _client.PurgeRecycleAsync(fileIds).ConfigureAwait(false); } catch { }
        }

        public void Dispose()
        {
            if (_ownsClient && _client != null)
            {
                try { _client.Dispose(); } catch { }
            }
        }
    }
}