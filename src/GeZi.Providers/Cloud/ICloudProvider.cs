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
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// 一个网盘平台的**必须实现**能力。
    ///
    /// 设计参照 wenxi 的 <c>CloudConnector</c> 抽象类。这里拆成两部分：
    ///   · 本接口 = 每个 provider 必须实现的（打开分享/列表/取链/账号）
    ///   · <see cref="CloudProviderBase"/> = 提供"不支持就抛统一异常"的默认实现
    ///
    /// ⚠️ 为什么不用「接口 + 默认接口实现（DIM）」：
    ///    本项目要同时编 net48（Windows WPF 版）和 net8.0。
    ///    DIM 需要运行时支持，**net48 不支持**，直接报 CS8701。
    ///    所以退回到经典的「接口 + 抽象基类」组合 ——
    ///    接口保证契约，基类提供默认实现，效果一样且两边都能编。
    ///
    /// 所有异步方法都收 <see cref="CancellationToken"/>：
    /// Dart 那边靠 Future 链，C# 里取消是显式的 —— 界面点"取消"时
    /// 必须能真正中断在途请求，而不是等它自己超时。
    /// </summary>
    public interface ICloudProvider
    {
        /// <summary>所属平台。</summary>
        CloudPlatform Platform { get; }

        // ================= 能力声明 =================
        // 界面用这些属性决定按钮是否可点，避免"点了才报不支持"。

        bool SupportsUpload { get; }
        bool SupportsRename { get; }
        bool SupportsMove { get; }
        bool SupportsDelete { get; }
        bool SupportsCreateFolder { get; }
        bool SupportsCreateShare { get; }

        // ================= 账号 =================

        /// <summary>取账号信息（昵称、容量），用于确认凭据有效。</summary>
        Task<CloudAccount> GetAccountAsync(CloudCredential credential, CancellationToken ct = default);

        // ================= 打开浏览会话 =================

        /// <summary>打开一个分享，返回可用于后续列表操作的会话。</summary>
        Task<BrowseSession> OpenShareAsync(ParsedLink link, CloudCredential credential,
            CancellationToken ct = default);

        /// <summary>打开自己的网盘（根目录）。</summary>
        Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default);

        // ================= 浏览 =================

        /// <summary>列出目录内容。</summary>
        Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session, string parentId,
            CloudCredential credential, CancellationToken ct = default);

        // ================= 下载 =================

        /// <summary>取得下载规格（可能需要二次取链，见 <see cref="DownloadSpec.NeedsPreparation"/>）。</summary>
        Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session, CloudFile file,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>
        /// 二次取链（把 <see cref="DownloadSpec.NeedsPreparation"/> 的那一步补完）。
        ///
        /// 为什么放在接口里而不是只留基类：上层（下载队列）持有的是
        /// <see cref="ICloudProvider"/> 接口，不是具体类型。
        /// 接口不声明就无法调用 —— 那会导致"转存类平台永远下不了"。
        /// </summary>
        Task<DownloadSpec> PrepareDownloadAsync(DownloadSpec spec,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>在线播放取链（默认复用下载）。</summary>
        Task<DownloadSpec> GetPlaybackSpecAsync(BrowseSession session, CloudFile file,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>列出存储空间（阿里/城通等支持切换）。</summary>
        Task<IReadOnlyList<CloudSpace>> GetPersonalSpacesAsync(CloudCredential credential,
            CancellationToken ct = default);

        /// <summary>列出家庭云空间。</summary>
        Task<IReadOnlyList<CloudSpace>> GetFamilySpacesAsync(CloudCredential credential,
            CancellationToken ct = default);

        /// <summary>新建文件夹。</summary>
        Task<CloudFile> CreateFolderAsync(BrowseSession session, string parentId, string name,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>上传文件。</summary>
        Task<CloudFile> UploadAsync(BrowseSession session, string parentId, string localPath,
            CloudCredential credential, IProgress<UploadProgress> progress = null,
            CancellationToken ct = default);

        /// <summary>重命名。</summary>
        Task RenameAsync(BrowseSession session, CloudFile file, string newName,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>移动。</summary>
        Task MoveAsync(BrowseSession session, IReadOnlyList<CloudFile> files, string targetId,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>删除。</summary>
        Task DeleteAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>创建分享。</summary>
        Task<ShareCreation> CreateShareAsync(BrowseSession session,
            IReadOnlyList<CloudFile> files, ShareOptions options,
            CloudCredential credential, CancellationToken ct = default);

        /// <summary>转存分享。</summary>
        Task SaveShareAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            string targetId, CloudCredential credential, CancellationToken ct = default);

        /// <summary>清理临时数据（转存产生的目录）。</summary>
        Task CleanupAsync(DownloadCleanup cleanup, CloudCredential credential,
            CancellationToken ct = default);

        /// <summary>
        /// 是否支持"转存取链"。为 true 时 <see cref="PrepareDownloadAsync"/> 会走完整流程。
        /// 界面据此决定是否显示"需转存（自动）"而不是"需转存（手动）"。
        /// </summary>
        bool SupportsTransfer { get; }
    }

    /// <summary>
    /// provider 基类：为「可选能力」提供统一的中文"不支持"实现。
    ///
    /// 好处是新增一个网盘时只需要实现接口里的 5 个核心方法，
    /// 其余能力自动获得「友好提示 + 界面按钮禁用」，不用写一堆 throw。
    /// </summary>
    public abstract class CloudProviderBase : ICloudProvider
    {
        public abstract CloudPlatform Platform { get; }

        // ---- 能力声明 ----
        //
        // ⚠️ 默认值必须是 **false**，由真正实现了对应方法的 provider 显式覆盖为 true。
        //
        // 反过来（默认为 true）会造成"界面显示了按钮、点了才报暂不支持"——
        // 实测踩过：只有夸克实现了重命名/移动/删除/建目录，
        // 但所有 provider 都继承了 true，于是 17 个平台全都显示这些按钮，
        // 除夸克外一点就报错。**能力声明必须等于实际能力**。

        public virtual bool SupportsUpload => false;
        public virtual bool SupportsRename => false;
        public virtual bool SupportsMove => false;
        public virtual bool SupportsDelete => false;
        public virtual bool SupportsCreateFolder => false;
        // 建分享也必须默认 false：平台"理论上支持"不等于"我们实现了"。
        // （之前用 Platform.SupportsSharing 判断，导致阿里/百度/天翼/123 都显示
        //   “创建分享”按钮，点了却抛未实现。）
        public virtual bool SupportsCreateShare => false;

        // ---- 必须实现 ----

        public abstract Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default);

        public abstract Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default);

        public abstract Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default);

        public abstract Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default);

        public abstract Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default);

        // ---- 可选能力：默认不支持 ----

        public virtual Task<IReadOnlyList<CloudSpace>> GetPersonalSpacesAsync(
            CloudCredential credential, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudSpace>>(Array.Empty<CloudSpace>());

        public virtual Task<IReadOnlyList<CloudSpace>> GetFamilySpacesAsync(
            CloudCredential credential, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudSpace>>(Array.Empty<CloudSpace>());

        public virtual Task<DownloadSpec> PrepareDownloadAsync(DownloadSpec spec,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(spec?.Url))
                return Task.FromResult(spec);
            throw NotSupported("二次取链", Platform);
        }

        public virtual Task<DownloadSpec> GetPlaybackSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
            => GetDownloadSpecAsync(session, file, credential, ct);

        public virtual Task<CloudFile> CreateFolderAsync(BrowseSession session, string parentId,
            string name, CloudCredential credential, CancellationToken ct = default)
            => throw NotSupported("新建文件夹", Platform);

        public virtual Task<CloudFile> UploadAsync(BrowseSession session, string parentId,
            string localPath, CloudCredential credential,
            IProgress<UploadProgress> progress = null, CancellationToken ct = default)
            => throw NotSupported("上传文件", Platform);

        public virtual Task RenameAsync(BrowseSession session, CloudFile file, string newName,
            CloudCredential credential, CancellationToken ct = default)
            => throw NotSupported("重命名", Platform);

        public virtual Task MoveAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            string targetId, CloudCredential credential, CancellationToken ct = default)
            => throw NotSupported("移动文件", Platform);

        public virtual Task DeleteAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            CloudCredential credential, CancellationToken ct = default)
            => throw NotSupported("删除文件", Platform);

        public virtual Task<ShareCreation> CreateShareAsync(BrowseSession session,
            IReadOnlyList<CloudFile> files, ShareOptions options,
            CloudCredential credential, CancellationToken ct = default)
            => throw NotSupported("创建分享", Platform);

        public virtual Task SaveShareAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            string targetId, CloudCredential credential, CancellationToken ct = default)
            => throw NotSupported("转存分享", Platform);

        public virtual Task CleanupAsync(DownloadCleanup cleanup, CloudCredential credential,
            CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>
        /// 是否支持转存。默认按"是否实现了 <see cref="ICloudTransferSupport"/>"判断 ——
        /// 这样 provider 只要实现那个接口就自动声明支持，不用两处维护。
        /// </summary>
        public virtual bool SupportsTransfer => this is ICloudTransferSupport;

        /// <summary>构造"该平台不支持此操作"的异常（带中文提示）。</summary>
        public static CloudNotSupportedException NotSupported(string action)
            => new CloudNotSupportedException((action ?? "该操作") + "：此网盘暂不支持");

        /// <summary>带平台名的版本（提示更具体）。</summary>
        public static CloudNotSupportedException NotSupported(string action, CloudPlatform platform)
            => new CloudNotSupportedException(
                (action ?? "该操作") + "：" + (platform?.Label ?? "该网盘") + " 暂不支持");
    }

    /// <summary>
    /// "这个平台不支持该操作"。
    ///
    /// 单独定义而不是复用 <see cref="NotSupportedException"/>：
    /// 界面需要把这类错误显示成**友好提示**（"该网盘暂不支持上传"），
    /// 而不是当成异常弹一个红框。用独立类型便于精确捕获。
    /// </summary>
    public sealed class CloudNotSupportedException : Exception
    {
        public CloudNotSupportedException(string message) : base(message) { }
    }

    /// <summary>网盘接口返回的业务错误（带平台原始错误码/信息）。</summary>
    public class CloudApiException : Exception
    {
        public string Code { get; }
        public object RawCode { get; }

        public CloudApiException(string message, string code = null, object rawCode = null)
            : base(message)
        {
            Code = code;
            RawCode = rawCode;
        }
    }
}
