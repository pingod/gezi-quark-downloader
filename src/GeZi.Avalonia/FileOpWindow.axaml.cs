// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// GPL-3.0-or-later — 详见仓库根目录 LICENSE

using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GeZi.Providers.Cloud;

namespace GeZi.Avalonia
{
    /// <summary>
    /// 文件操作对话框（新建文件夹 / 重命名 / 移动 / 删除确认 / 创建分享）。
    ///
    /// 用一个窗口承载 5 种操作，而不是 5 个独立窗口：
    /// 它们共享"标题 + 一个输入区 + 确定/取消"的骨架，
    /// 差异只是表单字段组合。5 个窗口会产生大量重复代码。
    /// </summary>
    public partial class FileOpWindow : Window
    {
        public enum OpKind
        {
            CreateFolder,
            Rename,
            Move,
            Delete,
            CreateShare,
        }

        /// <summary>操作结果（用户点了确定）。</summary>
        public sealed class Result
        {
            public OpKind Kind { get; set; }

            /// <summary>新建文件夹/重命名用的名称。</summary>
            public string Name { get; set; } = "";

            /// <summary>移动的目标目录 id。</summary>
            public string TargetId { get; set; } = "";

            /// <summary>分享有效期（天；0 = 永久）。</summary>
            public int ExpireDays { get; set; }

            /// <summary>分享提取码（空 = 平台生成）。</summary>
            public string Passcode { get; set; } = "";
        }

        private TextBlock _titleText;
        private TextBlock _subText;
        private TextBlock _nameLabel;
        private TextBlock _hint;
        private TextBlock _warnText;
        private TextBox _nameBox;
        private TextBox _passcodeBox;
        private TextBox _targetBox;
        private TextBox _resultBox;
        private ComboBox _expiryBox;
        private Button _okBtn;
        private StackPanel _namePanel;
        private StackPanel _sharePanel;
        private StackPanel _movePanel;
        private StackPanel _resultPanel;

        private OpKind _kind;
        private Result _result;

        public FileOpWindow() : this(OpKind.CreateFolder, 1, "", "") { }

        /// <param name="kind">操作类型</param>
        /// <param name="count">涉及文件数（用于提示文案）</param>
        /// <param name="existingName">重命名时的原名称</param>
        /// <param name="currentFolderId">当前所在目录（移动的默认目标）</param>
        public FileOpWindow(OpKind kind, int count, string existingName, string currentFolderId)
        {
            _kind = kind;

            AvaloniaXamlLoader.Load(this);

            _titleText = this.FindControl<TextBlock>("TitleText");
            _subText = this.FindControl<TextBlock>("SubText");
            _nameLabel = this.FindControl<TextBlock>("NameLabel");
            _hint = this.FindControl<TextBlock>("Hint");
            _warnText = this.FindControl<TextBlock>("WarnText");
            _nameBox = this.FindControl<TextBox>("NameBox");
            _passcodeBox = this.FindControl<TextBox>("PasscodeBox");
            _targetBox = this.FindControl<TextBox>("TargetBox");
            _resultBox = this.FindControl<TextBox>("ResultBox");
            _expiryBox = this.FindControl<ComboBox>("ExpiryBox");
            _okBtn = this.FindControl<Button>("OkBtn");
            _namePanel = this.FindControl<StackPanel>("NamePanel");
            _sharePanel = this.FindControl<StackPanel>("SharePanel");
            _movePanel = this.FindControl<StackPanel>("MovePanel");
            _resultPanel = this.FindControl<StackPanel>("ResultPanel");

            Configure(kind, count, existingName, currentFolderId);
        }

