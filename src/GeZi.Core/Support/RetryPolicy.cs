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
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 失败重试的判据（纯函数，集中一份，供引擎与自检共用）。
    /// <para>
    /// 🚨 2026-11-14 新增背景：用户硬要求「任何文件都不可以轻易说失败，必须有其他路径
    /// 继续下载」。原来一次网络抖动就能把整个任务判死，而失败后界面连个「重试」入口都没有，
    /// 用户只能重开软件 —— 可重开之后进度明明还在（<c>.geziresume</c> 完好），
    /// 说明那次失败本来就不该是终局。
    /// </para>
    /// <para>
    /// 这里只回答两个问题：
    /// ① 这个异常是不是**传输层断开**（而不是磁盘/权限问题）；
    /// ② 这个失败原因**值不值得再试一次**。
    /// </para>
    /// </summary>
    public static class RetryPolicy
    {
        /// <summary>
        /// 判断异常是否属于「传输层连接断开」。
        /// <para>
        /// 🚨 为什么需要这个：.NET Framework 的 <c>ConnectStream</c> 在读响应体时抛的是
        /// <see cref="IOException"/>，消息是「无法从传输连接中读取数据: 连接已关闭。」——
        /// 那是**网络断开**，跟磁盘一点关系都没有。但它的 <c>Source</c> 是 "System"、
        /// <c>InnerException</c> 是空的，所以引擎原来两套判据
        /// （<c>HasSource("System.Net.Sockets")</c> / <c>HasTypeOf(typeof(SocketException))</c>）
        /// 全都不成立 → 落到兜底 catch 直接判死，还套上「写盘失败」的错误前缀误导用户。
        /// </para>
        /// <para>
        /// ⚠️ 判据必须能区分「网络」与「磁盘」：磁盘类异常（空间不足、拒绝访问、
        /// 路径过长）绝不能命中，否则会变成无限重试。
        /// </para>
        /// </summary>
        public static bool IsTransportIo(Exception ex)
        {
            if (ex == null) return false;

            // 先看异常链里有没有明确的网络类型 —— 这是最可靠的判据。
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is WebException) return true;
                if (cur is SocketException) return true;
            }

            // 再看消息关键字。ConnectStream 的 IOException 只能靠这条路认出来。
            string msg = ex.Message ?? "";
            if (msg.Length == 0) return false;

            if (msg.IndexOf("传输连接", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("连接已关闭", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("远程主机强迫关闭", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("已意外停止", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("OpenSSL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("underlying connection was closed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("forcibly closed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 🚨🚨 2026-11-15 修：.NET Framework 的中文版消息是「从传输流收到意外的 EOF」，
            // 英文原版才是 "unexpectedly"。上面那条英文判据对中文用户**永远不成立**，
            // 结果就是这种网络断开被套上「写盘失败：」的前缀，还会因为落进兜底 catch
            // 而直接判死任务、丢掉续传点（用户实测：16.33GB 下到 91.1% 报「写盘失败」）。
            // 所以中文变体必须一起认，否则用户看到的是完全错误的归因。
            if (msg.IndexOf("意外的 EOF", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("传输流", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("收到意外", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("意外结束", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("意外的结束", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        /// <summary>
        /// 判断这次失败值不值得自动重试。
        /// <para>
        /// 只有「换条连接 / 换个时机就可能好」的失败才重试：网络类、直链类、超时类。
        /// 内容校验不符、磁盘满、没权限、文件在服务器上已不存在 —— 重试纯粹浪费用户时间。
        /// </para>
        /// <para>判据基于引擎给用户看的**人话**消息（<c>FusionDownloader.DescribeFailure</c> 的产物）。</para>
        /// </summary>
        public static bool IsRetryableFailure(string message)
        {
            if (string.IsNullOrEmpty(message))
                return true;   // 没给原因，按可重试处理（保守）

            // —— 重试也没用的：直接判终局 ——
            if (message.IndexOf("内容校验失败", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("不一致", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("磁盘空间", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("空间不足", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("没有权限", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("无权限", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("已不存在", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("任务为空", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("下载地址为空", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("目标路径为空", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("不支持", StringComparison.Ordinal) >= 0) return false;
            if (message.IndexOf("已取消", StringComparison.Ordinal) >= 0) return false;

            // 其余（连接断开 / 超时 / 域名解析 / 直链失效 / 长度不符 …）一律值得再试。
            return true;
        }
    }
}
