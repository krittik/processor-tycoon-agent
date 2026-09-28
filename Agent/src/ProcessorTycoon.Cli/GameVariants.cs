using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

// CLI composition: previews several die sizes / frequencies of the open CPU draft and restores it. Reads and draft edits only; never develops.
internal static class GameVariants
{
    internal static async Task<JsonObject> Run(HttpClient client, JsonObject request, int timeout)
    {
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        if (options.Select(p => p.Key).Except(new[] { "dieSizes", "frequencies", "plannedPrice", "coreCounts" }).Any()) return Error("invalid_request", "cpu-variants accepts --die-sizes, optional --frequencies, --core-counts and --planned-price.");
        var dies = List(options["dieSizes"]?.ToString());
        var cores = (options["coreCounts"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        var price = options["plannedPrice"] == null ? (double?)null : double.Parse(options["plannedPrice"]!.ToString(), CultureInfo.InvariantCulture);
        var session = "variants-cli";

        var readReply = await GameWatch.Call(client, "game.cpu-read", "", null, session, timeout);
        var draft = Data(readReply);
        if (draft == null) return Failure("draft_unavailable", readReply, "Open the CPU draft first (game cpu-preview).");
        var originalDie = Number(draft["settings"]?["dieSizeMm2"]?["value"]);
        var originalFreq = Number(draft["settings"]?["frequencyMHz"]?["value"]);
        var originalCores = draft["settings"]?["coreCount"]?["value"]?.ToString();
        var (freqs, frequencyNote) = Frequencies(options["frequencies"]?.ToString(), originalFreq);
        if (freqs == null) return Error("invalid_value", frequencyNote!);
        if (freqs.Length == 0) freqs = new[] { originalFreq };
        if (dies.Length == 0) dies = new[] { originalDie };
        if (dies.Length * freqs.Length * Math.Max(1, cores.Length) > 60) return Error("invalid_value", "At most 60 combinations per sweep.");

        // Native per-line reference (monthly units for the reference die named in its tooltip) needs the Production window, so the draft form is closed briefly; its settings persist.
        await GameWatch.Call(client, "game.window-close", "Create CPU", null, session, timeout);
        var capacityReply = await GameWatch.Call(client, "game.production-capacity", "", null, session, timeout);
        double? perLineRef = null, refDie = null, refYield = null; int lines = 0;
        if (GameWatch.TryData(capacityReply, out var capacity))
        {
            lines = (int)Number(capacity!["currentLines"]);
            var r = Regex.Match(capacity["expansion"]?["details"]?["production"]?.ToString() ?? "", @"([\d,.]+[KMB]?)\s*units");
            var b = Regex.Match(capacity["expansion"]?["detailTooltips"]?["Production"]?.ToString() ?? "", @"([\d.]+)\s*mm.*?([\d.]+)%");
            if (r.Success && b.Success) { perLineRef = DisplayNumber.First(r.Groups[1].Value); refDie = Parse(b.Groups[1].Value); refYield = Parse(b.Groups[2].Value); }
        }
        await GameWatch.Call(client, "game.window-close", "FactoryManagementWindow", null, session, timeout);
        await GameWatch.Call(client, "game.window-close", "Production", null, session, timeout);

        var rows = new JsonArray();
        foreach (var core in cores.Length == 0 ? new string?[] { null } : cores.Select(c => (string?)c).ToArray())
        foreach (var f in freqs)
            foreach (var d in dies)
            {
                var previewParameters = new JsonObject { ["dieSizeMm2"] = d, ["frequencyMHz"] = f };
                if (core != null) previewParameters["coreCount"] = core;
                var reply = await GameWatch.Call(client, "game.cpu-preview", "", previewParameters, session, timeout);
                var p = Data(reply);
                if (p == null) { rows.Add(new JsonObject { ["dieSizeMm2"] = d, ["frequencyMHz"] = f, ["coreCount"] = core, ["error"] = reply["operation"]?["error"]?.DeepClone() ?? reply["error"]?.DeepClone() }); continue; }
                var specs = p["specs"];
                // Suffix-aware ("$1.95K -> $1.20K"): plain digits used to read $1.95K as 1.95.
                var costs = DisplayNumber.Moneys(specs?["unitCost"]?.ToString());
                var yield = Parse(Regex.Match(specs?["yield"]?.ToString() ?? "", @"[\d.]+").Value);
                var row = new JsonObject
                {
                    ["dieSizeMm2"] = Number(p["settings"]?["dieSizeMm2"]?["value"]), ["frequency"] = p["settings"]?["frequencyMHz"]?["display"]?.DeepClone(),
                    ["coreCount"] = p["settings"]?["coreCount"]?["value"]?.DeepClone(), ["mips"] = specs?["mips"]?.DeepClone(), ["mipsValue"] = DisplayNumber.First(specs?["mips"]?.ToString()), ["power"] = specs?["power"]?.DeepClone(), ["temperature"] = specs?["temperature"]?.DeepClone(), ["safeTemperature"] = p["details"]?["safeTemperature"]?.DeepClone(), ["yield"] = specs?["yield"]?.DeepClone(),
                    ["unitCostNow"] = costs.Length > 0 ? costs[0] : null, ["unitCostLater"] = costs.Length > 1 ? costs[1] : costs.Length > 0 ? costs[0] : null, ["projectCost"] = p["project"]?["totalCost"]?.DeepClone()
                };
                if (perLineRef != null && yield > 0)
                {
                    var perLine = perLineRef.Value * refDie!.Value / Number(row["dieSizeMm2"]) * yield / refYield!.Value;
                    row["estUnitsPerLinePerMonth"] = Math.Round(perLine / 10) * 10;
                    row["estUnitsPerMonthAllLines"] = Math.Round(perLine * lines / 10) * 10;
                    var cost = Number(row["unitCostLater"]);
                    if (price != null && cost > 0) row["estGrossMarginPerMonthIfAllSold"] = Math.Round(perLine * lines * (price.Value - cost));
                }
                rows.Add(row);
            }
        var restore = new JsonObject { ["dieSizeMm2"] = originalDie, ["frequencyMHz"] = originalFreq };
        if (cores.Length > 0 && originalCores != null) restore["coreCount"] = originalCores;
        await GameWatch.Call(client, "game.cpu-preview", "", restore, session, timeout);
        return new JsonObject
        {
            ["ok"] = true, ["kind"] = "cpu_variants", ["currentLines"] = lines, ["plannedPrice"] = price, ["referenceBasis"] = capacity?["expansion"]?["detailTooltips"]?["Production"]?.DeepClone(), ["variants"] = rows,
            ["restoredDraft"] = restore.DeepClone(), ["frequencyUnits"] = frequencyNote,
            ["note"] = "Estimates scale the native per-line monthly reference by die area and yield (small dies can come out somewhat higher). Margin assumes every unit sells at the planned price, which holds only while demand exceeds your supply; compare with market-share unserved demand and rival specs. Die size and frequency are fixed once development starts. No project was started."
        };
    }

    private static JsonObject? Data(JsonObject reply) => reply["data"] as JsonObject ?? (GameWatch.TryData(reply, out var d) ? d : null);
    // Frequencies in MHz. Accepts unit suffixes (3.5GHz, 3500MHz, 800KHz). A bare value below 100 is read as GHz when the
    // draft's current frequency is 100 MHz or more (late game: 3.5 means 3.5 GHz); otherwise bare values are MHz (early game: 3 = 3 MHz).
    internal static (double[]? Values, string? Note) Frequencies(string? text, double currentMHz)
    {
        var values = new List<double>();
        var ghzAssumed = false;
        foreach (var raw in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^([\d.]+)\s*(ghz|mhz|khz)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return (null, $"Cannot read frequency '{raw}'. Use MHz (3500), or a unit: 3.5GHz, 3500MHz, 800KHz.");
            var unit = m.Groups[2].Value.ToLowerInvariant();
            if (unit == "" && v < 100 && currentMHz >= 100) { unit = "ghz"; ghzAssumed = true; }
            values.Add(unit switch { "ghz" => v * 1000, "khz" => v / 1000, _ => v });
        }
        return (values.ToArray(), values.Count == 0 ? null : ghzAssumed ? "Bare values below 100 were read as GHz because the draft runs at 100 MHz or more; frequencies are sent in MHz." : "Frequencies are in MHz (unit suffixes GHz/MHz/KHz accepted).");
    }

    private static double[] List(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Parse(s.Trim())).Where(v => v > 0).ToArray();
    private static double Parse(string s) => double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Number(JsonNode? node) => double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static JsonObject Error(string code, string message) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static JsonObject Failure(string code, JsonObject reply, string next) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = "A native command failed." }, ["commandReply"] = reply.DeepClone(), ["next"] = next };
}
