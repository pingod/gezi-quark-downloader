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

// ---- 本文件是 MainWindow 的 partial 之一（代码规范化拆分，2026-10-24） ----
// 职责：收藏夹（用户 2026-11-15 提出的新功能）
//
// 用户原话：
//   「在搜索框和上级的中间，增加收藏功能，它的图标是一个空心星星。」
//   「当解析一个链接后我可以在链接输入位置右键选择收藏链接，
//     然后我也可以在解析完后的链接中右键文件选择收藏文件，
//     我点击收藏功能，就有个弹窗能够看到这些收藏，具体方式可复用解析历史的功能代码ui。」
//
// ⇒ 面板结构**完全照抄解析历史**（居中卡片 + 压暗遮罩 + 头/列表/脚三行），
//   存储单独放 GeZi.favorites.json（见 FavoriteStore.cs），不混进 state.json。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeZi.Core.Api;
using GeZi.Core.Support;
using GeZi.Providers.Shared;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using ShapePath = System.Windows.Shapes.Path;

namespace GeZi
{
    public partial class MainWindow : Window
    {
        /// <summary>收藏夹面板是不是开着（Esc 关闭与打开逻辑都看它）。</summary>
        private bool FavoriteOpen
        {
            get { return FavoriteOverlay != null && FavoriteOverlay.Visibility == Visibility.Visible; }
        }

        /// <summary>
        /// 解析成功后要「下钻定位」的收藏目标。
        /// 点收藏夹里的**文件**时不能只把分享打开，还得走到那个文件所在目录并选中它，
        /// 所以解析成功后要接着做这件事。
        /// </summary>
        private string _pendingFavoriteRelDir;
        private string _pendingFavoriteName;
        private string _pendingFavoriteFid;


        // ============================================================
        //  工具栏按钮 / 浮层开关
        // ============================================================

        /// <summary>工具栏上那个空心星星按钮（搜索框与「上级」之间）。</summary>
        private void OnFavoriteButtonClick(object sender, RoutedEventArgs e)
        {
            ShowFavoritePanel();
        }

        private void ShowFavoritePanel()
        {
            try
            {
                RebuildFavoritePanel();   // 每次打开都重建：这中间可能又收藏过
                if (FavoriteOverlay == null) return;
                FavoriteOverlay.Visibility = Visibility.Visible;
                // 焦点收到浮层自己身上，免得之后随手敲的字母钻进底下那个链接框。
                FavoriteOverlay.Focus();
            }
            catch (Exception ex)
            {
                LogDetail("打开收藏夹失败: " + ex.Message);
            }
        }

        private void OnFavoriteCloseClick(object sender, RoutedEventArgs e)
        {
            HideFavoritePanel();
        }

        private void HideFavoritePanel()
        {
            try { if (FavoriteOverlay != null) FavoriteOverlay.Visibility = Visibility.Collapsed; } catch { }
        }

        /// <summary>点遮罩（面板外的暗色区域）= 关闭。</summary>
        private void OnFavoriteOverlayClick(object sender, MouseButtonEventArgs e)
        {
            HideFavoritePanel();
        }

        private void OnFavoriteClearClick(object sender, RoutedEventArgs e)
        {
            try
            {
                FavoriteStore.Clear();
                RebuildFavoritePanel();
                Log("已清空收藏夹。");
            }
            catch (Exception ex)
            {
                Log("清空收藏夹失败: " + ex.Message);
            }
        }


        // ============================================================
        //  列表重建 / 造行
        // ============================================================

        private void RebuildFavoritePanel()
        {
            if (FavoriteListPanel == null) return;
            FavoriteListPanel.Children.Clear();
            _favoriteBadges.Clear();      // 行是重新造的，旧标签引用全部作废
            int gen = ++_favoriteGen;

            var items = FavoriteStore.GetAll();
            if (FavoriteCountText != null)
                FavoriteCountText.Text = items.Count > 0 ? "共 " + items.Count + " 条" : "";

            if (items.Count == 0)
            {
                FavoriteListPanel.Children.Add(new TextBlock
                {
                    Text = "还没有收藏。\n在链接输入框里右键「收藏链接」，或在文件上右键「收藏文件」。",
                    Style = (Style)FindResource("HistoryEmptyText"),
                });
                return;
            }

            // 新的在前（GetAll 已按时间正序，这里倒着铺，最新的在顶上）。
            for (int i = items.Count - 1; i >= 0; i--)
                FavoriteListPanel.Children.Add(MakeFavoriteRow(items[i]));

            // 行造完再后台校验失效（不 await：面板先出来，标签随后自己冒出来）。
            _ = ProbeFavoriteValidityAsync(gen);
        }


