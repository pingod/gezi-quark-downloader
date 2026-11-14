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
    /// 一个网盘账号的凭据。
    ///
    /// 不同平台的凭据形态完全不同：
    ///   · 夸克/UC/天翼/移动/联通 → 一整串 Cookie
    ///   · 阿里云盘/PikPak      → access_token + refresh_token
    ///   · 百度                 → Cookie（BDUSS + STOKEN）
    ///   · 115                  → Cookie（UID + CID + SEID + KID）
    ///
    /// 与其为每种形态定义一套字段，不如统一成「有序字典 + 语义槽位」：
    /// <see cref="Primary"/> / <see cref="Secondary"/> 给最常见的两段用，
    /// 其余平台差异走 <see cref="Field"/>。这样账号存储、加密、序列化
    /// 只需要写一份，不用为每个平台开一个子类。
    /// </summary>
    public sealed class CloudCredential
    {
        public const string PrimaryKey = "primary";
        public const string SecondaryKey = "secondary";

        /// <summary>该凭据属于哪个平台。</summary>
        public CloudPlatform Platform { get; }

        /// <summary>用户可见的账号名（昵称或标识）。</summary>
        public string Label { get; set; }

        private readonly Dictionary<string, string> _fields;

        /// <summary>最近更新时间（UTC ticks），用于"最近使用"排序。</summary>
        public DateTime UpdatedAt { get; set; }

        public CloudCredential(CloudPlatform platform, string label,
            IDictionary<string, string> fields = null, DateTime? updatedAt = null)
        {
            Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            Label = label ?? "";
            _fields = fields == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);
            UpdatedAt = updatedAt ?? DateTime.UtcNow;
        }

        /// <summary>主凭据（Cookie 整串 / access_token）。</summary>
        public string Primary => Field(PrimaryKey).Trim();

        /// <summary>次凭据（refresh_token / 第二个关键 Cookie）。</summary>
        public string Secondary => Field(SecondaryKey).Trim();

        public string Field(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return _fields.TryGetValue(key, out var v) ? v ?? "" : "";
        }

        public bool Has(string key) => !string.IsNullOrWhiteSpace(Field(key));

        /// <summary>是否看起来像"已填写"（至少要有一个非空字段）。</summary>
        public bool IsUsable => _fields.Any(kv => !string.IsNullOrWhiteSpace(kv.Value));

        /// <summary>复制并覆盖部分字段。</summary>
        public CloudCredential WithFields(IDictionary<string, string> values,
            bool preserveRevision = false)
        {
            var merged = new Dictionary<string, string>(_fields, StringComparer.OrdinalIgnoreCase);
            if (values != null)
            {
                foreach (var kv in values)
                    merged[kv.Key] = kv.Value ?? "";
            }
            return new CloudCredential(Platform, Label, merged,
                preserveRevision ? UpdatedAt : DateTime.UtcNow);
        }

        /// <summary>导出为可序列化的字段字典（加密由上层负责）。</summary>
        public IReadOnlyDictionary<string, string> SnapshotFields() => _fields;

        /// <summary>
        /// **就地**写入字段（合并，不删除未提及的键）。
        ///
        /// 专供 <see cref="TokenCloudProviderBase"/> 刷新令牌时使用：
        /// 令牌轮转必须原地更新，否则 provider 拿到的凭据和上层账号列表
        /// 会变成两个不同对象 —— 表现为"刷新成功了但下次请求还是旧 token"。
        ///
        /// 用 internal 而不是 public：这是基础设施内部约定，
        /// 不应该让业务代码随手改凭据字段（那样会绕过加密与审计）。
        /// </summary>
        internal void SetFieldsInternal(IDictionary<string, string> values)
        {
            if (values == null) return;
            foreach (var kv in values)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                _fields[kv.Key] = kv.Value ?? "";
            }
        }

        public override string ToString() => Platform.ShortName + ":" + Label;
    }

    /// <summary>账号基本信息（登录后展示）。</summary>
    public sealed class CloudAccount
    {
        public string Nickname { get; set; } = "";

        /// <summary>已用容量（字节，0 = 未知）。</summary>
        public long Used { get; set; }

        /// <summary>总容量（字节，0 = 未知）。</summary>
        public long Total { get; set; }

        /// <summary>额外信息（头像 URL、会员类型等，平台各异）。</summary>
        public Dictionary<string, string> Extra { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string DisplayName => string.IsNullOrEmpty(Nickname) ? "(未命名)" : Nickname;
    }

    /// <summary>存储空间（阿里云盘的"资源库"、城通的"扩展盘"等）。</summary>
    public sealed class CloudSpace
    {
        public string Id { get; }
        public string Name { get; }

        public CloudSpace(string id, string name)
        {
            Id = id ?? "";
            Name = name ?? "";
        }
    }

    /// <summary>
    /// 网盘里的一个文件或目录。
    ///
    /// 字段参照 wenxi 的 <c>CloudFile</c>。注意以下几点：
    /// - <see cref="Token"/> 是"分享内的文件令牌"（夸克的 share_fid_token 等），
    ///   只有分享场景才有，个人网盘里为空。
    /// - <see cref="HashType"/>/<see cref="HashValue"/> 用于秒传与完整性校验。
    /// - <see cref="ThumbnailUrl"/> 是**临时**预览地址，不参与持久化
    ///   （每次列表刷新都会变，存下来下次必然失效）。
    /// </summary>
    public sealed class CloudFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public string ParentId { get; set; } = "";

        /// <summary>分享内的文件令牌（只有分享场景有）。</summary>
        public string Token { get; set; } = "";

        /// <summary>修改时间（平台原始字符串，格式各异，仅用于显示/排序）。</summary>
        public string ModifiedAt { get; set; } = "";

        public string HashType { get; set; }
        public string HashValue { get; set; }

        /// <summary>临时缩略图地址，不持久化。</summary>
        public string ThumbnailUrl { get; set; } = "";

        public bool IsFile => !IsDirectory;

        public override string ToString() =>
            (IsDirectory ? "[目录] " : "[文件] ") + Name;
    }

    /// <summary>浏览模式：看别人的分享，还是看自己的网盘。</summary>
    public enum BrowseMode
    {
        Share,
        Personal,
    }

    /// <summary>
    /// 一次"浏览会话"：把「哪个平台 + 什么模式 + 根节点 + 所需上下文」打包在一起。
    ///
    /// 为什么需要它：后续每个操作（列表、下载、重命名、删除…）都需要知道
    /// 当前是在看分享还是在看自己的盘，以及要用哪个账号的凭据。
    /// 把这些收进一个对象，接口签名就能保持简单
    /// （`List(session, parentId, credential)` 而不是七八个散参数）。
    /// </summary>
    public sealed class BrowseSession
    {
        public CloudPlatform Platform { get; set; }
        public BrowseMode Mode { get; set; }

        /// <summary>显示用的标题（分享名 / "我的网盘"）。</summary>
        public string Title { get; set; } = "";

        /// <summary>根节点 id（分享的根通常是 "0"）。</summary>
        public string RootId { get; set; } = "0";

        /// <summary>
        /// 平台相关的附加上下文。常见键：
        ///   pwdId     分享 ID（分享模式）
        ///   stoken    分享凭证（分享模式）
        ///   passcode  提取码
        ///   accountId 当前账号标识
        ///   driveId   存储空间 id（阿里云盘）
        ///   familyId  家庭云 id
        /// </summary>
        public Dictionary<string, string> Metadata { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>来源链接（分享模式）。</summary>
        public ParsedLink SourceLink { get; set; }

        public string Meta(string key)
            => Metadata.TryGetValue(key, out var v) ? v ?? "" : "";

        public BrowseSession SetMeta(string key, string value)
        {
            Metadata[key] = value ?? "";
            return this;
        }

        public string AccountId => Meta("accountId");

        /// <summary>是否可管理文件（上传/删除/重命名）。分享模式一律不可。</summary>
        public bool CanManageFiles
        {
            get
            {
                if (Mode != BrowseMode.Personal)
                    return false;

                // 家庭云空间通常是只读协作区，按平台约定不允许删除/移动
                if (!string.IsNullOrEmpty(Meta("familyId")))
                    return false;

                return true;
            }
        }
    }

    /// <summary>链接类型。</summary>
    public enum LinkKind
    {
        /// <summary>网盘分享链接。</summary>
        CloudShare,

        /// <summary>HTTP(S) 直链。</summary>
        Direct,

        /// <summary>磁力链接。</summary>
        Magnet,

        /// <summary>种子文件。</summary>
        Torrent,

        /// <summary>看起来是网盘链接，但该平台还不支持。</summary>
        UnsupportedCloud,
    }

    /// <summary>
    /// 解析后的链接。
    /// </summary>
    public sealed class ParsedLink
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Source { get; set; } = "";
        public string Url { get; set; } = "";
        public LinkKind Kind { get; set; }
        public CloudPlatform Platform { get; set; }

        /// <summary>分享 ID（夸克的 pwd_id 等）。</summary>
        public string ShareId { get; set; }

        /// <summary>提取码。</summary>
        public string Passcode { get; set; }

        public bool IsCloudShare => Kind == LinkKind.CloudShare || Kind == LinkKind.UnsupportedCloud;

        public string CloudLabel => Platform?.Label ?? "";

        public ParsedLink WithPasscode(string value)
        {
            var c = (ParsedLink)MemberwiseClone();
            c.Passcode = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            return c;
        }

        public override string ToString() =>
            (Platform?.ShortName ?? "?") + " " + (ShareId ?? Url);
    }

    /// <summary>
    /// 下载规格：怎么把一个选中的文件变成"可下载的 URL"。
    ///
    /// 这是 wenxi 里最有价值的设计，直接照搬其语义：
    ///
    /// - 有些平台（夸克/UC）能在解析分享时直接给出**带签名的直链**，
    ///   此时 <see cref="Url"/> 有值，<see cref="NeedsPreparation"/> = false。
    ///
    /// - 有些平台（阿里/百度/天翼/123/迅雷…）必须先**转存到自己账号**，
    ///   再对转存后的文件取链。此时 <see cref="Url"/> 为空、
    ///   <see cref="Source"/> 有值，由下载器在真正开始前调
    ///   provider 的 <c>PrepareDownloadAsync</c> 完成二次取链。
    ///
    /// - <see cref="Cleanup"/> 记录"下载完要删掉哪些临时转存目录"，
    ///   否则用户的网盘会被下载过的文件塞满。
    /// </summary>
    public sealed class DownloadSpec
    {
        /// <summary>直链（为空表示需要二次取链）。</summary>
        public string Url { get; set; } = "";

        /// <summary>保存时的文件名。</summary>
        public string FileName { get; set; } = "";

        /// <summary>相对保存路径（保留分享内的目录结构）。</summary>
        public string RelativePath { get; set; } = "";

        /// <summary>下载时必须带的请求头（签名直链常要求特定 UA / Referer / Cookie）。</summary>
        public Dictionary<string, string> Headers { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>预期大小（0 = 未知；用于进度显示与断点校验）。</summary>
        public long ExpectedSize { get; set; }

        public string ChecksumType { get; set; }
        public string ChecksumValue { get; set; }

        /// <summary>下载完成后需要清理的临时目录/文件。</summary>
        public DownloadCleanup Cleanup { get; set; }

        /// <summary>二次取链所需的上下文（fid、平台会话信息等）。</summary>
        public Dictionary<string, string> Source { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否需要在开始下载前再取一次链。</summary>
        public bool NeedsPreparation =>
            string.IsNullOrEmpty(Url) && Source.Count > 0;

        /// <summary>是否游客（未登录）拿到的直链。</summary>
        public bool GuestDownload { get; set; }

        public DownloadSpec Clone()
        {
            var d = new DownloadSpec
            {
                Url = Url,
                FileName = FileName,
                RelativePath = RelativePath,
                ExpectedSize = ExpectedSize,
                ChecksumType = ChecksumType,
                ChecksumValue = ChecksumValue,
                Cleanup = Cleanup,
                GuestDownload = GuestDownload,
            };
            foreach (var kv in Headers) d.Headers[kv.Key] = kv.Value;
            foreach (var kv in Source) d.Source[kv.Key] = kv.Value;
            return d;
        }
    }

    /// <summary>
    /// "下载完要清理什么"。
    ///
    /// 典型场景：某个平台必须先把分享文件转存到自己网盘的临时目录才能取链。
    /// 下载完不清理的话，用户的网盘会被一堆「AsterLink临时转存_xxx」塞满。
    /// </summary>
    public sealed class DownloadCleanup
    {
        /// <summary>要删除的目录 id 列表。</summary>
        public List<string> DirectoryIds { get; } = new List<string>();

        /// <summary>要删除的文件 id 列表。</summary>
        public List<string> FileIds { get; } = new List<string>();

        /// <summary>平台标识（清理时要用对应平台的凭据）。</summary>
        public string PlatformKey { get; set; } = "";

        /// <summary>账号标识。</summary>
        public string AccountId { get; set; } = "";

        public bool HasWork => DirectoryIds.Count > 0 || FileIds.Count > 0;

        public DownloadCleanup Clone()
        {
            var c = new DownloadCleanup
            {
                PlatformKey = PlatformKey,
                AccountId = AccountId,
            };
            c.DirectoryIds.AddRange(DirectoryIds);
            c.FileIds.AddRange(FileIds);
            return c;
        }
    }

    /// <summary>创建分享的选项。</summary>
    public sealed class ShareOptions
    {
        /// <summary>有效期（天；0 或负数 = 平台默认/永久）。</summary>
        public int ExpireDays { get; set; }

        /// <summary>提取码（空 = 由平台生成，或不需要）。</summary>
        public string Passcode { get; set; } = "";
    }

    /// <summary>创建分享的结果。</summary>
    public sealed class ShareCreation
    {
        public string Url { get; set; } = "";
        public string Passcode { get; set; } = "";
        public string ShareId { get; set; } = "";
    }

    /// <summary>上传进度的上报载体。</summary>
    public sealed class UploadProgress
    {
        public long Done { get; set; }
        public long Total { get; set; }
        public double Speed { get; set; }
        public string Phase { get; set; }
        public double Percent => Total > 0 ? Done * 100.0 / Total : 0;
    }
}
