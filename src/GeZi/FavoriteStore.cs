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

namespace GeZi
{
    /// <summary>
    /// 一条收藏。
    /// <para>
    /// 收藏有两种（用户 2026-11-15 提的需求）：
    /// <list type="bullet">
    /// <item><b>收藏链接</b>：在链接输入框里右键「收藏链接」——
    ///       存下这个分享的网盘 / 分享 ID / 提取码 / 起始目录，
    ///       点收藏列表里的它就能重新解析。</item>
    /// <item><b>收藏文件</b>：在解析出来的文件列表里右键「收藏文件」——
    ///       在上面的基础上多存了文件名与它在分享内的相对目录，
    ///       点开时直接定位到那个文件。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 与 <see cref="HistoryStore"/>（state.json）**分开存**（<c>GeZi.favorites.json</c>）：
    /// state.json 里是用户的历史 / 未完成任务（敏感，含可换链的 Token），
    /// 收藏是「我手动留下的东西」，坏掉时不该牵连任务记录；反过来任务记录坏掉也不该丢收藏。
    /// </para>
    /// </summary>
    [DataContract]
    public sealed class FavoriteEntry
    {
        /// <summary>网盘序号（<c>PanKind</c>：0 夸克 / 1 UC / 2 阿里 / 3 迅雷）。</summary>
        [DataMember(Order = 1)] public int PanKind { get; set; }

        /// <summary>分享 ID（夸克 / UC 是 pwd_id，迅雷是 share_id，阿里的 share_id）。</summary>
        [DataMember(Order = 2)] public string ShareId { get; set; }

        /// <summary>提取码（可能为空）。</summary>
        [DataMember(Order = 3)] public string Passcode { get; set; }

        /// <summary>起始目录 fid（收藏链接时是分享根，收藏文件时是它所在的目录）。</summary>
        [DataMember(Order = 4)] public string StartFid { get; set; }

        /// <summary>收藏时的原始分享链接（用于重新解析的兜底，也给用户看）。</summary>
        [DataMember(Order = 5)] public string Url { get; set; }

        /// <summary>显示名：收藏链接时是网盘名，收藏文件时是文件名。</summary>
        [DataMember(Order = 6)] public string Name { get; set; }

        /// <summary>收藏的是不是「具体某个文件」（false = 收藏的是整个链接）。</summary>
        [DataMember(Order = 7)] public bool IsFile { get; set; }

        /// <summary>文件在分享内的相对目录（收藏文件时用；收藏链接时为空）。</summary>
        [DataMember(Order = 8)] public string RelDir { get; set; }

        /// <summary>收藏时的时间（"yyyy-MM-dd HH:mm:ss"，给用户看）。</summary>
        [DataMember(Order = 9)] public string Time { get; set; }

        /// <summary>收藏时的时间戳（Ticks，排序与去重用）。</summary>
        [DataMember(Order = 10)] public long TimeTicks { get; set; }

        /// <summary>
        /// 收藏的是文件时，它的 fid（收藏链接时为空）。
        /// <para>
        /// fid 比文件名可靠：文件名可能被改、可能被规范化（去非法字符），
        /// 而 fid 在同一个分享里是稳定的 —— 定位时先按 fid 找，找不到再按名字。
        /// </para>
        /// </summary>
        [DataMember(Order = 11)] public string Fid { get; set; }

        /// <summary>
        /// 这条收藏指向的分享**是否还有效**（0 未知 / 1 有效 / 2 失效）。
        /// <para>
        /// 收藏的是某个**文件**时，失效判断针对它所在的**分享**——
        /// 我们只做分享级的轻量探测（换一次凭证），不做文件级请求。
        /// </para>
        /// </summary>
        [DataMember(Order = 12)] public int Validity { get; set; }

        /// <summary>上面那次结论的时刻（"yyyy-MM-dd HH:mm:ss"），用来做缓存有效期。</summary>
        [DataMember(Order = 13)] public string ValidityAt { get; set; }
    }

