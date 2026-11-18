using NeatWin.Core;
using NeatWin.Recording;

namespace NeatWin.App;

internal sealed class MainWindow : Form
{
    private readonly Label _activityLabel;
    private readonly SegmentedSelector<TidyAlgorithmMode> _algorithmModeSelector;
    private readonly SegmentedSelector<AutomaticLayoutMode> _automaticMode;
    private readonly Label _automaticDescription;
    private readonly Button _tidyButton;
    private readonly SegmentedSelector<MainSection> _sectionSelector;
    private readonly TidyOptionsEditor _tidyOptionsEditor;
    private readonly Control _settingsPage;
    private readonly Control _recorderPage;
    internal event Action<string>? Activity;
    internal event Action? PreferencesRequested;
    internal event Action<bool>? ReferenceChanged;
    private Label _recorderStatusLabel = null!;
    private Button _recorderPauseButton = null!;
    private bool _suppressAutoTidyChange;
    private bool _allowClose;

    internal MainWindow(
        bool useReference,
        TidyOptions initialOptions,
        SmartBehaviorOptions initialBehaviorOptions,
        AutomaticLayoutMode automaticMode)
    {
        Text = "WinWindow · 桌面整理设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(780, 680);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        BackColor = UiTheme.Page;
        ForeColor = UiTheme.Text;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 20, 22, 18),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = UiTheme.Page,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
            BackColor = UiTheme.Page, Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _algorithmModeSelector = new SegmentedSelector<TidyAlgorithmMode>(
            new SegmentOption<TidyAlgorithmMode>(TidyAlgorithmMode.Smart, "Smart"),
            new SegmentOption<TidyAlgorithmMode>(TidyAlgorithmMode.Classic, "Classic"))
        { Width = 200, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        _algorithmModeSelector.SetValue(initialOptions.AlgorithmMode, raiseEvent: false);
        header.Controls.Add(_algorithmModeSelector, 0, 0);
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            header.Controls.Add(new Label
            {
                Text = elevated ? "管理员运行" : "未提权：管理员窗口可能无法操作",
                AutoSize = true, Anchor = AnchorStyles.Right,
                ForeColor = elevated ? UiTheme.TextMuted : UiTheme.Danger,
            }, 1, 0);
        }

