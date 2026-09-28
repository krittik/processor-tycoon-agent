using System.Text.Json.Nodes;

// Monthly digest (reads only): market share and its change per market/segment, rival events of the month, the largest
// own supply/demand gaps, idle lines, stock value, cash runway and upcoming completions. The previous digest's shares
// are kept in tools/monthly-digest.json to compute deltas.
internal static class Digest
{
    private const string File = "monthly-digest.json";

    internal static async Task<JsonObject> Run(HttpClient client, string session, int timeout, string? date = null)
    {
        date ??= (await CliReads.Data(client, "game.time-read", null, session, timeout))?["date"]?.ToString();
        var desktop = await CliReads.Data(client, "game.desktop-read", null, session, timeout);
        var production = await CliReads.Data(client, "game.production-read", null, session, timeout);
        var sales = await CliReads.Sales(client, session, timeout);
        var projects = await CliReads.Data(client, "game.projects-list", null, session, timeout);
        var research = await CliReads.Data(client, "game.research-read", null, session, timeout);
        var shares = new CliReads.Shares();
        var segmentShares = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var filter in new[] { "All", "Desktop", "Mobile" })
        {
            var data = await CliReads.Data(client, "game.market-share", new JsonObject { ["market"] = filter, ["category"] = "CPU Sales" }, session, timeout);
            if (data == null) continue;
            CliReads.ParseShares(shares, filter, data);
            if (filter == "All") continue;
            foreach (var pair in data["markets"]?.AsObject() ?? new JsonObject())
            {
                var companies = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var s in pair.Value?["shares"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) if (s["company"]?.ToString() is string c && CliReads.Num(s["share"]) is double v) companies[c] = v;
                segmentShares[filter + " " + (pair.Value?["displayName"]?.ToString() ?? pair.Key)] = companies;
            }
        }
        var store = MarketSignals.Load(date);
        var player = store["player"]?.ToString();
        var previous = CliReads.LoadJson(File);
        if (CliReads.Date(previous?["date"]?.ToString()) > CliReads.Date(date)) previous = null;

        var shareRows = new JsonArray();
        var current = new JsonObject();
        void Share(string market, Dictionary<string, double> companies, double? size, double? unserved)
        {
            double? mine = player != null && companies.TryGetValue(player, out var v) ? v : null;
            var before = CliReads.Num(previous?["shares"]?[market]);
            current[market] = mine;
            shareRows.Add(new JsonObject { ["market"] = market, ["sharePercent"] = mine, ["changePoints"] = mine != null && before != null ? Math.Round(mine.Value - before.Value, 2) : null, ["unitsPerMonth"] = size, ["unservedPercent"] = unserved });
        }
        foreach (var (market, m) in shares.Markets) Share(market, m.Companies, m.Size, m.Unserved);
        foreach (var (segment, companies) in segmentShares) Share(segment, companies, null, companies.TryGetValue("Potential Sales", out var u) ? u : null);

        var rows = (production?["products"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()).Select(ProductionRows.FromRow).ToList();
        var gaps = rows.Select(r => (Row: r, Gap: (CliReads.Num(r["demandPerMonth"]) ?? 0) - (CliReads.Num(r["productionPerMonth"]) ?? 0))).OrderByDescending(x => Math.Abs(x.Gap)).Take(5)
            .Select(x => (JsonNode?)new JsonObject { ["product"] = x.Row["name"]?.DeepClone(), ["demandPerMonth"] = x.Row["demandPerMonth"]?.DeepClone(), ["productionPerMonth"] = x.Row["productionPerMonth"]?.DeepClone(), ["gapPerMonth"] = Math.Round(x.Gap), ["kind"] = x.Gap > 0 ? "demand_exceeds_production" : "production_exceeds_demand", ["stockUnits"] = x.Row["stockUnits"]?.DeepClone(), ["outOfStock"] = x.Row["outOfStock"]?.DeepClone() }).ToArray();
        var lines = production == null ? null : ProductionRows.Lines(production);
        var stockValue = sales == null ? (double?)null : Math.Round(rows.Sum(r => (CliReads.Num(r["stockUnits"]) ?? 0) * (sales.GetValueOrDefault(r["name"]?.ToString() ?? "")?.UnitCost ?? 0)));
        var fin = desktop?["finances"] as JsonObject;
        var credit = DisplayNumber.Money(fin?["availableCredit"]?.ToString()) ?? 0;
        var cash = DisplayNumber.Money(fin?["cashDisplay"]?.ToString()) ?? 0;
        var balance = DisplayNumber.Money(fin?["balanceDisplay"]?.ToString());
        var headroom = credit + Math.Max(0, cash);
        var upcoming = new JsonArray((projects?["projects"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()).Where(p => CliReads.Num(p["timeLeftDays"]) is double d && d <= 45).Select(p => (JsonNode?)new JsonObject { ["name"] = p["name"]?.DeepClone(), ["timeLeftDays"] = p["timeLeftDays"]?.DeepClone(), ["status"] = p["status"]?.DeepClone() }).ToArray());
        if (research?["active"] is JsonObject active && ResearchTools.Days(active["timeLeft"]?.ToString()) is double left && left <= 45) upcoming.Add(new JsonObject { ["name"] = active["name"]?.DeepClone(), ["timeLeftDays"] = left, ["status"] = "research" });

        CliReads.SaveJson(File, new JsonObject { ["date"] = date, ["shares"] = current });
        return new JsonObject
        {
            ["kind"] = "monthly_digest", ["date"] = date, ["comparedWith"] = previous?["date"]?.DeepClone(), ["player"] = player,
            ["marketShare"] = shareRows,
            ["rivalEvents"] = MarketSignals.Recent(store, date, 31),
            ["supplyDemandGaps"] = new JsonArray(gaps),
            ["idleLines"] = lines?["unassigned"]?.DeepClone(), ["lines"] = lines == null ? null : new JsonObject { ["capacity"] = lines["capacity"]?.DeepClone(), ["unassigned"] = lines["unassigned"]?.DeepClone(), ["pendingExpansion"] = lines["pendingExpansion"]?.DeepClone() },
            ["stockValueAtUnitCost"] = stockValue,
            ["finance"] = new JsonObject { ["availableCredit"] = credit, ["cashOnHand"] = Math.Max(0, cash), ["headroomBeforeDefault"] = headroom, ["monthlyBalance"] = balance, ["runwayMonths"] = balance < 0 ? Math.Round(headroom / -balance.Value, 1) : null },
            ["upcomingCompletions"] = upcoming,
            ["note"] = "Reads only. Share changes compare with the previous digest in this timeline; rivalEvents are the CLI-synthesized notifications of the last 31 days (they exist only for days the market tables were read, e.g. by watch-advance, situation or market-changes)."
        };
    }
}
