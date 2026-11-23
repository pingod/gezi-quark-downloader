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

namespace GeZi.Core.Support
{
    /// <summary>
    /// 一条「风控参谋」样本：某次下载任务结束时的观测记录。
    /// 被动采集，不主动探测 —— 只在用户真实下载结束后记一笔，
    /// 数据是白捡的，不冒任何额外风控风险。
    /// </summary>
    public sealed class AdvisorSample
    {
        /// <summary>网盘（<c>GeZi.Core.Api.PanKind</c> 的整数值）。</summary>
        public int PanKind { get; set; }

        /// <summary>任务实际启用的并发连接上限（<c>TaskItem.WorkerCount</c>）。</summary>
        public int Threads { get; set; }

        /// <summary>本次任务的峰值速度（字节/秒）。</summary>
        public long PeakBps { get; set; }

        /// <summary>是否观察到停滞/风控信号（连接大量失败、速度断崖归零）。</summary>
        public bool Stalled { get; set; }

        /// <summary>任务是否完成。</summary>
        public bool Completed { get; set; }

        /// <summary>记录时刻（<c>DateTime.UtcNow.Ticks</c>）。</summary>
        public long TimeTicks { get; set; }
    }

    /// <summary>
    /// 「风控参谋」对某一家网盘的结论（纯数据，文案由 UI 层拼）。
    /// </summary>
    public sealed class AdvisorVerdict
    {
        /// <summary>样本是否足够（有效样本 ≥ <see cref="AdvisorEngine.MinSamples"/>）。</summary>
        public bool Ready { get; set; }

        /// <summary>有效样本条数（未过期）。</summary>
        public int SampleCount { get; set; }

        /// <summary>红线：达到此并发会触发风控（0 = 还没碰到）。</summary>
        public int RedLine { get; set; }

        /// <summary>建议并发区间下限（0 = 无建议）。</summary>
        public int SuggestMin { get; set; }

        /// <summary>建议并发区间上限（0 = 无建议）。</summary>
        public int SuggestMax { get; set; }

        /// <summary>推荐档位的峰值速度（字节/秒，供 UI 格式化）。</summary>
        public long BestPeakBps { get; set; }
    }

    /// <summary>
    /// 「风控参谋」纯函数：从一堆下载记录里推出一家网盘的安全并发区间。
    ///
    /// <para>
    /// 三条规则（对应用户拍板）：
    /// ① <b>保质期衰减</b>：记录带时间戳，超过 <see cref="MaxDays"/> 天直接丢弃，
    ///    超过 <see cref="FreshDays"/> 天权重减半（只在峰值打平时做 tie-break）。
    /// ② <b>失败记忆优先</b>：只要某个并发档出现过停滞/失败，它就是「红线」，
    ///    该档及以上直接标红、建议区间上限永远压在红线之下 —— 绝不建议重踩。
    /// ③ <b>冷启动兜底</b>：有效样本不足 <see cref="MinSamples"/> 条时 <c>Ready=false</c>，
    ///    卡片退回显示磁盘介质，不瞎给建议。
    /// </para>
    ///
    /// <para>
    /// 抽成 Core 纯函数是为了能自检：三条规则 + 边界（红线压档 / 全失败 / 样本不足）
    /// 都是纯算术，不需要 WPF 也不需要真实下载就能穷举验证。
    /// </para>
    /// </summary>
    public static class AdvisorEngine
    {
        /// <summary>开始参谋所需的最少有效样本数（用户拍板 3 次起）。</summary>
        public const int MinSamples = 3;

        /// <summary>「新鲜」窗口（天）：此窗口内的样本权重为 2，其余为 1。</summary>
        public const int FreshDays = 7;

        /// <summary>保质期（天）：超过此天数的样本直接丢弃。</summary>
        public const int MaxDays = 30;

        /// <summary>无安全样本时建议区间的下限（16 = 引擎阻塞领取下限）。</summary>
        public const int FloorThreads = 16;

        private const long TicksPerDay = TimeSpan.TicksPerDay;

        private sealed class Merged
        {
            public long PeakBps;
            public bool Stalled;
            public bool Completed;
            public int Fresh;   // 新鲜样本计数（tie-break 用）
        }

