// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
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
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Generic;
using System.IO;
using ZXing;
using ZXing.Common;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Providers.Qr
{
    /// <summary>
    /// 二维码/条码解码器（基于 ZXing.Net，Apache-2.0，本地固化引用见 lib/ZXing/）。
    /// <see cref="QrEncoder"/> 那种编码器只要把数据排进矩阵，
    /// 而解码反着来：要先在杂乱图像里找出定位图形、做透视校正、按模块采样，
    /// 最后跑 Reed-Solomon 纠错 —— 现实中的截图/照片还带缩放、JPEG 噪点、背景干扰。
    /// 这部分用成熟库远比自写可靠。
    /// 现实图片千奇百怪，单次尝试失败率偏高，所以这里做**多轮渐进尝试**：
    ///   1) 原图直解（清晰截图命中率最高，也最快）
    ///   2) 放大（小图/被缩小的图，模块太小会丢）
    ///   3) 灰度 + 提高对比度（低对比、带色调的截图）
    ///   4) 灰度 + 二值化（噪点、渐变背景）
    ///   5) 反转色（深浅色主题下的"反色二维码"）
    ///   6) 中央裁剪重试（二维码只占图片一小块时，裁掉无关区域减少干扰）
    /// 任一命中即返回，避免无谓开销。
    ///
    /// ★ 跨平台（2026-10 移植）：整条图像管线**不再依赖 System.Drawing**。
    ///   原始实现用 GDI+ 的 Bitmap/Graphics 做缩放、灰度、二值化，那套 API 在
    ///   Linux/macOS 上不可用（.NET 6+ 的 System.Drawing.Common 直接抛
    ///   PlatformNotSupportedException）。现在改成：
    ///     解码图片 → <see cref="Pixels"/>（32bpp BGRA 纯托管数组）
    ///     所有变换都在这个数组上做，ZXing 侧统一走
    ///     <see cref="RGBLuminanceSource"/>（它只吃字节数组，完全跨平台）。
    ///   好处不只是"能跑"：net48 与 net8.0 现在跑的是**同一份图像处理代码**，
    ///   不再有"Windows 上能用、Linux 上行为不同"的分裂。
    /// </summary>
    public static class QrDecoder
    {
        /// <summary>解码结果。</summary>
        public sealed class Result
        {
            /// <summary>是否成功解出内容。</summary>
            public bool Ok;

            /// <summary>解出的文本（含非链接内容，交给上层判断）。</summary>
            public string Text;

            /// <summary>识别到的码制（如 QR_CODE、CODE_128）。</summary>
            public string Format;

            /// <summary>失败原因（成功时为空）。</summary>
            public string Error;

            /// <summary>命中的尝试轮次名（排查用）。</summary>
            public string Attempt;
        }

        /// <summary>允许的码制。默认收窄到场景相关的两类，避免把菜谱、产品条码等误识成结果。</summary>
        private static readonly List<BarcodeFormat> Formats = new List<BarcodeFormat>
        {
            BarcodeFormat.QR_CODE,
            BarcodeFormat.CODE_128,   // 少数分享会以条码图给出
        };

        /// <summary>尝试的放大倍数（原图之外的额外轮次）。</summary>
        private const int UpscaleFactor = 2;

        /// <summary>图片像素总量上限（超过则先等比缩小，避免大图爆内存/超时）。</summary>
        private const long MaxPixels = 32L * 1024 * 1024;   // 约 3200 万像素

        /// <summary>按最大边限制的尺寸（超大图缩到这个量级再解）。</summary>
        private const int MaxSide = 4000;

        // ================= 纯托管像素容器 =================

        /// <summary>
        /// 32bpp BGRA 像素缓冲（每像素 4 字节，行间无 padding）。
        /// 用它替代原来的 GDI+ Bitmap：所有图像变换都是对 <see cref="Data"/> 的纯数组操作。
        /// </summary>
        public sealed class Pixels
        {
            public readonly int Width;
            public readonly int Height;
            public readonly byte[] Data;   // BGRA, stride = Width * 4

            public Pixels(int w, int h)
            {
                Width = w;
                Height = h;
                Data = new byte[w * h * 4];
            }

            public Pixels(int w, int h, byte[] data)
            {
                Width = w;
                Height = h;
                Data = data;
            }

            public Pixels Clone()
                => new Pixels(Width, Height, (byte[])Data.Clone());
        }

        // ================= 公开入口 =================

        /// <summary>
        /// 从图片文件解码。支持 png / jpg / bmp / gif / webp 等常见格式。
        /// </summary>
        public static Result DecodeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Fail("路径为空");
            if (!File.Exists(path))
                return Fail("文件不存在：" + path);

            byte[] bytes;
            try
            {
                // 一次读进内存：不占文件句柄（用户可能马上删/移这张图）
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                // 这里只是读文件失败，还没有任何位图被创建 —— 不需要（也无法）释放。
                return Fail("图片读取失败：" + ex.Message);
            }
            return DecodeBytes(bytes);
        }

        /// <summary>
        /// 从内存字节解码（贴剪贴板图片、拖拽文件都走这里）。
        /// </summary>
        public static Result DecodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return Fail("图片数据为空");

            Pixels src;
            try
            {
                src = ImageLoader.Load(bytes);
            }
            catch (Exception ex)
            {
                return Fail("图片解析失败：" + ex.Message);
            }
            if (src == null)
                return Fail("图片格式不支持（可试试 PNG / JPG / BMP）");

            return DecodePixels(src);
        }

        /// <summary>从已解码的像素缓冲解码（供 UI 层直接把屏幕截图喂进来）。</summary>
        public static Result DecodePixels(Pixels src)
        {
            if (src == null || src.Width <= 0 || src.Height <= 0)
                return Fail("图片无效");

            try
            {
                Pixels work = Normalize(src);

                // ---- 第 1 轮：原图 ----
                var r = TryDecode(work, "原图");
                if (r != null) return r;

                // ---- 第 2 轮：放大 ----
                var up = Resize(work, work.Width * UpscaleFactor, work.Height * UpscaleFactor);
                r = TryDecode(up, "放大 " + UpscaleFactor + "×");
                if (r != null) return r;

                // ---- 第 3 轮：灰度增强对比 ----
                var gray = ToGray(work, contrast: true);
                r = TryDecode(gray, "灰度增强");
                if (r != null) return r;

                // ---- 第 4 轮：灰度 + 二值化（Otsu 阈值） ----
                var bw = Binarize(gray);
                r = TryDecode(bw, "二值化");
                if (r != null) return r;

                // ---- 第 5 轮：反转色（深色主题截图里的"反色二维码"） ----
                var inv = Invert(work);
                r = TryDecode(inv, "反色");
                if (r != null) return r;

                // ---- 第 6 轮：中央裁剪（码只占画面一小块） ----
                foreach (int keepPct in new[] { 80, 60, 40 })
                {
                    var crop = CenterCrop(work, keepPct);
                    if (crop == null) continue;
                    r = TryDecode(crop, "中央裁剪 " + keepPct + "%");
                    if (r != null) return r;
                }

                return Fail("没识别到二维码。试试换一张更清晰、二维码更完整的图。");
            }
            catch (Exception ex)
            {
                return Fail("解码出错：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---------------- 内部实现 ----------------

        /// <summary>单次解码尝试；失败返回 null（不抛异常，避免噪声图打断整条链）。</summary>
        private static Result TryDecode(Pixels px, string attempt)
        {
            try
            {
                // RGBLuminanceSource 只吃字节数组 → 天然跨平台。
                // 给它 BGRA 格式的原始数据，由 ZXing 自己算亮度。
                var source = new RGBLuminanceSource(
                    px.Data, px.Width, px.Height,
                    RGBLuminanceSource.BitmapFormat.BGRA32);

                var binarizer = new HybridBinarizer(source);
                var bitmap = new BinaryBitmap(binarizer);

                var reader = new MultiFormatReader();
                var hints = new Dictionary<DecodeHintType, object>
                {
                    { DecodeHintType.POSSIBLE_FORMATS, Formats },
                    { DecodeHintType.TRY_HARDER, true },
                };

                var res = reader.decode(bitmap, hints);
                if (res == null || string.IsNullOrEmpty(res.Text)) return null;

                return new Result
                {
                    Ok = true,
                    Text = res.Text,
                    Format = res.BarcodeFormat.ToString(),
                    Attempt = attempt,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 归一化：超大图等比缩小（避免内存与耗时失控）。
        /// 格式已经统一是 BGRA，不需要再做像素格式转换。
        /// </summary>
        private static Pixels Normalize(Pixels src)
        {
            int w = src.Width, h = src.Height;

            double scale = 1.0;
            if (w > MaxSide || h > MaxSide)
                scale = Math.Min((double)MaxSide / w, (double)MaxSide / h);
            if ((long)(w * scale) * (long)(h * scale) > MaxPixels)
                scale = Math.Sqrt((double)MaxPixels / ((long)w * h));

            if (scale < 1.0)
                return Resize(src, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));

            return src.Clone();
        }

        /// <summary>双线性插值缩放（纯托管）。缩小与放大用同一套，质量足够 QR 识别。</summary>
        private static Pixels Resize(Pixels src, int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            var dst = new Pixels(w, h);
            byte[] s = src.Data, d = dst.Data;
            int sw = src.Width, sh = src.Height;

            double xRatio = (double)sw / w;
            double yRatio = (double)sh / h;

            for (int y = 0; y < h; y++)
            {
                double sy = (y + 0.5) * yRatio - 0.5;
                int y0 = (int)Math.Floor(sy);
                if (y0 < 0) y0 = 0;
                if (y0 > sh - 1) y0 = sh - 1;
                int y1 = Math.Min(y0 + 1, sh - 1);
                double fy = sy - y0;
                if (fy < 0) fy = 0;

                int dRow = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    double sx = (x + 0.5) * xRatio - 0.5;
                    int x0 = (int)Math.Floor(sx);
                    if (x0 < 0) x0 = 0;
                    if (x0 > sw - 1) x0 = sw - 1;
                    int x1 = Math.Min(x0 + 1, sw - 1);
                    double fx = sx - x0;
                    if (fx < 0) fx = 0;

                    int i00 = (y0 * sw + x0) * 4;
                    int i10 = (y0 * sw + x1) * 4;
                    int i01 = (y1 * sw + x0) * 4;
                    int i11 = (y1 * sw + x1) * 4;

                    for (int c = 0; c < 4; c++)
                    {
                        double top = s[i00 + c] * (1 - fx) + s[i10 + c] * fx;
                        double bottom = s[i01 + c] * (1 - fx) + s[i11 + c] * fx;
                        d[dRow + x * 4 + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                    }
                }
            }
            return dst;
        }

        /// <summary>转灰度；contrast=true 时把对比度拉开（围绕 0.5 提升 1.6 倍）。</summary>
        private static Pixels ToGray(Pixels src, bool contrast)
        {
            var dst = new Pixels(src.Width, src.Height);
            byte[] s = src.Data, d = dst.Data;

            const float c = 1.6f, t = (1f - c) / 2f;

            for (int i = 0; i < s.Length; i += 4)
            {
                // BT.601 亮度权重（与 GDI+ ColorMatrix 那套系数一致）
                float lum = .299f * s[i + 2] + .587f * s[i + 1] + .114f * s[i];
                if (contrast)
                    lum = lum * c + t * 255f;

                byte v = (byte)(lum < 0 ? 0 : lum > 255 ? 255 : lum);
                d[i] = v; d[i + 1] = v; d[i + 2] = v; d[i + 3] = 255;
            }
            return dst;
        }

        /// <summary>Otsu 大津法自动阈值二值化（比固定阈值稳，能适应不同亮度）。</summary>
        private static Pixels Binarize(Pixels gray)
        {
            int w = gray.Width, h = gray.Height;
            byte[] s = gray.Data;
            var dst = new Pixels(w, h);
            byte[] d = dst.Data;

            // 灰度直方图（取 B 通道即可，前面已转灰度，三通道相等）
            var hist = new int[256];
            for (int i = 0; i < s.Length; i += 4) hist[s[i]]++;

            int threshold = OtsuThreshold(hist, w * h);

            for (int i = 0; i < s.Length; i += 4)
            {
                byte v = s[i] >= threshold ? (byte)255 : (byte)0;
                d[i] = v; d[i + 1] = v; d[i + 2] = v; d[i + 3] = 255;
            }
            return dst;
        }

        private static int OtsuThreshold(int[] hist, int total)
        {
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += (double)i * hist[i];

            double sumB = 0, maxVar = -1;
            int wB = 0, best = 127;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;

                sumB += (double)t * hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar) { maxVar = between; best = t; }
            }
            return best;
        }

        /// <summary>RGB 反转（处理"反色二维码"）。</summary>
        private static Pixels Invert(Pixels src)
        {
            var dst = new Pixels(src.Width, src.Height);
            byte[] s = src.Data, d = dst.Data;
            for (int i = 0; i < s.Length; i += 4)
            {
                d[i] = (byte)(255 - s[i]);
                d[i + 1] = (byte)(255 - s[i + 1]);
                d[i + 2] = (byte)(255 - s[i + 2]);
                d[i + 3] = 255;
            }
            return dst;
        }

        /// <summary>按百分比取中央区域（<paramref name="keepPct"/> = 保留边长百分比）。</summary>
        private static Pixels CenterCrop(Pixels src, int keepPct)
        {
            int cw = src.Width * keepPct / 100;
            int ch = src.Height * keepPct / 100;
            if (cw < 32 || ch < 32) return null;             // 太小没意义
            if (cw >= src.Width && ch >= src.Height) return null;

            int x0 = (src.Width - cw) / 2;
            int y0 = (src.Height - ch) / 2;

            var dst = new Pixels(cw, ch);
            byte[] s = src.Data, d = dst.Data;
            for (int y = 0; y < ch; y++)
            {
                int sRow = ((y0 + y) * src.Width + x0) * 4;
                int dRow = y * cw * 4;
                Buffer.BlockCopy(s, sRow, d, dRow, cw * 4);
            }
            return dst;
        }

        private static Result Fail(string msg)
        {
            return new Result { Ok = false, Error = msg };
        }
    }
}
