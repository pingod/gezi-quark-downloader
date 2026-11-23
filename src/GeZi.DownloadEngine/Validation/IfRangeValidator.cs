// GeZi - 鸽子下载器
// Copyright (C) 2026 Yi Yuan
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
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GeZi.DownloadEngine.Transport;

namespace GeZi.DownloadEngine.Validation;

/// <summary>
/// 基于 HTTP 条件请求（If-Range）与内容哈希的校验器。
///
/// 三条防线：
/// 1. <b>If-Range</b>：探测阶段记下强 ETag（或 Last-Modified），每个分片请求都带上；
///    资源一旦变化，服务端会返回 200 而不是 206，我们据此判定资源变更并作废本次下载。
/// 2. <b>Content-Range 起点校验</b>：206 响应的 <c>Content-Range</c> 起点必须等于请求起点，
///    否则写进去会错位（历史上这是静默损坏的主要来源之一）。
/// 3. <b>哈希比对</b>：完成后按 SHA-256 → SHA-1 → MD5 优先级比对，接口没给哈希则标「未校验」。
///
/// 注意：弱 ETag（<c>W/"..."</c>）一律不认。部分 CDN 对弱 ETag 的 If-Range 会直接回 200，
/// 反而把「正常续传」误判成「资源变更」。
/// </summary>
public sealed class IfRangeValidator : IContentValidator
{
    /// <summary>无状态单例。</summary>
    public static readonly IfRangeValidator Instance = new IfRangeValidator();

    private const int HashBufferSize = 1024 * 1024;

    public string Name => "if-range";

    public string Capture(ChunkResponse response)
    {
        if (response == null)
        {
            return "";
        }

        // ETag 优先：它比 Last-Modified 精确（后者只有秒级粒度，同一秒内两次修改无法区分）
        string etag = response.ETag;
        if (!string.IsNullOrEmpty(etag) && !IsWeakEtag(etag))
        {
            return etag;
        }

        string lastModified = response.LastModified;
        if (!string.IsNullOrEmpty(lastModified))
        {
            return lastModified;
        }

        return "";
    }

    public void Guard(ChunkRequest request, string validator)
    {
        if (request == null || string.IsNullOrEmpty(validator))
        {
            return;
        }

        // 调用方通过 RequestConfiguration.Headers["If-Range"] 显式指定时以它为准，
        // 我们只在没有显式值时补上探测阶段捕获的标识。
        if (!string.IsNullOrEmpty(request.IfRange))
        {
            return;
        }

        request.IfRange = validator;
    }

    public ChunkGuardResult GuardChunk(ChunkRequest request, ChunkResponse response)
    {
        if (request == null || response == null)
        {
            return ChunkGuardResult.Ok;
        }

        bool isRangeRequest = request.End >= request.Start && request.End >= 0;

        // 服务端忽略 Range 回了 200：只有在「本来要区间」的时候才是问题。
        // 单连接整文件下载（End < Start）回 200 是正常的。
        if (isRangeRequest && response.StatusCode == 200)
        {
            return ChunkGuardResult.ResourceChanged;
        }

        if (response.StatusCode == 206)
        {
            // ContentRangeStart < 0 表示响应里没带 Content-Range，无法校验，
            // 这种情况交给上层按「整文件覆盖」的既有逻辑处理，不在这里拦。
            if (response.ContentRangeStart >= 0 && response.ContentRangeStart != request.Start)
            {
                return ChunkGuardResult.StartMismatch;
            }
        }

        return ChunkGuardResult.Ok;
    }

    public Task<VerifyOutcome> VerifyAsync(string path, HashExpectation expect, CancellationToken cancel)
    {
        if (expect == null || expect.IsEmpty)
        {
            return Task.FromResult(VerifyOutcome.Skip("未校验（接口未提供哈希）"));
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Task.FromResult(VerifyOutcome.Fail("", "待校验文件不存在：" + path));
        }

        return Task.Run(() => VerifyCore(path, expect, cancel), cancel);
    }

