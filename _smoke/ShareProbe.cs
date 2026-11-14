using System;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;

internal static class ShareProbe
{
    /// 用真实分享链接跑一遍完整链路：解析 → 取直链（含重解析兜底）
    public static async Task RunAsync(string shareUrl)
    {
        Console.WriteLine();
        Console.WriteLine("[7] 真实分享链路");

        var info = GeZi.Providers.Shared.ShareUrlParser.Parse(shareUrl);
        Console.WriteLine("    pwd_id   = " + (info.PwdId ?? "(未识别)"));
        Console.WriteLine("    passcode = " + (string.IsNullOrEmpty(info.Passcode) ? "(无)" : info.Passcode));
        if (string.IsNullOrEmpty(info.PwdId)) { Console.WriteLine("    ✗ 链接无法解析"); return; }

        var client = new QuarkClient("");
        try
        {
            string stoken = await client.GetStokenAsync(info.PwdId, info.Passcode ?? "");
            Console.WriteLine("    stoken   = " + (string.IsNullOrEmpty(stoken) ? "(失败)" : stoken.Substring(0, Math.Min(16, stoken.Length)) + "…"));
            if (string.IsNullOrEmpty(stoken)) { Console.WriteLine("    ✗ 拿不到分享凭证"); return; }

            var page = await client.ShareDetailPageAsync(info.PwdId, stoken, "0", 1);
            Console.WriteLine("    条目数   = " + (page?.Items?.Count ?? 0));
            if (page?.Items == null || page.Items.Count == 0) { Console.WriteLine("    ✗ 列表为空"); return; }

            var f = page.Items[0];
            Console.WriteLine("    首个条目 = " + f.Name + " / dir=" + f.IsDir + " / size=" + f.Size);
            if (f.IsDir) { Console.WriteLine("    (是目录，跳过取链)"); return; }

            var links = await client.GetShareDownloadAsync(
                new System.Collections.Generic.List<string> { f.Fid },
                new System.Collections.Generic.List<string> { f.Token ?? "" },
                info.PwdId, stoken);

            var hit = links?.Count > 0 ? links[0].DownloadUrl : null;
            Console.WriteLine("    直链     = " + (string.IsNullOrEmpty(hit) ? "✗ 取不到" : "✓ " + hit.Substring(0, Math.Min(80, hit.Length)) + "…"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("    ✗ 异常: " + ex.GetType().Name + " " + ex.Message);
        }
    }
}
