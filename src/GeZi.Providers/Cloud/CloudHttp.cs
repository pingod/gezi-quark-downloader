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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;
using GeZi.Providers.Quark;
using GeZi.Providers.Shared;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// 网盘 provider 共用的 HTTP 小工具。
    ///
    /// 12 个平台各自重复写「拼 query、加 header、解析 JSON、取字段」
    /// 会非常啰嗦且容易不一致。这里收成一份：
    ///   · <see cref="GetJsonAsync"/> / <see cref="PostJsonAsync"/>：带 query/body 的 GET/POST
    ///   · <see cref="Get"/> / <see cref="Str"/> / <see cref="Num"/> / <see cref="Bool"/>
    ///     等取值辅助 —— 全部**容错**（取不到返回默认值，绝不抛空引用）
    ///
    /// 取值一律容错是刻意的：这些网盘的返回结构**经常变**（字段改名、
    /// 偶尔给 string 偶尔给 number）。用强类型反序列化会一改就崩；
    /// 按路径软取值，字段缺失时只是少一个信息，不会让整个操作失败。
    /// </summary>
    public static class CloudJson
    {
        // ================= 取值辅助（全部容错） =================

        /// <summary>按 key 取值（对象或 null）。</summary>
        public static object Get(object obj, string key)
        {
            if (obj is Dictionary<string, object> d && key != null)
                return d.TryGetValue(key, out var v) ? v : null;
            return null;
        }

        /// <summary>按 a.b.c 路径取值。</summary>
        public static object Path(object obj, string dottedPath)
        {
            if (string.IsNullOrEmpty(dottedPath)) return obj;

            object cur = obj;
            foreach (var seg in dottedPath.Split('.'))
            {
                cur = Get(cur, seg);
                if (cur == null) return null;
            }
            return cur;
        }

        public static Dictionary<string, object> Dict(object obj)
            => obj as Dictionary<string, object>;

        public static object[] Arr(object obj) => obj as object[];

        /// <summary>取数组；不是数组时返回空数组（省掉调用方判空）。</summary>
        public static IReadOnlyList<object> List(object obj)
            => obj as object[] ?? Array.Empty<object>();

        public static string Str(object obj, string key = null, string fallback = "")
        {
            var v = key == null ? obj : Get(obj, key);
            if (v == null) return fallback;
            if (v is string s) return s;
            return v.ToString();
        }

        /// <summary>取字符串并按 a.b 路径（常用于 obj.str('data.share_id')）。</summary>
        public static string StrPath(object obj, string dottedPath, string fallback = "")
        {
            var v = Path(obj, dottedPath);
            if (v == null) return fallback;
            return v as string ?? v.ToString();
        }

        public static long Num(object obj, string key = null, long fallback = 0)
        {
            var v = key == null ? obj : Get(obj, key);
            if (v == null) return fallback;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is double d) return (long)d;
            if (v is decimal m) return (long)m;
            if (v is bool b) return b ? 1 : 0;
            if (v is string s) return long.TryParse(s.Trim(), out var n) ? n : fallback;
            return fallback;
        }

        public static bool Bool(object obj, string key = null, bool fallback = false)
        {
            var v = key == null ? obj : Get(obj, key);
            if (v == null) return fallback;
            if (v is bool b) return b;
            if (v is string s)
            {
                var t = s.Trim().ToLowerInvariant();
                if (t == "true" || t == "1" || t == "yes") return true;
                if (t == "false" || t == "0" || t == "no" || t == "") return false;
                return fallback;
            }
            return Num(v) != 0;
        }

        /// <summary>
        /// 判断响应是否是失败。
        ///
        /// 这些平台的失败标记五花八门，统一按几种常见形态判断：
        ///   code != 0 / 200  |  success:false  |  errno != 0  |  error 字段存在
        /// 返回 null 表示"看起来是成功的"。
        /// </summary>
        public static string FailureMessage(object res, params string[] extraCodeKeys)
        {
            if (res == null) return "响应为空";

            // 常见成功码：0、200
            foreach (var key in new[] { "code", "errno", "status", "errcode", "Code", "error_code" })
            {
                var v = Get(res, key);
                if (v == null) continue;
                long n = Num(v, null, long.MinValue);
                if (n == long.MinValue) continue;
                if (n != 0 && n != 200) 
                    return FirstNonEmpty(
                        Str(res, "message"), Str(res, "msg"), Str(res, "error_msg"),
                        Str(res, "error"), Str(res, "errmsg"), "(code " + n + ")");
            }

            var succ = Get(res, "success");
            if (succ is bool b && !b)
                return FirstNonEmpty(Str(res, "message"), Str(res, "msg"), "操作失败");

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v)) return v;
            return "操作失败";
        }

        // ================= JSON =================

        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonCompat.Deserialize(json);
            }
            catch
            {
                // 有些接口返回的 content-type 是 text/html 但内容其实是 JSON，
                // 也可能返回带 BOM 或前后杂字符的 JSON —— 尝试找到第一个 { 或 [
                int i = json.IndexOfAny(new[] { '{', '[' });
                if (i > 0)
                {
                    try { return JsonCompat.Deserialize(json.Substring(i)); } catch { }
                }
                return null;
            }
        }
    }

    /// <summary>
    /// 一个轻量的 HTTP 客户端封装，供各 provider 共用。
    ///
    /// 为什么不直接用 <see cref="HttpClient"/>：
    /// 每次都要记得带 UA、Referer、Cookie、超时、错误翻译 —— 散落 12 份必然不一致。
    /// 这里收口成一份，provider 只描述"要什么"，不关心"怎么发"。
    /// </summary>
    public sealed class CloudHttp : IDisposable
    {
        private readonly HttpClient _http;
        private readonly QuarkSession _session;

        /// <summary>默认 UA（大多数平台用普通浏览器 UA 即可）。</summary>
        public const string DefaultUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        public CloudHttp()
        {
            _session = new QuarkSession(null);
            _http = _session.Http;
        }

        public HttpClient Client => _http;

        // ================= 请求 =================

        public Task<object> GetJsonAsync(string url,
            IDictionary<string, string> query = null,
            IDictionary<string, string> headers = null,
            CancellationToken ct = default)
            => SendAsync(HttpMethod.Get, url, query, null, headers, ct);

        public Task<object> PostJsonAsync(string url,
            object body,
            IDictionary<string, string> query = null,
            IDictionary<string, string> headers = null,
            CancellationToken ct = default)
            => SendAsync(HttpMethod.Post, url, query, body, headers, ct);

        /// <summary>发请求并解析 JSON。</summary>
        public async Task<object> SendAsync(HttpMethod method, string url,
            IDictionary<string, string> query = null,
            object body = null,
            IDictionary<string, string> headers = null,
            CancellationToken ct = default)
        {
            string full = BuildUrl(url, query);

            using (var req = new HttpRequestMessage(method, full))
            {
                ApplyHeaders(req, headers);

                if (body != null)
                {
                    string json = body is string s ? s : JsonCompat.Serialize(body);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        // 把 HTTP 错误翻译成带状态码的业务异常，上层好判断是否可重试
                        throw new CloudHttpException(
                            "HTTP " + (int)resp.StatusCode + " " + (resp.ReasonPhrase ?? ""),
                            (int)resp.StatusCode, text);
                    }

                    var parsed = CloudJson.Parse(text);
                    if (parsed == null && !string.IsNullOrWhiteSpace(text))
                        throw new CloudApiException("接口返回的不是有效 JSON");

                    return parsed;
                }
            }
        }

        /// <summary>发请求，返回原始文本（拉文件内容、非 JSON 接口用）。</summary>
        public async Task<string> SendStringAsync(HttpMethod method, string url,
            IDictionary<string, string> query = null,
            object body = null,
            IDictionary<string, string> headers = null,
            CancellationToken ct = default)
        {
            string full = BuildUrl(url, query);

            using (var req = new HttpRequestMessage(method, full))
            {
                ApplyHeaders(req, headers);
                if (body != null)
                {
                    string json = body is string s ? s : JsonCompat.Serialize(body);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new CloudHttpException(
                            "HTTP " + (int)resp.StatusCode + " " + (resp.ReasonPhrase ?? ""),
                            (int)resp.StatusCode, text);
                    return text;
                }
            }
        }

        private static void ApplyHeaders(HttpRequestMessage req, IDictionary<string, string> headers)
        {
            bool hasUa = false;
            bool hasAccept = false;

            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) hasUa = true;
                    if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase)) hasAccept = true;

                    // Content-Type 属于 content 而非 request
                    if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (req.Content != null)
                        {
                            try { req.Content.Headers.ContentType =
                                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(kv.Value); } catch { }
                        }
                        continue;
                    }

                    try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value ?? ""); } catch { }
                }
            }

            if (!hasUa) req.Headers.TryAddWithoutValidation("User-Agent", DefaultUa);
            if (!hasAccept) req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        }

        private static string BuildUrl(string url, IDictionary<string, string> query)
        {
            if (query == null || query.Count == 0) return url;

            var sb = new StringBuilder(url);
            sb.Append(url.Contains("?") ? "&" : "?");
            bool first = true;
            foreach (var kv in query)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (!first) sb.Append("&");
                sb.Append(Uri.EscapeDataString(kv.Key)).Append("=")
                  .Append(Uri.EscapeDataString(kv.Value ?? ""));
                first = false;
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { }
        }
    }

    /// <summary>HTTP 层错误（带状态码，便于判断可重试）。</summary>
    public sealed class CloudHttpException : CloudApiException
    {
        public int Status { get; }
        public string Body { get; }

        public CloudHttpException(string message, int status, string body = null)
            : base(message, "http_" + status, status)
        {
            Status = status;
            Body = body;
        }

        /// <summary>限流类错误（可退避重试）。</summary>
        public bool IsRateLimited => Status == 429;

        /// <summary>服务端临时错误（可重试）。</summary>
        public bool IsTransient => Status >= 500 || Status == 408 || Status == 429;
    }
}
