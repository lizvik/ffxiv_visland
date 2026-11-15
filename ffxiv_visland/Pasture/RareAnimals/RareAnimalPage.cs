using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using visland.Helpers;
using R = visland.Helpers.LocalizationKeys.RareAnimals;

namespace visland.Pasture.RareAnimals;

internal sealed class RareAnimalPage {
    private readonly IReadOnlyList<RareAnimal> _animals = RareAnimalCatalog.Load();
    private readonly RareAnimalForecast? _forecast = RareAnimalForecast.Load();
    private string _search = "";
    private int _filter;
    private bool _availableOnly;

    public void Draw() {
        var capture = Service.RareAnimalCapture;
        var snapshot = AnimalPastureSnapshot.Read();
        var hour = Utils.EorzeanHour();
        var now = DateTimeOffset.UtcNow;
        ImGui.TextWrapped(Localization.GetText(R.Description));
        if (snapshot.Known) {
            var owned = _animals.Count(a => snapshot.Counts.ContainsKey(a.Id));
            ImGui.TextUnformatted(Localization.FormatText(R.Progress, owned, _animals.Count, snapshot.Total, snapshot.Capacity));
            ImGui.TextUnformatted(Localization.FormatText(R.CurrentConditions, hour,
                Weather.GetRow(snapshot.Weather)?.Name.ToString() ?? "?"));
        }
        else ImGui.TextWrapped(Localization.GetText(R.OwnedDataUnavailable));
        ImGui.TextWrapped(Localization.GetText(R.ToolsHelp));
        if (capture.Active || capture.StatusKey != R.Idle) {
            ImGui.TextWrapped(Localization.GetText(capture.StatusKey));
            if (capture.Active) {
                ImGui.SameLine();
                if (ImGui.SmallButton(Localization.GetLabel(R.Stop))) capture.Stop();
            }
        }
        ImGui.SetNextItemWidth(220);
        ImGui.InputText(Localization.GetLabel(R.Search), ref _search, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.Combo(Localization.GetLabel(R.Filter), ref _filter,
            new[] { Localization.GetText(R.All), Localization.GetText(R.Uncaptured), Localization.GetText(R.Captured) }, 3);
        ImGui.Checkbox(Localization.GetLabel(R.AvailableOnly), ref _availableOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton(Localization.GetLabel(R.Source))) Dalamud.Utility.Util.OpenLink(RareAnimalCatalog.Source);

        var rows = _animals.Where(a => Matches(a, snapshot, hour)).ToArray();
        if (rows.Length == 0) { ImGui.TextUnformatted(Localization.GetText(R.NoResults)); return; }
        using var table = ImRaii.Table("RareAnimals", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table) return;
        foreach (var title in new[] { R.Animal, R.Size, R.Conditions, R.Location, R.Tools, R.OwnedCount, R.Status, R.NextAppearance, R.Actions })
            ImGui.TableSetupColumn(Localization.GetText(title));
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var animal in rows) {
            ImGui.PushID((int)animal.Id);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var icon = Service.TextureProvider.GetFromGameIcon(new GameIconLookup(animal.Icon)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new Vector2(24));
            ImGui.SameLine();
            ImGui.TextWrapped(animal.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Localization.GetText(animal.Size switch { 1 => R.Small, 2 => R.Medium, _ => R.Large }));
            ImGui.TableNextColumn();
            var def = animal.Definition;
            ImGui.TextUnformatted(def.StartHour == 0 && def.EndHour == 24 ? Localization.GetText(R.AnyTime)
                : $"ET {def.StartHour:00}:00–{def.EndHour:00}:00");
            ImGui.TextWrapped(def.Weather == 0 ? Localization.GetText(R.AnyWeather)
                : Weather.GetRow(def.Weather)?.Name.ToString() ?? $"#{def.Weather}");
            if (def.Flight) ImGui.TextWrapped(Localization.GetText(R.FlightRequired));
            ImGui.TableNextColumn();
            if (def.Cave) ImGui.TextUnformatted(Localization.GetText(R.Cave));
            ImGui.TextUnformatted($"X:{def.X:0.#} Y:{def.Y:0.#}");
            if (def.Alternate is { } alt) ImGui.TextUnformatted($"X:{alt.X:0.#} Y:{alt.Y:0.#}");
            ImGui.TableNextColumn();
            ImGui.TextWrapped(animal.ToolName);
            if (snapshot.Known) ImGui.TextUnformatted(Localization.FormatText(R.ToolCount,
                AnimalCaptureTools.Count(animal.ToolPouchId), AnimalCaptureTools.Count(animal.FlawlessToolPouchId)));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(snapshot.Known ? snapshot.Counts.GetValueOrDefault(animal.Id).ToString()
                : Localization.GetText(R.Unknown));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(Localization.GetText(!snapshot.Known ? R.Unknown
                : def.Available(hour, snapshot.Weather) ? R.Available : R.Unavailable));
            ImGui.TableNextColumn();
            if (_forecast?.Next(def, now) is { } next) {
                var wait = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((next - now).TotalSeconds)));
                ImGui.TextUnformatted(Localization.FormatText(R.NextAppearanceTime, next.ToLocalTime(),
                    (int)wait.TotalHours, wait.Minutes, wait.Seconds));
            }
            else ImGui.TextUnformatted(Localization.GetText(R.Unknown));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton(Localization.GetLabel(R.Map))) {
                var map = RareAnimalCapture.TerritoryTypeMap();
                if (map.HasValue) Service.GameGui.OpenMapWithMapLink(new MapLinkPayload(RareAnimalCatalog.Territory,
                    map.Value.RowId, def.X, def.Y));
            }
            if (ImGui.SmallButton(Localization.GetLabel(R.Capture))) capture.Start(animal);
            ImGui.PopID();
        }
    }

    private bool Matches(RareAnimal animal, AnimalPastureSnapshot snapshot, int hour) {
        if (_search.Length > 0 && !animal.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            && !animal.Definition.EnglishName.Contains(_search, StringComparison.OrdinalIgnoreCase)) return false;
        if (!snapshot.MatchesFilter(animal.Id, _filter)) return false;
        return !_availableOnly || snapshot.Known && animal.Definition.Available(hour, snapshot.Weather);
    }
}
