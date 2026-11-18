using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui;

/// <summary>
/// 主窗口。布局全部手写（没有 designer 文件），原因有两个：
/// 1) 读屏树由控件添加顺序与 TabIndex 决定，代码里能一眼看清顺序；
/// 2) designer 生成的 resx 在这个仓库里没人愿意审。
/// 交互逻辑在 MainForm.Commands.cs。
/// </summary>
public sealed partial class MainForm : Form
{
    private const string WindowTitle = "GalA11y Forge — 无障碍补丁工坊";

    private readonly GuiStartupOptions _startup;
    private readonly ForgeProcessRunner _runner = new();
    private readonly ToolTip _toolTip = new();
    private readonly List<HistoryEntry> _history = new();
    private readonly Dictionary<ForgeCommandKind, Button> _actionButtons = new();
    private readonly List<Button> _browseButtons = new();

    private readonly TextBox _gamePathBox = new();
    private readonly TextBox _workspaceBox = new();
    private readonly TextBox _bepInExBox = new();
    private readonly TextBox _speechLogBox = new();
    private readonly TextBox _forgeExeBox = new();
    private readonly TextBox _ghidraExeBox = new();
    private readonly CheckBox _dryRunCheck = new();
    private readonly CheckBox _advancedCheck = new();
    private readonly CheckBox _forceCheck = new();
    private readonly Label _hintLabel = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _resultBox = new();
    private readonly ListBox _historyList = new();

    private Button _oneClickButton = null!;
    private Button _stopButton = null!;
    private Button _clearButton = null!;
    private Announcer _announcer = null!;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private ForgeCommandKind? _running;

    public MainForm(GuiStartupOptions? startup = null)
    {
        _startup = startup ?? new GuiStartupOptions();

        SuspendLayout();
        BuildUi();
        ResumeLayout(performLayout: true);

        _announcer = new Announcer(_statusLabel);
        Load += OnFormLoad;
        Shown += OnFormShown;
        FormClosing += OnFormClosing;
    }

    /// <summary>最近一次执行记录；<see cref="Text"/> 是完整结果，供「回车调出」。</summary>
    private sealed record HistoryEntry(string Label, string Text, ForgeRunStatus Status);

    private void BuildUi()
    {
        Text = WindowTitle;
        AccessibleName = "无障碍补丁工坊主窗口";
        AccessibleDescription =
            "GalA11y Forge 主界面。先填游戏目录（Alt+1），再按按钮或 Alt+字母执行；结果在下方只读文本区。";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(1000, 760);
        Size = new Size(1060, 800);
        KeyPreview = true;
        DoubleBuffered = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            TabIndex = 0,
            Padding = new Padding(10, 8, 10, 10),
            AccessibleName = "主布局",
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 332F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        root.Controls.Add(BuildInputGroup(), 0, 0);
        root.Controls.Add(BuildActionGroup(), 0, 1);
        root.Controls.Add(BuildStatusArea(), 0, 2);
        root.Controls.Add(BuildResultGroup(), 0, 3);

        Controls.Add(root);

        _toolTip.AutoPopDelay = 30000;
        _toolTip.InitialDelay = 500;
        _toolTip.ReshowDelay = 100;
        _toolTip.ShowAlways = true;
    }

