// 冒烟测试：验证移植后的 Core 在 Linux 上真的能干活。
// 覆盖 4 件事：
//   1) 分享链接解析（纯逻辑）
//   2) Cookie 加密往返（net8 的 AES-GCM 分支）
//   3) JSON 兼容层形状与 JavaScriptSerializer 一致（net8 的 System.Text.Json 分支）
//   4) 多线程分片下载真的能下到文件（用公开的大文件做真实验证）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;
using GeZi.Core.Support;

internal static class Program
{
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine("=== GeZi.Core 移植冒烟测试 (net8.0 / Linux) ===");
        Console.WriteLine();

        TestShareUrlParser();
        TestSecretProtector();
        TestJsonCompat();
        await TestDownloadAsync();
        QrProbe.Run();
        await QrFetch.RunAsync();
        await LoginProbe.RunAsync();
        string share = Environment.GetEnvironmentVariable("GEZI_TEST_SHARE");
        if (!string.IsNullOrEmpty(share))
        {
            await ShareProbe.RunAsync(share);
            await DirProbe.RunAsync(share);
            await RealDownload.RunAsync(share);
        }

        CloudTests.Run();
        _failed += CloudTests.Failed;
        ErrorRoutingTests.Run();
        _failed += ErrorRoutingTests.Failed;
        await TransferTests.RunAsync();
        _failed += TransferTests.Failed;
        PlatformTests.Run();
        _failed += PlatformTests.Failed;
        await UploadTests.RunAsync();
        _failed += UploadTests.Failed;

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "全部通过 ✅" : $"{_failed} 项失败 ❌");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- 1) 分享链接解析 ----------
    private static void TestShareUrlParser()
    {
        Console.WriteLine("[1] 分享链接解析");

        var a = ShareUrlParser.Parse("https://pan.quark.cn/s/abc123def456");
        Check("识别分享 ID", a.PwdId == "abc123def456", a.PwdId);

        var b = ShareUrlParser.Parse("我用夸克网盘分享了「文件」，链接：https://pan.quark.cn/s/xyz789 提取码：k9m2");
        Check("整段文案取 ID", b.PwdId == "xyz789", b.PwdId);
        Check("整段文案取提取码", b.Passcode == "k9m2", b.Passcode);

        var c = ShareUrlParser.Parse("https://pan.quark.cn/s/abc?pwd=1234");
        Check("URL 里的 pwd 参数", c.Passcode == "1234", c.Passcode);

        var d = ShareUrlParser.Parse("/~469d3M9zTu~:/");
        Check("识别口令并标记", d.IsKouling, d.Kouling);
    }

    // ---------- 2) 加密往返 ----------
    private static void TestSecretProtector()
    {
        Console.WriteLine();
        Console.WriteLine("[2] Cookie 加密（net8 = AES-256-GCM）");

        string plain = "__puus=abc123%3D%3D; __pus=xyz";
        string enc = SecretProtector.Protect(plain);

        Check("加密后带前缀", enc.StartsWith("enc:v1:", StringComparison.Ordinal), enc.Length > 20 ? "(密文)" : enc);
        Check("密文不含明文", !enc.Contains("abc123"), "ok");

        string dec = SecretProtector.Unprotect(enc);
        Check("解密还原", dec == plain, dec);

        // 旧版明文要能原样读出
        Check("无前缀按明文处理", SecretProtector.Unprotect("old-plain-cookie") == "old-plain-cookie", "ok");

        // 二次加密应幂等
        Check("重复加密幂等", SecretProtector.Protect(enc) == enc, "ok");

        // 损坏的密文不能抛异常，返回空串
        Check("损坏密文安全降级", SecretProtector.Unprotect("enc:v1:bm90LXZhbGlk") == "", "ok");
    }

    // ---------- 3) JSON 形状 ----------
    private static void TestJsonCompat()
    {
        Console.WriteLine();
        Console.WriteLine("[3] JSON 兼容层（net8 = System.Text.Json）");

        string json = "{\"status\":200,\"code\":0,\"data\":{\"list\":[{\"fid\":\"f1\",\"size\":12345678901,\"dir\":false}],\"total\":1}}";
        var root = JsonCompat.Deserialize(json) as Dictionary<string, object>;

        Check("顶层是 Dictionary", root != null, root?.Count.ToString());

        // 关键：数值类型要能被 AsLong 的现有分支吃下（int / long / decimal）
        object code = root?["code"];
        Check("整数映射为 int", code is int, code?.GetType().Name);

        var data = root?["data"] as Dictionary<string, object>;
        var list = data?["list"] as object[];
        Check("数组映射为 object[]", list != null && list.Length == 1, list?.Length.ToString());

        var item = list?[0] as Dictionary<string, object>;
        object size = item?["size"];
        Check("大整数映射为 long", size is long, size?.GetType().Name);
        Check("大整数值正确", size is long l && l == 12345678901L, size?.ToString());

        Check("字符串取值", item?["fid"] as string == "f1", item?["fid"]?.ToString());

        // 序列化（上传请求体用）要能产出合法 JSON
        string outJson = JsonCompat.Serialize(new Dictionary<string, object>
        {
            { "fids", new List<string> { "f1", "f2" } }
        });
        Check("序列化可用", outJson.Contains("\"fids\"") && outJson.Contains("f2"), outJson);
    }

    // ---------- 4) 真实分片下载 ----------
    private static async Task TestDownloadAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[4] 多线程分片下载（真实网络）");

        // ⚠️ 本机（开发沙箱）出网必须经 HTTP 代理，而下载器默认强制直连
        //    （那是为了夸克 CDN 的正确行为）。这里显式打开系统代理开关，
        //    否则会得到 "Connection refused" —— 那是环境限制，不是下载引擎的问题。
        GeZi.Core.Support.NetworkOptions.UseSystemProxy = true;

        // 用 GitHub 上的一个稳定小文件，验证整条下载管线
        const string url = "https://raw.githubusercontent.com/pingod/wstream/main/README.md";
        string dir = Path.Combine(Path.GetTempPath(), "gezi-smoke");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, "README.md");
        try { if (File.Exists(dest)) File.Delete(dest); } catch { }

        using (var dl = new SegmentedDownloader(threads: 4, cookie: null, partsRoot: null))
        {
            dl.Log = msg => { };   // 静默

            long lastDone = 0;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p != null) lastDone = p.Done;
            });

            DownloadResult res;
            try
            {
                res = await dl.DownloadAsync(url, dest, progress, CancellationToken.None, null);
            }
            catch (Exception ex)
            {
                Check("下载不抛异常", false, ex.GetType().Name + ": " + ex.Message);
                return;
            }

            long size = 0;
            try { size = new FileInfo(dest).Length; } catch { }

            Check("下载成功", res.Ok, res.Message);
            Check("文件已落盘且非空", size > 0, size + " 字节");
            Check("进度有上报", lastDone > 0, lastDone + " 字节");

            // 内容抽查：README 里必然有项目名
            string text = "";
            try { text = File.ReadAllText(dest); } catch { }
            Check("内容像样", text.Contains("wstream") || text.Length > 100, text.Length + " 字符");
        }
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
            Console.WriteLine("    ✅ " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
        else
        {
            Console.WriteLine("    ❌ " + name + "  [" + detail + "]");
            _failed++;
        }
    }
}
