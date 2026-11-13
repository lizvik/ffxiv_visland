using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace visland.Helpers;

public class LanguageConfig : Configuration.Node {
    public string Language = "auto";
}

// English source text doubles as a stable resource key. Unknown keys fall back to English.
public static class L {
    private static readonly Dictionary<string, string> Chinese = Load("zh-CN");
    private static Dictionary<string, string> Load(string language) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"visland.Localization.{language}.json");
        if (stream == null)
            throw new InvalidDataException($"Missing localization resource: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }

    public static string Text(string source) {
        var language = Service.Config.Get<LanguageConfig>().Language;
        var chinese = language == "zh-CN" || (language == "auto" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh");
        return chinese && Chinese.TryGetValue(source, out var translated) && translated.Length > 0 ? translated : source;
    }

    public static string Format(string source, params object[] args) => string.Format(CultureInfo.CurrentCulture, Text(source), args);

    // Keep widget identity independent of the selected display language.
    public static string Label(string source) {
        var separator = source.IndexOf("##", StringComparison.Ordinal);
        var visible = separator < 0 ? source : source[..separator];
        return Text(visible) + "###" + (separator < 0 ? source : source[(separator + (source[separator..].StartsWith("###") ? 3 : 2))..]);
    }

    public static void DrawSelector() {
        var config = Service.Config.Get<LanguageConfig>();
        var selected = config.Language switch { "en" => 1, "zh-CN" => 2, _ => 0 };
        string[] names = [Text("Automatic (system)"), "English", "简体中文"];
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Text("Language"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("###Language", ref selected, names, names.Length)) {
            config.Language = selected switch { 1 => "en", 2 => "zh-CN", _ => "auto" };
            config.NotifyModified();
        }
    }
}
