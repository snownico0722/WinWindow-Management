using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.App;

internal sealed record TaskPreferences(TaskLayoutProfile Profile, ViewingCalibration? Calibration = null)
{
    internal static TaskPreferences Load(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TaskPreferences>(File.ReadAllText(path));
            return value is null ? new TaskPreferences(new TaskLayoutProfile()) : value with { Profile = (value.Profile ?? new TaskLayoutProfile()).Normalize() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new TaskPreferences(new TaskLayoutProfile()); }
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this with { Profile = Profile.Normalize() }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class TaskPreferencesDialog : Form
{
    private readonly CheckBox _resize = new() { Text = "允许为更好的使用效果调整大小", AutoSize = true };
    private readonly CheckBox _bleed = new() { Text = "允许少量水平出屏（外侧边缘可牺牲）", AutoSize = true };
    private readonly CheckBox _protect = new() { Text = "保护四侧边缘（不以一侧新增遮挡换取总面积）", AutoSize = true };
    private readonly CheckBox _physical = new() { Text = "为此工作区提供物理观看参数", AutoSize = true };
    private readonly NumericUpDown _bleedSize = Number(0, 48);
    private readonly NumericUpDown _width = Number(480, 2400);
    private readonly NumericUpDown _height = Number(320, 1600);
    private readonly NumericUpDown _physicalWidth = Number(150, 4000);
    private readonly NumericUpDown _distance = Number(200, 3000);
    private readonly TaskPreferences _initial;
    private readonly RectI _area;
    private readonly uint _dpi;

    internal TaskPreferences Value => new(_initial.Profile with
    {
        AllowUsefulResize = _resize.Checked, AllowPeripheralBleed = _bleed.Checked,
        MaximumBleedDip = (double)_bleedSize.Value, ComfortableWidthDip = (double)_width.Value,
        ComfortableHeightDip = (double)_height.Value, ProtectWindowEdges = _protect.Checked,
    }, _physical.Checked ? new(_area, _dpi, (double)_physicalWidth.Value, (double)_distance.Value) : null);

    internal TaskPreferencesDialog(TaskPreferences preferences, RectI area, uint dpi)
    {
        _initial = preferences; _area = area; _dpi = dpi;
        Text = "人因排布偏好"; ClientSize = new Size(660, 550); MinimumSize = new Size(620, 500);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9.5F);
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, AutoScroll = true };
        root.ColumnStyles.Add(new(SizeType.Percent, 75)); root.ColumnStyles.Add(new(SizeType.Percent, 25));
        Controls.Add(root);
        var row = 0;
        void Add(Control label, Control? input = null)
        {
            root.RowStyles.Add(new(SizeType.AutoSize)); label.Margin = new Padding(3, 7, 3, 7);
            root.Controls.Add(label, 0, row);
            if (input is null) root.SetColumnSpan(label, 2);
            else { input.Anchor = AnchorStyles.Left; root.Controls.Add(input, 1, row); }
            row++;
        }
        Label Label(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(570, 0) };
        Add(Label("自动判断窗口关系；这些是偏好，不是必须选择的工作模式。中心区域只是几何先验，并不保证边缘信息不重要。"));
        _resize.Checked = preferences.Profile.AllowUsefulResize; Add(_resize);
        _bleed.Checked = preferences.Profile.AllowPeripheralBleed; Add(_bleed);
        _protect.Checked = preferences.Profile.ProtectWindowEdges; Add(_protect);
        _bleedSize.Value = (decimal)preferences.Profile.MaximumBleedDip; Add(Label("最大水平出屏量（DIP）"), _bleedSize);
        _width.Value = (decimal)preferences.Profile.ComfortableWidthDip; Add(Label("共同观看的参考信息宽度（DIP；不是固定窗口宽度）"), _width);
        _height.Value = (decimal)preferences.Profile.ComfortableHeightDip; Add(Label("共同观看的参考信息高度（DIP）"), _height);
        _physical.Checked = preferences.Calibration?.Matches(area, dpi) == true; Add(_physical);
        _physicalWidth.Value = (decimal)(_physical.Checked ? preferences.Calibration!.WidthMillimeters : 600);
        _distance.Value = (decimal)(_physical.Checked ? preferences.Calibration!.DistanceMillimeters : 700);
        Add(Label("当前可用工作区实际宽度（毫米，不是对角线）"), _physicalWidth);
        Add(Label("眼睛到屏幕中心的距离（毫米）"), _distance);
        Add(Label($"对应工作区：{area.Width} × {area.Height}，DPI {dpi}。\n不填写时使用归一化距离；不是眼动追踪。曲面屏的角度仅为平面近似。"));
        void EnableInputs() { _physicalWidth.Enabled = _distance.Enabled = _physical.Checked; _bleed.Enabled = !_protect.Checked; _bleedSize.Enabled = _bleed.Checked && !_protect.Checked; }
        _protect.CheckedChanged += (_, _) => EnableInputs();
        _physical.CheckedChanged += (_, _) => EnableInputs(); _bleed.CheckedChanged += (_, _) => EnableInputs(); EnableInputs();
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); Add(buttons);
        AcceptButton = save; CancelButton = cancel;
    }
    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 105 };
}
