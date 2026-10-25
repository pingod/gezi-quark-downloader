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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 极小的 JSON 兼容层，让业务代码在 net48 与 net8.0 下拿到**同一种对象形状**。
    ///
    /// 背景：net48 版用框架自带的 <c>JavaScriptSerializer</c>，它把 JSON 反序列化成
    /// <c>Dictionary&lt;string,object&gt;</c> / <c>object[]</c> / <c>string</c> / 数值 / <c>bool</c>，
    /// 全项目（QuarkClient、LoginQrService）都按这个形状取值（<c>AsDict</c> / <c>AsArray</c> / <c>AsLong</c>）。
    /// net8.0 上 <c>System.Web.Extensions</c> 已不存在，但也不能把业务代码改成
    /// <c>JsonElement</c> —— 那会把平台差异扩散到每个取值点。
    ///
    /// 所以这里只做一件事：<b>在 net8.0 上把 JSON 转成和 JavaScriptSerializer 一模一样的形状</b>，
    /// 业务层代码一行都不用改。net48 上则直接沿用原实现（保持零 NuGet、离线可构建）。
    ///
    /// 数值映射刻意对齐 JavaScriptSerializer：
    ///   整数 → <see cref="int"/>（放得下时）否则 <see cref="long"/>
    ///   小数 → <see cref="decimal"/>
    ///   这样 <c>AsLong</c> 里对 int/long/decimal 的分支原样可用。
    /// </summary>
    public static class JsonCompat
    {
        /// <summary>把 JSON 文本反序列化成 Dictionary/object[]/基础类型。</summary>
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
#if NET8_0_OR_GREATER
            using (var doc = System.Text.Json.JsonDocument.Parse(json,
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true }))
            {
                return FromElement(doc.RootElement);
            }
#else
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
            return ser.DeserializeObject(json);
#endif
        }

        /// <summary>把对象序列化成 JSON（Dictionary/object[]/基础类型）。</summary>
        public static string Serialize(object o)
        {
            if (o == null)
                return "null";
#if NET8_0_OR_GREATER
            return System.Text.Json.JsonSerializer.Serialize(o, o.GetType(), JsonOptions);
#else
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
            return ser.Serialize(o);
#endif
        }

#if NET8_0_OR_GREATER
        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                // 不转义中文/斜杠 —— 与 JavaScriptSerializer 一致，也便于排查日志
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
            };

        /// <summary>JsonElement → JavaScriptSerializer 风格的对象图。</summary>
        private static object FromElement(System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                {
                    var d = new Dictionary<string, object>();
                    foreach (var p in e.EnumerateObject())
                        d[p.Name] = FromElement(p.Value);
                    return d;
                }
                case System.Text.Json.JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (var item in e.EnumerateArray())
                        list.Add(FromElement(item));
                    return list.ToArray();
                }
                case System.Text.Json.JsonValueKind.String:
                    return e.GetString();
                case System.Text.Json.JsonValueKind.Number:
                {
                    // 先试整数：JavaScriptSerializer 对能放进 int 的值给 int，否则 long
                    if (e.TryGetInt32(out int i32))
                        return i32;
                    if (e.TryGetInt64(out long i64))
                        return i64;
                    // 超出 long 或带小数 → decimal（与 JavaScriptSerializer 对浮点的处理一致）
                    if (e.TryGetDecimal(out decimal dec))
                        return dec;
                    return e.GetDouble();
                }
                case System.Text.Json.JsonValueKind.True:
                    return true;
                case System.Text.Json.JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
#endif
    }
}
