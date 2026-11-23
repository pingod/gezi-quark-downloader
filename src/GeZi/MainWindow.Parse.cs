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
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Providers.Shared;

namespace GeZi
{
    /// <summary>
    /// 「解析」入口的补充逻辑：分享口令解析 + 解析过程的灵动岛提示。
    /// <para>
    /// 拆出来单独放一个 partial，是因为这块跟目录浏览、网盘切换都无关，
    /// 混在 <c>MainWindow.Browse.cs</c> 里会越来越难找。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 解析过程的轻提示（灵动岛）。
        /// <para>
        /// ⚠️ 用轻提示而不是弹窗：解析是高频操作，弹窗会挡住界面、还要点一下才能关，
        /// 连续解析几个分享时非常烦人。真正需要用户决策的场合才用 <c>AppDialog</c>。
        /// </para>
        /// </summary>
        private void NoticeParse(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            RunOnUi(() =>
            {
                try
                {
                    ShowIslandNotice(message);
                }
                catch { }
            });
        }

        /// <summary>
        /// 解析分享口令。
        /// <para>
        /// 触发场景：用户粘进来的东西既不是四家网盘的链接，也不是 <c>/s/xxx</c> 形态的
        /// 分享 ID —— 但看起来像一句口令（中文口令、含「提取码/口令/pan.」等关键词、
        /// 或 6~40 位的纯字母数字短码）。
        /// </para>
        /// <para>
        /// 流程：先判断像不像口令 → 不像就提示「请输入链接和口令」；
        /// 像就去问口令解析服务（迅雷官方 / 星海）→ 成功就把链接填回输入框再走正常解析，
        /// 失败就把原因用灵动岛报出来。
        /// </para>
        /// </summary>
        private async Task ParseShareCommandAsync(string text)
        {
            if (!ShareCommandResolver.LooksLikeCommand(text))
            {
                Log("输入的既不是分享链接也不是口令，已忽略。");
                NoticeParse("请输入分享链接或口令");
                return;
            }

            long t0 = BusyClock();
            if (!BeginBusy(ParseBtn, "解析口令"))
                return;
            SetBusy(true);
            try
            {
                var r = await ShareCommandResolver.ResolveAsync(text).ConfigureAwait(true);
                if (r == null || !r.Success || string.IsNullOrEmpty(r.Url))
                {
                    string reason = (r != null && !string.IsNullOrEmpty(r.Reason))
                        ? r.Reason
                        : "这条口令没能解析出分享链接";
                    Log("口令解析失败：" + reason + "（口令长度 " + (text ?? "").Trim().Length + " 字）。");
                    NoticeParse(reason);
                    return;
                }

                // 解析成功：把链接填回输入框，让用户看得见"变成了什么"，
                // 然后走正常的分享解析流程。
                Log("口令已解析成" + PanUrlParser.DisplayNameOf(r.Kind ?? PanKind.Quark) + "分享链接。");
                if (LinkBox != null)
                {
                    LinkBox.Text = r.Url;
                    LinkBox.CaretIndex = LinkBox.Text.Length;
                }
                if (!string.IsNullOrEmpty(r.Passcode) && PassBox != null)
                    PassBox.Text = r.Passcode;

                NoticeParse("口令已解析，正在打开分享");
                // alreadyBusy=true：这个按钮的忙态是本方法开的，别让 ParseShareCoreAsync
                // 再 BeginBusy 一次（它见到按钮已在忙会直接 return，等于口令解析白跑）。
                await ParseShareCoreAsync(true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("口令解析出错: " + ex.Message);
                NoticeParse("口令解析失败，请稍后再试");
            }
            finally
            {
                SetBusy(false);
                await EndBusyAsync(ParseBtn, t0);
            }
        }
    }
}
