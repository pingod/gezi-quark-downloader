using System;
using GeZi.Core.Api;
using GeZi.Core.Support;

internal static class QrProbe
{
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("[5] OpenSSL 通道（扫码登录的前提）");
        Console.WriteLine("    IsAvailable = " + OpenSslTls.IsAvailable);
        Console.WriteLine("    Version     = " + OpenSslTls.VersionString);
        if (!OpenSslTls.IsAvailable)
        {
            Console.WriteLine("    ProbeError  = " + OpenSslTls.ProbeError);
            return;
        }
        Console.WriteLine("    Diagnostics = " + PanAccountBridge.Diagnostics);

        // 真正打一次 pan.quark.cn，验证 TLS 指纹通道能过
        try
        {
            var r = OpenSslTls.Get("pan.quark.cn", "/account/info?fr=pc", new System.Collections.Generic.Dictionary<string,string>
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36" },
                { "Accept", "application/json" },
            }, 15000);
            Console.WriteLine("    HTTP 状态   = " + r.StatusCode);
            Console.WriteLine("    响应长度    = " + (r.Body?.Length ?? 0));
        }
        catch (Exception ex)
        {
            Console.WriteLine("    请求失败    = " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
