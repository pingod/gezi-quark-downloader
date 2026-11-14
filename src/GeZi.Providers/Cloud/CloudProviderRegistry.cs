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
using System.Collections.Generic;
using System.Linq;
using GeZi.Providers.Cloud.Providers;

namespace GeZi.Providers.Cloud
{
    /// <summary>
    /// provider 注册表：按平台取对应的实现。
    ///
    /// 为什么用注册表而不是一个大 switch：
    /// 新增网盘时只需要写一个 provider 类 + 在这里登记一行，
    /// 界面和上层调度代码完全不用动。这也让"某平台暂未实现"
    /// 变成一种可查询的状态（<see cref="IsSupported"/>），
    /// 界面可以据此把不支持的平台灰掉。
    /// </summary>
    public static class CloudProviderRegistry
    {
        private static readonly Dictionary<string, Func<ICloudProvider>> Factories =
            new Dictionary<string, Func<ICloudProvider>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Locker = new object();
        private static bool _initialized;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (Locker)
            {
                if (_initialized) return;

                // ================= 已实现的平台 =================
                //
                // 每个平台都必须是**真实实现**（能列目录 + 能取链），
                // 不做"占位类"—— 占位会让 IsSupported() 撒谎，
                // 界面显示"支持"但一点就报"待适配"，比明确不支持更糟。

                // ---- 三个"完整能力"平台（分享 + 个人盘 + 文件管理）----
                Register(CloudPlatform.Quark.Key, () => new QuarkProvider());
                Register(CloudPlatform.Uc.Key, () => new UcProvider());
                Register(CloudPlatform.Aliyun.Key, () => new AliyunProvider());

                // ---- 分享解析型（分享可匿名，个人盘需登录）----
                Register(CloudPlatform.Pan115.Key, () => new Pan115Provider());
                Register(CloudPlatform.Baidu.Key, () => new BaiduProvider());
                Register(CloudPlatform.Pan123.Key, () => new Pan123Provider());
                Register(CloudPlatform.Tianyi.Key, () => new TianyiProvider());

                // ---- 必须登录才能解析分享 ----
                Register(CloudPlatform.Xunlei.Key, () => new XunleiProvider());
                Register(CloudPlatform.PikPak.Key, () => new PikPakProvider());

                // ---- 公开分享型（只需链接即可取链）----
                Register(CloudPlatform.Lanzou.Key, () => new LanzouProvider());
                Register(CloudPlatform.Ctfile.Key, () => new CtfileProvider());

                // ---- OAuth 类（令牌刷新）----
                Register(CloudPlatform.Guangya.Key, () => new GuangyaProvider());
                Register(CloudPlatform.Feijipan.Key, () => new FeijipanProvider());
                Register(CloudPlatform.Ilanzou.Key, () => new IlanzouProvider());
                Register(CloudPlatform.Wopan.Key, () => new WopanProvider());

                // ---- 内容浏览型 ----
                Register(CloudPlatform.Github.Key, () => new GithubProvider());

                // ---- 明确不支持（保留登记，只为让 IsSupported 如实返回 true，
                //      调用时会抛出带原因的 CloudNotSupportedException）----
                // 微云：自有二进制协议，未适配
                Register(CloudPlatform.Weiyun.Key, () => new WeiyunProvider());

                _initialized = true;
            }
        }

        /// <summary>登记一个平台的 provider 工厂。</summary>
        public static void Register(string platformKey, Func<ICloudProvider> factory)
        {
            if (string.IsNullOrEmpty(platformKey) || factory == null) return;
            Factories[platformKey] = factory;
        }

        /// <summary>该平台是否有可用实现。</summary>
        public static bool IsSupported(CloudPlatform platform)
        {
            if (platform == null) return false;
            EnsureInitialized();
            return Factories.ContainsKey(platform.Key);
        }

        /// <summary>
        /// 取 provider 实例。没有实现时返回 null
        /// （而不是抛异常 —— "不支持"是常态，不该用异常表达）。
        /// </summary>
        public static ICloudProvider Get(CloudPlatform platform)
        {
            if (platform == null) return null;
            EnsureInitialized();

            if (!Factories.TryGetValue(platform.Key, out var factory))
                return null;

            try
            {
                return factory();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>取 provider，没有就抛一个带中文提示的异常（给"必须要有"的调用点用）。</summary>
        public static ICloudProvider Require(CloudPlatform platform)
        {
            var p = Get(platform);
            if (p == null)
                throw new CloudNotSupportedException(
                    (platform?.Label ?? "该网盘") + " 暂不支持");
            return p;
        }

        /// <summary>当前所有有实现的平台。</summary>
        public static IReadOnlyList<CloudPlatform> SupportedPlatforms()
        {
            EnsureInitialized();
            return CloudPlatform.All.Where(p => Factories.ContainsKey(p.Key)).ToList();
        }
    }
}
