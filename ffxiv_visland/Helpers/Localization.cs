using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace visland.Helpers;

// Keep this type name stable: Configuration stores its fully qualified name.
public class LanguageConfig : Configuration.Node {
    public string LanguageCode = Localization.SystemLanguageCode;

    public override void Deserialize(JObject configurationJson, Newtonsoft.Json.JsonSerializer serializer) {
        LanguageCode = configurationJson.Value<string>(nameof(LanguageCode))
            ?? configurationJson.Value<string>("Language")
            ?? Localization.SystemLanguageCode;
    }
}

// Semantic resource keys are independent of display text. Missing translations fall back to English.
public static class Localization {
    public const string SystemLanguageCode = "auto";
    public const string EnglishLanguageCode = "en";
    public const string SimplifiedChineseLanguageCode = "zh-CN";

    private static readonly IReadOnlyDictionary<string, string> _englishTranslations = LoadTranslations(EnglishLanguageCode);
    private static readonly IReadOnlyDictionary<string, string> _simplifiedChineseTranslations = LoadTranslations(SimplifiedChineseLanguageCode);

    private static Dictionary<string, string> LoadTranslations(string languageCode) {
        var resourceName = $"visland.Localization.{languageCode}.json";
        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Missing localization resource: {languageCode}");
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(resourceStream) ?? new();
    }

    public static string GetText(string resourceKey) {
        var languageCode = Service.Config.Get<LanguageConfig>().LanguageCode;
        var useSimplifiedChinese = languageCode == SimplifiedChineseLanguageCode
            || (languageCode == SystemLanguageCode && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh");
        if (useSimplifiedChinese && _simplifiedChineseTranslations.TryGetValue(resourceKey, out var translatedText)
            && translatedText.Length > 0)
            return translatedText;
        return _englishTranslations.TryGetValue(resourceKey, out var englishText) ? englishText : resourceKey;
    }

    public static string FormatText(string resourceKey, params object[] arguments)
        => string.Format(CultureInfo.CurrentCulture, GetText(resourceKey), arguments);

    // Call sites explicitly retain pre-migration widget IDs; new widgets default to their stable resource key.
    public static string GetLabel(string resourceKey, string? stableWidgetId = null)
        => GetText(resourceKey) + "###" + (stableWidgetId ?? resourceKey);

    public static void DrawLanguageSelector() {
        var languageConfig = Service.Config.Get<LanguageConfig>();
        var selectedLanguageIndex = languageConfig.LanguageCode switch {
            EnglishLanguageCode => 1,
            SimplifiedChineseLanguageCode => 2,
            _ => 0,
        };
        string[] languageDisplayNames = [GetText(LocalizationKeys.Settings.SystemLanguage), "English", "简体中文"];
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(GetText(LocalizationKeys.Settings.Language));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("###Language", ref selectedLanguageIndex, languageDisplayNames, languageDisplayNames.Length)) {
            languageConfig.LanguageCode = selectedLanguageIndex switch {
                1 => EnglishLanguageCode,
                2 => SimplifiedChineseLanguageCode,
                _ => SystemLanguageCode,
            };
            languageConfig.NotifyModified();
        }
    }
}
