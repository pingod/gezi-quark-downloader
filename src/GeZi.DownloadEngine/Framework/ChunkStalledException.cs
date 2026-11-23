// GeZi - 鸽子下载器
// Copyright (C) 2026 Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// 「这一片已经很久一个字节都没下动了」—— 2026-11-14 为「失败永不判死」加的熔断信号。
///
/// <para>
/// 为什么需要它：分片的重试额度是无限的（<c>MaxTryAgainOnFailover = int.MaxValue</c>），
/// 一条彻底死掉、或者被 CDN 死死限速到 0 的连接会**永远**在
/// 「失败 → 退避 → 重连 → 再失败」里打转，界面就一直停在 99.x%，看起来像卡死
/// （用户 issue #21：#21 反馈「下载到 99.7% 就没反应了」）。
/// </para>
/// <para>
/// 🚨 关键区别：这**不是**「判失败、清续传」。抛它只是让这一轮下载收手，
/// 上层会把续传快照保存下来、把任务标成「失败（可继续）」，
/// 用户重启软件或点「重试下载」就能从断点接着下 —— 一个字节都不会白下。
/// 所以它必须**不是** <see cref="Validation.FatalChunkException"/>，
/// 也不能是 <see cref="System.IO.InvalidDataException"/>（那两个会清续传）。
/// </para>
/// </summary>
internal sealed class ChunkStalledException : Exception
{
    public ChunkStalledException(string message) : base(message)
    {
    }

    public ChunkStalledException(string message, Exception inner) : base(message, inner)
    {
    }
}
