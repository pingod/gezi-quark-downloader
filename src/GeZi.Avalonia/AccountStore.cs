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
using System.Text.Json;
using GeZi.Core.Support;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 一个本机保存的账号档案。
    /// 与 WPF 版的 <c>GeZi.AccountProfile</c> 语义一致（Key / Nick / Cookie / LastUsedUtc），
    /// 但序列化格式从 XML 换成了 JSON —— 原生版整套配置都用 JSON，没必要为账号单独引 XML。
    /// </summary>
    public sealed class AccountProfile
    {
        /// <summary>
        /// 所属平台（CloudPlatform.Key）。
        /// 多网盘支持后，同一个"账号"概念要按平台区分 ——
        /// 否则夸克的 Pingod 和百度的某账号会被当成同一个 Key 互相覆盖。
        /// </summary>
        public string Platform { get; set; } = "quark";

        /// <summary>账号稳定标识（夸克返回的 uid/account_id）。用于去重与文件名。</summary>
        public string Key { get; set; } = "";

        /// <summary>显示昵称。取不到时回退到账号标识。</summary>
        public string Nick { get; set; } = "";

        /// <summary>登录 Cookie（落盘前由 Store 加密）。</summary>
        public string Cookie { get; set; } = "";

        /// <summary>最近使用时间（UTC），列表按它倒序。</summary>
        public DateTime LastUsedUtc { get; set; }

        public string DisplayName => string.IsNullOrEmpty(Nick) ? Key : Nick;

        /// <summary>平台显示名（界面用）。</summary>
        public string PlatformLabel =>
            Core.Cloud.CloudPlatform.FromKey(Platform)?.ShortName ?? Platform ?? "";
    }

    /// <summary>
    /// 多账号档案的读写。
    ///
    /// 设计原则与 WPF 版一致：**失败不抛**。账号档案属于锦上添花的功能，
    /// 任何一个文件读坏了都不该影响登录与下载主流程。
    ///
    /// 存储布局：
    /// <code>
    ///   ~/.config/GeZi/accounts/{key}.json
    /// </code>
    /// Cookie 字段用 <see cref="SecretProtector"/> 加密（Linux = AES-GCM）。
    /// </summary>
    public static class AccountStore
    {
        private static readonly object Locker = new object();

        public static string AccountsDir => Path.Combine(
            Path.GetDirectoryName(SettingsStore.ConfigPath) ?? ".", "accounts");

        /// <summary>把账号标识转成安全文件名（去非法字符并限长）。</summary>
        public static string SafeKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "account";

            var sb = new System.Text.StringBuilder(key.Length);
            foreach (char c in key)
            {
                if (c < 0x20 || c == '\\' || c == '/' || c == ':' || c == '*' ||
                    c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim().Trim('.', ' ');
            if (s.Length > 40) s = s.Substring(0, 40);
            return s.Length == 0 ? "account" : s;
        }

        private static string ProfilePath(string key, string platform = "quark")
            => Path.Combine(AccountsDir, SafeKey(platform) + "_" + SafeKey(key) + ".json");

        /// <summary>保存/更新账号档案（同 Key 覆盖）。</summary>
        public static void Save(string key, string nick, string cookie,
            string platform = "quark")
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (Locker)
            {
                try
                {
                    Directory.CreateDirectory(AccountsDir);

                    var p = LoadRaw(ProfilePath(key, platform))
                            ?? new AccountProfile { Key = key, Platform = platform };
                    p.Key = key;
                    p.Platform = platform;
                    if (!string.IsNullOrEmpty(nick)) p.Nick = nick;
                    p.Cookie = SecretProtector.Protect(cookie ?? "");
                    p.LastUsedUtc = DateTime.UtcNow;

                    string path = ProfilePath(key, platform);
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(p,
                        new JsonSerializerOptions { WriteIndented = true }));

                    // net48 的 File.Move 没有 overwrite 参数
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch
                {
                    // 存不下就算了，不影响本次登录使用
                }
            }
        }

        /// <summary>读取全部账号，按最近使用倒序。Cookie 已解密。</summary>
        /// <summary>读取指定平台的账号；platform 为 null 时返回全部。</summary>
        public static List<AccountProfile> LoadByPlatform(string platform)
        {
            var list = new List<AccountProfile>();
            lock (Locker)
            {
                try
                {
                    if (!Directory.Exists(AccountsDir))
                        return list;

                    foreach (var f in Directory.GetFiles(AccountsDir, "*.json"))
                    {
                        var p = LoadRaw(f);
                        if (p == null || string.IsNullOrEmpty(p.Key)) continue;
                        p.Cookie = SecretProtector.Unprotect(p.Cookie);
                        list.Add(p);
                    }
                }
                catch
                {
                    // 目录读不了 → 返回已读到的部分
                }
            }
            return list.OrderByDescending(p => p.LastUsedUtc).ToList();
        }

        /// <summary>读取全部账号（跨平台）。</summary>
        public static List<AccountProfile> LoadAll()
        {
            var all = new List<AccountProfile>();
            lock (Locker)
            {
                try
                {
                    if (!Directory.Exists(AccountsDir)) return all;
                    foreach (var f in Directory.GetFiles(AccountsDir, "*.json"))
                    {
                        var p = LoadRaw(f);
                        if (p == null || string.IsNullOrEmpty(p.Key)) continue;
                        p.Cookie = SecretProtector.Unprotect(p.Cookie);
                        all.Add(p);
                    }
                }
                catch { }
            }
            return all.OrderByDescending(p => p.LastUsedUtc).ToList();
        }

        /// <summary>删除一个账号档案。</summary>
        public static bool Delete(string key, string platform = null)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (Locker)
            {
                try
                {
                    // platform 为 null → 删所有平台的同名 Key
                    if (string.IsNullOrEmpty(platform))
                    {
                        bool any = false;
                        foreach (var f in Directory.GetFiles(AccountsDir, "*.json"))
                        {
                            var prof = LoadRaw(f);
                            if (prof != null && prof.Key == key)
                            {
                                File.Delete(f);
                                any = true;
                            }
                        }
                        return any;
                    }

                    string p = ProfilePath(key, platform);
                    if (!File.Exists(p)) return false;
                    File.Delete(p);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static AccountProfile LoadRaw(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<AccountProfile>(json);
            }
            catch
            {
                return null;   // 单个文件坏了不影响其它账号
            }
        }
    }
}