        // ---------------- 收藏夹：失效校验（2026-11-15 补） ----------------

        /// <summary>面板重建代次：异步校验回来时代次变了就丢弃结果（行已经不在屏幕上了）。</summary>
        private int _favoriteGen;

        /// <summary>失效标签：ValidityKey → 该分享下所有收藏行的「已失效」标签。</summary>
        private readonly Dictionary<string, List<Border>> _favoriteBadges = new Dictionary<string, List<Border>>();

        /// <summary>
        /// 校验收藏夹里的分享**是否还有效**，失效的在行尾打「已失效」标签。
        /// <para>
        /// 🚨 2026-11-15 补：解析历史早就有这套检测（<c>ProbeHistoryValidityAsync</c>），
        /// 收藏夹却一次探针都没发 —— 用户收藏完过一阵再点，链接早没了也不知道。
        /// 这里照搬历史那套**两阶段**逻辑（阶段一只换凭证、阶段二才列目录），
        /// 语义也完全一致：
        /// <list type="bullet">
        /// <item><b>阶段一</b> <c>IPanProvider.ProbeShareAsync</c>：只换凭证不列目录。
        ///       服务端明确拒绝 = 确定失效；提取码不对 = 也是失效。</item>
        /// <item><b>阶段二</b> 只有阶段一判定「还活着」的才列一次目录，
        ///       看是不是空分享（用户 2026-10-26 明确要求空分享也算失效）。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 三条纪律与历史那边一致：
        /// ① **网络故障 ≠ 失效**（连不上就返回 Unknown，不显示标签，绝不瞎标）；
        /// ② 并发限 3，不把界面线程/网络打满；
        /// ③ 结论按「网盘 + 分享」缓存 1 小时并**落盘**，重开面板/重启软件立刻能看见。
        /// </para>
        /// </summary>
        private async Task ProbeFavoriteValidityAsync(int gen)
        {
            // 按「网盘 + 分享」归组：同一个分享只探一次，组内所有收藏共享结论。
            var groups = new Dictionary<string, List<FavoriteEntry>>();
            var order = new List<string>();
            foreach (var it in FavoriteStore.GetAll())
            {
                if (it == null || string.IsNullOrEmpty(it.ShareId)) continue;
                // 只探我们真的能构造出客户端的四家；别的（将来新增的）宁可不显示标签。
                if (it.PanKind < (int)PanKind.Quark || it.PanKind > (int)PanKind.Xunlei) continue;
                string key = FavoriteStore.ValidityKey(it.PanKind, it.ShareId);
                if (!groups.ContainsKey(key))
                {
                    groups[key] = new List<FavoriteEntry>();
                    order.Add(key);
                }
                groups[key].Add(it);
            }
            if (groups.Count == 0) return;

            var todo = new List<FavoriteEntry>();
            foreach (var key in order)
            {
                var g = groups[key];
                // 1 小时内探过就不重复打网盘（结论已落盘在 Validity / ValidityAt）。
                var first = g[0];
                if (first.Validity != 0 && FreshEnough(first.ValidityAt)) continue;
                todo.Add(first);
            }
            if (todo.Count == 0) return;

            // 同一家网盘共用一个临时客户端（复用 TLS 连接），别每条都 new 一个。
            var providers = new Dictionary<int, IPanProvider>();
            var verdicts = new Dictionary<string, int>();
            try
            {
                using (var gate = new SemaphoreSlim(3))
                {
                    var tasks = new List<Task>();
                    foreach (var it in todo)
                    {
                        var entry = it;
                        tasks.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            // 0 = 判断不了（网络问题，不落盘、不显示标签）；1 = 有效；2 = 失效。
                            int verdict = 0;
                            try
                            {
                                IPanProvider p;
                                lock (providers)
                                {
                                    if (!providers.TryGetValue(entry.PanKind, out p))
                                    {
                                        p = BuildProbeProvider((PanKind)entry.PanKind);
                                        providers[entry.PanKind] = p;
                                    }
                                }
                                if (p == null) return;

                                var shareRef = new PanShareRef
                                {
                                    Kind = (PanKind)entry.PanKind,
                                    ShareId = entry.ShareId,
                                    Passcode = entry.Passcode ?? "",
                                    StartFid = string.IsNullOrEmpty(entry.StartFid) ? "0" : entry.StartFid,
                                };

                                // 阶段一：只换凭证，不列目录。
                                var probe = await p.ProbeShareAsync(shareRef).ConfigureAwait(false);
                                if (probe == null) return;
                                if (probe.IsInvalid)
                                {
                                    verdict = 2;   // 服务端明确拒绝 = 确定失效
                                }
                                else if (probe.IsAlive)
                                {
                                    // 阶段二：只有「还活着」的才列一次目录，看是不是空分享。
                                    try
                                    {
                                        var files = await p.ListShareAsync(shareRef, shareRef.StartFid)
                                            .ConfigureAwait(false);
                                        verdict = (files != null && files.Count > 0) ? 1 : 2;
                                    }
                                    catch (QuarkApiException)
                                    {
                                        // 服务端明确拒绝（分享被删 / 子目录没了）= 失效。
                                        verdict = 2;
                                    }
                                    catch
                                    {
                                        // 网络问题 ≠ 失效，别把好好的链接标红。
                                        verdict = 0;
                                    }
                                }
                                else
                                {
                                    // Unknown：连不上或者需要提取码才能判断 —— 不下结论。
                                    verdict = 0;
                                }
                            }
                            catch (QuarkApiException)
                            {
                                // QuarkApiException 是服务端**明确拒绝**（分享不存在 / 已过期 /
                                // 提取码被改 / 文件被删），它恰恰是「失效」最硬的证据。
                                verdict = 2;
                            }
                            catch
                            {
                                // 剩下的才是真的网络问题（超时 / DNS / 断网 / TLS）。
                                // 这种情况不能算「失效」，否则断网时整列收藏都会变红 —— 那是说谎。
                                verdict = 0;
                            }
                            finally
                            {
                                gate.Release();
                            }

                            if (verdict == 0) return;
                            string vkey = FavoriteStore.ValidityKey(entry.PanKind, entry.ShareId);
                            lock (verdicts) verdicts[vkey] = verdict;
                            if (verdict == 1) return;

                            // 回到 UI 线程改标签；代次变了说明面板已经重建，这一行作废。
                            try
                            {
                                // 用 `_ =` 丢掉 DispatcherOperation：它在 .NET 4.5+ 是可 await 的，
                                // 不丢弃的话编译器会报 CS4014（本项目要求 0 警告）。
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (gen != _favoriteGen) return;
                                    List<Border> badges;
                                    if (!_favoriteBadges.TryGetValue(vkey, out badges)) return;
                                    if (badges == null) return;
                                    foreach (var b in badges)
                                    {
                                        if (b != null) b.Visibility = Visibility.Visible;
                                    }
                                }));
                            }
                            catch { }
                        }));
                    }
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }

                // 一次探测得出的一批结论统一落盘（只写一次 favorites.json）。
                // 落盘之后重开面板/重启软件都能立刻看到「已失效」，不用再等网络。
                if (verdicts.Count > 0)
                {
                    try { FavoriteStore.SetValidity(verdicts); } catch { }
                }
            }
            finally
            {
                foreach (var kv in providers)
                {
                    if (kv.Value == null) continue;
                    try { kv.Value.Dispose(); } catch { }
                }
            }
        }

        /// <summary>造一行收藏。整行是个 Button，点 = 重新打开那条分享。</summary>
        private UIElement MakeFavoriteRow(FavoriteEntry it)
        {
            var kind = (PanKind)it.PanKind;
            string panName = PanUrlParser.DisplayNameOf(kind);

            // 图标：收藏的是文件就用文件类型图标，收藏的是链接就用文件夹。
            var icon = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                "SurfaceSunkenBrush");
            icon.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
                "BorderBrushSoft");

            var glyph = new ShapePath
            {
                Data = it.IsFile
                    ? FileTypeIconConverter.GeometryFor(it.Name ?? "")
                    : (Geometry)FindResource("IconFolder"),
                Stretch = Stretch.Uniform,
                Width = 17, Height = 17,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 用 SetResourceReference（等价 DynamicResource）：换主题时 Brushes.xaml 整份重建，
            // 握在手里的旧实例不一定跟着变。
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                it.IsFile ? FileTypeIconConverter.BrushKeyFor(it.Name ?? "") : "WarningBrush");
            icon.Child = glyph;

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // 2026-11-15 用户要求「就地改名」：名字这一块平时是文字，
            //   右键「重命名」后**原地**变成输入框（光标竖杠 + 全选，直接打字覆盖）。
            //   点名字本身也能进编辑态（不用右键）。
            // ⚠️ 收藏的是「分享」时，平时显示的是网盘名（如「夸克网盘 分享」），
            //   但真正要改的是这条收藏自己的名字（it.Name）——
            //   所以：显示态优先用 it.Name（改名后立刻看得见），
            //   it.Name 为空（刚收藏、还没名字）时才退回「网盘名 分享」；
            //   编辑态里放的始终是 it.Name。
            string displayName = !string.IsNullOrEmpty(it.Name)
                ? it.Name
                : (it.IsFile ? "" : (panName + " 分享"));
            var nameEditor = InlineNameEditor.Create(
                displayName,
                it.Name ?? "",
                "HistoryNameText",
                newName =>
                {
                    // 只改显示名，ShareId / RelDir / Fid 一律不动 —— 那是这条收藏的身份。
                    string oldName = it.Name ?? "";
                    if (!FavoriteStore.Rename(it, newName)) return false;
                    // ⚠️ 必须把这条收藏对象本身的新名字也更新掉：
                    //   FavoriteStore.Rename 是按「改名前的名字」定位并写盘的，
                    //   盘上已经变成新名字了，但内存里这条还是旧名字 ——
                    //   不更新的话「第二次改名」就再也匹配不上（会误报没成功）。
                    it.Name = newName;
                    Log("已重命名收藏：" + oldName + " → " + newName);
                    NoticeFavorite("已重命名为「" + newName + "」");
                    return true;
                },
                null);
            meta.Children.Add(nameEditor);
            meta.Children.Add(new TextBlock
            {
                Text = FavoriteMetaOf(it),
                Style = (Style)FindResource("HistoryMetaText"),
            });

            // 右上角的「已失效」小标签：面板打开后在后台逐条校验，**失效的才显示**
            // （校验逻辑见 ProbeFavoriteValidityAsync）。默认收起，避免每行先闪一下灰字。
            var badgeText = new TextBlock { Text = "已失效", FontSize = 11 };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 2, 0),
                Visibility = Visibility.Collapsed,
                Child = badgeText,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "DangerLightBrush");
            // 结论已经落盘（FavoriteEntry.Validity），所以**造行时就先摆上**，
            // 不用再等后台探测 —— 否则每次重开面板都要重跑一遍网络才看得到红标。
            if (it.Validity == 2) badge.Visibility = Visibility.Visible;
            string vkey = FavoriteStore.ValidityKey(it.PanKind, it.ShareId);
            List<Border> badges;
            if (!_favoriteBadges.TryGetValue(vkey, out badges))
            {
                badges = new List<Border>();
                _favoriteBadges[vkey] = badges;
            }
            badges.Add(badge);

            // ⚠️ 外层用 Grid 而不是横向 StackPanel：名称那个省略号只在**宽度被约束**时才生效，
            //    StackPanel 会用无限宽去量孩子，长名字就直接撑出去、不截断。
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            meta.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(meta, 1);
            Grid.SetColumn(badge, 2);
            row.Children.Add(icon);
            row.Children.Add(meta);
            row.Children.Add(badge);

            var btn = new Button
            {
                Content = row,
                Style = (Style)FindResource("HistoryRowButton"),
                Tag = it,     // 点击时取回这一条
                ToolTip = it.IsFile
                    ? "重新打开并定位到：" + (it.Name ?? "")
                    : "重新打开这个分享",
            };
            btn.Click += OnFavoriteItemClick;
            btn.ContextMenu = MakeFavoriteMenu(it, nameEditor);
            // 告诉名称控件：外面这层按钮就是「整行可点」的那个
            //   （改名期间要把它的点击摘掉，否则改个名会被当成「重新打开这条」）。
            nameEditor.AttachHostButton(btn, OnFavoriteItemClick);
            return btn;
        }

        /// <summary>副标题：网盘 · 收藏时间 · 分享 ID（太长就截断）。</summary>
        private static string FavoriteMetaOf(FavoriteEntry it)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(PanUrlParser.DisplayNameOf((PanKind)it.PanKind));
            if (!string.IsNullOrEmpty(it.Time))
                sb.Append(" · ").Append(it.Time);
            if (!string.IsNullOrEmpty(it.RelDir))
                sb.Append(" · ").Append(it.RelDir);
            if (!string.IsNullOrEmpty(it.ShareId))
            {
                sb.Append(" · /s/");
                sb.Append(it.ShareId.Length > 8 ? it.ShareId.Substring(0, 8) : it.ShareId);
            }
            return sb.ToString();
        }

        /// <summary>行上右键：重命名、删除这条收藏。</summary>
        private ContextMenu MakeFavoriteMenu(FavoriteEntry it, InlineNameEditor nameEditor)
        {
            var menu = new ContextMenu();

            // 同解析历史的右键菜单：这一项没有图标，
            // 所以套 TrayMenuItem（纯文字紧凑模板），
            // 免得左边空出隐式样式那个固定 22px 图标列。
            var del = new MenuItem { Header = "删除这条收藏", Style = Application.Current.TryFindResource("TrayMenuItem") as Style };
            del.Click += (s, e) =>
            {
                try
                {
                    FavoriteStore.Remove(it);
                    RebuildFavoritePanel();
                    Log("已从收藏夹删除：" + (it.IsFile ? it.Name : it.ShareId));
                }
                catch (Exception ex) { Log("删除收藏失败: " + ex.Message); }
            };

            // 2026-11-15 用户要求：收藏夹里的条目也能右键重命名。
            //   只改显示名，ShareId / RelDir / Fid 一律不动 —— 那是这条收藏的身份。
            //   ⚠️ 是**就地编辑**（名字原地变输入框、光标竖杠 + 全选），不是弹框。
            var rename = new MenuItem { Header = "重命名", Style = Application.Current.TryFindResource("TrayMenuItem") as Style };
            rename.Click += (s, e) =>
            {
                try
                {
                    if (nameEditor == null) return;
                    nameEditor.BeginEdit();
                }
                catch (Exception ex) { Log("重命名收藏失败: " + ex.Message); }
            };

            menu.Items.Add(rename);
            menu.Items.Add(del);
            return menu;
        }


        // ============================================================
        //  收藏链接（链接输入框右键）
        // ============================================================

        /// <summary>
        /// 链接输入框右键「收藏链接」。
        /// ⚠️ 收藏的是**这段文字指向的那个分享**，所以要先从文字里把分享 ID 抠出来。
        /// </summary>
        private void OnFavoriteLinkClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string raw = LinkBox != null ? LinkBox.Text.Trim() : "";
                if (string.IsNullOrEmpty(raw))
                {
                    NoticeFavorite("输入框里没有内容，没法收藏");
                    return;
                }

                PanKind kind;
                string shareId;
                string passcode;
                string startFid;

                // ① 能认出网盘域名（夸克/UC/阿里/迅雷）就用它的解析结果 —— 最准。
                var pref = PanUrlParser.Parse(raw);
                if (pref != null)
                {
                    kind = pref.Kind;
                    shareId = pref.ShareId;
                    passcode = pref.Passcode;
                    startFid = string.IsNullOrEmpty(pref.StartFid) ? "0" : pref.StartFid;
                }
                else
                {
                    // ② 认不出域名，但有 /s/xxx —— 按「当前正在分享的那家」算。
                    var info = ShareUrlParser.Parse(raw);
                    if (string.IsNullOrEmpty(info.PwdId))
                    {
                        NoticeFavorite("这段文字里没认出分享链接，收藏不了");
                        return;
                    }
                    kind = _panKind;
                    shareId = info.PwdId;
                    passcode = info.Passcode;
                    startFid = string.IsNullOrEmpty(info.StartFid) ? "0" : info.StartFid;
                }

                var entry = new FavoriteEntry
                {
                    PanKind = (int)kind,
                    ShareId = shareId,
                    Passcode = passcode ?? "",
                    StartFid = startFid,
                    Url = BuildShareUrl(kind, shareId, startFid),
                    Name = PanUrlParser.DisplayNameOf(kind),
                    IsFile = false,
                    RelDir = "",
                };

                if (FavoriteStore.Add(entry))
                {
                    Log("已收藏分享：" + entry.Name + " /s/" + shareId);
                    NoticeFavorite("已收藏这个分享");
                    RebuildFavoritePanel();
                }
                else
                {
                    NoticeFavorite("这个分享已经在收藏夹里了");
                }
            }
            catch (Exception ex)
            {
                Log("收藏链接失败: " + ex.Message);
                NoticeFavorite("收藏失败：" + ex.Message);
            }
        }


        // ============================================================
        //  收藏文件（文件列表右键）
        // ============================================================

        /// <summary>
        /// 文件列表右键「收藏文件」。
        /// 存下：分享 ID + 提取码 + 分享进入点 + 该文件在分享内的相对目录 + 文件名与 fid。
        /// 点开时靠这些重新解析并**下钻定位**到那个文件。
        /// </summary>
        private void OnFavoriteFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var sel = FileList != null ? FileList.SelectedItem as ShareFileItem : null;
                if (sel == null)
                {
                    NoticeFavorite("先选中一个文件再收藏");
                    return;
                }

                string shareId = _panShareRef != null ? _panShareRef.ShareId : _pwdId;
                if (string.IsNullOrEmpty(shareId))
                {
                    NoticeFavorite("还没解析出分享，没法收藏文件");
                    return;
                }

                var kind = _panShareRef != null ? _panShareRef.Kind : _panKind;
                string passcode = _panShareRef != null ? (_panShareRef.Passcode ?? "") : (_passcode ?? "");
                string startFid = string.IsNullOrEmpty(_startFid) ? "0" : _startFid;

                // 当前浏览路径在分享内的相对目录（相对分享进入点 _startFid）。
                // ⚠️ 不能用 _files 里的 RelDir —— 那个字段只在递归收集（CollectFilesAsync）
                //    时填，列表页显示的行它是空的。
                string relDir = CurrentShareRelDir();

                var entry = new FavoriteEntry
                {
                    PanKind = (int)kind,
                    ShareId = shareId,
                    Passcode = passcode,
                    StartFid = startFid,
                    Url = BuildShareUrl(kind, shareId, startFid),
                    Name = sel.Name ?? "",
                    IsFile = true,
                    RelDir = relDir,
                    Fid = sel.Fid ?? "",
                };

                if (FavoriteStore.Add(entry))
                {
                    Log("已收藏文件：" + entry.Name);
                    NoticeFavorite("已收藏这个文件");
                    RebuildFavoritePanel();
                }
                else
                {
                    NoticeFavorite("这个文件已经在收藏夹里了");
                }
            }
            catch (Exception ex)
            {
                Log("收藏文件失败: " + ex.Message);
                NoticeFavorite("收藏失败：" + ex.Message);
            }
        }


        // ============================================================
        //  点开一条收藏
        // ============================================================

        /// <summary>点收藏里的一条 = 重新打开那个分享（文件则顺带定位过去）。</summary>
        private void OnFavoriteItemClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var it = btn != null ? btn.Tag as FavoriteEntry : null;
            if (it == null || string.IsNullOrEmpty(it.ShareId)) return;

            HideFavoritePanel();

            var kind = (PanKind)it.PanKind;
            string url = string.IsNullOrEmpty(it.Url)
                ? BuildShareUrl(kind, it.ShareId, it.StartFid)
                : it.Url;

            if (it.IsFile)
            {
                // 解析成功之后要下钻定位到这个文件。
                _pendingFavoriteRelDir = it.RelDir ?? "";
                _pendingFavoriteName = it.Name ?? "";
                _pendingFavoriteFid = it.Fid ?? "";
            }
            else
            {
                _pendingFavoriteRelDir = null;
                _pendingFavoriteName = null;
                _pendingFavoriteFid = null;
            }

            LogDetail("从收藏夹打开：「" + (it.IsFile ? it.Name : it.ShareId) + "」");
            FillAndParse(url, it.Passcode);
        }

        /// <summary>
        /// 解析成功后的下钻定位（收藏的是**文件**时才需要）。
        /// 由 ParseShareCoreAsync 在解析成功后调用。
        /// </summary>
        internal void TryDrillToFavoriteTarget()
        {
            string relDir = _pendingFavoriteRelDir;
            string name = _pendingFavoriteName;
            string fid = _pendingFavoriteFid;
            if (string.IsNullOrEmpty(relDir) && string.IsNullOrEmpty(name)) return;

            _pendingFavoriteRelDir = null;
            _pendingFavoriteName = null;
            _pendingFavoriteFid = null;

            var ignored = DrillToFavoriteAsync(relDir, name, fid);
        }

        private async Task DrillToFavoriteAsync(string relDir, string fileName, string fileFid)
        {
            try
            {
                // 逐级下钻到那个文件所在的目录。
                var segs = (relDir ?? "").Replace('/', '\\')
                    .Split(new string[] { "\\" }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segs.Length; i++)
                {
                    string seg = segs[i];
                    // 名字匹配做两手准备：_pathList 里存的是目录原名，
                    // 而规范化过的名字可能与原名不同（带非法字符时）。
                    var dir = _files.FirstOrDefault(f => f.IsDir
                        && (f.Name == seg || PathUtil.SanitizeFileName(f.Name ?? "") == seg));
                    if (dir == null)
                    {
                        NoticeFavorite("收藏的文件所在目录找不到了，链接可能已经变了");
                        return;
                    }
                    _fidStack.Push(_curFid);
                    _curFid = dir.Fid;
                    _pathList.Add(dir.Name);
                    UpdatePathText();
                    await LoadDirAsync(dir.Fid);
                }

                // 定位并选中那个文件。
                // 优先按 fid（同一分享内稳定），其次按原名，最后按规范化后的名字。
                ShareFileItem item = null;
                if (!string.IsNullOrEmpty(fileFid))
                    item = _files.FirstOrDefault(f => !f.IsDir && f.Fid == fileFid);
                if (item == null)
                    item = _files.FirstOrDefault(f => !f.IsDir && f.Name == fileName);
                if (item == null)
                    item = _files.FirstOrDefault(f => !f.IsDir
                        && PathUtil.SanitizeFileName(f.Name ?? "") == PathUtil.SanitizeFileName(fileName ?? ""));
                if (item == null)
                {
                    NoticeFavorite("这个目录里没找到收藏的那个文件");
                    return;
                }
                try
                {
                    FileList.SelectedItem = item;
                    FileList.ScrollIntoView(item);
                }
                catch { }
                NoticeFavorite("已定位到：" + item.Name);
            }
            catch (Exception ex)
            {
                LogDetail("定位收藏文件失败: " + ex.Message);
            }
        }


        // ============================================================
        //  小工具
        // ============================================================

        /// <summary>
        /// 按网盘拼出分享链接。
        /// ⚠️ 域名必须对：夸克 pan.quark.cn、UC drive.uc.cn、阿里 aliyundrive.com、迅雷 pan.xunlei.com。
        /// </summary>
        internal static string BuildShareUrl(PanKind kind, string shareId, string startFid)
        {
            if (string.IsNullOrEmpty(shareId)) return "";
            string host;
            switch (kind)
            {
                case PanKind.Quark: host = "pan.quark.cn"; break;
                case PanKind.Uc: host = "drive.uc.cn"; break;
                case PanKind.Aliyun: host = "www.aliyundrive.com"; break;
                case PanKind.Xunlei: host = "pan.xunlei.com"; break;
                default: host = "pan.quark.cn"; break;
            }
            string url = "https://" + host + "/s/" + shareId;
            if (!string.IsNullOrEmpty(startFid) && startFid != "0")
                url += "/" + startFid;
            return url;
        }

        /// <summary>收藏相关的轻提示（走灵动岛，与登录提示同一套语言）。</summary>
        private void NoticeFavorite(string message)
        {
            try
            {
                RunOnUi(() =>
                {
                    ShowIslandNotice(message);
                });
            }
            catch { }
        }
    }
}
