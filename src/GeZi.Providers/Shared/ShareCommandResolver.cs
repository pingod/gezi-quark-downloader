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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Support;

namespace GeZi.Providers.Shared
{
    /// <summary>
    /// 分享口令解析结果。
    /// </summary>
    public sealed class ShareCommandResult
    {
        /// <summary>是否解析成功。</summary>
        public bool Success { get; set; }

        /// <summary>解析出来的分享链接（成功时非空）。</summary>
        public string Url { get; set; }

        /// <summary>分享 ID（成功时非空）。</summary>
        public string ShareId { get; set; }

        /// <summary>提取码（没有就是空串）。</summary>
        public string Passcode { get; set; }

        /// <summary>网盘类型（解析不出来就是 null）。</summary>
        public PanKind? Kind { get; set; }

        /// <summary>失败原因（成功时为空）。</summary>
        public string Reason { get; set; }

        /// <summary>来源标记（"xunlei" / "starsea"），只用于日志排查。</summary>
        public string Source { get; set; }
    }

    /// <summary>
    /// 分享口令解析器：把「一句口令」换成一个能直接打开的分享链接。
    /// <para>
    /// <b>为什么要做：</b>网盘 App 里分享出来的是一段中文口令（不是 URL），
    /// 用户粘进来以后如果不处理，就只会被当成一段普通文字，界面上什么也不会发生
    /// （以前就是这个毛病：只有夸克口令被认出来，还弹个框说「这是夸克口令，无法解析」，
    /// 于是用户看到的是「程序把我输入的话当空气了」）。
    /// </para>
    /// <para>
    /// <b>两条解析路线：</b>
    /// ① 迅雷官方「口令跳转」接口（零密钥、官方提供）：
    ///    <c>api-shoulei-ssl.xunlei.com/xlppc.searcher.api/jump</c>，
    ///    参照 refs/wenxi <c>lib/domain/xunlei_kouling.dart</c>；
    /// ② 夸克 / UC 走第三方「星海」解析服务（refs/YunX <c>StarSeaApi.kt</c>）。
    /// </para>
    /// <para>
    /// <b>隐私：</b>口令原文会发到第三方（对方服务端会写访问日志），
    /// 所以这里 <b>绝不把口令原文写进日志</b>，只记录「长度 + HTTP 码 + 原因」。
    /// 用户在设置里不登录任何东西也不需要 —— 这条路只用公共接口。
    /// </para>
    /// <para>
    /// <b>阿里不做口令：</b>阿里早已下线口令功能，官方没有口令↔链接的换算入口。
    /// </para>
    /// </summary>
    public static class ShareCommandResolver
    {
        /// <summary>口令最长多少字（超过就不当口令处理）。</summary>
        public const int MaxCommandLength = 120;

        private const string StarSeaBase = "https://starsea-api.qwq.pics";
        private const string StarSeaResolvePath = "/api/resolve";
        private const string StarSeaApiKey = "8bfbe163c670ae500aca5f3b2f060ecc8463e1eb1691ba8d";

        private const string XunleiJumpApi = "https://api-shoulei-ssl.xunlei.com/xlppc.searcher.api/jump";
        private const string XunleiOrigin = "https://sl-m-ssl.xunlei.com";
        private const string XunleiUa =
            "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/108.0.5359.215 Safari/537.36 TBC/1.3.3.999 Thunder/12.4.4.3740";

        // 口令里常见的装饰符（引号、括号、书名号…），两端去掉再判断。
        private static readonly char[] Decorations =
        {
            '「', '」', '『', '』', '【', '】', '《', '》', '〈', '〉',
            '“', '”', '‘', '’', '"', '\'', '`', '(', ')', '（', '）',
            '[', ']', '{', '}', '<', '>', '.', ',', '。', '，', ';', '；',
            ':', '：', '!', '！', '?', '？', '、', '|',
        };

