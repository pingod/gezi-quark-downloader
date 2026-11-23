using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace GeZi.DownloadEngine.Framework.Extensions.Helpers;

internal static class ExceptionHelper
{
    internal static bool IsMomentumError(this Exception error)
    {
        if (error.HasSource("System.Net.Http",
            "System.Net.Sockets",
            "System.Net.Security"))
            return true;

        if (error.HasTypeOf(typeof(WebException), typeof(SocketException)))
            return true;

        // 🚨🚨 2026-11-14 修：把**传输层 IOException** 也算作「可重试的网络错误」。
        //
        // 原来这里只认 Source 是 System.Net.* 或类型是 WebException/SocketException，
        // 漏掉了 .NET Framework 最常见的一种：ConnectStream 读响应体时抛
        //     IOException("无法从传输连接中读取数据: 连接已关闭。")
        // 它的 Source = "System"、InnerException 为空 ⇒ 上面两个判据都不成立
        // ⇒ ChunkDownloader 走到兜底 catch，LogCritical 后直接 throw
        // ⇒ 整个任务被判「失败」，而且续传点丢失（用户实测 issue #18）。
        //
        // 现在：只要是 IOException 且消息/内层看着像传输层断开，就按可重试处理。
        // 真正的磁盘错误（磁盘已满、文件被占用、拒绝访问）不会命中 IsTransportIo 的判据，
        // 仍然走「不可重试」的兜底 —— 两边都要对。
        if (error is IOException && IsTransportIo(error))
            return true;

        return false;
    }

    /// <summary>
    /// 判断一个 <see cref="IOException"/> 是不是「网络传输层断开」而不是「磁盘坏了」。
    /// <para>
    /// 只认传输层特征，绝不因为 Message 里出现「写」就判是磁盘 —— .NET 的
    /// "无法从传输连接中读取数据: 连接已关闭。" 恰好同时含「连接」和「传输」，很容易误伤。
    /// </para>
    /// <para>
    /// 🚨 2026-11-15：判据表**只有一份**，在 <c>GeZi.Core.Support.RetryPolicy</c> 里。
    /// 原来这里自己抄了一份，两边已经漂移（Core 那份多了 "unexpectedly"、这份没有，
    /// 中文「从传输流收到意外的 EOF」两边都认不出来）⇒ 同一个异常在引擎层和融合层
    /// 得到两种归因。现在直接转发，避免再漂移。
    /// </para>
    /// </summary>
    internal static bool IsTransportIo(this Exception error) =>
        GeZi.Core.Support.RetryPolicy.IsTransportIo(error);

    internal static bool HasTypeOf(this Exception exp, params Type[] types)
    {
        Exception innerException = exp;
        while (innerException != null)
        {
            foreach (Type type in types)
            {
                if (innerException.GetType() == type)
                    return true;
            }

            innerException = innerException.InnerException;
        }

        return false;
    }

    internal static bool HasSource(this Exception exp, params string[] sources)
    {
        Exception innerException = exp;
        while (innerException != null)
        {
            foreach (string source in sources)
            {
                if (string.Equals(innerException.Source, source, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            innerException = innerException.InnerException;
        }

        return false;
    }
}
