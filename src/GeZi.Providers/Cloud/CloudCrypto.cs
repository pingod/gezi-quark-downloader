// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// 几个平台的加解密原语（照着 wenxi 的 crypto_box / 各 provider 实现）。
    ///
    /// 为什么集中在一个文件：这些是**协议级**的固定常量与算法，
    /// 散落在各 provider 里既难审计也容易抄错（尤其是 padding 和 IV）。
    /// </summary>
    public static class CloudCrypto
    {
        // ================= 联通云盘（AES-128-CBC + 固定 IV） =================

        /// <summary>联通云盘的固定 IV（来自 wopan.dart:20）。</summary>
        private const string WopanIv = "wNSOYIB1k1DjY5lA";

        /// <summary>联通云盘的 clientSecret，同时用作 api-user 通道的密钥。</summary>
        public const string WopanClientSecret = "XFmi9GS2hzk98jGX";

        /// <summary>联通 clientId。</summary>
        public const string WopanClientId = "1001000021";

        /// <summary>
        /// 联通的密钥派生：
        /// - <c>api-user</c> 通道用 clientSecret
        /// - 其它通道用 access_token 的前 16 个字符
        /// </summary>
        public static string WopanKey(string channel, string accessToken)
        {
            if (string.Equals(channel, "api-user", StringComparison.OrdinalIgnoreCase))
                return WopanClientSecret;

            if (string.IsNullOrEmpty(accessToken) || accessToken.Length < 16)
                throw new CloudApiException("联通云盘：访问令牌无效，请重新登录");

            return accessToken.Substring(0, 16);
        }

        /// <summary>联通云盘加密：AES-128-CBC / PKCS7 / 固定 IV → Base64。</summary>
        public static string WopanEncrypt(string json, string channel, string accessToken)
        {
            byte[] key = Encoding.UTF8.GetBytes(WopanKey(channel, accessToken));
            byte[] iv = Encoding.UTF8.GetBytes(WopanIv);
            byte[] plain = Encoding.UTF8.GetBytes(json ?? "");

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var enc = aes.CreateEncryptor())
                    return Convert.ToBase64String(enc.TransformFinalBlock(plain, 0, plain.Length));
            }
        }

        /// <summary>联通云盘解密（响应可能是加密串，也可能是明文 JSON）。</summary>
        public static string WopanDecrypt(string cipherText, string channel, string accessToken)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";

            // 明文 JSON 直接返回（联通有时不加密）
            string trimmed = cipherText.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                return cipherText;

            try
            {
                byte[] key = Encoding.UTF8.GetBytes(WopanKey(channel, accessToken));
                byte[] iv = Encoding.UTF8.GetBytes(WopanIv);
                byte[] data = Convert.FromBase64String(trimmed);

                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var dec = aes.CreateDecryptor())
                        return Encoding.UTF8.GetString(dec.TransformFinalBlock(data, 0, data.Length));
                }
            }
            catch
            {
                return cipherText;
            }
        }

        // ================= 小飞机（AES-128-ECB，固定 key） =================

        /// <summary>小飞机的固定密钥（来自 feijipan.dart:38，硬编码在客户端里）。</summary>
        private const string FeijipanKey = "dingHao-disk-app";

        /// <summary>
        /// 小飞机加密：AES-128-ECB / PKCS7 → 十六进制小写。
        /// 用于 downloadId / auth 参数。
        /// </summary>
        public static string FeijipanEncrypt(string plain)
        {
            byte[] key = Encoding.UTF8.GetBytes(FeijipanKey);
            byte[] data = Encoding.UTF8.GetBytes(plain ?? "");

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;

                using (var enc = aes.CreateEncryptor())
                {
                    byte[] cipher = enc.TransformFinalBlock(data, 0, data.Length);
                    return ToHex(cipher);
                }
            }
        }

        /// <summary>
        /// 小飞机解密下载地址：AES-128-ECB → 十六进制字符串。
        ///
        /// ⚠️ 校验很严格（wenxi 的做法）：
        ///   长度 ≤ 16384、长度是 32 的倍数、必须是纯十六进制。
        ///   不满足就说明响应结构变了或拿到的是错误页，不能硬解。
        /// </summary>
        public static string FeijipanDecryptUrl(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new CloudApiException("小飞机：下载地址为空");

            if (value.Length > 16384 || value.Length % 32 != 0 || !IsHex(value))
                throw new CloudApiException("小飞机：下载地址格式异常");

            try
            {
                byte[] key = Encoding.UTF8.GetBytes(FeijipanKey);
                byte[] data = FromHex(value);

                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var dec = aes.CreateDecryptor())
                    {
                        byte[] plain = dec.TransformFinalBlock(data, 0, data.Length);
                        return Encoding.UTF8.GetString(plain);
                    }
                }
            }
            catch (Exception ex) when (!(ex is CloudApiException))
            {
                throw new CloudApiException("小飞机：下载地址解密失败（" + ex.Message + "）");
            }
        }

        // ================= 通用辅助 =================

        public static string ToHex(byte[] data)
        {
            if (data == null) return "";
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                throw new ArgumentException("十六进制字符串长度必须为偶数");

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>MD5（部分平台上传秒传要用）。</summary>
        public static string Md5Hex(byte[] data)
        {
            if (data == null) return "";
            using (var md5 = MD5.Create())
                return ToHex(md5.ComputeHash(data));
        }

        /// <summary>流式计算文件的 MD5（大文件上传用，不整体读进内存）。</summary>
        public static async Task<string> Md5FileAsync(string path, CancellationToken ct = default)
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)
                    .ConfigureAwait(false)) > 0)
                {
                    md5.TransformBlock(buffer, 0, read, null, 0);
                }
                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(md5.Hash);
            }
        }

        // ================= 天翼云盘上传通道（RSA + AES + HMAC） =================

        /// <summary>
        /// 天翼上传的请求签名。
        ///
        /// 天翼上传走的是一套**独立的加密通道**（与普通 API 不同）：
        ///   1. 生成 12 字节随机 secret（转 24 位十六进制）
        ///   2. 用 secret 前 16 字符作 AES-128-ECB 密钥，加密 params
        ///   3. 用 secret 作 HMAC-SHA1 密钥，签名一串固定格式的元信息
        ///   4. 用服务端 RSA 公钥加密 secret 放进 EncryptionText 头
        ///
        /// 三样缺一不可，任一出错服务端都只回一个笼统的失败。
        /// </summary>
        public sealed class TianyiUploadSignature
        {
            /// <summary>24 位十六进制随机串（同时用作 AES 密钥与 HMAC 密钥）。</summary>
            public string Secret { get; set; } = "";

            /// <summary>AES-128-ECB 加密后的 params（十六进制）。</summary>
            public string EncryptedParams { get; set; } = "";

            /// <summary>HMAC-SHA1 签名（十六进制）。</summary>
            public string Signature { get; set; } = "";

            /// <summary>RSA 加密后的 secret（放进 EncryptionText 头）。</summary>
            public string EncryptionText { get; set; } = "";

            /// <summary>时间戳（毫秒）。</summary>
            public string Date { get; set; } = "";
        }

        /// <summary>
        /// 构造一次天翼上传请求的签名。
        /// </summary>
        /// <param name="path">接口路径（如 /person/initMultiUpload）</param>
        /// <param name="sessionKey">上传会话密钥</param>
        /// <param name="params">表单参数</param>
        /// <param name="rsaPublicKey">服务端 RSA 公钥（PEM 或裸 Base64）</param>
        public static TianyiUploadSignature TianyiSign(string path, string sessionKey,
            IDictionary<string, string> param, string rsaPublicKey)
        {
            // ① 24 位十六进制随机 secret
            string secret = RandomHex(24);

            // ② params 按 key=value&key=value 拼好，AES-128-ECB 加密
            var raw = new StringBuilder();
            if (param != null)
            {
                foreach (var kv in param)
                {
                    if (raw.Length > 0) raw.Append('&');
                    raw.Append(kv.Key).Append('=').Append(kv.Value ?? "");
                }
            }

            string encrypted = AesEcbEncryptHex(raw.ToString(), secret.Substring(0, 16));

            // ③ HMAC-SHA1 签名
            string date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string toSign = "SessionKey=" + sessionKey +
                            "&Operate=GET&RequestURI=" + path +
                            "&Date=" + date + "&params=" + encrypted;

            string signature = HmacSha1Hex(secret, toSign);

            return new TianyiUploadSignature
            {
                Secret = secret,
                EncryptedParams = encrypted,
                Signature = signature,
                EncryptionText = RsaEncryptHex(rsaPublicKey, secret),
                Date = date,
            };
        }

        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static string RandomHex(int charCount)
        {
            int bytes = (charCount + 1) / 2;
            byte[] buf = new byte[bytes];
            lock (Rng) Rng.GetBytes(buf);

            var sb = new StringBuilder(charCount);
            foreach (var b in buf)
            {
                sb.Append(b.ToString("x2"));
                if (sb.Length >= charCount) break;
            }
            return sb.ToString().Substring(0, charCount);
        }

        /// <summary>AES-128-ECB / PKCS7 → 十六进制小写。</summary>
        public static string AesEcbEncryptHex(string plain, string key16)
        {
            byte[] key = Encoding.UTF8.GetBytes(key16 ?? "");
            if (key.Length != 16)
                throw new CloudApiException("内部错误：AES-128 密钥必须是 16 字节");

            byte[] data = Encoding.UTF8.GetBytes(plain ?? "");

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;

                using (var enc = aes.CreateEncryptor())
                    return ToHex(enc.TransformFinalBlock(data, 0, data.Length));
            }
        }

        /// <summary>HMAC-SHA1 → 十六进制小写。</summary>
        public static string HmacSha1Hex(string key, string message)
        {
            using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key ?? "")))
                return ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(message ?? "")));
        }

        /// <summary>
        /// RSA 公钥加密（PKCS#1 v1.5）→ 十六进制小写。
        /// 公钥可以是 PEM（含 BEGIN 头）或裸 Base64。
        /// </summary>
        public static string RsaEncryptHex(string publicKey, string plain)
        {
            if (string.IsNullOrEmpty(publicKey))
                throw new CloudApiException("缺少服务端公钥，无法加密上传凭据");

            try
            {
                using (var rsa = System.Security.Cryptography.RSA.Create())
                {
                    LoadPublicKey(rsa, publicKey);

                    byte[] data = Encoding.UTF8.GetBytes(plain ?? "");
                    byte[] cipher = rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
                    return ToHex(cipher);
                }
            }
            catch (Exception ex) when (!(ex is CloudApiException))
            {
                throw new CloudApiException("RSA 加密失败：" + ex.Message);
            }
        }

        private static void LoadPublicKey(RSA rsa, string key)
        {
            string k = key.Trim();

            // 去掉 PEM 头尾，只留 Base64 正文
            var sb = new StringBuilder();
            foreach (var line in k.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("-----")) continue;
                sb.Append(t);
            }

            byte[] der = Convert.FromBase64String(sb.ToString());

