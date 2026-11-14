// 多网盘架构测试：验证链接识别、平台能力、provider 分发
using System;
using System.Linq;
using GeZi.Providers.Cloud;
using GeZi.Providers.Cloud.Providers;

internal static class CloudTests
{
    private static int _fail;
    private static int _pass;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("[11] 多网盘架构");

        TestLinkParser();
        TestPlatformMatrix();
        TestRegistry();
        TestDownloadSpecSemantics();
    }

    private static void TestLinkParser()
    {
        Console.WriteLine("  -- 链接识别");

        // 各平台真实形态的链接
        var cases = new (string url, CloudPlatform expected, string shareId, string name)[]
        {
            ("https://pan.quark.cn/s/ce68dabd38c6", CloudPlatform.Quark, "ce68dabd38c6", "夸克"),
            ("https://pan.quark.cn/s/abc123#/list/share", CloudPlatform.Quark, "abc123", "夸克带锚点"),
            ("https://drive.uc.cn/s/xyz789", CloudPlatform.Uc, "xyz789", "UC"),
            ("https://www.aliyundrive.com/s/AbCdEf12345", CloudPlatform.Aliyun, "AbCdEf12345", "阿里"),
            ("https://www.123pan.com/s/abcd-efgh", CloudPlatform.Pan123, "abcd-efgh", "123"),
            ("https://cloud.189.cn/t/AbCdEf", CloudPlatform.Tianyi, "AbCdEf", "天翼"),
            ("https://pan.baidu.com/s/1AbCdEfGh", CloudPlatform.Baidu, "1AbCdEfGh", "百度"),
            ("https://pan.xunlei.com/s/xyz", CloudPlatform.Xunlei, "xyz", "迅雷"),
            ("https://115.com/s/abc123", CloudPlatform.Pan115, "abc123", "115"),
            ("https://wwi.lanzoup.com/iABCDEF", CloudPlatform.Lanzou, "ABCDEF", "蓝奏"),
        };

        foreach (var c in cases)
        {
            var p = LinkParser.Parse(c.url);
            bool ok = p.Platform == c.expected
                      && p.Kind == LinkKind.CloudShare
                      && p.ShareId == c.shareId;
            Check(c.name + " → " + c.expected?.ShortName, ok,
                $"{p.Platform?.ShortName ?? "null"} / {p.ShareId ?? "null"} / {p.Kind}");
        }

        // 提取码
        var withPwd = LinkParser.Parse("我用夸克网盘分享了「文件」，链接：https://pan.quark.cn/s/abc123 提取码：k9m2");
        Check("整段文案取提取码", withPwd.Passcode == "k9m2", withPwd.Passcode);
        Check("整段文案取分享ID", withPwd.ShareId == "abc123", withPwd.ShareId);

        var urlPwd = LinkParser.Parse("https://pan.quark.cn/s/abc?pwd=1234");
        Check("URL 里的 pwd", urlPwd.Passcode == "1234", urlPwd.Passcode);

        // 分享ID 本身叫 pwd1234 时不能被误当提取码
        var tricky = LinkParser.Parse("https://pan.quark.cn/s/pwd1234");
        Check("分享ID 含 pwd 不误判", tricky.ShareId == "pwd1234", tricky.ShareId);

        // 非网盘链接 → 直链
        var direct = LinkParser.Parse("https://example.com/file.zip");
        Check("普通链接识别为直链", direct.Kind == LinkKind.Direct, direct.Kind.ToString());

        // 磁力
        var magnet = LinkParser.Parse("magnet:?xt=urn:btih:abcdef123456");
        Check("磁力链", magnet.Kind == LinkKind.Magnet, magnet.Kind.ToString());

        // 认得域名但认不出分享ID
        var weird = LinkParser.Parse("https://pan.quark.cn/about");
        Check("认得域名但无分享ID", weird.Platform == CloudPlatform.Quark
            && weird.Kind == LinkKind.UnsupportedCloud, weird.Kind.ToString());
    }

    private static void TestPlatformMatrix()
    {
        Console.WriteLine("  -- 平台能力矩阵");

        // 关键事实核对（来自 wenxi 的 CloudPlatform 定义）
        Check("夸克分享不需要登录", !CloudPlatform.Quark.ShareRequiresAccount);
        Check("UC分享不需要登录", !CloudPlatform.Uc.ShareRequiresAccount);
        Check("阿里分享不需要登录", !CloudPlatform.Aliyun.ShareRequiresAccount);
        Check("百度分享不需要登录", !CloudPlatform.Baidu.ShareRequiresAccount);
        Check("天翼分享需要登录", CloudPlatform.Tianyi.ShareRequiresAccount);
        Check("移动分享需要登录", CloudPlatform.C139.ShareRequiresAccount);
        // ⚠️ 联通(wopan)在 wenxi 的排除集合里 → 分享不需要登录。
        //    我第一版断言写反了，被测试抓出来。源码：models.dart:69-81
        Check("联通分享不需要登录", !CloudPlatform.Wopan.ShareRequiresAccount);

        Check("家庭云仅天翼/移动/联通",
            CloudPlatform.Tianyi.SupportsFamilyCloud
            && CloudPlatform.C139.SupportsFamilyCloud
            && CloudPlatform.Wopan.SupportsFamilyCloud
            && !CloudPlatform.Quark.SupportsFamilyCloud);

        Check("存储空间仅阿里/城通",
            CloudPlatform.Aliyun.SupportsPersonalSpaces
            && CloudPlatform.Ctfile.SupportsPersonalSpaces
            && !CloudPlatform.Quark.SupportsPersonalSpaces);

        // 域名识别（后缀匹配）
        Check("子域名识别 quark",
            CloudPlatform.FromHost("drive-pc.quark.cn") == CloudPlatform.Quark);
        Check("www 前缀识别",
            CloudPlatform.FromHost("www.aliyundrive.com") == CloudPlatform.Aliyun);
        Check("123 号段域名",
            CloudPlatform.FromHost("123865.com") == CloudPlatform.Pan123);
        Check("蓝奏变体域名",
            CloudPlatform.FromHost("wwi.lanzoup.com") == CloudPlatform.Lanzou);
        Check("未知域名返回 null",
            CloudPlatform.FromHost("example.com") == null);
    }

    private static void TestRegistry()
    {
        Console.WriteLine("  -- provider 注册表");

        var quark = CloudProviderRegistry.Get(CloudPlatform.Quark);
        Check("夸克 provider 可取", quark != null, quark?.GetType().Name);

        var uc = CloudProviderRegistry.Get(CloudPlatform.Uc);
        Check("UC provider 可取", uc != null, uc?.GetType().Name);

        var ali = CloudProviderRegistry.Get(CloudPlatform.Aliyun);
        Check("阿里 provider 可取", ali != null, ali?.GetType().Name);

        var p123 = CloudProviderRegistry.Get(CloudPlatform.Pan123);
        Check("123 provider 可取", p123 != null, p123?.GetType().Name);

        var tianyi = CloudProviderRegistry.Get(CloudPlatform.Tianyi);
        Check("天翼 provider 可取", tianyi != null, tianyi?.GetType().Name);

        // 全部已实现平台
        var all = new (CloudPlatform p, string name)[]
        {
            (CloudPlatform.Quark, "夸克"), (CloudPlatform.Uc, "UC"),
            (CloudPlatform.Aliyun, "阿里"), (CloudPlatform.Pan115, "115"),
            (CloudPlatform.Baidu, "百度"), (CloudPlatform.Pan123, "123"),
            (CloudPlatform.Tianyi, "天翼"), (CloudPlatform.Xunlei, "迅雷"),
            (CloudPlatform.PikPak, "PikPak"), (CloudPlatform.Lanzou, "蓝奏"),
            (CloudPlatform.Ctfile, "城通"), (CloudPlatform.Github, "GitHub"),
        };

        int ok = 0;
        foreach (var (p, name) in all)
        {
            var prov = CloudProviderRegistry.Get(p);
            if (prov != null && prov.Platform == p) ok++;
            else Check(name + " provider 可用", false, prov?.GetType().Name ?? "null");
        }
        Check("全部 12 个平台可实例化", ok == all.Length, ok + "/" + all.Length);

        // 微云：已注册但功能未适配（调用时抛带原因的异常，而不是假装能用）
        var weiyun = CloudProviderRegistry.Get(CloudPlatform.Weiyun);
        Check("微云已注册（功能未适配）", weiyun != null, weiyun?.GetType().Name ?? "null");

        // 真正的"未实现平台"：检查一个没登记的平台返回 null
        var unregistered = CloudPlatform.FromKey("nonexistent_platform");
        Check("未登记的 key 返回 null", unregistered == null, "ok");

        Check("SupportedPlatforms 数量正确",
            CloudProviderRegistry.SupportedPlatforms().Count == 17,
            CloudProviderRegistry.SupportedPlatforms().Count.ToString());

        // UC 复用夸克逻辑：应该是不同的类，但能力一致
        Check("UC 与夸克是不同实现", uc.GetType() != quark.GetType(),
            uc.GetType().Name + " vs " + quark.GetType().Name);
        Check("UC 上报正确平台", uc.Platform == CloudPlatform.Uc, uc.Platform.ShortName);
    }

    private static void TestDownloadSpecSemantics()
    {
        Console.WriteLine("  -- DownloadSpec 语义");

        // 直接可下载
        var direct = new DownloadSpec { Url = "https://x/y", FileName = "a.zip" };
        Check("有 URL 时无需二次取链", !direct.NeedsPreparation, "NeedsPreparation=false");

        // 需要转存/二次取链
        var needPrep = new DownloadSpec { Url = "", FileName = "a.zip" };
        needPrep.Source["fid"] = "123";
        Check("URL 空但有 Source → 需二次取链", needPrep.NeedsPreparation, "NeedsPreparation=true");

        // 空规格
        var empty = new DownloadSpec();
        Check("完全空规格不需准备", !empty.NeedsPreparation, "NeedsPreparation=false");

        // 清理信息
        var cleanup = new DownloadCleanup { PlatformKey = "quark", AccountId = "u1" };
        cleanup.DirectoryIds.Add("dir1");
        Check("Cleanup 有工作项", cleanup.HasWork, cleanup.DirectoryIds.Count + " 个目录");

        var noWork = new DownloadCleanup();
        Check("空 Cleanup 无工作项", !noWork.HasWork, "ok");

        // Clone 独立性
        var cloned = direct.Clone();
        cloned.Headers["X-Test"] = "1";
        Check("Clone 不污染原对象", !direct.Headers.ContainsKey("X-Test"), "ok");
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("      ✓ " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]")); }
        else { _fail++; Console.WriteLine("      ✗ " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]")); }
    }

    public static int Failed => _fail;
}
