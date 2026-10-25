// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 蓝奏云优享版 provider（OAuth）。
    ///
    /// 与普通蓝奏的区别：优享版是**独立域名 + 独立 API**（apis.ilanzou.com），
    /// 走 token 鉴权而非 Cookie，且是单页应用式的 JSON 接口。
    ///
    /// 能力限制（与 wenxi 一致）：
    /// - **不支持分享解析**（SupportsShareParsing = false）
    /// - **不支持创建分享**
    /// - 文件大小可能不准
    /// </summary>
    public sealed class IlanzouProvider : TokenCloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Ilanzou;

        protected override string ApiBase => "https://apis.ilanzou.com";
        protected override string TokenUrl => "https://apis.ilanzou.com/v1/auth/refresh";

        private readonly CloudHttp _http = new CloudHttp();

        public override bool SupportsUpload => true;
        public override bool SupportsRename => false;
        public override bool SupportsMove => false;
        public override bool SupportsDelete => false;
        public override bool SupportsCreateFolder => false;
        public override bool SupportsCreateShare => false;

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                { "grantType", "refresh_token" },
                { "refreshToken", Refresh(credential) },
            };

            var res = await _http.PostJsonAsync(TokenUrl, body, null,
                new Dictionary<string, string> { { "User-Agent", UserAgent } }, ct)
                .ConfigureAwait(false);

            ApplyTokenResponse(credential, res);
        }

        private Task<string> AccessAsync(CloudCredential credential, CancellationToken ct)
            => EnsureFreshAsync(credential, RenewTokenAsync, ct);

        private static Dictionary<string, string> H(string access)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", CloudHttp.DefaultUa },
                { "Referer", "https://www.ilanzou.com/" },
            };
            if (!string.IsNullOrEmpty(access)) h["Authorization"] = "Bearer " + access;
            return h;
        }

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var res = await _http.GetJsonAsync(ApiBase + "/v1/user/info",
                null, H(access), ct).ConfigureAwait(false);

            return new CloudAccount
            {
                Nickname = CloudJson.Str(res, "nickname") is string n && n.Length > 0
                    ? n : "优享用户",
                Total = CloudJson.Num(res, "totalSpace"),
                Used = CloudJson.Num(res, "usedSpace"),
            };
        }

        public override Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            // 优享版明确不支持分享解析 —— 如实抛出而不是假装能用
            throw new CloudNotSupportedException(
                "蓝奏云优享版不支持分享解析（请在「我的网盘」里浏览个人文件）");
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            // 先验一次 token（顺便拿昵称），失败会抛 CloudApiException
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Ilanzou,
                Mode = BrowseMode.Personal,
                Title = "我的蓝奏云优享版",
                RootId = "0",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var query = new Dictionary<string, string>
            {
                { "folderId", string.IsNullOrEmpty(parentId) ? "0" : parentId },
                { "page", "1" },
                { "size", "200" },
            };

            var res = await _http.GetJsonAsync(ApiBase + "/unproved/file/list", query,
                H(access), ct).ConfigureAwait(false);

            var arr = CloudJson.Arr(CloudJson.Get(res, "list"))
                   ?? CloudJson.Arr(CloudJson.Get(res, "data"));

            var result = new List<CloudFile>();
            if (arr == null) return result;

            foreach (var it in arr)
            {
                var d = CloudJson.Dict(it);
                if (d == null) continue;

                result.Add(new CloudFile
                {
                    Id = CloudJson.Str(d, "fileId") is string fid && fid.Length > 0
                        ? fid : CloudJson.Str(d, "id"),
                    Name = CloudJson.Str(d, "name") is string nm && nm.Length > 0
                        ? nm : CloudJson.Str(d, "fileName"),
                    Size = CloudJson.Num(d, "size"),
                    IsDirectory = CloudJson.Bool(d, "isFolder") || CloudJson.Num(d, "type") == 1,
                    ParentId = parentId ?? "",
                });
            }
            return result;
        }

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");

            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var res = await _http.GetJsonAsync(ApiBase + "/unproved/file/redirect",
                new Dictionary<string, string> { { "fileId", file.Id } },
                H(access), ct).ConfigureAwait(false);

            string url = CloudJson.Str(res, "downloadUrl");
            if (string.IsNullOrEmpty(url)) url = CloudJson.Str(res, "url");
            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("优享：取链失败");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = UserAgent;
            spec.Headers["Referer"] = "https://www.ilanzou.com/";
            return spec;
        }
    }

}
