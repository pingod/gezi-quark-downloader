using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GeZi.Providers.Cloud;

internal static class UploadTests
{
    private static int _fail, _pass;

    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[15] 上传能力与加密原语");

        TestUploadDeclarations();
        await TestHashHelpers();
        TestTianyiSignature();
    }

    private static void TestUploadDeclarations()
    {
        Console.WriteLine("  -- 上传能力声明");

        var quark = CloudProviderRegistry.Get(CloudPlatform.Quark);
        Check("夸克支持上传", quark.SupportsUpload);

        var p123 = CloudProviderRegistry.Get(CloudPlatform.Pan123);
        Check("123 支持上传", p123.SupportsUpload);

        var tianyi = CloudProviderRegistry.Get(CloudPlatform.Tianyi);
        Check("天翼支持上传", tianyi.SupportsUpload);

        // 不支持的要如实返回 false
        var lanzou = CloudProviderRegistry.Get(CloudPlatform.Lanzou);
        Check("蓝奏不支持上传", !lanzou.SupportsUpload);

        var github = CloudProviderRegistry.Get(CloudPlatform.Github);
        Check("GitHub 不支持上传", !github.SupportsUpload);
    }

    private static async Task TestHashHelpers()
    {
        Console.WriteLine("  -- 哈希辅助（上传必需）");

        string tmp = Path.Combine(Path.GetTempPath(),
            "gezi-upload-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            byte[] data = new byte[1024 * 100];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(tmp, data);

            string full = await CloudCrypto.Md5FileAsync(tmp);
            Check("整文件 MD5 长度 32", full.Length == 32, full);
            Check("整文件 MD5 是十六进制", CloudCrypto.IsHex(full));

            // 分段 MD5 应该与整段一致（当分段覆盖全文件时）
            string part = await CloudCrypto.Md5RangeAsync(tmp, 0, data.Length);
            Check("分段 MD5（全覆盖）等于整体", 
                string.Equals(part, full, StringComparison.OrdinalIgnoreCase),
                part);

            // 部分分段应该不同
            string half = await CloudCrypto.Md5RangeAsync(tmp, 0, data.Length / 2);
            Check("部分分段 MD5 不同", !string.Equals(half, full, StringComparison.OrdinalIgnoreCase));

            // 分段 MD5 是大写（天翼要求）
            Check("天翼分片 MD5 为大写", part == part.ToUpperInvariant(), "ok");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static void TestTianyiSignature()
    {
        Console.WriteLine("  -- 天翼上传签名");

        // 生成一对 RSA 密钥做往返验证
        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
        {
            string pubPem = rsa.ExportSubjectPublicKeyInfoPem();

            var param = new System.Collections.Generic.Dictionary<string, string>
            {
                { "parentFolderId", "-11" },
                { "fileName", "test.txt" },
                { "fileSize", "1024" },
            };

            CloudCrypto.TianyiUploadSignature sig;
            try
            {
                sig = CloudCrypto.TianyiSign("/person/initMultiUpload",
                    "session-key-123", param, pubPem);
            }
            catch (Exception ex)
            {
                Check("天翼签名生成", false, ex.GetType().Name + ": " + ex.Message);
                return;
            }

            Check("生成 24 位 secret", sig.Secret.Length == 24, sig.Secret.Length + " 位");
            Check("secret 是十六进制", CloudCrypto.IsHex(sig.Secret));
            Check("params 已加密（十六进制）", CloudCrypto.IsHex(sig.EncryptedParams)
                && sig.EncryptedParams.Length > 0, sig.EncryptedParams.Length + " 字符");
            Check("HMAC 签名长度 40", sig.Signature.Length == 40, sig.Signature.Length + " 字符");
            Check("RSA 加密结果非空", sig.EncryptionText.Length > 0,
                sig.EncryptionText.Length + " 字符");
            Check("时间戳是数字", long.TryParse(sig.Date, out _), sig.Date);

            // 用私钥解出 secret，确认 RSA 往返正确
            try
            {
                byte[] cipher = CloudCrypto.FromHex(sig.EncryptionText);
                byte[] plain = rsa.Decrypt(cipher,
                    System.Security.Cryptography.RSAEncryptionPadding.Pkcs1);
                string recovered = System.Text.Encoding.UTF8.GetString(plain);
                Check("RSA 往返还原 secret", recovered == sig.Secret, recovered);
            }
            catch (Exception ex)
            {
                Check("RSA 往返还原 secret", false, ex.Message);
            }

            // 同样的 AES 参数要能解回原文
            try
            {
                byte[] key = System.Text.Encoding.UTF8.GetBytes(sig.Secret.Substring(0, 16));
                byte[] data = CloudCrypto.FromHex(sig.EncryptedParams);

                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Key = key;
                    aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                    aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                    using (var dec = aes.CreateDecryptor())
                    {
                        byte[] plain = dec.TransformFinalBlock(data, 0, data.Length);
                        string recovered = System.Text.Encoding.UTF8.GetString(plain);
                        Check("AES 可解回 params 原文",
                            recovered.Contains("parentFolderId=-11")
                            && recovered.Contains("fileName=test.txt"), recovered);
                    }
                }
            }
            catch (Exception ex)
            {
                Check("AES 可解回 params 原文", false, ex.Message);
            }

            // 同一输入两次签名，secret 必须不同（随机性）
            var sig2 = CloudCrypto.TianyiSign("/person/initMultiUpload",
                "session-key-123", param, pubPem);
            Check("每次签名 secret 不同（随机）", sig.Secret != sig2.Secret, "ok");
        }
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("      ✓ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
        else { _fail++; Console.WriteLine("      ✗ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
    }

    public static int Failed => _fail;
}
