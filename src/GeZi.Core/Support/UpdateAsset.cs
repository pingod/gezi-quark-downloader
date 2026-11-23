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
    /// 更新包附件名的识别规则（2026-11-15）。
    ///
    /// 🚨 起因（真 bug，已实测）：检查器原来只认「<c>-win64.zip</c>」，
    /// 而打包脚本 <c>tools/package_release.py</c> 生成的名字是「<c>-win-x64.zip</c>」
    /// ⇒ 一个都匹配不上 ⇒ <c>AssetSize</c> 恒 0、「安装包大小」徽章不显示、
    /// 也没有安装包直链可用。
    ///
    /// 放在 Core 是为了让自检能直接断言（自检不引用 WPF 层，
    /// 且全仓没有 <c>InternalsVisibleTo</c>）。
    /// </summary>
    public static class UpdateAsset
    {
        /// <summary>附件名前缀（打包脚本固定生成 <c>GeZi-vX.Y.Z-…</c>）。</summary>
        public const string NamePrefix = "GeZi-v";

        /// <summary>Windows x64 安装包的后缀（当前打包脚本用的）。</summary>
        public const string SuffixWinX64 = "-win-x64.zip";

        /// <summary>Windows 64 位安装包的后缀（旧命名，保留兼容）。</summary>
        public const string SuffixWin64 = "-win64.zip";

        /// <summary>
        /// 这个附件名是不是「Windows x64 安装包」。
        /// 两种后缀都认（<c>-win-x64.zip</c> 与 <c>-win64.zip</c>），
        /// 且必须带 <c>GeZi-v</c> 前缀，避免把 <c>.sha256</c> 校验文件当成安装包。
        /// </summary>
        public static bool IsWindowsPackage(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return false;
            if (!assetName.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase)) return false;
            return assetName.EndsWith(SuffixWinX64, StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith(SuffixWin64, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从附件名列表里挑出安装包（第一个命中的）。
        /// 挑不到返回 null —— 调用方据此隐藏「直接下载」按钮。
        /// </summary>
        public static string PickPackageName(IEnumerable<string> assetNames)
        {
            if (assetNames == null) return null;
            foreach (var n in assetNames)
            {
                if (IsWindowsPackage(n)) return n;
            }
            return null;
        }
    }
}