        private static readonly Regex XunleiShareIdInLocation =
            new Regex(@"https?://pan\.xunlei\.com/s/([A-Za-z0-9_-]+)", RegexOptions.Compiled);

        private static readonly Regex XunleiPasscodeInLocation =
            new Regex(@"[?&]pwd=([A-Za-z0-9]+)", RegexOptions.Compiled);

        /// <summary>
        /// 分享相关的关键词 —— 出现这些字样，说明用户粘的极可能是「口令文案」
        /// （例如「【标题】口令：xxxx 提取码：yyyy」这种带说明的整段分享文案）。
        /// </summary>
        private static readonly Regex CommandKeywords = new Regex(
            @"提取码|提取碼|访问码|訪問碼|分享码|口令|密码|密碼|链接|連結|" +
            @"pan\.|quark|uc\.cn|aliyundrive|alipan|xunlei|夸克|阿里|阿里云|迅雷|UC",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 夸克口令的固定形态：<c>/~469d3M9zTu~:/</c>（与 ShareUrlParser 的正则一致）。
        /// 它不是 URL，但确实是口令 —— 得走口令解析，不能当普通文字。
        /// </summary>
        private static readonly Regex QuarkKoulingShape =
            new Regex(@"^/~([A-Za-z0-9]{4,32})~:?/?$", RegexOptions.Compiled);

        /// <summary>
        /// 判断一段文本「像不像口令」。
        /// <para>
        /// 判定口径分三档（比"有中文就是口令"严得多，否则用户随便打句「今天天气不错」
        /// 也会被发到第三方服务商，白跑一次请求还会被限流）：
        /// ① 紧凑中文口令（去掉装饰符后全是中文/数字/字母、长度 ≤64、至少一个汉字）；
        /// ② 含分享相关关键词（提取码 / 口令 / 链接 / pan. / 夸克 / 迅雷…）；
        /// ③ 长度 6~40 的纯字母数字紧凑串（网盘 App 发的那种短码）。
        /// </para>
        /// <para>
        /// 另外：含 http:// 或 https:// 的一律**不算口令** —— 那已经是链接了，
        /// 该走正常解析而不是口令解析。
        /// </para>
        /// </summary>
        public static bool LooksLikeCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var t = text.Trim();
            if (t.Length == 0 || t.Length > MaxCommandLength)
                return false;
            if (t.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (t.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // ① 紧凑中文口令
            if (LooksLikeChineseCommand(t))
                return true;

            // ①b 夸克口令形态（/~xxx~:/）
            if (QuarkKoulingShape.IsMatch(t))
                return true;

            // ② 含分享关键词
            if (CommandKeywords.IsMatch(t))
                return true;

            // ③ 纯字母数字短串
            if (t.Length >= 6 && t.Length <= 40)
            {
                bool allAlnum = true;
                foreach (var ch in t)
                {
                    bool ok = (ch >= '0' && ch <= '9')
                              || (ch >= 'a' && ch <= 'z')
                              || (ch >= 'A' && ch <= 'Z');
                    if (!ok) { allAlnum = false; break; }
                }
                if (allAlnum)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判断一段文本「像不像中文口令」（全是中文/数字/字母的紧凑串）。
        /// <para>
        /// 口径对齐 refs/wenxi <c>looksLike</c>：去掉两端装饰符后长度 1~64，
        /// 每个字符必须落在中文 / 数字 / ASCII 字母内，且至少有一个中日韩字符。
        /// 这种串是迅雷口令的典型形态，优先走迅雷官方接口（零第三方依赖）。
        /// </para>
        /// </summary>
        public static bool LooksLikeChineseCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var t = Normalize(text);
            if (t.Length == 0 || t.Length > 64)
                return false;
            bool hasCjk = false;
            foreach (var ch in t)
            {
                bool ok = (ch >= 0x4e00 && ch <= 0x9fff)
                          || (ch >= 0x3400 && ch <= 0x4dbf)
                          || (ch >= '0' && ch <= '9')
                          || (ch >= 'a' && ch <= 'z')
                          || (ch >= 'A' && ch <= 'Z');
                if (!ok)
                    return false;
                if ((ch >= 0x4e00 && ch <= 0x9fff) || (ch >= 0x3400 && ch <= 0x4dbf))
                    hasCjk = true;
            }
            return hasCjk;
        }

        /// <summary>去掉首尾装饰符并 trim。</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var t = text.Trim();
            while (t.Length > 0 && Array.IndexOf(Decorations, t[0]) >= 0)
                t = t.Substring(1);
            while (t.Length > 0 && Array.IndexOf(Decorations, t[t.Length - 1]) >= 0)
                t = t.Substring(0, t.Length - 1);
            return t.Trim();
        }

        /// <summary>
        /// 解析一段口令。
        /// <para>
        /// 顺序：中文口令先试迅雷官方接口（零第三方、结果更准），
        /// 再试星海服务（夸克 / UC）。两条都失败时返回失败原因，绝不静默。
        /// </para>
        /// </summary>
        public static async Task<ShareCommandResult> ResolveAsync(string text, CancellationToken cancel = default)
        {
            var raw = (text ?? "").Trim();
            if (raw.Length == 0)
                return Fail("口令是空的");
            if (raw.Length > MaxCommandLength)
                return Fail("这段话太长了，不像口令");

            ShareCommandResult last = null;

            if (LooksLikeChineseCommand(raw))
            {
                last = await ResolveXunleiAsync(raw, cancel).ConfigureAwait(false);
                if (last != null && last.Success)
                    return last;
            }

            var star = await ResolveStarSeaAsync(raw, cancel).ConfigureAwait(false);
            if (star != null && star.Success)
                return star;
            if (star != null && !string.IsNullOrEmpty(star.Reason))
                last = star;

            if (!LooksLikeChineseCommand(raw))
            {
                // 不是紧凑中文口令，但第一次没走迅雷官方，这里补一次。
                var xl = await ResolveXunleiAsync(raw, cancel).ConfigureAwait(false);
                if (xl != null && xl.Success)
                    return xl;
                if (xl != null && !string.IsNullOrEmpty(xl.Reason))
                    last = xl;
            }

            if (last == null || string.IsNullOrEmpty(last.Reason))
                last = Fail("这条口令没能解析出分享链接。");
            return last;
        }

        // ---------------- 迅雷官方口令跳转 ----------------

        private static async Task<ShareCommandResult> ResolveXunleiAsync(string text, CancellationToken cancel)
        {
            var keyword = Normalize(text);
            if (keyword.Length == 0)
                return Fail("口令是空的");

            var url = XunleiJumpApi
                      + "?noredirect=1&t=20&wd=" + Uri.EscapeDataString(keyword)
                      + "&tn=15007414_5_dg&src=lm&ls=sm3016480&lm_extend=ctype:31";

            string body;
            try
            {
                using (var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                })
                {
                    NetworkOptions.ApplyTo(handler);
                    using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) })
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.TryAddWithoutValidation("User-Agent", XunleiUa);
                        req.Headers.TryAddWithoutValidation("Referer", XunleiOrigin + "/");
                        req.Headers.TryAddWithoutValidation("Origin", XunleiOrigin);
                        using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancel)
                                   .ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                                return Fail("迅雷口令解析失败（HTTP " + (int)resp.StatusCode + "）");
                            body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 只记类型不记口令内容
                return Fail("迅雷口令解析失败（" + ex.GetType().Name + "）");
            }

