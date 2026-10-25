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
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 加密/解密本地敏感字符串（Cookie 登录态等）。
    ///
    /// 平台分成两条实现，**对外接口与密文前缀完全一致**，上层无需关心：
    ///
    /// - <b>Windows（net48）</b>：Windows DPAPI（<c>ProtectedData</c>），作用域 CurrentUser。
    ///   密文只能被同一台机器的同一个 Windows 用户解开 —— 这正是我们要的。
    ///
    /// - <b>Linux / macOS（net8.0）</b>：DPAPI 不存在，改用 **AES-256-GCM + 本机密钥文件**。
    ///   密钥放在 <c>~/.config/GeZi/secret.key</c>（权限 0600），首次使用时随机生成。
    ///   安全性定位与 DPAPI 相当：能读到你用户目录的人本来也就能读到配置文件，
    ///   这里防的是「配置文件被误同步/误拷贝到别处」，不是防本机同用户攻击。
    ///   密钥文件丢失 → 旧密文解不开，程序按「未登录」处理并让你重新登录，不会崩。
    ///
    /// 通用约定：
    /// - 密文以 "enc:v1:" 前缀标记，用于识别与平滑迁移：
    ///   读到没有前缀的字符串 → 视为旧版明文，直接返回原文，下次保存时自动加密。
    /// - 加解密失败一律降级为「明文原样返回 / 返回空串」，绝不因加密问题让程序起不来。
    /// </summary>
    public static class SecretProtector
    {
        private const string Prefix = "enc:v1:";

        // 额外的熵，防止别的程序用无参 DPAPI 直接解出我们的数据
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GeZi.Downloader.Cookie.v1");

        /// <summary>是否已加密（带我们的前缀标记）。</summary>
        public static bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// 加密明文。已是密文则原样返回；空字符串返回空字符串。
        /// </summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            if (IsProtected(plain))
                return plain;

            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                return Prefix + Convert.ToBase64String(ProtectRaw(raw));
            }
            catch
            {
                // 加密不可用（极罕见：用户配置文件损坏等）→ 保持可用性优先
                return plain;
            }
        }

        /// <summary>
        /// 解密。无前缀视为旧版明文直接原样返回；解不开（换了机器/换了用户/数据损坏）
        /// 则返回空串 —— 让调用方按「未登录」处理，而不是拿到一堆乱码去请求接口。
        /// </summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return "";
            if (!IsProtected(stored))
                return stored;   // 旧版明文，向后兼容

            try
            {
                string b64 = stored.Substring(Prefix.Length);
                byte[] enc = Convert.FromBase64String(b64);
                byte[] raw = UnprotectRaw(enc);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                // 解密失败：多半是配置文件被复制到了别的机器/用户
                return "";
            }
        }

        // ---------------- 平台实现 ----------------

#if NET8_0_OR_GREATER
        /// <summary>本机密钥文件路径（Linux/macOS）。</summary>
        private static string KeyFilePath
        {
            get
            {
                // 尊重 XDG：优先 $GEZI_CONFIG_DIR，其次 $XDG_CONFIG_HOME，兜底 ~/.config
                string root = Environment.GetEnvironmentVariable("GEZI_CONFIG_DIR");
                if (string.IsNullOrEmpty(root))
                {
                    string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                    root = string.IsNullOrEmpty(xdg)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                        : xdg;
                    root = Path.Combine(root, "GeZi");
                }
                return Path.Combine(root, "secret.key");
            }
        }

        private static readonly object KeyLock = new object();

        /// <summary>读取（必要时生成）本机 32 字节主密钥。</summary>
        private static byte[] GetOrCreateKey()
        {
            lock (KeyLock)
            {
                string path = KeyFilePath;
                if (File.Exists(path))
                {
                    byte[] existing = File.ReadAllBytes(path);
                    if (existing.Length >= 32)
                        return existing;
                    // 长度不对（被截断/手工改过）→ 重新生成，旧密文按解密失败处理
                }

                byte[] key = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(key);

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllBytes(path, key);
                TrySetOwnerOnly(path);
                return key;
            }
        }

        /// <summary>把密钥文件权限收成 0600（Windows 上会抛异常，忽略即可）。</summary>
        private static void TrySetOwnerOnly(string path)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // 某些文件系统（FAT/exFAT/网络盘）不支持 Unix 权限位 → 忽略
            }
        }

        /// <summary>AES-256-GCM：输出 nonce(12) + tag(16) + ciphertext。</summary>
        private static byte[] ProtectRaw(byte[] raw)
        {
            byte[] key = GetOrCreateKey();
            byte[] nonce = new byte[12];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);

            byte[] cipher = new byte[raw.Length];
            byte[] tag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, raw, cipher, tag, Entropy);

            byte[] outBuf = new byte[nonce.Length + tag.Length + cipher.Length];
            Buffer.BlockCopy(nonce, 0, outBuf, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, outBuf, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipher, 0, outBuf, nonce.Length + tag.Length, cipher.Length);
            return outBuf;
        }

        private static byte[] UnprotectRaw(byte[] enc)
        {
            if (enc.Length < 12 + 16)
                throw new CryptographicException("密文长度不足");

            byte[] key = GetOrCreateKey();
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] cipher = new byte[enc.Length - 28];
            Buffer.BlockCopy(enc, 0, nonce, 0, 12);
            Buffer.BlockCopy(enc, 12, tag, 0, 16);
            Buffer.BlockCopy(enc, 28, cipher, 0, cipher.Length);

            byte[] raw = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, 16))
                gcm.Decrypt(nonce, cipher, tag, raw, Entropy);
            return raw;
        }
#else
        private static byte[] ProtectRaw(byte[] raw)
            => ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);

        private static byte[] UnprotectRaw(byte[] enc)
            => ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
#endif
    }
}
