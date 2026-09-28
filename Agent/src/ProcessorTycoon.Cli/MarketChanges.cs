using System.Text.Json.Nodes;

// Compares the current market tables (Desktop, Mobile, Industries) with the stored snapshot and records
// mod-synthesized notifications (rival price changes, leader changes, recommended-price moves, unserved spikes).
// The game announces new CPUs but not price changes, so this makes rival price moves visible.
internal static class MarketChanges
{
    internal static readonly string[] Markets = CliReads.CatalogMarkets;

    internal sealed class Observation
    {
        public string? Date;
        public Dictionary<string, List<CliReads.CatalogRow>> Catalogs = new(StringComparer.Ordinal);
        public CliReads.Shares? Shares;
        public MarketSignals.DiffResult? Diff;
        public JsonObject Store = new();
        public string? Player;
        public string? PreviousSnapshotDate;
        public JsonArray NewRivalCpus = new();
    }

    // One observation of the market tables. `shares` adds segment prices and unserved demand (3 market-share reads);
    // `marketView` adds rival popularity (3 more reads). Specs alone are 3 reads.
    internal static async Task<Observation> Observe(HttpClient client, string session, int timeout, ISet<string> own, string? date, bool shares, bool marketView, Thresholds? thresholds = null)
    {
        var o = new Observation { Date = date };
        o.Catalogs = await CliReads.Catalogs(client, session, timeout);
        o.Store = MarketSignals.Load(date);
        // Segment prices are needed to classify price moves; read them when the stored snapshot has none for a table read now.
        var storedSegments = MarketSignals.Snapshot.FromJson(o.Store["snapshot"])?.Segments;
        if (!shares && o.Catalogs.Keys.Any(m => storedSegments == null || !storedSegments.TryGetValue(m, out var seg) || seg.Count == 0)) shares = true;
        if (shares) o.Shares = await CliReads.MarketShares(client, session, timeout);
        o.Player = CliReads.PlayerCompany(o.Catalogs.Values.SelectMany(r => r), own) ?? o.Store["player"]?.ToString();
        if (o.Catalogs.Count == 0 || date == null) return o;
        o.PreviousSnapshotDate = o.Store["snapshot"]?["date"]?.ToString();
        var unserved = o.Shares?.Markets.Where(m => m.Value.Unserved != null).ToDictionary(m => m.Key, m => m.Value.Unserved!.Value);
        o.Diff = MarketSignals.Observe(o.Store, MarketSignals.Snapshot.From(date, o.Catalogs, o.Shares?.SegmentPrices, unserved), own, o.Player, thresholds?.MinPercent ?? MarketSignals.DefaultMinPercent, thresholds?.MinDollars ?? MarketSignals.DefaultMinDollars, thresholds?.SameCompanyLeads == true);
        if (marketView)
        {
            var views = await CliReads.Catalogs(client, session, timeout, null, marketView: true);
            o.NewRivalCpus = MarketSignals.TrackPopularity(o.Store, date, views, own, o.Player);
        }
        o.Store["ownCpus"] = new JsonArray(own.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        MarketSignals.Save(o.Store);
        return o;
    }

    // Old response shape (priceChanges/newCpus/retiredCpus since this consumer's previous call) plus synthesized notifications.
    internal static JsonObject Report(Observation o, string consumer)
    {
        if (o.Catalogs.Count == 0) return new JsonObject { ["available"] = false, ["note"] = "Market tables could not be read." };
        var cursors = o.Store["cursorDates"] as JsonObject ?? new JsonObject();
        var since = cursors[consumer]?.ToString();
        var fresh = MarketSignals.TakeNew(o.Store, consumer);
        cursors[consumer] = o.Date;
        o.Store["cursorDates"] = cursors;
        MarketSignals.Save(o.Store);
        if (o.Diff == null && since == null) return new JsonObject { ["baseline"] = true, ["note"] = "First snapshot stored; the next call reports rival price changes, new and no-longer-listed CPUs, leader changes and recommended-price moves since this date." };
        var priceChanges = new JsonArray();
        foreach (var n in fresh.OfType<JsonObject>().Where(n => n["type"]?.ToString() == "competitor_price_changed"))
            foreach (var c in n["aggregate"] != null ? n["cpus"]!.AsArray().OfType<JsonObject>() : new[] { n })
                priceChanges.Add(new JsonObject { ["market"] = c["market"]?.DeepClone(), ["cpu"] = c["cpu"]?.DeepClone(), ["company"] = c["company"]?.DeepClone(), ["from"] = c["oldPrice"]?.DeepClone(), ["to"] = c["newPrice"]?.DeepClone(), ["changePercent"] = c["changePercent"]?.DeepClone(), ["date"] = n["gameDate"]?.DeepClone(), ["impactSummary"] = c["impactSummary"]?.DeepClone() });
        return new JsonObject
        {
            ["sinceDate"] = since ?? o.PreviousSnapshotDate, ["snapshotComparedWith"] = o.PreviousSnapshotDate, ["priceChanges"] = priceChanges,
            ["newCpus"] = new JsonArray((o.Diff?.Listed ?? new()).Select(x => (JsonNode?)x.DeepClone()).ToArray()),
            ["retiredCpus"] = new JsonArray((o.Diff?.Delisted ?? new()).Select(x => (JsonNode?)x.DeepClone()).ToArray()),
            ["notifications"] = fresh,
            ["note"] = "Rival price cuts and new CPUs can move your demand sharply overnight; the game's popups announce releases but not price changes. notifications are synthesized by the CLI from the Analysis tables (source cli_synthesized, native false). retiredCpus are no longer listed: retired or dropped out of the table's top 50. The table lists at most 50 CPUs per market, so rivals outside it are not tracked."
        };
    }

    internal sealed record Thresholds(double MinPercent, double MinDollars, bool SameCompanyLeads = false);

    // --competitor-price-min-percent / --competitor-price-min-dollars (defaults 2% / $5 net since the last reported price).
    internal static Thresholds? ReadThresholds(JsonObject? options)
    {
        var percent = CliReads.Num(options?["competitorPriceMinPercent"] ?? options?["competitorMinChangePercent"]);
        var dollars = CliReads.Num(options?["competitorPriceMinDollars"]);
        var sameCompany = options?["includeSameCompanyLeads"]?.GetValue<bool>() == true;
        return percent == null && dollars == null && !sameCompany ? null : new Thresholds(percent ?? MarketSignals.DefaultMinPercent, dollars ?? MarketSignals.DefaultMinDollars, sameCompany);
    }

    internal static async Task<JsonObject> Run(HttpClient client, int timeout, JsonObject? options = null)
    {
        var session = "market-changes-cli";
        var time = await CliReads.Data(client, "game.time-read", null, session, timeout);
        var production = await CliReads.Data(client, "game.production-read", null, session, timeout);
        var own = (production?["products"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()).Select(p => p["name"]?.ToString()).Where(n => n != null).Select(n => n!).ToHashSet(StringComparer.Ordinal);
        if (own.Count == 0 && CliReads.LoadJson(MarketSignals.StoreFile)?["ownCpus"] is JsonArray cached) foreach (var n in cached) if (n != null) own.Add(n.ToString());
        var o = await Observe(client, session, timeout, own, time?["date"]?.ToString(), shares: true, marketView: true, ReadThresholds(options));
        if (o.Catalogs.Count == 0) return new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "catalog_unavailable", ["message"] = "Market catalogs could not be read." } };
        var result = new JsonObject { ["ok"] = true, ["kind"] = "market_changes", ["date"] = o.Date, ["markets"] = new JsonArray(o.Catalogs.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()), ["changes"] = Report(o, "market-changes") };
        result["recentRivalPriceMoves"] = MarketSignals.Recent(o.Store, o.Date, 30, "competitor_price_changed");
        if (o.Shares != null) result["segmentRecommendedPrices"] = SegmentsJson(o.Shares);
        result["newRivalCpus"] = o.NewRivalCpus;
        return result;
    }

    internal static JsonObject SegmentsJson(CliReads.Shares shares)
    {
        var result = new JsonObject();
        foreach (var (market, map) in shares.SegmentPrices) { var m = new JsonObject(); foreach (var (k, v) in map) m[k] = v; result[market] = m; }
        return result;
    }
}
