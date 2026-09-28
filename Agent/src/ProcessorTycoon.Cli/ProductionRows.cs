using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

// Parses native Production rows, product pulses and finance displays into compact per-product values.
// Everything is display-rounded player-visible text; nothing is inferred from hidden game data.
internal static class ProductionRows
{
    internal const string LinesSemantics = "lines = total lines producing the CPU (manual + contract). manualLines = your own (non-contract, market) allocation: the Production slider value that product-production --lines sets. contractLines = lines the game dedicates to active contracts for that CPU (total - manual); --lines never sets them. requestedLines appears only when more lines are assigned than the factory can run (capacity-limited); lines is then the effective count. The slider accepts 0..(manual lines + unassigned lines).";

    internal sealed record Indicator(bool Available, double? Value, bool OutOfStock, string? Display, string? Reason);

    internal static Indicator Read(IEnumerable<string> display, string name)
    {
        var values = display.Select(text => Regex.Match(text, $@"^{Regex.Escape(name)}:\s*\|\s*(.+)$")).Where(m => m.Success).Select(m => m.Groups[1].Value.Trim()).Distinct().ToArray();
        if (values.Length != 1) return new(false, null, false, null, $"Expected one native {name} indicator, found {values.Length}.");
        if (name == "Stock" && values[0].StartsWith("Out of Stock", StringComparison.OrdinalIgnoreCase)) return new(true, 0, true, values[0], null);
        if (!Regex.IsMatch(values[0], @"^-?[\d,]+(?:\.\d+)?[KMBT]?(?:/(m|d))?$") || !DisplayNumber.TryParse(values[0], out var value)) return new(false, null, false, values[0], "Native indicator format was not recognized.");
        return new(true, value, false, values[0], null);
    }

    internal static double? Money(string? text) => DisplayNumber.Money(text);

    // Value of a native "Label: | value" summary field, suffix-aware ("Production lines: | 1.15K (+50)" -> 1150, not 1).
    internal static int? LeadingInt(string? text) => DisplayNumber.FieldInt(text);

