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
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 123 网盘 provider。
    ///
    /// 协议要点（参照 wenxi 的 pan123.dart）：
    /// - 分享列表接口**不需要登录**，路径 <c>/b/api/share/get</c>，
    ///   参数 shareKey / ParentFileId / Page / SharePwd。
    /// - 下载必须登录（个人盘），走 <c>/api/file/download_info</c> 取直链。
    /// - 分享文件想下载要先**转存到自己账号**（copy/save），
    ///   所以这是典型的 <see cref="DownloadSpec.NeedsPreparation"/> = true 平台。
    /// - 有严格的 429 限流：列表请求之间要留间隔，否则会被冷却。
    ///
    /// 本实现覆盖「浏览分享 / 浏览个人盘 / 取直链」，
    /// 转存与上传留待后续（接口路径已在注释里标注，便于补）。
    /// </summary>
    public sealed class Pan123Provider : CloudProviderBase, ICloudTransferSupport
    {
        public override CloudPlatform Platform => CloudPlatform.Pan123;

        private const string ShareApi = "https://yun.123pan.cn/b/api/share/get";
        private const string FileListApi = "https://yun.123pan.cn/b/api/file/list/new";
        private const string DownloadInfoApi = "https://yun.123pan.cn/api/file/download_info";
        private const string UserInfoApi = "https://yun.123pan.cn/b/api/user/info";
        private const string FileCreateApi = "https://yun.123pan.cn/b/api/file";
        private const string TrashApi = "https://yun.123pan.cn/b/api/file/trash";
        private const string CopySaveApi = "https://yun.123pan.cn/b/api/restful/goapi/v1/file/copy/save";
        private const string CopySaveGetApi = "https://yun.123pan.cn/b/api/restful/goapi/v1/file/copy/save/get";

        // 123 的 UA 就是普通浏览器 UA（它不像夸克那样挑剔客户端指纹）
        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // 123 已实现：上传（S3+秒传）、转存
        // 未实现：重命名/移动/删除/建目录/建分享 → 如实声明 false
        public override bool SupportsUpload => true;
        public override bool SupportsRename => false;
        public override bool SupportsMove => false;
        public override bool SupportsDelete => false;
        public override bool SupportsCreateFolder => false;
        public override bool SupportsCreateShare => false;

        // 列表限流：两次列表请求之间至少隔这么久。
        // 123 对匿名列表查得很严，连续快速翻页会直接 429。
        private static DateTime _nextListAt = DateTime.MinValue;
        private static int _listIntervalMs = 350;
        private static readonly object ListGate = new object();

        private readonly QuarkSession _session;

        public Pan123Provider()
        {
            // 复用 QuarkSession 的 HttpClient 封装（统一请求头、重试、节流），
            // 只是不带任何夸克专属语义 —— 它本质是个带 Cookie 容器的 HTTP 客户端。
            _session = new QuarkSession(null);
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var res = await GetJsonAsync(UserInfoApi, credential, null, ct).ConfigureAwait(false);

            var data = AsDict(Get(res, "data"));
            if (data == null)
                throw new CloudApiException("123 网盘：读取账号信息失败");

            var account = new CloudAccount
            {
                Nickname = AsString(Get(data, "Nickname")) ?? AsString(Get(data, "nickname")) ?? "",
                Used = AsLong(Get(data, "UsedSpace")),
                Total = AsLong(Get(data, "SpacePermanent")) + AsLong(Get(data, "SpaceTemp")),
            };
            return account;
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("123 分享链接缺少分享 Key");

            // 先拉一次根目录，确认分享有效（顺便取标题）
            var root = await ListShareAsync(link.ShareId, link.Passcode, "0", credential, ct)
                .ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Pan123,
                Mode = BrowseMode.Share,
                // 123 的分享没有"分享名"这个概念，用第一个条目名或固定文案
                Title = root.Count > 0 ? "123 分享" : "123 分享",
                RootId = "0",
                SourceLink = link,
            };

            session.SetMeta("shareKey", link.ShareId);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var account = await GetAccountAsync(credential, ct).ConfigureAwait(false);

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Pan123,
                Mode = BrowseMode.Personal,
                Title = "我的 123 网盘",
                RootId = "0",
            };
            session.SetMeta("accountId", account.Nickname ?? credential?.Label ?? "");
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null) return Array.Empty<CloudFile>();

            if (session.Mode == BrowseMode.Share)
            {
                return await ListShareAsync(session.Meta("shareKey"), session.Meta("passcode"),
                    parentId, credential, ct).ConfigureAwait(false);
            }
            return await ListPersonalAsync(parentId, credential, ct).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<CloudFile>> ListShareAsync(string key, string password,
            string parent, CloudCredential credential, CancellationToken ct)
        {
            await ThrottleListAsync(ct).ConfigureAwait(false);

            var query = new Dictionary<string, string>
            {
                { "limit", "100" },
                { "next", "0" },
                { "orderBy", "file_name" },
                { "orderDirection", "asc" },
                { "shareKey", key ?? "" },
                { "ParentFileId", string.IsNullOrEmpty(parent) ? "0" : parent },
                { "Page", "1" },
            };
            if (!string.IsNullOrEmpty(password))
                query["SharePwd"] = password;

            var res = await GetJsonAsync(ShareApi, null, query, ct, useUa: true).ConfigureAwait(false);

            var data = AsDict(Get(res, "data"));
            if (data == null)
                throw new CloudApiException("123 分享：读取列表失败");

            if (AsBool(Get(data, "Expired")))
                throw new CloudApiException("123 分享已失效");

            return ParseFileList(data, string.IsNullOrEmpty(parent) ? "0" : parent);
        }

        private async Task<IReadOnlyList<CloudFile>> ListPersonalAsync(string parentId,
            CloudCredential credential, CancellationToken ct)
        {
            await ThrottleListAsync(ct).ConfigureAwait(false);

            var query = new Dictionary<string, string>
            {
                { "driveId", "0" },
                { "limit", "100" },
                { "next", "0" },
                { "orderBy", "file_name" },
                { "orderDirection", "asc" },
                { "parentFileId", string.IsNullOrEmpty(parentId) ? "0" : parentId },
                { "trashed", "false" },
                { "Page", "1" },
            };

            var res = await GetJsonAsync(FileListApi, credential, query, ct).ConfigureAwait(false);
            var data = AsDict(Get(res, "data"));
            if (data == null)
                throw new CloudApiException("123 网盘：读取目录失败");

            return ParseFileList(data, string.IsNullOrEmpty(parentId) ? "0" : parentId);
        }

        /// <summary>123 的列表返回 → 统一模型。</summary>
        private static IReadOnlyList<CloudFile> ParseFileList(Dictionary<string, object> data,
            string parentId)
        {
            var list = new List<CloudFile>();

            // 分享接口把条目放在 InfoList，个人盘接口放在 InfoList/FileList
            var arr = AsArray(Get(data, "InfoList")) ?? AsArray(Get(data, "FileList"));
            if (arr == null) return list;

            foreach (var item in arr)
            {
                var d = AsDict(item);
                if (d == null) continue;

                string id = AsStringOrNumber(Get(d, "FileId")) ?? AsStringOrNumber(Get(d, "FileID")) ?? "";
                string name = AsString(Get(d, "FileName")) ?? "";

                // 123 用 Type 区分：0=文件 1=文件夹
                long type = AsLong(Get(d, "Type"));
                bool isDir = type == 1 || AsBool(Get(d, "IsFolder"));

                list.Add(new CloudFile
                {
                    Id = id,
                    Name = name,
                    Size = AsLong(Get(d, "Size")),
                    IsDirectory = isDir,
                    ParentId = parentId,
                    ModifiedAt = AsString(Get(d, "UpdateAt")) ?? AsString(Get(d, "CreateAt")) ?? "",
                    HashType = "md5",
                    HashValue = AsString(Get(d, "Etag")) ?? AsString(Get(d, "MD5")) ?? "",
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

            // 分享模式：123 必须先转存到自己账号才能取链。
            // 这里明确告知调用方"需要二次取链"，而不是假装能直接下。
            if (session.Mode == BrowseMode.Share)
            {
                throw new CloudNewPreparationException(
                    "123 网盘的分享文件需要先转存到自己账号才能下载。",
                    BuildPreparationSpec(session, file));
            }

            // 个人盘：直接取直链
            var url = await GetPersonalDownloadUrlAsync(file.Id, credential, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(url))
                throw new CloudApiException("123 网盘：获取直链失败");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = Ua;
            return spec;
        }

        private async Task<string> GetPersonalDownloadUrlAsync(string fileId,
            CloudCredential credential, CancellationToken ct)
        {
            var query = new Dictionary<string, string>
            {
                { "driveId", "0" },
                { "fileId", fileId ?? "" },
                { "fileName", "" },
                { "type", "1" },
            };

            var res = await GetJsonAsync(DownloadInfoApi, credential, query, ct).ConfigureAwait(false);
            var data = AsDict(Get(res, "data"));
            if (data == null) return null;

            // 123 返回的直链字段名有多个版本，按优先级取
            return AsString(Get(data, "DownloadUrl"))
                ?? AsString(Get(data, "DownloadURL"))
                ?? AsString(Get(data, "url"))
                ?? AsString(Get(res, "DownloadUrl"));
        }

        /// <summary>构造"待二次取链"的规格（转存后由上层补 URL）。</summary>
        private static DownloadSpec BuildPreparationSpec(BrowseSession session, CloudFile file)
        {
            var spec = new DownloadSpec
            {
                Url = "",
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Source["fid"] = file.Id;
            spec.Source["shareKey"] = session.Meta("shareKey");
            spec.Source["passcode"] = session.Meta("passcode");
            spec.Source["platform"] = CloudPlatform.Pan123.Key;
            spec.Source["accountId"] = session.AccountId;
            return spec;
        }

        // ================= HTTP =================

        /// <summary>列表限流：保证两次列表请求之间有间隔，避免 429。</summary>
        private static async Task ThrottleListAsync(CancellationToken ct)
        {
            int wait;
            lock (ListGate)
            {
                var now = DateTime.UtcNow;
                wait = (int)Math.Max(0, (_nextListAt - now).TotalMilliseconds);
                _nextListAt = now.AddMilliseconds(Math.Max(wait, _listIntervalMs));
            }
            if (wait > 0)
                await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        private async Task<object> GetJsonAsync(string url, CloudCredential credential,
            Dictionary<string, string> query, CancellationToken ct, bool useUa = true)
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
                if (useUa)
                    req.Headers.TryAddWithoutValidation("User-Agent", Ua);

                // 个人盘接口要带登录 Cookie
                string cookie = credential?.Primary;
                if (!string.IsNullOrEmpty(cookie))
                {
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);
                    req.Headers.TryAddWithoutValidation("Referer", "https://www.123pan.com/");
                    req.Headers.TryAddWithoutValidation("Origin", "https://www.123pan.com");
                }

                using (var resp = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if ((int)resp.StatusCode == 429)
                    {
                        // 撞限流 → 拉长间隔后让对方重试
                        lock (ListGate) _listIntervalMs = Math.Min(_listIntervalMs * 2, 3000);
                        throw new CloudApiException("123 网盘请求过于频繁，请稍后再试", "429");
                    }

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    try
                    {
                        return JsonCompat.Deserialize(body);
                    }
                    catch
                    {
                        throw new CloudApiException("123 网盘：响应不是有效 JSON（HTTP "
                            + (int)resp.StatusCode + "）");
                    }
                }
            }
        }

        // ================= 取值辅助（与 QuarkClient 同一套约定） =================

        private static object Get(object obj, string key)
        {
            var d = obj as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out var v) ? v : null;
        }

        private static Dictionary<string, object> AsDict(object o)
            => o as Dictionary<string, object>;

        private static object[] AsArray(object o)
            => o as object[];

        private static string AsString(object o) => o as string;

        /// <summary>写操作只对个人网盘有意义。</summary>
        private static void EnsurePersonal(BrowseSession s, string action)
        {
            if (s == null || s.Mode != BrowseMode.Personal)
                throw new CloudNotSupportedException(
                    action + "：请在「我的网盘」里操作（分享浏览模式不可修改文件）");
        }

        private static string AsStringOrNumber(object o)
        {
            if (o == null) return null;
            if (o is string s) return s;
            if (o is int || o is long || o is double || o is decimal) return o.ToString();
            return null;
        }

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
        // 123 走标准 5 步模板：建临时目录 → 登记清理 → 转存 → 轮询可见 → 取链
        // 参数取自 wenxi：8 次 × 1 秒，可见性 = 名字 + 大小精确匹配且唯一。

        private const int TransferAttempts = 8;
        private const int TransferIntervalMs = 1000;

        public async Task<string> CreateTempFolderAsync(CloudCredential credential,
            CancellationToken ct)
        {
            string name = CloudTransferFlow.NewTempFolderName();

            var body = new Dictionary<string, object>
            {
                { "driveId", 0 },
                { "etag", "" },
                { "fileName", name },
                { "parentFileId", 0 },
                { "size", 0 },
                { "type", 1 },
                { "duplicate", 0 },
            };

            var res = await PostAsync(FileCreateApi + "/create", body, credential, ct)
                .ConfigureAwait(false);

            string id = CloudJson.StrPath(res, "data.Info.FileId");
            if (string.IsNullOrEmpty(id)) id = CloudJson.StrPath(res, "data.fileId");

            if (string.IsNullOrEmpty(id))
                throw new CloudApiException("123：创建临时目录失败（"
                    + (CloudJson.Str(res, "message") ?? "未知原因") + "）");

            return id;
        }

        public async Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct)
        {
            spec.Source.TryGetValue("shareKey", out var shareKey);
            spec.Source.TryGetValue("passcode", out var passcode);
            spec.Source.TryGetValue("fid", out var fid);

            var body = new Dictionary<string, object>
            {
                { "shareKey", shareKey ?? "" },
                { "sharePwd", passcode ?? "" },
                { "fileId", fid ?? "" },
                { "parentFileId", long.TryParse(targetFolderId, out var pid) ? pid : 0 },
                { "type", 1 },
            };

            var res = await PostAsync(CopySaveApi, body, credential, ct).ConfigureAwait(false);

            string taskId = CloudJson.StrPath(res, "data.taskId");
            if (string.IsNullOrEmpty(taskId)) taskId = CloudJson.StrPath(res, "data.taskID");

            if (string.IsNullOrEmpty(taskId))
                throw new CloudApiException("123：转存请求失败（"
                    + (CloudJson.Str(res, "message") ?? "未知原因") + "）");

            return taskId;
        }

        public async Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct)
        {
            // ① 先等转存任务本身完成（123 是异步任务）
            if (saveResult is string taskId && !string.IsNullOrEmpty(taskId))
            {
                for (int i = 0; i < TransferAttempts; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var q = await GetJsonAsync(CopySaveGetApi, credential,
                            new Dictionary<string, string> { { "taskId", taskId } }, ct)
                            .ConfigureAwait(false);

                        long status = CloudJson.Num(CloudJson.Get(q, "data"), "status");
                        if (status == 2) break;
                        if (status < 0)
                            throw new CloudApiException("123：转存任务失败");
                    }
                    catch (CloudApiException) { throw; }
                    catch { }

                    onPhase?.Invoke(string.Format("等待转存任务（{0}/{1}）", i + 1, TransferAttempts));
                    await Task.Delay(TransferIntervalMs, ct).ConfigureAwait(false);
                }
            }

            // ② 再轮询目录，直到目标文件可见且唯一
            string wantName = spec.FileName;
            long wantSize = spec.ExpectedSize;

            return await TransferWaiter.WaitAsync(TransferAttempts, TransferIntervalMs,
                async token =>
                {
                    var items = await ListPersonalAsync(folderId, credential, token)
                        .ConfigureAwait(false);
                    return TransferWaiter.MatchUnique(items, wantName, wantSize);
                }, onPhase, ct).ConfigureAwait(false);
        }

        public async Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct)
        {
            string url = await GetPersonalDownloadUrlAsync(savedFile.Id, credential, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(url)) return null;

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

            // 删除走回收站（可恢复），不做硬删除 —— 万一把用户的东西误判了还能救回来
            var body = new Dictionary<string, object>
            {
                { "driveId", 0 },
                {
                    "fileTrashInfoList",
                    new[] { new Dictionary<string, object> { { "FileId", folderId } } }
                },
            };

            try
            {
                await PostAsync(TrashApi, body, credential, ct).ConfigureAwait(false);
            }
            catch
            {
                // 清理失败不影响下载；下次启动会重试
            }
        }

        private async Task<object> PostAsync(string url, object body, CloudCredential credential,
            CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                foreach (var kv in ApiHeaders(credential))
                {
                    try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { }
                }

                req.Content = new StringContent(JsonCompat.Serialize(body),
                    System.Text.Encoding.UTF8, "application/json");

                using (var resp = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new CloudHttpException("HTTP " + (int)resp.StatusCode,
                            (int)resp.StatusCode, text);
                    return JsonCompat.Deserialize(text);
                }
            }
        }

        /// <summary>123 的鉴权头（authorToken）。</summary>
        private static Dictionary<string, string> ApiHeaders(CloudCredential credential)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", Ua },
                { "Referer", "https://www.123pan.com/" },
                { "Origin", "https://www.123pan.com" },
            };
            string token = credential?.Primary;
            if (!string.IsNullOrEmpty(token))
            {
                h["Authorization"] = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? token : "Bearer " + token;
            }
            return h;
        }

        // ================= 上传 =================
        //
        // 123 的上传分两条路（照抄 pan123.dart:940-1000）：
        //   ① **秒传**：upload_request 返回 Reuse=true，直接完成，不传字节
        //   ② S3 直传：拿到 AccessKeyId/SecretAccessKey/EndPoint 后 PUT 到 S3
        //      小文件单次 PUT；大文件分片（16MB/片）+ presigned URL
        //
        // 没有 S3 凭据时退到"预签名 URL"模式（s3_upload_object/auth）。

        private const long Pan123ChunkSize = 16L * 1024 * 1024;

        public override async Task<CloudFile> UploadAsync(BrowseSession session,
            string parentId, string localPath, CloudCredential credential,
            IProgress<UploadProgress> progress = null, CancellationToken ct = default)
        {
            EnsurePersonal(session, "上传");
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

            Report("计算校验值", 0, size);
            string md5 = await CloudCrypto.Md5FileAsync(localPath, ct).ConfigureAwait(false);

            // ---- ① 申请上传（可能命中秒传）----
            Report("准备上传", 0, size);

            var body = new Dictionary<string, object>
            {
                { "driveId", 0 },
                { "duplicate", 1 },
                { "etag", md5 },
                { "fileName", fileName },
                { "parentFileId", long.TryParse(parentId, out var pid) ? pid : 0 },
                { "size", size },
                { "type", 0 },
            };

            var pre = await PostDataAsync(
                "https://yun.123pan.cn/b/api/file/upload_request", body, credential, ct)
                .ConfigureAwait(false);

            var preInfo = CloudJson.Dict(CloudJson.Get(pre, "data"));
            if (preInfo == null)
                throw new CloudApiException("123：上传申请失败（"
                    + (CloudJson.Str(pre, "message") ?? "未知原因") + "）");

            string fileId = CloudJson.Str(preInfo, "FileId");
            if (string.IsNullOrEmpty(fileId))
                fileId = CloudJson.StrPath(preInfo, "Info.FileId");

            if (string.IsNullOrEmpty(fileId))
                throw new CloudApiException("123：未返回上传文件标识");

            // ---- 秒传命中 ----
            if (CloudJson.Bool(preInfo, "Reuse"))
            {
                Report("秒传完成", size, size);
                return new CloudFile
                {
                    Id = fileId, Name = fileName, Size = size,
                    IsDirectory = false, ParentId = parentId,
                };
            }

            // ---- ② 上传到 S3 ----
            string bucket = CloudJson.Str(preInfo, "Bucket");
            string key = CloudJson.Str(preInfo, "Key");
            string endPoint = CloudJson.Str(preInfo, "EndPoint");

            if (string.IsNullOrEmpty(key))
                throw new CloudApiException("123：未创建上传任务");

            long uploaded = 0;
            var started = DateTime.UtcNow;

            int chunkCount = (int)Math.Max(1, (size + Pan123ChunkSize - 1) / Pan123ChunkSize);

            for (int i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                long start = i * Pan123ChunkSize;
                long end = Math.Min(size, (i + 1) * Pan123ChunkSize);

                // 拿预签名 URL
                var authBody = new Dictionary<string, object>
                {
                    { "StorageNode", CloudJson.Get(preInfo, "StorageNode") ?? "" },
                    { "bucket", bucket },
                    { "key", key },
                    { "uploadId", CloudJson.Get(preInfo, "UploadId") ?? "" },
                    { "partNumberStart", i + 1 },
                    { "partNumberEnd", i + 2 },
                };

                string authPath = chunkCount == 1
                    ? "/b/api/file/s3_upload_object/auth"
                    : "/b/api/file/s3_repare_upload_parts_batch";

                var authRes = await PostDataAsync("https://yun.123pan.cn" + authPath,
                    authBody, credential, ct).ConfigureAwait(false);

                string presigned = CloudJson.StrPath(authRes,
                    "data.presignedUrls." + (i + 1));

                if (string.IsNullOrEmpty(presigned))
                    presigned = CloudJson.StrPath(authRes, "data.presignedUrls.1");
                if (string.IsNullOrEmpty(presigned))
                    throw new CloudApiException(string.Format(
                        "123：未拿到第 {0} 片的上传地址", i + 1));

                // 读分片
                int length = (int)(end - start);
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

                using (var req = new HttpRequestMessage(HttpMethod.Put, presigned))
                {
                    req.Content = new ByteArrayContent(buffer);

                    using (var resp = await _session.Http.SendAsync(req, ct)
                        .ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            throw new CloudApiException(string.Format(
                                "123：第 {0} 片上传失败 HTTP {1}",
                                i + 1, (int)resp.StatusCode));
                    }
                }

                uploaded += length;
                double elapsed = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                Report("上传中", uploaded, size, uploaded / elapsed);
            }

            // ---- ③ 提交 ----
            Report("提交", size, size);

            var completeBody = new Dictionary<string, object>
            {
                { "StorageNode", CloudJson.Get(preInfo, "StorageNode") ?? "" },
                { "bucket", bucket },
                { "key", key },
                { "uploadId", CloudJson.Get(preInfo, "UploadId") ?? "" },
                { "fileId", fileId },
                { "fileSize", size },
                { "isMultipart", chunkCount > 1 },
            };

            try
            {
                await PostDataAsync("https://yun.123pan.cn/b/api/file/upload_complete/v2",
                    completeBody, credential, ct).ConfigureAwait(false);
            }
            catch
            {
                // 老接口兜底
                try
                {
                    await PostDataAsync("https://yun.123pan.cn/b/api/file/upload_complete",
                        new Dictionary<string, object> { { "fileId", fileId } },
                        credential, ct).ConfigureAwait(false);
                }
                catch { }
            }

            Report("完成", size, size);

            return new CloudFile
            {
                Id = fileId, Name = fileName, Size = size,
                IsDirectory = false, ParentId = parentId,
            };
        }

        /// <summary>POST 并返回 data 字段（123 的接口统一把结果包在 data 里）。</summary>
        private Task<object> PostDataAsync(string url, object body,
            CloudCredential credential, CancellationToken ct)
            => PostAsync(url, body, credential, ct);
    }

}
