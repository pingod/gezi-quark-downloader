// UI 响应性探针：在**真实 Avalonia 窗口**里洪泛日志，量 UI 线程的调度延迟。
//
// 为什么必须这样测：修复的是「日志管线把 UI 线程占满」，而它的代价
// （TextBlock 全量重排、同步写盘）只有在真窗口里才成立 —— 用假对象测不出来。
//
// 指标：后台线程反复 Dispatcher.InvokeAsync(空操作)，量**从投递到执行**的耗时。
// 这就是用户点按钮到界面响应的延迟，单位毫秒，越小越好。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Wayland;
using GeZi.Avalonia;

internal static class UiLatency
{
    public static int Run(string[] args)
    {
        int threads = int.TryParse(Environment.GetEnvironmentVariable("UI_THREADS"), out int t0) ? t0 : 8;
        int perThread = int.TryParse(Environment.GetEnvironmentVariable("UI_PER_THREAD"), out int p0) ? p0 : 3000;

        Console.WriteLine();
        Console.WriteLine("[ui] 日志洪泛下的 UI 调度延迟（" + threads + " 线程 × " + perThread + " 条）");

        // 用与 Program.BuildAvaloniaApp() 等价的最小配置（那个方法是 internal，
        // 探针在另一个程序集里取不到）。Linux 上先试 Wayland，失败再退 X11。
        AppBuilder builder = AppBuilder.Configure<App>();
        if (OperatingSystem.IsLinux())
            builder = builder.UsePlatformDetect().UseWaylandWithFallback();
        else
            builder = builder.UsePlatformDetect();
        builder.SetupWithoutStarting();

        var win = new MainWindow();
        win.Show();

        MethodInfo log = typeof(MainWindow).GetMethod("Log",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (log == null) { Console.WriteLine("    ✗ 找不到 MainWindow.Log"); return 1; }

        // 预热：让窗口完成首次布局，否则第一拍的耗时会污染统计
        Dispatcher.UIThread.Invoke(() => { });
        Thread.Sleep(500);

        var samples = new List<double>(4096);
        var stop = new CancellationTokenSource();

        // 采样线程：量「投递 → 执行」的往返延迟
        var sampler = new Thread(() =>
        {
            var sw = new Stopwatch();
            while (!stop.IsCancellationRequested)
            {
                sw.Restart();
                try
                {
                    Dispatcher.UIThread.Invoke(() => { sw.Stop(); });
                }
                catch { break; }
                lock (samples) samples.Add(sw.Elapsed.TotalMilliseconds);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        sampler.Start();

        // 洪泛线程：模拟引擎在高并发下产出的日志（含大量重复模板）
        var flood = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            flood[t] = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    log.Invoke(win, new object[] { "分片 " + (i % 256) + " 已连接：HTTP 206，通道 http" });
                    log.Invoke(win, new object[] { "开始下载分片 " + (i % 256) });
                    if (i % 8 == 0)
                        log.Invoke(win, new object[] { "配置项 DynamicSlowThresholdBps 已变更" });
                }
            }) { IsBackground = true };
            flood[t].Start();
        }

        // SetupWithoutStarting() 只初始化平台服务，**不会跑消息循环** ——
        // 必须自己跑 Dispatcher，否则 UI 线程永远不执行排队的回调，
        // 采到的延迟就是无穷大（第一版就是这样，样本数 0）。
        var wall = Stopwatch.StartNew();
        var loopStop = new CancellationTokenSource();
        var waiter = new Thread(() =>
        {
            foreach (var th in flood) th.Join();
            Thread.Sleep(800);          // 让最后一批日志刷完
            loopStop.Cancel();
        }) { IsBackground = true };
        waiter.Start();

        Dispatcher.UIThread.MainLoop(loopStop.Token);
        wall.Stop();
        stop.Cancel();
        sampler.Join(2000);

        List<double> s;
        lock (samples) s = samples.ToList();
        if (s.Count == 0) { Console.WriteLine("    ✗ 没有采到样本"); return 1; }

        s.Sort();
        double P(double q) => s[Math.Min(s.Count - 1, (int)(s.Count * q))];

        int total = threads * perThread * 2 + threads * (perThread / 8);
        Console.WriteLine("    日志条数：" + total + "，耗时 " + wall.ElapsedMilliseconds + " ms");
        Console.WriteLine("    样本数：" + s.Count);
        Console.WriteLine("    中位延迟：" + P(0.50).ToString("F1") + " ms");
        Console.WriteLine("    P90 延迟：" + P(0.90).ToString("F1") + " ms");
        Console.WriteLine("    P99 延迟：" + P(0.99).ToString("F1") + " ms");
        Console.WriteLine("    最大延迟：" + s[s.Count - 1].ToString("F1") + " ms");

        // 门槛：中位延迟 < 20ms 意味着界面在 20ms 内一定响应点击（人眼阈值约 100ms）。
        bool ok = P(0.50) < 20 && P(0.99) < 200;
        Console.WriteLine(ok ? "    ✓ 通过（界面可响应）" : "    ✗ 未通过（UI 线程被占满）");
        return ok ? 0 : 1;
    }
}
