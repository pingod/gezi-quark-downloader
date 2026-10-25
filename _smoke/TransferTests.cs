using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Cloud;

internal static class TransferTests
{
    private static int _fail, _pass;

    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[13] 转存流程与清理队列");

        await TestTempFolderNaming();
        await TestCleanupQueueAsync();
        TestTransferWaiter();
    }

    // ---------- 临时目录命名与安全校验 ----------
    private static Task TestTempFolderNaming()
    {
        Console.WriteLine("  -- 临时目录命名（安全边界）");

        string name = CloudTransferFlow.NewTempFolderName();
        Check("生成的名字可被校验通过", CloudTransferFlow.IsTempFolderName(name), name);

        // 必须拒绝：用户真实目录、格式不对的、前缀对但后缀不是 uuid
        Check("拒绝普通目录名", !CloudTransferFlow.IsTempFolderName("我的文件"));
        Check("拒绝空前缀", !CloudTransferFlow.IsTempFolderName(""));
        Check("拒绝 null", !CloudTransferFlow.IsTempFolderName(null));
        Check("拒绝前缀对但后缀非法",
            !CloudTransferFlow.IsTempFolderName("AsterLink临时转存_not-a-uuid"));
        Check("拒绝前缀不对的 uuid",
            !CloudTransferFlow.IsTempFolderName("Other_" + Guid.NewGuid().ToString("D")));

        return Task.CompletedTask;
    }

    // ---------- 清理队列 ----------
    private static async Task TestCleanupQueueAsync()
    {
        Console.WriteLine("  -- 清理队列（租约模型）");

        string tmp = Path.Combine(Path.GetTempPath(), "gezi-cleanup-" + Guid.NewGuid().ToString("N") + ".json");
        var q = new CleanupQueue(tmp);

        Check("初始队列为空", q.PendingCount == 0, q.PendingCount.ToString());

        // 入队
        var cleanup = new DownloadCleanup { PlatformKey = "quark", AccountId = "u1" };
        cleanup.DirectoryIds.Add("dir-123");
        cleanup.FileIds.Add(CloudTransferFlow.NewTempFolderName());

        var entry = q.Enqueue(cleanup);
        Check("入队成功", entry != null, entry?.Id);
        Check("入队后 RetainCount=1（下载中不可清）", entry.RetainCount == 1, entry.RetainCount.ToString());

        // 注意：PendingCount 统计 RetainCount<=0 的，所以此时应为 0
        Check("有活跃引用时不进入待清理", q.PendingCount == 0, q.PendingCount.ToString());

        // 释放租约
        q.Release(entry.Id);
        Check("释放后进入待清理", q.PendingCount == 1, q.PendingCount.ToString());

        // 执行清理
        int deleted = 0;
        int done = await q.DrainAsync((e, ct) =>
        {
            deleted++;
            Check("清理前校验目录名合法", CloudTransferFlow.IsTempFolderName(e.FolderName), e.FolderName);
            return Task.CompletedTask;
        });

        Check("Drain 执行了 1 条", done == 1 && deleted == 1, done + "/" + deleted);
        Check("清理后队列为空", q.PendingCount == 0, q.PendingCount.ToString());

        // 篡改防护：手工塞一个非法名字的条目，Drain 应该直接丢弃而不调用删除
        var fake = new CleanupQueue(tmp);
        var c2 = new DownloadCleanup { PlatformKey = "quark" };
        c2.DirectoryIds.Add("evil-dir");
        c2.FileIds.Add("我的重要文件夹");   // ← 非法名字
        fake.Enqueue(c2);

        // 让它变成可清理
        foreach (var e in fake.Snapshot()) fake.Release(e.Id);

        bool deleteCalled = false;
        await fake.DrainAsync((e, ct) => { deleteCalled = true; return Task.CompletedTask; });

        Check("篡改的条目不会触发删除", !deleteCalled, deleteCalled ? "危险！" : "已拦截");

        try { File.Delete(tmp); } catch { }
    }

    // ---------- 可见性等待 ----------
    private static void TestTransferWaiter()
    {
        Console.WriteLine("  -- 转存可见性判定");

        var items = new[]
        {
            new CloudFile { Id = "1", Name = "a.zip", Size = 100 },
            new CloudFile { Id = "2", Name = "b.zip", Size = 200 },
        };

        var hit = TransferWaiter.MatchUnique(items, "a.zip", 100);
        Check("唯一匹配成功", hit?.Id == "1", hit?.Id);

        Check("名字不符返回 null", TransferWaiter.MatchUnique(items, "c.zip", 0) == null);
        Check("大小不符返回 null", TransferWaiter.MatchUnique(items, "a.zip", 999) == null);

        // 同名同大小两个 → 不能确定，必须返回 null（防取错文件）
        var dup = new[]
        {
            new CloudFile { Id = "1", Name = "same.zip", Size = 100 },
            new CloudFile { Id = "2", Name = "same.zip", Size = 100 },
        };
        Check("重名不唯一时返回 null（防取错）",
            TransferWaiter.MatchUnique(dup, "same.zip", 100) == null);

        // 目录不参与匹配
        var withDir = new[]
        {
            new CloudFile { Id = "d", Name = "x", IsDirectory = true },
        };
        Check("目录不参与匹配", TransferWaiter.MatchUnique(withDir, "x", 0) == null);
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("      ✓ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
        else { _fail++; Console.WriteLine("      ✗ " + name + (detail == "" ? "" : "  [" + detail + "]")); }
    }

    public static int Failed => _fail;
}
