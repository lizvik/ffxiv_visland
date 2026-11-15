using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using visland.Helpers;

namespace visland.Pasture.RareAnimals;

internal sealed class RareAnimalForecast(IReadOnlyList<(uint Weather, int Rate)> rates) {
    internal const int SecondsPerHour = 175;
    private const int SearchHours = 30 * 24 * 60 * 60 / SecondsPerHour;
    private readonly Dictionary<uint, DateTimeOffset?> _next = new();
    private long _cachedHour = -1;

    public static RareAnimalForecast? Load() {
        var territory = TerritoryType.GetRow(RareAnimalCatalog.Territory);
        if (!territory.HasValue || !territory.Value.WeatherRate.IsValid) return null;
        var row = territory.Value.WeatherRate.Value;
        var rates = Enumerable.Range(0, row.Rate.Count)
            .Select(i => (Weather: row.Weather[i].RowId, Rate: (int)row.Rate[i])).ToArray();
        return rates.Sum(r => r.Rate) == 100 ? new(rates) : null;
    }

    public uint WeatherAt(long unixSeconds) {
        var target = WeatherTarget(unixSeconds);
        foreach (var (weather, rate) in rates) {
            if (target < rate) return weather;
            target -= rate;
        }
        return 0;
    }

    // FFXIV weather seed: https://github.com/Asvel/ffxiv-weather/blob/master/src/Weather.ts
    internal static int WeatherTarget(long unixSeconds) {
        var hour = unixSeconds / SecondsPerHour;
        var increment = (uint)((hour + 8 - hour % 8) % 24);
        var seed = unchecked((uint)(unixSeconds / 4200) * 100 + increment);
        var mixed = (seed << 11) ^ seed;
        return (int)(((mixed >> 8) ^ mixed) % 100);
    }

    public DateTimeOffset? Next(RareAnimalDefinition animal, DateTimeOffset now) {
        var hour = now.ToUnixTimeSeconds() / SecondsPerHour;
        if (hour != _cachedHour) {
            _next.Clear();
            _cachedHour = hour;
        }
        if (_next.TryGetValue(animal.Id, out var cached)) return cached;
        bool Matches(long h) => animal.Available((int)(h % 24), WeatherAt(h * SecondsPerHour));
        var previous = Matches(hour);
        DateTimeOffset? result = null;
        // Skip the current window; report the next transition into an available window.
        for (var h = hour + 1; h <= hour + SearchHours; ++h) {
            var available = Matches(h);
            if (available && !previous) {
                result = DateTimeOffset.FromUnixTimeSeconds(h * SecondsPerHour);
                break;
            }
            previous = available;
        }
        _next[animal.Id] = result;
        return result;
    }
}
