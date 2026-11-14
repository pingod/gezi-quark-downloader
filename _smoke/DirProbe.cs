using System;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;

internal static class DirProbe
{
    public static async Task RunAsync(string shareUrl)
    {
        Console.WriteLine();
        Console.WriteLine("[9] 进入子目录查看内容");
        var info = GeZi.Providers.Shared.ShareUrlParser.Parse(shareUrl);
        var client = new QuarkClient("");
        string stoken = await client.GetStokenAsync(info.PwdId, info.Passcode ?? "");
        var root = await client.ShareDetailPageAsync(info.PwdId, stoken, "0", 1);

        foreach (var e in root.Items)
        {
            Console.WriteLine("  根: " + e.Name + "  dir=" + e.IsDir);
            if (!e.IsDir) continue;

            var sub = await client.ShareDetailPageAsync(info.PwdId, stoken, e.Fid, 1);
            Console.WriteLine("    └─ " + e.Name + " 内含 " + sub.Items.Count + " 项:");
            foreach (var c in sub.Items)
                Console.WriteLine("         " + (c.IsDir ? "[目录] " : "[文件] ") + c.Name
                    + (c.IsDir ? "" : "  " + (c.Size / 1e6).ToString("F1") + " MB")
                    + "  token=" + (string.IsNullOrEmpty(c.Token) ? "✗空" : "✓"));
        }
    }
}
