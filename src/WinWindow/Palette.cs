using System.Windows;
using System.Windows.Media;

namespace WinWindow;

internal static class Palette
{
    public static void Apply(bool dark)
    {
        // Warm semantic tokens match PaperTodo's warm-paper family; layout and controls are independent.
        string[] keys = { "Paper", "Surface", "Panel", "Line", "Ink", "Muted", "Accent", "Selection", "Danger" };
        string[] values = dark
            ? new[] { "#211F1C", "#2D2A26", "#282521", "#4C453D", "#E7E0D4", "#A99D8C", "#A88E6A", "#43392B", "#E66E5A" }
            : new[] { "#FFF9EA", "#F7EDD2", "#FFFCF4", "#E0CEA7", "#33291E", "#8A7A63", "#8C7350", "#EDE0C2", "#B05A46" };
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
            brush.Freeze(); Application.Current.Resources[keys[i]] = brush;
        }
    }
}
