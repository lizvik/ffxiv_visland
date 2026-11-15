using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
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
    private readonly Dictionary<uint, Vector2> _liveLocations = new();
    private long _nextLocationRefresh;

    public void Draw() {
        var capture = Service.RareAnimalCapture;
        var snapshot = AnimalPastureSnapshot.Read();
        RefreshLocations(snapshot);
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
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Localization.GetText(R.Search));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("###" + R.Search, ref _search, 128);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Localization.GetText(R.Filter));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.Combo("###" + R.Filter, ref _filter,
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
            var location = _liveLocations.GetValueOrDefault(animal.Id, new(def.X, def.Y));
            ImGui.TextUnformatted($"X:{location.X:0.0} Y:{location.Y:0.0}");
            if (!_liveLocations.ContainsKey(animal.Id) && def.Alternate is { } alt)
                ImGui.TextUnformatted($"X:{alt.X:0.0} Y:{alt.Y:0.0}");
            ImGui.TableNextColumn();
            ImGui.TextWrapped(animal.ToolName);
            if (snapshot.Known) ImGui.TextUnformatted(Localization.FormatText(R.ToolCount,
                AnimalCaptureTools.Count(animal.ToolPouchId), AnimalCaptureTools.Count(animal.FlawlessToolPouchId)));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(snapshot.Known ? snapshot.Counts.GetValueOrDefault(animal.Id).ToString()
                : Localization.GetText(R.Unknown));
            ImGui.TableNextColumn();
            if (!snapshot.Known) ImGui.TextUnformatted(Localization.GetText(R.Unknown));
            else {
                var available = def.Available(hour, snapshot.Weather);
                using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
                using var iconColor = ImRaii.PushColor(ImGuiCol.Text, available
                    ? new Vector4(0.2f, 0.85f, 0.3f, 1f) : new Vector4(1f, 0.25f, 0.25f, 1f));
                ImGui.TextUnformatted((available
                    ? FontAwesomeIcon.Check : FontAwesomeIcon.Times).ToIconString());
            }
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
                if (map.HasValue) {
                    var live = snapshot.Known ? RareAnimalCapture.Find(animal, snapshot, targetableOnly: false) : null;
                    var flag = live == null ? location
                        : Dalamud.Utility.MapUtil.WorldToMap(new Vector2(live.Position.X, live.Position.Z), map.Value);
                    Service.GameGui.OpenMapWithMapLink(new MapLinkPayload(RareAnimalCatalog.Territory,
                        map.Value.RowId, flag.X, flag.Y));
                }
            }
            if (ImGui.SmallButton(Localization.GetLabel(R.Capture))) capture.Start(animal);
            ImGui.PopID();
        }
    }

    private void RefreshLocations(AnimalPastureSnapshot snapshot) {
        if (!snapshot.Known) { _liveLocations.Clear(); _nextLocationRefresh = 0; return; }
        var now = Environment.TickCount64;
        if (now < _nextLocationRefresh) return;
        _nextLocationRefresh = now + 500;
        _liveLocations.Clear();
        var map = RareAnimalCapture.TerritoryTypeMap();
        if (!map.HasValue) return;
        foreach (var animal in _animals) {
            var live = RareAnimalCapture.Find(animal, snapshot, targetableOnly: false);
            if (live != null) _liveLocations[animal.Id] = Dalamud.Utility.MapUtil.WorldToMap(
                new Vector2(live.Position.X, live.Position.Z), map.Value);
        }
    }

    private bool Matches(RareAnimal animal, AnimalPastureSnapshot snapshot, int hour) {
        if (_search.Length > 0 && !animal.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            && !animal.Definition.EnglishName.Contains(_search, StringComparison.OrdinalIgnoreCase)) return false;
        if (!snapshot.MatchesFilter(animal.Id, _filter)) return false;
        return !_availableOnly || snapshot.Known && animal.Definition.Available(hour, snapshot.Weather);
    }
}
