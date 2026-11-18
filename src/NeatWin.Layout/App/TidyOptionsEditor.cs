using NeatWin.Core;

namespace NeatWin.App;

internal sealed class TidyOptionsEditor : UserControl
{
    private readonly ModernCard _smartCard;
    private readonly SegmentedSelector<SmartTidyStrength> _smartStrength;
    private readonly ModernToggle _preferReversibleVerticalFill;
    private readonly ModernToggle _removeVideoBlackBars;
    private readonly SegmentedSelector<VideoBlackBarTendency> _videoBlackBarTendency;
    private readonly Control _videoTendencyRow;
    private readonly ModernCard _classicCard;
    private readonly NumericUpDown _neighborSnap;
    private readonly NumericUpDown _alignmentSnap;
    private readonly NumericUpDown _screenSnap;
    private readonly NumericUpDown _maximumAdjustment;
    private readonly NumericUpDown _maximumResizePercent;
    private readonly NumericUpDown _passes;
    private readonly ModernToggle _rescueOffscreen;
    private readonly Label _statusLabel;
    private TidyOptions _currentOptions;
    private SmartBehaviorOptions _currentBehaviorOptions;
    private bool _suppressControlEvents;

    internal TidyOptionsEditor(TidyOptions initialOptions, SmartBehaviorOptions initialBehaviorOptions)
    {
        _currentOptions = initialOptions;
        _currentBehaviorOptions = initialBehaviorOptions;
        Dock = DockStyle.Fill;
        Padding = new Padding(0, 8, 0, 0);
        AutoScroll = true;
        BackColor = UiTheme.Page;
        ForeColor = UiTheme.Text;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = UiTheme.Page,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(root);

        _smartCard = new ModernCard { Dock = DockStyle.Top };
        root.Controls.Add(_smartCard, 0, 0);

        var smartLayout = CreateSettingsLayout(5);
        _smartCard.Controls.Add(smartLayout);
        AddCardTitle(smartLayout, 0, "Smart 设置");

        _smartStrength = new SegmentedSelector<SmartTidyStrength>(
            new SegmentOption<SmartTidyStrength>(SmartTidyStrength.Gentle, "保守"),
            new SegmentOption<SmartTidyStrength>(SmartTidyStrength.Balanced, "平衡"),
            new SegmentOption<SmartTidyStrength>(SmartTidyStrength.Assertive, "积极"))
        {
            Width = 240,
            Anchor = AnchorStyles.Right,
        };
        AddSettingRow(
            smartLayout,
            1,
            "整理倾向",
            "控制整体整理幅度",
            _smartStrength);

        _preferReversibleVerticalFill = new ModernToggle { Anchor = AnchorStyles.Right };
        AddSettingRow(
            smartLayout,
            2,
            "可逆纵向填满",
            "接近全高时贴满上下，拖动即可恢复",
            _preferReversibleVerticalFill);

        _removeVideoBlackBars = new ModernToggle { Anchor = AnchorStyles.Right };
        AddSettingRow(
            smartLayout,
            3,
            "视频去黑边",
            "调整浏览器窗口比例，不裁切画面",
            _removeVideoBlackBars);

        _videoBlackBarTendency = new SegmentedSelector<VideoBlackBarTendency>(
            new SegmentOption<VideoBlackBarTendency>(VideoBlackBarTendency.Shrink, "缩小优先"),
            new SegmentOption<VideoBlackBarTendency>(VideoBlackBarTendency.Expand, "放大优先"))
        {
            Width = 240,
            Anchor = AnchorStyles.Right,
        };
        _videoTendencyRow = AddSettingRow(
            smartLayout,
            4,
            "视频尺寸倾向",
            "先消除黑边，再决定缩小或放大",
            _videoBlackBarTendency);

        _classicCard = new ModernCard { Dock = DockStyle.Top };
        root.Controls.Add(_classicCard, 0, 1);

        var classicLayout = CreateSettingsLayout(7);
        _classicCard.Controls.Add(classicLayout);
        AddCardTitle(classicLayout, 0, "Classic 设置");

        _neighborSnap = AddNumericRow(classicLayout, 1, "邻近吸合", "相邻窗口的识别距离", 0, 240, "px", 4);
        _alignmentSnap = AddNumericRow(classicLayout, 2, "边缘对齐", "相近边缘的对齐距离", 0, 120, "px", 2);
        _screenSnap = AddNumericRow(classicLayout, 3, "贴屏边", "屏幕边缘的吸附距离", 0, 240, "px", 4);
        _maximumAdjustment = AddNumericRow(classicLayout, 4, "最大调整", "单条边允许的最大改动", 0, 480, "px", 8);
        _maximumResizePercent = AddNumericRow(classicLayout, 5, "最大尺寸变化", "单次整理的尺寸变化上限", 0, 50, "%", 1);
        _passes = AddNumericRow(classicLayout, 6, "迭代轮数", "Classic 规则重复次数", 1, 5, "轮", 1);

        var safetyCard = new ModernCard { Dock = DockStyle.Top };
        root.Controls.Add(safetyCard, 0, 2);
        var safetyLayout = CreateSettingsLayout(2);
        safetyCard.Controls.Add(safetyLayout);
        AddCardTitle(safetyLayout, 0, "窗口保护");
        _rescueOffscreen = new ModernToggle { Anchor = AnchorStyles.Right };
        AddSettingRow(
            safetyLayout,
            1,
            "拉回部分出屏窗口",
            "仅处理当前可见的部分出屏窗口",
            _rescueOffscreen);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = UiTheme.Page,
            Margin = new Padding(0),
        };
        var reset = new Button
        {
            Text = "恢复默认",
            AutoSize = true,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 2, 0, 2),
        };
        UiTheme.StyleSecondary(reset);
        reset.Click += (_, _) => ResetDefaults();
        actions.Controls.Add(reset);
        root.Controls.Add(actions, 0, 3);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Danger,
            Text = string.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(2, 0, 0, 0),
        };
        root.Controls.Add(_statusLabel, 0, 4);

        _smartStrength.ValueChanged += (_, _) => ApplyFromControls();
        _preferReversibleVerticalFill.CheckedChanged += (_, _) => ApplyFromControls();
        _removeVideoBlackBars.CheckedChanged += (_, _) =>
        {
            UpdateVideoTendencyVisibility();
            ApplyFromControls();
        };
        _videoBlackBarTendency.ValueChanged += (_, _) => ApplyFromControls();
        _neighborSnap.ValueChanged += (_, _) => ApplyFromControls();
        _alignmentSnap.ValueChanged += (_, _) => ApplyFromControls();
        _screenSnap.ValueChanged += (_, _) => ApplyFromControls();
        _maximumAdjustment.ValueChanged += (_, _) => ApplyFromControls();
        _maximumResizePercent.ValueChanged += (_, _) => ApplyFromControls();
        _passes.ValueChanged += (_, _) => ApplyFromControls();
        _rescueOffscreen.CheckedChanged += (_, _) => ApplyFromControls();

        SetControls(initialOptions, initialBehaviorOptions);
    }

    internal event EventHandler<TidyOptionsChangeEventArgs>? OptionsChangeRequested;

    internal void SetStatus(
        TidyOptions activeOptions,
        SmartBehaviorOptions activeBehaviorOptions,
        bool success,
        string message)
    {
        _currentOptions = activeOptions;
        _currentBehaviorOptions = activeBehaviorOptions;
        SetControls(activeOptions, activeBehaviorOptions);
        _statusLabel.Text = success ? string.Empty : message;
    }

    internal void ChangeAlgorithmMode(TidyAlgorithmMode mode)
    {
        if (_currentOptions.AlgorithmMode == mode)
        {
            UpdateModeVisibility();
            return;
        }

        _currentOptions = _currentOptions with { AlgorithmMode = mode };
        UpdateModeVisibility();
        ApplyFromControls();
    }

    private void ApplyFromControls()
    {
        if (_suppressControlEvents)
        {
            return;
        }

        var mode = _currentOptions.AlgorithmMode;
        var options = _currentOptions with
        {
            RescueOffscreenWindows = _rescueOffscreen.Checked,
        };
        var behaviorOptions = _currentBehaviorOptions;

        if (mode == TidyAlgorithmMode.Smart)
        {
            options = options with
            {
                SmartStrength = _smartStrength.Value,
                SmartHitTendency = SmartHitTendency.Balanced,
                SmartSizeTendency = SmartSizeTendency.Balanced,
            };
            behaviorOptions = behaviorOptions with
            {
                OverlapAvoidance = SmartOverlapAvoidance.Balanced,
                PreferReversibleVerticalFill = _preferReversibleVerticalFill.Checked,
                RemoveVideoBlackBars = _removeVideoBlackBars.Checked,
                VideoBlackBarTendency = _videoBlackBarTendency.Value,
            };
        }
        else
        {
            options = options with
            {
                NeighborSnapDistance = (int)_neighborSnap.Value,
                AlignmentSnapDistance = (int)_alignmentSnap.Value,
                ScreenSnapDistance = (int)_screenSnap.Value,
                MaximumEdgeAdjustment = (int)_maximumAdjustment.Value,
                MaximumSizeChangeRatio = (double)_maximumResizePercent.Value / 100.0,
                Passes = (int)_passes.Value,
            };
        }

        _currentOptions = options;
        _currentBehaviorOptions = behaviorOptions;
        _statusLabel.Text = string.Empty;
        OptionsChangeRequested?.Invoke(this, new TidyOptionsChangeEventArgs(options, behaviorOptions));
    }

    private void ResetDefaults()
    {
        var options = new TidyOptions(AlgorithmMode: _currentOptions.AlgorithmMode);
        var behavior = new SmartBehaviorOptions();
        SetControls(options, behavior);
        ApplyFromControls();
    }

    private void SetControls(TidyOptions options, SmartBehaviorOptions behaviorOptions)
    {
        _suppressControlEvents = true;
        try
        {
            _smartStrength.SetValue(options.SmartStrength, raiseEvent: false);
            _preferReversibleVerticalFill.Checked = behaviorOptions.PreferReversibleVerticalFill;
            _removeVideoBlackBars.Checked = behaviorOptions.RemoveVideoBlackBars;
            _videoBlackBarTendency.SetValue(behaviorOptions.VideoBlackBarTendency, raiseEvent: false);

            _neighborSnap.Value = ClampToDecimal(options.NeighborSnapDistance, _neighborSnap.Minimum, _neighborSnap.Maximum);
            _alignmentSnap.Value = ClampToDecimal(options.AlignmentSnapDistance, _alignmentSnap.Minimum, _alignmentSnap.Maximum);
            _screenSnap.Value = ClampToDecimal(options.ScreenSnapDistance, _screenSnap.Minimum, _screenSnap.Maximum);
            _maximumAdjustment.Value = ClampToDecimal(options.MaximumEdgeAdjustment, _maximumAdjustment.Minimum, _maximumAdjustment.Maximum);
            _maximumResizePercent.Value = ClampToDecimal(
                (decimal)(options.MaximumSizeChangeRatio * 100),
                _maximumResizePercent.Minimum,
                _maximumResizePercent.Maximum);
            _passes.Value = ClampToDecimal(options.Passes, _passes.Minimum, _passes.Maximum);
            _rescueOffscreen.Checked = options.RescueOffscreenWindows;
        }
        finally
        {
            _suppressControlEvents = false;
        }

        UpdateVideoTendencyVisibility();
        UpdateModeVisibility();
    }

    private void UpdateModeVisibility()
    {
        var smart = _currentOptions.AlgorithmMode == TidyAlgorithmMode.Smart;
        _smartCard.Visible = smart;
        _classicCard.Visible = !smart;
    }

    private void UpdateVideoTendencyVisibility()
    {
        _videoTendencyRow.Visible = _removeVideoBlackBars.Checked;
    }

    private static TableLayoutPanel CreateSettingsLayout(int rows)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rows,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        for (var row = 1; row < rows; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        return layout;
    }

    private static void AddCardTitle(TableLayoutPanel root, int row, string text)
    {
        var label = new Label
        {
            AutoSize = true,
            Text = text,
            Font = UiTheme.Semibold(11.5F),
            ForeColor = UiTheme.Text,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.SetColumnSpan(label, 2);
        root.Controls.Add(label, 0, row);
    }

    private static Control AddSettingRow(
        TableLayoutPanel root,
        int row,
        string title,
        string description,
        Control editor)
    {
        var textHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0, 8, 16, 8),
        };
        textHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textHost.Controls.Add(new Label
        {
            AutoSize = true,
            Text = title,
            Font = UiTheme.Semibold(10F),
            ForeColor = UiTheme.Text,
            Margin = new Padding(0, 0, 0, 3),
        }, 0, 0);
        textHost.Controls.Add(new Label
        {
            AutoSize = true,
            Text = description,
            ForeColor = UiTheme.TextMuted,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(0),
        }, 0, 1);
        root.Controls.Add(textHost, 0, row);

        var editorHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0, 8, 0, 8),
            MinimumSize = new Size(0, 50),
        };
        editor.Anchor = AnchorStyles.Right;
        editor.Location = new Point(Math.Max(0, 252 - editor.Width), 7);
        editorHost.Controls.Add(editor);
        editorHost.Resize += (_, _) =>
        {
            editor.Left = Math.Max(0, editorHost.ClientSize.Width - editor.Width);
            editor.Top = Math.Max(0, (editorHost.ClientSize.Height - editor.Height) / 2);
        };
        root.Controls.Add(editorHost, 1, row);

        var rowHost = new Panel { Visible = true };
        rowHost.VisibleChanged += (_, _) =>
        {
            textHost.Visible = rowHost.Visible;
            editorHost.Visible = rowHost.Visible;
        };
        return rowHost;
    }

    private static NumericUpDown AddNumericRow(
        TableLayoutPanel root,
        int row,
        string title,
        string description,
        decimal minimum,
        decimal maximum,
        string unit,
        decimal increment)
    {
        var value = new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Increment = increment,
            DecimalPlaces = 0,
            Width = 112,
            Height = 30,
            TextAlign = HorizontalAlignment.Right,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = new Font("Segoe UI", 9.5F),
        };

        var editor = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiTheme.Surface,
        };
        editor.Controls.Add(value);
        editor.Controls.Add(new Label
        {
            AutoSize = true,
            Text = unit,
            ForeColor = UiTheme.TextMuted,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(7, 6, 0, 0),
        });
        AddSettingRow(root, row, title, description, editor);
        return value;
    }

    private static decimal ClampToDecimal(decimal value, decimal minimum, decimal maximum) =>
        Math.Min(maximum, Math.Max(minimum, value));
}

internal sealed class TidyOptionsChangeEventArgs(
    TidyOptions options,
    SmartBehaviorOptions behaviorOptions) : EventArgs
{
    internal TidyOptions Options { get; } = options;
    internal SmartBehaviorOptions BehaviorOptions { get; } = behaviorOptions;
}
