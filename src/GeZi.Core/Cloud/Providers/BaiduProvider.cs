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
    /// 百度网盘 provider。
    ///
    /// 关键差异（对照 wenxi 的 baidu.dart）：
    /// - 凭据是 Cookie，**必须含 BDUSS**（否则不算登录）。
    /// - 分享解析要先 <c>/share/verify</c> 校验提取码，拿 <c>randsk</c>/<c>BDCLND</c>。
    /// - 列表走 <c>/rest/2.0/xpan/share?method=list</c>（分享）或 <c>/api/list</c>（个人）。
    /// - 取链后**必须带 Cookie（含 BDCLND）+ 特定 headers**，否则 403：
    ///   <c>Accept-Encoding: identity</c>、<c>Content-Transfer-Encoding: binary</c>。
    /// - 百度分享文件多数需要**转存**才能取链（非本人拥有的分享）。
    ///
    /// ⚠️ wenxi 还有一条「公益解析站 mf.dg22.top」通道，
    ///    但它的服务端取链实现**未开源**，且依赖账号密码。
    ///    本实现**不接入**该通道（安全与合规考虑），只做官方接口。
    /// </summary>
    public sealed class BaiduProvider : CloudProviderBase, ICloudTransferSupport
    {
        public override CloudPlatform Platform => CloudPlatform.Baidu;

        private const string Pan = "https://pan.baidu.com";
        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

        private readonly CloudHttp _http = new CloudHttp();

        // 百度已实现：分享解析、转存、取链
        // 未实现：上传、写操作 → 如实声明 false
        public override bool SupportsUpload => false;

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string cookie = RequireCookie(credential);

            var res = await _http.GetJsonAsync(Pan + "/rest/2.0/xpan/nas",
                new Dictionary<string, string> { { "method", "uinfo" }, { "access_token", "" } },
                Headers(cookie), ct).ConfigureAwait(false);

            string name = CloudJson.Str(res, "baidu_name");
            if (string.IsNullOrEmpty(name))
                throw new CloudApiException("百度网盘：Cookie 无效或已过期（BDUSS 缺失/失效）");

            var account = new CloudAccount { Nickname = name };

            try
            {
                var quota = await _http.GetJsonAsync(Pan + "/api/quota",
                    new Dictionary<string, string> { { "checkfree", "1" }, { "checkexpire", "1" } },
                    Headers(cookie), ct).ConfigureAwait(false);
                account.Total = CloudJson.Num(quota, "total");
                account.Used = CloudJson.Num(quota, "used");
            }
            catch { }

            return account;
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("百度网盘：分享链接缺少 shareid");

            string cookie = credential?.Primary ?? "";
            string surl = link.ShareId;

            // ① 校验提取码（无码分享跳过）
            string pwd = link.Passcode ?? "";
            if (!string.IsNullOrEmpty(pwd))
            {
                var verify = await _http.PostJsonAsync(Pan + "/share/verify",
                    null,
                    new Dictionary<string, string> { { "surl", surl }, { "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() } },
                    MergeHeaders(cookie, new Dictionary<string, string>
                    {
                        { "Content-Type", "application/x-www-form-urlencoded" },
                    }), ct).ConfigureAwait(false);

                // 校验失败时 errno != 0
                long errno = CloudJson.Num(verify, "errno");
                if (errno != 0)
                    throw new CloudApiException("百度网盘：提取码错误或分享已失效（errno " + errno + "）");
            }

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Baidu,
                Mode = BrowseMode.Share,
                Title = "百度分享 " + surl,
                RootId = "0",
                SourceLink = link,
            };
            session.SetMeta("surl", surl);
            session.SetMeta("passcode", pwd);
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);
            var session = new BrowseSession
            {
                Platform = CloudPlatform.Baidu,
                Mode = BrowseMode.Personal,
                Title = "我的百度网盘",
                RootId = "/",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string cookie = credential?.Primary ?? "";
            string dir = string.IsNullOrEmpty(parentId) ? session.RootId : parentId;
            var list = new List<CloudFile>();

            if (session.Mode == BrowseMode.Share)
            {
                var query = new Dictionary<string, string>
                {
                    { "method", "list" },
                    { "shareid", FindShareId(session) },
                    { "uk", session.Meta("uk") },
                    { "dir", dir },
                    { "page", "1" },
                    { "num", "100" },
                    { "root", "1" },
                    { "sekey", session.Meta("sekey") },
                };

                var res = await _http.GetJsonAsync(Pan + "/rest/2.0/xpan/share", query,
                    Headers(cookie), ct).ConfigureAwait(false);

                long errno = CloudJson.Num(res, "errno");
                if (errno != 0)
                    throw new CloudApiException("百度分享：读取列表失败（errno " + errno + "）");

                var arr = CloudJson.Arr(CloudJson.Get(res, "list"));
                if (arr != null)
                {
                    foreach (var it in arr)
                    {
                        var d = CloudJson.Dict(it);
                        if (d != null) list.Add(ToCloudFile(d, dir, share: true));
                    }
                }
                return list;
            }

            var pres = await _http.GetJsonAsync(Pan + "/api/list",
                new Dictionary<string, string>
                {
                    { "dir", dir },
                    { "order", "name" },
                    { "start", "0" },
                    { "limit", "1000" },
                    { "web", "1" },
                }, Headers(cookie), ct).ConfigureAwait(false);

            var parr = CloudJson.Arr(CloudJson.Get(pres, "list"));
            if (parr != null)
            {
                foreach (var it in parr)
                {
                    var d = CloudJson.Dict(it);
                    if (d != null) list.Add(ToCloudFile(d, dir, share: false));
                }
            }
            return list;
        }

        private static string FindShareId(BrowseSession s)
        {
            // 百度的 shareid 是数字，藏在 surl 对应的分享里；优先用缓存的
            string cached = s.Meta("shareid");
            return string.IsNullOrEmpty(cached) ? s.Meta("surl") : cached;
        }

        private static CloudFile ToCloudFile(Dictionary<string, object> d, string parent, bool share)
        {
            long isDir = CloudJson.Num(d, "isdir");
            return new CloudFile
            {
                Id = CloudJson.Str(d, "fs_id"),
                Name = CloudJson.Str(d, "server_filename"),
                Size = CloudJson.Num(d, "size"),
                IsDirectory = isDir == 1,
                ParentId = parent ?? "",
                // 分享场景要的是 fs_id 对应的 share 内 id
                Token = CloudJson.Str(d, "share_fsid"),
                ModifiedAt = CloudJson.Num(d, "server_mtime") > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(CloudJson.Num(d, "server_mtime"))
                        .ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "",
                HashType = "md5",
                HashValue = CloudJson.Str(d, "md5"),
            };
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");

            if (session.Mode == BrowseMode.Share)
            {
                // 百度分享文件：非本人拥有时必须先转存
                var prep = new DownloadSpec
                {
                    Url = "",
                    FileName = file.Name,
                    ExpectedSize = file.Size,
                };
                prep.Source["fid"] = file.Id;
                prep.Source["surl"] = session.Meta("surl");
                prep.Source["passcode"] = session.Meta("passcode");
                prep.Source["platform"] = CloudPlatform.Baidu.Key;
                throw new CloudNewPreparationException(
                    "百度网盘的分享文件需要先转存到自己账号才能下载。", prep);
            }

            string cookie = RequireCookie(credential);
            long fsId = long.TryParse(file.Id, out var n) ? n : 0;

            var res = await _http.GetJsonAsync(Pan + "/api/filemetas",
                new Dictionary<string, string>
                {
                    { "target", "[\"" + fsId + "\"]" },
                    { "dlink", "1" },
                }, Headers(cookie), ct).ConfigureAwait(false);

            var info = CloudJson.Arr(CloudJson.Get(res, "info"));
            string dlink = info != null && info.Length > 0
                ? CloudJson.Str(CloudJson.Dict(info[0]), "dlink") : "";

            if (string.IsNullOrEmpty(dlink))
                throw new CloudApiException("百度网盘：取链失败（文件可能已删除或无权访问）");

            var spec = new DownloadSpec
            {
                Url = dlink,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };

            // ⚠️ 百度下载必须带这几个头，缺一个就可能 403
            spec.Headers["User-Agent"] = Ua;
            spec.Headers["Cookie"] = cookie;
            spec.Headers["Referer"] = "https://pan.baidu.com/";
            spec.Headers["Accept-Encoding"] = "identity";
            spec.Headers["Content-Transfer-Encoding"] = "binary";
            return spec;
        }

        // ================= 辅助 =================

        private static Dictionary<string, string> Headers(string cookie)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", Ua },
                { "Referer", "https://pan.baidu.com/" },
            };
            if (!string.IsNullOrEmpty(cookie)) h["Cookie"] = cookie;
            return h;
        }

        private static Dictionary<string, string> MergeHeaders(string cookie,
            Dictionary<string, string> extra)
        {
            var h = Headers(cookie);
            foreach (var kv in extra) h[kv.Key] = kv.Value;
            return h;
        }

        private static string RequireCookie(CloudCredential credential)
        {
            string cookie = credential?.Primary;
            if (string.IsNullOrEmpty(cookie))
                throw new CloudApiException("百度网盘：未登录（需要 Cookie）");
            if (!cookie.Contains("BDUSS="))
                throw new CloudApiException("百度网盘：Cookie 缺少 BDUSS，请重新登录");
            return cookie;
        }

        // ================= ICloudTransferSupport（转存） =================
        //
        // 百度走两级目录：/我的网盘/doc/临时转存/tr_<uuid>
        // 参照 wenxi 的做法：
        //   · 固定父目录复用（避免每次都建）
        //   · 转存接口 share/transfer 直接返回 to_fs_id，**不需要轮询列表**
        //   · 清理用 /api/filemanager?opera=delete（HTTP 直调）

        private const string TempParentName = "文析助手临时转存";
        private const int TransferAttempts = 8;
        private const int TransferIntervalMs = 800;

        public async Task<string> CreateTempFolderAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string cookie = RequireCookie(credential);

            // ① 确保父目录存在（不存在就建）
            string parentId = await EnsureFolderAsync(cookie, "/", TempParentName, ct)
                .ConfigureAwait(false);

            // ② 在父目录下建本次的临时目录
            string name = "tr_" + Guid.NewGuid().ToString("N").Substring(0, 16);
            string fullPath = "/" + TempParentName + "/" + name;

            await CreateFolderAsync(cookie, fullPath, ct).ConfigureAwait(false);

            // ③ 返回路径（百度的转存接口要 path 而不是 id）
            return fullPath;
        }

        public async Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct)
        {
            string cookie = RequireCookie(credential);

            spec.Source.TryGetValue("surl", out var surl);
            spec.Source.TryGetValue("fid", out var fid);
            spec.Source.TryGetValue("shareid", out var shareid);
            spec.Source.TryGetValue("uk", out var uk);

            if (string.IsNullOrEmpty(shareid) || string.IsNullOrEmpty(uk))
                throw new CloudApiException("百度：缺少分享 ID/uk，请重新解析链接");

            var query = new Dictionary<string, string>
            {
                { "shareid", shareid },
                { "from", uk },
                { "ondup", "newcopy" },
            };

            // 转存要 form 编码，不是 JSON
            var form = new Dictionary<string, string>
            {
                { "fsidlist", "[" + fid + "]" },
                { "path", targetFolderId },
                { "force", "1" },
            };

            string body = string.Join("&", form.Select(kv =>
                Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

            var res = await _http.SendAsync(System.Net.Http.HttpMethod.Post,
                Pan + "/share/transfer", query, body,
                MergeHeaders(cookie, new Dictionary<string, string>
                {
                    { "Content-Type", "application/x-www-form-urlencoded" },
                    { "Origin", "https://pan.baidu.com" },
                    { "Referer", "https://pan.baidu.com/s/" },
                }), ct).ConfigureAwait(false);

            // 转存接口直接返回 to_fs_id，不需要轮询
            var list = CloudJson.Arr(CloudJson.Path(res, "extra.list"));
            string toFsId = list != null && list.Length > 0
                ? CloudJson.Str(CloudJson.Dict(list[0]), "to_fs_id") : "";

            if (string.IsNullOrEmpty(toFsId))
            {
                long errno = CloudJson.Num(res, "errno");
                throw new CloudApiException("百度：转存失败（errno " + errno + "，"
                    + (CloudJson.Str(res, "show_msg") ?? "可能是容量不足或文件已失效") + "）");
            }

            return toFsId;
        }

        public async Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct)
        {
            // 百度转存接口**同步返回 to_fs_id**，不需要轮询列表。
            // 但索引偶尔会延迟，所以还是查一下确认文件真的在。
            string fsId = saveResult as string;
            if (string.IsNullOrEmpty(fsId)) return null;

            string cookie = RequireCookie(credential);

            return await TransferWaiter.WaitAsync(TransferAttempts, TransferIntervalMs,
                async token =>
                {
                    try
                    {
                        var res = await _http.GetJsonAsync(Pan + "/api/filemetas",
                            new Dictionary<string, string>
                            {
                                { "target", "[\"" + fsId + "\"]" },
                                { "dlink", "0" },
                            }, Headers(cookie), token).ConfigureAwait(false);

                        var info = CloudJson.Arr(CloudJson.Get(res, "info"));
                        if (info == null || info.Length == 0) return null;

                        var d = CloudJson.Dict(info[0]);
                        if (d == null) return null;

                        return new CloudFile
                        {
                            Id = fsId,
                            Name = CloudJson.Str(d, "filename") is string n && n.Length > 0
                                ? n : spec.FileName,
                            Size = CloudJson.Num(d, "size"),
                            ParentId = folderId,
                            HashType = "md5",
                            HashValue = CloudJson.Str(d, "md5"),
                        };
                    }
                    catch { return null; }
                }, onPhase, ct).ConfigureAwait(false);
        }

        public async Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct)
        {
            string cookie = RequireCookie(credential);
            long fsId = long.TryParse(savedFile.Id, out var n) ? n : 0;

            var res = await _http.GetJsonAsync(Pan + "/api/filemetas",
                new Dictionary<string, string>
                {
                    { "target", "[\"" + fsId + "\"]" },
                    { "dlink", "1" },
                }, Headers(cookie), ct).ConfigureAwait(false);

            var info = CloudJson.Arr(CloudJson.Get(res, "info"));
            string dlink = info != null && info.Length > 0
                ? CloudJson.Str(CloudJson.Dict(info[0]), "dlink") : "";

            if (string.IsNullOrEmpty(dlink)) return null;

            var spec = new DownloadSpec
            {
                Url = dlink,
                FileName = savedFile.Name,
                ExpectedSize = savedFile.Size > 0 ? savedFile.Size : originalSpec.ExpectedSize,
            };
            spec.Headers["User-Agent"] = Ua;
            spec.Headers["Cookie"] = cookie;
            spec.Headers["Referer"] = "https://pan.baidu.com/";
            spec.Headers["Accept-Encoding"] = "identity";
            spec.Headers["Content-Transfer-Encoding"] = "binary";
            return spec;
        }

        public async Task DeleteTempFolderAsync(string folderId, CloudCredential credential,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(folderId)) return;

            // 安全校验：只删我们自己建的 tr_ 目录
            string name = folderId.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            if (!name.StartsWith("tr_", StringComparison.Ordinal))
                return;

            try
            {
                string cookie = RequireCookie(credential);
                string body = "filelist=" + Uri.EscapeDataString("[\"" + folderId + "\"]");

                await _http.SendAsync(System.Net.Http.HttpMethod.Post,
                    Pan + "/api/filemanager",
                    new Dictionary<string, string>
                    {
                        { "async", "0" },
                        { "onnest", "fail" },
                        { "opera", "delete" },
                    },
                    body,
                    MergeHeaders(cookie, new Dictionary<string, string>
                    {
                        { "Content-Type", "application/x-www-form-urlencoded" },
                    }), ct).ConfigureAwait(false);
            }
            catch
            {
                // 清理失败不影响下载
            }
        }

        // ---------------- 辅助 ----------------

        /// <summary>确保某个目录存在，返回其路径。</summary>
        private async Task<string> EnsureFolderAsync(string cookie, string parentPath,
            string name, CancellationToken ct)
        {
            string full = parentPath.TrimEnd('/') + "/" + name;
            try
            {
                await CreateFolderAsync(cookie, full, ct).ConfigureAwait(false);
            }
            catch
            {
                // 已存在会报错，忽略
            }
            return full;
        }

        private async Task CreateFolderAsync(string cookie, string path, CancellationToken ct)
        {
            await _http.SendAsync(System.Net.Http.HttpMethod.Post,
                Pan + "/api/create",
                new Dictionary<string, string>
                {
                    { "a", "commit" },
                    { "isdir", "1" },
                    { "block_list", "[]" },
                },
                "path=" + Uri.EscapeDataString(path),
                MergeHeaders(cookie, new Dictionary<string, string>
                {
                    { "Content-Type", "application/x-www-form-urlencoded" },
                }), ct).ConfigureAwait(false);
        }
    }
}
