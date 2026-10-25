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
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 阿里云盘 provider。
    ///
    /// **认证模型与夸克完全不同**，这是本类最关键的部分：
    ///   · 夸克是一个 Cookie 串走天下
    ///   · 阿里是 OAuth2：refresh_token → 换 access_token（有效期约 2 小时）
    ///     → 所有业务请求带 <c>Authorization: Bearer {access_token}</c>
    ///     → 每次响应都可能下发新的 access_token，必须**立即替换**
    ///
    /// 参照 wenxi 的 aliyun.dart 与 token_session.dart：
    /// 换票入口是 <c>POST auth.aliyundrive.com/v2/account/token</c>，
    /// 参数 <c>{ refresh_token, grant_type: "refresh_token" }</c>。
    ///
    /// 凭据字段约定：
    ///   primary   = refresh_token（长期有效，用户从网页端复制）
    ///   secondary = access_token（短期，可空；首次调用会自动换取）
    ///
    /// 实现范围：账号信息、个人盘浏览、分享解析、取直链。
    /// 上传/分享创建等写操作留待后续 —— 接口路径已标注。
    /// </summary>
    public sealed class AliyunProvider : CloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Aliyun;

        private const string AuthTokenUrl = "https://auth.aliyundrive.com/v2/account/token";
        private const string ApiBase = "https://api.aliyundrive.com";
        private const string UserInfoUrl = "https://user.aliyundrive.com/v2/user/get";

        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly QuarkSession _session = new QuarkSession(null);

        // access_token 缓存：按 refresh_token 分桶
        private static readonly Dictionary<string, (string token, DateTime expireAt)> TokenCache =
            new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        private static readonly object TokenLock = new object();

        /// <summary>默认存储空间（阿里云盘有"备份盘/资源库"之分）。</summary>
        private const string DefaultDrive = "resource";

        // ================= 令牌管理 =================

        /// <summary>
        /// 取可用的 access_token；过期就自动用 refresh_token 换。
        /// </summary>
        private async Task<string> GetAccessTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string refresh = credential?.Primary ?? "";
            if (string.IsNullOrEmpty(refresh))
                throw new CloudApiException("阿里云盘：缺少 refresh_token，请重新登录");

            // ① 缓存命中且未过期
            lock (TokenLock)
            {
                if (TokenCache.TryGetValue(refresh, out var cached)
                    && cached.expireAt > DateTime.UtcNow.AddMinutes(2))
                    return cached.token;
            }

            // ② 凭据里自带的 access_token（用户手动填的）也认
            string provided = credential.Secondary;
            if (!string.IsNullOrEmpty(provided))
            {
                lock (TokenLock)
                    TokenCache[refresh] = (provided, DateTime.UtcNow.AddMinutes(30));
                return provided;
            }

            // ③ 换新票
            var body = new Dictionary<string, object>
            {
                { "refresh_token", refresh },
                { "grant_type", "refresh_token" },
            };

            var res = await PostJsonAsync(AuthTokenUrl, null, body, ct).ConfigureAwait(false);
            string access = AsString(Get(res, "access_token"));
            string newRefresh = AsString(Get(res, "refresh_token"));

            if (string.IsNullOrEmpty(access))
            {
                string msg = AsString(Get(res, "message")) ?? "换取 access_token 失败";
                throw new CloudApiException("阿里云盘：" + msg);
            }

            // 阿里每次换票都会轮换 refresh_token，旧的随即失效。
            // 上层如果没把这个新值存下来，下次启动就得重新登录 ——
            // 所以这里通过 credential 回写（凭据对象是引用类型）。
            long expiresIn = AsLong(Get(res, "expires_in"));
            if (expiresIn <= 0) expiresIn = 7200;

            lock (TokenLock)
                TokenCache[refresh] = (access, DateTime.UtcNow.AddSeconds(expiresIn));

            if (!string.IsNullOrEmpty(newRefresh) && newRefresh != refresh)
            {
                // 记录轮换后的 refresh_token，供上层保存
                credential.WithFields(new Dictionary<string, string>
                {
                    { CloudCredential.SecondaryKey, access },
                    { "rotated_refresh", newRefresh },
                });
            }

            return access;
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string token = await GetAccessTokenAsync(credential, ct).ConfigureAwait(false);

            var res = await PostJsonAsync(UserInfoUrl, token,
                new Dictionary<string, object>(), ct).ConfigureAwait(false);

            if (AsString(Get(res, "user_id")) == null && AsString(Get(res, "message")) != null)
                throw new CloudApiException("阿里云盘：" + AsString(Get(res, "message")));

            return new CloudAccount
            {
                Nickname = AsString(Get(res, "nick_name")) ?? AsString(Get(res, "user_name")) ?? "",
                Used = AsLong(Get(res, "used_size")),
                Total = AsLong(Get(res, "total_size")),
            };
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("阿里云盘：分享链接缺少 share_id");

            string token = await GetAccessTokenAsync(credential, ct).ConfigureAwait(false);

            // 换分享凭证：POST /v2/share_link/get_share_token
            var res = await PostJsonAsync(ApiBase + "/v2/share_link/get_share_token", token,
                new Dictionary<string, object>
                {
                    { "share_id", link.ShareId },
                    { "share_pwd", link.Passcode ?? "" },
                }, ct).ConfigureAwait(false);

            string shareToken = AsString(Get(res, "share_token"));
            if (string.IsNullOrEmpty(shareToken))
                throw new CloudApiException(
                    "阿里云盘：未返回分享凭据，请检查提取码" +
                    (AsString(Get(res, "message")) is string m ? "（" + m + "）" : ""));

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Aliyun,
                Mode = BrowseMode.Share,
                Title = "阿里云盘分享 " + link.ShareId,
                RootId = "root",
                SourceLink = link,
            };
            session.SetMeta("shareId", link.ShareId);
            session.SetMeta("shareToken", shareToken);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("driveId", DefaultDrive);
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Aliyun,
                Mode = BrowseMode.Personal,
                Title = "我的阿里云盘",
                RootId = "root",
            };
            session.SetMeta("driveId", DefaultDrive);
            session.SetMeta("accountId", account.Nickname ?? credential?.Label ?? "");
            return session;
        }

        /// <summary>阿里的"资源库/备份盘"就是这里的存储空间。</summary>
        public override Task<IReadOnlyList<CloudSpace>> GetPersonalSpacesAsync(
            CloudCredential credential, CancellationToken ct = default)
        {
            IReadOnlyList<CloudSpace> spaces = new List<CloudSpace>
            {
                new CloudSpace(DefaultDrive, "资源库"),
                new CloudSpace("backup", "备份盘"),
            };
            return Task.FromResult(spaces);
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null) return Array.Empty<CloudFile>();

            string token = await GetAccessTokenAsync(credential, ct).ConfigureAwait(false);
            string drive = session.Meta("driveId");
            if (string.IsNullOrEmpty(drive)) drive = DefaultDrive;

            string parent = string.IsNullOrEmpty(parentId) ? "root" : parentId;
            var result = new List<CloudFile>();

            string marker = "";
            for (int page = 0; page < 200; page++)
            {
                ct.ThrowIfCancellationRequested();

                var body = new Dictionary<string, object>
                {
                    { "drive_id", drive },
                    { "parent_file_id", parent },
                    { "limit", 100 },
                    { "order_by", "name" },
                    { "order_direction", "ASC" },
                    { "fields", "*" },
                };
                if (!string.IsNullOrEmpty(marker))
                    body["marker"] = marker;

                string url = session.Mode == BrowseMode.Share
                    ? ApiBase + "/v2/share_link/list"
                    : ApiBase + "/v2/file/list";

                if (session.Mode == BrowseMode.Share)
                {
                    body["share_id"] = session.Meta("shareId");
                    body["parent_file_id"] = parent == "root" ? "root" : parent;
                }

                var res = await PostJsonAsync(url, token, body, ct,
                    shareToken: session.Mode == BrowseMode.Share
                        ? session.Meta("shareToken") : null).ConfigureAwait(false);

                var items = AsArray(Get(res, "items"));
                if (items == null || items.Length == 0) break;

                foreach (var it in items)
                {
                    var d = AsDict(it);
                    if (d == null) continue;

                    result.Add(new CloudFile
                    {
                        Id = AsString(Get(d, "file_id")) ?? "",
                        Name = AsString(Get(d, "name")) ?? "",
                        Size = AsLong(Get(d, "size")),
                        IsDirectory = string.Equals(AsString(Get(d, "type")), "folder",
                            StringComparison.OrdinalIgnoreCase),
                        ParentId = parent,
                        ModifiedAt = AsString(Get(d, "updated_at")) ?? "",
                        HashType = "sha1",
                        HashValue = AsString(Get(d, "content_hash")) ?? "",
                        ThumbnailUrl = AsString(Get(d, "thumbnail")) ?? "",
                    });
                }

                marker = AsString(Get(res, "next_marker")) ?? "";
                if (string.IsNullOrEmpty(marker)) break;
            }

            return result;
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (file.IsDirectory)
                throw new CloudApiException("目录不能直接下载");

            string token = await GetAccessTokenAsync(credential, ct).ConfigureAwait(false);
            string drive = session.Meta("driveId");
            if (string.IsNullOrEmpty(drive)) drive = DefaultDrive;

            string url;
            if (session.Mode == BrowseMode.Share)
            {
                url = await GetShareDownloadUrlAsync(session, file, token, ct).ConfigureAwait(false);
            }
            else
            {
                var body = new Dictionary<string, object>
                {
                    { "drive_id", drive },
                    { "file_id", file.Id },
                    { "expire_sec", 14400 },
                };
                var res = await PostJsonAsync(ApiBase + "/v2/file/get_download_url", token,
                    body, ct).ConfigureAwait(false);
                url = AsString(Get(res, "url"));
            }

            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("阿里云盘：获取直链失败");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = Ua;
            spec.Headers["Referer"] = "https://www.alipan.com/";
            return spec;
        }

        private async Task<string> GetShareDownloadUrlAsync(BrowseSession session,
            CloudFile file, string token, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                { "share_id", session.Meta("shareId") },
                { "drive_id", session.Meta("driveId") },
                { "file_id", file.Id },
                { "expire_sec", 14400 },
            };

            var res = await PostJsonAsync(ApiBase + "/v2/file/get_download_url", token, body, ct,
                shareToken: session.Meta("shareToken")).ConfigureAwait(false);

            return AsString(Get(res, "url"));
        }

        // ================= HTTP =================

        private async Task<object> PostJsonAsync(string url, string accessToken,
            Dictionary<string, object> body, CancellationToken ct, string shareToken = null)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                if (!string.IsNullOrEmpty(shareToken))
                    req.Headers.TryAddWithoutValidation("x-share-token", shareToken);

                req.Headers.TryAddWithoutValidation("User-Agent", Ua);
                req.Headers.TryAddWithoutValidation("Referer", "https://www.alipan.com/");
                req.Headers.TryAddWithoutValidation("Origin", "https://www.alipan.com");

                req.Content = new StringContent(
                    JsonCompat.Serialize(body ?? new Dictionary<string, object>()),
                    System.Text.Encoding.UTF8, "application/json");

                using (var resp = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try
                    {
                        return JsonCompat.Deserialize(text);
                    }
                    catch
                    {
                        throw new CloudApiException(
                            "阿里云盘：响应不是有效 JSON（HTTP " + (int)resp.StatusCode + "）");
                    }
                }
            }
        }

        // ================= 取值辅助 =================

        private static object Get(object obj, string key)
        {
            var d = obj as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out var v) ? v : null;
        }

        private static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;
        private static object[] AsArray(object o) => o as object[];
        private static string AsString(object o) => o as string;

        private static long AsLong(object o)
        {
            if (o == null) return 0;
            if (o is long l) return l;
            if (o is int i) return i;
            if (o is double d) return (long)d;
            if (o is decimal m) return (long)m;
            if (o is string s) return long.TryParse(s, out var v) ? v : 0;
            return 0;
        }
    }
}
