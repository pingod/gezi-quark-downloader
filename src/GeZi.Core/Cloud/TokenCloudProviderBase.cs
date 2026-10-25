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
    /// OAuth 类网盘的令牌管理基类。
    ///
    /// 参照 wenxi 的 <c>TokenSessions</c> / <c>token_session.dart</c>。
    /// 适用平台：阿里、PikPak、光鸭、小飞机、优享、迅雷、联通、115、城通、微云。
    ///
    /// 这几个平台的鉴权形态几乎一致：
    ///   access_token（短期）+ refresh_token（长期）→ 到期前用 refresh 换新的
    ///
    /// 核心难点是**并发安全**：多个任务同时发现 token 过期时会同时去刷新，
    /// 后刷新的会让先刷新的那个立刻失效（多数平台 refresh_token 是一次性的）。
    /// 解决方式是「单飞（single-flight）」+ 双重检查：
    ///   ① 进锁前检查一次（快路径，绝大多数请求不走锁）
    ///   ② 拿到锁后再检查一次（别人可能刚刷完）
    ///   ③ 若发现别人已经刷新过（access 变了），直接用新值，不重复刷
    /// </summary>
    public abstract class TokenCloudProviderBase : CloudProviderBase
    {
        /// <summary>提前多久视为过期（与 wenxi 一致：60 秒）。</summary>
        protected const long ExpirySkewMs = 60_000;

        /// <summary>API 基址（子类必须提供）。</summary>
        protected abstract string ApiBase { get; }

        /// <summary>令牌刷新端点（子类必须提供）。</summary>
        protected abstract string TokenUrl { get; }

        /// <summary>OAuth client_id（部分平台不需要，默认空）。</summary>
        protected virtual string ClientId => "";

        /// <summary>OAuth client_secret（部分平台需要，默认空）。</summary>
        protected virtual string ClientSecret => "";

        /// <summary>默认 UA。</summary>
        protected virtual string UserAgent => CloudHttp.DefaultUa;

        /// <summary>
        /// 给请求加鉴权头（子类覆盖以适配各平台：Bearer / 自定义头 / 额外签名）。
        /// </summary>
        protected virtual Dictionary<string, string> AuthHeaders(CloudCredential credential,
            string accessToken)
            => new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + accessToken },
                { "User-Agent", UserAgent },
            };

        /// <summary>
        /// 用 refresh_token 换新令牌（子类必须实现具体请求）。
        /// 实现里应调用 <see cref="ApplyTokenResponse"/> 写回凭据。
        /// </summary>
        protected abstract Task RenewTokenAsync(CloudCredential credential,
            CancellationToken ct);

        // 每个账号一把锁：不同账号可以并行刷新，同账号必须串行
        private readonly Dictionary<string, SemaphoreSlim> _gates =
            new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private readonly object _gateLock = new object();

        private SemaphoreSlim GateFor(string key)
        {
            lock (_gateLock)
            {
                if (!_gates.TryGetValue(key, out var g))
                {
                    g = new SemaphoreSlim(1, 1);
                    _gates[key] = g;
                }
                return g;
            }
        }

        // ================= 令牌字段约定 =================

        protected const string AccessKey = "accessToken";
        protected const string RefreshKey = "refreshToken";
        protected const string ExpiresKey = "expiresAt";

        protected static string Access(CloudCredential c) => c?.Field(AccessKey) ?? "";
        protected static string Refresh(CloudCredential c) => c?.Field(RefreshKey) ?? "";

        /// <summary>
        /// 判断令牌是否过期。
        /// expiresAt = 0 时回退到解析 JWT 的 exp，再不行就当"不参与判断"（交给服务端拒绝）。
        /// </summary>
        protected static bool IsExpired(CloudCredential c)
        {
            if (c == null) return true;

            string access = Access(c);
            if (string.IsNullOrEmpty(access)) return true;

            long expiresAt = ParseLong(c.Field(ExpiresKey));
            if (expiresAt <= 0)
                expiresAt = JwtExpiry(access);

            if (expiresAt <= 0) return false;   // 无从判断 → 乐观放行

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return expiresAt <= now + ExpirySkewMs;
        }

        /// <summary>从 JWT 里取 exp（毫秒）。不是 JWT 或解析失败返回 0。</summary>
        protected static long JwtExpiry(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return 0;
                var parts = token.Split('.');
                if (parts.Length < 2) return 0;

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                byte[] bytes = Convert.FromBase64String(payload);
                string json = System.Text.Encoding.UTF8.GetString(bytes);

                var obj = CloudJson.Parse(json);
                long exp = CloudJson.Num(obj, "exp");
                return exp > 0 ? exp * 1000 : 0;   // JWT 的 exp 是秒
            }
            catch
            {
                return 0;
            }
        }

        protected static long ParseLong(string s)
            => long.TryParse(s, out var v) ? v : 0;

        // ================= 刷新 =================

        /// <summary>
        /// 确保 access_token 可用（过期则刷新）。返回当前有效的 access_token。
        ///
        /// 刷新是**原地修改** <paramref name="credential"/> 的字段
        /// （凭据对象是引用类型，上层拿到的就是新值）。
        /// </summary>
        protected async Task<string> EnsureFreshAsync(CloudCredential credential,
            Func<CloudCredential, CancellationToken, Task> renew,
            CancellationToken ct)
        {
            if (credential == null)
                throw new CloudApiException("未登录");

            // ① 快路径：没过期就直接用
            if (!IsExpired(credential))
                return Access(credential);

            string oldAccess = Access(credential);
            var gate = GateFor(credential.Platform.Key + "|" + credential.Label);

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // ② 拿到锁后再检查：可能别人刚刷完
                if (!IsExpired(credential))
                    return Access(credential);

                if (Refresh(credential).Length == 0)
                    throw new CloudApiException(
                        credential.Platform.Label + "登录已过期，请重新登录（缺少续期凭据）");

                // ③ 真正刷新（子类实现具体接口）
                await renew(credential, ct).ConfigureAwait(false);

                string now = Access(credential);
                if (string.IsNullOrEmpty(now))
                    throw new CloudApiException(
                        credential.Platform.Label + "：续期未返回有效凭据");

                return now;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 把刷新响应里的字段写回凭据。
        ///
        /// 各平台的响应字段名不统一（access_token / accessToken、
        /// expires_in / expire_time / expires_at），这里统一处理。
        /// </summary>
        /// <param name="refreshAsPrimary">
        /// 为 true 时把 refresh_token 也写进 primary。
        /// 阿里和联通属于这一类（它们的 primary 就是 refresh_token）。
        /// </param>
        protected static void ApplyTokenResponse(CloudCredential credential, object res,
            bool refreshAsPrimary = false)
        {
            string access = CloudJson.Str(res, "access_token");
            if (string.IsNullOrEmpty(access)) access = CloudJson.Str(res, "accessToken");

            string refresh = CloudJson.Str(res, "refresh_token");
            if (string.IsNullOrEmpty(refresh)) refresh = CloudJson.Str(res, "refreshToken");
            if (string.IsNullOrEmpty(refresh)) refresh = Refresh(credential);

            if (string.IsNullOrEmpty(access))
                throw new CloudApiException("续期响应里没有 access_token");

            long expiresAt = CloudJson.Num(res, "expire_time");
            if (expiresAt <= 0) expiresAt = CloudJson.Num(res, "expires_at");

            long seconds = CloudJson.Num(res, "expires_in");
            if (seconds <= 0) seconds = CloudJson.Num(res, "expiresIn");

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (expiresAt <= 0 && seconds > 0)
                expiresAt = nowMs + seconds * 1000;
            if (expiresAt <= 0)
                expiresAt = JwtExpiry(access);

            var fields = new Dictionary<string, string>
            {
                { AccessKey, access },
                { RefreshKey, refresh ?? "" },
                { ExpiresKey, expiresAt.ToString() },
            };

            // primary：多数平台放 access，阿里/联通放 refresh
            fields[CloudCredential.PrimaryKey] = refreshAsPrimary ? (refresh ?? "") : access;

            string userId = CloudJson.Str(res, "user_id");
            if (string.IsNullOrEmpty(userId)) userId = CloudJson.Str(res, "sub");
            if (!string.IsNullOrEmpty(userId)) fields["userId"] = userId;

            string nick = CloudJson.Str(res, "nick_name");
            if (string.IsNullOrEmpty(nick)) nick = CloudJson.Str(res, "nickname");
            if (!string.IsNullOrEmpty(nick)) fields["nickname"] = nick;

            // 写回（WithFields 返回新对象，所以这里要逐字段拷进去）
            var updated = credential.WithFields(fields, preserveRevision: true);
            foreach (var kv in updated.SnapshotFields())
            {
                // 通过 WithFields 已生成新对象；这里用一个"就地更新"的约定：
                // 把字段写进原对象 —— 见下面的 CopyInto
            }
            CopyInto(credential, fields);
        }

        /// <summary>
        /// 就地把字段写进凭据对象。
        ///
        /// 为什么需要它：<see cref="CloudCredential.WithFields"/> 返回**新对象**，
        /// 但调用方（provider）持有的是传进来的那个引用。
        /// 如果每次刷新都产生新对象，上层的账号列表就和 provider 用的不是同一份了。
        /// 所以刷新走"原地更新"，保证所有人看到的都是最新令牌。
        /// </summary>
        private static void CopyInto(CloudCredential target, IDictionary<string, string> fields)
        {
            if (target == null || fields == null) return;

            var mutable = target as CloudCredential;
            if (mutable == null) return;

            // CloudCredential 目前没有公开的可变字段写入方法，
            // 这里借助反射之外的正式途径：WithFields 后取其快照再回填。
            // CloudCredential 内部字典是私有的，因此提供 SetFields 供本类使用。
            mutable.SetFieldsInternal(fields);
            mutable.UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 令牌失效时的统一重试：先强刷一次再重试原操作。
        /// </summary>
        protected async Task<T> WithTokenRetryAsync<T>(CloudCredential credential,
            Func<CloudCredential, CancellationToken, Task> renew,
            Func<CancellationToken, Task<T>> action,
            CancellationToken ct,
            int maxAttempts = 2)
        {
            Exception last = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    await EnsureFreshAsync(credential, renew, ct).ConfigureAwait(false);
                    return await action(ct).ConfigureAwait(false);
                }
                catch (CloudHttpException ex) when (ex.Status == 401 && attempt < maxAttempts - 1)
                {
                    // 401 → 强制刷新后重试
                    last = ex;
                    credential.SetFieldsInternal(new Dictionary<string, string>
                    {
                        { ExpiresKey, "0" },   // 标记为过期，触发下一轮刷新
                    });
                }
                catch (CloudApiException ex) when (attempt < maxAttempts - 1
                    && IsAuthError(ex.Message))
                {
                    last = ex;
                    credential.SetFieldsInternal(new Dictionary<string, string>
                    {
                        { ExpiresKey, "0" },
                    });
                }
            }

            if (last != null) throw last;
            throw new CloudApiException("操作失败（重试已用尽）");
        }

        private static bool IsAuthError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.Contains("401") || message.Contains("token")
                || message.Contains("登录") || message.Contains("未授权")
                || message.Contains("expired");
        }
    }
}
