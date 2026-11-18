using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace NeatWin.App;

internal static class UiTheme
{
    internal static readonly Color Page = Color.FromArgb(246, 247, 249);
    internal static readonly Color Surface = Color.White;
    internal static readonly Color SurfaceMuted = Color.FromArgb(237, 240, 244);
    internal static readonly Color Border = Color.FromArgb(198, 205, 215);
    internal static readonly Color Divider = Color.FromArgb(220, 224, 230);
    internal static readonly Color Text = Color.FromArgb(18, 21, 25);
    internal static readonly Color TextMuted = Color.FromArgb(72, 79, 89);
    internal static readonly Color Accent = Color.FromArgb(43, 103, 222);
    internal static readonly Color AccentHover = Color.FromArgb(35, 87, 190);
    internal static readonly Color Danger = Color.FromArgb(180, 45, 45);

    internal static Font Semibold(float size) => new("Segoe UI Semibold", size, FontStyle.Regular);

    internal static void StylePrimary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(31, 83, 185);
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.Font = Semibold(Math.Max(10F, button.Font.Size + 0.5F));
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentHover;
    }

    internal static void StyleSecondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.Font = Semibold(Math.Max(9F, button.Font.Size));
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.MouseOverBackColor = SurfaceMuted;
        button.FlatAppearance.MouseDownBackColor = SurfaceMuted;
    }

    internal static void StyleCombo(ComboBox combo)
    {
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Surface;
        combo.ForeColor = Text;
        combo.Font = new Font("Segoe UI", 9.5F);
    }

    internal static void StyleChip(CheckBox chip)
    {
        chip.Appearance = Appearance.Button;
        chip.AutoSize = false;
        chip.TextAlign = ContentAlignment.MiddleCenter;
        chip.FlatStyle = FlatStyle.Flat;
        chip.FlatAppearance.BorderSize = 1;
        chip.FlatAppearance.BorderColor = Border;
        chip.BackColor = Surface;
        chip.ForeColor = Text;
        chip.Font = Semibold(9F);
        chip.Cursor = Cursors.Hand;
        chip.UseVisualStyleBackColor = false;
        chip.CheckedChanged += (_, _) =>
        {
            chip.BackColor = chip.Checked ? Accent : Surface;
            chip.ForeColor = chip.Checked ? Color.White : Text;
            chip.FlatAppearance.BorderColor = chip.Checked ? Accent : Border;
        };
    }

    internal static void EnableRowDividers(TableLayoutPanel layout, int firstRow = 1)
    {
        layout.CellPaint += (_, eventArgs) =>
        {
            if (eventArgs.Row < firstRow || eventArgs.Row >= layout.RowCount - 1)
            {
                return;
            }

            using var pen = new Pen(Divider);
            var y = eventArgs.CellBounds.Bottom - 1;
            eventArgs.Graphics.DrawLine(
                pen,
                eventArgs.CellBounds.Left,
                y,
                eventArgs.CellBounds.Right,
                y);
        };
    }

    internal static Panel CreateDivider() => new()
    {
        Dock = DockStyle.Fill,
        Height = 1,
        BackColor = Divider,
        Margin = new Padding(0),
    };
}

internal sealed class ModernCard : Panel
{
    internal ModernCard()
    {
        BackColor = UiTheme.Surface;
        Padding = new Padding(18);
        Margin = new Padding(0, 0, 0, 14);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is TableLayoutPanel layout)
        {
            UiTheme.EnableRowDividers(layout, firstRow: 1);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiTheme.Border);
        var rect = ClientRectangle;
        if (rect.Width > 0 && rect.Height > 0)
        {
            e.Graphics.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);
        }
    }
}

internal sealed class ModernToggle : CheckBox
{
    internal ModernToggle()
    {
        AutoSize = false;
        Size = new Size(46, 24);
        Text = string.Empty;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface);

        var track = new Rectangle(1, 2, Width - 2, Height - 4);
        var trackColor = !Enabled
            ? Color.FromArgb(209, 214, 222)
            : Checked ? UiTheme.Accent : Color.FromArgb(166, 173, 184);
        using (var trackBrush = new SolidBrush(trackColor))
        using (var trackPath = RoundedRect(track, track.Height / 2F))
        {
            e.Graphics.FillPath(trackBrush, trackPath);
        }

        var knobSize = Height - 8;
        var knobX = Checked ? Width - knobSize - 4 : 4;
        var knob = new Rectangle(knobX, 4, knobSize, knobSize);
        using var knobBrush = new SolidBrush(Color.White);
        e.Graphics.FillEllipse(knobBrush, knob);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SegmentedSelector<T> : UserControl
    where T : struct, Enum
{
    private readonly TableLayoutPanel _layout;
    private readonly List<(T Value, Button Button)> _items = new();
    private T _value;
    private bool _hasValue;

    internal SegmentedSelector(params SegmentOption<T>[] options)
    {
        Height = 38;
        MinimumSize = new Size(120, 38);
        BackColor = UiTheme.Border;
        Padding = new Padding(1);

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = Math.Max(1, options.Length),
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BackColor = UiTheme.SurfaceMuted,
        };
        Controls.Add(_layout);

        foreach (var option in options)
        {
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / Math.Max(1, options.Length)));
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Text = option.Name,
                Margin = new Padding(0),
                FlatStyle = FlatStyle.Flat,
                Font = UiTheme.Semibold(9.5F),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SetValue(option.Value, raiseEvent: true);
            _items.Add((option.Value, button));
            _layout.Controls.Add(button, _items.Count - 1, 0);
        }

        if (options.Length > 0)
        {
            SetValue(options[0].Value, raiseEvent: false);
        }
    }

    internal event EventHandler? ValueChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal T Value
    {
        get => _value;
        set => SetValue(value, raiseEvent: false);
    }

    internal void SetValue(T value, bool raiseEvent)
    {
        if (_hasValue && EqualityComparer<T>.Default.Equals(_value, value))
        {
            UpdateVisuals();
            return;
        }

        _value = value;
        _hasValue = true;
        UpdateVisuals();
        if (raiseEvent)
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateVisuals()
    {
        foreach (var item in _items)
        {
            var selected = _hasValue && EqualityComparer<T>.Default.Equals(item.Value, _value);
            item.Button.BackColor = selected ? UiTheme.Accent : UiTheme.SurfaceMuted;
            item.Button.ForeColor = selected ? Color.White : UiTheme.Text;
        }
    }
}

internal readonly record struct SegmentOption<T>(T Value, string Name)
    where T : struct, Enum;
