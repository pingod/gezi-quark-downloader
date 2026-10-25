// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Support;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 夸克/UC 的上传实现（OSS 分片上传 + 秒传）。
    ///
    /// 完整流程（照抄 quark_upload.dart）：
    /// <code>
    /// ① POST file/upload/pre      → task_id / bucket / obj_key / upload_id / auth_info
    /// ② POST file/update/hash     → 传 md5 + sha1，若返回 finish=true 则**秒传成功**
    /// ③ POST file/upload/auth     → 用 auth_meta 换 OSS 签名 auth_key
    /// ④ PUT  OSS（带签名）          → 上传分片
    /// ⑤ POST file/upload/finish   → 提交，拿到 fid
    /// </code>
    ///
    /// 秒传是关键优化：如果服务端已有相同文件（md5+sha1 命中），
    /// 第 ② 步就 finish=true，整个上传瞬间完成，不用传字节。
    ///
    /// OSS 签名格式（阿里云 V1）：
    ///   Authorization: OSS {accessKeyId}:{signature}
    ///   signature = Base64(HMAC-SHA1(accessKeySecret, StringToSign))
    ///   StringToSign = VERB\nContent-MD5\nContent-Type\nDate\nCanonicalizedOSSHeaders\nCanonicalizedResource
    /// </summary>
    internal static class QuarkUploader
    {
        /// <summary>分片大小：8MB（OSS 最小 100KB，但太小会导致请求数爆炸）。</summary>
        private const long PartSize = 8L * 1024 * 1024;

        /// <summary>超过这个大小才分片；否则单次 PUT。</summary>
        private const long MultipartThreshold = 8L * 1024 * 1024;

        public sealed class UploadResult
        {
            public string Fid { get; set; } = "";
            public string Name { get; set; } = "";
            public long Size { get; set; }

            /// <summary>是否走了秒传（没有真正上传字节）。</summary>
            public bool Deduped { get; set; }
        }

        /// <summary>
        /// 上传一个本地文件。
        /// </summary>
        public static async Task<UploadResult> UploadAsync(
            QuarkClient client,
            string pdirFid,
            string localPath,
            IProgress<UploadProgress> progress = null,
            CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (!File.Exists(localPath))
                throw new CloudApiException("上传失败：文件不存在 " + localPath);

            var info = new FileInfo(localPath);
            string fileName = info.Name;
            long size = info.Length;

            void Report(string phase, long done, long total, double speed = 0)
            {
                progress?.Report(new UploadProgress
                {
                    Phase = phase,
                    Done = done,
                    Total = total,
                    Speed = speed,
                });
            }

            Report("准备上传", 0, size);

            // ---- ① 预上传 ----
            var pre = await client.RequestRawAsync(
                System.Net.Http.HttpMethod.Post,
                "/1/clouddrive/file/upload/pre",
                new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                },
                new
                {
                    ccp_hash_update = true,
                    dir_name = "",
                    file_name = fileName,
                    format_type = GuessMime(fileName),
                    pdir_fid = string.IsNullOrEmpty(pdirFid) ? "0" : pdirFid,
                    size,
                    l_created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    l_updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    same_path_reuse = false,
                }).ConfigureAwait(false);

            var preData = CloudJson.Dict(CloudJson.Get(pre, "data"));
            string taskId = CloudJson.Str(preData, "task_id");
            if (string.IsNullOrEmpty(taskId))
                throw new CloudApiException("上传失败：网盘未返回上传任务");

            // ---- ② 算哈希 + 秒传检测 ----
            Report("计算校验值", 0, size);

            string md5 = await CloudCrypto.Md5FileAsync(localPath, ct).ConfigureAwait(false);
            string sha1 = await Sha1FileAsync(localPath, ct).ConfigureAwait(false);

            var hashRes = await client.RequestRawAsync(
                System.Net.Http.HttpMethod.Post,
                "/1/clouddrive/file/update/hash",
                new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                },
                new { md5, sha1, task_id = taskId }).ConfigureAwait(false);

            // 秒传命中 → 直接完成
            if (CloudJson.Bool(CloudJson.Get(hashRes, "data"), "finish"))
            {
                Report("秒传完成", size, size);
                return new UploadResult
                {
                    Fid = CloudJson.StrPath(hashRes, "data.fid"),
                    Name = fileName,
                    Size = size,
                    Deduped = true,
                };
            }

            // ---- ③~④ 上传到 OSS ----
            string bucket = CloudJson.Str(preData, "bucket");
            string objKey = CloudJson.Str(preData, "obj_key");
            string uploadId = CloudJson.Str(preData, "upload_id");
            string uploadUrl = CloudJson.Str(preData, "upload_url");

            if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(objKey))
                throw new CloudApiException("上传失败：网盘未返回完整上传凭据");

            bool multipart = size > MultipartThreshold && !string.IsNullOrEmpty(uploadId);

            if (multipart)
            {
                await UploadMultipartAsync(client, localPath, size, bucket, objKey,
                    uploadId, uploadUrl, taskId, Report, ct).ConfigureAwait(false);
            }
            else
            {
                await UploadSingleAsync(client, localPath, size, bucket, objKey,
                    uploadUrl, taskId, md5, Report, ct).ConfigureAwait(false);
            }

            // ---- ⑤ 提交 ----
            Report("提交", size, size);

            var finish = await client.RequestRawAsync(
                System.Net.Http.HttpMethod.Post,
                "/1/clouddrive/file/upload/finish",
                new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                },
                new { task_id = taskId, obj_key = objKey }).ConfigureAwait(false);

            string fid = CloudJson.StrPath(finish, "data.fid");
            if (string.IsNullOrEmpty(fid)) fid = CloudJson.StrPath(finish, "data.file.fid");

            if (string.IsNullOrEmpty(fid))
                throw new CloudApiException("上传失败：提交后未返回文件 ID");

            Report("完成", size, size);

            return new UploadResult { Fid = fid, Name = fileName, Size = size };
        }

        // ================= 单次上传 =================

        private static async Task UploadSingleAsync(QuarkClient client, string localPath,
            long size, string bucket, string objKey, string uploadUrl, string taskId,
            string md5, Action<string, long, long, double> report, CancellationToken ct)
        {
            string authKey = await RequestAuthAsync(client, taskId,
                "PUT\n\n" + GuessMime(Path.GetFileName(localPath)) + "\n" +
                DateTime.UtcNow.ToString("R") + "\n" + "/" + bucket + "/" + objKey)
                .ConfigureAwait(false);

            string baseUrl = string.IsNullOrEmpty(uploadUrl) ? "https://oss.quark.cn" : uploadUrl;
            var uri = new Uri(baseUrl);
            string host = uri.Host.StartsWith(bucket + ".", StringComparison.Ordinal)
                ? uri.Host : bucket + "." + uri.Host;
            string url = "https://" + host + "/" + objKey.TrimStart('/');

            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true))
            using (var content = new StreamContent(fs))
            {
                content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(GuessMime(
                        Path.GetFileName(localPath)));

                using (var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Put, url))
                {
                    req.Content = content;
                    req.Headers.TryAddWithoutValidation("Authorization", authKey);
                    // net48 没有 MD5.HashData，用传统 API
                    using (var md5Alg = System.Security.Cryptography.MD5.Create())
                    {
                        byte[] digest = md5Alg.ComputeHash(
                            System.Text.Encoding.UTF8.GetBytes(md5));
                        req.Headers.TryAddWithoutValidation("Content-MD5",
                            Convert.ToBase64String(digest));
                    }

                    var started = DateTime.UtcNow;
                    var resp = await SendWithProgressAsync(client, req,
                        size, report, "上传中", started, ct).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        throw new CloudApiException(
                            "上传失败：OSS 返回 HTTP " + (int)resp.StatusCode);
                }
            }
        }

        // ================= 分片上传 =================

        private static async Task UploadMultipartAsync(QuarkClient client, string localPath,
            long size, string bucket, string objKey, string uploadId, string uploadUrl,
            string taskId, Action<string, long, long, double> report, CancellationToken ct)
        {
            int partCount = (int)((size + PartSize - 1) / PartSize);
            var etags = new List<string>(partCount);

            var started = DateTime.UtcNow;
            long uploaded = 0;

            for (int i = 0; i < partCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                long offset = i * PartSize;
                int length = (int)Math.Min(PartSize, size - offset);

                string authMeta = "PUT\n\n\n" + DateTime.UtcNow.ToString("R") + "\n" +
                    "/" + bucket + "/" + objKey + "?partNumber=" + (i + 1) +
                    "&uploadId=" + uploadId;

                string authKey = await RequestAuthAsync(client, taskId, authMeta)
                    .ConfigureAwait(false);

                byte[] buffer = new byte[length];
                using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, useAsync: true))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    int read = 0;
                    while (read < length)
                    {
                        int n = await fs.ReadAsync(buffer, read, length - read, ct)
                            .ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                }

                var uri = new Uri(string.IsNullOrEmpty(uploadUrl)
                    ? "https://oss.quark.cn" : uploadUrl);
                string host = uri.Host.StartsWith(bucket + ".", StringComparison.Ordinal)
                    ? uri.Host : bucket + "." + uri.Host;

                string url = "https://" + host + "/" + objKey.TrimStart('/') +
                             "?partNumber=" + (i + 1) + "&uploadId=" + Uri.EscapeDataString(uploadId);

                using (var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Put, url))
                {
                    req.Content = new ByteArrayContent(buffer);
                    req.Headers.TryAddWithoutValidation("Authorization", authKey);

                    var resp = await SendWithProgressAsync(client, req, length,
                        (phase, done, total, speed) =>
                        {
                            report(phase, uploaded + done, size, speed);
                        }, "上传中", started, ct).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        throw new CloudApiException(
                            "上传分片 " + (i + 1) + " 失败：HTTP " + (int)resp.StatusCode);

                    string etag = resp.Headers.ETag?.Tag?.Trim('"') ?? "";
                    etags.Add(etag);
                }

                uploaded += length;
                report("上传中", uploaded, size,
                    uploaded / Math.Max(1, (DateTime.UtcNow - started).TotalSeconds));
            }

            // 注意：夸克的 OSS 上传完成后由 upload/finish 提交，
            // 不需要额外的 CompleteMultipartUpload（服务端按 upload_id 自己组装）。
        }

        // ================= 辅助 =================

        /// <summary>向网盘申请 OSS 上传授权（auth_key）。</summary>
        private static async Task<string> RequestAuthAsync(QuarkClient client,
            string taskId, string authMeta)
        {
            var res = await client.RequestRawAsync(
                System.Net.Http.HttpMethod.Post,
                "/1/clouddrive/file/upload/auth",
                new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                },
                new { auth_info = "", auth_meta = authMeta, task_id = taskId })
                .ConfigureAwait(false);

            string key = CloudJson.Str(CloudJson.Get(res, "data"), "auth_key");
            if (string.IsNullOrEmpty(key))
                throw new CloudApiException("上传失败：网盘未签发上传授权");

            return key;
        }

        /// <summary>发请求并上报上传进度。</summary>
        private static async Task<System.Net.Http.HttpResponseMessage> SendWithProgressAsync(
            QuarkClient client, System.Net.Http.HttpRequestMessage req,
            long contentLength, Action<string, long, long, double> report,
            string phase, DateTime started, CancellationToken ct)
        {
            // 简化处理：直接发送，按完成时刻算平均速度。
            // 精确的流式进度需要包一层 ProgressableStreamContent，收益不大。
            var resp = await client.SendRawAsync(req, ct).ConfigureAwait(false);

            double elapsed = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
            report(phase, contentLength, contentLength, contentLength / elapsed);

            return resp;
        }

        private static async Task<string> Sha1FileAsync(string path, CancellationToken ct)
        {
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)
                    .ConfigureAwait(false)) > 0)
                {
                    sha1.TransformBlock(buffer, 0, read, null, 0);
                }
                sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return CloudCrypto.ToHex(sha1.Hash);
            }
        }

        /// <summary>按扩展名猜 MIME（夸克需要用它决定在线预览行为）。</summary>
        private static string GuessMime(string fileName)
        {
            string ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".mp4": case ".m4v": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".avi": return "video/x-msvideo";
                case ".mov": return "video/quicktime";
                case ".mp3": return "audio/mpeg";
                case ".flac": return "audio/flac";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".rar": return "application/vnd.rar";
                case ".7z": return "application/x-7z-compressed";
                case ".txt": return "text/plain";
                case ".json": return "application/json";
                default: return "application/octet-stream";
            }
        }
    }
}
