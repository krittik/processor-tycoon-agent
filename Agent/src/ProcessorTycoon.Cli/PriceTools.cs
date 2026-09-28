using System.Globalization;
using System.Text.Json.Nodes;

// Explicit price/line commands with pre-commit warnings, a local price history (tools/price-history.json), revert,
// batch plans and post-change follow-ups. Every change is an explicit agent command; nothing is decided automatically.
internal static class PriceTools
{
    private const string HistoryFile = "price-history.json";

    // ---- History and follow-ups ----

    private static JsonObject LoadHistory(string? today = null)
    {
        var store = CliReads.LoadJson(HistoryFile) ?? new JsonObject();
        store["products"] ??= new JsonObject();
        store["followUps"] ??= new JsonArray();
        // Entries after "today" belong to another timeline (a loaded earlier save or another campaign).
        if (CliReads.Date(today) is DateTime now)
        {
            foreach (var (_, list) in store["products"]!.AsObject())
                if (list is JsonArray items) foreach (var stale in items.OfType<JsonObject>().Where(e => CliReads.Date(e["date"]?.ToString()) > now).ToArray()) items.Remove(stale);
            foreach (var stale in store["followUps"]!.AsArray().OfType<JsonObject>().Where(e => CliReads.Date(e["date"]?.ToString()) > now).ToArray()) store["followUps"]!.AsArray().Remove(stale);
        }
        return store;
    }

    internal static void Record(string product, double? from, double to, string? date, string source, JsonObject? followUp = null)
    {
        var store = LoadHistory(date);
        var products = store["products"]!.AsObject();
        var list = products[product] as JsonArray ?? new JsonArray();
        products[product] = list;
        list.Add(new JsonObject { ["date"] = date, ["from"] = from, ["to"] = to, ["source"] = source });
        while (list.Count > 50) list.RemoveAt(0);
        if (followUp != null) store["followUps"]!.AsArray().Add(followUp);
        CliReads.SaveJson(HistoryFile, store);
    }

