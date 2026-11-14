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
using System.Linq;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// 支持的网盘平台。
    ///
    /// 设计参照 wenxi（Flutter 多网盘客户端）的 <c>CloudPlatform</c> 枚举 ——
    /// 每个平台除了标识和名字，还携带**域名列表**（用于从粘贴的链接识别平台）
    /// 和一组**能力开关**（能否解析分享、能否上传、是否必须登录…）。
    ///
    /// 把能力做成枚举上的属性而不是散落在各 provider 里的 if 判断，
    /// 好处是界面可以直接问"这个平台能不能上传"来决定按钮是否可用，
    /// 不需要事先知道具体是哪个 provider 实现。
    /// </summary>
    public sealed class CloudPlatform
    {
        /// <summary>稳定标识（小写，用于配置持久化，不要改）。</summary>
        public string Key { get; }

        /// <summary>完整显示名，如「夸克网盘」。</summary>
        public string Label { get; }

        /// <summary>短名，如「夸克」（空间紧张的地方用）。</summary>
        public string ShortName { get; }

        /// <summary>归属域名，用于 <see cref="FromHost"/> 识别。</summary>
        public IReadOnlyList<string> Hosts { get; }

        private CloudPlatform(string key, string label, string shortName, params string[] hosts)
        {
            Key = key;
            Label = label;
            ShortName = shortName;
            Hosts = hosts;
        }

        // ================= 平台定义 =================

        // 已实现或计划优先实现的平台（与 wenxi 的清单对齐，按实用性排序）
        public static readonly CloudPlatform Quark = new CloudPlatform(
            "quark", "夸克网盘", "夸克", "quark.cn");

        public static readonly CloudPlatform Uc = new CloudPlatform(
            "uc", "UC网盘", "UC", "uc.cn");

        public static readonly CloudPlatform Aliyun = new CloudPlatform(
            "aliyun", "阿里云盘", "阿里", "aliyundrive.com", "alipan.com");

        public static readonly CloudPlatform Pan123 = new CloudPlatform(
            "123", "123网盘", "123", "123pan.com", "123pan.cn");

        public static readonly CloudPlatform Tianyi = new CloudPlatform(
            "tianyi", "天翼云盘", "天翼", "189.cn");

        public static readonly CloudPlatform C139 = new CloudPlatform(
            "yidong", "中国移动云盘", "139", "yun.139.com", "caiyun.139.com");

        public static readonly CloudPlatform Wopan = new CloudPlatform(
            "wopan", "中国联通云盘", "联通", "pan.wo.cn", "panservice.mail.wo.cn");

        public static readonly CloudPlatform Baidu = new CloudPlatform(
            "baidu", "百度网盘", "百度", "pan.baidu.com", "baidu.com");

        public static readonly CloudPlatform Xunlei = new CloudPlatform(
            "xunlei", "迅雷网盘", "迅雷", "xunlei.com");

        public static readonly CloudPlatform Pan115 = new CloudPlatform(
            "115", "115网盘", "115", "115.com", "115cdn.com", "115sha1.com");

        public static readonly CloudPlatform Lanzou = new CloudPlatform(
            "lanzous", "蓝奏云", "蓝奏", "lanzou.com", "lanzous.com", "lanzoux.com");

        /// <summary>蓝奏云优享版（独立域名 + token 鉴权，与普通蓝奏不是一套协议）。</summary>
        public static readonly CloudPlatform Ilanzou = new CloudPlatform(
            "ilanzou", "蓝奏云优享版", "优享", "ilanzou.com");

        public static readonly CloudPlatform Ctfile = new CloudPlatform(
            "ctfile", "城通网盘", "城通", "ctfile.com", "ctfile.cn", "400gb.com",
            "545c.com", "pipipan.com");

        public static readonly CloudPlatform Feijipan = new CloudPlatform(
            "feijipan", "小飞机网盘", "小飞机", "feijipan.com");

        public static readonly CloudPlatform PikPak = new CloudPlatform(
            "pikpak", "PikPak", "PikPak", "mypikpak.com");

        public static readonly CloudPlatform Guangya = new CloudPlatform(
            "guangya", "光鸭云盘", "光鸭", "guangyapan.com");

        public static readonly CloudPlatform Weiyun = new CloudPlatform(
            "weiyun", "腾讯微云", "微云", "weiyun.com");

        public static readonly CloudPlatform Github = new CloudPlatform(
            "github", "GitHub", "GitHub", "github.com",
            "raw.githubusercontent.com", "codeload.github.com");

        /// <summary>全部平台。</summary>
        public static readonly IReadOnlyList<CloudPlatform> All = new[]
        {
            Quark, Uc, Aliyun, Pan123, Tianyi, C139, Wopan, Baidu, Xunlei,
            Pan115, Lanzou, Ilanzou, Ctfile, Feijipan, PikPak, Guangya, Weiyun, Github,
        };

        // ================= 能力开关 =================

        public bool CanCreateFolder => true;

        /// <summary>是否支持家庭云。</summary>
        public bool SupportsFamilyCloud =>
            this == Tianyi || this == C139 || this == Wopan;

        /// <summary>是否支持切换存储空间（如阿里云盘的资源库）。</summary>
        public bool SupportsPersonalSpaces => this == Aliyun || this == Ctfile;

        /// <summary>是否支持解析分享链接。</summary>
        public bool SupportsShareParsing => this != Weiyun && this != Ilanzou;

        /// <summary>
        /// **解析分享是否必须登录**。
        ///
        /// 这是很关键的一条：夸克/UC 的分享可以匿名解析并取链，
        /// 而百度/天翼/移动/联通/迅雷等必须先登录。
        /// 界面据此决定"未登录时是否还允许用户点解析"。
        /// </summary>
        public bool ShareRequiresAccount => !(this == Baidu)
            && !(this == Lanzou) && !(this == Guangya) && !(this == Aliyun)
            && !(this == Wopan) && !(this == Quark) && !(this == Uc)
            && !(this == Ctfile) && !(this == Feijipan) && !(this == PikPak)
            && !(this == Github);

        /// <summary>是否支持创建分享。</summary>
        public bool SupportsSharing =>
            this != Weiyun && this != Wopan && this != Github && this != Ilanzou;

        /// <summary>文件大小是否精确（蓝奏/小飞机这类给的 size 可能不准）。</summary>
        public bool ExactFileSize => this != Lanzou && this != Feijipan;

        /// <summary>未支持平台时的提示语。</summary>
        public string ShareUnavailableMessage => Label + "暂不支持分享解析";

        // ================= 识别 =================

        /// <summary>按标识找平台（配置里存的 key）。</summary>
        public static CloudPlatform FromKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            return All.FirstOrDefault(p =>
                string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 按域名识别平台。
        ///
        /// ⚠️ 用"后缀匹配"而不是相等匹配：分享链接可能是
        /// `pan.quark.cn`、`drive.quark.cn`、`quark.cn` 中的任一个，
        /// 硬编码全量域名既啰嗦又容易漏。改成"域名等于 h 或以 .h 结尾"，
        /// 只要 h = quark.cn 就全都能命中。
        /// </summary>
        public static CloudPlatform FromHost(string host)
        {
            if (string.IsNullOrEmpty(host))
                return null;

            string h = host.ToLowerInvariant();
            if (h.StartsWith("www."))
                h = h.Substring(4);

            // 蓝奏云的域名变体特别多（lanzous/lanzoux/lanzoui…），单独按模式匹配
            if (LanzouPattern.IsMatch(h))
                return Lanzou;

            // 123 网盘还有 123865.com / 123684.com 这类按号段分域名的情况
            if (Pan123Pattern.IsMatch(h))
                return Pan123;

            foreach (var p in All)
            {
                foreach (var d in p.Hosts)
                {
                    if (h == d || h.EndsWith("." + d, StringComparison.Ordinal))
                        return p;
                }
            }
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex LanzouPattern =
            new System.Text.RegularExpressions.Regex(
                @"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)*(?:lanzou[a-z0-9-]*|lan[zs]o[ux])\.(?:com|net|org|cn)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex Pan123Pattern =
            new System.Text.RegularExpressions.Regex(
                @"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)*123\d+\.(?:com|cn)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public override string ToString() => Label;
    }
}
