using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace OscarWatch.Troubleshoot.Localization;

/// <summary>Strings from <c>Resources/Strings*.resx</c>, in the language OscarWatch is set to.</summary>
internal static class Tr
{
    public const string DefaultLanguage = "en-GB";

    private static readonly ResourceManager ResourceManager =
        new("OscarWatch.Troubleshoot.Resources.Strings", typeof(Tr).Assembly);

    private static readonly string[] SupportedLanguages =
        ["ja", "pt-BR", "zh-CN", "es", "th", "id", "ru", "de", "it"];

    public static string Get(string key)
    {
        var value = ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
            ?? ResourceManager.GetString(key, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static void ApplyCulture(string? uiLanguage)
    {
        var uiCulture = ResolveCulture(uiLanguage);
        var formattingCulture = CultureInfo.GetCultureInfo(DefaultLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = formattingCulture;
        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.CurrentCulture = formattingCulture;
    }

    public static CultureInfo ResolveCulture(string? uiLanguage)
    {
        var code = string.Equals(uiLanguage, "zh-Hans", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : uiLanguage?.Trim();

        var match = SupportedLanguages.FirstOrDefault(
            lang => string.Equals(lang, code, StringComparison.OrdinalIgnoreCase));
        return CultureInfo.GetCultureInfo(match ?? DefaultLanguage);
    }

    /// <summary>System fonts first for scripts Inter does not cover.</summary>
    public static string FontFamilyFor(string? uiLanguage) =>
        ResolveCulture(uiLanguage).Name switch
        {
            "ja" => "Yu Gothic UI, Meiryo UI, Hiragino Sans, Noto Sans CJK JP, fonts:Inter#Inter, $Default",
            "zh-CN" => "Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, fonts:Inter#Inter, $Default",
            "th" => "Leelawadee UI, Tahoma, Thonburi, Noto Sans Thai, fonts:Inter#Inter, $Default",
            _ => "fonts:Inter#Inter, $Default"
        };
}

/// <summary>XAML: <c>Text="{t:Tr Window.Title}"</c>.</summary>
internal sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Tr.Get(Key);
}
