using System.Linq;
using System;
using GeZi.Core.Api;

internal static class ErrorRoutingTests
{
    private static int _fail, _pass;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("[12] 分享取链错误码分流");

        // 验证常量值与 wenxi 的实测规则一致
        Check("14001 = 凭证过期", QuarkErrorCode.ShareDownloadCredentialExpired == 14001,
            QuarkErrorCode.ShareDownloadCredentialExpired.ToString());
        Check("41020 = 凭证过期(alt)", QuarkErrorCode.ShareDownloadCredentialExpiredAlt == 41020,
            QuarkErrorCode.ShareDownloadCredentialExpiredAlt.ToString());
        Check("23018 = 需登录", QuarkErrorCode.ShareDownloadNeedLogin == 23018,
            QuarkErrorCode.ShareDownloadNeedLogin.ToString());
        Check("31001 = 未登录", QuarkErrorCode.NotLoggedIn == 31001,
            QuarkErrorCode.NotLoggedIn.ToString());

        // 这几个码必须**互不相同**，否则分流会串
        var codes = new[] {
            QuarkErrorCode.ShareDownloadCredentialExpired,
            QuarkErrorCode.ShareDownloadCredentialExpiredAlt,
            QuarkErrorCode.ShareDownloadNeedLogin,
            QuarkErrorCode.NotLoggedIn,
        };
        Check("四个码互不相同", codes.Distinct().Count() == codes.Length, "4 distinct");

        // 凭证过期异常必须能被单独捕获（上层据此决定"重试"而非"失败"）
        try
        {
            throw new QuarkShareCredentialExpiredException("test");
        }
        catch (QuarkShareCredentialExpiredException)
        {
            Check("凭证过期异常可精确捕获", true, "caught");
        }
        catch
        {
            Check("凭证过期异常可精确捕获", false, "wrong catch");
        }

        // 但也要能被基类捕获（兼容既有 catch QuarkApiException 的地方）
        try
        {
            throw new QuarkShareCredentialExpiredException("test");
        }
        catch (QuarkApiException)
        {
            Check("仍是 QuarkApiException 子类", true, "compatible");
        }

        Console.WriteLine();
        Console.WriteLine($"  通过 {_pass}，失败 {_fail}");
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("    ✓ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
        else { _fail++; Console.WriteLine("    ✗ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
    }

    public static int Failed => _fail;
}
