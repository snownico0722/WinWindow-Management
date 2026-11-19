using System.Globalization;
using System.Resources;

namespace NeatWin.App;

// Both the layout STA and the WPF dispatcher use the language selected in the workbench.
internal sealed class TextCatalog(Type owner)
{
    private readonly ResourceManager _resources = new(owner);
    private static readonly CultureInfo SystemLanguage = CultureInfo.CurrentUICulture;
    private static volatile CultureInfo _culture = Resolve("system");
    internal static CultureInfo Culture => _culture;

    private static CultureInfo Resolve(string? language)
    {
        bool chinese = language switch
        {
            "zh-CN" => true,
            "en" => false,
            _ => SystemLanguage.TwoLetterISOLanguageName == "zh"
        };
        return CultureInfo.GetCultureInfo(chinese ? "zh-CN" : "en");
    }

    internal static void SetLanguage(string language) => _culture = Resolve(language);
    internal ResourceSet Entries => _resources.GetResourceSet(Culture, true, true)!;
    internal string Get(string key) => _resources.GetString(key, Culture)
        ?? throw new MissingManifestResourceException($"Missing text '{key}' in {_resources.BaseName}.");
    internal string Format(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