        private void Configure(OpKind kind, int count, string existingName, string currentFolderId)
        {
            _namePanel.IsVisible = false;
            _sharePanel.IsVisible = false;
            _movePanel.IsVisible = false;
            _resultPanel.IsVisible = false;

            switch (kind)
            {
                case OpKind.CreateFolder:
                    _titleText.Text = "新建文件夹";
                    _subText.Text = "在当前目录下创建";
                    _namePanel.IsVisible = true;
                    _nameLabel.Text = "文件夹名称";
                    _nameBox.Text = "";
                    _okBtn.Content = "创建";
                    break;

                case OpKind.Rename:
                    _titleText.Text = "重命名";
                    _subText.Text = string.IsNullOrEmpty(existingName)
                        ? "输入新名称" : "原名称：" + existingName;
                    _namePanel.IsVisible = true;
                    _nameLabel.Text = "新名称";
                    _nameBox.Text = existingName ?? "";
                    _okBtn.Content = "重命名";
                    break;

                case OpKind.Move:
                    _titleText.Text = "移动";
                    _subText.Text = string.Format("将 {0} 项移动到其他目录", count);
                    _movePanel.IsVisible = true;
                    _targetBox.Text = "0";
                    _okBtn.Content = "移动";
                    break;

                case OpKind.Delete:
                    _titleText.Text = "确认删除";
                    _subText.Text = string.Format("将删除 {0} 项，删除后可在网盘网页版回收站恢复。", count);
                    _warnText.Text = "⚠️ 此操作会从你的网盘移除文件";
                    _okBtn.Content = "删除";
                    _okBtn.Classes.Add("danger");
                    break;

                case OpKind.CreateShare:
                    _titleText.Text = "创建分享";
                    _subText.Text = string.Format("分享 {0} 项", count);
                    _sharePanel.IsVisible = true;
                    _expiryBox.SelectedIndex = 0;
                    _okBtn.Content = "创建分享";
                    break;
            }
        }

        /// <summary>创建分享成功后，把结果展示在窗口里（不直接关闭）。</summary>
        public void ShowShareResult(ShareCreation share)
        {
            _sharePanel.IsVisible = false;
            _resultPanel.IsVisible = true;
            _okBtn.Content = "完成";

            string url = share?.Url ?? "";
            string code = share?.Passcode ?? "";

            _resultBox.Text = string.IsNullOrEmpty(code)
                ? url
                : url + Environment.NewLine + "提取码：" + code;

            _subText.Text = "分享已创建";
            _hint.Text = string.IsNullOrEmpty(code)
                ? "链接已复制到剪贴板"
                : "链接和提取码已复制到剪贴板";

            CopyToClipboard(_resultBox.Text);
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            // 分享结果页：点"完成"直接关闭
            if (_resultPanel.IsVisible)
            {
                Close(_result);
                return;
            }

            switch (_kind)
            {
                case OpKind.CreateFolder:
                case OpKind.Rename:
                {
                    string name = (_nameBox.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        _hint.Text = "名称不能为空";
                        return;
                    }
                    if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    {
                        _hint.Text = "名称不能包含 / \\ : * ? \" < > | 这些字符";
                        return;
                    }
                    _result = new Result { Kind = _kind, Name = name };
                    break;
                }

                case OpKind.Move:
                {
                    string target = (_targetBox.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(target))
                    {
                        _hint.Text = "目标目录不能为空";
                        return;
                    }
                    _result = new Result { Kind = _kind, TargetId = target };
                    break;
                }

                case OpKind.Delete:
                    _result = new Result { Kind = _kind };
                    break;

                case OpKind.CreateShare:
                {
                    int days = _expiryBox.SelectedIndex switch
                    {
                        1 => 1,
                        2 => 7,
                        3 => 30,
                        _ => 0,
                    };
                    _result = new Result
                    {
                        Kind = _kind,
                        ExpireDays = days,
                        Passcode = (_passcodeBox.Text ?? "").Trim(),
                    };
                    break;
                }
            }

            Close(_result);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close(null);

        private async void OnCopyClick(object sender, RoutedEventArgs e)
        {
            await CopyToClipboard(_resultBox.Text);
            _hint.Text = "已复制";
        }

        private async System.Threading.Tasks.Task CopyToClipboard(string text)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null && !string.IsNullOrEmpty(text))
                    await clipboard.SetTextAsync(text);
            }
            catch
            {
                // 剪贴板不可用不影响主流程
            }
        }
    }
}
