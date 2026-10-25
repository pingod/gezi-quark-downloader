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

using GeZi.Core.Support;

namespace GeZi.Core.Cloud.Providers
{
    /// <summary>
    /// UC 网盘 provider。
    ///
    /// UC 与夸克的接口**完全同构** —— 同样的路径（/1/clouddrive/…）、
    /// 同样的参数名、同样的返回结构，只有三处不同：
    ///   · API 域名：drive-pc.quark.cn  →  pc-api.uc.cn
    ///   · 站点来源：pan.quark.cn      →  drive.uc.cn
    ///   · pr 参数： ucpro             →  UCBrowser
    ///
    /// 参照 wenxi 的做法（quark_uc.dart 用一个连接器类、用 platform 参数区分），
    /// 这里让 <see cref="QuarkProvider"/> 的三个成员可覆盖，UC 只需改这三个值
    /// 就能复用全部逻辑（分享解析、取链、重解析兜底、文件管理）。
    ///
    /// 这比"复制一份再改域名"好得多：夸克那边修了 bug，UC 自动跟着修。
    /// </summary>
    public sealed class UcProvider : QuarkProvider
    {
        public override CloudPlatform Platform => CloudPlatform.Uc;

        protected override string ApiBase => QuarkConstants.UcApiBase;

        protected override string Product => QuarkConstants.UcProduct;
    }
}
