// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GeZi.Providers.Cloud;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 网盘选择器：列出所有已接入平台，让用户选一个打开。
    /// </summary>
    public partial class PlatformPickerWindow : Window
    {
        /// <summary>列表项。</summary>
        public sealed class Item
        {
            public CloudPlatform Platform { get; set; }
            public string Label => Platform?.Label ?? "";
            public string Detail { get; set; } = "";
            public string Badge { get; set; } = "";
        }

        private readonly ObservableCollection<Item> _items = new ObservableCollection<Item>();
        private ListBox _list;
        private TextBlock _hint;

        /// <summary>用户选择的平台；取消为 null。</summary>
        public CloudPlatform Selected { get; private set; }

        public PlatformPickerWindow()
        {
            AvaloniaXamlLoader.Load(this);

            _list = this.FindControl<ListBox>("PlatformList");
            _hint = this.FindControl<TextBlock>("Hint");
            _list.ItemsSource = _items;

            BuildList();
        }

        private void BuildList()
        {
            var supported = CloudProviderRegistry.SupportedPlatforms();

            foreach (var p in supported)
            {
                var provider = CloudProviderRegistry.Get(p);
                if (provider == null) continue;

                // 详情行：能做什么
                var caps = new List<string>();
                if (p.ShareRequiresAccount) caps.Add("分享需登录");
                else caps.Add("分享免登录");

                if (provider.SupportsTransfer) caps.Add("自动转存");
                if (provider.SupportsUpload) caps.Add("上传");
                if (provider.SupportsCreateShare) caps.Add("建分享");

                _items.Add(new Item
                {
                    Platform = p,
                    Detail = string.Join(" · ", caps),
                    Badge = provider.GetType().Name.Replace("Provider", ""),
                });
            }

            _hint.Text = string.Format("共 {0} 个平台已接入", _items.Count);
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            var item = _list?.SelectedItem as Item;
            if (item == null)
            {
                _hint.Text = "请先选一个网盘";
                return;
            }

            Selected = item.Platform;
            Close(Selected);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close(null);
    }
}
