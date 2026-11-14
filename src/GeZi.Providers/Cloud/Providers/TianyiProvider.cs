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
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;
using GeZi.Providers.Quark;
using GeZi.Providers.Shared;

namespace GeZi.Providers.Cloud.Providers
{
    /// <summary>
    /// 天翼云盘 provider。
    ///
    /// 与其他平台的关键差异：
    /// 1. **分享用「分享码 + 访问码」**，不是 URL 里的 ID。
    ///    解析入口 <c>open/share/getShareInfoByCodeV2</c>，
    ///    必须返回**纯数字**的 shareId 才算有效（见 wenxi 的正则校验）。
    /// 2. 分享会话靠 **Cookie** 承载：<c>share_{shareId}={accessCode}</c>。
    /// 3. 有家庭云（family cloud），是只读协作区。
    /// 4. 下载前要拿 <c>downloadUrl</c>，再换一次真实地址。
    ///
    /// 实现范围：分享解析、个人盘浏览、取直链。
    /// 上传/家庭云等留待后续（接口已在注释标注）。
    /// </summary>
    public sealed class TianyiProvider : CloudProviderBase, ICloudTransferSupport
    {
        public override CloudPlatform Platform => CloudPlatform.Tianyi;

        private const string ApiBase = "https://api.cloud.189.cn";
        private const string WebOrigin = "https://cloud.189.cn";

        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly QuarkSession _session = new QuarkSession(null);

        // 天翼已实现：上传（RSA+AES 加密通道）、转存、读取
        // 未实现：重命名/移动/删除/建目录/建分享 → 如实声明 false
        public override bool SupportsUpload => true;

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var res = await GetJsonAsync(ApiBase + "/v2/getUserInfo.action", credential, null, ct)
                .ConfigureAwait(false);

            var account = new CloudAccount
            {
                Nickname = AsString(Get(res, "nickname")) ?? AsString(Get(res, "loginName")) ?? "",
                Used = AsLong(Get(res, "usedSize")),
                Total = AsLong(Get(res, "totalSize")),
            };

            if (string.IsNullOrEmpty(account.Nickname) && AsString(Get(res, "errorCode")) != null)
                throw new CloudApiException("天翼云盘：" +
                    (AsString(Get(res, "errorMessage")) ?? "读取账号信息失败"));

            return account;
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("天翼云盘：分享链接缺少分享码");

            string code = link.ShareId;
            string accessCode = (link.Passcode ?? "").Trim();

            // ① 按分享码取信息（不需要访问码时能直接拿到）
            var info = await GetShareInfoAsync(code, "", ct).ConfigureAwait(false);
            string shareId = AsString(Get(info, "shareId")) ?? "";

            // ② 需要访问码 / 上面没拿到 → 带上访问码再试
            if (!string.IsNullOrEmpty(accessCode) || string.IsNullOrEmpty(shareId))
            {
                var withCode = await GetShareInfoAsync(code, accessCode, ct).ConfigureAwait(false);
                shareId = AsString(Get(withCode, "shareId")) ?? "";
            }

