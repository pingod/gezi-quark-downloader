using System;
using System.Threading.Tasks;
using GeZi.Core.Api;

internal static class QrFetch
{
    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[6] 扫码登录：获取二维码 token");
        using (var svc = new LoginQrService())
        {
            var (ok, token, qrUrl, err) = await svc.GetQrCodeAsync();
            Console.WriteLine("    ok      = " + ok);
            Console.WriteLine("    token   = " + (string.IsNullOrEmpty(token) ? "(空)" : token.Substring(0, Math.Min(20, token.Length)) + "…"));
            Console.WriteLine("    qrUrl   = " + (string.IsNullOrEmpty(qrUrl) ? "(空)" : qrUrl.Substring(0, Math.Min(70, qrUrl.Length)) + "…"));
            if (!ok) Console.WriteLine("    error   = " + err);

            if (ok)
            {
                // 不扫码，只轮询 2 秒，确认状态机正常工作（应报 WaitingScan）
                using (var cts = new System.Threading.CancellationTokenSource(4000))
                {
                    var p = new Progress<QrPollResult>(r =>
                        Console.WriteLine("    轮询状态 = " + r.Stage + " (status=" + r.Status + ")"));
                    var res = await svc.PollForTicketWithStatusAsync(token, 3, p, cts.Token);
                    Console.WriteLine("    最终阶段 = " + res.Stage);
                }
            }
        }
    }
}
