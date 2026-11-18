using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui;

/// <summary>主窗口的交互：输入快照、按钮可用性、执行、二次确认、键盘快捷键。</summary>
public sealed partial class MainForm
{
    private const string EmptyResultHint = "（还没有执行任何命令。先选游戏目录，然后按「识别引擎」或「一键」。）";

    // ───────────────────────── 生命周期 ─────────────────────────

    private void OnFormLoad(object? sender, EventArgs e)
    {
        ApplyStartupOptions();
        ResolveForgeExecutable();
        RefreshAvailability();
        ActiveControl = _gamePathBox;
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        var message = new List<string> { "界面已就绪。" };
        if (File.Exists(_forgeExeBox.Text.Trim()))
        {
            message.Add("命令工具已找到。");
        }
        else
        {
            message.Add("没找到 forge.exe，请用 Alt+4 指定路径。");
        }

        if (!SnapshotInputs().HasGamePath)
        {
            message.Add("先按 Alt+1 填写游戏目录。");
        }

        foreach (var problem in _startup.Problems)
        {
            message.Add(problem);
        }

        Announce(string.Join(string.Empty, message));
    }

    private void ApplyStartupOptions()
    {
        if (!string.IsNullOrWhiteSpace(_startup.GamePath))
        {
            _gamePathBox.Text = _startup.GamePath;
        }

        if (!string.IsNullOrWhiteSpace(_startup.WorkspacePath))
        {
            _workspaceBox.Text = _startup.WorkspacePath;
        }

        if (!string.IsNullOrWhiteSpace(_startup.ForgeExePath))
        {
            _forgeExeBox.Text = _startup.ForgeExePath;
        }

        if (_startup.DryRun)
        {
            _dryRunCheck.Checked = true;
        }
    }

    private void ResolveForgeExecutable()
    {
        var current = _forgeExeBox.Text.Trim();
        if (current.Length > 0 && File.Exists(current))
        {
            return;
        }

        var resolution = ForgeExeLocator.Resolve(ForgeExeSearchOptions.CreateDefault(current));
        if (resolution.Found)
        {
            _forgeExeBox.Text = resolution.Path!;
            _toolTip.SetToolTip(_forgeExeBox, $"自动找到（{resolution.Source}）：{resolution.Path}");
            return;
        }

        _hintLabel.Text = @"没找到 forge.exe：点「forge.exe(&4)」旁的「浏览…」选 src\Forge.Cli\bin\Release\net8.0\forge.exe。";
        _toolTip.SetToolTip(_hintLabel, _hintLabel.Text);
    }

    // ───────────────────────── 输入与可用性 ─────────────────────────