    private GroupBox BuildInputGroup()
    {
        var group = new GroupBox
        {
            Text = "① 输入（Alt+数字直接跳到对应输入框）",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 24, 10, 8),
            TabIndex = 0,
            AccessibleName = "输入区",
            AccessibleDescription = "五个路径输入框（游戏目录、工作区、BepInEx 包、forge.exe、语音日志）、一个可选的分析程序输入框和三个复选框（演练模式、高级分析、允许强制）。",
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            TabIndex = 0,
            AccessibleName = "输入控件",
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        for (var i = 0; i < 9; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        }

        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _gamePathBox.PlaceholderText = @"例如 D:\Steam\steamapps\common\游戏目录";
        AddPathRow(
            table,
            row: 0,
            labelText: "游戏目录(&1)",
            accessibleName: "游戏目录",
            box: _gamePathBox,
            description: "要处理的 Galgame 安装目录。只读命令不会改它；安装与回滚会写它。Alt 加 1 跳到这个输入框。",
            browseText: "浏览…",
            browseAccessibleName: "浏览游戏目录",
            browseDescription: "打开文件夹选择框，选择游戏安装目录。",
            onBrowse: (_, _) => BrowseFolder(_gamePathBox, "选择游戏安装目录", allowCreate: false));

        _workspaceBox.PlaceholderText = @"留空 = %LOCALAPPDATA%\GalA11yForge";
        AddPathRow(
            table,
            row: 1,
            labelText: "工作区(&2)",
            accessibleName: "工作区",
            box: _workspaceBox,
            description: "分析结果、生成的补丁工程与日志都放这里；留空则用默认目录。Alt 加 2 跳到这个输入框。",
            browseText: "浏览…",
            browseAccessibleName: "浏览工作区目录",
            browseDescription: "打开文件夹选择框，选择或新建工作区目录。",
            onBrowse: (_, _) => BrowseFolder(_workspaceBox, "选择工作区目录", allowCreate: true));

        _bepInExBox.PlaceholderText = "可选；Unity 游戏需要，Ren'Py 一般不需要";
        AddPathRow(
            table,
            row: 2,
            labelText: "BepInEx 包(&3)",
            accessibleName: "BepInEx 包",
            box: _bepInExBox,
            description: "可选的 BepInEx 压缩包或 DLL 路径，只有 Unity 游戏需要。Alt 加 3 跳到这个输入框。",
            browseText: "浏览…",
            browseAccessibleName: "浏览 BepInEx 包",
            browseDescription: "打开文件选择框，选择 BepInEx 压缩包或 dll。",
            onBrowse: (_, _) => BrowseFile(
                _bepInExBox,
                "选择 BepInEx 包",
                "BepInEx 包 (*.zip;*.dll)|*.zip;*.dll|所有文件 (*.*)|*.*"));

        _forgeExeBox.PlaceholderText = @"留空 = 自动查找（GUI 同目录 / 上一级 app\ / 仓库产物 / PATH）";
        AddPathRow(
            table,
            row: 3,
            labelText: "forge.exe(&4)",
            accessibleName: "forge.exe 路径",
            box: _forgeExeBox,
            description: "命令行工具 forge.exe 的完整路径；留空会自动在仓库与 PATH 里查找。Alt 加 4 跳到这个输入框。",
            browseText: "浏览…",
            browseAccessibleName: "浏览 forge.exe",
            browseDescription: "打开文件选择框，选择 forge.exe。",
            onBrowse: (_, _) => BrowseFile(
                _forgeExeBox,
                "选择 forge.exe",
                "forge.exe|forge.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"));

        _speechLogBox.PlaceholderText = "可选；自检时填游戏运行日志，自动检查补丁有没有读出台词";
        AddPathRow(
            table,
            row: 4,
            labelText: "语音日志(&5)",
            accessibleName: "语音日志",
            box: _speechLogBox,
            description: "可选的游戏运行日志（例如 BepInEx\\LogOutput.log 或 Ren'Py 的 a11y_speech.log）；填了以后「安装后自检」会额外统计补丁到底读出了多少句台词。Alt 加 5 跳到这个输入框。",
            browseText: "浏览…",
            browseAccessibleName: "浏览语音日志",
            browseDescription: "打开文件选择框，选择游戏运行日志。",
            onBrowse: (_, _) => BrowseFile(
                _speechLogBox,
                "选择语音日志",
                "日志文件 (*.log;*.txt)|*.log;*.txt|所有文件 (*.*)|*.*"));

        _dryRunCheck.Text = "演练模式：只检查、不写盘(&6)";
        _dryRunCheck.Dock = DockStyle.Fill;
        _dryRunCheck.AutoSize = false;
        _dryRunCheck.TextAlign = ContentAlignment.MiddleLeft;
        _dryRunCheck.TabIndex = 15;
        _dryRunCheck.AccessibleName = "演练模式";
        _dryRunCheck.AccessibleDescription = "勾选后所有命令都加 --dry-run：安装与回滚只校验不写盘。Alt 加 6 切换。";
        _dryRunCheck.CheckedChanged += (_, _) => RefreshAvailability();
        table.Controls.Add(_dryRunCheck, 0, 5);
        table.SetColumnSpan(_dryRunCheck, 3);

        _advancedCheck.Text = "高级分析：调用 Ghidra（只读，较慢）(&7)";
        _advancedCheck.Dock = DockStyle.Fill;
        _advancedCheck.AutoSize = false;
        _advancedCheck.TextAlign = ContentAlignment.MiddleLeft;
        _advancedCheck.TabIndex = 16;
        _advancedCheck.AccessibleName = "高级分析";
        _advancedCheck.AccessibleDescription =
            "勾选后「分析文本」会额外调用 Ghidra 做原生静态分析，只读但较慢。Alt 加 7 切换。";
        _advancedCheck.CheckedChanged += (_, _) => RefreshAvailability();
        table.Controls.Add(_advancedCheck, 0, 6);
        table.SetColumnSpan(_advancedCheck, 3);

        _forceCheck.Text = "允许强制：跳过安全拦阻（&9）";
        _forceCheck.Dock = DockStyle.Fill;
        _forceCheck.AutoSize = false;
        _forceCheck.TextAlign = ContentAlignment.MiddleLeft;
        _forceCheck.TabIndex = 24; // 0.2.58：排在「分析程序」之后，与视觉顺序一致（子代理 UIA 实测反馈）
        _forceCheck.AccessibleName = "允许强制";
        _forceCheck.AccessibleDescription =
            "勾选后安装会加 --force：计划里没有正文入口时仍然安装（大概率读不到剧情），或覆盖已有补丁/账本。";
        _forceCheck.CheckedChanged += (_, _) => RefreshAvailability();
        table.Controls.Add(_forceCheck, 0, 8);
        table.SetColumnSpan(_forceCheck, 3);

        _ghidraExeBox.PlaceholderText = "可选；勾选高级分析时指定要分析的 exe，留空则由 Ghidra 自动选择";        AddPathRow(
            table,
            row: 7,
            labelText: "分析程序(&8)",
            accessibleName: "分析程序",
            box: _ghidraExeBox,
            description: "可选的 exe：勾选「高级分析」后只分析这个文件；留空则自动挑选（结果里会列出其它候选）。Alt 加 8 跳到这个输入框。",
            browseText: "浏览…",
            browseAccessibleName: "浏览分析程序",
            browseDescription: "打开文件选择框，选择要分析的 exe。",
            onBrowse: (_, _) => BrowseFile(
                _ghidraExeBox,
                "选择要分析的 exe",
                "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"));

        group.Controls.Add(table);
        return group;
    }

