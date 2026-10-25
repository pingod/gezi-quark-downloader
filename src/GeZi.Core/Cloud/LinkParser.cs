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
using System.Text.RegularExpressions;

namespace GeZi.Core.Cloud
{
    /// <summary>
    /// 链接识别与分享 ID 提取。
    ///
    /// 这是"多网盘"能力的第一道门：用户粘贴一段文本，要判断出
    /// 「哪个网盘 + 分享 ID + 提取码」。参照 wenxi 的 <c>ParsedLink</c>/
    /// <c>CloudPlatform.FromHost</c> 做法，但按各平台实际的 URL 形态做了适配。
    ///
    /// 设计要点：
    /// - **先过一遍提取码**，因为分享文案里提取码可能出现在链接前面
    ///   （"提取码：abcd 链接：https://…"），后解析链接就漏了。
    /// - **域名识别走 CloudPlatform.FromHost**（后缀匹配），
    ///   不在这里硬编码域名清单，避免两处维护。
    /// - 识别不出网盘但 URL 指向某个已知域时，返回
    ///   <see cref="LinkKind.UnsupportedCloud"/>，界面可以提示
    ///   "该网盘暂不支持"而不是"无法识别的链接"。
    /// </summary>
    public static class LinkParser
    {
        // 各平台的分享 ID 路径形态。注意顺序：更具体的规则放前面。
        private static readonly (CloudPlatform Platform, Regex Pattern)[] SharePatterns =
        {
            // 夸克 / UC：/s/xxxxxx
            (CloudPlatform.Quark, new Regex(@"/s/([A-Za-z0-9_-]{6,})", RegexOptions.Compiled)),
            (CloudPlatform.Uc,    new Regex(@"/s/([A-Za-z0-9_-]{6,})", RegexOptions.Compiled)),

            // 阿里云盘：/s/xxxxxx（新版 alipan.com 同样）
            (CloudPlatform.Aliyun, new Regex(@"/s/([A-Za-z0-9]{8,})", RegexOptions.Compiled)),

            // 123 网盘：/s/xxxx 或 /s/xxxx-xxxx
            (CloudPlatform.Pan123, new Regex(@"/s/([A-Za-z0-9-]{4,})", RegexOptions.Compiled)),

            // 天翼云盘：/t/xxxxx 或 cloud.189.cn/t/xxx
            (CloudPlatform.Tianyi, new Regex(@"/t/([A-Za-z0-9]+)", RegexOptions.Compiled)),

            // 移动云盘：/share/xxx 或 /h5/share/xxx
            (CloudPlatform.C139, new Regex(@"/(?:h5/)?share/([A-Za-z0-9_-]+)", RegexOptions.Compiled)),

            // 联通云盘：/s/xxx
            (CloudPlatform.Wopan, new Regex(@"/s/([A-Za-z0-9_-]+)", RegexOptions.Compiled)),

            // 百度网盘：/s/1xxxxx（带 1 前缀是显著特征），也支持 /surl=xxx
            (CloudPlatform.Baidu, new Regex(@"/s/1([A-Za-z0-9_-]{5,})", RegexOptions.Compiled)),

            // 迅雷云盘：/s/xxx
            (CloudPlatform.Xunlei, new Regex(@"/s/([A-Za-z0-9_-]+)", RegexOptions.Compiled)),

            // 115：/s/xxxxx
            (CloudPlatform.Pan115, new Regex(@"/s/([A-Za-z0-9]+)", RegexOptions.Compiled)),

            // 蓝奏云：/i/xxxxx 或 /b00xxxxx
            (CloudPlatform.Lanzou, new Regex(@"/i([A-Za-z0-9]{6,})", RegexOptions.Compiled)),

            // 城通：/f/xxxxxxx-xxxxx-xxxxx
            (CloudPlatform.Ctfile, new Regex(@"/f/([A-Za-z0-9-]+)", RegexOptions.Compiled)),

            // 小飞机：/s/xxxx
            (CloudPlatform.Feijipan, new Regex(@"/s/([A-Za-z0-9-]+)", RegexOptions.Compiled)),

            // PikPak：/s/xxxx
            (CloudPlatform.PikPak, new Regex(@"/s/([A-Za-z0-9_-]+)", RegexOptions.Compiled)),

            // 光鸭：/s/xxxx
            (CloudPlatform.Guangya, new Regex(@"/s/([A-Za-z0-9]+)", RegexOptions.Compiled)),
        };