    /// <summary>
    /// 「收藏」的本地存储（<c>GeZi.favorites.json</c>）。
    /// <para>
    /// 写法照抄 <see cref="AdvisorStore"/>：临时文件 + <c>File.Replace</c> 原子替换，
    /// 读不动就把坏文件改名留档、重新开始 —— 绝不牵连用户任务记录。
    /// </para>
    /// </summary>
    internal static class FavoriteStore
    {
        /// <summary>最多保留多少条收藏（超出丢最旧的）。</summary>
        private const int MaxFavorites = 200;

        private static readonly object Gate = new object();

        /// <summary>可选的日志出口（与 HistoryStore.LogSink 同一套，由 MainWindow 启动时挂上）。</summary>
        public static Action<string> LogSink { get; set; }

        /// <summary>落盘结构。</summary>
        [DataContract]
        private sealed class State
        {
            [DataMember(Order = 1)] public List<FavoriteEntry> Items { get; set; }
        }

        private static string StatePath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(SettingsStore.FilePath);
                    return Path.Combine(dir, "GeZi.favorites.json");
                }
                catch
                {
                    return Path.Combine(Util.GetLocalAppDataDir(), "GeZi.favorites.json");
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
                            if (s.Items == null) s.Items = new List<FavoriteEntry>();
                            return s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                QuarantineBroken(ex);
            }
            return new State { Items = new List<FavoriteEntry>() };
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
                LogSink?.Invoke("收藏数据读不动（" + cause.GetType().Name +
                                "），已留档为 " + Path.GetFileName(target) + "，本次重新开始。");
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

        /// <summary>读取全部收藏（按收藏时间正序）。</summary>
        public static List<FavoriteEntry> GetAll()
        {
            lock (Gate)
            {
                var list = Load().Items;
                list.Sort((a, b) => a.TimeTicks.CompareTo(b.TimeTicks));
                return new List<FavoriteEntry>(list);
            }
        }

        /// <summary>
        /// 加一条收藏。<b>同一个分享里的同一个东西不重复加</b>。
        /// </summary>
        /// <returns>true = 新加的；false = 已经收藏过了。</returns>
        public static bool Add(FavoriteEntry entry)
        {
            if (entry == null) return false;
            if (string.IsNullOrEmpty(entry.ShareId)) return false;
            lock (Gate)
            {
                var st = Load();
                foreach (var it in st.Items)
                {
                    if (it == null) continue;
                    if (it.PanKind != entry.PanKind) continue;
                    if (!string.Equals(it.ShareId ?? "", entry.ShareId ?? "", StringComparison.Ordinal)) continue;
                    if (it.IsFile != entry.IsFile) continue;
                    if (!string.Equals(it.RelDir ?? "", entry.RelDir ?? "", StringComparison.Ordinal)) continue;
                    if (!string.Equals(it.Name ?? "", entry.Name ?? "", StringComparison.Ordinal)) continue;
                    return false;   // 已经有了
                }
                entry.TimeTicks = DateTime.UtcNow.Ticks;
                entry.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                st.Items.Add(entry);
                Trim(st);
                Save(st);
                return true;
            }
        }

        /// <summary>删掉一条收藏（按内容匹配，与 <see cref="Add"/> 的去重判据一致）。</summary>
        public static bool Remove(FavoriteEntry entry)
        {
            if (entry == null) return false;
            lock (Gate)
            {
                var st = Load();
                int removed = 0;
                for (int i = st.Items.Count - 1; i >= 0; i--)
                {
                    var it = st.Items[i];
                    if (it == null) continue;
                    if (it.PanKind != entry.PanKind) continue;
                    if (!string.Equals(it.ShareId ?? "", entry.ShareId ?? "", StringComparison.Ordinal)) continue;
                    if (it.IsFile != entry.IsFile) continue;
                    if (!string.Equals(it.RelDir ?? "", entry.RelDir ?? "", StringComparison.Ordinal)) continue;
                    if (!string.Equals(it.Name ?? "", entry.Name ?? "", StringComparison.Ordinal)) continue;
                    st.Items.RemoveAt(i);
                    removed++;
                }
                if (removed == 0) return false;
                Save(st);
                return true;
            }
        }

