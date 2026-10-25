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

namespace GeZi.Core.Api
{
    /// <summary>夸克接口的业务错误，携带原始 status/code 以便上层分级处理。</summary>
    public class QuarkApiException : Exception
    {
        public object Code { get; }
        public object Status { get; }
        /// <summary>原始 HTTP 状态码（如 401/412/429），与 JSON 中的 status 区分。</summary>
        public int? HttpStatus { get; set; }

        public QuarkApiException(string message, object code = null, object status = null, int? httpStatus = null)
            : base(message)
        {
            Code = code;
            Status = status;
            HttpStatus = httpStatus;
        }
    }
}

namespace GeZi.Core.Api
{
    /// <summary>
    /// 分享下载凭证过期（code 14001 / 41020）。
    ///
    /// 与普通 <see cref="QuarkApiException"/> 区分开，是因为它**可重试**：
    /// 上层拿到它应该去重新解析分享换新 stoken，而不是直接告诉用户失败。
    /// </summary>
    public sealed class QuarkShareCredentialExpiredException : QuarkApiException
    {
        public QuarkShareCredentialExpiredException(string message)
            : base(message, "share_credential_expired", null)
        {
        }
    }
}
