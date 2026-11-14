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
    /// 115 网盘 provider。
    ///
    /// 关键差异（对照 wenxi 的 pan115.dart）：
    /// - **凭据必须同时含 UID / CID / SEID 三个 Cookie**，缺一个都不算登录。
    /// - 分享走 <c>115cdn.com/webapi/share/snap</c>（注意是 cdn 域），
    ///   个人盘走 <c>webapi.115.com/files/*</c>。
    /// - 取链用 **pickcode**（不是 fid）：
    ///   分享 → <c>proapi.115.com/app/share/downurl</c>
    ///   个人 → <c>proapi.115.com/app/chrome/downurl</c>
    /// - 下载 headers 只需 UA + Referer，**不要转发 UID/CID/SEID**
    ///   （转发登录 Cookie 到 CDN 反而容易被判盗链）。
    /// - 115 的响应有个特色：<c>state</c> 字段为 <c>true</c> 表示成功。
    /// </summary>
    public sealed class Pan115Provider : CloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Pan115;

        private const string Api = "https://webapi.115.com";
        private const string ShareApi = "https://115cdn.com/webapi";
        private const string ProApi = "https://proapi.115.com";

        private readonly CloudHttp _http = new CloudHttp();

        // 115 必须三个 Cookie 齐全
        private static readonly string[] RequiredCookies = { "UID", "CID", "SEID" };

        public override bool SupportsUpload => false;

        /// <summary>115 只实现了删除（其余写操作待补）。</summary>
        public override bool SupportsDelete => true;

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string cookie = RequireCookie(credential);

            var res = await _http.GetJsonAsync(Api + "/files?cid=0&limit=1&show_dir=1",
                null, new Dictionary<string, string> { { "Cookie", cookie } }, ct)
                .ConfigureAwait(false);

            if (!CloudJson.Bool(res, "state") && CloudJson.Str(res, "error") is string err && err.Length > 0)
                throw new CloudApiException("115 网盘：" + err);

            // 昵称要从另一个接口取，失败就用 Cookie 里的 UID
            string nick = ExtractCookie(cookie, "UID") ?? "115 用户";
            try
            {
                var info = await _http.GetJsonAsync(Api + "/user/info",
                    null, new Dictionary<string, string> { { "Cookie", cookie } }, ct).ConfigureAwait(false);
                nick = CloudJson.StrPath(info, "data.user_name", nick);
            }
            catch { }

            return new CloudAccount { Nickname = nick };
        }

        // ================= 打开会话 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("115 分享链接缺少分享码");

            string cookie = credential?.Primary ?? "";

            var query = new Dictionary<string, string>
            {
                { "share_code", link.ShareId },
                { "receive_code", link.Passcode ?? "" },
            };

            var res = await _http.GetJsonAsync(ShareApi + "/share/snap", query,
                Headers(cookie), ct).ConfigureAwait(false);

            if (!CloudJson.Bool(res, "state"))
                throw new CloudApiException("115 分享："
                    + (CloudJson.Str(res, "error") is string e && e.Length > 0 ? e : "打开失败，可能提取码错误"));

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Pan115,
                Mode = BrowseMode.Share,
                Title = "115 分享 " + link.ShareId,
                RootId = "0",
                SourceLink = link,
            };
            session.SetMeta("shareCode", link.ShareId);
            session.SetMeta("receiveCode", link.Passcode ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Pan115,
                Mode = BrowseMode.Personal,
                Title = "我的 115 网盘",
                RootId = "0",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string cookie = credential?.Primary ?? "";
            string cid = string.IsNullOrEmpty(parentId) ? "0" : parentId;

            var query = new Dictionary<string, string>
            {
                { "cid", cid },
                { "limit", "500" },
                { "show_dir", "1" },
                { "offset", "0" },
            };

            object res;
            if (session.Mode == BrowseMode.Share)
            {
                // 分享浏览：snap 接口已经返回了根目录清单，翻页走 share/list
                query["share_code"] = session.Meta("shareCode");
                query["receive_code"] = session.Meta("receiveCode");
                res = await _http.GetJsonAsync(ShareApi + "/share/snap", query, Headers(cookie), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                res = await _http.GetJsonAsync(Api + "/files", query, Headers(cookie), ct)
                    .ConfigureAwait(false);
            }

            var arr = CloudJson.Arr(CloudJson.Get(res, "data"))
                      ?? CloudJson.Arr(CloudJson.Get(res, "data.list"));
            if (arr == null) return Array.Empty<CloudFile>();

            var list = new List<CloudFile>();
            foreach (var it in arr)
            {
                var d = CloudJson.Dict(it);
                if (d == null) continue;

                list.Add(new CloudFile
                {
                    Id = CloudJson.Str(d, "cid") is string c && c.Length > 0
                        ? c : CloudJson.Str(d, "fid"),
                    Name = CloudJson.Str(d, "n") is string n && n.Length > 0
                        ? n : CloudJson.Str(d, "file_name"),
                    Size = CloudJson.Num(d, "s") is long sz && sz > 0
                        ? sz : CloudJson.Num(d, "file_size"),
                    IsDirectory = CloudJson.Bool(d, "is_dir") || CloudJson.Num(d, "file_category") == 0,
                    ParentId = cid,
                    // pickcode 是 115 取链的唯一凭据
                    Token = CloudJson.Str(d, "pc") is string pc && pc.Length > 0
                        ? pc : CloudJson.Str(d, "pick_code"),
                    ModifiedAt = CloudJson.Str(d, "te") is string te && te.Length > 0
                        ? te : CloudJson.Str(d, "user_utime"),
                    HashType = "sha1",
                    HashValue = CloudJson.Str(d, "sha"),
                });
            }
            return list;
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");
            if (string.IsNullOrEmpty(file.Token))
                throw new CloudApiException("115：缺少 pickcode，请刷新列表");

            string cookie = credential?.Primary ?? "";
            bool share = session.Mode == BrowseMode.Share;

            string url = share
                ? ProApi + "/app/share/downurl"
                : ProApi + "/app/chrome/downurl";

            var body = share
                ? (object)new Dictionary<string, string>
                {
                    { "share_code", session.Meta("shareCode") },
                    { "receive_code", session.Meta("receiveCode") },
                    { "pick_code", file.Token },
                }
                : new Dictionary<string, string> { { "pickcode", file.Token } };

            var res = await _http.PostJsonAsync(url, body, null, Headers(cookie), ct)
                .ConfigureAwait(false);

            if (!CloudJson.Bool(res, "state"))
                throw new CloudApiException("115：取链失败（"
                    + (CloudJson.Str(res, "error") ?? "未知原因") + "）");

            // 115 把结果塞在一层 JSON 字符串里（data 是 string，需要二次解析）
            string dataStr = CloudJson.StrPath(res, "data");
            object data = dataStr != null && dataStr.StartsWith("{")
                ? CloudJson.Parse(dataStr) : CloudJson.Get(res, "data");

            string downloadUrl = "";
            var files = CloudJson.Dict(data);
            if (files != null)
            {
                foreach (var kv in files)
                {
                    var item = CloudJson.Dict(kv.Value);
                    if (item == null) continue;
                    downloadUrl = CloudJson.Str(item, "url");
                    if (!string.IsNullOrEmpty(downloadUrl)) break;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
                throw new CloudApiException("115：响应里没有下载地址");

            var spec = new DownloadSpec
            {
                Url = downloadUrl,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            // ⚠️ 刻意只带 UA + Referer，不转发登录 Cookie
            spec.Headers["User-Agent"] = CloudHttp.DefaultUa;
            spec.Headers["Referer"] = "https://115.com/";
            return spec;
        }

        // ================= 文件管理 =================

        public override async Task DeleteAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            CloudCredential credential, CancellationToken ct = default)
        {
            EnsurePersonal(session, "删除");
            string cookie = RequireCookie(credential);

            foreach (var f in files)
            {
                var query = new Dictionary<string, string>
                {
                    { "aid", "1" },
                    { "cid", f.IsDirectory ? f.Id : "" },
                    { "fid", f.IsDirectory ? "" : f.Id },
                };
                var res = await _http.PostJsonAsync(Api + "/rb/delete", null, query, Headers(cookie), ct)
                    .ConfigureAwait(false);
                if (!CloudJson.Bool(res, "state"))
                    throw new CloudApiException("115 删除失败：" + CloudJson.Str(res, "error"));
            }
        }

        // ================= 辅助 =================

        private static Dictionary<string, string> Headers(string cookie)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", CloudHttp.DefaultUa },
                { "Referer", "https://115.com/" },
            };
            if (!string.IsNullOrEmpty(cookie)) h["Cookie"] = cookie;
            return h;
        }

        private static string RequireCookie(CloudCredential credential)
        {
            string cookie = credential?.Primary;
            if (string.IsNullOrEmpty(cookie))
                throw new CloudApiException("115 网盘：未登录（需要 Cookie）");

            foreach (var key in RequiredCookies)
            {
                if (!cookie.Contains(key + "="))
                    throw new CloudApiException("115 网盘：Cookie 缺少 " + key + "，请重新登录");
            }
            return cookie;
        }

        private static string ExtractCookie(string cookie, string name)
        {
            if (string.IsNullOrEmpty(cookie)) return null;
            foreach (var part in cookie.Split(';'))
            {
                var kv = part.Trim().Split(new[] { '=' }, 2);
                if (kv.Length == 2 && string.Equals(kv[0], name, StringComparison.OrdinalIgnoreCase))
                    return kv[1];
            }
            return null;
        }

        private static void EnsurePersonal(BrowseSession s, string action)
        {
            if (s == null || s.Mode != BrowseMode.Personal)
                throw new CloudNotSupportedException(action + "：分享浏览模式下不可用");
        }
    }
}
