// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// "这个文件需要先做一步准备（转存）才能下载"。
    ///
    /// **这不是错误**，而是"此路不通但另有出路"：
    ///   · 123 / 天翼：分享文件必须先转存到自己账号才能取链
    ///   · 夸克 / UC：直链接口返回 31001/23018（需登录/受限）时，转存是唯一可行路径
    ///
    /// 之所以用异常而不是返回值：
    /// 调用方（UI 的下载队列）已经有一整条 try/catch 处理链，
    /// 用异常能在**不改动正常返回类型**的前提下，把"改走另一条路"的
    /// 信号精确送到上层。携带的 <see cref="Spec"/> 保留了转存所需的全部上下文，
    /// 上层拿到就能直接执行"转存 → 取链 → 下载 → 清理"。
    /// </summary>
    public sealed class CloudNewPreparationException : Exception
    {
        /// <summary>转存所需的上下文（fid / 分享凭证 / 平台 / 账号）。</summary>
        public DownloadSpec Spec { get; }

        public CloudNewPreparationException(string message, DownloadSpec spec) : base(message)
        {
            Spec = spec;
        }
    }
}
