// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Providers.Cloud.Providers
{
    /// <summary>
    /// 光鸭云盘 provider（OAuth）。
    ///
    /// 协议要点（对照 guangya.dart）：
    /// - 凭据：refresh_token 作 primary
    /// - 分享解析两步：
    ///   ① POST /userres/v1/get_share_summary  → 拿标题等元信息
    ///   ② POST /userres/v1/get_share_access_token → 拿分享访问令牌
    /// - 取链：/userres/v1/get_share_download_url（分享）
    ///         /userres/v1/get_res_download_url（个人盘）
    /// - **已知坑**：CDN 对 `Range: bytes=0-0` 返回 200 长度 1，
    ///   会把大文件误判成一字节 —— 下载器侧要靠前两字节复核（已在文末注明）
    /// - 转存需要 30 × 0.75s 轮询（比其他平台久）
    /// </summary>
    public sealed class GuangyaProvider : TokenCloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Guangya;

        protected override string ApiBase => "https://api.guangyapan.com";
        protected override string TokenUrl => "https://api.guangyapan.com/v1/auth/token";
        protected override string ClientId => "guangya";

        private readonly CloudHttp _http = new CloudHttp();

        public override bool SupportsUpload => true;
        public override bool SupportsCreateShare => false;

        // 光鸭的轮询参数（比 123 久：30 次）
        private const int TransferAttempts = 30;
        private const int TransferIntervalMs = 750;

        // ================= 令牌 =================

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", Refresh(credential) },
            };

            var res = await _http.PostJsonAsync(TokenUrl, body, null,
                new Dictionary<string, string> { { "User-Agent", UserAgent } }, ct)
                .ConfigureAwait(false);

            // 光鸭把 refresh_token 当 primary
            ApplyTokenResponse(credential, res, refreshAsPrimary: true);
        }

        private Task<string> AccessAsync(CloudCredential credential, CancellationToken ct)
            => EnsureFreshAsync(credential, RenewTokenAsync, ct);

        private static Dictionary<string, string> H(string access)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", CloudHttp.DefaultUa },
                { "Referer", "https://www.guangyapan.com/" },
            };
            if (!string.IsNullOrEmpty(access))
                h["Authorization"] = "Bearer " + access;
            return h;
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var res = await _http.GetJsonAsync(ApiBase + "/v1/user/me",
                null, H(access), ct).ConfigureAwait(false);

            if (CloudJson.FailureMessage(res) is string err)
                throw new CloudApiException("光鸭云盘：" + err);

            var account = new CloudAccount
            {
                Nickname = CloudJson.Str(res, "nickname") is string n && n.Length > 0
                    ? n : (CloudJson.Str(res, "name") is string n2 && n2.Length > 0 ? n2 : "光鸭用户"),
            };

            try
            {
                var assets = await _http.GetJsonAsync(ApiBase + "/assets/v1/get_assets",
                    null, H(access), ct).ConfigureAwait(false);
                account.Used = CloudJson.Num(assets, "usedSpace");
                account.Total = CloudJson.Num(assets, "totalSpace");
            }
            catch { }

            return account;
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("光鸭云盘：分享链接无效");

            // ① 分享摘要（拿标题）—— 不需要登录
            var summary = await _http.PostJsonAsync(ApiBase + "/userres/v1/get_share_summary",
                new Dictionary<string, object> { { "shareId", link.ShareId } },
                null, H(null), ct).ConfigureAwait(false);

            if (CloudJson.FailureMessage(summary) is string err)
                throw new CloudApiException("光鸭云盘：" + err);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Guangya,
                Mode = BrowseMode.Share,
                Title = CloudJson.Str(summary, "title") is string t && t.Length > 0
                    ? t : (CloudJson.Str(summary, "shareName") is string sn && sn.Length > 0
                        ? sn : "光鸭分享"),
                RootId = "root",
                SourceLink = link,
            };
            session.SetMeta("shareId", link.ShareId);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");

            // ② 分享访问令牌（有提取码时必须）
            string token = await GetShareAccessTokenAsync(link.ShareId, link.Passcode, ct)
                .ConfigureAwait(false);
            session.SetMeta("shareAccessToken", token ?? "");

            return session;
        }

        private async Task<string> GetShareAccessTokenAsync(string shareId, string passcode,
            CancellationToken ct)
        {
            try
            {
                var res = await _http.PostJsonAsync(ApiBase + "/userres/v1/get_share_access_token",
                    new Dictionary<string, object>
                    {
                        { "shareId", shareId },
                        { "code", passcode ?? "" },
                    }, null, H(null), ct).ConfigureAwait(false);

                string token = CloudJson.Str(res, "accessToken");
                if (string.IsNullOrEmpty(token)) token = CloudJson.Str(res, "access_token");
                return token;
            }
            catch
            {
                return null;
            }
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Guangya,
                Mode = BrowseMode.Personal,
                Title = "我的光鸭云盘",
                RootId = "root",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);
            string parent = NormalizeParent(parentId);

            var body = new Dictionary<string, object>
            {
                { "parentId", parent },
                { "pageSize", 200 },
                { "page", 1 },
            };

            if (session.Mode == BrowseMode.Share)
            {
                body["shareId"] = session.Meta("shareId");
                body["accessToken"] = session.Meta("shareAccessToken");
            }

            string path = session.Mode == BrowseMode.Share
                ? "/userres/v1/get_share_file_list"
                : "/userres/v1/get_file_list";

            var res = await _http.PostJsonAsync(ApiBase + path, body, null, H(access), ct)
                .ConfigureAwait(false);

            var arr = CloudJson.Arr(CloudJson.Get(res, "list"))
                   ?? CloudJson.Arr(CloudJson.Get(res, "files"))
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
                    IsDirectory = CloudJson.Bool(d, "isFolder") || CloudJson.Bool(d, "dir")
                        || CloudJson.Num(d, "type") == 1,
                    ParentId = parent,
                    ModifiedAt = CloudJson.Str(d, "updateTime") is string ut && ut.Length > 0
                        ? ut : CloudJson.Str(d, "createTime"),
                    HashType = "md5",
                    HashValue = CloudJson.Str(d, "md5"),
                });
            }
            return result;
        }

        private static string NormalizeParent(string id)
            => string.IsNullOrEmpty(id) || id == "root" || id == "0" || id == "/" ? "" : id;

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");

            string access = await AccessAsync(credential, ct).ConfigureAwait(false);
            bool share = session.Mode == BrowseMode.Share;

            if (share)
            {
                var prep = new DownloadSpec
                {
                    Url = "",
                    FileName = file.Name,
                    ExpectedSize = file.Size,
                };
                prep.Source["fid"] = file.Id;
                prep.Source["shareId"] = session.Meta("shareId");
                prep.Source["passcode"] = session.Meta("passcode");
                prep.Source["platform"] = Platform.Key;
                throw new CloudNewPreparationException(
                    "光鸭云盘的分享文件需要先转存到自己账号才能下载。", prep);
            }

            var body = new Dictionary<string, object> { { "fileId", file.Id } };
            var res = await _http.PostJsonAsync(ApiBase + "/userres/v1/get_res_download_url",
                body, null, H(access), ct).ConfigureAwait(false);

            string url = CloudJson.Str(res, "downloadUrl");
            if (string.IsNullOrEmpty(url)) url = CloudJson.Str(res, "url");
            if (string.IsNullOrEmpty(url)) url = CloudJson.StrPath(res, "data.url");

            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("光鸭云盘：取链失败（"
                    + (CloudJson.Str(res, "message") ?? "未知原因") + "）");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = UserAgent;
            return spec;
        }

        // ================= 转存 =================

        public async Task<string> CreateTempFolderAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);
            string name = CloudTransferFlow.NewTempFolderName();

            var res = await _http.PostJsonAsync(ApiBase + "/userres/v1/create_folder",
                new Dictionary<string, object>
                {
                    { "parentId", "" },
                    { "name", name },
                }, null, H(access), ct).ConfigureAwait(false);

            string id = CloudJson.Str(res, "fileId");
            if (string.IsNullOrEmpty(id)) id = CloudJson.StrPath(res, "data.fileId");

            if (string.IsNullOrEmpty(id))
                throw new CloudApiException("光鸭云盘：创建临时目录失败");

            return id;
        }

        public async Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            spec.Source.TryGetValue("fid", out var fid);
            spec.Source.TryGetValue("shareId", out var shareId);

            var res = await _http.PostJsonAsync(ApiBase + "/userres/v1/save_share_files",
                new Dictionary<string, object>
                {
                    { "shareId", shareId ?? "" },
                    { "fileIds", new[] { fid ?? "" } },
                    { "parentId", targetFolderId },
                }, null, H(access), ct).ConfigureAwait(false);

            return CloudJson.Str(res, "taskId") ?? "ok";
        }

        public async Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct)
        {
            return await TransferWaiter.WaitAsync(TransferAttempts, TransferIntervalMs,
                async token =>
                {
                    var items = await ListAsync(
                        await OpenPersonalAsync(credential, token).ConfigureAwait(false),
                        folderId, credential, token).ConfigureAwait(false);
                    return TransferWaiter.MatchUnique(items, spec.FileName, spec.ExpectedSize);
                }, onPhase, ct).ConfigureAwait(false);
        }

        public async Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var res = await _http.PostJsonAsync(ApiBase + "/userres/v1/get_res_download_url",
                new Dictionary<string, object> { { "fileId", savedFile.Id } },
                null, H(access), ct).ConfigureAwait(false);

            string url = CloudJson.Str(res, "downloadUrl");
            if (string.IsNullOrEmpty(url)) url = CloudJson.Str(res, "url");
            if (string.IsNullOrEmpty(url)) return null;

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = savedFile.Name,
                ExpectedSize = savedFile.Size > 0 ? savedFile.Size : originalSpec.ExpectedSize,
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
                string access = await AccessAsync(credential, ct).ConfigureAwait(false);
                await _http.PostJsonAsync(ApiBase + "/userres/v1/delete_files",
                    new Dictionary<string, object> { { "fileIds", new[] { folderId } } },
                    null, H(access), ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// 小飞机网盘 provider（OAuth）。
    ///
    /// 协议要点（对照 feijipan.dart）：
    /// - 凭据：access_token + refreshToken + **deviceId**
    /// - 下载地址是**加密的**：AES-128-ECB + 固定 key `dingHao-disk-app`，
    ///   接口返回十六进制密文，要解密才拿到 URL
    /// - 取链请求要带两个加密参数：
    ///   downloadId = AES("fileId|userId")
    ///   auth       = AES("fileId|timestamp")
    /// - 文件大小可能不准（ExactFileSize = false）
    /// </summary>
    public sealed class FeijipanProvider : TokenCloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Feijipan;

        protected override string ApiBase => "https://api.feijipan.com";
        protected override string TokenUrl => "https://api.feijipan.com/ws/renew";

        private readonly CloudHttp _http = new CloudHttp();

        public override bool SupportsUpload => true;
        public override bool SupportsCreateShare => false;

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                { "refreshToken", Refresh(credential) },
            };

            var res = await _http.PostJsonAsync(TokenUrl, body, null,
                new Dictionary<string, string>
                {
                    { "User-Agent", UserAgent },
                    { "deviceId", credential.Field("deviceId") ?? "" },
                }, ct).ConfigureAwait(false);

            ApplyTokenResponse(credential, res);
        }

        private Task<string> AccessAsync(CloudCredential credential, CancellationToken ct)
            => EnsureFreshAsync(credential, RenewTokenAsync, ct);

        private Dictionary<string, string> H(CloudCredential credential, string access)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", UserAgent },
                { "Referer", "https://www.feijipan.com/" },
                { "Origin", "https://www.feijipan.com" },
            };
            if (!string.IsNullOrEmpty(access)) h["Authorization"] = access;
            if (!string.IsNullOrEmpty(credential?.Field("deviceId")))
                h["deviceId"] = credential.Field("deviceId");
            return h;
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var res = await _http.GetJsonAsync(ApiBase + "/user/info/map",
                null, H(credential, access), ct).ConfigureAwait(false);

            if (CloudJson.FailureMessage(res) is string err)
                throw new CloudApiException("小飞机：" + err);

            var account = new CloudAccount
            {
                Nickname = CloudJson.Str(res, "nickName") is string n && n.Length > 0
                    ? n : "小飞机用户",
            };

            try
            {
                var acc = await _http.GetJsonAsync(ApiBase + "/user/account/map",
                    null, H(credential, access), ct).ConfigureAwait(false);
                account.Total = CloudJson.Num(acc, "totalSpace");
                account.Used = CloudJson.Num(acc, "usedSpace");
            }
            catch { }

            return account;
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("小飞机：分享链接无效");

            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            // 分享列表：/ws/share/list
            var res = await _http.GetJsonAsync(ApiBase + "/ws/share/list",
                new Dictionary<string, string>
                {
                    { "shareId", link.ShareId },
                    { "sharePwd", link.Passcode ?? "" },
                    { "parentId", "" },
                }, H(credential, access), ct).ConfigureAwait(false);

            if (CloudJson.FailureMessage(res) is string err)
                throw new CloudApiException("小飞机：" + err);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Feijipan,
                Mode = BrowseMode.Share,
                Title = CloudJson.Str(res, "shareName") is string sn && sn.Length > 0
                    ? sn : "小飞机分享",
                RootId = "",
                SourceLink = link,
            };
            session.SetMeta("shareId", link.ShareId);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Feijipan,
                Mode = BrowseMode.Personal,
                Title = "我的小飞机网盘",
                RootId = "",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string access = await AccessAsync(credential, ct).ConfigureAwait(false);

            var query = new Dictionary<string, string>
            {
                { "parentId", parentId ?? "" },
                { "page", "1" },
                { "size", "200" },
            };

            string path;
            if (session.Mode == BrowseMode.Share)
            {
                path = "/ws/share/list";
                query["shareId"] = session.Meta("shareId");
                query["sharePwd"] = session.Meta("passcode");
            }
            else
            {
                path = "/ws/recommend/list";
            }

            var res = await _http.GetJsonAsync(ApiBase + path, query,
                H(credential, access), ct).ConfigureAwait(false);

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
                    Name = CloudJson.Str(d, "fileName") is string fn && fn.Length > 0
                        ? fn : CloudJson.Str(d, "name"),
                    // 小飞机的 size 可能不准，但仍然是唯一可用值
                    Size = CloudJson.Num(d, "fileSize") > 0
                        ? CloudJson.Num(d, "fileSize") : CloudJson.Num(d, "size"),
                    IsDirectory = CloudJson.Bool(d, "isFolder") || CloudJson.Num(d, "fileType") == 1,
                    ParentId = parentId ?? "",
                    ModifiedAt = CloudJson.Str(d, "updateTime"),
                });
            }
            return result;
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");

            string access = await AccessAsync(credential, ct).ConfigureAwait(false);
            string userId = credential.Field("userId") ?? session.AccountId ?? "";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 两个加密参数（AES-ECB + 固定 key）
            string downloadId = CloudCrypto.FeijipanEncrypt(file.Id + "|" + userId);
            string auth = CloudCrypto.FeijipanEncrypt(file.Id + "|" + ts);

            var query = new Dictionary<string, string>
            {
                { "enable", "1" },
                { "downloadId", downloadId },
                { "auth", auth },
            };

            var res = await _http.GetJsonAsync(ApiBase + "/ws/file/redirect", query,
                H(credential, access), ct).ConfigureAwait(false);

            string encrypted = CloudJson.Str(res, "downloadUrl");
            if (string.IsNullOrEmpty(encrypted))
                throw new CloudApiException("小飞机：未返回下载地址（"
                    + (CloudJson.Str(res, "message") ?? "可能文件已失效") + "）");

            // 解密拿真实 URL
            string url = CloudCrypto.FeijipanDecryptUrl(encrypted);

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = UserAgent;
            spec.Headers["Referer"] = "https://www.feijipan.com/";
            return spec;
        }
    }
}