    internal static double? Number(JsonNode? node) => node != null && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // Exact line totals: plugin >= 0.4.0 reports `lines` natively; older plugins get the same shared computation here.
    internal static JsonObject Lines(JsonObject production)
    {
        if (production["lines"] is JsonObject native && native["capacity"] != null) return native.DeepClone().AsObject();
        var summary = production["summary"] as JsonObject;
        var rows = (production["products"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()).Select(p => new LineAccounting.Row(p["name"]?.ToString() ?? "", (int?)Number(p["requestedTotalLines"]) ?? (int?)Number(p["productionLines"]) ?? 0, (int?)Number(p["productionLines"]) ?? 0, (int?)Number(p["manualLines"]), (int?)Number(p["availableRange"]?["maxLines"]))).ToList();
        var r = LineAccounting.Compute(summary?["productionLines"]?.ToString(), summary?["linesUsed"]?.ToString(), summary?["usedByContracts"]?.ToString(), summary?["usedByClients"]?.ToString(), rows);
        return new JsonObject
        {
            ["capacity"] = r.Capacity, ["used"] = r.Used, ["unassigned"] = r.Unassigned, ["pendingExpansion"] = r.Pending, ["contracts"] = r.Contracts, ["clients"] = r.Clients, ["overAssigned"] = r.OverAssigned,
            ["rowsRequestedTotal"] = r.RowsRequested, ["rowsEffectiveTotal"] = r.RowsEffective,
            ["exact"] = new JsonObject { ["capacity"] = r.CapacityExact, ["used"] = r.UsedExact, ["unassigned"] = r.UnassignedExact }, ["basis"] = r.Basis,
            ["display"] = new JsonObject { ["capacity"] = r.CapacityDisplay, ["capacityPrecision"] = r.CapacityPrecision, ["used"] = r.UsedDisplay, ["usedPrecision"] = r.UsedPrecision },
            ["consistent"] = r.Consistent, ["problems"] = r.Problems.Count > 0 ? new JsonArray(r.Problems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) : null
        };
    }

    // Adds manualLines/contractLines to production-read style rows. Plugins >= 0.3.9 report the native slider value;
    // older plugins get a derivation from the slider range (manual = max - unassigned), accepted only when the
    // contract lines it implies add up to the native "Used by contracts" total.
    internal static void EnrichLines(JsonObject production)
    {
        var rows = production["products"]?.AsArray().OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        var summary = production["summary"] as JsonObject;
        var total = LeadingInt(summary?["productionLines"]?.ToString());
        var used = LeadingInt(summary?["linesUsed"]?.ToString());
        var contracts = LeadingInt(summary?["usedByContracts"]?.ToString());
        var free = total != null && used != null ? total - used : null;
        var derived = new Dictionary<JsonObject, (int Manual, int Contract)>();
        foreach (var row in rows)
        {
            if (row["manualLines"] != null) { row["linesBasis"] ??= "native_slider"; continue; }
            var requested = (int?)Number(row["requestedTotalLines"]) ?? (int?)Number(row["productionLines"]);
            var max = (int?)Number(row["availableRange"]?["maxLines"]);
            if (requested == null || max == null || free == null) continue;
            var manual = max.Value - free.Value;
            if (manual < 0 || manual > requested) continue;
            derived[row] = (manual, requested.Value - manual);
        }
        var allDerivable = rows.All(row => row["manualLines"] != null || derived.ContainsKey(row));
        var nativeContracts = rows.Where(row => row["manualLines"] != null).Sum(row => (int?)Number(row["contractLines"]) ?? 0);
        var consistent = allDerivable && contracts != null && nativeContracts + derived.Values.Sum(v => v.Contract) == contracts;
        foreach (var row in rows.Where(row => row["manualLines"] == null))
        {
            if (consistent && derived.TryGetValue(row, out var split)) { row["manualLines"] = split.Manual; row["contractLines"] = split.Contract; row["linesBasis"] = "derived_from_slider_range"; }
            else { row["manualLines"] = null; row["contractLines"] = null; row["linesBasis"] = "unknown"; }
        }
        production["linesSemantics"] = LinesSemantics;
    }

    // Compact product view shared by watch-advance, products-pulse and situation. Out of stock counts as 0 units.
    internal static JsonObject FromRow(JsonObject row)
    {
        var display = row["display"]?.AsArray().Select(d => d?.ToString() ?? "").ToArray() ?? Array.Empty<string>();
        var lines = (int?)Number(row["productionLines"]);
        var compact = Compact(row["name"]?.ToString() ?? "", row["productRef"]?.ToString(), lines, (int?)Number(row["requestedTotalLines"]), (int?)Number(row["manualLines"]), (int?)Number(row["contractLines"]), Read(display, "Demand"), Read(display, "Production"), Read(display, "Stock"), Read(display, "Balance"));
        // Unassigned lines this product's slider could still take (plugin sliders: max - manual).
        if (Number(row["availableRange"]?["maxLines"]) is double max && Number(row["manualLines"]) is double manual) compact["sliderMaxLines"] = (int)max;
        return compact;
    }

    internal static JsonObject FromPulse(JsonObject pulse)
    {
        var p = pulse["product"] as JsonObject ?? new JsonObject();
        return Compact(p["name"]?.ToString() ?? "", p["productRef"]?.ToString(), (int?)Number(p["productionLines"]), (int?)Number(p["requestedTotalLines"]), (int?)Number(p["manualLines"]), (int?)Number(p["contractLines"]), Typed(p["demand"]), Typed(p["production"]), Typed(p["stock"]), Typed(p["balance"]));
    }

    private static Indicator Typed(JsonNode? node)
    {
        if (node?["available"]?.GetValue<bool>() != true) return new(false, null, false, node?["display"]?.ToString(), node?["reason"]?.ToString() ?? "unavailable");
        var outOfStock = node["outOfStock"]?.GetValue<bool>() == true;
        return new(true, outOfStock ? 0 : Number(node["value"]), outOfStock, node["display"]?.ToString(), null);
    }

    private static JsonObject Compact(string name, string? productRef, int? lines, int? requested, int? manual, int? contract, Indicator demand, Indicator production, Indicator stock, Indicator balance)
    {
        var unavailable = new JsonArray();
        var productionValue = production.Value;
        // A product without lines produces nothing even if the native row omits or blanks its rate.
        if (productionValue == null && lines == 0) productionValue = 0;
        else if (productionValue == null) unavailable.Add("production");
        if (!demand.Available) unavailable.Add("demand");
        if (!stock.Available) unavailable.Add("stock");
        var result = new JsonObject
        {
            ["name"] = name, ["productRef"] = productRef, ["lines"] = lines, ["manualLines"] = manual, ["contractLines"] = contract,
            ["demandPerMonth"] = demand.Value, ["productionPerMonth"] = productionValue, ["stockUnits"] = stock.Available ? stock.Value : null, ["outOfStock"] = stock.OutOfStock,
            ["demandToProduction"] = demand.Value != null && productionValue > 0 ? Math.Round(demand.Value.Value / productionValue.Value, 3) : null,
            ["balancePerDay"] = balance.Value
        };
        // Lines requested above the factory's capacity: `lines` is what actually produces, `requestedLines` what is assigned.
        if (requested != null && lines != null && requested != lines) result["requestedLines"] = requested;
        if (unavailable.Count > 0) result["unavailable"] = unavailable;
        return result;
    }

    // Company finance values from product-pulse, time-advance or desktop-read results.
    internal static JsonObject Finance(JsonObject? pulseFinances = null, JsonObject? advanceFinances = null, JsonObject? desktopFinances = null)
    {
        double? balance = null, credit = null, sales = null; string? cash = null; JsonNode? defaultState = null;
        if (pulseFinances?["available"]?.GetValue<bool>() == true)
        {
            balance = Number(pulseFinances["balance"]?["value"]); credit = Number(pulseFinances["credit"]?["value"]); sales = Number(pulseFinances["sales"]?["value"]); defaultState = pulseFinances["default"];
            cash = pulseFinances["cash"]?["display"]?.ToString();
        }
        else if (advanceFinances?["available"]?.GetValue<bool>() == true)
        {
            balance = Money(advanceFinances["balance"]?.ToString()); credit = Money(advanceFinances["availableCredit"]?.ToString()); cash = advanceFinances["cash"]?.ToString(); defaultState = advanceFinances["default"];
        }
        else if (desktopFinances?["available"]?.GetValue<bool>() == true)
        {
            balance = Money(desktopFinances["balanceDisplay"]?.ToString()); credit = Money(desktopFinances["availableCredit"]?.ToString()); cash = desktopFinances["cashDisplay"]?.ToString(); defaultState = desktopFinances["default"];
            sales = Money(desktopFinances["items"]?.AsArray().FirstOrDefault(i => i?["key"]?.ToString() == "sales")?["display"]?.ToString());
        }
        else return new JsonObject { ["available"] = false };
        var result = new JsonObject { ["available"] = true, ["balancePerMonth"] = balance, ["availableCredit"] = credit };
        if (sales != null) result["salesPerMonth"] = sales;
        if (cash != null) result["cash"] = cash;
        // Available Credit excludes positive cash (native rule); money spendable before default is credit + cash on hand.
        if (credit != null) result["headroomBeforeDefault"] = credit + Math.Max(0, Money(cash) ?? 0);
        result["defaultActive"] = defaultState?["active"]?.GetValue<bool>() == true;
        if (defaultState?["active"]?.GetValue<bool>() == true) { result["defaultDaysLeft"] = defaultState["daysLeft"]?.DeepClone(); result["default"] = defaultState.DeepClone(); }
        return result;
    }
}
