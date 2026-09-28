using System.Globalization;
using System.Text.Json.Nodes;

// Typed views of existing native reads used by several compositions. Reads only; each call is one native command.
internal static class CliReads
{
    // Market Dropdown labels of the Analysis CPU table. The table lists at most 50 CPUs per market (highest MIPS in Specs).
    internal static readonly string[] CatalogMarkets = { "Desktop", "Mobile", "Industries" };

    internal sealed record CatalogRow(string Market, string Company, string Cpu, double? Mips, double? Price, int? Year, double? Popularity);

    internal sealed record SalesRow(string Cpu, double? AllTime, double? SoldLastMonth, double? MissedLastMonth, double? Stock, double? IncomeLastMonth, double? ProfitLastMonth, double? UnitCost, double? Price, double? Popularity);

    internal sealed class Shares
    {
        // market display name -> size (units/month), unserved percent, company -> share percent
        public Dictionary<string, (double? Size, double? Unserved, Dictionary<string, double> Companies)> Markets = new(StringComparer.Ordinal);
        // "Desktop" -> {"High End": 234, "Mid Range": 134, "Low End": 0}; "Industries" -> {"Industries": 70}
        public Dictionary<string, Dictionary<string, double>> SegmentPrices = new(StringComparer.Ordinal);
        // Old flat shape (segment name -> native "Recommended Price" display), kept for compatibility.
        public JsonObject Flat = new();
        public List<string> Problems = new();
    }

    internal static async Task<JsonObject?> Data(HttpClient client, string command, JsonObject? parameters, string session, int timeout, string target = "")
    {
        var reply = await GameWatch.Call(client, command, target, parameters, session, timeout);
        return GameWatch.TryData(reply, out var data) ? data : null;
    }

    internal static double? Num(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => Num(o["value"]),
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        _ => DisplayNumber.First(node.ToString())
    };

