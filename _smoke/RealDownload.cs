using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;
using GeZi.DownloadEngine;
using GeZi.Core.Support;

internal static class RealDownload
{
    /// 完整走一遍：解析 → 进子目录 → 取直链 → 真实分片下载（只下前一小段验证）
    public static async Task RunAsync(string shareUrl)
    {
        Console.WriteLine();
        Console.WriteLine("[10] 真实分享：进目录 + 实际下载");

        var info = ShareUrlParser.Parse(shareUrl);

        // 用已保存账号的 Cookie（取直链接口需要登录态！）
        string cookie = "";
        string accDir = Path.Combine(
            Environment.GetEnvironmentVariable("GEZI_CONFIG_DIR") ?? ".",
            "accounts");
        if (Directory.Exists(accDir))
        {
            var f = Directory.GetFiles(accDir, "*.json").FirstOrDefault();
            if (f != null)
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                string enc = doc.RootElement.GetProperty("Cookie").GetString();
                cookie = SecretProtector.Unprotect(enc);
                Console.WriteLine("    使用账号 Cookie: " + cookie.Length + " 字符");
            }
        }

        var client = new QuarkClient(cookie);
        string stoken = await client.GetStokenAsync(info.PwdId, info.Passcode ?? "");
        if (string.IsNullOrEmpty(stoken)) { Console.WriteLine("    ✗ 无 stoken"); return; }

        var root = await client.ShareDetailPageAsync(info.PwdId, stoken, "0", 1);
        var dir = root.Items.FirstOrDefault(e => e.IsDir);
        if (dir == null) { Console.WriteLine("    ✗ 根目录没有文件夹"); return; }
        Console.WriteLine("    进入文件夹: " + dir.Name);

        var sub = await client.ShareDetailPageAsync(info.PwdId, stoken, dir.Fid, 1);
        var file = sub.Items.FirstOrDefault(e => !e.IsDir);
        if (file == null) { Console.WriteLine("    ✗ 文件夹内没有文件"); return; }
        Console.WriteLine("    选中文件: " + file.Name + "  " + (file.Size / 1e6).ToString("F0") + " MB");

        var links = await client.GetShareDownloadAsync(
            new List<string> { file.Fid }, new List<string> { file.Token ?? "" },
            info.PwdId, stoken);
        var url = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
        if (string.IsNullOrEmpty(url)) { Console.WriteLine("    ✗ 取不到直链"); return; }
        Console.WriteLine("    直链: ✓ 长度 " + url.Length);

        // 只下前面 8MB 验证管线（不下整包 4GB）
        string outDir = Path.Combine(Path.GetTempPath(), "gezi-real");
        Directory.CreateDirectory(outDir);
        string dest = Path.Combine(outDir, PathUtil.SanitizeFileName(file.Name));
        try { if (File.Exists(dest)) File.Delete(dest); } catch { }

        // 沙箱内必须走代理才能出网；下载器默认强制直连（那是给夸克 CDN 的正确行为）
        NetworkOptions.UseSystemProxy = true;

        // 走融合引擎（与界面同一条路径）
        var job = new DownloadJob
        {
            Name = file.Name,
            Url = url,
            Dest = dest,
            Cookie = client.CookieStr,
            Threads = 8,
        };

        using (var scheduler = new DownloadScheduler(maxConcurrent: 1))
        {
            scheduler.Log = msg => Console.WriteLine("      [dl] " + msg);

            // ⚠️ 不能靠 CancellationToken 限时：下载器的语义是「取消即清理」，
            //    取消会把已下载的分片和目标文件一起删掉（那是给用户点「取消」用的正确行为）。
            //    所以这里改成「跑 20 秒 → 在取消之前观察磁盘」。
            using (var cts = new CancellationTokenSource())
            {
                var prog = new Progress<JobUpdate>(u => { });

                long observed = 0, observedParts = 0;
                var watch = Task.Run(async () =>
                {
                    for (int i = 0; i < 40; i++)   // 最多观察 20 秒
                    {
                        await Task.Delay(500);
                        try
                        {
                            if (File.Exists(dest)) observed = Math.Max(observed, new FileInfo(dest).Length);
                            string pd = dest + ".qparts";
                            if (Directory.Exists(pd))
                                observedParts = Math.Max(observedParts,
                                    Directory.GetFiles(pd, "*", SearchOption.AllDirectories)
                                             .Sum(f => new FileInfo(f).Length));
                        }
                        catch { }
                        if (observed + observedParts > 3_000_000) break;   // 下到 3MB 就够证明
                    }
                    cts.Cancel();      // 观察完再取消（此时数据已证明落盘）
                });

                // 观察 20 秒后取消：DownloadScheduler 走的是「取消即清理」语义，
                // 所以先看磁盘、再取消。
                bool ok = false;
                string msg = "(观察后主动取消)";
                try
                {
                    await scheduler.RunAllAsync(new[] { job }, prog, cts.Token);
                    ok = true;
                    msg = "";
                }
                catch (OperationCanceledException) { ok = false; }
                var res = new DownloadResult { Ok = ok, Message = msg };

                await watch;
                long got = observed;

                // 分片目录里的数据也是"下载成功"的证据（边合并模式下未合并的部分在这）
                long parts = observedParts;
                Console.WriteLine("    下载结果: Ok=" + res.Ok
                    + "  目标文件 " + (got / 1e6).ToString("F2") + " MB"
                    + "  分片数据 " + (parts / 1e6).ToString("F2") + " MB"
                    + "  合计 " + ((got + parts) / 1e6).ToString("F2") + " MB");
                Console.WriteLine("    " + (got + parts > 0 ? "✓ 下载管线工作正常（有数据落盘）" : "✗ 一个字节都没下来"));
                if (!res.Ok) Console.WriteLine("    失败原因: " + res.Message);
            }
        }
    }
}
