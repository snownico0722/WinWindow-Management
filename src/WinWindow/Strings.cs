global using static WinWindow.Strings;
using System.Collections;
using System.Windows;
using NeatWin.App;

namespace WinWindow;

internal static class Strings
{
    internal static readonly TextCatalog UiText = new(typeof(Strings));

    internal static void ApplyLanguage(string language)
    {
        TextCatalog.SetLanguage(language);
        foreach (DictionaryEntry entry in UiText.Entries)
            Application.Current.Resources["Text." + entry.Key] = entry.Value;
    }
}
