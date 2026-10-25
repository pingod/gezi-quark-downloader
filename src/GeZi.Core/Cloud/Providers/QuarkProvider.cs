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
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Support;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// 夸克网盘 provider —— 把已有的 <see cref="QuarkClient"/> 能力
    /// 适配到统一的 <see cref="ICloudProvider"/> 接口上。
    ///
    /// 为什么要适配而不是重写：
    /// <see cref="QuarkClient"/> 是经过实战打磨的（多入口取链回退、
    /// 容量不足识别、风控退避、401/412 重试…），推翻重写只会引入回归。
    /// 这一层只做**语义转换**：把 QuarkClient 的调用形状翻译成接口要求的形状。
    ///
    /// 关键映射：
    ///   OpenShareAsync  → GetStokenAsync + ShareDetailPageAsync
    ///   ListAsync       → ShareDetailPageAsync / ListDirPageAsync
    ///   GetDownloadSpec → GetShareDownloadAsync / GetDownloadAsync
    /// </summary>
    public class QuarkProvider : CloudProviderBase, ICloudTransferSupport
    {
        /// <summary>本实例对应的平台（子类覆盖以复用全部逻辑）。</summary>
        public override CloudPlatform Platform => CloudPlatform.Quark;

        /// <summary>API 基址（UC 覆盖）。</summary>
        protected virtual string ApiBase => QuarkConstants.ApiBase;

        /// <summary>pr 参数（UC 覆盖）。</summary>
        protected virtual string Product => "ucpro";

        /// <summary>夸克的分享支持匿名解析与取链。</summary>
        public bool SupportsGuestShare => true;

        // 夸克是唯一实现了完整文件管理的平台（重命名/移动/删除/建目录/建分享）
        public override bool SupportsUpload => true;
        public override bool SupportsRename => true;
        public override bool SupportsMove => true;
        public override bool SupportsDelete => true;
        public override bool SupportsCreateFolder => true;
        public override bool SupportsCreateShare => true;

        // ================= 客户端缓存 =================

        // QuarkClient 内部有 HttpClient 与 Cookie 容器，重建代价高且会丢 Set-Cookie。
        // 按 "cookie 串" 缓存：同一个账号复用同一个实例，换账号自然换实例。
        private readonly Dictionary<string, QuarkClient> _clients =
            new Dictionary<string, QuarkClient>(StringComparer.Ordinal);

        private readonly object _clientLock = new object();

        private QuarkClient GetClient(CloudCredential credential)
        {
            string cookie = credential?.Primary ?? "";

            lock (_clientLock)
            {
                if (_clients.TryGetValue(cookie, out var existing))
                    return existing;

                var c = new QuarkClient(cookie, ApiBase, Product);
                _clients[cookie] = c;
                return c;
            }
        }

        /// <summary>释放所有缓存的客户端。</summary>
        public void Dispose()
        {
            lock (_clientLock)
            {
                foreach (var c in _clients.Values)
                {
                    try { c.Dispose(); } catch { }
                }
                _clients.Clear();
            }
        }

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var client = GetClient(credential);

            var account = new CloudAccount();

            var (ok, nick) = await client.ValidateAsync().ConfigureAwait(false);
            if (!ok)
                throw new CloudApiException("登录态已失效，请重新登录", "401");

            account.Nickname = nick ?? "";

            try
            {
                var (total, used) = await client.GetCapacityAsync().ConfigureAwait(false);
                account.Total = total;
                account.Used = used;
            }
            catch
            {
                // 容量查不到不影响登录成功
            }

            // 账号稳定标识（用于多账号去重）
            if (!string.IsNullOrEmpty(client.AccountSig))
                account.Extra["accountSig"] = client.AccountSig;

            return account;
        }

        // ================= 打开分享 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            if (link == null || string.IsNullOrEmpty(link.ShareId))
                throw new CloudApiException("分享链接无效");

            var client = GetClient(credential);

            string stoken = await client.GetStokenAsync(link.ShareId, link.Passcode ?? "")
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(stoken))
                throw new CloudApiException(
                    "获取分享凭证失败。分享可能已失效，或提取码不正确。");

            var session = new BrowseSession
            {
                Platform = Platform,
                Mode = BrowseMode.Share,
                Title = Platform.ShortName + "分享 " + link.ShareId,
                RootId = "0",
                SourceLink = link,
            };

            session.SetMeta("pwdId", link.ShareId);
            session.SetMeta("stoken", stoken);
            session.SetMeta("passcode", link.Passcode ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");

            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var client = GetClient(credential);

            var (ok, _) = await client.ValidateAsync().ConfigureAwait(false);
            if (!ok)
                throw new CloudApiException("登录态已失效，请重新登录", "401");

            var session = new BrowseSession
            {
                Platform = Platform,
                Mode = BrowseMode.Personal,
                Title = "我的" + Platform.ShortName + "网盘",
                RootId = "0",
            };
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            if (session == null) return Array.Empty<CloudFile>();

            string dir = string.IsNullOrEmpty(parentId) ? "0" : parentId;
            var client = GetClient(credential ?? CredentialFromSession(session));
            var result = new List<CloudFile>();

            if (session.Mode == BrowseMode.Share)
            {
                string pwdId = session.Meta("pwdId");
                string stoken = session.Meta("stoken");

                // 分页拉全（夸克单页 50 条）。上限 200 页与核心保持一致。
                for (int page = 1; page <= 200; page++)
                {
                    ct.ThrowIfCancellationRequested();

                    var p = await client.ShareDetailPageAsync(pwdId, stoken, dir, page)
                        .ConfigureAwait(false);
                    if (p?.Items == null) break;

                    foreach (var it in p.Items)
                        result.Add(ToCloudFile(it, dir));

                    if (!p.HasMore) break;
                }
            }
            else
            {
                for (int page = 1; page <= 200; page++)
                {
                    ct.ThrowIfCancellationRequested();

                    var p = await client.ListDirPageAsync(dir, page).ConfigureAwait(false);
                    if (p?.Items == null) break;

                    foreach (var it in p.Items)
                        result.Add(ToCloudFile(it, dir));

                    if (!p.HasMore) break;
                }
            }

            return result;
        }

        /// <summary>夸克条目 → 统一模型。</summary>
        private static CloudFile ToCloudFile(Core.Models.QuarkFileEntry it, string parentId)
        {
            return new CloudFile
            {
                Id = it.Fid ?? "",
                Name = it.Name ?? "",
                Size = it.Size,
                IsDirectory = it.IsDir,
                ParentId = parentId,
                Token = it.Token ?? "",

                // 夸克条目本身不带修改时间/哈希，留空（界面会自动隐藏空字段）
                ModifiedAt = "",
            };
        }

        /// <summary>从会话元数据里还原一个"游客/空凭据"。</summary>
        private CloudCredential CredentialFromSession(BrowseSession session)
            => new CloudCredential(Platform, session?.AccountId ?? "",
                new Dictionary<string, string> { { CloudCredential.PrimaryKey, "" } });

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (file.IsDirectory)
                throw new CloudApiException("目录不能直接下载，请先展开选择里面的文件");

            var client = GetClient(credential ?? CredentialFromSession(session));

            string url;

            if (session.Mode == BrowseMode.Share)
            {
                url = await GetShareUrlAsync(client, session, file).ConfigureAwait(false);
            }
            else
            {
                var links = await client.GetDownloadAsync(new List<string> { file.Id })
                    .ConfigureAwait(false);
                url = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
            }

            if (string.IsNullOrEmpty(url))
            {
                // 区分三种失败，给出的提示和后续动作完全不同：
                //   · 需登录/受限（31001/23018）→ 文件还能下，但要走转存
                //   · 凭证过期（14001/41020）  → 重新解析分享即可，无需转存
                //   · 其它                     → 才是真失败
                string reason = client.ShareDirectUnavailableReason;
                if (!string.IsNullOrEmpty(reason))
                {
                    // 这是「此路不通但另有出路」，不是错误。
                    // 抛专用异常让上层改走转存，而不是当成失败丢给用户。
                    var prep = new DownloadSpec
                    {
                        Url = "",
                        FileName = file.Name,
                        ExpectedSize = file.Size,
                    };
                    prep.Source["fid"] = file.Id;
                    prep.Source["shareId"] = session.Meta("pwdId");
                    prep.Source["stoken"] = session.Meta("stoken");
                    prep.Source["platform"] = Platform.Key;
                    prep.Source["accountId"] = session.AccountId;
                    throw new CloudNewPreparationException(
                        "该分享的直链需要登录或已被限制，改走「转存后下载」：", prep);
                }

                throw new CloudApiException(
                    "获取下载直链失败。分享可能已过期，或需要登录；请重新解析后再试。");
            }

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };

            // 夸克直链对 UA 敏感：取链与下载必须同族 UA，否则容易被判盗链降速/412。
            // 下载器默认就会带 DlUa，这里不用重复塞。
            return spec;
        }

        /// <summary>
        /// 取分享直链，带**凭证自动重解析**。
        ///
        /// stoken / share_fid_token 都有时效，用户解析完隔几分钟再下载就会失败。
        /// 失败时重新换取 stoken + 按 fid 找回新的 token，再试一次。
        /// 这条自愈路径是 WPF 版的核心体验，必须保留。
        /// </summary>
        private async Task<string> GetShareUrlAsync(QuarkClient client,
            BrowseSession session, CloudFile file)
        {
            string pwdId = session.Meta("pwdId");
            string stoken = session.Meta("stoken");

            // ① 现有凭证
            string url = await TryShareUrlAsync(client, file.Id, file.Token, pwdId, stoken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(url))
                return url;

            // ② 重新解析
            string freshStoken = await client.GetStokenAsync(pwdId, session.Meta("passcode"))
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(freshStoken))
                return null;

            string freshToken = await FindShareTokenAsync(client, pwdId, freshStoken, file.Id)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(freshToken))
                return null;

            // ③ 用新凭证再取，并把新值写回会话（后续换链直接用新值）
            url = await TryShareUrlAsync(client, file.Id, freshToken, pwdId, freshStoken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(url))
            {
                session.SetMeta("stoken", freshStoken);
                file.Token = freshToken;
            }

            return url;
        }

        /// <summary>
        /// 取一次分享直链。
        /// 会**向上抛** <see cref="QuarkShareCredentialExpiredException"/>，
        /// 不能在这里吞掉 —— 上层要据此决定"刷新凭证重试"，
        /// 如果这里 catch 成 null，刷新逻辑就永远不会被触发。
        /// </summary>
        private static async Task<string> TryShareUrlAsync(QuarkClient client,
            string fid, string token, string pwdId, string stoken)
        {
            var links = await client.GetShareDownloadAsync(
                new List<string> { fid },
                new List<string> { token ?? "" },
                pwdId, stoken).ConfigureAwait(false);

            return links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
        }

        /// <summary>在分享根目录里按 fid 找回新的 share_fid_token。</summary>
        private static async Task<string> FindShareTokenAsync(QuarkClient client,
            string pwdId, string stoken, string fid)
        {
            try
            {
                for (int page = 1; page <= 40; page++)
                {
                    var p = await client.ShareDetailPageAsync(pwdId, stoken, "0", page)
                        .ConfigureAwait(false);
                    if (p?.Items == null) return null;

                    var hit = p.Items.FirstOrDefault(e => !e.IsDir && e.Fid == fid);
                    if (hit != null) return hit.Token;

                    if (!p.HasMore) break;
                }
            }
            catch { }
            return null;
        }

        // ================= 文件管理 =================

        public override async Task<CloudFile> CreateFolderAsync(BrowseSession session, string parentId,
            string name, CloudCredential credential, CancellationToken ct = default)
        {
            EnsurePersonal(session, "新建文件夹");
            var client = GetClient(credential);

            string dir = string.IsNullOrEmpty(parentId) ? "0" : parentId;
            string path = await ResolvePathAsync(client, dir).ConfigureAwait(false);
            string full = (path.TrimEnd('/')) + "/" + name;

            await client.MkdirAsync(full).ConfigureAwait(false);

            return new CloudFile
            {
                Name = name,
                IsDirectory = true,
                ParentId = dir,
            };
        }

        public override async Task RenameAsync(BrowseSession session, CloudFile file, string newName,
            CloudCredential credential, CancellationToken ct = default)
        {
            EnsurePersonal(session, "重命名");
            var client = GetClient(credential);

            // 夸克的重命名接口按 fid 提交
            await client.RenameAsync(file.Id, newName).ConfigureAwait(false);
            file.Name = newName;
        }

        public override async Task DeleteAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            CloudCredential credential, CancellationToken ct = default)
        {
            EnsurePersonal(session, "删除");
            if (files == null || files.Count == 0) return;

            var client = GetClient(credential);
            var ids = files.Select(f => f.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

            await client.DeleteAsync(ids).ConfigureAwait(false);
        }

        public override async Task MoveAsync(BrowseSession session, IReadOnlyList<CloudFile> files,
            string targetId, CloudCredential credential, CancellationToken ct = default)
        {
            EnsurePersonal(session, "移动");
            if (files == null || files.Count == 0) return;

            var client = GetClient(credential);
            var ids = files.Select(f => f.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

            await client.MoveAsync(ids, string.IsNullOrEmpty(targetId) ? "0" : targetId)
                .ConfigureAwait(false);
        }

        public override async Task<ShareCreation> CreateShareAsync(BrowseSession session,
            IReadOnlyList<CloudFile> files, ShareOptions options,
            CloudCredential credential, CancellationToken ct = default)
        {
            EnsurePersonal(session, "创建分享");
            if (files == null || files.Count == 0)
                throw new CloudApiException("请先选择要分享的文件");

            var client = GetClient(credential);
            var ids = files.Select(f => f.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

            var (shareId, passcode) = await client.CreateShareAsync(ids, options?.Passcode)
                .ConfigureAwait(false);

            return new ShareCreation
            {
                ShareId = shareId,
                Passcode = passcode ?? "",
                Url = string.IsNullOrEmpty(shareId) ? "" : "https://pan.quark.cn/s/" + shareId,
            };
        }

        // ================= 上传 =================

        /// <summary>
        /// 上传本地文件到网盘。
        ///
        /// 交给 <see cref="QuarkUploader"/> 执行完整流程（预上传 → 秒传检测
        /// → OSS 分片 → 提交）。
        /// </summary>
        public override async Task<CloudFile> UploadAsync(BrowseSession session,
            string parentId, string localPath, CloudCredential credential,
            IProgress<UploadProgress> progress = null, CancellationToken ct = default)
        {
            EnsurePersonal(session, "上传");

            var client = GetClient(credential);
            string dir = string.IsNullOrEmpty(parentId) ? "0" : parentId;

            var result = await QuarkUploader.UploadAsync(client, dir, localPath, progress, ct)
                .ConfigureAwait(false);

            return new CloudFile
            {
                Id = result.Fid,
                Name = result.Name,
                Size = result.Size,
                IsDirectory = false,
                ParentId = dir,
            };
        }

        /// <summary>新建文件夹/重命名等操作只对个人网盘有意义。</summary>
        private static void EnsurePersonal(BrowseSession session, string action)
        {
            if (session == null || session.Mode != BrowseMode.Personal)
                throw new CloudNotSupportedException(action + "：分享浏览模式下不可用（请先转存到自己网盘）");
        }

        /// <summary>把 fid 解析成完整路径（新建文件夹接口要的是路径而非 fid）。</summary>
        private async Task<string> ResolvePathAsync(QuarkClient client, string fid)
        {
            try
            {
                return await client.PathOfAsync(fid).ConfigureAwait(false);
            }
            catch
            {
                return "/";
            }
        }

        // ================= ICloudTransferSupport（转存） =================
        //
        // 夸克的转存是 31001/23018（需登录/受限）时的**唯一可行路径**，
        // 所以必须实现 —— 否则那些"直链拿不到"的分享就彻底下不了。
        //
        // 流程（对照 quark_uc.dart:292-320）：
        //   ① 建两级临时目录：我的网盘/文析助手临时转存/tr_<epoch>_<8hex>
        //      父目录要**复用**（用信号量串行化，避免并发建出多个同名父目录）
        //   ② 登记 cleanup（由 CloudTransferFlow 负责）
        //   ③ sharepage/save 转存
        //   ④ 等 task 完成（save_as_top_fids 给出新 fid）
        //   ⑤ 用新 fid 走个人盘取链

        private const string TempParentName = "文析助手临时转存";

        /// <summary>父目录复用需要串行化（否则并发下载会建出多个同名父目录）。</summary>
        private static readonly SemaphoreSlim TempRootGate = new SemaphoreSlim(1, 1);

        public async Task<string> CreateTempFolderAsync(CloudCredential credential,
            CancellationToken ct)
        {
            var client = GetClient(credential);

            // ① 找/建父目录（串行）
            await TempRootGate.WaitAsync(ct).ConfigureAwait(false);
            string parentId;
            try
            {
                var root = await client.ListDirPageAsync("0", 1).ConfigureAwait(false);
                var existing = root?.Items?.FirstOrDefault(e =>
                    e.IsDir && e.Name == TempParentName);

                parentId = existing?.Fid
                    ?? await client.MkdirAsync("/" + TempParentName).ConfigureAwait(false);
            }
            finally
            {
                TempRootGate.Release();
            }

            // ② 在父目录下建本次的临时目录
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            string name = "tr_" + ms + "_" + suffix;

            string path = "/" + TempParentName + "/" + name;
            string folderId = await client.MkdirAsync(path).ConfigureAwait(false);

            if (string.IsNullOrEmpty(folderId))
                throw new CloudApiException("夸克：创建临时目录失败");

            // 返回 "id|name" —— 名字要留给清理队列做安全校验
            return folderId + "|" + name;
        }

        public async Task<object> SaveToFolderAsync(DownloadSpec spec, string targetFolderId,
            CloudCredential credential, CancellationToken ct)
        {
            var client = GetClient(credential);

            spec.Source.TryGetValue("fid", out var fid);
            spec.Source.TryGetValue("shareId", out var shareId);
            spec.Source.TryGetValue("stoken", out var stoken);

            if (string.IsNullOrEmpty(fid) || string.IsNullOrEmpty(shareId))
                throw new CloudApiException("夸克：缺少分享上下文，请重新解析");

            // stoken 可能已过期 → 重新换一次
            if (string.IsNullOrEmpty(stoken))
                stoken = await client.GetStokenAsync(shareId, "").ConfigureAwait(false);

            string dirId = targetFolderId?.Split('|')[0] ?? "0";

            var res = await client.RequestRawAsync(
                System.Net.Http.HttpMethod.Post,
                "/1/clouddrive/share/sharepage/save",
                new Dictionary<string, string>
                {
                    { "pr", "ucpro" }, { "fr", "pc" }, { "uc_param_str", "" },
                },
                new
                {
                    fid_list = new[] { fid },
                    fid_token_list = new[] { spec.Source.TryGetValue("token", out var t) ? t : "" },
                    to_pdir_fid = dirId,
                    pwd_id = shareId,
                    stoken,
                    pdir_fid = "0",
                    scene = "link",
                }).ConfigureAwait(false);

            string taskId = CloudJson.StrPath(res, "data.task_id");
            if (string.IsNullOrEmpty(taskId))
                throw new CloudApiException("夸克：转存请求失败（"
                    + (CloudJson.Str(res, "message") ?? "未知原因") + "）");

            return taskId;
        }

        public async Task<CloudFile> WaitVisibleAsync(object saveResult, string folderId,
            DownloadSpec spec, CloudCredential credential,
            Action<string> onPhase, CancellationToken ct)
        {
            string taskId = saveResult as string;
            if (string.IsNullOrEmpty(taskId)) return null;

            var client = GetClient(credential);
            string dirId = folderId?.Split('|')[0] ?? "0";

            // 夸克的转存是异步任务，要轮询 task 接口
            for (int i = 0; i < 20; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var res = await client.RequestRawAsync(
                        System.Net.Http.HttpMethod.Get,
                        "/1/clouddrive/task",
                        new Dictionary<string, string> { { "task_id", taskId }, { "retry_index", i.ToString() } })
                        .ConfigureAwait(false);

                    var data = CloudJson.Dict(CloudJson.Get(res, "data"));
                    long status = CloudJson.Num(data, "status");
                    string finishedAt = CloudJson.Str(data, "finished_at");

                    if (status == 2 || !string.IsNullOrEmpty(finishedAt))
                    {
                        // 拿 save_as.save_as_top_fids
                        var saveAs = CloudJson.Dict(CloudJson.Get(data, "save_as"));
                        var fids = CloudJson.Arr(CloudJson.Get(saveAs, "save_as_top_fids"));

                        if (fids != null && fids.Length > 0)
                        {
                            string newFid = fids[0] as string
                                ?? CloudJson.Str(CloudJson.Dict(fids[0]), "fid");

                            if (!string.IsNullOrEmpty(newFid))
                            {
                                return new CloudFile
                                {
                                    Id = newFid,
                                    Name = spec.FileName,
                                    Size = spec.ExpectedSize,
                                    ParentId = dirId,
                                };
                            }
                        }
                        break;
                    }

                    if (status < 0)
                        throw new CloudApiException("夸克：转存任务失败");
                }
                catch (CloudApiException) { throw; }
                catch { }

                onPhase?.Invoke(string.Format("等待转存完成（{0}/20）", i + 1));
                await Task.Delay(700, ct).ConfigureAwait(false);
            }

            // 兜底：直接查目录里是否出现了目标文件
            return await TransferWaiter.WaitAsync(8, 750, async token =>
            {
                var page = await client.ListDirPageAsync(dirId, 1).ConfigureAwait(false);
                return TransferWaiter.MatchUnique(
                    page?.Items?.Select(e => new CloudFile
                    {
                        Id = e.Fid,
                        Name = e.Name,
                        Size = e.Size,
                        IsDirectory = e.IsDir,
                    }).ToList(),
                    spec.FileName, spec.ExpectedSize);
            }, onPhase, ct).ConfigureAwait(false);
        }

        public async Task<DownloadSpec> ResolveSavedAsync(CloudFile savedFile, DownloadSpec originalSpec,
            CloudCredential credential, CancellationToken ct)
        {
            var client = GetClient(credential);

            var links = await client.GetDownloadAsync(new List<string> { savedFile.Id })
                .ConfigureAwait(false);

            string url = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
            if (string.IsNullOrEmpty(url)) return null;

            return new DownloadSpec
            {
                Url = url,
                FileName = savedFile.Name,
                ExpectedSize = savedFile.Size > 0 ? savedFile.Size : originalSpec.ExpectedSize,
            };
        }

        public async Task DeleteTempFolderAsync(string folderId, CloudCredential credential,
            CancellationToken ct)
        {
            // folderId 可能是 "id|name" 形式
            string id = folderId?.Split('|')[0];
            string name = folderId != null && folderId.Contains("|")
                ? folderId.Split('|')[1] : "";

            if (string.IsNullOrEmpty(id)) return;

            // 安全校验：只删我们自己的临时目录（tr_ 前缀 或 父目录名）
            if (!string.IsNullOrEmpty(name)
                && !name.StartsWith("tr_", StringComparison.Ordinal)
                && name != TempParentName)
                return;

            try
            {
                var client = GetClient(credential);
                await client.DeleteAsync(new List<string> { id }).ConfigureAwait(false);
            }
            catch
            {
                // 清理失败不影响下载
            }
        }
    }
}