    private static VerifyOutcome VerifyCore(string path, HashExpectation expect, CancellationToken cancel)
    {
        // 🚨🚨 2026-11-14 改：**多算法全试，不再「取第一个非空的就算」**。
        //
        // 原来的写法是互斥 if-else：给了 Sha256 就只算 SHA-256，不符直接判 Warn，
        // 后面明明还有 Sha1 / Md5 两个字段却从不尝试。
        //
        // 后果（用户实测反复撞上）：夸克同时返回 md5 / sha1 / sha256 三个字段
        // （见 QuarkClient.cs 里的 file_md5 / file_sha1 / file_sha256），
        // 而它的 md5 **经常和真实内容不符**（2026-10-24 全量比对坐实：本地与服务器逐字节一致，
        // 但都不等于接口给的 md5）。于是只用 md5 判断就永远「不一致」。
        //
        // 现在：按可信度从高到低逐个试，**任一匹配就算通过**。
        // 代价控制：只有前一个不符才会算下一个；SHA-256 本身不可能错，
        // 所以「接口给了 sha256」的正常情况仍然只算一遍。
        var attempts = new List<KeyValuePair<string, string>>(3);
        if (!string.IsNullOrEmpty(expect.Sha256)) attempts.Add(new KeyValuePair<string, string>("SHA-256", expect.Sha256));
        if (!string.IsNullOrEmpty(expect.Sha1)) attempts.Add(new KeyValuePair<string, string>("SHA-1", expect.Sha1));
        if (!string.IsNullOrEmpty(expect.Md5)) attempts.Add(new KeyValuePair<string, string>("MD5", expect.Md5));

        if (attempts.Count == 0)
        {
            return VerifyOutcome.Skip("未校验（接口未提供哈希）");
        }

        var mismatches = new List<string>(attempts.Count);

        for (int i = 0; i < attempts.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            string algorithm = attempts[i].Key;
            string expected = attempts[i].Value;

            string actual;
            try
            {
                using (var hash = CreateHash(algorithm))
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, HashBufferSize, FileOptions.SequentialScan))
                {
                    byte[] digest = hash.ComputeHash(fs);
                    actual = ToHex(digest);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个算法算不出来（比如文件被独占）不值得判死，记下来试下一个
                mismatches.Add(algorithm + " 计算失败：" + ex.Message);
                continue;
            }

            cancel.ThrowIfCancellationRequested();

            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                // 任一算法匹配即通过。首选算法第一次就过 → Message 留空（走默认文案）；
                // 靠后面的算法才匹配 → 把「哪个算法救回来的」写进 Message，便于排查。
                if (i == 0 && mismatches.Count == 0)
                {
                    return VerifyOutcome.Pass(algorithm);
                }
                return VerifyOutcome.Pass(algorithm + "（多算法复核，其余算法不符）");
            }

            mismatches.Add(algorithm + " 不一致（期望 " + expected + "，实际 " + actual + "）");
        }

        // 全部算法都不符**不直接判失败**：网盘接口给的哈希可能只是上传时的元数据，
        // 与实际下发内容不符（实测夸克就是这样）。标记 HashMismatch，
        // 交给上层抽样逐字节复核后再定性。
        return VerifyOutcome.Warn(string.Join("；", mismatches));
    }

    /// <summary>
    /// 创建哈希实例。
    /// <para>
    /// 🚨 2026-11-14：**必须用 CryptoServiceProvider 系列，不能用 <c>SHA256.Create()</c>**。
    /// </para>
    /// <para>
    /// <c>SHA256.Create()</c> 在 .NET Framework 上返回的是 <c>SHA256Managed</c> —— 纯托管实现，
    /// 单核只有约 100~200 MB/s。22GB 大文件收尾校验要白跑 110~220 秒，用户看起来像「卡在 99%」。
    /// </para>
    /// <para>
    /// <c>SHA256CryptoServiceProvider</c> 走系统 CNG/CAPI，支持 CPU 的 SHA-NI 指令集，
    /// 实测比托管实现快 3~5 倍。它要求「FIPS 兼容」，而我们这里只做本地文件摘要，
    /// 不涉及密钥派生，完全满足。算法结果与托管实现**逐位一致**（都是标准 SHA-256），
    /// 所以不会改变任何校验判定。
    /// </para>
    /// </summary>
    private static HashAlgorithm CreateHash(string algorithm)
    {
        switch (algorithm)
        {
            case "SHA-256":
                return new SHA256CryptoServiceProvider();
            case "SHA-1":
                return new SHA1CryptoServiceProvider();
            default:
                return new MD5CryptoServiceProvider();
        }
    }

    private static bool IsWeakEtag(string etag)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return false;
        }

        string trimmed = etag.TrimStart();
        return trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToHex(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return "";
        }

        var chars = new char[bytes.Length * 2];
        const string digits = "0123456789abcdef";
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = digits[bytes[i] >> 4];
            chars[i * 2 + 1] = digits[bytes[i] & 0x0F];
        }

        return new string(chars);
    }
}