    internal static List<CatalogRow> ParseCatalog(string market, JsonObject catalog, bool marketView = false)
    {
        var rows = new List<CatalogRow>();
        foreach (var row in catalog["rows"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (row["values"] is not JsonObject v || v["cpu"] == null) continue;
            var year = Num(v["year"]);
            rows.Add(new CatalogRow(market, v["company"]?.ToString() ?? "", v["cpu"]!.ToString(), marketView ? null : Num(v["mips"]), Num(v["price"]), year == null ? null : (int)year, marketView ? Num(v["popularity"]) : null));
        }
        return rows;
    }

    internal static async Task<Dictionary<string, List<CatalogRow>>> Catalogs(HttpClient client, string session, int timeout, IEnumerable<string>? markets = null, bool marketView = false)
    {
        var result = new Dictionary<string, List<CatalogRow>>(StringComparer.Ordinal);
        foreach (var market in markets ?? CatalogMarkets)
        {
            var data = await Data(client, "game.market-catalog", new JsonObject { ["view"] = marketView ? "market" : "specs", ["market"] = market, ["showRetired"] = false }, session, timeout);
            if (data != null) result[market] = ParseCatalog(market, data, marketView);
        }
        return result;
    }

    // "Your Sales" sheet (variant 1, all markets): every own CPU with price, unit cost, popularity, last month's sold/missed units.
    internal static async Task<Dictionary<string, SalesRow>?> Sales(HttpClient client, string session, int timeout)
    {
        var data = await Data(client, "game.sales-read", new JsonObject { ["variant"] = 1, ["market"] = "All Markets", ["showRetired"] = false }, session, timeout);
        return data == null ? null : ParseSales(data);
    }

    internal static Dictionary<string, SalesRow> ParseSales(JsonObject data)
    {
        var result = new Dictionary<string, SalesRow>(StringComparer.Ordinal);
        foreach (var r in data["rows"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var cpu = (r["cPU"] ?? r["cpu"])?.ToString();
            if (string.IsNullOrEmpty(cpu) || r["allTime"] == null) continue;
            double? N(string key) => DisplayNumber.First(r[key]?.ToString());
            result[cpu] = new SalesRow(cpu, N("allTime"), N("sold"), N("missedSales"), N("stock"), N("income"), N("profit"), N("unitCost"), N("price"), N("popularity"));
        }
        return result;
    }

    internal static async Task<Shares> MarketShares(HttpClient client, string session, int timeout, bool includeMobile = true)
    {
        var shares = new Shares();
        foreach (var filter in includeMobile ? new[] { "All", "Desktop", "Mobile" } : new[] { "All", "Desktop" })
        {
            var data = await Data(client, "game.market-share", new JsonObject { ["market"] = filter, ["category"] = "CPU Sales" }, session, timeout);
            if (data == null) { shares.Problems.Add($"market-share {filter} unavailable"); continue; }
            ParseShares(shares, filter, data);
        }
        return shares;
    }

    internal static void ParseShares(Shares shares, string filter, JsonObject data)
    {
        foreach (var guide in data["guidance"]?["items"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var segment = guide["market"]?.ToString();
            if (segment == null) continue;
            var display = guide["recommendations"]?["Recommended Price"];
            if (filter != "Mobile") shares.Flat[segment] = display?.DeepClone();
            var price = DisplayNumber.Money(display?.ToString());
            // The All filter's Desktop/Mobile tooltips are whole-market summaries; their segments come from their own filters.
            if (price == null || filter == "All" && segment is "Desktop" or "Mobile") continue;
            // The All filter's Industries tooltip is the whole Industries market (one segment); its title may vary ("Industrial").
            var market = filter != "All" ? filter : segment.StartsWith("Industr", StringComparison.OrdinalIgnoreCase) ? "Industries" : segment;
            if (!shares.SegmentPrices.TryGetValue(market, out var map)) shares.SegmentPrices[market] = map = new Dictionary<string, double>(StringComparer.Ordinal);
            map[segment] = price.Value;
        }
        if (filter != "All") return;
        foreach (var pair in data["markets"]?.AsObject() ?? new JsonObject())
        {
            var name = pair.Value?["displayName"]?.ToString() ?? pair.Key;
            var companies = new Dictionary<string, double>(StringComparer.Ordinal);
            double? unserved = null;
            foreach (var s in pair.Value?["shares"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var company = s["company"]?.ToString();
                var pct = Num(s["share"]);
                if (company == null || pct == null) continue;
                if (company == "Potential Sales") unserved = pct; else companies[company] = pct.Value;
            }
            shares.Markets[name] = (Num(pair.Value?["size"]), unserved, companies);
        }
    }

    // Price segment by the native recommended prices: the highest segment whose recommended price is at or below the price.
    internal static string? Segment(Dictionary<string, Dictionary<string, double>> segmentPrices, string market, double? price)
    {
        if (price == null || !segmentPrices.TryGetValue(market, out var map) || map.Count == 0) return null;
        if (map.Count == 1) return map.Keys.First();
        return map.Where(p => p.Value <= price.Value).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() ?? map.OrderBy(p => p.Value).First().Key;
    }

    internal static string? PlayerCompany(IEnumerable<CatalogRow> rows, ISet<string> ownNames) => rows.Where(r => ownNames.Contains(r.Cpu)).GroupBy(r => r.Company).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    internal static DateTime? Date(string? text) => DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    internal static string Iso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    // State files live in the game's tools/ directory; PT_AGENT_STATE_DIR overrides it (used by the offline tests).
    internal static string? ToolsPath(string file)
    {
        var overrideDir = Environment.GetEnvironmentVariable("PT_AGENT_STATE_DIR");
        if (!string.IsNullOrEmpty(overrideDir)) return Path.Combine(overrideDir, file);
        var root = GameHost.FindRoot();
        return root == null ? null : Path.Combine(root, "tools", file);
    }

    internal static JsonObject? LoadJson(string file)
    {
        var path = ToolsPath(file);
        try { return path != null && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; } catch { return null; }
    }

    internal static void SaveJson(string file, JsonObject value)
    {
        var path = ToolsPath(file);
        try { if (path != null) File.WriteAllText(path, value.ToJsonString()); } catch { }
    }
}
