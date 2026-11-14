// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// GitHub provider：把仓库当"网盘"浏览。
    ///
    /// 设计参照 wenxi 的 github.dart，这是**最干净的实现范本**：
    /// - 凭据是 Personal Access Token（可选；公开仓库不填也能浏览）
    /// - 目录 = 仓库路径，列表走 <c>/repos/{o}/{r}/contents/{path}</c>
    /// - 下载 = 直接拼 <c>raw.githubusercontent.com</c>，不需要取链
    /// - 无分享概念（<see cref="CloudPlatform.SupportsSharing"/> 为 false）
    ///
    /// 支持的链接形态：
    ///   https://github.com/{owner}/{repo}                    → 仓库根
    ///   https://github.com/{owner}/{repo}/tree/{branch}/{p} → 子目录
    ///   https://github.com/{owner}/{repo}/blob/{branch}/{p} → 单个文件
    ///
    /// 注意：<see cref="CloudPlatform.FromHost"/> 已把 github.com 归到本平台，
    /// 但分享 ID 解析对 GitHub 无意义（没有 /s/xxx），所以在
    /// <see cref="OpenShareAsync"/> 里改从 URL 直接抽 owner/repo。
    /// </summary>
    // CS1998：GitHub 的"打开会话"只是把 URL 拆成 owner/repo/ref/path，
    // 真正的 HTTP 都在 Browse/Download 阶段才发生。保留 async 的理由同上。
