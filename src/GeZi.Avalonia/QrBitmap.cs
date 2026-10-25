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
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GeZi.Core.Support;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 把二维码模块矩阵渲染成 Avalonia 可显示的位图。
    ///
    /// 为什么不复用 <see cref="QrEncoder.EncodeToBgr"/>：那个方法输出的是
    /// **GDI+ 约定**的位图（自下而上存储、24bpp BGR、行按 4 字节对齐），
    /// 是给 WPF 的 System.Drawing.Bitmap 直接喂的。Avalonia 走 Skia，
    /// 要的是自上而下的 BGRA。硬套会得到一张上下颠倒的二维码（扫不出来）。
    ///
    /// 所以这里直接用 <see cref="QrEncoder.Encode"/> 拿到 bool 矩阵，
    /// 自己按 Avalonia 的像素格式画 —— 逻辑简单，且不依赖任何 GDI 约定。
    /// </summary>
    internal static class QrBitmap
    {
        /// <summary>
        /// 渲染二维码。
        /// </summary>
        /// <param name="text">要编码的内容</param>
        /// <param name="modulePixels">每个模块的像素边长</param>
        /// <param name="quietZone">四周静默区模块数（标准要求 ≥ 4）</param>
        public static WriteableBitmap Render(string text, int modulePixels = 8, int quietZone = 4)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("二维码内容不能为空", nameof(text));
            if (modulePixels < 1) modulePixels = 1;
            if (quietZone < 0) quietZone = 0;

            bool[,] modules = QrEncoder.Encode(text);
            int count = modules.GetLength(0);
            int side = (count + quietZone * 2) * modulePixels;

            var bmp = new WriteableBitmap(
                new PixelSize(side, side),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = bmp.Lock())
            {
                unsafe
                {
                    // 直接按行写指针：side 最大也就 (177+8)*8 ≈ 1480 px，一次填充很快
                    byte* basePtr = (byte*)fb.Address;
                    int stride = fb.RowBytes;

                    // 先铺白底（含静默区）
                    for (int y = 0; y < side; y++)
                    {
                        byte* row = basePtr + y * stride;
                        for (int x = 0; x < side; x++)
                        {
                            row[x * 4 + 0] = 0xFF;   // B
                            row[x * 4 + 1] = 0xFF;   // G
                            row[x * 4 + 2] = 0xFF;   // R
                            row[x * 4 + 3] = 0xFF;   // A
                        }
                    }

                    // 画深色模块（Avalonia 是**自上而下**，不需要像 GDI+ 那样翻转 y）
                    for (int mr = 0; mr < count; mr++)
                    {
                        for (int mc = 0; mc < count; mc++)
                        {
                            if (!modules[mr, mc]) continue;    // 浅色模块保持白色

                            int px0 = (mc + quietZone) * modulePixels;
                            int py0 = (mr + quietZone) * modulePixels;

                            for (int dy = 0; dy < modulePixels; dy++)
                            {
                                byte* row = basePtr + (py0 + dy) * stride;
                                for (int dx = 0; dx < modulePixels; dx++)
                                {
                                    int off = (px0 + dx) * 4;
                                    row[off + 0] = 0x00;   // B
                                    row[off + 1] = 0x00;   // G
                                    row[off + 2] = 0x00;   // R
                                    row[off + 3] = 0xFF;   // A
                                }
                            }
                        }
                    }
                }
            }

            return bmp;
        }
    }
}
