// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using GeZi.Core.Support;

namespace GeZi
{
    /// <summary>
    /// 「风控参谋」的本地样本存储（<c>GeZi.advisor.json</c>）。
    ///
    /// <para>
    /// 与 <see cref="HistoryStore"/>（state.json）**分开存**：
    /// state.json 里是用户的历史/未完成任务（敏感，含可换链的 Token），
    /// advisor.json 里只有「网盘 + 线程数 + 速度 + 是否停滞」这种**完全非敏感**的
    /// 观测数字，不混在一起是为了：① 不污染敏感文件的加密/迁移逻辑；
    /// ② 参谋数据坏了（读不动）直接清空重来，绝不牵连用户任务记录。
    /// </para>
    ///
    /// <para>
    /// 每条记录都带 <c>TimeTicks</c>，参谋时按保质期（<see cref="AdvisorEngine.MaxDays"/>）
    /// 丢弃过期的 —— 存的时候不删，删的职责全在纯函数侧，避免写盘时还要算日期。
    /// </para>
    /// </summary>
    internal static class AdvisorStore
    {
        /// <summary>最多保留多少条样本，超出丢最旧的（一次任务一条，200 条足够覆盖很久）。</summary>
        private const int MaxSamples = 200;

        private static readonly object Gate = new object();

        /// <summary>可选的日志出口（与 HistoryStore.LogSink 同一套，由 MainWindow 启动时挂上）。</summary>
        public static Action<string> LogSink { get; set; }

        /// <summary>落盘结构：只有纯数字观测，无任何敏感字段，不需要 DPAPI。</summary>
        [DataContract]
        private sealed class State
        {
            [DataMember(Order = 1)] public List<AdvisorSample> Samples { get; set; }
        }

        private static string StatePath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(SettingsStore.FilePath);
                    return Path.Combine(dir, "GeZi.advisor.json");
                }
                catch
                {
                    return Path.Combine(Util.GetLocalAppDataDir(), "GeZi.advisor.json");
                }
            }
        }

        private static State Load()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    using (var fs = File.OpenRead(StatePath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(State));
                        var s = ser.ReadObject(fs) as State;
                        if (s != null)
                        {
                            if (s.Samples == null) s.Samples = new List<AdvisorSample>();
                            return s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 参谋数据读不动：直接留档后清空重来，绝不牵连用户任务记录。
                QuarantineBroken(ex);
            }
            return new State { Samples = new List<AdvisorSample>() };
        }

        private static void QuarantineBroken(Exception cause)
        {
            try
            {
                string path = StatePath;
                if (!File.Exists(path)) return;
                string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                string target = path + ".broken-" + stamp;
                int n = 1;
                while (File.Exists(target)) { target = path + ".broken-" + stamp + "-" + n; n++; }
                File.Move(path, target);
                LogSink?.Invoke("风控参谋数据读不动（" + cause.GetType().Name +
                                "），已留档为 " + Path.GetFileName(target) + "，本次重新开始采集。");
            }
            catch { }
        }

        private static void Save(State s)
        {
            string tmp = null;
            try
            {
                string path = StatePath;
                tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var ser = new DataContractJsonSerializer(typeof(State));
                    ser.WriteObject(fs, s);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch
            {
                try { if (!string.IsNullOrEmpty(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>读取全部样本（按时间正序，参谋时再按网盘+保质期过滤）。</summary>
        public static List<AdvisorSample> GetAll()
        {
            lock (Gate)
            {
                return new List<AdvisorSample>(Load().Samples);
            }
        }

        /// <summary>追加一条任务结束样本。超出上限丢最旧的。</summary>
        public static void RecordSample(AdvisorSample sample)
        {
            if (sample == null) return;
            lock (Gate)
            {
                var s = Load();
                s.Samples.Add(sample);
                if (s.Samples.Count > MaxSamples)
                    s.Samples.RemoveRange(0, s.Samples.Count - MaxSamples);
                Save(s);
            }
        }
    }
}