        /// <summary>
        /// 给某条收藏改显示名（用户在收藏夹里右键「重命名」用）。
        /// <para>
        /// 只改 <c>Name</c>；<c>ShareId</c> / <c>RelDir</c> / <c>Fid</c> 一律不动 ——
        /// 那是「这条收藏指向哪个东西」的身份，改了就定位不到了。
        /// </para>
        /// </summary>
        /// <returns>真的改到并写盘了才返回 true。</returns>
        public static bool Rename(FavoriteEntry entry, string newName)
        {
            if (entry == null) return false;
            string name = (newName ?? "").Trim();
            if (name.Length == 0) return false;
            lock (Gate)
            {
                var st = Load();
                bool changed = false;
                foreach (var it in st.Items)
                {
                    if (it == null) continue;
                    if (it.PanKind != entry.PanKind) continue;
                    if (!string.Equals(it.ShareId ?? "", entry.ShareId ?? "", StringComparison.Ordinal)) continue;
                    if (it.IsFile != entry.IsFile) continue;
                    if (!string.Equals(it.RelDir ?? "", entry.RelDir ?? "", StringComparison.Ordinal)) continue;
                    // ⚠️ 这里要拿 **改名前的旧名字** 一起匹配。
                    //   Add 的去重键是「网盘 + 分享 + 是否文件 + 目录 + **名字**」，
                    //   所以同一个分享可以存在「名字不同」的两条收藏
                    //   （例如：先把历史记录改了名，再收藏一次，就会多出一条）。
                    //   只按前四项匹配会把**两条一起改名** —— 用户只想改其中一条。
                    if (!string.Equals(it.Name ?? "", entry.Name ?? "", StringComparison.Ordinal)) continue;
                    it.Name = name;
                    changed = true;
                }
                if (!changed) return false;
                Save(st);
                return true;
            }
        }

        /// <summary>
        /// 一条收藏的「失效结论」的键。
        /// <para>
        /// 同一个分享下可以存在多条收藏（收藏的是不同文件、或改了名又收藏一次），
        /// 它们共享同一个分享，所以**探测一次、结论共用**。
        /// </para>
        /// </summary>
        public static string ValidityKey(int panKind, string shareId)
        {
            return panKind + "|" + (shareId ?? "");
        }

        /// <summary>
        /// 把一批「分享是否还有效」的结论落盘（0 未知 / 1 有效 / 2 失效）。
        /// <para>
        /// 与 <see cref="HistoryStore.SetShareValidity"/> 同一套做法：一批结论只写一次盘，
        /// 免得打开一次收藏面板就把 favorites.json 重写十几遍。
        /// </para>
        /// </summary>
        public static void SetValidity(IDictionary<string, int> results)
        {
            if (results == null || results.Count == 0) return;
            try
            {
                lock (Gate)
                {
                    var st = Load();
                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    bool changed = false;
                    foreach (var it in st.Items)
                    {
                        if (it == null) continue;
                        int v;
                        if (!results.TryGetValue(ValidityKey(it.PanKind, it.ShareId), out v)) continue;
                        if (it.Validity == v && !string.IsNullOrEmpty(it.ValidityAt)) continue;
                        it.Validity = v;
                        it.ValidityAt = now;
                        changed = true;
                    }
                    if (changed) Save(st);
                }
            }
            catch { }
        }

        /// <summary>清空全部收藏。</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Save(new State { Items = new List<FavoriteEntry>() });
            }
        }

        /// <summary>收藏数（给工具栏按钮上的角标用）。</summary>
        public static int Count()
        {
            lock (Gate)
            {
                return Load().Items.Count;
            }
        }

        private static void Trim(State st)
        {
            if (st.Items.Count <= MaxFavorites) return;
            st.Items.Sort((a, b) => a.TimeTicks.CompareTo(b.TimeTicks));
            st.Items.RemoveRange(0, st.Items.Count - MaxFavorites);
        }
    }
}
