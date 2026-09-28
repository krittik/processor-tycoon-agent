using System.Globalization;
using System.Text.Json.Nodes;

// Research observation helpers. Signals compare displayed research times between reads (research becomes faster once
// rivals have reached a technology); the funding comparison previews several funding levels and restores the original.
internal static class ResearchTools
{
    internal sealed record Tech(string Name, double? Days, double? MonthlyCost);

    internal sealed class Snapshot
    {
        public string Date = "";
        public Tech? Active;
        public double? FundingPercent;
        public Dictionary<string, Tech> Available = new(StringComparer.Ordinal);
    }

    internal static double? Days(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        var n = DisplayNumber.First(t);
        if (n == null) return null;
        return t.Contains("year") ? n * 365 : t.Contains("month") ? n * 30 : n;
    }

    internal static Snapshot Parse(string date, JsonObject? read, JsonObject? list)
    {
        var s = new Snapshot { Date = date };
        var active = read?["active"] as JsonObject ?? list?["active"] as JsonObject;
        if (active != null) { s.Active = new Tech(active["name"]?.ToString() ?? "", Days(active["timeLeft"]?.ToString()), DisplayNumber.Money(active["fundingDisplay"]?.ToString())); s.FundingPercent = CliReads.Num(active["fundingPercent"]); }
        foreach (var t in list?["technologies"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (t["name"]?.ToString() is string name) s.Available[name] = new Tech(name, Days(t["timeDisplay"]?.ToString()), DisplayNumber.Money(t["costDisplay"]?.ToString()));
        return s;
    }

    // Pure comparison: a sharp drop beyond the elapsed days (active) or at unchanged funding (available) is reported.
    internal static List<JsonObject> Diff(Snapshot before, Snapshot after, double minDropPercent = 15, double minDropDays = 5)
    {
        var notes = new List<JsonObject>();
        var elapsed = CliReads.Date(after.Date) is DateTime a && CliReads.Date(before.Date) is DateTime b ? Math.Max(0, (a - b).TotalDays) : 0;
        void Check(Tech then, Tech now, double expected, string status)
        {
            if (then.Days is not double old || now.Days is not double current) return;
            var drop = expected - current;
            if (drop < minDropDays || drop < old * minDropPercent / 100) return;
            notes.Add(MarketSignals.Note("research_time_dropped", after.Date, before.Date, FormattableString.Invariant($"{now.Name} ({status}) now shows {current:0} days, {drop:0} fewer than expected ({old:0} days on {before.Date})"), new JsonObject
            {
                ["technology"] = now.Name, ["status"] = status, ["previousDays"] = old, ["elapsedDays"] = elapsed, ["expectedDays"] = Math.Round(expected), ["currentDays"] = current, ["dropDays"] = Math.Round(drop),
                ["note"] = "Research becomes cheaper and faster once rival companies have researched a technology. A sharp drop usually means a rival reached it; it can also follow a funding or innovation change."
            }));
        }
        if (before.Active != null && after.Active != null && before.Active.Name == after.Active.Name && SameFunding(before, after)) Check(before.Active, after.Active, (before.Active.Days ?? 0) - elapsed, "active");
        if (SameFunding(before, after))
            foreach (var (name, now) in after.Available)
                if (before.Available.TryGetValue(name, out var then)) Check(then, now, then.Days ?? 0, "available");
        return notes;
    }

    private static bool SameFunding(Snapshot a, Snapshot b) => a.FundingPercent == null || b.FundingPercent == null || Math.Abs(a.FundingPercent.Value - b.FundingPercent.Value) < 0.5;

    // Reads research-read + research-list (available), diffs against the stored research snapshot and records notifications.
    internal static async Task<(JsonObject? Active, List<JsonObject> Notes)> Observe(HttpClient client, string session, int timeout, string? date)
    {
        var read = await CliReads.Data(client, "game.research-read", null, session, timeout);
        var list = await CliReads.Data(client, "game.research-list", new JsonObject { ["status"] = "available" }, session, timeout);
        var notes = new List<JsonObject>();
        if (date == null || read == null && list == null) return (read?["active"] as JsonObject, notes);
        var store = MarketSignals.Load(date);
        var now = Parse(date, read, list);
        if (store["research"] is JsonObject previous && CliReads.Date(previous["date"]?.ToString()) is DateTime then && CliReads.Date(date) is DateTime today && then <= today)
        {
            notes = Diff(FromJson(previous), now);
            foreach (var note in notes) MarketSignals.Append(store, note);
        }
        store["research"] = ToJson(now);
        MarketSignals.Save(store);
        return (read?["active"] as JsonObject, notes);
    }

    private static JsonObject ToJson(Snapshot s)
    {
        var available = new JsonObject();
        foreach (var (k, t) in s.Available) available[k] = new JsonObject { ["days"] = t.Days, ["monthlyCost"] = t.MonthlyCost };
        return new JsonObject { ["date"] = s.Date, ["fundingPercent"] = s.FundingPercent, ["active"] = s.Active == null ? null : new JsonObject { ["name"] = s.Active.Name, ["days"] = s.Active.Days, ["monthlyCost"] = s.Active.MonthlyCost }, ["available"] = available };
    }

    private static Snapshot FromJson(JsonObject o)
    {
        var s = new Snapshot { Date = o["date"]?.ToString() ?? "", FundingPercent = CliReads.Num(o["fundingPercent"]) };
        if (o["active"] is JsonObject a) s.Active = new Tech(a["name"]?.ToString() ?? "", CliReads.Num(a["days"]), CliReads.Num(a["monthlyCost"]));
        foreach (var (k, v) in o["available"]?.AsObject() ?? new JsonObject()) s.Available[k] = new Tech(k, CliReads.Num(v?["days"]), CliReads.Num(v?["monthlyCost"]));
        return s;
    }

    // game research-funding-compare --funding-percents 25,50,100,200: sets each level on the active research while time is
    // paused, records the native monthly cost, speed and time left, then restores the original funding (and innovation).
    internal static async Task<JsonObject> Compare(HttpClient client, JsonObject request, int timeout)
    {
        var session = "research-compare-cli";
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        if (options.Select(p => p.Key).Except(new[] { "fundingPercents" }).Any() || (request["target"]?.ToString() ?? "").Length > 0) return Error("invalid_request", "research-funding-compare takes only --funding-percents A,B,C.");
        var levels = (options["fundingPercents"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).Distinct().ToArray();
        if (levels.Length == 0 || levels.Any(l => l < 0)) return Error("invalid_value", "--funding-percents needs a comma-separated list of integers, for example 25,50,100.");
        var clock = await CliReads.Data(client, "game.time-read", null, session, timeout);
        // A Multiplayer session's shared clock keeps running; each level is set and read within about a second there.
        var sessionClock = clock?["multiplayerSession"]?.GetValue<bool>() == true;
        if (clock?["paused"]?.GetValue<bool>() != true && !sessionClock) return Error("time_running", "Game time must be paused (the comparison edits funding temporarily). Nothing was changed.");
        var read = await CliReads.Data(client, "game.research-read", null, session, timeout);
        var active = read?["active"] as JsonObject;
        if (active == null) return Error("no_active_research", "No research is running; nothing to compare.");
        var original = (int)Math.Round(CliReads.Num(active["fundingPercent"]) ?? 0);
        var innovation = active["innovationEffort"]?.GetValue<bool>() == true;
        var max = CliReads.Num(active["controls"]?["funding"]?["max"]) ?? 100;
        var min = CliReads.Num(active["controls"]?["funding"]?["min"]) ?? 0;
        var rows = new JsonArray();
        foreach (var level in levels)
        {
            if (level < min || level > max) { rows.Add(new JsonObject { ["fundingPercent"] = level, ["skipped"] = $"outside the current native range {min}-{max}" + (level > max && !innovation ? " (higher funding needs innovation effort; enable it yourself with research-set --innovation-effort true to include such levels)" : "") }); continue; }
            var set = await CliReads.Data(client, "game.research-set", new JsonObject { ["fundingPercent"] = level }, session, timeout);
            var after = set?["active"] as JsonObject ?? (await CliReads.Data(client, "game.research-read", null, session, timeout))?["active"] as JsonObject;
            var monthly = DisplayNumber.Money(after?["fundingDisplay"]?.ToString());
            var days = Days(after?["timeLeft"]?.ToString());
            rows.Add(new JsonObject
            {
                ["fundingPercent"] = level, ["monthlyCost"] = monthly, ["timeLeftDays"] = days, ["researchSpeed"] = after?["researchSpeed"]?.DeepClone(),
                ["estimatedRemainingCost"] = monthly != null && days != null ? Math.Round(monthly.Value * days.Value / 30) : null, ["display"] = new JsonObject { ["funding"] = after?["fundingDisplay"]?.DeepClone(), ["timeLeft"] = after?["timeLeft"]?.DeepClone() }
            });
        }
        var restore = await CliReads.Data(client, "game.research-set", new JsonObject { ["fundingPercent"] = original }, session, timeout);
        var final = restore?["active"] as JsonObject ?? (await CliReads.Data(client, "game.research-read", null, session, timeout))?["active"] as JsonObject;
        var restored = final != null && Math.Abs((CliReads.Num(final["fundingPercent"]) ?? -1) - original) < 0.5 && (final["innovationEffort"]?.GetValue<bool>() == true) == innovation;
        return new JsonObject
        {
            ["ok"] = restored, ["kind"] = "research_funding_compare", ["technology"] = active["name"]?.DeepClone(), ["originalFundingPercent"] = original, ["innovationEffort"] = innovation, ["levels"] = rows,
            ["restored"] = restored, ["error"] = restored ? null : new JsonObject { ["code"] = "restore_unverified", ["message"] = $"The original funding {original}% could not be verified after the comparison. Check research-read and set it explicitly." },
            ["note"] = (sessionClock && clock?["paused"]?.GetValue<bool>() != true ? "Multiplayer session: the shared clock kept running, so a day or two may have been billed at the temporary levels. " : "") + "Native displayed monthly cost and time left at each level" + (sessionClock && clock?["paused"]?.GetValue<bool>() != true ? "; " : ", read while time was paused; ") + "estimatedRemainingCost = monthly cost x days left / 30 (display-rounded, excluding future cost changes such as rivals reaching the technology). The original funding was restored; nothing else was changed."
        };
    }

    private static JsonObject Error(string code, string message) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