    private GuiInputs SnapshotInputs()
        => GuiInputs.Create(
            _gamePathBox.Text,
            _workspaceBox.Text,
            _bepInExBox.Text,
            _forgeExeBox.Text,
            _dryRunCheck.Checked,
            _busy,
            _running,
            path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path),
            path => !string.IsNullOrWhiteSpace(path) && File.Exists(path),
            _speechLogBox.Text,
            _advancedCheck.Checked,
            _ghidraExeBox.Text,
            _advancedCheck.Checked ? TryGetFileSize(_ghidraExeBox.Text) : null,
            _forceCheck.Checked);

    private void RefreshAvailability()
    {
        var inputs = SnapshotInputs();

        foreach (var (kind, button) in _actionButtons)
        {
            var descriptor = ForgeCommands.Of(kind);
            var availability = GuiStateMachine.Evaluate(kind, inputs);
            button.Enabled = availability.Enabled;
            var tip = availability.Enabled ? descriptor.Description : availability.Reason ?? descriptor.Description;
            _toolTip.SetToolTip(button, tip);
            // 禁用按钮也能被读屏读到：把「为什么不能按」写进可访问描述。
            button.AccessibleDescription = availability.Enabled
                ? descriptor.AccessibleDescription
                : $"{descriptor.Title}当前不可用：{availability.Reason}";
        }

        var oneClick = GuiStateMachine.EvaluateOneClick(inputs);
        _oneClickButton.Enabled = oneClick.Enabled;
        _oneClickButton.AccessibleDescription = oneClick.Enabled
            ? "依次执行识别、分析、生成、构建四步；任一步失败就停下。不会安装到游戏目录。Alt 加 O。"
            : $"一键当前不可用：{oneClick.Reason}";
        _toolTip.SetToolTip(_oneClickButton, oneClick.Enabled ? "依次执行识别→分析→生成→构建（不安装）。Alt+O" : oneClick.Reason ?? string.Empty);

        var stop = GuiStateMachine.EvaluateStop(inputs);
        _stopButton.Enabled = stop.Enabled;
        _toolTip.SetToolTip(_stopButton, stop.Enabled ? "中止正在执行的命令（Esc）。" : "当前没有正在执行的操作。");

        if (!_busy)
        {
            _hintLabel.Text = GuiStateMachine.HintText(inputs);
            _toolTip.SetToolTip(_hintLabel, _hintLabel.Text);
        }
    }

    // ───────────────────────── 浏览对话框 ─────────────────────────

    private void BrowseFolder(TextBox target, string title, bool allowCreate)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = allowCreate,
        };

        var current = target.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            target.Text = dialog.SelectedPath;
            RefreshAvailability();
        }
    }

    private void BrowseFile(TextBox target, string title, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        var current = target.Text.Trim();
        if (current.Length > 0)
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
                dialog.FileName = Path.GetFileName(current);
            }
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            target.Text = dialog.FileName;
            RefreshAvailability();
        }
    }

    /// <summary>读取文件大小（字节）；文件不存在或读不到时返回 null，不抛异常。</summary>
    private static long? TryGetFileSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path.Trim());
            return info.Exists && info.Length > 0 ? info.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ───────────────────────── 执行 ─────────────────────────

    private async Task RunCommandAsync(ForgeCommandKind kind)
    {
        if (!TryBeginCommand(kind, out var inputs))
        {
            return;
        }

        await RunSequenceAsync(new[] { kind }, inputs);
    }

    private async Task RunOneClickAsync()
    {
        if (!TryBeginOneClick(out var inputs))
        {
            return;
        }

        await RunSequenceAsync(ForgeCommands.OneClickSequence, inputs);
    }

    private bool TryBeginCommand(ForgeCommandKind kind, out GuiInputs inputs)
    {
        inputs = SnapshotInputs();
        if (_busy)
        {
            Announce("正在执行其他操作，请等它完成或按 Esc 停止。");
            return false;
        }

        var reason = GuiStateMachine.ValidateBeforeRun(kind, inputs);
        if (reason is not null)
        {
            ShowBlocked(kind, reason);
            return false;
        }

        var descriptor = ForgeCommands.Of(kind);
        if (descriptor.RequiresConfirmation && !inputs.DryRun && !ConfirmWrite(descriptor, inputs))
        {
            _statusLabel.Text = $"{descriptor.Title}：已取消，没有执行。";
            Announce(_statusLabel.Text);
            return false;
        }

        return true;
    }

    private bool TryBeginOneClick(out GuiInputs inputs)
    {
        inputs = SnapshotInputs();
        if (_busy)
        {
            Announce("正在执行其他操作，请等它完成或按 Esc 停止。");
            return false;
        }

        var availability = GuiStateMachine.EvaluateOneClick(inputs);
        if (!availability.Enabled)
        {
            ShowBlocked(ForgeCommandKind.Detect, availability.Reason ?? "一键现在不能执行。");
            return false;
        }

        return true;
    }

    private async Task RunSequenceAsync(IReadOnlyList<ForgeCommandKind> sequence, GuiInputs inputs)
    {
        var multiStep = sequence.Count > 1;
        var allGood = true;
        var adapterlessStop = false;
        _cts = new CancellationTokenSource();

        try
        {
            for (var index = 0; index < sequence.Count; index++)
            {
                var kind = sequence[index];
                SetBusy(true, kind);
                var outcome = await ExecuteOneAsync(kind, inputs, _cts.Token).ConfigureAwait(true);
                PresentOutcome(kind, outcome, append: multiStep);

                if (!outcome.IsGood)
                {
                    // 已知引擎但没有内置适配器：不白跑后续构建，改为“高级分析 + 导出报告”。
                    if (multiStep
                        && kind == ForgeCommandKind.Analyze
                        && AdapterlessFlow.IsAdapterlessAnalyzeFailure(outcome.Envelope))
                    {
                        adapterlessStop = true;
                        var family = AdapterlessFlow.TryGetEngineFamily(outcome.Envelope);

                        if (!inputs.Advanced)
                        {
                            SetBusy(true, ForgeCommandKind.Analyze);
                            var advancedOutcome = await ExecuteOneAsync(
                                ForgeCommandKind.Analyze, inputs, _cts.Token, advancedOverride: true).ConfigureAwait(true);
                            PresentOutcome(ForgeCommandKind.Analyze, advancedOutcome, append: true);
                        }

                        SetBusy(true, ForgeCommandKind.Report);
                        var reportOutcome = await ExecuteOneAsync(
                            ForgeCommandKind.Report, inputs, _cts.Token, advancedOverride: true).ConfigureAwait(true);
                        PresentOutcome(ForgeCommandKind.Report, reportOutcome, append: true);
                        allGood = reportOutcome.IsGood;

                        var explanation = AdapterlessFlow.Explain(family);
                        _hintLabel.Text = explanation;
                        _toolTip.SetToolTip(_hintLabel, explanation);
                        Announce(explanation);
                        break;
                    }

                    allGood = false;
                    break;
                }

                if (_cts.IsCancellationRequested)
                {
                    allGood = false;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            allGood = false;
            _resultBox.Text = $"【界面错误】执行中断{Environment.NewLine}{Environment.NewLine}{ex.GetType().Name}：{ex.Message}{Environment.NewLine}";
            _resultBox.SelectionStart = 0;
            _statusLabel.Text = "界面错误：执行中断（详见结果区）";
            Announce("界面错误，执行中断。请到结果区查看错误信息。");
        }
        finally
        {
            SetBusy(false, null);
            try
            {
                _cts?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 释放失败不影响结果
            }

            _cts = null;
        }

        if (multiStep && !adapterlessStop)
        {
            var summary = allGood ? "一键完成：识别、分析、生成、构建都成功。" : "一键已停止：有步骤未成功，结果区里保留了每一步的输出。";
            _hintLabel.Text = summary;
            _toolTip.SetToolTip(_hintLabel, summary);
            Announce(summary);
        }
    }

    private async Task<ForgeRunOutcome> ExecuteOneAsync(ForgeCommandKind kind, GuiInputs inputs, CancellationToken cancellationToken, bool? advancedOverride = null)
    {
        var descriptor = ForgeCommands.Of(kind);
        var request = new ForgeRequest
        {
            Command = kind,
            GamePath = inputs.GamePath,
            WorkspacePath = inputs.WorkspacePath,
            BepInExPath = inputs.BepInExPath,
            SpeechLogPath = inputs.SpeechLogPath,
            Advanced = advancedOverride ?? inputs.Advanced,
            GhidraExecutablePath = inputs.GhidraExecutablePath,
            DryRun = inputs.DryRun,
            Force = inputs.Force,
        };

        var arguments = ForgeArgumentBuilder.Build(request);
        var commandLine = ForgeArgumentBuilder.Describe(arguments, "forge");

        _hintLabel.Text = $"正在执行「{descriptor.Title}」……按 Esc 或 Alt+X 可停止。";
        _toolTip.SetToolTip(_hintLabel, _hintLabel.Text);

        var progress = new Progress<ProcessLine>(OnProcessLine);
        return await _runner.RunAsync(inputs.ForgeExePath!, commandLine, arguments, progress, cancellationToken);
    }

    private void OnProcessLine(ProcessLine line)
    {
        if (!_busy)
        {
            return;
        }

        var text = line.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > 120)
        {
            text = text[..120] + "…";
        }

        var title = _running is { } running ? ForgeCommands.Of(running).Title : "操作";
        _hintLabel.Text = $"正在执行「{title}」…… 最近输出（{(line.IsError ? "错误流" : "标准流")}）：{text}";
        _toolTip.SetToolTip(_hintLabel, _hintLabel.Text);
    }

    private void PresentOutcome(ForgeCommandKind kind, ForgeRunOutcome outcome, bool append)
    {
        var descriptor = ForgeCommands.Of(kind);
        var text = ForgeResultTextBuilder.Build(outcome);

        var existing = _resultBox.Text;
        var keepExisting = append && existing.Length > 0 && !string.Equals(existing, EmptyResultHint, StringComparison.Ordinal);
        _resultBox.Text = keepExisting
            ? existing + Environment.NewLine + "————————" + Environment.NewLine + text
            : text;
        _resultBox.SelectionStart = 0;
        _resultBox.SelectionLength = 0;
        _resultBox.ScrollToCaret();

        var headline = ForgeResultTextBuilder.BuildHeadline(outcome);
        _statusLabel.Text = headline;

        AddHistory(kind, descriptor, outcome, text);

        var announcement = outcome.IsGood
            ? headline
            : headline + "。按 Tab 到结果区听详细信息。";
        Announce(announcement);

        if (Visible)
        {
            // 焦点兜底：UIA 通知在部分读屏（实测 NVDA + WinForms）不会念，
            // 所以执行完成后把焦点放到结果区，让读屏按“控件获得焦点”念出结果首段；
            // 用户按 Shift+Tab 即可回到操作区。失败时同样走这里（更早的版本只处理失败）。
            _resultBox.Focus();
            _resultBox.SelectionStart = 0;
            _resultBox.SelectionLength = 0;
        }
    }

    private void AddHistory(ForgeCommandKind kind, ForgeCommandDescriptor descriptor, ForgeRunOutcome outcome, string text)
    {
        var label = $"{DateTime.Now:HH:mm:ss}　{descriptor.Title}：{outcome.StatusText}（退出码 {outcome.ExitCode}）";
        _history.Insert(0, new HistoryEntry(label, text, outcome.Status));
        _historyList.Items.Insert(0, label);
        while (_history.Count > 50)
        {
            _history.RemoveAt(_history.Count - 1);
            _historyList.Items.RemoveAt(_historyList.Items.Count - 1);
        }
    }

    private void ShowSelectedHistory()
    {
        var index = _historyList.SelectedIndex;
        if (index < 0 || index >= _history.Count)
        {
            Announce("没有选中执行记录。");
            return;
        }

        var entry = _history[index];
        _resultBox.Text = entry.Text;
        _resultBox.SelectionStart = 0;
        _resultBox.SelectionLength = 0;
        _resultBox.ScrollToCaret();
        _statusLabel.Text = entry.Label;
        Announce($"已调出记录：{entry.Label}。");
    }

    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        ShowSelectedHistory();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void SetBusy(bool busy, ForgeCommandKind? running)
    {
        _busy = busy;
        _running = running;
        _progress.Visible = busy;
        UseWaitCursor = busy;

        foreach (var box in new[] { _gamePathBox, _workspaceBox, _bepInExBox, _forgeExeBox })
        {
            box.ReadOnly = busy;
        }

        _dryRunCheck.Enabled = !busy;
        foreach (var browse in _browseButtons)
        {
            browse.Enabled = !busy;
        }

        RefreshAvailability();

        if (!busy)
        {
            _progress.Visible = false;
        }
    }

    private void StopCurrent()
    {
        if (!_busy)
        {
            _hintLabel.Text = "当前没有正在执行的操作。";
            _toolTip.SetToolTip(_hintLabel, _hintLabel.Text);
            Announce("当前没有正在执行的操作。");
            return;
        }

        RequestStop();
    }

    private void RequestStop()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            _hintLabel.Text = "正在停止……（连同子进程一起结束）";
            _toolTip.SetToolTip(_hintLabel, _hintLabel.Text);
            Announce("正在停止。");
        }
        catch (ObjectDisposedException)
        {
            // 刚好执行完并释放了令牌，视为已经停止。
        }
    }

    private void ClearResult()
    {
        _resultBox.Text = "（结果已清空。执行记录仍然保留在下方列表里，选中后按回车可以调回来。）";
        _resultBox.SelectionStart = 0;
        Announce("结果区已清空，执行记录还在。");
    }

    private void AnnounceAgain()
    {
        var text = _resultBox.Text;
        if (text.Length > 400)
        {
            text = text[..400] + "……（还有更多，请到结果区阅读）";
        }

        Announce(_statusLabel.Text + "。" + text);
        if (_resultBox.CanFocus)
        {
            _resultBox.Focus();
            _resultBox.SelectionStart = 0;
            _resultBox.SelectionLength = 0;
        }
    }

    private void ShowBlocked(ForgeCommandKind kind, string reason)
    {
        var title = ForgeCommands.Of(kind).Title;
        _statusLabel.Text = $"{title}：现在不能执行。";
        _hintLabel.Text = reason;
        _toolTip.SetToolTip(_hintLabel, reason);
        Announce($"{title}现在不能执行：{reason}");

        var availability = GuiStateMachine.Evaluate(kind, SnapshotInputs());
        switch (availability.BlockingKind)
        {
            case BlockingKind.MissingGamePath:
            case BlockingKind.GamePathNotFound:
                _gamePathBox.Focus();
                _gamePathBox.SelectAll();
                break;
            case BlockingKind.MissingExecutable:
                _forgeExeBox.Focus();
                _forgeExeBox.SelectAll();
                break;
        }
    }

    private bool ConfirmWrite(ForgeCommandDescriptor descriptor, GuiInputs inputs)
    {
        var lines = new List<string>
        {
            descriptor.Title,
            string.Empty,
            "目标游戏目录：",
            inputs.GamePath ?? "（未指定）",
            string.Empty,
            descriptor.Description,
            string.Empty,
        };

        lines.Add(descriptor.Kind == ForgeCommandKind.Rollback
            ? "回滚会按安装清单移除补丁并还原备份。"
            : "安装前会逐文件备份；之后可以用「回滚补丁」还原。");
        lines.Add(string.Empty);
        lines.Add("确定继续吗？");

        var answer = MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            $"确认{descriptor.Title}",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        return answer == DialogResult.Yes;
    }

    private void Announce(string? text) => _announcer.Announce(text);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                StopCurrent();
                return true;

            case Keys.F5:
                _ = RunOneClickAsync();
                return true;

            case Keys.Control | Keys.Shift | Keys.R:
                AnnounceAgain();
                return true;

            case Keys.Control | Keys.L:
                ClearResult();
                return true;

            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
