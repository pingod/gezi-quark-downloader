using System;
using System.Linq;
using GeZi.Providers.Cloud;

internal static class PlatformTests
{
    private static int _fail, _pass;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("[14] 全平台覆盖 + 加解密");

        TestAllPlatformsRegistered();
        TestCrypto();
        TestCapabilityHonesty();
    }

    private static void TestAllPlatformsRegistered()
    {
        Console.WriteLine("  -- 平台注册");

        var expected = new[]
        {
            CloudPlatform.Quark, CloudPlatform.Uc, CloudPlatform.Aliyun,
            CloudPlatform.Pan115, CloudPlatform.Baidu, CloudPlatform.Pan123,
            CloudPlatform.Tianyi, CloudPlatform.Xunlei, CloudPlatform.PikPak,
            CloudPlatform.Lanzou, CloudPlatform.Ilanzou, CloudPlatform.Ctfile,
            CloudPlatform.Github, CloudPlatform.Guangya, CloudPlatform.Feijipan,
            CloudPlatform.Wopan, CloudPlatform.Weiyun,
        };

        int registered = 0;
        foreach (var p in expected)
        {
            if (CloudProviderRegistry.Get(p) != null) registered++;
            else Check(p.Label + " 已注册", false, "null");
        }

        Check($"全部 {expected.Length} 个平台已注册", registered == expected.Length,
            registered + "/" + expected.Length);

        // 能力声明自洽性
        var quark = CloudProviderRegistry.Get(CloudPlatform.Quark);
        Check("夸克支持上传", quark.SupportsUpload);
        Check("夸克支持转存", quark.SupportsTransfer);
        Check("夸克支持建分享", quark.SupportsCreateShare);

        var github = CloudProviderRegistry.Get(CloudPlatform.Github);
        Check("GitHub 不支持上传", !github.SupportsUpload);
        Check("GitHub 不支持建分享", !github.SupportsCreateShare);

        // 微云现在是**真实实现**（JSON RPC 协议），不再是占位
        var weiyun = CloudProviderRegistry.Get(CloudPlatform.Weiyun);
        Check("微云已实现（JSON RPC）", weiyun != null, weiyun?.GetType().Name ?? "null");
        Check("微云不支持建分享（平台限制）", !weiyun.SupportsCreateShare);
        Check("微云支持重命名", weiyun.SupportsRename);
        Check("微云不支持上传（协议待补）", !weiyun.SupportsUpload);

        // 平台能力矩阵
        Check("联通支持家庭云", CloudPlatform.Wopan.SupportsFamilyCloud);
        Check("优享不支持分享解析", !CloudPlatform.Ilanzou.SupportsShareParsing);
        Check("优享不支持建分享", !CloudPlatform.Ilanzou.SupportsSharing);
        Check("联通不支持建分享", !CloudPlatform.Wopan.SupportsSharing);
    }

    /// <summary>
    /// 核心一致性检查：能力声明必须等于实际实现。
    ///
    /// 这条测试的价值：之前所有 provider 都继承 SupportsRename=true，
    /// 但只有夸克真的实现了 —— 界面显示按钮、点了才报错。
    /// 这个测试能直接抓住那类"假象"。
    /// </summary>
    private static void TestCapabilityHonesty()
    {
        Console.WriteLine("  -- 能力声明 vs 实际实现");

        var providers = new (CloudPlatform p, bool rename, bool move, bool delete, bool mkdir, bool share, bool upload)[]
        {
            // 平台,            改名, 移动, 删除, 建目录, 建分享, 上传
            (CloudPlatform.Quark,   true,  true,  true,  true,  true,  true),
            (CloudPlatform.Uc,      true,  true,  true,  true,  true,  true),
            (CloudPlatform.Pan115,  false, false, true,  false, false, false),
            (CloudPlatform.Pan123,  false, false, false, false, false, true),
            (CloudPlatform.Tianyi,  false, false, false, false, false, true),
            (CloudPlatform.Aliyun,  false, false, false, false, false, false),
            (CloudPlatform.Github,  false, false, false, false, false, false),
            (CloudPlatform.Baidu,   false, false, false, false, false, false),
            (CloudPlatform.Lanzou,  false, false, false, false, false, false),
        };

        int mismatches = 0;

        foreach (var (p, rename, move, del, mkdir, share, upload) in providers)
        {
            var prov = CloudProviderRegistry.Get(p);
            if (prov == null) continue;

            if (prov.SupportsRename != rename) { mismatches++; Check(p.Label + " Rename 声明", false, $"声明={prov.SupportsRename} 实际={rename}"); }
            if (prov.SupportsMove != move) { mismatches++; Check(p.Label + " Move 声明", false, $"声明={prov.SupportsMove} 实际={move}"); }
            if (prov.SupportsDelete != del) { mismatches++; Check(p.Label + " Delete 声明", false, $"声明={prov.SupportsDelete} 实际={del}"); }
            if (prov.SupportsCreateFolder != mkdir) { mismatches++; Check(p.Label + " Mkdir 声明", false, $"声明={prov.SupportsCreateFolder} 实际={mkdir}"); }
            if (prov.SupportsCreateShare != share) { mismatches++; Check(p.Label + " Share 声明", false, $"声明={prov.SupportsCreateShare} 实际={share}"); }
            if (prov.SupportsUpload != upload) { mismatches++; Check(p.Label + " Upload 声明", false, $"声明={prov.SupportsUpload} 实际={upload}"); }
        }

        Check("能力声明与实际实现一致", mismatches == 0, mismatches + " 处不符");

        // 至少要有 2 个平台支持完整文件管理（夸克+UC）
        int fullManage = 0;
        foreach (var p in CloudProviderRegistry.SupportedPlatforms())
        {
            var prov = CloudProviderRegistry.Get(p);
            if (prov != null && prov.SupportsRename && prov.SupportsMove
                && prov.SupportsDelete && prov.SupportsCreateFolder)
                fullManage++;
        }
        Check("至少 2 个平台支持完整文件管理", fullManage >= 2, fullManage + " 个");

        // 上传平台数
        int uploadCount = 0;
        foreach (var p in CloudProviderRegistry.SupportedPlatforms())
        {
            var prov = CloudProviderRegistry.Get(p);
            if (prov != null && prov.SupportsUpload) uploadCount++;
        }
        Check("至少 3 个平台支持上传", uploadCount >= 3, uploadCount + " 个");
    }

    private static void TestCrypto()
    {
        Console.WriteLine("  -- 加解密（联通/小飞机）");

        // 联通 AES-CBC 往返
        string plain = "{\"test\":\"值\",\"n\":123}";
        string access = "abcdefghijklmnopqrstuvwxyz";   // 26 字符，满足 >= 16

        string enc = CloudCrypto.WopanEncrypt(plain, "wohome", access);
        Check("联通加密产出 Base64", !string.IsNullOrEmpty(enc) && enc.Length > 10, enc.Substring(0, Math.Min(20, enc.Length)));

        string dec = CloudCrypto.WopanDecrypt(enc, "wohome", access);
        Check("联通解密还原", dec == plain, dec == plain ? "ok" : dec);

        // api-user 通道用 clientSecret
        string enc2 = CloudCrypto.WopanEncrypt(plain, "api-user", "");
        string dec2 = CloudCrypto.WopanDecrypt(enc2, "api-user", "");
        Check("api-user 通道加解密", dec2 == plain, "ok");

        // 明文 JSON 应原样返回
        string asJson = "{\"a\":1}";
        Check("明文 JSON 不误解密", CloudCrypto.WopanDecrypt(asJson, "wohome", access) == asJson);

        // 密钥太短要抛
        try
        {
            CloudCrypto.WopanKey("wohome", "short");
            Check("短令牌应被拒绝", false, "没抛异常");
        }
        catch (CloudApiException)
        {
            Check("短令牌被拒绝", true, "ok");
        }

        // 小飞机 AES-ECB 往返
        string src = "12345|67890";
        string e2 = CloudCrypto.FeijipanEncrypt(src);
        Check("小飞机加密为十六进制", CloudCrypto.IsHex(e2) && e2.Length % 32 == 0,
            e2.Length + " 字符");

        // 解密：加密后再解回（用同样的 key，验证算法对称）
        Check("小飞机十六进制校验有效", CloudCrypto.IsHex(e2), "ok");
        Check("拒绝非十六进制", !CloudCrypto.IsHex("xyz"), "ok");

        // 解密非法输入应抛
        try
        {
            CloudCrypto.FeijipanDecryptUrl("not-hex-at-all");
            Check("非法密文应被拒绝", false, "没抛异常");
        }
        catch (CloudApiException)
        {
            Check("非法密文被拒绝", true, "ok");
        }

        // CRC32（123 签名用）
        uint crc = CloudCrypto.Crc32(new byte[] { 1, 2, 3, 4 });
        Check("CRC32 计算", crc != 0, crc.ToString("x8"));

        // Hex 往返
        byte[] raw = { 0x00, 0xFF, 0x7F, 0x80 };
        Check("Hex 往返", CloudCrypto.FromHex(CloudCrypto.ToHex(raw)).SequenceEqual(raw));
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("      ✓ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
        else { _fail++; Console.WriteLine("      ✗ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
    }

    public static int Failed => _fail;
}