#pragma warning disable CS1998
    public sealed class GithubProvider : CloudProviderBase
    {
        public override CloudPlatform Platform => CloudPlatform.Github;

        private const string ApiBase = "https://api.github.com";
        private const string RawBase = "https://raw.githubusercontent.com";

        private readonly CloudHttp _http = new CloudHttp();

        // GitHub 不支持写操作（本实现范围内），明确声明出来让界面禁用按钮
        public override bool SupportsUpload => false;
        public override bool SupportsRename => false;
        public override bool SupportsMove => false;
        public override bool SupportsDelete => false;
        public override bool SupportsCreateFolder => false;
        public override bool SupportsCreateShare => false;

        /// <summary>默认分支缓存（按 owner/repo）。避免每次列表都多打一个接口。</summary>
        private static readonly Dictionary<string, string> BranchCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        // ================= 账号 =================

        public override async Task<CloudAccount> GetAccountAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            var headers = Headers(credential);

            // 没 token 也能用（匿名限速 60 次/小时）
            if (string.IsNullOrEmpty(credential?.Primary))
                return new CloudAccount { Nickname = "GitHub（匿名）" };

            var res = await _http.GetJsonAsync(ApiBase + "/user", null, headers, ct)
                .ConfigureAwait(false);

            string login = CloudJson.Str(res, "login");
            if (string.IsNullOrEmpty(login))
                throw new CloudApiException("GitHub：Token 无效或已过期");

            return new CloudAccount
            {
                Nickname = login,
                Extra = { { "token", "ok" } },
            };
        }

        // ================= 打开会话 =================

        public override async Task<BrowseSession> OpenShareAsync(ParsedLink link,
            CloudCredential credential, CancellationToken ct = default)
        {
            // GitHub 的"分享"其实是仓库链接，从 URL 解析 owner/repo/path
            var target = ParseRepoUrl(link?.Url ?? link?.Source);
            if (target == null)
                throw new CloudApiException(
                    "GitHub 链接格式不对。支持：github.com/owner/repo[/tree/branch/path]");

            var session = new BrowseSession
            {
                Platform = CloudPlatform.Github,
                Mode = BrowseMode.Share,
                Title = target.Owner + "/" + target.Repo,
                RootId = target.Path ?? "",
                SourceLink = link,
            };
            session.SetMeta("owner", target.Owner);
            session.SetMeta("repo", target.Repo);
            session.SetMeta("ref", target.Ref ?? "");
            session.SetMeta("path", target.Path ?? "");
            session.SetMeta("accountId", credential?.Label ?? "");

            return session;
        }

        public override async Task<BrowseSession> OpenPersonalAsync(CloudCredential credential,
            CancellationToken ct = default)
        {
            // 匿名浏览时没有"自己的网盘"，这里把资料库当成根
            var session = new BrowseSession
            {
                Platform = CloudPlatform.Github,
                Mode = BrowseMode.Personal,
                Title = "我的 GitHub",
                RootId = "",
            };
            session.SetMeta("accountId", credential?.Label ?? "");
            return session;
        }

        // ================= 列表 =================

        public override async Task<IReadOnlyList<CloudFile>> ListAsync(BrowseSession session,
            string parentId, CloudCredential credential, CancellationToken ct = default)
        {
            string owner = session.Meta("owner");
            string repo = session.Meta("repo");

            // 个人模式没有 owner/repo → 列出该 token 可见的仓库
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                return await ListReposAsync(credential, ct).ConfigureAwait(false);

            string path = string.IsNullOrEmpty(parentId) ? session.Meta("path") : parentId;
            string reference = session.Meta("ref");

            if (string.IsNullOrEmpty(reference))
            {
                reference = await GetDefaultBranchAsync(owner, repo, credential, ct).ConfigureAwait(false);
                session.SetMeta("ref", reference);
            }

            var query = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(reference)) query["ref"] = reference;

            string url = ApiBase + "/repos/" + owner + "/" + repo + "/contents/" + Uri.EscapeDataString(path ?? "");
            var res = await _http.GetJsonAsync(url, query, Headers(credential), ct).ConfigureAwait(false);

            var arr = CloudJson.Arr(res);
            if (arr == null)
            {
                // 单个文件时返回的是对象而不是数组
                var single = CloudJson.Dict(res);
                if (single != null && CloudJson.Str(single, "type") == "file")
                    return new[] { ToCloudFile(single, parentId) };
                return Array.Empty<CloudFile>();
            }

            var list = new List<CloudFile>();
            foreach (var it in arr)
            {
                var d = CloudJson.Dict(it);
                if (d != null) list.Add(ToCloudFile(d, path));
            }
            return list;
        }

        private async Task<IReadOnlyList<CloudFile>> ListReposAsync(CloudCredential credential,
            CancellationToken ct)
        {
            var res = await _http.GetJsonAsync(ApiBase + "/user/repos",
                new Dictionary<string, string>
                {
                    { "per_page", "100" },
                    { "sort", "updated" },
                    { "affiliation", "owner,collaborator" },
                }, Headers(credential), ct).ConfigureAwait(false);

            var arr = CloudJson.Arr(res);
            if (arr == null) return Array.Empty<CloudFile>();

            var list = new List<CloudFile>();
            foreach (var it in arr)
            {
                var d = CloudJson.Dict(it);
                if (d == null) continue;

                string fullName = CloudJson.Str(d, "full_name");
                list.Add(new CloudFile
                {
                    Id = fullName,
                    Name = CloudJson.Str(d, "name"),
                    IsDirectory = true,
                    Size = CloudJson.Num(d, "size") * 1024,   // GitHub 给的是 KB
                    ParentId = "",
                    ModifiedAt = CloudJson.Str(d, "updated_at"),
                    Token = string.Join("|",
                        CloudJson.Str(CloudJson.Get(d, "owner"), "login"),
                        CloudJson.Str(d, "name")),
                });
            }
            return list;
        }

        private async Task<string> GetDefaultBranchAsync(string owner, string repo,
            CloudCredential credential, CancellationToken ct)
        {
            string key = owner + "/" + repo;
            lock (CacheLock)
            {
                if (BranchCache.TryGetValue(key, out var cached)) return cached;
            }

            try
            {
                var res = await _http.GetJsonAsync(ApiBase + "/repos/" + owner + "/" + repo,
                    null, Headers(credential), ct).ConfigureAwait(false);

                string branch = CloudJson.Str(res, "default_branch");
                if (!string.IsNullOrEmpty(branch))
                {
                    lock (CacheLock) BranchCache[key] = branch;
                    return branch;
                }
            }
            catch { }
            return "main";   // 兜底：绝大多数新仓库用 main
        }

        private static CloudFile ToCloudFile(Dictionary<string, object> d, string parentId)
        {
            string type = CloudJson.Str(d, "type");
            return new CloudFile
            {
                Id = CloudJson.Str(d, "path"),
                Name = CloudJson.Str(d, "name"),
                Size = CloudJson.Num(d, "size"),
                IsDirectory = type == "dir",
                ParentId = parentId ?? "",
                Token = CloudJson.Str(d, "sha"),
                HashType = "sha1",
                HashValue = CloudJson.Str(d, "sha"),
            };
        }

        // ================= 下载 =================

        public override async Task<DownloadSpec> GetDownloadSpecAsync(BrowseSession session,
            CloudFile file, CloudCredential credential, CancellationToken ct = default)
        {
            if (file.IsDirectory) throw new CloudApiException("目录不能直接下载");

            string owner = session.Meta("owner");
            string repo = session.Meta("repo");
            string reference = session.Meta("ref");

            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                throw new CloudApiException("GitHub：缺少仓库信息，请重新打开链接");

            if (string.IsNullOrEmpty(reference))
                reference = await GetDefaultBranchAsync(owner, repo, credential, ct).ConfigureAwait(false);

            // 直接拼 raw 地址：GitHub 不需要"取链"这一步
            string url = RawBase + "/" + owner + "/" + repo + "/"
                       + (string.IsNullOrEmpty(reference) ? "main" : reference) + "/"
                       + Uri.EscapeDataString(file.Id ?? "").Replace("%2F", "/");

            var spec = new DownloadSpec
            {
                Url = url,
                FileName = file.Name,
                ExpectedSize = file.Size,
            };
            spec.Headers["User-Agent"] = CloudHttp.DefaultUa;

            // 私有仓库要给 raw 地址带 token
            if (!string.IsNullOrEmpty(credential?.Primary))
                spec.Headers["Authorization"] = "token " + credential.Primary;

            return spec;
        }

        // ================= 辅助 =================

        private static Dictionary<string, string> Headers(CloudCredential credential)
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", CloudHttp.DefaultUa },
                { "Accept", "application/vnd.github+json" },
                { "X-GitHub-Api-Version", "2022-11-28" },
            };
            if (!string.IsNullOrEmpty(credential?.Primary))
                h["Authorization"] = "token " + credential.Primary;
            return h;
        }

        private sealed class RepoTarget
        {
            public string Owner;
            public string Repo;
            public string Ref;
            public string Path;
        }

        /// <summary>从 GitHub URL 解析出 owner / repo / ref / path。</summary>
        private static RepoTarget ParseRepoUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            string s = url;
            int i = s.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            s = s.Substring(i + "github.com".Length).TrimStart('/');

            // 去掉 query / fragment
            int cut = s.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);

            var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;

            var t = new RepoTarget
            {
                Owner = parts[0],
                // 去掉 .git 后缀
                Repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? parts[1].Substring(0, parts[1].Length - 4) : parts[1],
            };

            // /tree/{ref}/{path...} 或 /blob/{ref}/{path...}
            if (parts.Length >= 4 && (parts[2] == "tree" || parts[2] == "blob"))
            {
                t.Ref = parts[3];
                if (parts.Length > 4)
                    t.Path = string.Join("/", parts.Skip(4));
            }
            return t;
        }
    }
}
