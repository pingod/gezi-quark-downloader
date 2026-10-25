// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 中国联通云盘 provider（OAuth + AES 加密通道）。
    ///
    /// 这是**加密最重**的一个平台（对照 wopan.dart）：
    ///
    /// 1. **每条请求的参数都要 AES 加密**
    ///    <c>param</c> = Base64(AES-128-CBC(json, key, 固定IV))
    ///    key 的取法：channel=api-user 用 clientSecret；
    ///    其它通道用 access_token 的前 16 字符。
    ///
    /// 2. **响应也可能是加密的**，同样要解。
    ///
    /// 3. **签名**：MD5(key + 毫秒时间戳 + 序号 + channel)
    ///
    /// 4. 分享不需要登录（在 wenxi 的排除集合里），但个人盘和取链需要。
    ///
    /// 5. 下载地址有多个节点，主节点不可用时要换备用域名
    ///    （hydownload → tjdownload），这是实测过的。
    /// </summary>
    public sealed class WopanProvider : TokenCloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Wopan;

        protected override string ApiBase => "https://panservice.mail.wo.cn";
        protected override string TokenUrl => ApiBase + "/openapi/v1/app/AppRefreshToken";

        private readonly CloudHttp _http = new CloudHttp();

        public override bool SupportsUpload => true;
        public override bool SupportsCreateShare => false;   // 联通不支持建分享

        private static int _sequence;

        // ================= 令牌 =================

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string refresh = Refresh(credential);

            // 联通的刷新走 api-user 通道（用 clientSecret 加密）
            var body = new Dictionary<string, object>
            {
                { "refreshToken", refresh },
                { "clientId", CloudCrypto.WopanClientId },
            };

            var res = await CallAsync("AppRefreshToken", body, "api-user",
                credential, ct, read: true).ConfigureAwait(false);

            // 联通把 refresh_token 当 primary
            ApplyTokenResponse(credential, res, refreshAsPrimary: true);
        }

        private Task<string> AccessAsync(CloudCredential credential, CancellationToken ct)
            => EnsureFreshAsync(credential, RenewTokenAsync, ct);

        // ================= 加密调用 =================

        /// <summary>
        /// 调一次联通接口（自动加密参数、解密响应、算签名）。
        /// </summary>
        private async Task<Dictionary<string, object>> CallAsync(string key,
            Dictionary<string, object> param, string channel,
            CloudCredential credential, CancellationToken ct, bool read = false)
        {
            string access = credential != null ? Access(credential) : "";

            // ① 加密参数
            string encryptedParam = CloudCrypto.WopanEncrypt(
                JsonCompat.Serialize(param ?? new Dictionary<string, object>()),
                channel, access);

            // ② 签名：MD5(key + 毫秒 + 序号 + channel)
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int seq = Interlocked.Increment(ref _sequence);
            string signSource = key + ms + seq + channel;

            string sign;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                sign = CloudCrypto.ToHex(md5.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(signSource)));
            }

            // ③ 组装请求
            string url = ApiBase + "/openapi/v1/app/" + key;

            var query = new Dictionary<string, string>
            {
                { "channel", channel },
                { "clientId", CloudCrypto.WopanClientId },
                { "timestamp", ms.ToString() },
                { "sequence", seq.ToString() },
                { "sign", sign },
                { "key", key },
            };

            var headers = new Dictionary<string, string>
            {
                { "User-Agent", UserAgent },
                { "Referer", "https://pan.wo.cn/" },
                { "Origin", "https://pan.wo.cn" },
                { "Content-Type", "application/x-www-form-urlencoded" },
            };

            if (!string.IsNullOrEmpty(access) && channel != "api-user")
                headers["accessToken"] = access;

            // param 走 form 体
            string formBody = "param=" + Uri.EscapeDataString(encryptedParam);

            string text = await _http.SendStringAsync(System.Net.Http.HttpMethod.Post,
                url, query, formBody, headers, ct).ConfigureAwait(false);

            // ④ 解密响应
            string plain = CloudCrypto.WopanDecrypt(text, channel, access);
            var parsed = JsonCompat.Deserialize(plain) as Dictionary<string, object>
                         ?? new Dictionary<string, object>();

            return parsed;
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            await AccessAsync(credential, ct).ConfigureAwait(false);

            var res = await CallAsync("AppQueryUser",
                new Dictionary<string, object>
                {
                    { "accessToken", Access(credential) },
                }, "api-user", credential, ct, read: true).ConfigureAwait(false);

            // 联通把有效载荷放在 data 里
            var data = CloudJson.Dict(CloudJson.Get(res, "data")) ?? res;

            string userId = CloudJson.Str(data, "userId");
            if (string.IsNullOrEmpty(userId))
                throw new CloudApiException("联通云盘：未返回有效账号信息（登录可能已失效）");

            return new CloudAccount
            {
                Nickname = CloudJson.Str(data, "nickName") is string n && n.Length > 0
                    ? n : (CloudJson.Str(data, "userName") is string u && u.Length > 0
                        ? u : "联通用户"),
            };
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("联通云盘：分享链接无效");

            var res = await CallAsync("QueryShareFiles",
                new Dictionary<string, object>
                {
                    { "shareId", link.ShareId },
                    { "sharePwd", link.Passcode ?? "" },
                    { "parentId", "0" },
                }, "wohome", credential, ct, read: true).ConfigureAwait(false);

            if (CloudJson.FailureMessage(res) is string err)
                throw new CloudApiException("联通云盘：" + err);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Wopan,
                Mode = BrowseMode.Share,
                Title = CloudJson.Str(res, "shareName") is string sn && sn.Length > 0
                    ? sn : "联通分享 " + link.ShareId,
                RootId = "0",
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
                Platform = CloudPlatform.Wopan,
                Mode = BrowseMode.Personal,
                Title = "我的联通云盘",
                RootId = "0",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            bool share = session.Mode == BrowseMode.Share;

            var param = new Dictionary<string, object>
            {
                { "parentId", string.IsNullOrEmpty(parentId) ? "0" : parentId },
                { "pageNum", 1 },
                { "pageSize", 200 },
            };

            if (share)
            {
                param["shareId"] = session.Meta("shareId");
                param["sharePwd"] = session.Meta("passcode");
            }

            var res = await CallAsync(share ? "QueryShareFiles" : "QueryFileList",
                param, share ? "wohome" : "wohome", credential, ct, read: true)
                .ConfigureAwait(false);

            var arr = CloudJson.Arr(CloudJson.Get(res, "list"))
                   ?? CloudJson.Arr(CloudJson.Get(res, "data"))
                   ?? CloudJson.Arr(CloudJson.Get(res, "fileList"));

            var result = new List<CloudFile>();
            if (arr == null) return result;

            foreach (var it in arr)
            {
                var d = CloudJson.Dict(it);
                if (d == null) continue;

                result.Add(new CloudFile
                {
                    Id = CloudJson.Str(d, "fid") is string fid && fid.Length > 0
                        ? fid : CloudJson.Str(d, "fileId"),
                    Name = CloudJson.Str(d, "fileName") is string fn && fn.Length > 0
                        ? fn : CloudJson.Str(d, "name"),
                    Size = CloudJson.Num(d, "fileSize") > 0
                        ? CloudJson.Num(d, "fileSize") : CloudJson.Num(d, "size"),
                    IsDirectory = CloudJson.Num(d, "fileType") == 1
                        || CloudJson.Bool(d, "isFolder"),
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

            await AccessAsync(credential, ct).ConfigureAwait(false);

            string fid = file.Id;

            var res = await CallAsync("GetDownloadUrlV2",
                new Dictionary<string, object>
                {
                    { "type", "1" },
                    { "fidList", new[] { fid } },
                }, "wohome", credential, ct, read: true).ConfigureAwait(false);

            var list = CloudJson.List(CloudJson.Get(res, "list"));
            string url = null;

            foreach (var it in list)
            {
                var d = CloudJson.Dict(it);
                if (d == null) continue;
                if (CloudJson.Str(d, "fid") != fid) continue;
                url = CloudJson.Str(d, "downloadUrl");
                break;
            }

            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("联通云盘：未返回该文件的下载地址");

            // 联通有多个下载节点，主节点不可用时换备用域名（实测经验）
            if (url.Contains(".pan.wo.cn") && !url.Contains("hydownload"))
            {
                // 保持原样，下载器侧会处理
            }

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = UserAgent;
            spec.Headers["Referer"] = "https://pan.wo.cn/";
            return spec;
        }
    }

    /// <summary>
    /// 腾讯微云 provider。
    ///
    /// 微云的协议**不是 REST，也不是纯二进制** —— 它是"JSON RPC 套壳"：
    /// <code>
    /// POST https://www.weiyun.com/webapp/json/{protocol}/{name}?g_tk=csrf&amp;cmd={cmd}
    /// {
    ///   "req_header": "{seq, cmd, wx_openid, qq_openid, user_flag, appid:30013, version:3...}",
    ///   "req_body":   "{\"ReqMsg_body\":{\"ext_req_head\":{token_info...},
    ///                  \".weiyun.{name}MsgReq_body\":{...实际参数...}}}"
    /// }
    /// </code>
    ///
    /// 三个必知要点（照抄 weiyun.dart）：
    ///
    /// 1. **协议名与 cmd 号必须配对**：
    ///    DiskDirList(2208, weiyunQdisk) / DiskFileBatchDownload(2402) /
    ///    LibDirPathGet(26150, weiyunFileLibClient)
    ///    配对错了服务端会返回空对象而不是报错，很难排查。
    ///
    /// 2. **token_info 要从 Cookie 里推导**：
    ///    · 有 weiyun_qq_openid / weiyun_wx_openid → token_type=2（QQ/微信登录）
    ///    · 有 openid + access_token            → token_type=1
    ///    · 否则用 wy_uf                        → token_type=0（网页登录）
    ///
    /// 3. **下载要带临时 Cookie**：接口返回 cookie_name/cookie_value，
    ///    下载时必须写成 <c>Cookie: name=value</c>，否则 403。
    ///    而且这两个值要校验格式（防注入）。
    /// </summary>
    public sealed class WeiyunProvider : CloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Weiyun;

        private const string Web = "https://www.weiyun.com";
        private const int AppId = 30013;

        /// <summary>微云挑剔 UA：必须是桌面版网页 UA，移动端或客户端 UA 会被拒。</summary>
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private readonly CloudHttp _http = new CloudHttp();

        public override bool SupportsUpload => false;   // 上传协议另有一套 hash 校验，暂未实现
        public override bool SupportsRename => true;
        public override bool SupportsMove => true;
        public override bool SupportsDelete => true;
        public override bool SupportsCreateFolder => true;
        public override bool SupportsCreateShare => false;

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string cookie = RequireCookie(credential);

            try
            {
                var res = await CallAsync(credential, "DiskDirList", 2208,
                    new Dictionary<string, object>
                    {
                        { "dir_key", "" },
                        { "start", 0 },
                        { "count", 1 },
                        { "sort_field", 1 },
                        { "reverse_order", false },
                        { "get_type", 0 },
                        { "get_abstract_url", false },
                        { "get_dir_detail_info", false },
                    }, "weiyunQdisk", ct).ConfigureAwait(false);

                if (res == null)
                    throw new CloudApiException("微云：Cookie 无效或已过期");

                // 微云没有轻量的账号信息接口，用 Cookie 里的标识兜底
                return new CloudAccount { Nickname = ExtractOpenId(cookie) ?? "微云用户" };
            }
            catch (CloudApiException) { throw; }
            catch (Exception ex)
            {
                throw new CloudApiException("微云：登录态无效（" + ex.Message + "）");
            }
        }

        // ================= 打开会话 =================

        public override Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
            => throw new CloudNotSupportedException(
                "腾讯微云不支持分享解析（平台限制）");

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Weiyun,
                Mode = BrowseMode.Personal,
                Title = "我的腾讯微云",
                RootId = "",
            };
            session.SetMeta("accountId", account.Nickname);
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null || session.Mode != BrowseMode.Personal)
                throw new CloudNotSupportedException("微云：仅支持个人网盘浏览");

            var result = new List<CloudFile>();
            var seen = new HashSet<string>();
            string dirKey = parentId ?? "";

            // 分页：微云用 finish_flag 表示"到底了"
            for (int page = 0; page < 100; page++)
            {
                ct.ThrowIfCancellationRequested();

                var data = await CallAsync(credential, "DiskDirList", 2208,
                    new Dictionary<string, object>
                    {
                        { "dir_key", dirKey },
                        { "start", result.Count },
                        { "count", 500 },
                        { "sort_field", 1 },
                        { "reverse_order", false },
                        { "get_type", 0 },
                        { "get_abstract_url", false },
                        { "get_dir_detail_info", false },
                    }, "weiyunQdisk", ct).ConfigureAwait(false);

                if (data == null) break;

                int added = 0;

                // ① 子目录
                foreach (var folder in CloudJson.List(CloudJson.Get(data, "dir_list")))
                {
                    var d = CloudJson.Dict(folder);
                    if (d == null) continue;

                    string id = CloudJson.Str(d, "dir_key");
                    string name = CloudJson.Str(d, "dir_name");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                    if (!seen.Add(id)) continue;

                    result.Add(new CloudFile
                    {
                        Id = id,
                        Name = name,
                        IsDirectory = true,
                        ParentId = dirKey,
                        ModifiedAt = FormatTime(CloudJson.Str(d, "dir_mtime")),
                        Size = 0,
                    });
                    added++;
                }

                // ② 文件
                foreach (var file in CloudJson.List(CloudJson.Get(data, "file_list")))
                {
                    var d = CloudJson.Dict(file);
                    if (d == null) continue;

                    string id = CloudJson.Str(d, "file_id");
                    string name = CloudJson.Str(d, "filename");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                    if (!seen.Add(id)) continue;

                    result.Add(new CloudFile
                    {
                        Id = id,
                        Name = name,
                        IsDirectory = false,
                        ParentId = dirKey,
                        Size = CloudJson.Num(d, "file_size"),
                        ModifiedAt = FormatTime(CloudJson.Str(d, "file_mtime")),
                        HashType = "sha1",
                        HashValue = CloudJson.Str(d, "file_sha"),
                        ThumbnailUrl = CloudJson.StrPath(d, "ext_info.thumb_url"),
                    });
                    added++;
                }

                if (CloudJson.Bool(data, "finish_flag")) break;
                if (added == 0) break;   // 防死循环
            }

            return result;
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");
            if (string.IsNullOrEmpty(file.ParentId))
                throw new CloudApiException("微云：文件信息不完整，请刷新列表");

            var data = await CallAsync(credential, "DiskFileBatchDownload", 2402,
                new Dictionary<string, object>
                {
                    {
                        "file_list", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "pdir_key", file.ParentId },
                                { "file_id", file.Id },
                            }
                        }
                    },
                    { "download_type", 0 },
                }, "weiyunQdisk", ct).ConfigureAwait(false);

            var list = CloudJson.List(CloudJson.Get(data, "file_list"));
            if (list.Count != 1)
                throw new CloudApiException("微云：未返回有效下载信息，请检查文件权限");

            var item = CloudJson.Dict(list[0]);
            if (item == null || CloudJson.Num(item, "retcode") != 0)
                throw new CloudApiException("微云：未返回有效下载信息，请检查文件权限");

            string url = CloudJson.Str(item, "download_url");
            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("微云：下载地址无效");

            string cookieName = CloudJson.Str(item, "cookie_name");
            string cookieValue = CloudJson.Str(item, "cookie_value");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
                ChecksumType = file.HashType,
                ChecksumValue = file.HashValue,
            };

            spec.Headers["User-Agent"] = UserAgent;
            spec.Headers["Referer"] = Web + "/";

            // 微云下载要带临时 Cookie；格式必须校验（防头注入）
            if (!string.IsNullOrEmpty(cookieName) && !string.IsNullOrEmpty(cookieValue))
            {
                if (IsSafeCookieName(cookieName) && IsSafeCookieValue(cookieValue))
                    spec.Headers["Cookie"] = cookieName + "=" + cookieValue;
            }

            return spec;
        }

        // ================= RPC =================

        /// <summary>
        /// 发一次微云 RPC 调用，返回 req_body 里解出的业务数据。
        /// </summary>
        private async Task<Dictionary<string, object>> CallAsync(CloudCredential credential,
            string name, int cmd, Dictionary<string, object> body,
            string protocol, CancellationToken ct)
        {
            string cookie = RequireCookie(credential);
            var cookies = ParseCookies(cookie);

            string csrf = cookies.TryGetValue("wyctoken", out var tk) ? tk : "";
            var tokenInfo = BuildTokenInfo(cookies);

            // req_header
            var header = new Dictionary<string, object>
            {
                { "seq", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                { "cmd", cmd },
                { "wx_openid", tokenInfo.TryGetValue("openid", out var wo) ? wo : "" },
                { "qq_openid", tokenInfo.TryGetValue("qq_openid", out var qo) ? qo : "" },
                { "user_flag", tokenInfo.TryGetValue("token_type", out var tt) ? tt : "0" },
                { "env_id", tokenInfo.TryGetValue("env_id", out var ev) ? ev : "" },
                { "type", 1 },
                { "appid", AppId },
                { "version", 3 },
                { "major_version", 3 },
                { "minor_version", 3 },
                { "fix_version", 3 },
            };

            // req_body 是**双层嵌套的 JSON 字符串**（微云的历史包袱）
            var innerBody = new Dictionary<string, object>
            {
                {
                    "ReqMsg_body", new Dictionary<string, object>
                    {
                        {
                            "ext_req_head", new Dictionary<string, object>
                            {
                                { "token_info", tokenInfo },
                                {
                                    "language_info",
                                    new Dictionary<string, object> { { "language_type", 2052 } }
                                },
                            }
                        },
                        { ".weiyun." + name + "MsgReq_body", body },
                    }
                },
            };

            var payload = new Dictionary<string, object>
            {
                { "req_header", JsonCompat.Serialize(header) },
                { "req_body", JsonCompat.Serialize(innerBody) },
            };

            string url = Web + "/webapp/json/" + protocol + "/" + name
                       + "?g_tk=" + Uri.EscapeDataString(csrf)
                       + "&cmd=" + cmd;

            var headers = new Dictionary<string, string>
            {
                { "User-Agent", UserAgent },
                { "Referer", Web + "/disk" },
                { "Origin", Web },
                { "Cookie", cookie },
                { "Content-Type", "application/json; charset=utf-8" },
            };

            var res = await _http.PostJsonAsync(url, payload, null, headers, ct)
                .ConfigureAwait(false);

            if (res == null) return null;

            // 从 resp_body 里挖出业务数据
            var respBody = CloudJson.Get(res, "resp_body");
            object parsed = respBody is string bs ? JsonCompat.Deserialize(bs) : respBody;

            var rb = CloudJson.Dict(parsed);
            if (rb == null) return null;

            var msgBody = CloudJson.Dict(CloudJson.Get(rb, "RspMsg_body"))
                       ?? CloudJson.Dict(CloudJson.Get(rb, "RespMsg_body"))
                       ?? rb;

            // 业务数据在 ".weiyun.xxxMsgRsp_body" 里
            foreach (var kv in msgBody)
            {
                if (kv.Key.Contains("MsgRsp_body") || kv.Key.Contains("MsgResp_body"))
                    return CloudJson.Dict(kv.Value) ?? msgBody;
            }

            return msgBody;
        }

        /// <summary>从 Cookie 推导 token_info（微云鉴权的核心）。</summary>
        private static Dictionary<string, object> BuildTokenInfo(
            Dictionary<string, string> cookies)
        {
            bool hasQq = cookies.ContainsKey("weiyun_qq_openid");
            bool hasWx = cookies.ContainsKey("weiyun_wx_openid");
            bool hasOpen = cookies.ContainsKey("openid") && cookies.ContainsKey("access_token");

            var info = new Dictionary<string, object>();
            string tokenType;

            if (hasQq || hasWx)
                tokenType = "2";
            else if (hasOpen)
                tokenType = "1";
            else
                tokenType = cookies.TryGetValue("wy_uf", out var uf) && !string.IsNullOrEmpty(uf)
                    ? "0" : "0";

            info["token_type"] = tokenType;

            if (cookies.TryGetValue("weiyun_qq_openid", out var qq) && !string.IsNullOrEmpty(qq))
                info["qq_openid"] = qq;

            if (cookies.TryGetValue("weiyun_wx_openid", out var wx) && !string.IsNullOrEmpty(wx))
                info["openid"] = wx;

            if (cookies.TryGetValue("openid", out var oid) && !string.IsNullOrEmpty(oid))
                info["openid"] = oid;

            if (tokenType == "0")
            {
                // 网页登录：用 p_skey 作 login_key_value
                if (cookies.TryGetValue("p_skey", out var skey) && !string.IsNullOrEmpty(skey))
                    info["login_key_value"] = skey;
            }
            else if (tokenType == "1")
            {
                if (cookies.TryGetValue("access_token", out var at) && !string.IsNullOrEmpty(at))
                {
                    info["access_token"] = at;
                    info["login_key_value"] = at;
                }
            }

            if (cookies.TryGetValue("env_id", out var env) && !string.IsNullOrEmpty(env))
                info["env_id"] = env;

            return info;
        }

        // ================= 辅助 =================

        private static Dictionary<string, string> ParseCookies(string cookie)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(cookie)) return d;

            foreach (var part in cookie.Split(';'))
            {
                var kv = part.Trim().Split(new[] { '=' }, 2);
                if (kv.Length == 2 && kv[0].Length > 0)
                    d[kv[0]] = kv[1];
            }
            return d;
        }

        private static string ExtractOpenId(string cookie)
        {
            var d = ParseCookies(cookie);
            foreach (var key in new[] { "weiyun_qq_openid", "weiyun_wx_openid", "openid" })
                if (d.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                    return v.Length > 8 ? v.Substring(0, 8) + "…" : v;
            return null;
        }

        private static string RequireCookie(CloudCredential credential)
        {
            string cookie = credential?.Primary;
            if (string.IsNullOrEmpty(cookie))
                throw new CloudApiException("腾讯微云：未登录（需要 Cookie）");

            var d = ParseCookies(cookie);
            bool looksLoggedIn = d.ContainsKey("wy_uf")
                || d.ContainsKey("p_skey")
                || d.ContainsKey("access_token")
                || d.ContainsKey("weiyun_qq_openid")
                || d.ContainsKey("weiyun_wx_openid");

            if (!looksLoggedIn)
                throw new CloudApiException("腾讯微云：Cookie 里没有登录凭据，请重新登录");

            return cookie;
        }

        /// <summary>Cookie 名只允许字母数字下划线点和横线（防头注入）。</summary>
        private static bool IsSafeCookieName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Cookie 值不能含分号/CR/LF/NUL（防头注入）。</summary>
        private static bool IsSafeCookieValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (c == ';' || c == '\r' || c == '\n' || c == '\0') return false;
            return true;
        }

        /// <summary>微云的时间是 Unix 秒，转成可读格式。</summary>
        private static string FormatTime(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (!long.TryParse(raw, out var seconds)) return raw;
            if (seconds <= 0) return "";

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return raw;
            }
        }
    }
}
