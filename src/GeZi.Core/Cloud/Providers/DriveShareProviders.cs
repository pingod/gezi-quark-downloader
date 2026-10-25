// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 迅雷 / PikPak 共用实现。
    ///
    /// 这两个平台的 API **几乎完全同构**（参照 wenxi 的 xunlei.dart 与 pikpak.dart，
    /// 连取链字段都一模一样：<c>links["application/octet-stream"].url</c>
    /// 或 <c>web_content_link</c>），所以用一个基类 + 两个薄子类。
    ///
    /// 共同流程：
    ///   openShare → GET /drive/v1/share?share_id=&amp;pass_code=
    ///   list      → GET /drive/v1/share（分享）或 /drive/v1/files（个人）
    ///   download  → 分享需 POST /drive/v1/share/restore（转存，直接返回新 id 列表）
    ///               → GET /drive/v1/files/{id}?_magic=2021&amp;usage=PLAY
    ///               → 取 links.application/octet-stream.url，退化到 web_content_link
    ///
    /// 关键差异：
    ///   · 迅雷：auth 在 xluser-ssl.xunlei.com，clientId 内置；分享**必须登录**
    ///   · PikPak：auth 在 user.mypikpak.com，必须带 deviceId；分享必须登录
    ///   · 迅雷的 restore 直接返回新 fileId 列表，**不需要轮询列表可见性**
    /// </summary>
    public abstract class DriveShareProviderBase : TokenCloudProviderBase, ICloudTransferSupport
    {
        protected readonly CloudHttp Http = new CloudHttp();

        /// <summary>是否需要 deviceId（PikPak 必需）。</summary>
        protected virtual bool RequiresDeviceId => false;

        // ================= 鉴权 =================

        /// <summary>取一个可用的 access_token。</summary>
        protected Task<string> AccessTokenAsync(CloudCredential credential, CancellationToken ct)
            => EnsureFreshAsync(credential, RenewTokenAsync, ct);

        protected Dictionary<string, string> Headers(CloudCredential credential, string access)
        {
            var h = AuthHeaders(credential, access);
            h["Accept"] = "application/json";
            return h;
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);

            // 先试 /v1/user/me（PikPak），失败再试 /drive/v1/about（迅雷）
            try
            {
                var me = await Http.GetJsonAsync(ApiBase + "/v1/user/me",
                    null, Headers(credential, access), ct).ConfigureAwait(false);
                string nick = CloudJson.Str(me, "name");
                if (!string.IsNullOrEmpty(nick))
                    return new CloudAccount { Nickname = nick };
            }
            catch { }

            try
            {
                var about = await Http.GetJsonAsync(ApiBase + "/drive/v1/about",
                    null, Headers(credential, access), ct).ConfigureAwait(false);

                var quota = CloudJson.Get(about, "quota");
                return new CloudAccount
                {
                    Nickname = CloudJson.Str(about, "nickname") is string n && n.Length > 0
                        ? n : Platform.ShortName + "用户",
                    Used = CloudJson.Num(quota, "used"),
                    Total = CloudJson.Num(quota, "size"),
                };
            }
            catch (CloudHttpException ex) when (ex.Status == 401)
            {
                throw new CloudApiException(Platform.Label + "：登录已失效，请重新登录");
            }
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            // 两个平台的分享解析都**硬性要求登录**（wenxi 源码第一行就 require(c != null)）
            if (credential == null || string.IsNullOrEmpty(Access(credential)))
                throw new CloudApiException(Platform.Label + " 分享解析需要先登录");

            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException(Platform.Label + "：分享链接缺少分享 ID");

            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);
            var data = await FetchShareAsync(link.ShareId, link.Passcode ?? "", access, ct)
                .ConfigureAwait(false);

            string status = CloudJson.Str(data, "share_status");
            if (status == "PASS_CODE_EMPTY" || status == "PASS_CODE_NEED")
                throw new CloudApiException("该分享需要提取码");
            if (status == "PASS_CODE_ERROR")
                throw new CloudApiException("提取码错误");

            var session = new BrowseSession
            {
                Platform = Platform,
                Mode = BrowseMode.Share,
                Title = CloudJson.Str(data, "title") is string t && t.Length > 0
                    ? t : Platform.ShortName + "分享",
                RootId = "",
                SourceLink = link,
            };
            session.SetMeta("shareId", link.ShareId);
            session.SetMeta("passCode", link.Passcode ?? "");
            // pass_code_token 是后续转存/取链的凭据
            session.SetMeta("passCodeToken", CloudJson.Str(data, "pass_code_token"));
            session.SetMeta("accountId", credential.Label ?? "");
            return session;
        }

        protected async Task<object> FetchShareAsync(string shareId, string passCode,
            string access, CancellationToken ct)
        {
            var query = new Dictionary<string, string>
            {
                { "share_id", shareId },
                { "pass_code", passCode ?? "" },
                { "limit", "100" },
                { "page_token", "" },
                { "thumbnail_size", "SIZE_LARGE" },
            };
            return await Http.GetJsonAsync(ApiBase + "/drive/v1/share", query,
                Headers(null, access), ct).ConfigureAwait(false);
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);
            var session = new BrowseSession
            {
                Platform = Platform,
                Mode = BrowseMode.Personal,
                Title = "我的" + Platform.ShortName + "网盘",
                RootId = "",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);
            var list = new List<CloudFile>();

            object res;
            if (session.Mode == BrowseMode.Share)
            {
                res = await FetchShareAsync(session.Meta("shareId"), session.Meta("passCode"),
                    access, ct).ConfigureAwait(false);
            }
            else
            {
                var query = new Dictionary<string, string>
                {
                    { "parent_id", parentId ?? "" },
                    { "limit", "100" },
                    { "with_audit", "true" },
                    { "thumbnail_size", "SIZE_LARGE" },
                };
                res = await Http.GetJsonAsync(ApiBase + "/drive/v1/files", query,
                    Headers(credential, access), ct).ConfigureAwait(false);
            }

            var arr = CloudJson.Arr(CloudJson.Get(res, "files"));
            if (arr == null) return list;

            foreach (var it in arr)
            {
                var d = CloudJson.Dict(it);
                if (d == null) continue;

                string kind = CloudJson.Str(d, "kind");

                list.Add(new CloudFile
                {
                    Id = CloudJson.Str(d, "id"),
                    Name = CloudJson.Str(d, "name"),
                    Size = CloudJson.Num(d, "size"),
                    IsDirectory = string.Equals(kind, "drive#folder", StringComparison.OrdinalIgnoreCase),
                    ParentId = parentId ?? "",
                    ModifiedAt = CloudJson.Str(d, "modified_time"),
                    HashType = "sha1",
                    HashValue = CloudJson.Str(d, "hash"),
                });
            }
            return list;
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");

            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);
            string fileId = file.Id;
            DownloadCleanup cleanup = null;

            // ---- 分享模式：先转存 ----
            if (session.Mode == BrowseMode.Share)
            {
                var (newIds, cleanupDir) = await RestoreShareAsync(session, file.Id, access, ct)
                    .ConfigureAwait(false);

                if (newIds == null || newIds.Count == 0)
                    throw new CloudApiException(Platform.Label + "：转存后未返回新文件 ID");

                fileId = newIds[0];
                cleanup = new DownloadCleanup { PlatformKey = Platform.Key, AccountId = session.AccountId };
                if (!string.IsNullOrEmpty(cleanupDir)) cleanup.DirectoryIds.Add(cleanupDir);
            }

            // ---- 取链 ----
            var query = new Dictionary<string, string>
            {
                { "_magic", "2021" },
                { "usage", "PLAY" },
                { "thumbnail_size", "SIZE_LARGE" },
                { "with", "hdr10,subtitle_files,task,public_share_tag" },
            };

            var data = await Http.GetJsonAsync(
                ApiBase + "/drive/v1/files/" + Uri.EscapeDataString(fileId),
                query, Headers(credential, access), ct).ConfigureAwait(false);

            // 优先 links["application/octet-stream"].url，退化到 web_content_link
            string url = CloudJson.StrPath(data, "links.application/octet-stream.url");
            if (string.IsNullOrEmpty(url)) url = CloudJson.Str(data, "web_content_link");

            if (string.IsNullOrEmpty(url))
                throw new CloudApiException(Platform.Label + "：没有返回可用下载链接");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = CloudJson.Num(data, "size") > 0
                    ? CloudJson.Num(data, "size") : file.Size,
                Cleanup = cleanup,
            };
            spec.Headers["User-Agent"] = UserAgent;
            return spec;
        }

        /// <summary>
        /// 转存分享文件到自己网盘。
        /// 两个平台的 restore 都是**直接返回新 fileId 列表**，不需要轮询列表可见性 ——
        /// 这是它们比其他平台简单的地方（对照 wenxi 的 xunlei.dart:660-680）。
        /// </summary>
        protected async Task<(List<string> ids, string cleanupFolderId)> RestoreShareAsync(
            BrowseSession session, string shareFileId, string access, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                { "share_id", session.Meta("shareId") },
                { "pass_code", session.Meta("passCode") },
                { "pass_code_token", session.Meta("passCodeToken") },
                { "file_ids", new[] { shareFileId } },
                { "parent_id", "" },
            };

            var res = await Http.PostJsonAsync(ApiBase + "/drive/v1/share/restore", body,
                null, Headers(session == null ? null : null, access), ct).ConfigureAwait(false);

            var ids = new List<string>();
            foreach (var key in new[] { "file_ids", "restored_file_ids" })
            {
                var arr = CloudJson.Arr(CloudJson.Get(res, key));
                if (arr == null) continue;
                foreach (var it in arr)
                {
                    string id = it as string ?? CloudJson.Str(it, "id");
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
                if (ids.Count > 0) break;
            }

            return (ids, null);
        }

        // ================= ICloudTransferSupport（转存） =================
        //
        // 迅雷/PikPak 的 restore 接口**直接返回新 fileId 列表**，
        // 不需要轮询列表可见性 —— 这是它们比其他平台简单的地方
        // （对照 wenxi 的 xunlei.dart:660-680："不轮询"）。

        public async Task<string> CreateTempFolderAsync(CloudCredential credential,
            CancellationToken ct)
        {
            // 两个平台的"建目录"都是 POST /drive/v1/files 带 kind=folder
            string name = CloudTransferFlow.NewTempFolderName();

            var body = new Dictionary<string, object>
            {
                { "kind", "drive#folder" },
                { "name", name },
                { "parent_id", "" },
            };

            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);

            var res = await Http.PostJsonAsync(ApiBase + "/drive/v1/files", body,
                null, Headers(credential, access), ct).ConfigureAwait(false);

            string id = CloudJson.Str(res, "id");
            if (string.IsNullOrEmpty(id)) id = CloudJson.StrPath(res, "file.id");

            if (string.IsNullOrEmpty(id))
                throw new CloudApiException(Platform.Label + "：创建临时目录失败");

            return id;
        }

        public async Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct)
        {
            spec.Source.TryGetValue("fid", out var fid);
            spec.Source.TryGetValue("shareId", out var shareId);
            spec.Source.TryGetValue("passCodeToken", out var passCodeToken);

            var body = new Dictionary<string, object>
            {
                { "share_id", shareId ?? "" },
                { "pass_code", spec.Source.TryGetValue("passCode", out var pc) ? pc : "" },
                { "pass_code_token", passCodeToken ?? "" },
                { "file_ids", new[] { fid ?? "" } },
                { "parent_id", targetFolderId ?? "" },
            };

            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);

            var res = await Http.PostJsonAsync(ApiBase + "/drive/v1/share/restore", body,
                null, Headers(credential, access), ct).ConfigureAwait(false);

            var ids = new List<string>();
            foreach (var key in new[] { "file_ids", "restored_file_ids" })
            {
                var arr = CloudJson.Arr(CloudJson.Get(res, key));
                if (arr == null) continue;
                foreach (var it in arr)
                {
                    string id = it as string ?? CloudJson.Str(it, "id");
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
                if (ids.Count > 0) break;
            }

            if (ids.Count == 0)
                throw new CloudApiException(Platform.Label + "：转存未返回新文件 ID");

            return ids;
        }

        public Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct)
        {
            // restore 已经直接给了新 id，**不需要轮询**。
            // 仍然做一次轻量确认（防止个别账号需要索引时间）。
            var ids = saveResult as List<string>;
            if (ids == null || ids.Count == 0)
                return Task.FromResult<CloudFile>(null);

            var file = new CloudFile
            {
                Id = ids[0],
                Name = spec.FileName,
                Size = spec.ExpectedSize,
                ParentId = folderId,
            };
            return Task.FromResult(file);
        }

        public async Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct)
        {
            string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);

            var query = new Dictionary<string, string>
            {
                { "_magic", "2021" },
                { "usage", "PLAY" },
                { "thumbnail_size", "SIZE_LARGE" },
            };

            var data = await Http.GetJsonAsync(
                ApiBase + "/drive/v1/files/" + Uri.EscapeDataString(savedFile.Id),
                query, Headers(credential, access), ct).ConfigureAwait(false);

            string url = CloudJson.StrPath(data, "links.application/octet-stream.url");
            if (string.IsNullOrEmpty(url)) url = CloudJson.Str(data, "web_content_link");

            if (string.IsNullOrEmpty(url)) return null;

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = savedFile.Name,
                ExpectedSize = CloudJson.Num(data, "size") > 0
                    ? CloudJson.Num(data, "size") : originalSpec.ExpectedSize,
            };
            spec.Headers["User-Agent"] = UserAgent;
            return spec;
        }

        public async Task DeleteTempFolderAsync(string folderId, CloudCredential credential,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(folderId)) return;

            try
            {
                string access = await AccessTokenAsync(credential, ct).ConfigureAwait(false);

                using (var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Delete,
                    ApiBase + "/drive/v1/files/" + Uri.EscapeDataString(folderId)))
                {
                    foreach (var kv in Headers(credential, access))
                        try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { }

                    using (var resp = await Http.Client.SendAsync(req, ct).ConfigureAwait(false))
                    { }
                }
            }
            catch
            {
                // 清理失败不影响下载
            }
        }
    }

    /// <summary>迅雷网盘。</summary>
    public sealed class XunleiProvider : DriveShareProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Xunlei;

        protected override string ApiBase => "https://api-pan.xunlei.com";
        protected override string TokenUrl => "https://xluser-ssl.xunlei.com/v1/auth/token";

        /// <summary>迅雷官方内置 clientId（公开值，所有第三方客户端通用）。</summary>
        protected override string ClientId => "Xp6vsxz_7IYVw2BB";

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string refresh = Refresh(credential);
            string device = credential.Field("deviceId");
            if (string.IsNullOrEmpty(device))
            {
                device = Guid.NewGuid().ToString("N");
                credential.SetFieldsInternal(new Dictionary<string, string> { { "deviceId", device } });
            }

            var body = new Dictionary<string, object>
            {
                { "grant_type", "refresh_token" },
                { "client_id", ClientId },
                { "refresh_token", refresh },
            };

            var res = await Http.PostJsonAsync(TokenUrl, body, null,
                new Dictionary<string, string>
                {
                    { "X-Device-Id", device },
                    { "User-Agent", UserAgent },
                }, ct).ConfigureAwait(false);

            ApplyTokenResponse(credential, res);
        }
    }

    /// <summary>PikPak 网盘。</summary>
    public sealed class PikPakProvider : DriveShareProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.PikPak;

        protected override string ApiBase => "https://api-drive.mypikpak.com";
        protected override string TokenUrl => "https://user.mypikpak.com/v1/auth/token";

        /// <summary>PikPak 的 client_id 是公开固定值。</summary>
        protected override string ClientId => "YNxT9w7GMdWvEOKa";
        protected override string ClientSecret => "dbw2OtmVEeuUvIptb1Coyg";

        protected override bool RequiresDeviceId => true;

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string refresh = Refresh(credential);
            string device = credential.Field("deviceId");
            if (string.IsNullOrEmpty(device))
                throw new CloudApiException("PikPak：缺少 deviceId，请重新登录");

            var body = new Dictionary<string, object>
            {
                { "grant_type", "refresh_token" },
                { "client_id", ClientId },
                { "client_secret", ClientSecret },
                { "refresh_token", refresh },
            };

            var res = await Http.PostJsonAsync(TokenUrl, body, null,
                new Dictionary<string, string>
                {
                    { "X-Device-Id", device },
                    { "User-Agent", UserAgent },
                }, ct).ConfigureAwait(false);

            ApplyTokenResponse(credential, res);
        }

        protected override Dictionary<string, string> AuthHeaders(CloudCredential credential,
            string accessToken)
        {
            var h = base.AuthHeaders(credential, accessToken);
            string device = credential?.Field("deviceId");
            if (!string.IsNullOrEmpty(device)) h["X-Device-Id"] = device;
            return h;
        }

    }
}