            var root = PanJson.Deserialize(body);
            var dict = PanJson.AsDict(root);
            var ext = PanJson.AsDict(PanJson.Get(dict, "ext"));
            var koulingType = PanJson.AsString(PanJson.Get(ext, "kouling_type"));
            var location = PanJson.AsString(PanJson.Get(dict, "location"));
            if (string.IsNullOrEmpty(location))
                location = PanJson.AsString(PanJson.Get(dict, "url"));

            if (!string.IsNullOrEmpty(koulingType)
                && !koulingType.Equals("share_page", StringComparison.OrdinalIgnoreCase))
                return Fail("这条口令不是网盘分享口令");

            if (string.IsNullOrEmpty(location))
                return Fail("这条迅雷口令没有对应的网盘分享");

            var idM = XunleiShareIdInLocation.Match(location);
            if (!idM.Success)
                return Fail("这条迅雷口令没有对应的网盘分享");

            var passcode = "";
            var pwdM = XunleiPasscodeInLocation.Match(location);
            if (pwdM.Success)
                passcode = pwdM.Groups[1].Value;

            var shareId = idM.Groups[1].Value;
            var shareUrl = "https://pan.xunlei.com/s/" + shareId;
            if (passcode.Length > 0)
                shareUrl += "?pwd=" + passcode;

