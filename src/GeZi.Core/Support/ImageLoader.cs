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
using System.IO;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 通用图片解码器：把任意常见格式（PNG/JPG/BMP/GIF/WebP/TIFF）的字节
    /// 解成 <see cref="QrDecoder.Pixels"/>（32bpp BGRA，无行 padding）。
    ///
    /// 这是移植到 Linux 时唯一必须换掉的图像 IO 点：
    /// - Windows（net48）：复用系统自带的 GDI+（System.Drawing），零额外依赖。
    /// - Linux/macOS（net8.0）：GDI+ 不存在（.NET 6+ 的 System.Drawing.Common
    ///   会直接抛 PlatformNotSupportedException），改用 **SkiaSharp**，
    ///   它是 Avalonia 本身就在用的渲染后端，不引入新的图形栈。
    ///
    /// 选择 SkiaSharp 而不是 ImageSharp 的理由：体积更小、解码格式覆盖够用，
    /// 且 Avalonia 已经把 libSkiaSharp 带进输出目录，等于**白拿**，不增加发布体积。
    /// </summary>
    internal static class ImageLoader
    {
        /// <summary>
        /// 解码图片字节为 BGRA 像素。失败或格式不支持时返回 null（调用方给用户提示）。
        /// </summary>
        public static QrDecoder.Pixels Load(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

#if NET8_0_OR_GREATER
            return LoadWithSkia(bytes);
#else
            return LoadWithGdi(bytes);
#endif
        }

#if NET8_0_OR_GREATER
        private static QrDecoder.Pixels LoadWithSkia(byte[] bytes)
        {
            using (var data = SkiaSharp.SKData.Create(new MemoryStream(bytes)))
            using (var bmp = SkiaSharp.SKBitmap.Decode(data))
            {
                if (bmp == null)
                    return null;

                int w = bmp.Width, h = bmp.Height;
                if (w <= 0 || h <= 0)
                    return null;

                var px = new QrDecoder.Pixels(w, h);

                // 目标统一成 BGRA8888 / Unpremul —— 与 Pixels 的内存布局一致（每像素 4 字节，无 padding）
                var info = new SkiaSharp.SKImageInfo(w, h,
                    SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);

                var handle = System.Runtime.InteropServices.GCHandle.Alloc(px.Data,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    // ⚠️ SkiaSharp 3.x 起 ReadPixels 移到了 SKPixmap 上，
                    //    SKBitmap 本身不再有该方法 —— 必须先 PeekPixels() 拿到 pixmap。
                    using (var pixmap = bmp.PeekPixels())
                    {
                        if (pixmap == null)
                            return null;
                        if (!pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), w * 4, 0, 0))
                            return null;
                    }
                }
                finally
                {
                    handle.Free();
                }
                return px;
            }
        }
#else
        private static QrDecoder.Pixels LoadWithGdi(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes, writable: false))
            using (var bmp = new System.Drawing.Bitmap(ms))
            {
                int w = bmp.Width, h = bmp.Height;
                if (w <= 0 || h <= 0)
                    return null;

                var px = new QrDecoder.Pixels(w, h);
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var sd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    // Format32bppArgb 在内存里就是 BGRA，与 Pixels 布局一致
                    if (sd.Stride == w * 4)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, px.Data, 0, px.Data.Length);
                    }
                    else
                    {
                        // 有行 padding（宽度不是 4 的倍数时可能出现）→ 逐行拷
                        for (int y = 0; y < h; y++)
                        {
                            IntPtr rowPtr = sd.Scan0 + y * sd.Stride;
                            System.Runtime.InteropServices.Marshal.Copy(
                                rowPtr, px.Data, y * w * 4, w * 4);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(sd);
                }
                return px;
            }
        }
#endif
    }
}