        // Four choices are visible without opening a menu. "平铺" describes the controls,
        // not an additional automatic window-tiling policy.
        _automaticMode = new SegmentedSelector<AutomaticLayoutMode>(
            Enum.GetValues<AutomaticLayoutMode>().Select(mode =>
                new SegmentOption<AutomaticLayoutMode>(mode, AutomaticLayoutPolicy.Name(mode))).ToArray())
        { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), AccessibleName = "自动整理档位" };
        _automaticMode.SetValue(automaticMode, raiseEvent: false);
        header.Controls.Add(_automaticMode, 0, 1);
        header.SetColumnSpan(_automaticMode, 2);
        _automaticDescription = new Label
        {
            Dock = DockStyle.Fill, ForeColor = UiTheme.TextMuted,
            Text = AutomaticLayoutPolicy.Description(automaticMode), Margin = new Padding(0, 4, 0, 4),
        };
        header.Controls.Add(_automaticDescription, 0, 2);
        header.SetColumnSpan(_automaticDescription, 2);
        root.Controls.Add(header, 0, 0);

        _tidyButton = new Button
        {
            Dock = DockStyle.Fill, Text = "整理当前可见窗口", Font = UiTheme.Semibold(12F),
            Margin = new Padding(0, 0, 0, 10),
        };
        UiTheme.StylePrimary(_tidyButton);
        _tidyButton.Click += (_, _) => TidyRequested?.Invoke(this, EventArgs.Empty);
        root.Controls.Add(_tidyButton, 0, 1);

        _activityLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = string.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextMuted,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(2, 0, 0, 0),
        };
        root.Controls.Add(_activityLabel, 0, 2);

        _sectionSelector = new SegmentedSelector<MainSection>(
            new SegmentOption<MainSection>(MainSection.Settings, "整理设置"),
            new SegmentOption<MainSection>(MainSection.Recorder, "记录器"))
        {
            Width = 360,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 0, 6),
        };
        root.Controls.Add(_sectionSelector, 0, 3);

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Page,
            Margin = new Padding(0),
        };
        root.Controls.Add(contentHost, 0, 4);

        _tidyOptionsEditor = new TidyOptionsEditor(initialOptions, initialBehaviorOptions);
        _tidyOptionsEditor.OptionsChangeRequested += (_, eventArgs) =>
            TidyOptionsChangeRequested?.Invoke(this, eventArgs);
        _settingsPage = _tidyOptionsEditor;
        contentHost.Controls.Add(_settingsPage);

        _recorderPage = BuildRecorderPage();
        contentHost.Controls.Add(_recorderPage);

        _sectionSelector.SetValue(MainSection.Settings, raiseEvent: false);
        ShowSection(MainSection.Settings);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = UiTheme.Page,
            Margin = new Padding(0, 4, 0, 0),
        };
        var exitButton = new Button
        {
            Text = "关闭",
            AutoSize = true,
            Padding = new Padding(10, 3, 10, 3),
        };
        UiTheme.StyleSecondary(exitButton);
        exitButton.Click += (_, _) => Hide();
        bottom.Controls.Add(exitButton);
        var undoButton = new Button { Text = "撤销上次整理", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        UiTheme.StyleSecondary(undoButton);
        undoButton.Click += (_, _) => UndoRequested?.Invoke(this, EventArgs.Empty);
        bottom.Controls.Add(undoButton);
        var preferences = new Button { Text = "人因排布偏好…", AutoSize = true };
        UiTheme.StyleSecondary(preferences);
        preferences.Click += (_, _) => PreferencesRequested?.Invoke();
        bottom.Controls.Add(preferences);
        var reference = new CheckBox { Text = "使用记录器的弱参考", Checked = useReference, AutoSize = true };
        reference.CheckedChanged += (_, _) => ReferenceChanged?.Invoke(reference.Checked);
        bottom.Controls.Add(reference);
        root.Controls.Add(bottom, 0, 5);

        _algorithmModeSelector.ValueChanged += OnAlgorithmModeChanged;
        _sectionSelector.ValueChanged += (_, _) => ShowSection(_sectionSelector.Value);
        _automaticMode.ValueChanged += OnAutoTidyChanged;
        SetAutomaticLayoutMode(automaticMode);

        FormClosing += OnFormClosing;
    }

    internal event EventHandler? TidyRequested;
    internal event EventHandler? UndoRequested;
    internal event EventHandler? RecorderPauseRequested;
    internal event EventHandler? RecorderOpenDataRequested;
    internal event EventHandler? RecorderExportRequested;
    internal event EventHandler? RecorderClearRequested;
    internal event EventHandler<TidyOptionsChangeEventArgs>? TidyOptionsChangeRequested;
    internal event EventHandler<AutoTidyChangeEventArgs>? AutoTidyChangeRequested;

    internal void SetActivity(string message, bool error = false)
    {
        _activityLabel.Text = message;
        Activity?.Invoke(message);
        _activityLabel.ForeColor = error ? UiTheme.Danger : UiTheme.TextMuted;
    }

    internal void SetTidyOptionsStatus(
        TidyOptions activeOptions,
        SmartBehaviorOptions activeBehaviorOptions,
        bool success,
        string message)
    {
        _tidyOptionsEditor.SetStatus(activeOptions, activeBehaviorOptions, success, message);
        _algorithmModeSelector.SetValue(activeOptions.AlgorithmMode, raiseEvent: false);
    }

    internal void SetAutomaticLayoutMode(AutomaticLayoutMode mode)
    {
        _suppressAutoTidyChange = true;
        try
        {
            _automaticMode.SetValue(mode, raiseEvent: false);
            _automaticDescription.Text = AutomaticLayoutPolicy.Description(mode);
        }
        finally
        {
            _suppressAutoTidyChange = false;
        }
    }

    internal void BringToFrontFromTray()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    internal void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnAlgorithmModeChanged(object? sender, EventArgs eventArgs) =>
        _tidyOptionsEditor.ChangeAlgorithmMode(_algorithmModeSelector.Value);

    private void OnAutoTidyChanged(object? sender, EventArgs eventArgs)
    {
        if (_suppressAutoTidyChange)
        {
            return;
        }

        var mode = _automaticMode.Value;
        SetAutomaticLayoutMode(mode);
        AutoTidyChangeRequested?.Invoke(this, new AutoTidyChangeEventArgs(mode));
    }

    private void ShowSection(MainSection section)
    {
        _settingsPage.Visible = section == MainSection.Settings;
        _recorderPage.Visible = section == MainSection.Recorder;
        (section switch
        {
            MainSection.Recorder => _recorderPage,
            _ => _settingsPage,
        }).BringToFront();
    }

    internal void ShowRecorderPage()
    {
        _sectionSelector.SetValue(MainSection.Recorder, raiseEvent: false);
        ShowSection(MainSection.Recorder);
        BringToFrontFromTray();
    }

    internal void SetRecorderStatus(RecorderStatus status)
    {
        _recorderStatusLabel.Text = $"{status.StateText} · 本次 {status.RecordedGestures} 次手势 · " +
            $"{status.ContextRecords} 次状态变化 · 跳过 {status.Excluded} 条\n" +
            (status.Error ?? "记录只保存在本机；不保存标题、应用名、按键、截图或鼠标轨迹。不会自动上传。");
        _recorderStatusLabel.ForeColor = status.Error is null ? UiTheme.TextMuted : UiTheme.Danger;
        _recorderPauseButton.Text = status.IsPaused ? "开始记录" : "暂停记录";
        _recorderPauseButton.Enabled = status.IsAvailable;
    }

    private Control BuildRecorderPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Page, Padding = new Padding(0, 8, 0, 0), AutoScroll = true };
        var card = new ModernCard { Dock = DockStyle.Top, AutoSize = false, Height = 264 };
        page.Controls.Add(card);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiTheme.Surface, Padding = Padding.Empty,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "内置习惯记录器", AutoSize = true, Font = UiTheme.Semibold(11.5F), ForeColor = UiTheme.Text,
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, ForeColor = UiTheme.TextMuted,
            Text = "开启后记录窗口几何、层级、前台和拖动／缩放结果，用于分析操作习惯。\n" +
                   "观察与整理诊断共用匿名编号。暂停后停止写入新记录；不影响整理功能。",
        }, 0, 1);
        _recorderStatusLabel = new Label { Dock = DockStyle.Fill, ForeColor = UiTheme.TextMuted, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(_recorderStatusLabel, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        _recorderPauseButton = new Button { Text = "暂停记录", AutoSize = true, Padding = new Padding(9, 3, 9, 3) };
        UiTheme.StyleSecondary(_recorderPauseButton);
        _recorderPauseButton.Click += (_, _) => RecorderPauseRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(_recorderPauseButton);
        foreach (var (text, action) in new (string, Action)[]
        {
            ("打开数据", () => RecorderOpenDataRequested?.Invoke(this, EventArgs.Empty)),
            ("导出记录", () => RecorderExportRequested?.Invoke(this, EventArgs.Empty)),
            ("清空记录", () => RecorderClearRequested?.Invoke(this, EventArgs.Empty)),
        })
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(9, 3, 9, 3), Margin = new Padding(8, 0, 0, 0) };
            UiTheme.StyleSecondary(button);
            button.Click += (_, _) => action();
            actions.Controls.Add(button);
        }
        layout.Controls.Add(actions, 0, 3);
        return page;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose || eventArgs.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private enum MainSection
    {
        Settings,
        Recorder,
    }
}

internal sealed class AutoTidyChangeEventArgs(AutomaticLayoutMode mode) : EventArgs
{
    internal AutomaticLayoutMode Mode { get; } = mode;
}
