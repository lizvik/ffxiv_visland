using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game;
using visland.Helpers;

namespace visland.Pasture.RareAnimals;

internal sealed record AnimalPastureSnapshot(bool Known, int Capacity, uint Weather, Dictionary<uint, int> Counts,
    HashSet<uint> OwnedEntities) {
    public bool MatchesFilter(uint animalId, int filter) => filter switch {
        0 => true,
        1 => Known && Counts.GetValueOrDefault(animalId) == 0,
        2 => Known && Counts.GetValueOrDefault(animalId) > 0,
        _ => false,
    };
    public int Total {
        get { var total = 0; foreach (var count in Counts.Values) total += count; return total; }
    }
    public static unsafe AnimalPastureSnapshot Read() {
        var manager = MJIManager.Instance();
        if (!Player.Available || !Player.IsOnIsland || Player.Territory != RareAnimalCatalog.Territory
            || manager == null || !manager->IslandState.CanEditIsland || manager->PastureHandler == null)
            return new(false, 0, 0, [], []);
        var capacity = manager->GetPastureSlotCount();
        if (capacity is 0 or > 20) return new(false, 0, 0, [], []);
        var counts = new Dictionary<uint, int>();
        var entities = new HashSet<uint>();
        foreach (ref var animal in manager->PastureHandler->MJIAnimals) {
            if (animal.AnimalType != 0) {
                counts[animal.AnimalType] = counts.GetValueOrDefault(animal.AnimalType) + 1;
                entities.Add(animal.EntityId);
            }
        }
        var weather = WeatherManager.Instance();
        return new(true, capacity, weather == null ? 0u : weather->GetCurrentWeather(), counts, entities);
    }
}
