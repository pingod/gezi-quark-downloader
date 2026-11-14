// 性能回归探针：本地 Range 服务器 + 融合引擎，量出「进度上报次数 / 日志条数」。
//
// 为什么需要它：这两条都是**数量级**问题（每秒上千次 UI Post、每秒上百条日志），
// 靠肉眼看界面只能得出"卡"，无法判断改完到底好了多少。
// 用一个本地回环服务器跑固定大小的文件，把两个计数打出来，
// 修复前后直接对比即可 —— 不需要真实网盘、不需要网络。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.DownloadEngine;
using GeZi.DownloadEngine.Storage;

internal static class PerfProbe
{
    private const int FileSize = 48 * 1024 * 1024;   // 48MB
    private const int Threads = 64;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[perf] 进度/日志上报量（本地 Range 服务器，48MB × 64 线程）");

        string dir = Path.Combine(Path.GetTempPath(), "gezi-perf");
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "source.bin");
        string dest = Path.Combine(dir, "dest.bin");

        var rnd = new Random(12345);
        var buf = new byte[FileSize];
        rnd.NextBytes(buf);
        File.WriteAllBytes(src, buf);
        try { if (File.Exists(dest)) File.Delete(dest); } catch { }

        int port = FindFreePort();
        using var server = new RangeServer(src, port);
        server.Start();

        long progressCount = 0;
        long logCount = 0;
        long logBytes = 0;
        var logKinds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var lastProgress = new object();

        var job = new DownloadJob
        {
            Name = "source.bin",
            Url = "http://127.0.0.1:" + port + "/source.bin",
            Dest = dest,
            ExpectedSize = FileSize,
        };

        object serviceConfig = null;
        var sw = Stopwatch.StartNew();
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            Interlocked.Increment(ref progressCount);
            lock (lastProgress) { }
        });

        var res = await FusionDownloader.DownloadAsync(
            job, Threads, progress, CancellationToken.None, new GeZi.DownloadEngine.PauseToken(),
            StorageMode.Sequential, 0, null, null, null,
            log: msg =>
            {
                Interlocked.Increment(ref logCount);
                Interlocked.Add(ref logBytes, msg.Length);
                // 归一成模板（抹掉数字）后计数，用来确认噪声源真的消失了
                string kind = System.Text.RegularExpressions.Regex.Replace(msg, "\\d+", "N");
                if (kind.Length > 60) kind = kind.Substring(0, 60);
                logKinds.AddOrUpdate(kind, 1, (k, v) => v + 1);
            },
            onServiceReady: svc => serviceConfig = svc.GetType()
                .GetProperty("Options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .GetValue(svc)).ConfigureAwait(false);
        sw.Stop();

        bool same = FilesEqual(src, dest);

        // 端到端：整个下载跑完后，服务用的配置对象上不该残留任何 ChunkDownloader 订阅。
        // 这是「DownloadService 是否真的 Dispose 了每个分片」的唯一可靠检查 ——
        // 只测 Dispose 方法本身会漏掉「创建方忘了调」。
        int subscribers = CountConfigSubscribers(serviceConfig);
        Console.WriteLine("    结果：" + (res.Ok ? "成功" : "失败 " + res.Message));
        Console.WriteLine("    耗时：" + sw.ElapsedMilliseconds + " ms");
        Console.WriteLine("    进度回调次数：" + progressCount
            + "（" + (progressCount * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds)).ToString("F0") + " 次/秒）");
        Console.WriteLine("    日志条数：" + logCount
            + "（" + (logCount * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds)).ToString("F0") + " 条/秒，"
            + (logBytes / 1024.0).ToString("F1") + " KB）");
        Console.WriteLine("    内容一致：" + (same ? "是" : "否"));
        Console.WriteLine("    残留配置订阅者：" + subscribers + " 个（期望 0）");
        Console.WriteLine("    日志分布（前 6）：");
        foreach (var kv in System.Linq.Enumerable.Take(
            System.Linq.Enumerable.OrderByDescending(logKinds, x => x.Value), 6))
            Console.WriteLine("      " + kv.Value.ToString().PadLeft(6) + "  " + kv.Key);

        // 断言（数量级门槛，不是精确值）：
        //   · 进度 10Hz 节流后，48MB / 数秒的下载不该超过 200 次
        //   · 调试级日志不再无条件外发，总量应该很低（每分片几条，不是每块几条）
        bool ok = res.Ok && same && progressCount <= 400 && logCount <= 2000 && subscribers == 0;
        Console.WriteLine(ok ? "    ✓ 通过" : "    ✗ 超出预期");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 确定性验证：ChunkDownloader 退订配置事件。
    ///
    /// 不依赖「监控线程恰好触发配置变更」这种时序 —— 直接用反射构造
    /// <c>ChunkDownloader</c>，触发一次配置变更，数日志回调被调了几次。
    /// 订阅泄漏的本质就是「对象用完了还挂在配置对象的调用列表里」，
    /// 所以这里量的正是那个列表：Dispose 之后它必须不再增长。
    /// </summary>
    public static int RunSubscriptionAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[perf] ChunkDownloader 订阅/退订（反射直测）");

        var asm = typeof(FusionDownloader).Assembly;
        Type cdType = asm.GetType("GeZi.DownloadEngine.Framework.ChunkDownloader");
        Type chunkType = asm.GetType("GeZi.DownloadEngine.Framework.Chunk");
        Type cfgType = asm.GetType("GeZi.DownloadEngine.Framework.DownloadConfiguration");
        Type loggerType = asm.GetType("GeZi.DownloadEngine.Framework.Extensions.Logging.ILogger");
        if (cdType == null || chunkType == null || loggerType == null)
        {
            Console.WriteLine("    ✗ 找不到内部类型（引擎结构变了？）");
            return 1;
        }

        object config = Activator.CreateInstance(cfgType);
        int logged = 0;

        // 用一个最小 ILogger 实现记录日志（ActionLogger 在 GeZi.DownloadEngine 命名空间）
        var logger = new ActionLogger(msg => Interlocked.Increment(ref logged));

        object chunk = Activator.CreateInstance(chunkType, 0L, 1024L);
        var ctor = cdType.GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)[0];
        var ps = ctor.GetParameters();
        object[] args = new object[ps.Length];
        args[0] = chunk;
        for (int i = 1; i < ps.Length; i++)
        {
            if (ps[i].ParameterType == cfgType) args[i] = config;
            else if (ps[i].ParameterType == loggerType) args[i] = logger;
            else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }

        var downloader = ctor.Invoke(args);
        // 修复前 ChunkDownloader 不实现 IDisposable —— 那就没有退订的途径。
        // 这不是测试写法的细节，而是问题本身：**没有 Dispose 就没有退订点**。
        var disposable = downloader as IDisposable;
        if (disposable == null)
        {
            Console.WriteLine("    ✗ ChunkDownloader 未实现 IDisposable → 配置事件订阅无法退订");
            return 1;
        }

        // 多个 downloader 共享同一个配置对象 —— 这正是真实场景：
        // DownloadService 的配置对象被**所有**分片共享，每个分片一个 ChunkDownloader。
        var all = new List<IDisposable>();
        for (int i = 1; i < 8; i++)
        {
            object c2 = Activator.CreateInstance(chunkType, (long)(i * 1024), (long)((i + 1) * 1024));
            object[] a2 = (object[])args.Clone();
            a2[0] = c2;
            all.Add((IDisposable)ctor.Invoke(a2));
        }
        int afterCtor = logged;

        var pMax = cfgType.GetProperty("MaximumBytesPerSecond");
        var pNoise = cfgType.GetProperty("DynamicSlowThresholdBps");

        // 触发一次「我们关心的那个」属性变更 → 应该记一条
        pMax.SetValue(config, 1_000_000L);
        int afterFirst = logged;

        // 触发一个「我们不关心的」属性 → 不该记（这是噪声源）
        pNoise.SetValue(config, 12345L);
        int afterNoise = logged;

        // 退订后再触发 → 不该再记
        disposable?.Dispose();
        foreach (var d in all) d.Dispose();
        pMax.SetValue(config, 2_000_000L);
        int afterDispose = logged;

        Console.WriteLine("    构造 8 个 downloader 后日志：" + afterCtor + " 条（期望 0）");
        Console.WriteLine("    关心的属性变更后日志：" + afterFirst + " 条（期望 8：8 个活跃订阅者各一条）");
        Console.WriteLine("    噪声属性变更后新增：" + (afterNoise - afterFirst) + " 条（期望 0）");
        Console.WriteLine("    Dispose 后新增：" + (afterDispose - afterNoise) + " 条（期望 0）");

        bool ok = afterCtor == 0 && afterFirst == 8 && afterNoise == afterFirst && afterDispose == afterNoise;
        Console.WriteLine(ok ? "    ✓ 通过" : "    ✗ 未通过");
        return ok ? 0 : 1;
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _f;
        public SyncProgress(Action<T> f) { _f = f; }
        public void Report(T value) => _f(value);
    }

    /// <summary>数一个对象上某个事件挂了多少个委托（PropertyChanged 的 invocation list）。</summary>
    private static int CountConfigSubscribers(object config)
    {
        if (config == null) return -1;
        var evt = config.GetType().GetEvent("PropertyChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        var field = evt?.DeclaringType.GetField(evt.Name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null)
        {
            // 自动实现的事件：委托存在同名私有字段里，名字不一定等于事件名，兜底扫一遍
            foreach (var f in evt.DeclaringType.GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            {
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) { field = f; break; }
            }
        }
        if (field == null) return -1;
        var d = field.GetValue(config) as Delegate;
        return d == null ? 0 : d.GetInvocationList().Length;
    }

    private static bool FilesEqual(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        while (true)
        {
            int na = fa.Read(ba, 0, ba.Length);
            int nb = fb.Read(bb, 0, bb.Length);
            if (na != nb) return false;
            if (na == 0) return true;
            for (int i = 0; i < na; i++)
                if (ba[i] != bb[i]) return false;
        }
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>最小 HTTP Range 服务器：够融合引擎跑分片，不做任何多余的事。</summary>
    private sealed class RangeServer : IDisposable
    {
        private readonly string _path;
        private readonly int _port;
        private readonly byte[] _data;
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        public RangeServer(string path, int port)
        {
            _path = path;
            _port = port;
            _data = File.ReadAllBytes(path);
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                long start = 0, end = _data.Length - 1;
                string range = ctx.Request.Headers["Range"];
                bool partial = false;
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string spec = range.Substring(6);
                    int dash = spec.IndexOf('-');
                    if (dash > 0)
                    {
                        string a = spec.Substring(0, dash);
                        string b = spec.Substring(dash + 1);
                        if (long.TryParse(a, out long s)) start = s;
                        if (long.TryParse(b, out long e) && e >= 0) end = e;
                        partial = true;
                    }
                }
                if (end > _data.Length - 1) end = _data.Length - 1;
                long len = end - start + 1;
                if (len < 0) len = 0;

                ctx.Response.StatusCode = partial ? 206 : 200;
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                ctx.Response.Headers["ETag"] = "\"perf-probe-v1\"";
                if (partial)
                    ctx.Response.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + _data.Length;
                ctx.Response.ContentLength64 = len;

                int chunk = 64 * 1024;
                long pos = start;
                while (pos <= end)
                {
                    int n = (int)Math.Min(chunk, end - pos + 1);
                    await ctx.Response.OutputStream.WriteAsync(_data, (int)pos, n).ConfigureAwait(false);
                    pos += n;
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }
    }
}
