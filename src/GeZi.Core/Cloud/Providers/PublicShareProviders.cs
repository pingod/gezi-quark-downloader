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
    /// 蓝奏云 provider。
    ///
    /// 协议特点（对照 wenxi 的 lanzou.dart / lanzou_protocol.dart）：
    /// - **不需要登录就能下载分享**（部分加密分享需要 `pwd`）
    /// - 两步取链：
    ///   ① POST <c>/ajaxm.php</c>（或新版 <c>/ajaxfile.php</c>）
    ///      返回 <c>{dom, url}</c> —— dom 是下载域名，url 是相对路径
    ///   ② 拼成 <c>https://{dom}/file/{url}</c> 再请求一次拿真实 302
    /// - 域名变体极多（lanzou/lanzous/lanzoux/lanzoui…），必须从
    ///   分享页 HTML 里动态解析，不能硬编码
    /// - **不支持分享解析**（只能开别人的分享，不能列自己的盘）且**不支持创建分享**
    /// - 文件大小不精确（平台只给近似值）
    ///
    /// 本实现覆盖「打开分享 → 取直链」，这是蓝奏最常用的场景。
    /// 个人盘需要登录 Cookie（ylogin + phpdisk_info），列目录走 web 后端，
    /// 接口易变，因此未实现时给出明确提示而不是假成功。
    /// </summary>
    public sealed class LanzouProvider : CloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Lanzou;

        private readonly CloudHttp _http = new CloudHttp();

        // 蓝奏不支持创建分享、不支持上传（以本实现范围为准）
        public override bool SupportsUpload => false;
        public override bool SupportsRename => false;
        public override bool SupportsMove => false;
        public override bool SupportsDelete => false;
        public override bool SupportsCreateFolder => false;
        public override bool SupportsCreateShare => false;

        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            string cookie = credential?.Primary ?? "";
            if (string.IsNullOrEmpty(cookie))
                return new CloudAccount { Nickname = "蓝奏云（游客）" };

            if (!cookie.Contains("ylogin="))
                throw new CloudApiException("蓝奏云：Cookie 缺少 ylogin，请重新登录");

            return new CloudAccount { Nickname = "蓝奏云用户" };
        }

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.Url))
                throw new CloudApiException("蓝奏云：需要分享链接");

            // 分享 ID 从 URL 的 /iXXXX 或 /bXXXX 里取
            string shareId = ExtractShareId(link.Url);
            if (string.IsNullOrEmpty(shareId))
                throw new CloudApiException("蓝奏云：链接格式无法识别（应形如 lanzou*.com/iXXXXXX）");

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Lanzou,
                Mode = BrowseMode.Share,
                Title = "蓝奏云分享 " + shareId,
                RootId = "0",
                SourceLink = link,
            };
            session.SetMeta("shareId", shareId);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("host", new Uri(link.Url).Host);
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            // 蓝奏的"个人盘"要登录 web 后端、接口易变；先不假装能用
            throw new CloudNotSupportedException(
                "蓝奏云暂不支持浏览个人网盘（仅支持分享链接下载）");
        }

        public override Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            // 蓝奏分享是"一个链接一个文件或一个文件夹页面"，
            // 单文件分享没有必要先列表 —— 直接把文件本身作为唯一条目返回。
            if (session == null || session.Mode != BrowseMode.Share)
                return Task.FromResult<IReadOnlyList<CloudFile>>(Array.Empty<CloudFile>());

            var file = new CloudFile
            {
                Id = session.Meta("shareId"),
                Name = "蓝奏云文件 " + session.Meta("shareId"),
                IsDirectory = false,
                ParentId = "0",
            };
            return Task.FromResult<IReadOnlyList<CloudFile>>(new[] { file });
        }

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null || session.Mode != BrowseMode.Share)
                throw new CloudNotSupportedException("蓝奏云：仅支持分享下载");

            string host = session.Meta("host");
            string shareId = session.Meta("shareId");
            string passcode = session.Meta("passcode");

            string baseUrl = "https://" + host;

            // ① 先拉分享页，取出页面里隐藏的 form 参数（sign / task / kd 等）
            string sharePage = await _http.SendStringAsync(
                System.Net.Http.HttpMethod.Get,
                baseUrl + "/" + shareId,
                null, null,
                new Dictionary<string, string>
                {
                    { "User-Agent", Ua },
                    { "Referer", baseUrl + "/" },
                }, ct).ConfigureAwait(false);

            var form = ExtractFormParams(sharePage);

            // need pwd → 带上 pwd 再请求一次
            if (form.ContainsKey("pwd") || sharePage.Contains("pwd"))
            {
                if (string.IsNullOrEmpty(passcode))
                    throw new CloudApiException("蓝奏云：该分享需要提取码");

                form["pwd"] = passcode;

                sharePage = await _http.SendStringAsync(
                    System.Net.Http.HttpMethod.Post,
                    baseUrl + "/ajaxm.php?file=" + shareId,
                    null,
                    string.Join("&", form.Select(kv =>
                        Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))),
                    new Dictionary<string, string>
                    {
                        { "User-Agent", Ua },
                        { "Referer", baseUrl + "/" + shareId },
                        { "Content-Type", "application/x-www-form-urlencoded" },
                    }, ct).ConfigureAwait(false);
            }

            // ② 解析 ajax 返回的 {dom, url}
            var res = CloudJson.Parse(sharePage);
            string dom = CloudJson.Str(res, "dom");
            string path = CloudJson.Str(res, "url");

            if (string.IsNullOrEmpty(dom) || string.IsNullOrEmpty(path))
            {
                // 新版接口返回的是 ajaxfile.php 的 JSON
                var rectified = await TryAjaxFileAsync(baseUrl, shareId, form, ct)
                    .ConfigureAwait(false);
                dom = CloudJson.Str(rectified, "dom");
                path = CloudJson.Str(rectified, "url");

                if (string.IsNullOrEmpty(dom) || string.IsNullOrEmpty(path))
                    throw new CloudApiException(
                        "蓝奏云：取链失败（可能提取码错误、分享失效，或页面结构已变）");
            }

            // ③ 拼出真实下载地址
            string downloadUrl = dom.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? dom.TrimEnd('/') + "/file/" + path.TrimStart('/')
                : "https://" + dom.TrimEnd('/') + "/file/" + path.TrimStart('/');

            var spec = new DownloadSpec
            {
                Url = downloadUrl,
                FileName = file.Name,
            };
            spec.Headers["User-Agent"] = Ua;
            spec.Headers["Referer"] = baseUrl + "/";
            return spec;
        }

        private async Task<object> TryAjaxFileAsync(string baseUrl, string shareId,
            Dictionary<string, string> form, CancellationToken ct)
        {
            try
            {
                string body = string.Join("&", form.Select(kv =>
                    Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

                string text = await _http.SendStringAsync(
                    System.Net.Http.HttpMethod.Post,
                    baseUrl + "/ajaxfile.php?file=" + shareId,
                    null, body,
                    new Dictionary<string, string>
                    {
                        { "User-Agent", Ua },
                        { "Referer", baseUrl + "/" + shareId },
                        { "Content-Type", "application/x-www-form-urlencoded" },
                    }, ct).ConfigureAwait(false);

                return CloudJson.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从分享页 HTML 里抽出表单参数（sign / task / kd / pwd…）。</summary>
        private static Dictionary<string, string> ExtractFormParams(string html)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(html)) return result;

            // <input type="hidden" name="X" value="Y">
            var re = new System.Text.RegularExpressions.Regex(
                @"<input[^>]*name=[""']([^""']+)[""'][^>]*value=[""']([^""']*)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in re.Matches(html))
                result[m.Groups[1].Value] = m.Groups[2].Value;

            // 有些版本把参数写在 JS 变量里：var xxx = 'value';
            var reJs = new System.Text.RegularExpressions.Regex(
                @"(?:var\s+)?(sign|task|kd|pwd)\s*=\s*['""]([^'""]*)['""]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in reJs.Matches(html))
            {
                if (!result.ContainsKey(m.Groups[1].Value))
                    result[m.Groups[1].Value] = m.Groups[2].Value;
            }

            return result;
        }

        private static string ExtractShareId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(url, @"/(?:i|b)([A-Za-z0-9]{6,})");
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    /// <summary>
    /// 城通网盘 provider。
    ///
    /// 协议特点（对照 ctfile.dart）：
    /// - 有两条路：**开放 API**（rest.ctfile.com/v1，需 session token）
    ///   和**网页接口**（webapi.ctfile.com/get_down_url.php，走 HTML 解析）
    /// - 公开分享用 <c>/v1/public/file/fetch_url</c>
    /// - 支持存储空间切换（supportsPersonalSpaces）
    /// - 取链后必须带固定 UA + Referer
    ///
    /// 本实现覆盖「分享取链」（最常用），个人盘浏览需要 session，
    /// 端点参数易变，未实现时明确提示。
    /// </summary>
    public sealed class CtfileProvider : TokenCloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Ctfile;

        protected override string ApiBase => "https://rest.ctfile.com/v1";
        protected override string TokenUrl => "https://rest.ctfile.com/p8/public/login/login";

        private readonly CloudHttp _http = new CloudHttp();

        public override bool SupportsUpload => false;
        public override bool SupportsCreateShare => false;

        protected override async Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct)
        {
            // 城通刷新接口未公开文档；这里用 refresh 换 access 的常见形态尝试
            var body = new Dictionary<string, object>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", Refresh(credential) },
            };
            var res = await _http.PostJsonAsync(TokenUrl, body, null,
                new Dictionary<string, string> { { "User-Agent", UserAgent } }, ct)
                .ConfigureAwait(false);
            ApplyTokenResponse(credential, res);
        }

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            if (credential == null || string.IsNullOrEmpty(credential.Primary))
                return new CloudAccount { Nickname = "城通网盘（游客）" };

            try
            {
                var res = await _http.GetJsonAsync(ApiBase + "/user/info/profile",
                    new Dictionary<string, string> { { "session", credential.Primary } },
                    Headers(credential), ct).ConfigureAwait(false);

                return new CloudAccount
                {
                    Nickname = CloudJson.StrPath(res, "data.username", "城通用户"),
                };
            }
            catch (Exception ex)
            {
                throw new CloudApiException("城通网盘：登录态无效（" + ex.Message + "）");
            }
        }

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("城通网盘：分享链接缺少文件 ID");

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Ctfile,
                Mode = BrowseMode.Share,
                Title = "城通分享 " + link.ShareId,
                RootId = "0",
                SourceLink = link,
            };
            session.SetMeta("fid", link.ShareId);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            throw new CloudNotSupportedException(
                "城通网盘暂不支持浏览个人网盘（仅支持分享链接下载）");
        }

        public override Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null || session.Mode != BrowseMode.Share)
                return Task.FromResult<IReadOnlyList<CloudFile>>(Array.Empty<CloudFile>());

            var file = new CloudFile
            {
                Id = session.Meta("fid"),
                Name = "城通文件 " + session.Meta("fid"),
                ParentId = "0",
            };
            return Task.FromResult<IReadOnlyList<CloudFile>>(new[] { file });
        }

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null || session.Mode != BrowseMode.Share)
                throw new CloudNotSupportedException("城通网盘：仅支持分享下载");

            string fid = session.Meta("fid");
            string passcode = session.Meta("passcode");

            // 走网页取链接口（开放 API 需要 session，分享场景通常没有）
            var query = new Dictionary<string, string>
            {
                { "path", fid },
                { "passcode", passcode ?? "" },
                { "user", "" },
            };

            string text = await _http.SendStringAsync(
                System.Net.Http.HttpMethod.Get,
                "https://webapi.ctfile.com/get_down_url.php",
                query, null,
                new Dictionary<string, string>
                {
                    { "User-Agent", Ua },
                    { "Referer", "https://www.ctfile.com/" },
                }, ct).ConfigureAwait(false);

            // 返回可能是 JSON，也可能是 HTML 里嵌的变量
            string url = null;
            var json = CloudJson.Parse(text);
            if (json != null)
            {
                url = CloudJson.Str(json, "downurl");
                if (string.IsNullOrEmpty(url)) url = CloudJson.Str(json, "url");
            }

            if (string.IsNullOrEmpty(url))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"(?:downurl|url)\s*[=:]\s*['""]([^'""]+)['""]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) url = m.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("城通网盘：取链失败（分享可能失效或需要提取码）");

            var spec = new DownloadSpec { Url = url, FileName = file.Name };
            spec.Headers["User-Agent"] = Ua;
            spec.Headers["Referer"] = "https://www.ctfile.com/";
            return spec;
        }

        private Dictionary<string, string> Headers(CloudCredential credential)
        {
            var h = new Dictionary<string, string> { { "User-Agent", Ua } };
            if (!string.IsNullOrEmpty(credential?.Primary))
                h["Cookie"] = "ctfile_session=" + credential.Primary;
            return h;
        }

        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    }
}