        /// <summary>
        /// 按网盘聚合后的参谋入口：先把样本过滤到指定网盘 + 保质期内，再走
        /// <see cref="Advise"/> 的核心逻辑。抽出来单独一个重载，方便 UI 一次传全量。
        /// </summary>
        public static AdvisorVerdict AdviseFor(IList<AdvisorSample> samples, int panKind, long nowTicks)
        {
            var filtered = new List<AdvisorSample>();
            if (samples != null)
            {
                foreach (var s in samples)
                {
                    if (s == null || s.PanKind != panKind) continue;
                    long age = nowTicks - s.TimeTicks;
                    if (age < 0) age = 0;
                    if (age > MaxDays * TicksPerDay) continue;   // 保质期：过期丢弃
                    filtered.Add(s);
                }
            }
            return Advise(filtered, nowTicks);
        }

        /// <summary>
        /// 核心参谋：入参必须是「同一家网盘、且已过滤掉过期样本」的列表。
        /// </summary>
        public static AdvisorVerdict Advise(IList<AdvisorSample> samples, long nowTicks)
        {
            var v = new AdvisorVerdict();
            if (samples == null) samples = new List<AdvisorSample>();
            v.SampleCount = samples.Count;
            v.Ready = samples.Count >= MinSamples;
            if (!v.Ready)
                return v;   // 样本不足，其余字段全 0，UI 退回冷启动兜底。

            // 按「并发档」归并：同档取峰值最大、停滞取或、完成取或、新鲜计数累加。
            var byThreads = new Dictionary<int, Merged>();
            foreach (var s in samples)
            {
                if (s.Threads < 1) continue;
                if (!byThreads.TryGetValue(s.Threads, out var m))
                {
                    m = new Merged();
                    byThreads[s.Threads] = m;
                }
                if (s.PeakBps > m.PeakBps) m.PeakBps = s.PeakBps;
                if (s.Stalled) m.Stalled = true;
                if (s.Completed) m.Completed = true;
                if (nowTicks - s.TimeTicks <= FreshDays * TicksPerDay) m.Fresh++;
            }

            // ② 失败记忆优先：红线 = 出现过停滞的最小并发档。
            int redLine = 0;
            foreach (var kv in byThreads)
            {
                if (kv.Value.Stalled && (redLine == 0 || kv.Key < redLine))
                    redLine = kv.Key;
            }
            v.RedLine = redLine;

            // 选推荐：从「无停滞且完成」的安全档里，挑峰值速度最高的一档。
            int suggest = 0;
            long suggestBps = -1;
            int suggestFresh = -1;
            foreach (var kv in byThreads)
            {
                if (kv.Value.Stalled || !kv.Value.Completed) continue;
                int t = kv.Key;
                long bps = kv.Value.PeakBps;
                if (bps > suggestBps || (bps == suggestBps && kv.Value.Fresh > suggestFresh))
                {
                    suggestBps = bps;
                    suggest = t;
                    suggestFresh = kv.Value.Fresh;
                }
            }

            // ② 失败记忆优先：推荐档触碰/越过红线 → 压到红线之下。
            if (suggest > 0)
            {
                if (redLine > 0 && suggest >= redLine)
                {
                    int bestUnder = 0;
                    foreach (var kv in byThreads)
                    {
                        if (kv.Value.Stalled || !kv.Value.Completed) continue;
                        if (kv.Key < redLine && kv.Key > bestUnder)
                            bestUnder = kv.Key;
                    }
                    suggest = bestUnder > 0 ? bestUnder : Math.Max(FloorThreads, redLine / 2);
                    suggestBps = -1;
                    if (bestUnder > 0)
                    {
                        foreach (var kv in byThreads)
                            if (kv.Key == bestUnder) suggestBps = kv.Value.PeakBps;
                    }
                }
            }
            else
            {
                // 没有安全样本（全停滞或全失败）：红线存在就建议降到红线一半以下。
                if (redLine > 0)
                    suggest = Math.Max(FloorThreads, redLine / 2);
            }

            v.SuggestMax = suggest;
            v.SuggestMin = suggest > 0 ? Math.Max(FloorThreads, suggest / 2) : 0;
            v.BestPeakBps = suggestBps > 0 ? suggestBps : 0;
            return v;
        }
    }
}
