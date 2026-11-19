using System.Windows;
using System.Windows.Media;

namespace WinWindow;

internal static class Palette
{
    public static void Apply(bool dark)
    {
        // Low-saturation warm alabaster / charcoal tokens keep PaperTodo's warmth
        // while giving dense window controls crisp card contrast and clear hierarchy.
        string[] keys =
        {
            "Paper", "Surface", "Panel", "Line", "CardLine",
            "Ink", "Muted", "Accent", "AccentHover", "OnAccent",
            "Selection", "WarningBg", "WarningInk", "Danger", "DangerSoft"
        };
        string[] values = dark
            ? new[]
            {
                "#191816", "#2E2B27", "#24221F", "#38342E", "#332F2A",
                "#EFECE6", "#A69E93", "#D89B68", "#E4AE80", "#191816",
                "#3D2E22", "#423218", "#F5CA86", "#EE8572", "#422622"
            }
            : new[]
            {
                "#F6F5F2", "#EDEAE4", "#FFFFFF", "#E2DDD5", "#EAE5DD",
                "#24201C", "#6E665C", "#9A6338", "#84522C", "#FFFFFF",
                "#F5E6D8", "#FDF0DC", "#8A5311", "#B44834", "#FAEAE6"
            };
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
            brush.Freeze();
            Application.Current.Resources[keys[i]] = brush;
        }
    }
}