#if NET8_0_OR_GREATER
            rsa.ImportSubjectPublicKeyInfo(der, out _);
#else
            // net48 没有 ImportSubjectPublicKeyInfo → 手工解析 SPKI（见下面 DecodeSpki）
            try
            {
                rsa.ImportParameters(DecodeSpki(der));
            }
            catch (Exception ex)
            {
                throw new CloudApiException("无法解析服务端 RSA 公钥：" + ex.Message);
            }
#endif
        }

#if !NET8_0_OR_GREATER
        /// <summary>
        /// 手工解析 SubjectPublicKeyInfo（DER）取出 RSA 的 n/e。
        ///
        /// net48 没有 <c>RSA.ImportSubjectPublicKeyInfo</c>，
        /// 而引入 BouncyCastle 只为解一个公钥太重。
        /// SPKI 结构固定：SEQUENCE { AlgorithmIdentifier, BIT STRING { RSAPublicKey } }
        ///   RSAPublicKey ::= SEQUENCE { modulus INTEGER, publicExponent INTEGER }
        /// 这里只做"够用"的最小 ASN.1 解析。
        /// </summary>
        private static RSAParameters DecodeSpki(byte[] der)
        {
            int pos = 0;

            // 外层 SEQUENCE
            ReadTag(der, ref pos, 0x30);
            ReadLength(der, ref pos);

            // AlgorithmIdentifier SEQUENCE（跳过）
            ReadTag(der, ref pos, 0x30);
            int algLen = ReadLength(der, ref pos);
            pos += algLen;

            // BIT STRING
            ReadTag(der, ref pos, 0x03);
            int bitLen = ReadLength(der, ref pos);
            pos++;   // 跳过 unused-bits 字节

            // RSAPublicKey SEQUENCE
            ReadTag(der, ref pos, 0x30);
            ReadLength(der, ref pos);

            // modulus INTEGER
            ReadTag(der, ref pos, 0x02);
            int nLen = ReadLength(der, ref pos);
            byte[] n = new byte[nLen];
            Buffer.BlockCopy(der, pos, n, 0, nLen);
            pos += nLen;

            // publicExponent INTEGER
            ReadTag(der, ref pos, 0x02);
            int eLen = ReadLength(der, ref pos);
            byte[] e = new byte[eLen];
            Buffer.BlockCopy(der, pos, e, 0, eLen);

            return new RSAParameters { Modulus = TrimLeadingZero(n), Exponent = TrimLeadingZero(e) };
        }

        private static void ReadTag(byte[] data, ref int pos, byte expected)
        {
            if (pos >= data.Length || data[pos] != expected)
                throw new CloudApiException("公钥格式异常（ASN.1 标签不符）");
            pos++;
        }

        private static int ReadLength(byte[] data, ref int pos)
        {
            int b = data[pos++];
            if ((b & 0x80) == 0) return b;

            int count = b & 0x7F;
            int len = 0;
            for (int i = 0; i < count; i++) len = (len << 8) | data[pos++];
            return len;
        }

        private static byte[] TrimLeadingZero(byte[] v)
        {
            if (v.Length > 1 && v[0] == 0)
            {
                var r = new byte[v.Length - 1];
                Buffer.BlockCopy(v, 1, r, 0, r.Length);
                return r;
            }
            return v;
        }
#endif

        /// <summary>分段 MD5（天翼大文件上传要每片的 MD5，大写）。</summary>
        public static async Task<string> Md5RangeAsync(string path, long start, long end,
            CancellationToken ct = default)
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true))
            {
                fs.Seek(start, SeekOrigin.Begin);
                long remaining = end - start;
                byte[] buffer = new byte[81920];

                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int n = await fs.ReadAsync(buffer, 0, want, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    md5.TransformBlock(buffer, 0, n, null, 0);
                    remaining -= n;
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(md5.Hash).ToUpperInvariant();
            }
        }

        /// <summary>CRC32（123 网盘的签名要用）。</summary>
        public static uint Crc32(byte[] data)
        {
            if (data == null) return 0;

            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
            return ~crc;
        }
    }
}