            // ③ 校验：天翼的 shareId 必须是纯数字
            //    （wenxi 用 ^\d+$ 校验，不是纯数字说明返回的是错误结构）
            if (string.IsNullOrEmpty(shareId) || !IsAllDigits(shareId))
                throw new CloudApiException("天翼云盘：没有返回有效分享凭证，请确认访问码");

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Tianyi,
                Mode = BrowseMode.Share,
                Title = AsString(Get(info, "shareName")) ?? ("天翼分享 " + code),
                RootId = "-11",       // 天翼分享根目录的固定 id
                SourceLink = link,
            };
            session.SetMeta("shareId", shareId);
            session.SetMeta("shareCode", code);
            session.SetMeta("accessCode", accessCode);
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        private async Task<Dictionary<string, object>> GetShareInfoAsync(string code,
            string accessCode, CancellationToken ct)
        {
            var query = new Dictionary<string, string> { { "shareCode", code } };
            if (!string.IsNullOrEmpty(accessCode))
                query["accessCode"] = accessCode;

            var res = await GetJsonAsync(ApiBase + "/open/share/getShareInfoByCodeV2.action",
                null, query, ct).ConfigureAwait(false);

            // 天翼把有效载荷包在 ShareInfo 里
            return AsDict(Get(res, "ShareInfo")) ?? res;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Tianyi,
                Mode = BrowseMode.Personal,
                Title = "我的天翼云盘",
                RootId = "-11",
            };
            session.SetMeta("accountId", account.Nickname ?? credential?.Label ?? "");
            return session;
        }

        /// <summary>天翼的家庭云空间。</summary>
        public override async Task<IReadOnlyList<CloudSpace>> GetFamilySpacesAsync(
            CloudCredential credential, CancellationToken ct = default)
        {
            var result = new List<CloudSpace>();
            try
            {
                var res = await GetJsonAsync(ApiBase + "/family/manage/getFamilyList.action",
                    credential, null, ct).ConfigureAwait(false);

                var arr = AsArray(Get(res, "familyInfoList"));
                if (arr != null)
                {
                    foreach (var it in arr)
                    {
                        var d = AsDict(it);
                        if (d == null) continue;
                        string id = AsString(Get(d, "familyId")) ?? "";
                        string name = AsString(Get(d, "remarkName")) ?? "家庭云";
                        if (!string.IsNullOrEmpty(id))
                            result.Add(new CloudSpace(id, name));
                    }
                }
            }
            catch
            {
                // 没有家庭云 / 接口不可用时返回空，不打扰用户
            }
            return result;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null) return Array.Empty<CloudFile>();

            string folder = string.IsNullOrEmpty(parentId) ? session.RootId : parentId;

            if (session.Mode == BrowseMode.Share)
                return await ListShareAsync(session, folder, ct).ConfigureAwait(false);

            return await ListPersonalAsync(folder, credential, ct).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<CloudFile>> ListShareAsync(BrowseSession session,
            string folderId, CancellationToken ct)
        {
            var query = new Dictionary<string, string>
            {
                { "shareId", session.Meta("shareId") },
                { "folderId", folderId },
                { "fileType", "0" },
                { "orderBy", "filename" },
                { "descending", "false" },
                { "pageNum", "1" },
                { "pageSize", "200" },
            };

            // 天翼靠 Cookie 里的 share_{shareId}={accessCode} 认证分享
            string shareCookie = "share_" + session.Meta("shareId") + "=" + session.Meta("accessCode");

            var res = await GetJsonAsync(ApiBase + "/open/share/listShareDirV2.action",
                null, query, ct, extraCookie: shareCookie).ConfigureAwait(false);

            return ParseFileList(res, folderId, "fileId", "fileName");
        }

        private async Task<IReadOnlyList<CloudFile>> ListPersonalAsync(string folderId,
            CloudCredential credential, CancellationToken ct)
        {
            var query = new Dictionary<string, string>
            {
                { "folderId", folderId },
                { "fileType", "0" },
                { "orderBy", "filename" },
                { "descending", "false" },
                { "pageNum", "1" },
                { "pageSize", "200" },
            };

            var res = await GetJsonAsync(ApiBase + "/v2/listFiles.action",
                credential, query, ct).ConfigureAwait(false);

            return ParseFileList(res, folderId, "id", "name");
        }

        private static IReadOnlyList<CloudFile> ParseFileList(Dictionary<string, object> res,
            string parentId, string idKey, string nameKey)
        {
            var list = new List<CloudFile>();
            var arr = AsArray(Get(res, "fileList"));
            if (arr == null) return list;

            foreach (var it in arr)
            {
                var d = AsDict(it);
                if (d == null) continue;

                string name = AsString(Get(d, nameKey)) ?? "";

                list.Add(new CloudFile
                {
                    Id = AsString(Get(d, idKey)) ?? "",
                    Name = name,
                    Size = AsLong(Get(d, "size")),
                    // 天翼用 isFolder 布尔 + 名字后缀双重标记，认一个即可
                    IsDirectory = AsBool(Get(d, "isFolder")),
                    ParentId = parentId,
                    ModifiedAt = AsString(Get(d, "lastOpTime")) ?? AsString(Get(d, "createDate")) ?? "",
                    HashType = "md5",
                    HashValue = AsString(Get(d, "md5")) ?? "",
                });
            }
            return list;
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (file.IsDirectory)
                throw new CloudApiException("目录不能直接下载");

            if (session.Mode == BrowseMode.Share)
            {
                // 天翼分享同样需要转存后取链
                var spec = new DownloadSpec
                {
                    Url = "",
                    FileName = file.Name,
                    ExpectedSize = file.Size,
                };
                spec.Source["fid"] = file.Id;
                spec.Source["shareId"] = session.Meta("shareId");
                spec.Source["accessCode"] = session.Meta("accessCode");
                spec.Source["platform"] = CloudPlatform.Tianyi.Key;
                throw new CloudNewPreparationException(
                    "天翼云盘的分享文件需要先转存到自己账号才能下载。", spec);
            }

            var query = new Dictionary<string, string> { { "fileId", file.Id } };
            var res = await GetJsonAsync(ApiBase + "/v2/getFileDownloadUrl.action",
                credential, query, ct).ConfigureAwait(false);

            string url = AsString(Get(res, "downloadUrl"));
            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("天翼云盘：获取直链失败");

            var dspec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            dspec.Headers["User-Agent"] = Ua;
            return dspec;
        }

        // ================= HTTP =================

        private async Task<Dictionary<string, object>> GetJsonAsync(string url,
            CloudCredential credential, Dictionary<string, string> query,
            CancellationToken ct, string extraCookie = null)
        {
            var sb = new System.Text.StringBuilder(url);
            if (query != null && query.Count > 0)
            {
                sb.Append(url.Contains("?") ? "&" : "?");
                bool first = true;
                foreach (var kv in query)
                {
                    if (!first) sb.Append("&");
                    sb.Append(Uri.EscapeDataString(kv.Key)).Append("=")
                      .Append(Uri.EscapeDataString(kv.Value ?? ""));
                    first = false;
                }
            }

            using (var req = new HttpRequestMessage(HttpMethod.Get, sb.ToString()))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", Ua);
                req.Headers.TryAddWithoutValidation("Referer", WebOrigin + "/");
                req.Headers.TryAddWithoutValidation("Origin", WebOrigin);
                req.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");

                string cookie = credential?.Primary;
                if (!string.IsNullOrEmpty(extraCookie))
                    cookie = string.IsNullOrEmpty(cookie) ? extraCookie : cookie + "; " + extraCookie;

                if (!string.IsNullOrEmpty(cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);

                using (var resp = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try
                    {
                        return JsonCompat.Deserialize(text) as Dictionary<string, object>
                               ?? new Dictionary<string, object>();
                    }
                    catch
                    {
                        throw new CloudApiException(
                            "天翼云盘：响应不是有效 JSON（HTTP " + (int)resp.StatusCode + "）");
                    }
                }
            }
        }

        // ================= 取值辅助 =================

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static object Get(object obj, string key)
        {
            var d = obj as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out var v) ? v : null;
        }

        private static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;
        private static object[] AsArray(object o) => o as object[];
        private static string AsString(object o) => o as string;

        private static long AsLong(object o)
        {
            if (o == null) return 0;
            if (o is long l) return l;
            if (o is int i) return i;
            if (o is double d) return (long)d;
            if (o is decimal m) return (long)m;
            if (o is string s) return long.TryParse(s, out var v) ? v : 0;
            return 0;
        }

        private static bool AsBool(object o)
        {
            if (o is bool b) return b;
            if (o is string s)
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
            return AsLong(o) != 0;
        }

        // ================= ICloudTransferSupport（转存） =================
        //
        // 天翼用统一的 createBatchTask 接口，type=SHARE_SAVE 表示转存。
        // 参数取自 wenxi：8 次 × 0.8s 轮询，可见性 = 名字 + 大小。

        private const int TransferAttempts = 8;
        private const int TransferIntervalMs = 800;

        public async Task<string> CreateTempFolderAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string name = CloudTransferFlow.NewTempFolderName();
            string parentId = "-11";   // 天翼根目录固定 id

            // 天翼建目录：open/batch/createBatchTask type=CREATE_FOLDER
            var body = new Dictionary<string, object>
            {
                { "type", "CREATE_FOLDER" },
                { "taskInfos", new[] { new Dictionary<string, object>
                    {
                        { "fileName", name },
                        { "parentFolderId", parentId },
                    }
                } },
            };

            var res = await PostJsonAsync(ApiBase + "/open/batch/createBatchTask.action",
                body, credential, ct).ConfigureAwait(false);

            string id = CloudJson.StrPath(res, "taskInfoList.0.fileId");
            if (string.IsNullOrEmpty(id)) id = CloudJson.StrPath(res, "data.fileId");

            if (string.IsNullOrEmpty(id))
                throw new CloudApiException("天翼：创建临时目录失败（"
                    + (CloudJson.Str(res, "res_message") ?? "未知原因") + "）");

            return id;
        }

        public async Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct)
        {
            spec.Source.TryGetValue("fid", out var fid);
            spec.Source.TryGetValue("shareId", out var shareId);

            if (string.IsNullOrEmpty(fid))
                throw new CloudApiException("天翼：缺少文件 ID，请重新解析分享");

            var body = new Dictionary<string, object>
            {
                { "type", "SHARE_SAVE" },
                { "taskInfos", new[] { new Dictionary<string, object>
                    {
                        { "fileId", fid },
                        { "parentFolderId", targetFolderId },
                        { "shareId", shareId ?? "" },
                        { "copyType", 1 },
                    }
                } },
            };

            var res = await PostJsonAsync(ApiBase + "/open/batch/createBatchTask.action",
                body, credential, ct).ConfigureAwait(false);

            string taskId = CloudJson.Str(res, "taskId");
            if (string.IsNullOrEmpty(taskId)) taskId = CloudJson.StrPath(res, "data.taskId");

            if (string.IsNullOrEmpty(taskId))
                throw new CloudApiException("天翼：转存请求失败（"
                    + (CloudJson.Str(res, "res_message") ?? "未知原因") + "）");

            return taskId;
        }

        public async Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct)
        {
            // 天翼的转存是异步任务，先等任务完成再查目录
            if (saveResult is string taskId && !string.IsNullOrEmpty(taskId))
            {
                for (int i = 0; i < TransferAttempts; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var q = await GetJsonAsync(ApiBase + "/open/batch/checkBatchTask.action",
                            credential, new Dictionary<string, string> { { "taskId", taskId } }, ct)
                            .ConfigureAwait(false);

                        long status = CloudJson.Num(q, "taskStatus");
                        if (status == 2 || status == 4) break;   // 成功
                        if (status == 3)                          // 失败
                            throw new CloudApiException("天翼：转存任务失败");
                    }
                    catch (CloudApiException) { throw; }
                    catch { }

                    onPhase?.Invoke(string.Format("等待转存任务（{0}/{1}）", i + 1, TransferAttempts));
                    await Task.Delay(TransferIntervalMs, ct).ConfigureAwait(false);
                }
            }

            return await TransferWaiter.WaitAsync(TransferAttempts, TransferIntervalMs,
                async token =>
                {
                    var items = await ListPersonalAsync(folderId, credential, token)
                        .ConfigureAwait(false);
                    return TransferWaiter.MatchUnique(items, spec.FileName, spec.ExpectedSize);
                }, onPhase, ct).ConfigureAwait(false);
        }

        public async Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct)
        {
            var query = new Dictionary<string, string> { { "fileId", savedFile.Id }, { "dt", "1" } };

            var res = await GetJsonAsync(ApiBase + "/open/file/getFileDownloadUrl.action",
                credential, query, ct).ConfigureAwait(false);

            // ⚠️ 天翼的响应会把 & 转成 &amp;，必须还原，否则签名参数会被拆坏
            string url = (CloudJson.Str(res, "fileDownloadUrl") ?? "").Replace("&amp;", "&");
            if (string.IsNullOrEmpty(url)) return null;

            // 天翼的 http://download.cloud.189.cn 要升到 https
            if (url.StartsWith("http://download.cloud.189.cn", StringComparison.OrdinalIgnoreCase))
                url = "https" + url.Substring(4);

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = savedFile.Name,
                ExpectedSize = savedFile.Size > 0 ? savedFile.Size : originalSpec.ExpectedSize,
            };
            spec.Headers["User-Agent"] = Ua;
            return spec;
        }

        public async Task DeleteTempFolderAsync(string folderId, CloudCredential credential,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(folderId)) return;

            try
            {
                var body = new Dictionary<string, object>
                {
                    { "type", "DELETE" },
                    { "taskInfos", new[] { new Dictionary<string, object>
                        {
                            { "fileId", folderId },
                        }
                    } },
                };

                await PostJsonAsync(ApiBase + "/open/batch/createBatchTask.action",
                    body, credential, ct).ConfigureAwait(false);
            }
            catch
            {
                // 清理失败不影响下载
            }
        }

        /// <summary>POST JSON 到天翼并返回解析后的字典。</summary>
        private async Task<Dictionary<string, object>> PostJsonAsync(string url, object body,
            CloudCredential credential, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", Ua);
                req.Headers.TryAddWithoutValidation("Referer", WebOrigin + "/");
                req.Headers.TryAddWithoutValidation("Origin", WebOrigin);
                req.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");

                string cookie = credential?.Primary;
                if (!string.IsNullOrEmpty(cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);

                req.Content = new StringContent(JsonCompat.Serialize(body),
                    System.Text.Encoding.UTF8, "application/json");

                using (var resp = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonCompat.Deserialize(text) as Dictionary<string, object>
                           ?? new Dictionary<string, object>();
                }
            }
        }

        // ================= 上传 =================
        //
        // 天翼上传走**独立加密通道**（与普通 API 完全不同）：
        //   ① 取 sessionKey
        //   ② 取服务端 RSA 公钥 + pkId
        //   ③ 每次调用：随机 24 位 secret → AES-128-ECB 加密 params
        //      → HMAC-SHA1 签名 → RSA 加密 secret 放进 EncryptionText 头
        //   ④ initMultiUpload → getMultiUploadUrls（每片预签名 URL）
        //      → PUT 每片 → commitMultiUploadFile
        //
        // 复杂度主要在第 ③ 步，已抽到 CloudCrypto.TianyiSign。

        private const long TianyiPartSize = 10L * 1024 * 1024;

        public override async Task<CloudFile> UploadAsync(BrowseSession session,
            string parentId, string localPath, CloudCredential credential,
            IProgress<UploadProgress> progress = null, CancellationToken ct = default)
        {
            if (session == null || session.Mode != BrowseMode.Personal)
                throw new CloudNotSupportedException("上传：请在「我的网盘」里操作");

            if (!File.Exists(localPath))
                throw new CloudApiException("上传失败：文件不存在 " + localPath);

            var info = new FileInfo(localPath);
            string fileName = info.Name;
            long size = info.Length;

            void Report(string phase, long done, long total, double speed = 0)
            {
                progress?.Report(new UploadProgress
                {
                    Phase = phase, Done = done, Total = total, Speed = speed,
                });
            }

            Report("准备上传", 0, size);

            // ---- ① 上传会话密钥 ----
            var skRes = await GetJsonAsync(ApiBase + "/person/initMultiUpload",
                credential, null, ct).ConfigureAwait(false);
            string sessionKey = CloudJson.Str(skRes, "sessionKey");

            if (string.IsNullOrEmpty(sessionKey))
                throw new CloudApiException("天翼：未取到上传会话密钥");

            // ---- ② RSA 公钥 ----
            var rsaRes = await GetJsonAsync(ApiBase + "/security/generateRsaKey",
                credential, null, ct).ConfigureAwait(false);

            string pubKey = CloudJson.Str(rsaRes, "pubKey");
            string pkId = CloudJson.Str(rsaRes, "pkId");

            if (string.IsNullOrEmpty(pubKey) || string.IsNullOrEmpty(pkId))
                throw new CloudApiException("天翼：未返回上传加密信息");

            // ---- ③ 算哈希 ----
            Report("计算校验值", 0, size);

            string fileMd5 = await CloudCrypto.Md5FileAsync(localPath, ct).ConfigureAwait(false);

            int partCount = (int)Math.Max(1, (size + TianyiPartSize - 1) / TianyiPartSize);
            var partHashes = new List<string>(partCount);

            for (int i = 0; i < partCount; i++)
            {
                long start = i * TianyiPartSize;
                long end = Math.Min(size, (i + 1) * TianyiPartSize);
                partHashes.Add(await CloudCrypto.Md5RangeAsync(localPath, start, end, ct)
                    .ConfigureAwait(false));
            }

            string sliceMd5 = partCount == 1
                ? fileMd5
                : CloudCrypto.Md5Hex(System.Text.Encoding.UTF8.GetBytes(
                    string.Join("\n", partHashes)));

            // ---- ④ initMultiUpload ----
            var pre = await TianyiUploadCallAsync("/person/initMultiUpload", sessionKey,
                pkId, pubKey, credential, new Dictionary<string, string>
                {
                    { "parentFolderId", string.IsNullOrEmpty(parentId) ? "-11" : parentId },
                    { "fileName", Uri.EscapeDataString(fileName) },
                    { "fileSize", size.ToString() },
                    { "sliceSize", TianyiPartSize.ToString() },
                    { "fileMd5", fileMd5 },
                    { "sliceMd5", sliceMd5 },
                }, ct).ConfigureAwait(false);

            var preData = CloudJson.Dict(CloudJson.Get(pre, "data")) ?? pre;
            string uploadId = CloudJson.Str(preData, "uploadFileId");

            if (string.IsNullOrEmpty(uploadId))
                throw new CloudApiException("天翼：未创建上传任务");

            bool deduped = CloudJson.Num(preData, "fileDataExists") == 1;

            if (!deduped)
            {
                long uploaded = 0;
                var started = DateTime.UtcNow;

                for (int i = 0; i < partCount; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    byte[] hashBytes = CloudCrypto.FromHex(partHashes[i]);
                    string partInfo = (i + 1) + "-" + Convert.ToBase64String(hashBytes);

                    var urlRes = await TianyiUploadCallAsync("/person/getMultiUploadUrls",
                        sessionKey, pkId, pubKey, credential,
                        new Dictionary<string, string>
                        {
                            { "partInfo", partInfo },
                            { "uploadFileId", uploadId },
                        }, ct).ConfigureAwait(false);

                    var part = CloudJson.Dict(CloudJson.Path(urlRes,
                        "uploadUrls.partNumber_" + (i + 1)));
                    if (part == null)
                        throw new CloudApiException(string.Format(
                            "天翼：未拿到第 {0} 片的上传地址", i + 1));

                    string requestUrl = CloudJson.Str(part, "requestURL");
                    string headerStr = CloudJson.Str(part, "requestHeader");

                    var headers = new Dictionary<string, string>();
                    foreach (var pair in Uri.UnescapeDataString(headerStr ?? "").Split('&'))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0) headers[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                    }

                    long start = i * TianyiPartSize;
                    int length = (int)(Math.Min(size, start + TianyiPartSize) - start);
                    byte[] buffer = new byte[length];

                    using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, useAsync: true))
                    {
                        fs.Seek(start, SeekOrigin.Begin);
                        int read = 0;
                        while (read < length)
                        {
                            int n = await fs.ReadAsync(buffer, read, length - read, ct)
                                .ConfigureAwait(false);
                            if (n <= 0) break;
                            read += n;
                        }
                    }

                    using (var req = new HttpRequestMessage(HttpMethod.Put, requestUrl))
                    {
                        foreach (var kv in headers)
                            try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { }

                        req.Content = new ByteArrayContent(buffer);

                        using (var resp = await _session.Http.SendAsync(req, ct)
                            .ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                                throw new CloudApiException(string.Format(
                                    "天翼：第 {0} 片上传失败 HTTP {1}",
                                    i + 1, (int)resp.StatusCode));
                        }
                    }

                    uploaded += length;
                    double elapsed = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                    Report("上传中", uploaded, size, uploaded / elapsed);
                }
            }

            // ---- ⑤ 提交 ----
            Report("提交", size, size);

            var commit = await TianyiUploadCallAsync("/person/commitMultiUploadFile",
                sessionKey, pkId, pubKey, credential,
                new Dictionary<string, string>
                {
                    { "uploadFileId", uploadId },
                    { "fileMd5", fileMd5 },
                    { "sliceMd5", sliceMd5 },
                    { "lazyCheck", "1" },
                }, ct).ConfigureAwait(false);

            var commitData = CloudJson.Dict(CloudJson.Get(commit, "data")) ?? commit;
            string newId = CloudJson.Str(commitData, "fileId");
            if (string.IsNullOrEmpty(newId)) newId = CloudJson.Str(commitData, "id");

            Report("完成", size, size);

            return new CloudFile
            {
                Id = newId ?? uploadId,
                Name = fileName,
                Size = size,
                IsDirectory = false,
                ParentId = parentId,
            };
        }

        /// <summary>
        /// 发一次**加密通道**的天翼上传请求。
        /// 三个头缺一不可：SessionKey / Signature / EncryptionText。
        /// </summary>
        private async Task<Dictionary<string, object>> TianyiUploadCallAsync(string path,
            string sessionKey, string pkId, string pubKey,
            CloudCredential credential, IDictionary<string, string> param,
            CancellationToken ct)
        {
            var sig = CloudCrypto.TianyiSign(path, sessionKey, param, pubKey);

            string url = "https://upload.cloud.189.cn" + path +
                         "?params=" + Uri.EscapeDataString(sig.EncryptedParams);

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
                req.Headers.TryAddWithoutValidation("SessionKey", sessionKey);
                req.Headers.TryAddWithoutValidation("Signature", sig.Signature);
                req.Headers.TryAddWithoutValidation("X-Request-Date", sig.Date);
                req.Headers.TryAddWithoutValidation("X-Request-ID", Guid.NewGuid().ToString("N"));
                req.Headers.TryAddWithoutValidation("EncryptionText", sig.EncryptionText);
                req.Headers.TryAddWithoutValidation("PkId", pkId);
                req.Headers.TryAddWithoutValidation("User-Agent", Ua);

                string cookie = credential?.Primary;
                if (!string.IsNullOrEmpty(cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);

                using (var resp = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var parsed = JsonCompat.Deserialize(text) as Dictionary<string, object>
                                 ?? new Dictionary<string, object>();

                    string code = CloudJson.Str(parsed, "code");
                    if (!string.IsNullOrEmpty(code) && code != "SUCCESS")
                    {
                        if (code == "InsufficientStorageSpace")
                            throw new CloudApiException("天翼云盘剩余空间不足，无法上传");

                        throw new CloudApiException("天翼上传请求失败（" + code + "）");
                    }

                    return parsed;
                }
            }
        }

    }
}