        // 提取码的各种写法：
        //   提取码：abcd / 提取码 abcd / 密码:abcd / 访问码：abcd / pwd=abcd / ?pwd=abcd
        // ⚠️ 负向后顾 (?<![/A-Za-z0-9])：分享 ID 本身可能就叫 pwd1234
        //    （https://pan.quark.cn/s/pwd1234），不加的话会把 pwd1234 当提取码解析成 1234。
        private static readonly Regex PasscodePattern = new Regex(
            @"(?<![/A-Za-z0-9])(?:提取码|提取碼|密码|密碼|访问码|訪問碼|口令|pwd|PWD|Pwd)\s*[=:：]?\s*([A-Za-z0-9]{1,12})",
            RegexOptions.Compiled);

        // 夸克 PC 客户端口令：/~xxxx~:/
        private static readonly Regex QuarkKouling = new Regex(
            @"(?:^|\s)/~([A-Za-z0-9]{4,32})~", RegexOptions.Compiled);

        private static readonly Regex UrlPattern = new Regex(
            @"https?://[^\s""'<>）)】\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MagnetPattern = new Regex(
            @"^magnet:\?xt=urn:btih:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 解析用户粘贴的一段文本，识别出链接类型与平台。
        /// </summary>
        public static ParsedLink Parse(string input)
        {
            var result = new ParsedLink { Source = input ?? "" };
            if (string.IsNullOrWhiteSpace(input))
            {
                result.Kind = LinkKind.Direct;
                return result;
            }

            string text = input.Trim();

            // ---- 1) 磁力链 ----
            if (MagnetPattern.IsMatch(text))
            {
                result.Kind = LinkKind.Magnet;
                result.Url = text;
                return result;
            }

            // ---- 2) 夸克口令（无链接可用，只识别出来给提示）----
            var kouling = QuarkKouling.Match(text);
            if (kouling.Success)
            {
                string code = kouling.Groups[1].Value;

                // 口令里可能同时带了链接
                var urlInText = UrlPattern.Match(text);
                if (!urlInText.Success)
                {
                    result.Kind = LinkKind.UnsupportedCloud;
                    result.Platform = CloudPlatform.Quark;
                    result.ShareId = null;
                    // 用 Source 保存口令原文，界面据此提示"请改用分享链接"
                    result.Source = text;
                    return result;
                }
            }

            // ---- 3) 提取码（先于链接解析，因为可能出现在链接之前）----
            var pwd = PasscodePattern.Match(text);
            if (pwd.Success)
                result.Passcode = pwd.Groups[1].Value;

            // ---- 4) 取出第一个 URL ----
            var m = UrlPattern.Match(text);
            if (!m.Success)
            {
                // 整段没有任何 URL → 当作直链尝试（可能是裸域名）
                if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    result.Kind = LinkKind.Direct;
                    result.Url = text;
                }
                else
                {
                    result.Kind = LinkKind.Direct;
                    result.Url = text;
                }
                return result;
            }

            string url = m.Value.TrimEnd('。', '，', ',', '.', '；', ';');
            result.Url = url;

            // ---- 5) 识别平台 ----
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                result.Kind = LinkKind.Direct;
                return result;
            }

            var platform = CloudPlatform.FromHost(uri.Host);
            if (platform == null)
            {
                // 不是已知网盘 → 直链（GitHub raw、普通 HTTP 文件等）
                result.Kind = LinkKind.Direct;
                return result;
            }

            result.Platform = platform;

            // ---- 6) 提取分享 ID ----
            string shareId = ExtractShareId(platform, url);
            result.ShareId = shareId;

            if (!string.IsNullOrEmpty(shareId))
                result.Kind = LinkKind.CloudShare;
            else
                result.Kind = LinkKind.UnsupportedCloud;   // 认得域名但认不出分享 ID

            return result;
        }

        /// <summary>
        /// 从 URL 里抽取分享 ID。
        /// 按"该平台的规则优先，其余规则兜底"的顺序试，避免误用别的平台的正则。
        /// </summary>
        private static string ExtractShareId(CloudPlatform platform, string url)
        {
            // ① 先用该平台自己的规则
            foreach (var (p, re) in SharePatterns)
            {
                if (p != platform) continue;
                var m = re.Match(url);
                if (m.Success)
                {
                    // 百度的规则把 "1" 放在捕获组外了，要补回来
                    return p == CloudPlatform.Baidu ? "1" + m.Groups[1].Value : m.Groups[1].Value;
                }
            }

            // ② 兜底：通用 /s/xxxx
            var generic = Regex.Match(url, @"/s/([A-Za-z0-9_-]{4,})");
            if (generic.Success)
                return generic.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// 判断一段文本是否"可能是个链接"，用于界面上的即时提示。
        /// </summary>
        public static bool LooksLikeLink(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            return t.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                || t.Contains("/s/")
                || QuarkKouling.IsMatch(t);
        }
    }
}