    // Own price changes recorded by the CLI with fromDate <= date <= toDate.
    internal static Dictionary<string, List<JsonObject>> ChangesBetween(string? fromDate, string? toDate)
    {
        var result = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var from = CliReads.Date(fromDate); var to = CliReads.Date(toDate);
        if (from == null || to == null) return result;
        foreach (var (product, list) in (CliReads.LoadJson(HistoryFile)?["products"] as JsonObject) ?? new JsonObject())
            foreach (var e in list?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                if (CliReads.Date(e["date"]?.ToString()) is DateTime d && d >= from && d <= to) { if (!result.TryGetValue(product, out var l)) result[product] = l = new(); l.Add(e); }
        return result;
    }

    internal static bool FollowUpsDue(string? date) => CliReads.Date(date) is DateTime today && (CliReads.LoadJson(HistoryFile)?["followUps"] as JsonArray)?.OfType<JsonObject>().Any(f => f["reported"]?.GetValue<bool>() != true && CliReads.Date(f["dueDate"]?.ToString()) <= today && CliReads.Date(f["date"]?.ToString()) <= today) == true;

    // Follow-ups whose day has come: demand of the product and its siblings now vs. when the price changed, plus share.
    internal static async Task<JsonArray> DueFollowUps(HttpClient client, string session, int timeout, string? date, IEnumerable<JsonObject> rows)
    {
        var result = new JsonArray();
        var today = CliReads.Date(date);
        if (today == null) return result;
        var store = LoadHistory(date);
        var due = store["followUps"]!.AsArray().OfType<JsonObject>().Where(f => f["reported"]?.GetValue<bool>() != true && CliReads.Date(f["dueDate"]?.ToString()) <= today).ToArray();
        if (due.Length == 0) return result;
        var byName = rows.GroupBy(r => r["name"]?.ToString() ?? "").ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        CliReads.Shares? shares = null;
        if (due.Any(f => f["shareBaseline"] != null)) shares = await CliReads.MarketShares(client, session, timeout, includeMobile: false);
        foreach (var f in due)
        {
            var product = f["product"]?.ToString() ?? "";
            var entry = new JsonObject { ["product"] = product, ["priceChangedOn"] = f["date"]?.DeepClone(), ["from"] = f["from"]?.DeepClone(), ["to"] = f["to"]?.DeepClone(), ["daysAfter"] = CliReads.Date(f["date"]?.ToString()) is DateTime d ? (int)(today.Value - d).TotalDays : null };
            var demands = new JsonArray();
            foreach (var (name, before) in f["demandBaseline"]?.AsObject() ?? new JsonObject())
            {
                var now = byName.GetValueOrDefault(name);
                var was = CliReads.Num(before); var current = now == null ? null : CliReads.Num(now["demandPerMonth"]);
                demands.Add(new JsonObject { ["product"] = name, ["role"] = name == product ? "changed" : "sibling", ["demandBefore"] = was, ["demandNow"] = current, ["changePercent"] = was > 0 && current != null ? Math.Round((current.Value / was.Value - 1) * 100, 1) : null, ["listed"] = now != null });
            }
            entry["demand"] = demands;
            if (f["shareBaseline"] is JsonObject baseShare && shares != null && baseShare["player"]?.ToString() is string player)
            {
                var shareNow = new JsonObject();
                foreach (var (market, was) in baseShare["markets"]?.AsObject() ?? new JsonObject())
                    if (shares.Markets.TryGetValue(market, out var m)) shareNow[market] = new JsonObject { ["before"] = was?.DeepClone(), ["now"] = m.Companies.TryGetValue(player, out var s) ? s : null };
                entry["marketShare"] = shareNow;
            }
            entry["note"] = "Follow-up requested with the price change (--follow-up-days). Demand moves also reflect rival moves, releases and month starts; compare with notifications.";
            f["reported"] = true;
            result.Add(entry);
        }
        CliReads.SaveJson(HistoryFile, store);
        return result;
    }

    internal static JsonObject History(string target, int? limit)
    {
        var store = LoadHistory();
        var products = store["products"]!.AsObject();
        var result = new JsonObject();
        foreach (var (name, list) in products)
        {
            if (target.Length > 0 && name != target) continue;
            var items = list?.AsArray().Select(x => x?.DeepClone()).ToArray() ?? Array.Empty<JsonNode?>();
            if (limit > 0) items = items.Skip(Math.Max(0, items.Length - limit.Value)).ToArray();
            result[name] = new JsonArray(items);
        }
        return new JsonObject { ["ok"] = true, ["kind"] = "price_history", ["products"] = result, ["pendingFollowUps"] = new JsonArray(store["followUps"]!.AsArray().OfType<JsonObject>().Where(f => f["reported"]?.GetValue<bool>() != true && (target.Length == 0 || f["product"]?.ToString() == target)).Select(f => (JsonNode?)new JsonObject { ["product"] = f["product"]?.DeepClone(), ["dueDate"] = f["dueDate"]?.DeepClone(), ["to"] = f["to"]?.DeepClone() }).ToArray()), ["note"] = "Price changes made through this CLI (product-price, price-probe, price-revert, plan-apply), newest last. Changes made in the game UI by the player are not recorded." };
    }

    // ---- Context for pre-commit checks (one read of each table) ----

    internal sealed class Context
    {
        public string? Date;
        public Dictionary<string, JsonObject> Rows = new(StringComparer.Ordinal);
        public Dictionary<string, CliReads.SalesRow> Sales = new(StringComparer.Ordinal);
        public Dictionary<string, List<CliReads.CatalogRow>> Catalogs = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, double>> Segments = new(StringComparer.Ordinal);
        public JsonObject? Production;
        public List<string> Gaps = new();

        public string? MarketOf(string cpu) => Catalogs.FirstOrDefault(c => c.Value.Any(r => r.Cpu == cpu)).Key;
        public CliReads.CatalogRow? Spec(string cpu) => Catalogs.Values.SelectMany(r => r).FirstOrDefault(r => r.Cpu == cpu);
    }

    internal static async Task<Context> ReadContext(HttpClient client, string session, int timeout, IEnumerable<string> products, bool prices)
    {
        var ctx = new Context();
        ctx.Production = await CliReads.Data(client, "game.production-read", null, session, timeout);
        if (ctx.Production == null) { ctx.Gaps.Add("production"); return ctx; }
        ProductionRows.EnrichLines(ctx.Production);
        ctx.Date = ctx.Production["date"]?.ToString();
        foreach (var row in ctx.Production["products"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var compact = ProductionRows.FromRow(row);
            compact["availableRange"] = row["availableRange"]?.DeepClone();
            ctx.Rows.TryAdd(row["name"]?.ToString() ?? "", compact);
        }
        if (ctx.Date == null) ctx.Date = (await CliReads.Data(client, "game.time-read", null, session, timeout))?["date"]?.ToString();
        if (!prices) return ctx;
        var sales = await CliReads.Sales(client, session, timeout);
        if (sales == null) ctx.Gaps.Add("sales_sheet"); else ctx.Sales = sales;
        // Only the markets needed to locate the changed products (and their siblings) are read.
        var wanted = products.ToHashSet(StringComparer.Ordinal);
        foreach (var market in CliReads.CatalogMarkets)
        {
            if (wanted.All(p => ctx.MarketOf(p) != null)) break;
            var cat = await CliReads.Catalogs(client, session, timeout, new[] { market });
            foreach (var (k, v) in cat) ctx.Catalogs[k] = v;
        }
        if (wanted.Any(p => ctx.MarketOf(p) == null)) ctx.Gaps.Add("market_table_row (product not among the table's top 50 by MIPS, so dominance checks are skipped for it)");
        // Segment prices: the stored market snapshot when it is recent, otherwise one market-share read per needed filter.
        var stored = MarketSignals.Snapshot.FromJson(CliReads.LoadJson(MarketSignals.StoreFile)?["snapshot"]);
        var markets = wanted.Select(ctx.MarketOf).Where(m => m != null).Select(m => m!).Distinct().ToArray();
        foreach (var market in markets)
        {
            if (stored != null && stored.Segments.TryGetValue(market, out var seg) && CliReads.Date(stored.Date) is DateTime d && CliReads.Date(ctx.Date) is DateTime t && (t - d).TotalDays <= 7 && d <= t) { ctx.Segments[market] = seg; continue; }
            var filter = market is "Desktop" or "Mobile" ? market : "All";
            var data = await CliReads.Data(client, "game.market-share", new JsonObject { ["market"] = filter, ["category"] = "CPU Sales" }, session, timeout);
            if (data == null) { ctx.Gaps.Add("segment_prices_" + market); continue; }
            var shares = new CliReads.Shares();
            CliReads.ParseShares(shares, filter, data);
            foreach (var (k, v) in shares.SegmentPrices) ctx.Segments[k] = v;
        }
        return ctx;
    }

    // Pure pre-commit analysis of one price change.
    internal static JsonArray Warnings(Context ctx, string product, double newPrice)
    {
        var warnings = new JsonArray();
        var row = ctx.Rows.GetValueOrDefault(product);
        var sale = ctx.Sales.GetValueOrDefault(product);
        var old = sale?.Price;
        var demand = CliReads.Num(row?["demandPerMonth"]);
        var made = CliReads.Num(row?["productionPerMonth"]);
        var stock = CliReads.Num(row?["stockUnits"]);
        var units = sale?.SoldLastMonth ?? (made > 0 && demand != null ? Math.Min(demand.Value, made.Value) : demand);
        void W(string kind, string note, JsonObject? detail = null) { var w = new JsonObject { ["kind"] = kind, ["note"] = note }; if (detail != null) foreach (var (k, v) in detail) w[k] = v?.DeepClone(); warnings.Add(w); }

        if (old == newPrice) W("price_unchanged", FormattableString.Invariant($"{product} already costs ${newPrice:0}."));
        if (sale?.UnitCost is double cost && newPrice < cost)
            W("below_unit_cost", FormattableString.Invariant($"${newPrice:0} is below the current unit cost ${cost:0}: every unit sold loses money."), new JsonObject { ["unitCost"] = cost, ["lossPerUnit"] = Math.Round(cost - newPrice, 2), ["monthlyUnits"] = units, ["estimatedMonthlyLoss"] = units != null ? Math.Round((cost - newPrice) * units.Value) : null });
        var supplyLimited = row != null && (row["outOfStock"]?.GetValue<bool>() == true || stock is double s && demand is double dm && made is double mk && mk > 0 && dm > mk * 1.05 && s < dm * 0.25);
        if (old is double before && newPrice < before && supplyLimited)
            W("supply_limited", FormattableString.Invariant($"{product} sells everything it makes (demand {demand:0}/m vs production {made:0}/m, stock {stock:0}). A price cut will not add units; it lowers revenue per unit. More output needs lines."), new JsonObject { ["demandPerMonth"] = demand, ["productionPerMonth"] = made, ["stockUnits"] = stock, ["estimatedMonthlyRevenueChange"] = made != null ? Math.Round((newPrice - before) * made.Value) : null });
        var market = ctx.MarketOf(product);
        if (market != null && old is double o && ctx.Segments.ContainsKey(market))
        {
            var from = CliReads.Segment(ctx.Segments, market, o); var to = CliReads.Segment(ctx.Segments, market, newPrice);
            var crossed = ctx.Segments[market].Where(p => p.Value > Math.Min(o, newPrice) && p.Value <= Math.Max(o, newPrice)).Select(p => (JsonNode?)new JsonObject { ["segment"] = p.Key, ["recommendedPrice"] = p.Value }).ToArray();
            if (from != to || crossed.Length > 0) W("segment_threshold_crossed", $"The change moves {product} across the native recommended price of {string.Join(", ", crossed.Select(c => c!["segment"] + " $" + c["recommendedPrice"]))} ({from} -> {to}). Demand can jump or collapse at segment boundaries; the response may be non-monotonic.", new JsonObject { ["market"] = market, ["fromSegment"] = from, ["toSegment"] = to, ["thresholds"] = new JsonArray(crossed) });
        }
        if (market != null && ctx.Spec(product) is CliReads.CatalogRow me && me.Mips > 0)
        {
            foreach (var sib in ctx.Catalogs[market].Where(r => r.Cpu != product && ctx.Rows.ContainsKey(r.Cpu) && r.Mips > 0 && r.Price > 0))
            {
                var sibSale = ctx.Sales.GetValueOrDefault(sib.Cpu);
                var sibPrice = sibSale?.Price ?? sib.Price!.Value;
                var sibVolume = sibSale?.SoldLastMonth ?? CliReads.Num(ctx.Rows[sib.Cpu]["demandPerMonth"]);
                bool Dominates(double aMips, double aPrice, double bMips, double bPrice) => aMips >= bMips && aPrice <= bPrice && (aMips > bMips || aPrice < bPrice);
                var nowDom = Dominates(me.Mips!.Value, newPrice, sib.Mips!.Value, sibPrice);
                var wasDom = old is double p0 && Dominates(me.Mips!.Value, p0, sib.Mips!.Value, sibPrice);
                if (nowDom && !wasDom)
                {
                    var highVolume = sibVolume != null && units != null && sibVolume >= units;
                    W(highVolume ? "cannibalizes_high_volume_sibling" : "dominates_own_product", FormattableString.Invariant($"At ${newPrice:0}, {product} ({me.Mips:0.##} MIPS) is at least as fast as your {sib.Cpu} ({sib.Mips:0.##} MIPS, ${sibPrice:0}) for no more money: buyers can switch from {sib.Cpu}") + (highVolume ? FormattableString.Invariant($", which sells {sibVolume:0}/month (cannibalization risk).") : "."), new JsonObject { ["sibling"] = sib.Cpu, ["siblingMips"] = sib.Mips, ["siblingPrice"] = sibPrice, ["siblingMonthlyUnits"] = sibVolume });
                }
                var nowDominated = Dominates(sib.Mips!.Value, sibPrice, me.Mips!.Value, newPrice);
                var wasDominated = old is double p1 && Dominates(sib.Mips!.Value, sibPrice, me.Mips!.Value, p1);
                if (nowDominated && !wasDominated) W("dominated_by_own_product", FormattableString.Invariant($"At ${newPrice:0}, your {sib.Cpu} ({sib.Mips:0.##} MIPS, ${sibPrice:0}) is at least as fast for no more money, so {product} becomes the worse offer (launch risk: its popularity may not grow)."), new JsonObject { ["sibling"] = sib.Cpu, ["siblingMips"] = sib.Mips, ["siblingPrice"] = sibPrice });
            }
        }
        return warnings;
    }

    // ---- Commands ----

    // game product-price NAME --price P [--dry-run true] [--skip-checks true] [--follow-up-days N]
    internal static async Task<JsonObject> Price(HttpClient client, JsonObject request, int timeout, string source = "product-price")
    {
        var session = "price-cli";
        var target = request["target"]?.ToString() ?? "";
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        var unknown = options.Select(p => p.Key).Except(new[] { "price", "dryRun", "skipChecks", "followUpDays" }).ToArray();
        if (unknown.Length > 0) return Error("invalid_request", "product-price accepts --price, --dry-run, --skip-checks and --follow-up-days; unknown: " + string.Join(", ", unknown));
        if (target.Length == 0) return Error("invalid_target", "Name one exact product.");
        if (!int.TryParse(options["price"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) || price is < 1 or > 9999) return Error("invalid_value", "price must be an integer from 1 to 9999.");
        var dryRun = options["dryRun"]?.GetValue<bool>() == true;
        var skip = options["skipChecks"]?.GetValue<bool>() == true;
        if (dryRun && skip) return Error("invalid_request", "--dry-run only runs the checks; do not combine it with --skip-checks.");
        var followDays = options["followUpDays"] == null ? 0 : (int)(CliReads.Num(options["followUpDays"]) ?? 0);
        Context? ctx = null;
        JsonArray warnings = new();
        if (!skip)
        {
            ctx = await ReadContext(client, session, timeout, new[] { target }, prices: true);
            if (ctx.Production != null && !ctx.Rows.ContainsKey(target)) return Error("not_found", $"No production row is named '{target}'. Nothing was changed.");
            warnings = Warnings(ctx, target, price);
            await CloseViews(client, session, timeout, keep: "ProductionWindow");
        }
        // The previous price is always recorded (price-revert needs it): with --skip-checks it comes from one sales-sheet read.
        var oldPrice = ctx?.Sales.GetValueOrDefault(target)?.Price ?? await CurrentPrice(client, session, timeout, target);
        var check = new JsonObject { ["product"] = target, ["oldPrice"] = oldPrice, ["newPrice"] = price, ["warnings"] = warnings, ["checked"] = !skip, ["gaps"] = ctx == null ? null : new JsonArray(ctx.Gaps.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()) };
        if (dryRun) return new JsonObject { ["ok"] = true, ["kind"] = "price_check", ["dryRun"] = true, ["committed"] = false, ["check"] = check, ["next"] = warnings.Count == 0 ? "No warnings. Commit with the same command without --dry-run." : "Review the warnings. They are observations, not a decision; commit without --dry-run if the change is still intended." };

        var reply = await GameWatch.Call(client, "game.product-price", target, new JsonObject { ["price"] = price }, request["session"]?.ToString() ?? CliSession.Aux, timeout);
        if (!GameWatch.TryData(reply, out var data)) { reply["check"] = check; return reply; }
        var date = data!["date"]?.ToString() ?? ctx?.Date;
        JsonObject? followUp = null;
        if (followDays > 0 && CliReads.Date(date) is DateTime today)
        {
            var baseline = new JsonObject();
            var market = ctx?.MarketOf(target);
            foreach (var (name, row) in ctx?.Rows ?? new())
                if (name == target || market != null && ctx!.MarketOf(name) == market) baseline[name] = CliReads.Num(row["demandPerMonth"]);
            if (ctx == null) baseline[target] = null;
            followUp = new JsonObject { ["product"] = target, ["date"] = date, ["dueDate"] = CliReads.Iso(today.AddDays(followDays)), ["from"] = oldPrice, ["to"] = price, ["demandBaseline"] = baseline };
            var store = MarketSignals.Load(date);
            var player = store["player"]?.ToString();
            var shares = player == null ? null : await CliReads.MarketShares(client, session, timeout, includeMobile: false);
            if (shares != null && player != null)
            {
                var m = new JsonObject();
                foreach (var (name, entry) in shares.Markets) m[name] = entry.Companies.TryGetValue(player, out var s) ? s : null;
                followUp["shareBaseline"] = new JsonObject { ["player"] = player, ["markets"] = m };
                await CloseViews(client, session, timeout, keep: "ProductionWindow");
            }
        }
        Record(target, oldPrice, price, date, source, followUp);
        data["oldPrice"] = oldPrice;
        data["check"] = check;
        data["priceHistoryRecorded"] = true;
        if (followUp != null) data["followUp"] = new JsonObject { ["dueDate"] = followUp["dueDate"]?.DeepClone(), ["note"] = "The next watch-advance (or time-advance) result on or after this date includes priceFollowUps with demand deltas for the product and its siblings." };
        if (warnings.Count > 0) data["next"] = "The price was committed despite the warnings in check.warnings (they do not block). Revert with game price-revert \"" + target + "\" if needed.";
        return reply;
    }

    // Current price of one own product from the Analysis "Your Sales" sheet (one read; the view is closed again if the read opened it).
    internal static async Task<double?> CurrentPrice(HttpClient client, string session, int timeout, string product)
    {
        var sales = await CliReads.Sales(client, session, timeout);
        await CloseViews(client, session, timeout, keep: null);
        return sales?.GetValueOrDefault(product)?.Price;
    }

    // game price-revert NAME [--steps N]: sets the price the product had before its last N recorded CLI changes.
    internal static async Task<JsonObject> Revert(HttpClient client, JsonObject request, int timeout)
    {
        var target = request["target"]?.ToString() ?? "";
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        if (options.Select(p => p.Key).Except(new[] { "steps", "dryRun", "skipChecks" }).Any()) return Error("invalid_request", "price-revert accepts --steps N, --dry-run and --skip-checks.");
        var steps = options["steps"] == null ? 1 : (int)(CliReads.Num(options["steps"]) ?? 0);
        if (target.Length == 0 || steps < 1) return Error("invalid_request", "Use game price-revert EXACT_PRODUCT [--steps N] (N >= 1).");
        var list = LoadHistory()["products"]?[target]?.AsArray().OfType<JsonObject>().ToList() ?? new();
        if (list.Count < steps) return Error("no_history", $"Only {list.Count} recorded CLI price change(s) for '{target}'. Nothing was changed.");
        var entry = list[list.Count - steps];
        if (CliReads.Num(entry["from"]) is not double previous) return Error("no_history", $"The recorded change on {entry["date"]} has no known previous price (the sales sheet could not be read then, or it was recorded by CLI 0.4.0 with --skip-checks). Nothing was changed; set the price explicitly with product-price.");
        var parameters = new JsonObject { ["price"] = (int)previous };
        foreach (var key in new[] { "dryRun", "skipChecks" }) if (options[key] != null) parameters[key] = options[key]!.DeepClone();
        var result = await Price(client, new JsonObject { ["target"] = target, ["parameters"] = parameters }, timeout, "price-revert");
        (result["operation"]?["result"]?["data"] as JsonObject ?? result)["revertedTo"] = new JsonObject { ["price"] = previous, ["changeUndone"] = entry.DeepClone(), ["steps"] = steps };
        return result;
    }

    // game product-production NAME with --add N, --lines max or --clamp true: resolves an absolute line count from the
    // row's native slider range (0..manual + unassigned) and sends it; the native command verifies the slider readback.
    internal static async Task<JsonObject?> ResolveLines(HttpClient client, JsonObject request, int timeout)
    {
        var options = request["parameters"] as JsonObject;
        if (options == null || options["add"] == null && options["lines"]?.ToString() != "max" && options["clamp"] == null) return null;
        var target = request["target"]?.ToString() ?? "";
        var ctx = await ReadContext(client, CliSession.Aux, timeout, new[] { target }, prices: false);
        if (!ctx.Rows.TryGetValue(target, out var row)) return Error("not_found", $"No production row is named '{target}'. Nothing was changed.");
        var (lines, detail, problem) = TargetLines(row, options["lines"]?.ToString(), CliReads.Num(options["add"]), options["clamp"]?.GetValue<bool>() == true);
        if (problem != null) return Error("invalid_value", problem);
        options.Remove("add"); options.Remove("clamp");
        options["lines"] = lines;
        return detail;
    }

    // Pure: absolute manual lines from a request ("max", a number, or current + add), clamped when asked.
    internal static (int Lines, JsonObject Detail, string? Problem) TargetLines(JsonObject row, string? lines, double? add, bool clamp)
    {
        var manual = (int?)CliReads.Num(row["manualLines"]);
        var max = (int?)CliReads.Num(row["availableRange"]?["maxLines"]);
        var min = (int?)CliReads.Num(row["availableRange"]?["minLines"]) ?? 0;
        if (manual == null || max == null) return (0, new JsonObject(), "The native slider range is unavailable (automation on or allocation disabled); nothing was changed.");
        int requested;
        if (lines == "max") requested = max.Value;
        else if (add != null) requested = manual.Value + (int)add.Value;
        else if (int.TryParse(lines, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) requested = n;
        else return (0, new JsonObject(), "Give --lines N, --lines max or --add N.");
        var target = requested;
        if (requested > max || requested < min)
        {
            if (!clamp && lines != "max") return (0, new JsonObject(), $"{requested} lines is outside the native range {min}-{max} (current manual {manual}, unassigned {max - manual}). Add --clamp true to use the nearest allowed value; nothing was changed.");
            target = Math.Clamp(requested, min, max.Value);
        }
        return (target, new JsonObject { ["currentManualLines"] = manual, ["requested"] = requested, ["applied"] = target, ["range"] = new JsonObject { ["min"] = min, ["max"] = max }, ["clamped"] = target != requested }, null);
    }

    // game plan-apply --file plan.json [--dry-run true] [--rollback-on-failure true]
    // Plan: {"items":[{"product":"C5","price":199},{"product":"C6","lines":12},{"product":"C7","add":-3},{"product":"C8","lines":"max"}],"clamp":true}
    internal static async Task<JsonObject> Plan(HttpClient client, JsonObject request, int timeout)
    {
        var session = "plan-cli";
        var p = request["parameters"] as JsonObject ?? new JsonObject();
        var dryRun = p["dryRun"]?.GetValue<bool>() == true;
        var rollback = p["rollbackOnFailure"]?.GetValue<bool>() == true;
        var clamp = p["clamp"]?.GetValue<bool>() == true;
        var items = (p["items"] as JsonArray)?.OfType<JsonObject>().ToList();
        if (items == null || items.Count == 0) return Error("invalid_request", "plan-apply needs --file PLAN.json (or --json) with {\"items\":[{\"product\":\"NAME\",\"price\":P},{\"product\":\"NAME\",\"lines\":N|\"max\"},{\"product\":\"NAME\",\"add\":N}],\"clamp\":true}.");
        foreach (var item in items)
        {
            var extra = item.Select(k => k.Key).Except(new[] { "product", "price", "lines", "add" }).ToArray();
            if (item["product"] == null || extra.Length > 0 || item["price"] == null && item["lines"] == null && item["add"] == null || item["lines"] != null && item["add"] != null) return Error("invalid_request", "Each plan item needs product and price and/or one of lines/add; unknown keys: " + string.Join(", ", extra));
            if (item["price"] != null && (!int.TryParse(item["price"]!.ToString(), out var pr) || pr is < 1 or > 9999)) return Error("invalid_value", $"{item["product"]}: price must be an integer from 1 to 9999.");
        }
        var names = items.Select(i => i["product"]!.ToString()).ToList();
        var ctx = await ReadContext(client, session, timeout, items.Where(i => i["price"] != null).Select(i => i["product"]!.ToString()), prices: items.Any(i => i["price"] != null));
        if (ctx.Production == null) return Error("production_unavailable", "The Production table could not be read; nothing was changed.");
        var missing = names.Where(n => !ctx.Rows.ContainsKey(n)).Distinct().ToArray();
        if (missing.Length > 0) return Error("not_found", "Unknown products: " + string.Join(", ", missing) + ". Nothing was changed.");
        var lines = ProductionRows.Lines(ctx.Production);
        var free = (int?)CliReads.Num(lines["unassigned"]);
        if (free == null && items.Any(i => i["lines"] != null || i["add"] != null)) return Error("lines_unavailable", "Unassigned lines could not be determined exactly; nothing was changed.");

        // Line steps: decreases first (they free lines), then increases in plan order against the remaining pool.
        var steps = new List<JsonObject>();
        var lineItems = items.Where(i => i["lines"] != null || i["add"] != null).Select(i =>
        {
            var row = ctx.Rows[i["product"]!.ToString()];
            var manual = (int?)CliReads.Num(row["manualLines"]) ?? 0;
            var raw = i["lines"]?.ToString();
            int? wanted = raw == "max" ? null : raw != null ? int.Parse(raw, CultureInfo.InvariantCulture) : manual + (int)(CliReads.Num(i["add"]) ?? 0);
            return (Item: i, Name: i["product"]!.ToString(), Manual: manual, Wanted: wanted);
        }).ToList();
        if (lineItems.Any(l => ctx.Rows[l.Name]["availableRange"] == null)) return Error("allocation_unavailable", "Manual allocation is unavailable for some products (automation on?); nothing was changed.");
        var pool = free ?? 0;
        foreach (var l in lineItems.Where(l => l.Wanted != null && l.Wanted < l.Manual))
        {
            var target = Math.Max(0, l.Wanted!.Value);
            if (target != l.Wanted && !clamp) return Error("invalid_value", $"{l.Name}: {l.Wanted} lines is below 0; nothing was changed.");
            pool += l.Manual - target;
            steps.Add(new JsonObject { ["product"] = l.Name, ["field"] = "lines", ["old"] = l.Manual, ["new"] = target, ["requested"] = l.Item["lines"]?.DeepClone() ?? l.Item["add"]?.DeepClone(), ["clamped"] = target != l.Wanted });
        }
        foreach (var l in lineItems.Where(l => l.Wanted == null || l.Wanted >= l.Manual))
        {
            var allowed = l.Manual + pool;
            var target = l.Wanted ?? allowed;
            if (target > allowed)
            {
                if (!clamp) return Error("insufficient_lines", $"{l.Name}: {target} lines needs {target - l.Manual} more but only {pool} are free after the plan's decreases. Add \"clamp\": true (or --clamp true) to assign what is available; nothing was changed.");
                target = allowed;
            }
            pool -= target - l.Manual;
            if (target != l.Manual || l.Wanted == null || target != l.Wanted) steps.Add(new JsonObject { ["product"] = l.Name, ["field"] = "lines", ["old"] = l.Manual, ["new"] = target, ["requested"] = l.Item["lines"]?.DeepClone() ?? l.Item["add"]?.DeepClone(), ["clamped"] = l.Wanted != null && target != l.Wanted });
        }
        foreach (var i in items.Where(i => i["price"] != null))
        {
            var name = i["product"]!.ToString();
            var price = int.Parse(i["price"]!.ToString(), CultureInfo.InvariantCulture);
            steps.Add(new JsonObject { ["product"] = name, ["field"] = "price", ["old"] = ctx.Sales.GetValueOrDefault(name)?.Price, ["new"] = price, ["warnings"] = Warnings(ctx, name, price) });
        }
        await CloseViews(client, session, timeout, keep: "ProductionWindow");
        var result = new JsonObject { ["ok"] = true, ["kind"] = "plan", ["dryRun"] = dryRun, ["date"] = ctx.Date, ["unassignedBefore"] = free, ["unassignedAfterPlan"] = pool, ["steps"] = new JsonArray(steps.Select(s => (JsonNode?)s).ToArray()), ["gaps"] = new JsonArray(ctx.Gaps.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()) };
        if (dryRun) { result["next"] = "Nothing was changed. Apply with the same command without --dry-run."; return result; }

        var applied = new List<JsonObject>();
        foreach (var step in steps)
        {
            var name = step["product"]!.ToString();
            var ok = false;
            if (step["field"]!.ToString() == "lines")
            {
                if ((int)CliReads.Num(step["new"])! == (int)CliReads.Num(step["old"])!) { step["applied"] = true; step["verified"] = true; continue; }
                var reply = await GameWatch.Call(client, "game.product-production", name, new JsonObject { ["lines"] = (int)CliReads.Num(step["new"])! }, session, timeout);
                var row = GameWatch.TryData(reply, out var data) ? data!["products"]?.AsArray().OfType<JsonObject>().FirstOrDefault(r => r["name"]?.ToString() == name) : null;
                ok = row != null && (int?)CliReads.Num(row["manualLines"]) == (int)CliReads.Num(step["new"])!;
                step["readback"] = row == null ? null : new JsonObject { ["manualLines"] = row["manualLines"]?.DeepClone(), ["productionLines"] = row["productionLines"]?.DeepClone() };
                if (!ok) step["error"] = reply["operation"]?["error"]?.DeepClone() ?? reply["error"]?.DeepClone();
            }
            else
            {
                var reply = await GameWatch.Call(client, "game.product-price", name, new JsonObject { ["price"] = (int)CliReads.Num(step["new"])! }, session, timeout);
                ok = GameWatch.TryData(reply, out var data) && data!["outcome"]?.ToString() == "price_confirmed" && (int?)CliReads.Num(data["appliedValue"]) == (int)CliReads.Num(step["new"])!;
                step["readback"] = ok ? new JsonObject { ["price"] = data!["appliedValue"]?.DeepClone() } : null;
                if (ok) Record(name, CliReads.Num(step["old"]), CliReads.Num(step["new"])!.Value, data!["date"]?.ToString() ?? ctx.Date, "plan-apply");
                else step["error"] = reply["operation"]?["error"]?.DeepClone() ?? reply["error"]?.DeepClone();
            }
            step["applied"] = ok; step["verified"] = ok;
            if (!ok) { result["ok"] = false; result["failedAt"] = name; break; }
            applied.Add(step);
        }
        if (result["ok"]?.GetValue<bool>() == false && rollback && applied.Count > 0)
        {
            var undone = new JsonArray();
            foreach (var step in Enumerable.Reverse(applied))
            {
                var name = step["product"]!.ToString();
                if (step["field"]!.ToString() == "lines") { var r = await GameWatch.Call(client, "game.product-production", name, new JsonObject { ["lines"] = (int)CliReads.Num(step["old"])! }, session, timeout); undone.Add(new JsonObject { ["product"] = name, ["field"] = "lines", ["restoredTo"] = step["old"]?.DeepClone(), ["ok"] = GameWatch.TryData(r, out _) }); }
                else if (CliReads.Num(step["old"]) is double oldPrice) { var r = await GameWatch.Call(client, "game.product-price", name, new JsonObject { ["price"] = (int)oldPrice }, session, timeout); var ok2 = GameWatch.TryData(r, out var d2); if (ok2) Record(name, CliReads.Num(step["new"]), oldPrice, d2!["date"]?.ToString() ?? ctx.Date, "plan-rollback"); undone.Add(new JsonObject { ["product"] = name, ["field"] = "price", ["restoredTo"] = oldPrice, ["ok"] = ok2 }); }
            }
            result["rolledBack"] = undone;
        }
        result["next"] = result["ok"]!.GetValue<bool>() ? "All steps were applied and read back. Observe demand over the next days (watch-advance)." : "The plan stopped at failedAt; later steps were not applied" + (rollback ? " and applied steps were rolled back (see rolledBack)." : ". Earlier steps remain applied (use --rollback-on-failure true to undo them automatically).");
        return result;
    }

    // game cleanup-preview [--demand-below N]: products with little demand, their stock and estimated liquidation value.
    internal static async Task<JsonObject> Cleanup(HttpClient client, JsonObject request, int timeout)
    {
        var session = "cleanup-cli";
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        if (options.Select(p => p.Key).Except(new[] { "demandBelow" }).Any()) return Error("invalid_request", "cleanup-preview accepts only --demand-below N.");
        var ctx = await ReadContext(client, session, timeout, Array.Empty<string>(), prices: false);
        if (ctx.Production == null) return Error("production_unavailable", "The Production table could not be read.");
        var sales = await CliReads.Sales(client, session, timeout);
        await CloseViews(client, session, timeout, keep: null);
        var maxDemand = ctx.Rows.Values.Select(r => CliReads.Num(r["demandPerMonth"]) ?? 0).DefaultIfEmpty(0).Max();
        var threshold = CliReads.Num(options["demandBelow"]) ?? Math.Max(50, Math.Round(maxDemand * 0.01));
        var candidates = new JsonArray();
        foreach (var (name, row) in ctx.Rows)
        {
            var demand = CliReads.Num(row["demandPerMonth"]) ?? 0;
            if (demand >= threshold) continue;
            var sale = sales?.GetValueOrDefault(name);
            var stock = CliReads.Num(row["stockUnits"]) ?? sale?.Stock ?? 0;
            candidates.Add(new JsonObject
            {
                ["product"] = name, ["demandPerMonth"] = demand, ["lines"] = row["lines"]?.DeepClone(), ["manualLines"] = row["manualLines"]?.DeepClone(), ["contractLines"] = row["contractLines"]?.DeepClone(),
                ["stockUnits"] = stock, ["unitCost"] = sale?.UnitCost, ["price"] = sale?.Price, ["soldLastMonth"] = sale?.SoldLastMonth, ["popularity"] = sale?.Popularity,
                ["estimatedLiquidationValue"] = sale?.UnitCost != null ? Math.Round(stock * sale.UnitCost.Value) : null,
                ["exactQuote"] = $"game product-preview \"{name}\" --retire true (native Storage sale and contract fines; cancelled, nothing retired)"
            });
        }
        return new JsonObject
        {
            ["ok"] = true, ["kind"] = "cleanup_preview", ["date"] = ctx.Date, ["demandBelow"] = threshold, ["candidates"] = candidates,
            ["totalEstimatedLiquidationValue"] = Math.Round(candidates.OfType<JsonObject>().Sum(c => CliReads.Num(c["estimatedLiquidationValue"]) ?? 0)),
            ["note"] = "Reads only. Retirement sells remaining stock at roughly unit cost (estimate = stock x current unit cost) and frees the product's lines; contract lines and fines are shown only by the native retire preview (exactQuote). Whether to retire is your decision."
        };
    }

    internal static async Task CloseViews(HttpClient client, string session, int timeout, string? keep)
    {
        var visible = await Workspace.Visible(client, session, timeout);
        var views = (visible ?? new JsonArray()).OfType<JsonObject>().Where(w => Workspace.AgentOpened(w) && w["scope"]?.ToString() is string s && s is "AnalysisWindow" && s != keep).Select(w => w["scope"]!.ToString()).ToArray();
        if (views.Length > 0) await Workspace.Close(client, session, timeout, views);
    }

    private static JsonObject Error(string code, string message) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