            return new ShareCommandResult
            {
                Success = true,
                Url = shareUrl,
                ShareId = shareId,
                Passcode = passcode,
                Kind = PanKind.Xunlei,
                Source = "xunlei",
            };
        }

        // ---------------- 星海（夸克 / UC） ----------------

        private static async Task<ShareCommandResult> ResolveStarSeaAsync(string text, CancellationToken cancel)
        {
            var payload = "{\"clipboard\":" + PanJson.Serialize(text) + ",\"platform\":\"auto\",\"step\":\"parse\"}";

            string body;
            int code;
            try
            {
                using (var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                })
                {
                    NetworkOptions.ApplyTo(handler);
                    using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) })
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, StarSeaBase + StarSeaResolvePath);
                        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                        req.Headers.TryAddWithoutValidation("X-API-Key", StarSeaApiKey);
                        req.Headers.TryAddWithoutValidation("Accept", "application/json");
                        using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancel)
                                   .ConfigureAwait(false))
                        {
                            code = (int)resp.StatusCode;
                            body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail("口令解析服务没连上（" + ex.GetType().Name + "）");
            }

            if (code == 429)
                return Fail("口令解析服务限流了，请稍后再试");
            if (code == 401)
                return Fail("口令解析服务鉴权失败（服务商的 Key 可能换了）");
            if (code == 413)
                return Fail("这段话太长了，口令最长 120 字");
            if (code == 400)
                return Fail("这段文字不是有效的口令");
            if (code == 404)
                return Fail("口令解析服务暂时不可用");
            if (code < 200 || code >= 300)
                return Fail("口令解析失败（HTTP " + code + "）");

            var root = PanJson.Deserialize(body);
            var dict = PanJson.AsDict(root);
            var ok = PanJson.AsBool(PanJson.Get(dict, "success"));
            var data = PanJson.AsDict(PanJson.Get(dict, "data"));
            var pwdId = PanJson.AsString(PanJson.Get(data, "pwdId"));
            var platform = PanJson.AsString(PanJson.Get(data, "platform"));
            var msg = PanJson.AsString(PanJson.Get(dict, "msg"));

            if (!ok || string.IsNullOrEmpty(pwdId))
            {
                if (!string.IsNullOrEmpty(msg))
                    return Fail(msg);
                return Fail("口令解析服务没认出这条口令");
            }

            PanKind kind;
            string host;
            if (!string.IsNullOrEmpty(platform) && platform.Equals("uc", StringComparison.OrdinalIgnoreCase))
            {
                kind = PanKind.Uc;
                host = "drive.uc.cn";
            }
            else
            {
                kind = PanKind.Quark;
                host = "pan.quark.cn";
            }

            return new ShareCommandResult
            {
                Success = true,
                Url = "https://" + host + "/s/" + pwdId,
                ShareId = pwdId,
                Passcode = "",
                Kind = kind,
                Source = "starsea",
            };
        }

        private static ShareCommandResult Fail(string reason)
        {
            return new ShareCommandResult { Success = false, Reason = reason };
        }
    }
}
