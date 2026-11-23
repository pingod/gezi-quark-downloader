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

// ---- 面板里的「就地改名」控件（2026-11-15 用户要求） ----
//
// 用户原话：
//   「我要做的重命名你也没做好呀，我又见成命名之后对应文件的名称我不就是能够进行修改了吗。
//     在名字那里应该会显示鼠标光标的竖杠来表示这个地方可以修改了」
//   「我希望右键之会有重命名选项点击之后就可以进行编辑了」
//
// ⇒ 交互：**就地编辑（inline edit）**，不是弹框。
//    右键 → 「重命名」→ 名字那一行**原地变成输入框**，光标竖杠在名字里闪，
//    旧文字已经全选好（直接打字就是覆盖），回车提交 / Esc 取消 / 点到别处也提交。
//
//    为什么不弹框：弹框是「换名字」的旧式做法（上一版就是这么做的，被用户指出来了）。
//    就地编辑是 Windows 资源管理器、Figma、VS Code 的通用做法 ——
//    用户看到**光标竖杠**的那一刻就已经知道「这里可以改」，不需要额外的对话框打断。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace GeZi
{
    /// <summary>
    /// 面板行里的「可编辑名称」控件：平时显示一段文字（带省略号），
    /// 进入编辑态时原地换成输入框（光标竖杠 + 全选），提交后换回文字。
    /// </summary>
    internal sealed class InlineNameEditor : Grid
    {
        /// <summary>当前有几个就地改名正在进行（供 Esc 判定用）。</summary>
        private static int _editingCount;

        /// <summary>有没有正在进行的就地改名。</summary>
        /// <remarks>
        /// ⚠️ Esc 的坑：窗口用的是 <c>PreviewKeyDown</c>（**隧道**事件，由外往里），
        /// 所以窗口的处理器**先于**输入框的 KeyDown 收到 Esc。
        /// 如果窗口不认这个标志，Esc 会被当成「关掉解析历史面板」，取消不了改名。
        /// </remarks>
        internal static bool AnyEditing
        {
            get { return _editingCount > 0; }
        }

        private readonly TextBlock _label;
        private readonly TextBox _box;
        private readonly Func<string, bool> _onCommit;
        private readonly Action _onCancel;

        /// <summary>最近一次生效的名字（用来判断「用户到底改没改」）。</summary>
        private string _origin;

        /// <summary>当前是不是编辑态。</summary>
        private bool _editing;

        /// <summary>外面那层「整行可点」的按钮。</summary>
        private ButtonBase _host;

        /// <summary>那层按钮的点击处理器（编辑期间临时摘掉，免得改名被当成「打开这条」）。</summary>
        private RoutedEventHandler _hostClick;

        /// <summary>点击处理器当前是不是已经被摘掉了。</summary>
        private bool _hostDetached;

        private InlineNameEditor(
            string displayText,
            string editText,
            string styleKey,
            Func<string, bool> onCommit,
            Action onCancel)
        {
            _origin = editText ?? "";
            _onCommit = onCommit;
            _onCancel = onCancel;

            _label = new TextBlock
            {
                Text = displayText ?? "",
                Style = Application.Current.TryFindResource(styleKey) as Style,
                VerticalAlignment = VerticalAlignment.Center,
                // ⚠️ 2026-11-15 用户原话：「我不需要点击名字后可以修改，
                //    我只要右键选择重命名后才可以修改。」
                //    ⇒ 名字平时就是**普通文字**：鼠标停在上面是普通箭头，
                //    点它 = 点整行 = 重新打开这条分享（原来的行为，别改）。
                //    所以这里**不设** IBeam 光标，也**不**监听点击进编辑。
            };

            _box = new TextBox
            {
                Text = _origin,
                Style = Application.Current.TryFindResource("InlineEditTextBox") as Style,
                // 名字很长时不要横向滚动条（面板行高是固定的，滚动条会把行撑高）；
                // 内容照样可以横向滚动（光标能移过去）。
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Visibility = Visibility.Collapsed,
            };

            Children.Add(_label);
            Children.Add(_box);

            Wire();
        }

        /// <summary>
        /// 造一个可编辑的名称区域。
        /// </summary>
        /// <param name="displayText">平时显示在行里的文字。</param>
        /// <param name="editText">进入编辑态时输入框里的初始内容（一般是「要保存的那个名字」）。</param>
        /// <param name="styleKey">平时文字用的样式键（HistoryNameText）。</param>
        /// <param name="onCommit">
        /// 提交回调：传入新名字（已 Trim）。返回 true 表示保存成功；
        /// 此时控件会**就地**把行里的文字改成新名字（不重建面板，点了哪一行不会被打断）。
        /// </param>
        /// <param name="onCancel">取消回调（Esc）。</param>
        internal static InlineNameEditor Create(
            string displayText,
            string editText,
            string styleKey,
            Func<string, bool> onCommit,
            Action onCancel)
        {
            return new InlineNameEditor(displayText, editText, styleKey, onCommit, onCancel);
        }

        /// <summary>
        /// 告诉这个控件：你外面那层「整行可点」的按钮是谁。
        /// </summary>
        /// <remarks>
        /// ⚠️ 必须告诉它，否则改名时会出这个 bug：
        ///   外层 Button 在鼠标抬起时抛 Click ⇒ 整行被当成「重新打开这条」⇒ 面板当场关掉。
        /// 编辑期间把它的点击处理器摘掉，就不会误触发。
        /// </remarks>
        internal void AttachHostButton(ButtonBase host, RoutedEventHandler clickHandler)
        {
            _host = host;
            _hostClick = clickHandler;
            _hostDetached = false;
        }

        /// <summary>进入编辑态（名字原地变成输入框，光标竖杠闪起来）。</summary>
        internal void BeginEdit()
        {
            if (_editing) return;
            _editing = true;
            _editingCount++;
            _box.Text = _origin;

            // ⚠️ 2026-11-15 用户原话：「光标的位置不对啊，默认光标不应该在最右边吗」
            //    ⇒ 光标竖杠默认落在文字**最末尾**；旧文字全选，直接打字就是覆盖
            //    （Windows 资源管理器就地重命名的同一个习惯）。
            //    这里在还没拿到焦点时就把选区钉好：TextBox 失去焦点时选区会被清，
            //    等拿到焦点再选，容易被右键菜单收尾抢焦点那一拍冲掉。
            _box.CaretIndex = _box.Text.Length;
            _box.Select(0, _box.Text.Length);

            _label.Visibility = Visibility.Collapsed;
            _box.Visibility = Visibility.Visible;

            // 编辑期间把外层那个「整行可点」的按钮的点击摘掉（见 AttachHostButton 的说明）。
            if (_host != null && _hostClick != null && !_hostDetached)
            {
                _host.Click -= _hostClick;
                _hostDetached = true;
            }

            // 焦点**延后一拍**再给：右键菜单刚点完时，菜单收尾会把焦点还给面板，
            // 不延后的话刚聚焦就被抢回去（表现就是「点了重命名但没反应」）。
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try
                {
                    if (!_editing) return;
                    _box.Focus();
                    // 焦点可能被菜单收尾抢走过，这里再钉一次（光标始终在最右）。
                    _box.CaretIndex = _box.Text.Length;
                    _box.Select(0, _box.Text.Length);
                }
                catch { }
            }));
        }

        // ---------------- 内部 ----------------

        private void Wire()
        {
            // ⚠️ 2026-11-15 用户原话：「我不需要点击名字后可以修改，
            //    我只要右键选择重命名后才可以修改。」
            //    ⇒ 名字平时是**普通文字**，**不监听点击**。
            //    点它 = 点整行 = 重新打开这条分享（原来的行为，别改）。
            //    所以这里刻意**没有** _label.PreviewMouseLeftButtonDown 处理器。

            // ⚠️ 输入框的点击**完全不接管**，交给 WPF 原生处理。
            //    原生 TextBox 会自己做两件事：
            //      ① 把光标放到你点的那一个字前面（点哪光标就在哪）；
            //      ② 按下时接管鼠标（拖选、选区）。
            //    之前这里手写了 `GetCharacterIndexFromPoint` 自己算光标位置，
            //    结果是「不管点哪光标都停在同一个地方」—— 那是自己算错的，
            //    不是 WPF 的问题。原生处理才是对的。
            //
            //    外层那个「整行可点」的 Button 会不会误触发 Click？
            //    不会：编辑期间它自己的点击处理器已经被摘掉了（见 AttachHostButton）。

            _box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Return)
                {
                    e.Handled = true;
                    Commit();
                }
                else if (e.Key == Key.Escape)
                {
                    // ⚠️ Esc 必须在这里吃掉，否则会冒泡到 MainWindow.PreviewKeyDown
                    //    ⇒ 整个「解析历史」面板被关掉（用户只是想取消改名）。
                    e.Handled = true;
                    Cancel();
                }
            };

            // 点走（失焦）也提交 —— 和文件管理器一个习惯。
            _box.LostFocus += (s, e) =>
            {
                if (_editing) Commit();
            };
        }

        /// <summary>提交：名字没变就什么都不做；变了就交给上层保存并就地更新文字。</summary>
        private void Commit()
        {
            if (!_editing) return;
            string newName = (_box.Text ?? "").Trim();
            if (newName.Length == 0)
            {
                Cancel();
                return;
            }
            if (string.Equals(newName, _origin, StringComparison.Ordinal))
            {
                // 没改 ⇒ 不写盘、不刷日志，直接收起输入框。
                EndEdit();
                return;
            }

            // 先把编辑态关掉：回调里可能要刷新界面，那会再触发一次 LostFocus，
            // 不先关就会走两遍提交逻辑。
            _editing = false;

            bool ok = false;
            try { ok = _onCommit != null && _onCommit(newName); }
            catch { ok = false; }

            if (ok)
            {
                _origin = newName;
                _box.Text = newName;
                _label.Text = newName;
            }
            else
            {
                _box.Text = _origin;
            }
            EndEdit();
        }

        /// <summary>取消：名字保持原样。</summary>
        private void Cancel()
        {
            if (!_editing) return;
            EndEdit();
            _box.Text = _origin;
            try { _onCancel?.Invoke(); } catch { }
        }

        /// <summary>退出编辑态：输入框收起、名字显示出来。</summary>
        private void EndEdit()
        {
            _editing = false;
            if (_editingCount > 0) _editingCount--;
            _box.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Visible;

            // 把外层按钮的点击恢复原样。
            if (_host != null && _hostClick != null && _hostDetached)
            {
                _host.Click += _hostClick;
                _hostDetached = false;
            }
        }
    }
}
