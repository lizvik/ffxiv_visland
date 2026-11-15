using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using Lumina.Extensions;
using visland.Helpers;

namespace visland.Pasture.RareAnimals;

// Spawn conditions/locations: https://lalachievements.com/ja/islands/
// Stable BNpcName IDs work even when the client has no English sheets.
// Display names, icons and sizes are resolved from the installed game's sheets.
// MJIAnimals row IDs and icon IDs have different ordering; keep explicit row/name pairs.
internal sealed record RareAnimalDefinition(uint Id, uint NameId, string EnglishName, float X, float Y,
    int StartHour = 0, int EndHour = 24, uint Weather = 0, bool Cave = false, bool Flight = false,
    Vector2? Alternate = null) {
    public bool TimeMatches(int hour) => StartHour <= EndHour
        ? hour >= StartHour && hour < EndHour : hour >= StartHour || hour < EndHour;
    public bool Available(int hour, uint weather) => TimeMatches(hour) && (Weather == 0 || Weather == weather);
}

internal sealed record RareAnimal(RareAnimalDefinition Definition, uint NameId, uint BaseId, uint Icon, byte Size) {
    public uint Id => Definition.Id;
    // Display game names in the client's language, independently of the plugin UI language.
    public string Name => NameId != 0 && BNpcName.Get(Service.ClientState.ClientLanguage).GetRowOrDefault(NameId) is { } row && row.Singular.ToString().Length > 0
        ? row.Singular.ToString() : Definition.EnglishName;
    public uint ToolItem => 37614u + Size;
    public uint FlawlessToolItem => 41642u + Size;
    public uint ToolPouchId => 63u + Size;
    public uint FlawlessToolPouchId => 104u + Size;
    public string ToolName => Item.GetRow(ToolItem)?.Name.ToString() ?? $"#{ToolItem}";
    public bool MatchesTool(uint selected) => selected == ToolPouchId || selected == ToolItem
        || selected == FlawlessToolPouchId || selected == FlawlessToolItem;
}

internal static class RareAnimalCatalog {
    public const ushort Territory = 1055;
    public const uint CaptureModeRow = 8;
    public const string Source = "https://lalachievements.com/ja/islands/";
    public static readonly RareAnimalDefinition[] Definitions = [
        new(2, 795, "ornery karakul", 20, 23, Weather: 2),
        new(4, 6, "lemur", 20, 26, 6, 9),
        new(6, 11448, "apkallu of paradise", 19, 11, 12, 15),
        new(8, 262, "star marmot", 15, 19, 9, 12),
        new(10, 11450, "yellow coblyn", 27, 19, Weather: 4),
        new(12, 11451, "dodo of paradise", 16, 12, 15, 18),
        new(14, 11453, "island stag", 20, 19, 18, 21),
        new(16, 6942, "black chocobo", 13, 11, Weather: 1),
        new(18, 10173, "glyptodon", 31, 11, 0, 3),
        new(20, 11455, "grand buffalo", 12, 17, Weather: 3),
        new(22, 11457, "island billy", 26, 22, 3, 6),
        new(24, 2769, "gold back", 31, 28, Weather: 7),
        new(25, 11458, "twinklefleece", 22.1f, 20.8f, 18, 21, 4),
        new(26, 11459, "beachcomb", 17.8f, 12.6f, 0, 3, 7),
        new(27, 3499, "paissa", 24, 28, 12, 15, 2, Flight: true),
        new(28, 11460, "alligator", 17, 24, 6, 9, 8),
        new(29, 353, "goobbue", 33, 16, 9, 12, 3),
        new(30, 12313, "griffin", 14, 22, 15, 18, 1, Flight: true),
        new(31, 12314, "tiger of paradise", 15, 14, 18, 21, 2),
        new(32, 12315, "morbol seedling", 19, 19, 3, 6, 3),
        new(34, 12457, "amethyst spriggan", 24, 19, 21, 24, Cave: true, Alternate: new(28, 16)),
        new(36, 12459, "boar of paradise", 17, 16, Weather: 8),
        new(37, 12460, "weird spriggan", 24, 19, 0, 3, 4, Cave: true),
        new(38, 12461, "funguar", 20, 27, 15, 18, 7),
        new(39, 12462, "alkonost", 23, 30, 21, 24, 1),
        new(43, 12630, "grand doblyn", 23, 21, 3, 6, 2, Cave: true),
        new(40, 12627, "morbol", 28, 24, 21, 24, 8),
        new(41, 12628, "adamantoise", 32, 12, 12, 15, 4),
        new(42, 12629, "pteranodon", 25, 16, 9, 12, 1, Flight: true),
    ];

    public static IReadOnlyList<RareAnimal> Load() {
        return Definitions.Select(def => {
            var row = MJIAnimals.GetRow(def.Id);
            if (!row.HasValue || row.Value.BNpcBase.RowId == 0 || row.Value.Size is < 1 or > 3)
                throw new InvalidOperationException($"Unsupported MJIAnimals row {def.Id}");
            return new RareAnimal(def, def.NameId, row.Value.BNpcBase.RowId,
                (uint)row.Value.Icon, row.Value.Size);
        }).ToArray();
    }
}