    private void AddPathRow(
        TableLayoutPanel table,
        int row,
        string labelText,
        string accessibleName,
        TextBox box,
        string description,
        string browseText,
        string browseAccessibleName,
        string browseDescription,
        EventHandler onBrowse)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = true,
            TabIndex = row * 3,
            AccessibleName = accessibleName,
            AccessibleDescription = description,
            Margin = new Padding(0, 0, 8, 0),
        };

        box.Dock = DockStyle.Fill;
        box.TabIndex = (row * 3) + 1;
        box.AccessibleName = accessibleName;
        box.AccessibleDescription = description;
        box.Margin = new Padding(0, 4, 8, 4);
        box.TextChanged += (_, _) => RefreshAvailability();

        var browse = new Button
        {
            Text = browseText,
            Dock = DockStyle.Fill,
            TabIndex = (row * 3) + 2,
            AccessibleName = browseAccessibleName,
            AccessibleDescription = browseDescription,
            Margin = new Padding(0, 4, 0, 4),
        };
        browse.Click += onBrowse;
        _browseButtons.Add(browse);

        table.Controls.Add(label, 0, row);
        table.Controls.Add(box, 1, row);
        table.Controls.Add(browse, 2, row);
    }

    private GroupBox BuildActionGroup()
    {
        var group = new GroupBox
        {
            Text = "② 操作（Alt+字母直接执行；Tab 到这里可以逐项查看）",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 24, 10, 8),
            TabIndex = 1,
            AccessibleName = "操作区",
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 2,
            TabIndex = 0,
            AccessibleName = "操作按钮",
        };
        for (var i = 0; i < 6; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6F));
        }

        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        var tabIndex = 0;
        AddActionButton(grid, ForgeCommandKind.Detect, 0, 0, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Analyze, 1, 0, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Generate, 2, 0, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Build, 3, 0, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Report, 4, 0, ref tabIndex);

        _oneClickButton = new Button
        {
            Text = $"一键(&{ForgeShortcuts.OneClick})",
            Dock = DockStyle.Fill,
            TabIndex = tabIndex++,
            AccessibleName = "一键：识别、分析、生成、构建",
            AccessibleDescription =
                "依次执行识别、分析、生成、构建四步；任一步失败就停下。不会安装到游戏目录。Alt 加 O。",
            Margin = new Padding(3),
        };
        _oneClickButton.Click += async (_, _) => await RunOneClickAsync();
        _toolTip.SetToolTip(_oneClickButton, "依次执行识别→分析→生成→构建（不安装）。Alt+O");
        grid.Controls.Add(_oneClickButton, 5, 0);

        AddActionButton(grid, ForgeCommandKind.Install, 0, 1, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Verify, 1, 1, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Doctor, 2, 1, ref tabIndex);
        AddActionButton(grid, ForgeCommandKind.Rollback, 3, 1, ref tabIndex);

        _stopButton = new Button
        {
            Text = $"停止(&{ForgeShortcuts.Stop})",
            Dock = DockStyle.Fill,
            TabIndex = tabIndex++,
            AccessibleName = "停止",
            AccessibleDescription = "中止正在执行的命令，连同它拉起的子进程一起结束。Esc 也可以停止。",
            Margin = new Padding(3),
        };
        _stopButton.Click += (_, _) => StopCurrent();
        _toolTip.SetToolTip(_stopButton, "中止正在执行的命令（Esc）。");
        grid.Controls.Add(_stopButton, 4, 1);

        _clearButton = new Button
        {
            Text = $"清空结果(&{ForgeShortcuts.ClearResult})",
            Dock = DockStyle.Fill,
            TabIndex = tabIndex,
            AccessibleName = "清空结果",
            AccessibleDescription = "清空下方结果区的文本，执行记录保留。",
            Margin = new Padding(3),
        };
        _clearButton.Click += (_, _) => ClearResult();
        _toolTip.SetToolTip(_clearButton, "清空结果区文本（执行记录保留）。Alt+L");
        grid.Controls.Add(_clearButton, 5, 1);

        group.Controls.Add(grid);
        return group;
    }

    private void AddActionButton(TableLayoutPanel grid, ForgeCommandKind kind, int column, int row, ref int tabIndex)
    {
        var descriptor = ForgeCommands.Of(kind);
        var button = new Button
        {
            Text = $"{descriptor.Title}(&{ForgeShortcuts.MnemonicOf(kind)})",
            Dock = DockStyle.Fill,
            TabIndex = tabIndex++,
            AccessibleName = descriptor.Title,
            AccessibleDescription = descriptor.AccessibleDescription,
            Margin = new Padding(3),
        };
        button.Click += async (_, _) => await RunCommandAsync(kind);
        _toolTip.SetToolTip(button, descriptor.Description);
        _actionButtons[kind] = button;
        grid.Controls.Add(button, column, row);
    }

    private TableLayoutPanel BuildStatusArea()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            TabIndex = 2,
            AccessibleName = "状态区",
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));

        _hintLabel.Dock = DockStyle.Fill;
        _hintLabel.AutoSize = false;
        _hintLabel.TextAlign = ContentAlignment.MiddleLeft;
        _hintLabel.Text = "正在准备界面……";
        // 不设固定 AccessibleName：WinForms Label 的可访问名默认取 Text，
        // 这样「提示/最近一次结果」变化时读屏（UIA/MSAA）能读到最新文字。
        // 固定名字会让读屏永远只报「提示」，听不到动态内容（task-38 P1）。
        _hintLabel.AccessibleDescription = "当前能做什么、缺什么。";
        _hintLabel.TabIndex = 0;
        panel.Controls.Add(_hintLabel, 0, 0);
        panel.SetColumnSpan(_hintLabel, 2);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoSize = false;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "尚未执行。";
        _statusLabel.Font = new Font(Font, FontStyle.Bold);
        // 同上：可访问名跟随 Text，读屏能听到最新一行结论。
        _statusLabel.AccessibleDescription = "最近一次命令的一行结论；详细内容在结果区。";
        _statusLabel.TabIndex = 1;
        panel.Controls.Add(_statusLabel, 0, 1);

        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(6, 7, 0, 7);
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        _progress.Visible = false;
        _progress.TabStop = false;
        _progress.AccessibleName = "进度";
        _progress.AccessibleDescription = "正在执行，无法确定进度；按 Esc 可停止。";
        _progress.TabIndex = 2;
        panel.Controls.Add(_progress, 1, 1);

        return panel;
    }

    private GroupBox BuildResultGroup()
    {
        var group = new GroupBox
        {
            Text = "③ 结果（只读：Ctrl+A 全选、Ctrl+C 复制；Ctrl+Shift+R 重新朗读）",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 24, 10, 8),
            TabIndex = 3,
            AccessibleName = "结果区",
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            TabIndex = 0,
            AccessibleName = "结果与执行记录",
        };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 108F));

        _resultBox.Multiline = true;
        _resultBox.ReadOnly = true;
        _resultBox.ScrollBars = ScrollBars.Vertical;
        _resultBox.WordWrap = true;
        _resultBox.Dock = DockStyle.Fill;
        _resultBox.BackColor = SystemColors.Window;
        _resultBox.Text = "（还没有执行任何命令。先选游戏目录，然后按「识别引擎」或「一键」。）";
        _resultBox.AccessibleName = "执行结果";
        _resultBox.AccessibleDescription =
            "只读文本；第一行是结论，然后是退出码、错误码、消息与建议。Ctrl+A 全选，Ctrl+C 复制。";
        _resultBox.TabIndex = 0;
        table.Controls.Add(_resultBox, 0, 0);

        var historyLabel = new Label
        {
            Text = "执行记录（↑↓ 选择，回车把该次结果调回上方）",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            TabIndex = 1,
            AccessibleName = "执行记录说明",
        };
        table.Controls.Add(historyLabel, 0, 1);

        _historyList.Dock = DockStyle.Fill;
        _historyList.IntegralHeight = false;
        _historyList.HorizontalScrollbar = false;
        _historyList.TabIndex = 2;
        _historyList.AccessibleName = "执行记录";
        _historyList.AccessibleDescription = "最近执行过的命令；按回车把该次完整结果调回上方结果区。";
        _historyList.KeyDown += OnHistoryKeyDown;
        _historyList.DoubleClick += (_, _) => ShowSelectedHistory();
        table.Controls.Add(_historyList, 0, 2);

        group.Controls.Add(table);
        return group;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_busy)
        {
            try
            {
                _cts?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 释放失败无关紧要
            }

            return;
        }

        var answer = MessageBox.Show(
            this,
            "还有命令在执行。要停止它并退出吗？\n\n退出时子进程会被结束，游戏目录可能处在「改了一半」的状态。",
            "仍在执行",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        RequestStop();
    }
}
