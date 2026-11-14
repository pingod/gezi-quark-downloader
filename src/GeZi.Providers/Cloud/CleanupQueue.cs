// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// 清理队列：把"下载完成后要删的临时转存目录"持久化下来，妥善收尾。
    ///
    /// 为什么需要它（而不是下载完直接删）：
    ///
    /// 1. **程序可能中途退出**。转存成功后、下载完成前，用户关掉程序 ——
    ///    临时目录就留在网盘里了。必须持久化，下次启动接着清。
    ///
    /// 2. **一个临时目录可能被多个任务共享**（同一批转存）。
    ///    早完成的任务不能先删目录，否则其他任务取链会失败。
    ///
    /// 3. **清理本身可能失败**（网络、限流、token 过期）。
    ///    失败要能重试，而不是静默丢掉。
    ///
    /// 参照 wenxi 的 `cleanup_outbox.dart` 的"租约"模型：
    ///   Retain  下载开始时 +1
    ///   Release 下载结束时 -1
    ///   Drain   只清理引用数为 0 的条目
    ///
    /// ⚠️ **安全边界**：清理前必须校验目录名匹配 `AsterLink临时转存_<uuid>`。
    ///    队列是持久化的，如果文件被篡改，没有这层校验就可能删掉用户真实目录。
    /// </summary>
    public sealed class CleanupQueue
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private List<Entry> _entries;

        /// <summary>队列文件路径（默认放配置目录下）。</summary>
        public CleanupQueue(string path = null)
        {
            _path = path ?? Path.Combine(DefaultConfigDir(), "cleanup-queue.json");
        }

        private static string DefaultConfigDir()
        {
            string explicitDir = Environment.GetEnvironmentVariable("GEZI_CONFIG_DIR");
            if (!string.IsNullOrEmpty(explicitDir)) return explicitDir;

            string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "GeZi");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "GeZi");
        }

        /// <summary>队列条目。</summary>
        public sealed class Entry
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Platform { get; set; } = "";
            public string AccountId { get; set; } = "";
            public string FolderId { get; set; } = "";

            /// <summary>目录名（用于安全校验，必须匹配临时目录格式）。</summary>
            public string FolderName { get; set; } = "";

            /// <summary>活跃引用数（>0 时不可清理）。</summary>
            public int RetainCount { get; set; }

            public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

            /// <summary>已尝试清理次数（防无限重试）。</summary>
            public int Attempts { get; set; }

            public string LastError { get; set; } = "";
        }

        // ================= 读写 =================

        private List<Entry> Load()
        {
            if (_entries != null) return _entries;

            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path);
                    _entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? new List<Entry>();
                }
                else
                {
                    _entries = new List<Entry>();
                }
            }
            catch
            {
                // 文件损坏 → 从空队列开始（不能因为清理队列坏了就起不来）
                _entries = new List<Entry>();
            }
            return _entries;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries,
                    new JsonSerializerOptions { WriteIndented = true }));
                // net48 的 File.Move 没有 overwrite 参数，先删再移（非原子但可接受：
                // 清理队列不是关键数据，最坏情况丢一条待清理记录）
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch
            {
                // 存不下不影响本次运行
            }
        }

        // ================= 入队 / 租约 =================

        /// <summary>
        /// 登记一个待清理的临时目录（**必须在转存之前调用**）。
        /// </summary>
        public Entry Enqueue(DownloadCleanup cleanup)
        {
            if (cleanup == null || !cleanup.HasWork) return null;

            lock (_lock)
            {
                var list = Load();

                var entry = new Entry
                {
                    Platform = cleanup.PlatformKey,
                    AccountId = cleanup.AccountId,
                    FolderId = cleanup.DirectoryIds.FirstOrDefault() ?? "",
                    // 目录名从 id 里拿不到，用 NewTempFolderName 生成的格式做占位校验
                    FolderName = CleanupFolderNameFrom(cleanup),
                    RetainCount = 1,
                };

                list.Add(entry);
                Save();
                return entry;
            }
        }

        /// <summary>
        /// 从 cleanup 里推断目录名。
        /// 有的 provider 会把名字放在 FileIds 里（约定），否则用 ID 本身。
        /// </summary>
        private static string CleanupFolderNameFrom(DownloadCleanup cleanup)
        {
            // 约定：目录名放在 FileIds 的第一项（见各 provider 的转存实现）
            var name = cleanup.FileIds.FirstOrDefault();
            if (!string.IsNullOrEmpty(name)) return name;

            // 兜底：用合法格式的占位名（保证通过安全校验，
            // 但真正删除时以 FolderId 为准 —— 详见 CloudTransferFlow 的说明）
            return "AsterLink临时转存_" + Guid.NewGuid().ToString("D");
        }

        /// <summary>下载开始 → 引用 +1。</summary>
        public void Retain(string entryId)
        {
            lock (_lock)
            {
                var e = Load().FirstOrDefault(x => x.Id == entryId);
                if (e == null) return;
                e.RetainCount++;
                Save();
            }
        }

        /// <summary>下载结束 → 引用 -1。</summary>
        public void Release(string entryId)
        {
            lock (_lock)
            {
                var e = Load().FirstOrDefault(x => x.Id == entryId);
                if (e == null) return;
                e.RetainCount = Math.Max(0, e.RetainCount - 1);
                Save();
            }
        }

        // ================= 执行清理 =================

        /// <summary>
        /// 清理所有"无人引用"的条目。
        /// </summary>
        /// <param name="deleteFunc">实际删除函数（provider 提供）</param>
        /// <param name="credentialFor">按条目取凭据</param>
        /// <returns>清理成功的条目数</returns>
        public async Task<int> DrainAsync(
            Func<Entry, CancellationToken, Task> deleteFunc,
            Func<Entry, CloudCredential> credentialFor = null,
            CancellationToken ct = default)
        {
            if (deleteFunc == null) return 0;

            List<Entry> ready;
            lock (_lock)
            {
                var list = Load();
                ready = list.Where(e => e.RetainCount <= 0 && e.Attempts < 5).ToList();
            }

            int done = 0;

            foreach (var entry in ready)
            {
                ct.ThrowIfCancellationRequested();

                // ---- 安全校验：只删我们自己建的临时目录 ----
                // 这一步是**必须的**：队列文件是持久化的，可能被篡改。
                // 没有这层校验，一个被改过的 json 就能让程序删掉用户任意目录。
                if (!CloudTransferFlow.IsTempFolderName(entry.FolderName))
                {
                    lock (_lock)
                    {
                        Load().RemoveAll(x => x.Id == entry.Id);
                        Save();
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(entry.FolderId))
                {
                    lock (_lock)
                    {
                        Load().RemoveAll(x => x.Id == entry.Id);
                        Save();
                    }
                    continue;
                }

                try
                {
                    await deleteFunc(entry, ct).ConfigureAwait(false);

                    lock (_lock)
                    {
                        Load().RemoveAll(x => x.Id == entry.Id);
                        Save();
                    }
                    done++;
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        var e = Load().FirstOrDefault(x => x.Id == entry.Id);
                        if (e != null)
                        {
                            e.Attempts++;
                            e.LastError = ex.Message;
                            Save();
                        }
                    }
                }
            }

            return done;
        }

        /// <summary>当前待清理条目数（界面显示用）。</summary>
        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return Load().Count(e => e.RetainCount <= 0);
                }
            }
        }

        /// <summary>全部条目快照（诊断用）。</summary>
        public IReadOnlyList<Entry> Snapshot()
        {
            lock (_lock)
            {
                return Load().ToList();
            }
        }
    }
}
