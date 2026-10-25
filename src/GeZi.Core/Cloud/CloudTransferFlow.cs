// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Cloud
{
    /// <summary>
    /// 转存（保存分享到自己账号）的通用流程编排。
    ///
    /// 参照 wenxi 的 5 步模板（`pan123.dart:820-880`）：
    /// <code>
    /// 1. 建唯一临时目录        createTemporary
    /// 2. 立刻登记 cleanup      ← 必须在转存之前！否则中途失败会留下垃圾
    /// 3. 执行转存 saveShare    transfer
    /// 4. 轮询列表直到目标可见   waitTransfer（8~30 次 × 间隔）
    /// 5. 用新 fileId 再取链    download(personalSession, 新文件)
    /// </code>
    ///
    /// **为什么第 2 步必须在前**：如果先转存再登记，转存成功后程序崩溃/断电，
    /// 临时目录就永远留在用户网盘里了。先登记，清理队列才知道有东西要删。
    ///
    /// 各平台的差异（临时目录名、轮询次数、可见性判定）由
    /// <see cref="ICloudTransferSupport"/> 提供，这里只负责流程。
    /// </summary>
    public static class CloudTransferFlow
    {
        /// <summary>临时目录名前缀（与 wenxi 一致，便于跨工具互认）。</summary>
        public const string TempFolderPrefix = "AsterLink临时转存_";

        /// <summary>
        /// 生成一个临时目录名。
        /// ⚠️ 必须是 <c>AsterLink临时转存_</c> + UUIDv4，因为这个格式后面会做
        /// **安全校验**（清理时只允许删匹配该正则的目录，防止被篡改成删任意目录）。
        /// </summary>
        public static string NewTempFolderName()
            => TempFolderPrefix + Guid.NewGuid().ToString("D");

        /// <summary>
        /// 校验一个名字是否是我们创建的临时目录。
        ///
        /// **这是安全边界**：清理任务会持久化到磁盘，如果磁盘上的数据被篡改
        /// （或从别处导入），没有这个校验就可能删掉用户的真实目录。
        /// </summary>
        public static bool IsTempFolderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!name.StartsWith(TempFolderPrefix, StringComparison.Ordinal)) return false;

            string suffix = name.Substring(TempFolderPrefix.Length);
            return Guid.TryParse(suffix, out _);
        }

        /// <summary>
        /// 完整执行一次"转存 → 等可见 → 取链"。
        /// </summary>
        /// <param name="provider">支持转存的 provider</param>
        /// <param name="spec">带 <see cref="DownloadSpec.Source"/> 上下文的待准备规格</param>
        /// <param name="credential">账号凭据</param>
        /// <param name="onPhase">阶段回调（给界面显示"正在转存/等待中"）</param>
        public static async Task<DownloadSpec> PrepareAsync(
            ICloudTransferSupport provider,
            DownloadSpec spec,
            CloudCredential credential,
            Action<string> onPhase = null,
            CancellationToken ct = default)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            // 已经有直链 → 不需要转存
            if (!string.IsNullOrEmpty(spec.Url))
                return spec;

            onPhase?.Invoke("正在创建临时目录");

            // ---- 步骤 1：建临时目录 ----
            string folderId = await provider.CreateTempFolderAsync(credential, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(folderId))
                throw new CloudApiException("转存失败：无法创建临时目录（账号可能空间不足或未登录）");

            // ---- 步骤 2：**立刻**登记清理 ----
            // 顺序绝对不能反：先登记再转存，中途崩溃也留不下垃圾。
            var cleanup = new DownloadCleanup
            {
                PlatformKey = provider.Platform.Key,
                AccountId = credential?.Label ?? "",
            };
            cleanup.DirectoryIds.Add(folderId);

            spec.Cleanup = cleanup;

            try
            {
                // ---- 步骤 3：执行转存 ----
                onPhase?.Invoke("正在转存到网盘");
                var saved = await provider.SaveToFolderAsync(spec, folderId, credential, ct)
                    .ConfigureAwait(false);

                if (saved == null)
                    throw new CloudApiException("转存失败：接口没有返回保存结果");

                // ---- 步骤 4：等目标文件可见 ----
                onPhase?.Invoke("等待转存完成");
                var visible = await provider.WaitVisibleAsync(
                    saved, folderId, spec, credential, onPhase, ct).ConfigureAwait(false);

                if (visible == null)
                {
                    // 转存已受理但列表还没刷新 —— 不是失败，只是需要等
                    throw new CloudApiException(
                        "已受理转存，但列表尚未更新。请稍后重试（临时文件会在下次启动时自动清理）。");
                }

                // ---- 步骤 5：用新 fileId 取链 ----
                onPhase?.Invoke("正在获取下载地址");
                var prepared = await provider.ResolveSavedAsync(
                    visible, spec, credential, ct).ConfigureAwait(false);

                if (prepared == null || string.IsNullOrEmpty(prepared.Url))
                    throw new CloudApiException("转存成功但取链失败，请稍后重试");

                prepared.Cleanup = cleanup;
                prepared.FileName = string.IsNullOrEmpty(prepared.FileName)
                    ? spec.FileName : prepared.FileName;

                return prepared;
            }
            catch
            {
                // 转存失败 → 尽力清理刚建的临时目录（不掩盖原始异常）
                try
                {
                    await provider.DeleteTempFolderAsync(folderId, credential, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
                throw;
            }
        }
    }

    /// <summary>
    /// 支持"转存取链"的平台需要实现这一组能力。
    ///
    /// 拆成独立接口而不是塞进 <see cref="ICloudProvider"/>：
    /// 只有 6 个平台需要转存（百度/123/天翼/迅雷/PikPak/光谷），
    /// 夸克、GitHub、蓝奏这类不需要。硬塞会让所有 provider 都被迫实现一堆空方法。
    /// </summary>
    public interface ICloudTransferSupport
    {
        CloudPlatform Platform { get; }

        /// <summary>在自己的网盘里建一个临时目录，返回目录 id。</summary>
        Task<string> CreateTempFolderAsync(CloudCredential credential, CancellationToken ct);

        /// <summary>
        /// 把分享文件转存到指定目录。
        /// 返回值交给 <see cref="WaitVisibleAsync"/> 用（可能是任务 id、新 fid 列表等）。
        /// </summary>
        Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct);

        /// <summary>
        /// 轮询等待转存的文件在目录里"可见"。
        /// 返回转存后的 <see cref="CloudFile"/>；超时返回 null（**不是异常** ——
        /// "已受理但索引慢" 和 "真失败" 要区分开）。
        /// </summary>
        Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct);

        /// <summary>用转存后的文件取链。</summary>
        Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct);

        /// <summary>删除临时目录（清理用）。</summary>
        Task DeleteTempFolderAsync(string folderId, CloudCredential credential, CancellationToken ct);
    }

    /// <summary>
    /// 转存流程的通用实现辅助：把"轮询等可见"这段重复逻辑收口。
    ///
    /// 各平台的差异只有三个数字：轮询次数、间隔、可见性判定。
    /// 其余（重试节奏、超时语义、日志）完全一样。
    /// </summary>
    public static class TransferWaiter
    {
        /// <summary>
        /// 轮询直到 <paramref name="probe"/> 返回非 null。
        /// </summary>
        /// <param name="attempts">最多尝试几次（123=8，阿里/光鸭=30）</param>
        /// <param name="intervalMs">每次间隔（123=1000，天翼/迅雷=800，阿里/光鸭=750）</param>
        /// <param name="probe">探测函数：找到目标文件返回它，否则 null</param>
        /// <returns>找到的文件；超时返回 null（**不抛异常**）</returns>
        public static async Task<CloudFile> WaitAsync(
            int attempts,
            int intervalMs,
            Func<CancellationToken, Task<CloudFile>> probe,
            Action<string> onPhase = null,
            CancellationToken ct = default)
        {
            if (probe == null) return null;
            if (attempts < 1) attempts = 1;
            if (intervalMs < 100) intervalMs = 100;

            for (int i = 0; i < attempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var found = await probe(ct).ConfigureAwait(false);
                    if (found != null) return found;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单次探测失败继续（索引延迟、瞬时 5xx 都常见）
                }

                if (i < attempts - 1)
                {
                    onPhase?.Invoke(string.Format("等待转存完成（{0}/{1}）", i + 1, attempts));
                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                }
            }

            return null;
        }

        /// <summary>
        /// 判断一个列表里是否"有且仅有一个"匹配目标文件。
        ///
        /// 为什么要求唯一：如果有两个同名同大小的文件，说明临时目录里混进了
        /// 别的东西（并发转存同名文件），此时取哪个都可能拿错 —— 宁可报"稍后重试"。
        /// </summary>
        public static CloudFile MatchUnique(IReadOnlyList<CloudFile> items,
            string name, long size)
        {
            if (items == null) return null;

            CloudFile hit = null;
            int count = 0;

            foreach (var f in items)
            {
                if (f == null || f.IsDirectory) continue;
                if (!string.Equals(f.Name, name, StringComparison.Ordinal)) continue;

                // 大小已知时才校验（有些平台转存后 size 短暂为 0）
                if (size > 0 && f.Size > 0 && f.Size != size) continue;

                count++;
                if (hit == null) hit = f;
            }

            return count == 1 ? hit : null;
        }
    }
}
