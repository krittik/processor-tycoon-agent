using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

// A CLI composition of existing native Game API actions. It never changes prices or chooses a business action.
internal static class GameWatch
{
    // Advisories seen in any reply of a composed command; Program attaches them to the final output.
    internal static readonly JsonArray Advisories = new();
    internal static void CollectAdvisories(JsonObject reply) { foreach (var a in reply["advisories"]?.AsArray() ?? new JsonArray()) if (a != null && !Advisories.Any(x => x?["kind"]?.ToString() == a["kind"]?.ToString())) Advisories.Add(a.DeepClone()); }
    private static readonly string[] portfolioGuards = { "stockGrowthDays", "idleDemandBelow", "idleDemandRatioBelow", "stockMonthsAbove", "anyDemandChangePercent" };
    private static readonly string[] guards = new[] { "demandUpPercent", "demandDownPercent", "minDemandToProduction", "maxDemandToProduction", "stockAbove", "creditBelow", "balanceBelow", "stopOnCompetitorRelease", "stopOnDefault", "stopOnCompetitorPriceCutPercent", "stopOnExpansionComplete", "portfolioGuards" }.Concat(portfolioGuards).ToArray();
    private static readonly string[] allowed = guards.Concat(new[] { "days", "speed", "products", "allProducts", "keepWindows", "trackCompetitorPrices", "competitorPriceScope", "competitorMinChangePercent", "competitorPriceMinPercent", "competitorPriceMinDollars", "includeSameCompanyLeads", "monthlyDigest" }).ToArray();

    // One observation: date, company finance and compact products keyed by name (single) or productRef (multi).
    private sealed class Snap
    {
        public string? Date;
        public JsonObject Finance = new();
        public readonly Dictionary<string, JsonObject> Products = new(StringComparer.Ordinal);
        // Every Production row (portfolio guards, expansion alerts), keyed by productRef; empty when only a pulse was read.
        public readonly Dictionary<string, JsonObject> All = new(StringComparer.Ordinal);
        public JsonObject? Lines;
    }

    private sealed class Watch
    {
        public HttpClient Client = null!;
        public string Session = "";
        public int Timeout;
        public bool Single;
        public List<string> Keys = new();
        public List<string> Requested = new();
        public JsonObject Options = new();
        // Plugin >= 0.3.9 with Production pinned: each daily time-advance returns the Production rows itself (one call per day).
        public bool IncludeProduction;
        // Production as it was before the watch (null when closed), whether the watch pinned it and must close it at the end.
        public JsonObject? PriorProduction;
        public bool Pinned;
        public bool CloseAtEnd;
        public JsonArray Notifications = new();
        // Every popup message seen during the watch (compact), reported at the stop; Notifications holds only the current day's.
        public JsonArray Seen = new();
        // Mod-synthesized notifications (competitor price changes, expansion completed, ...) raised during the watch.
        public JsonArray Synth = new();
        public List<MarketSignals.PriceChange> PriceChanges = new();
        public List<MarketSignals.PriceChange> TodayPriceChanges = new();
        // Rival prices when the watch started (company\u001fcpu -> row); the price-cut guard compares with these, so slow
        // step-by-step cuts add up. CutsSinceStart holds today's rivals that are below their start price.
        public Dictionary<string, CliReads.CatalogRow> StartPrices = new(StringComparer.Ordinal);
        public List<MarketSignals.PriceChange> CutsSinceStart = new();
        public HashSet<string> CutsReported = new(StringComparer.Ordinal);
        public string Tracking = "end";
        public HashSet<string> ScopesBefore = new(StringComparer.Ordinal);
        public Dictionary<string, List<CliReads.CatalogRow>> LastCatalogs = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, double>> LastSegments = new(StringComparer.Ordinal);
        public Dictionary<string, int> StockStreaks = new(StringComparer.Ordinal);
        public JsonArray Digests = new();
        public JsonArray PortfolioFlags = new();
        public bool NeedsAll;
        public string? StartDate;
    }

    private static void MoveNotifications(Watch w)
    {
        foreach (var item in w.Notifications.OfType<JsonObject>())
            if (!w.Seen.Any(seen => seen?["id"]?.ToString() == item["id"]?.ToString())) w.Seen.Add(new JsonObject { ["id"] = item["id"]?.DeepClone(), ["gameDate"] = item["gameDate"]?.DeepClone(), ["type"] = item["type"]?.DeepClone(), ["text"] = item["text"]?.DeepClone(), ["detail"] = item["detail"]?.DeepClone() });
        w.Notifications.Clear();
    }

    internal static async Task<JsonObject> Run(HttpClient client, JsonObject request, int timeout)
    {
        var target = request["target"]?.GetValue<string>() ?? "";
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        var unknown = options.Select(pair => pair.Key).Except(allowed, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) return Error("invalid_request", "Unknown watch parameter: " + string.Join(", ", unknown));
        var names = new List<string>();
        if (target.Length > 0) names.Add(target);
        if (options["products"] != null) names.AddRange(options["products"]!.ToString().Split(',').Select(name => name.Trim()).Where(name => name.Length > 0));
        names = names.Distinct(StringComparer.Ordinal).ToList();
        if (names.Any(name => name.StartsWith("product-ui:", StringComparison.Ordinal))) return Error("invalid_target", "Use exact unique product names, not transient productRefs.");
        if (!TryBool(options["allProducts"], out var all) || !TryBool(options["keepWindows"], out var keepWindows)) return Error("invalid_value", "allProducts and keepWindows must be true or false.");
        if (all && names.Count > 0) return Error("invalid_target", "Use --all-products alone, without a product target or --products.");
        if (!all && names.Count == 0) return Error("invalid_target", "Name one exact product (target), --products A,B,C or --all-products.");
        if (!TryNumber(options["days"], out var daysNumber) || daysNumber < 1 || daysNumber > 3650 || daysNumber != Math.Truncate(daysNumber)) return Error("invalid_value", "days must be an integer from 1 to 3650.");
        var days = (int)daysNumber;
        var speed = options["speed"] == null ? 3 : TryNumber(options["speed"], out var speedNumber) && speedNumber is >= 1 and <= 3 && speedNumber == Math.Truncate(speedNumber) ? (int)speedNumber : 0;
        if (speed == 0) return Error("invalid_value", "speed must be 1, 2 or 3.");
        foreach (var key in new[] { "demandUpPercent", "demandDownPercent", "minDemandToProduction", "maxDemandToProduction", "stockAbove", "creditBelow", "balanceBelow", "stopOnCompetitorPriceCutPercent", "competitorMinChangePercent", "competitorPriceMinPercent", "competitorPriceMinDollars", "stockGrowthDays", "idleDemandBelow", "idleDemandRatioBelow", "stockMonthsAbove", "anyDemandChangePercent" })
            if (options[key] != null && (!TryNumber(options[key], out var number) || !double.IsFinite(number) || key != "balanceBelow" && key != "creditBelow" && number < 0 || key is "demandUpPercent" or "demandDownPercent" or "stopOnCompetitorPriceCutPercent" or "anyDemandChangePercent" or "stockGrowthDays" or "stockMonthsAbove" && number == 0)) return Error("invalid_value", key + " must be a finite number (positive for a percentage, day count or month count).");
        foreach (var key in new[] { "stopOnCompetitorRelease", "stopOnDefault", "stopOnExpansionComplete", "monthlyDigest", "portfolioGuards", "includeSameCompanyLeads" })
            if (!TryBool(options[key], out _)) return Error("invalid_value", key + " must be true or false.");
        if (options["competitorPriceScope"] != null && options["competitorPriceScope"]!.ToString() is not ("near" or "all")) return Error("invalid_value", "competitorPriceScope must be near or all.");
        if (options["trackCompetitorPrices"] != null && options["trackCompetitorPrices"]!.ToString() is not ("daily" or "end" or "off")) return Error("invalid_value", "trackCompetitorPrices must be daily, end or off.");
        if (!guards.Any(key => options[key] != null)) return Error("invalid_request", "Supply at least one watch threshold or stopOnCompetitorRelease.");
        // Preset: portfolio-wide guards with moderate defaults; explicit values win.
        if (options["portfolioGuards"]?.GetValue<bool>() == true)
            foreach (var (key, value) in new[] { ("stockGrowthDays", 7.0), ("idleDemandRatioBelow", 0.05), ("stockMonthsAbove", 6.0), ("anyDemandChangePercent", 30.0) })
                if (options[key] == null) options[key] = value;
        if (options["stopOnCompetitorPriceCutPercent"] != null && options["trackCompetitorPrices"]?.ToString() is "end" or "off") return Error("invalid_value", "stopOnCompetitorPriceCutPercent needs daily competitor tracking (omit --track-competitor-prices or use daily).");

        var w = new Watch { Client = client, Session = "watch-cli", Timeout = timeout, Single = !all && names.Count == 1, Options = options, Requested = all ? new List<string> { "(all products)" } : names };
        w.Tracking = options["trackCompetitorPrices"]?.ToString() ?? (options["stopOnCompetitorPriceCutPercent"] != null ? "daily" : "end");
        w.NeedsAll = portfolioGuards.Any(key => options[key] != null) || options["stopOnExpansionComplete"]?.GetValue<bool>() == true;
        if (w.Single) w.Keys.Add(names[0]);
        var clockReply = await Call(client, "game.time-read", "", null, w.Session, timeout);
        if (!TryData(clockReply, out var clock)) return CommandFailure("clock_unavailable", clockReply);
        if (clock!["paused"]?.GetValue<bool>() != true)
        {
            var pauseReply = await Call(client, "game.time-advance", "", new JsonObject { ["days"] = 0, ["speed"] = speed }, w.Session, timeout);
            if (!TryData(pauseReply, out var pause) || pause!["paused"]?.GetValue<bool>() != true) return CommandFailure("pause_unverified", pauseReply);
        }

        // Stale read-only views are closed first so none covers the rows. The baseline read (a native Production command)
        // then opens Production; its domain cleanup also closes agent-owned leftovers such as a Settings window with its
        // Pause Menu. Production is pinned right after the baseline (see Pin).
        var workspace = new JsonObject();
        var visible = await Workspace.Visible(client, w.Session, timeout);
        w.PriorProduction = Workspace.Window(visible, "ProductionWindow");
        w.ScopesBefore = Workspace.Scopes(visible);
        if (!keepWindows)
        {
            var tidy = await Workspace.Tidy(client, w.Session, timeout, visible, "ProductionWindow");
            if (tidy["closed"]?.AsArray().Count > 0) workspace["closedStaleWindows"] = tidy["closed"]!.DeepClone();
            if (tidy["failed"]?.AsObject().Count > 0) workspace["staleWindowsNotClosed"] = tidy["failed"]!.DeepClone();
            foreach (var closed in tidy["closed"]?.AsArray() ?? new JsonArray()) w.ScopesBefore.Remove(closed!.ToString());
        }

        var result = await Observe(w, names, all, days, speed, clock["date"]?.ToString());
        workspace["productionPinned"] = w.Pinned;
        workspace["rowsReadWithAdvance"] = w.IncludeProduction;
        // Also close a Production window that a failed/early baseline opened before it could be pinned (best effort).
        var openedUnpinned = !w.Pinned && w.PriorProduction == null && Workspace.Window(await Workspace.Visible(client, w.Session, timeout), "ProductionWindow") != null;
        if (w.CloseAtEnd || openedUnpinned)
        {
            if (result["dialogs"] is JsonArray { Count: > 0 }) workspace["productionLeftOpen"] = "A native dialog or form is open; Production stays open behind it. Close it later with game window-close Production (or game window-tidy).";
            else
            {
                var close = await Call(client, "game.window-close", "ProductionWindow", null, w.Session, timeout);
                if (TryData(close, out var closed) && closed!["closed"]?.GetValue<bool>() == true) workspace["productionClosedAtEnd"] = true;
                else workspace["productionLeftOpen"] = close.At("operation", "error", "message")?.DeepClone() ?? "close not verified";
            }
        }
        result["workspace"] = workspace;
        return result;
    }

    private static async Task<JsonObject> Observe(Watch w, List<string> names, bool all, int days, int speed, string? clockDate)
    {
        var options = w.Options;
        var (baseReply, baseline, problem) = w.Single ? await ReadPulse(w, names[0]) : await ReadBaseline(w, names, all, clockDate);
        if (problem != null) return problem;
        if (baseline == null)
        {
            var blocking = await Dialogs.Blocking(w.Client, w.Session, w.Timeout, checkPauseMenu: true, failedReply: baseReply);
            if (blocking != null) return await Stop(w, "game_event", days, 0, clockDate, null, null, null, new JsonArray(), null, blocking);
            var failure = CommandFailure("baseline_unavailable", baseReply);
            failure["visibleWindows"] = await Workspace.Visible(w.Client, w.Session, w.Timeout);
            return failure;
        }
        if (baseline.All.Count == 0) await FillAll(w, baseline);
        // Daily competitor tracking starts from a fresh snapshot of the Analysis tables (Specs view, 3 reads).
        if (w.Tracking == "daily") await TrackCompetitors(w, baseline, recordToday: false, refreshSegments: true);
        await Pin(w);
        if (options["stopOnCompetitorRelease"]?.GetValue<bool>() == true && baseReply["notifications"]?["captureAvailable"]?.GetValue<bool>() != true) return Error("notification_capture_unavailable", "Native popup capture is unavailable; time was not advanced.");
        var unusable = Unusable(baseline, options);
        if (unusable != null) return Error("data_unavailable", unusable + " Time was not advanced.", Products(w, baseline, null));
        var startDate = baseline.Date;
        w.StartDate = startDate;
        var initialTriggers = Evaluate(w, baseline, baseline, baseline, options, new JsonArray(), compareChanges: false);
        if (initialTriggers.Count > 0) return await Stop(w, "threshold_already_met", days, 0, startDate, baseline, baseline, baseline, initialTriggers, null, null);
        var previous = baseline;
        var advanced = 0;
        for (var day = 0; day < days; day++)
        {
            var advanceParameters = new JsonObject { ["days"] = 1, ["speed"] = speed };
            if (w.IncludeProduction) advanceParameters["includeProduction"] = true;
            var advanceReply = await Call(w.Client, "game.time-advance", "", advanceParameters, w.Session, w.Timeout);
            if (!TryData(advanceReply, out var advance)) return CommandFailure("advance_unverified", advanceReply, startDate, advanced);
            advanced += advance!["daysAdvanced"]?.GetValue<int>() ?? 0;
            AppendNotifications(w.Notifications, advanceReply);
            if (options["stopOnCompetitorRelease"]?.GetValue<bool>() == true && advanceReply["notifications"]?["captureAvailable"]?.GetValue<bool>() != true) return Error("notification_capture_unavailable", "Native popup capture became unavailable. The game is paused; no further days were advanced.", advance);
            if (advance["paused"]?.GetValue<bool>() != true) return Error("pause_unverified", "The native clock was not confirmed paused after an advance.", advance);
            var stoppedDate = advance["stoppedDate"]?.ToString();
            var completedDay = advance["outcome"]?.ToString() == "completed" && advance["daysAdvanced"]?.GetValue<int>() == 1 && advance["wakeReasons"] is JsonArray reasons && reasons.Any(reason => reason?.ToString() == "target_reached");
            if (!completedDay)
            {
                // Native wake (popup, project change, default, player input). Read the day when one passed; the read may be blocked.
                Snap? woken = null;
                if (advance["daysAdvanced"]?.GetValue<int>() > 0)
                {
                    var (wokenReply, snap, _) = await DayRead(w, advanceReply, advance, stoppedDate);
                    AppendNotifications(w.Notifications, wokenReply);
                    if (snap != null && snap.Date == stoppedDate) woken = snap;
                }
                var blocking = await Dialogs.Blocking(w.Client, w.Session, w.Timeout, advance["dialogs"] as JsonArray);
                if (woken != null) Expansion(w, previous, woken);
                var triggers = woken == null ? new JsonArray() : Evaluate(w, baseline, previous, woken, options, w.Notifications, compareChanges: true);
                return await Stop(w, "game_event", days, advanced, startDate, baseline, previous, woken ?? previous, triggers, advance, blocking);
            }
            var (pulseReply, current, dayProblem) = await DayRead(w, advanceReply, advance, stoppedDate);
            AppendNotifications(w.Notifications, pulseReply);
            if (current == null && dayProblem != null)
                return await Stop(w, "threshold", days, advanced, startDate, baseline, previous, previous, new JsonArray { new JsonObject { ["product"] = w.Keys[0], ["metric"] = "product_missing", ["note"] = "The product row is no longer listed in Production (retired or renamed)." } }, advance, null);
            if (current == null)
            {
                // A popup that appeared after the day boundary covers the rows: stop cleanly instead of failing.
                var blocking = await Dialogs.Blocking(w.Client, w.Session, w.Timeout, checkPauseMenu: true, failedReply: pulseReply);
                if (blocking != null)
                {
                    blocking["wakeReasons"] = new JsonArray(new JsonNode?[] { "popup_after_day" }.Concat(blocking["wakeReasons"]!.AsArray().Select(r => r?.DeepClone())).ToArray());
                    return await Stop(w, "game_event", days, advanced, startDate, baseline, previous, previous, new JsonArray(), advance, blocking);
                }
                var failure = CommandFailure("pulse_unavailable", pulseReply, startDate, advanced);
                failure["visibleWindows"] = await Workspace.Visible(w.Client, w.Session, w.Timeout);
                return failure;
            }
            if (options["stopOnCompetitorRelease"]?.GetValue<bool>() == true && pulseReply["notifications"]?["captureAvailable"]?.GetValue<bool>() != true) return Error("notification_capture_unavailable", "Native popup capture became unavailable. The game is paused; no further days were advanced.", Products(w, current, null));
            if (current.Date != stoppedDate) return Error("date_mismatch", "The product read does not match the native stopped date. The game is paused; no further days were advanced.", Products(w, current, null));
            if (current.All.Count == 0 && w.NeedsAll) await FillAll(w, current);
            Expansion(w, previous, current);
            w.TodayPriceChanges.Clear();
            if (w.Tracking == "daily") await TrackCompetitors(w, current, recordToday: true, refreshSegments: MonthRolled(previous.Date, current.Date));
            if (options["monthlyDigest"]?.GetValue<bool>() == true && MonthRolled(previous.Date, current.Date)) await AddDigest(w, current);
            var dayTriggers = Evaluate(w, baseline, previous, current, options, w.Notifications, compareChanges: true);
            if (dayTriggers.Count > 0) return await Stop(w, "threshold", days, advanced, startDate, baseline, previous, current, dayTriggers, null, null);
            previous = current;
            MoveNotifications(w);
        }
        return await Stop(w, "completed", days, advanced, startDate, baseline, previous, previous, new JsonArray(), null, null);
    }

    // Pins the Production window the baseline read opened (explicit window-open), so daily time-advance cleanup does not
    // close it and no day reopens it. It is closed at the end when the watch opened it or an earlier agent command had
    // left it open (pinning would otherwise keep it forever); a player's own Production window stays open.
    private static async Task Pin(Watch w)
    {
        var pinReply = await Call(w.Client, "game.window-open", "Production", null, w.Session, w.Timeout);
        w.Pinned = TryData(pinReply, out var pin) && pin!["opened"]?.GetValue<bool>() == true;
        w.CloseAtEnd = w.Pinned && (w.PriorProduction == null || Workspace.AgentOpened(w.PriorProduction));
        w.IncludeProduction = w.Pinned && await PluginAtLeast(w.Client, w.Session, w.Timeout, new Version(0, 3, 9));
    }

    private static async Task<(JsonObject Reply, Snap? Snap, JsonObject? Problem)> ReadPulse(Watch w, string name)
    {
        var reply = await Call(w.Client, "game.product-pulse", name, null, w.Session, w.Timeout);
        if (!TryData(reply, out var data)) return (reply, null, NotFound(reply));
        var snap = new Snap { Date = data!["date"]?.ToString(), Finance = ProductionRows.Finance(pulseFinances: data["finances"] as JsonObject) };
        snap.Products[name] = ProductionRows.FromPulse(data);
        return (reply, snap, null);
    }

    // All Production rows for a snapshot that only has a pulse (single-product watch without rows attached to the advance).
    private static async Task FillAll(Watch w, Snap snap)
    {
        var reply = await Call(w.Client, "game.production-read", "", null, w.Session, w.Timeout);
        AppendNotifications(w.Notifications, reply);
        if (!TryData(reply, out var data)) return;
        ProductionRows.EnrichLines(data!);
        foreach (var row in data!["products"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) snap.All[row["productRef"]?.ToString() ?? row["name"]?.ToString() ?? ""] = ProductionRows.FromRow(row);
        snap.Lines = ProductionRows.Lines(data);
    }

    // Resolves requested names (or all rows) to productRefs once; later days read the same rendered rows.
    private static async Task<(JsonObject Reply, Snap? Snap, JsonObject? Problem)> ReadBaseline(Watch w, List<string> names, bool all, string? date)
    {
        var reply = await Call(w.Client, "game.production-read", "", null, w.Session, w.Timeout);
        if (!TryData(reply, out var data)) return (reply, null, null);
        var rows = data!["products"]?.AsArray().OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        if (all) w.Keys = rows.Select(row => row["productRef"]!.ToString()).ToList();
        else
        {
            foreach (var name in names)
            {
                var matches = rows.Where(row => row["name"]?.ToString() == name).ToArray();
                if (matches.Length != 1) return (reply, null, Error(matches.Length == 0 ? "not_found" : "ambiguous_target", matches.Length == 0 ? $"No production row is named '{name}'. Released products: {string.Join(", ", rows.Select(row => row["name"]?.ToString()))}. Time was not advanced." : $"Several production rows are named '{name}'; rename one or watch with --all-products. Time was not advanced."));
                w.Keys.Add(matches[0]["productRef"]!.ToString());
            }
        }
        if (w.Keys.Count == 0) return (reply, null, Error("not_found", "No released products are listed in Production. Time was not advanced."));
        var (_, snap, _) = await SnapFromRows(w, reply, data, date, null);
        return (reply, snap, null);
    }

    // Rows attached to the advance (includeProduction) when they contain the watched product(s); otherwise a separate read.
    private static async Task<(JsonObject Reply, Snap? Snap, JsonObject? Problem)> DayRead(Watch w, JsonObject advanceReply, JsonObject advance, string? date)
    {
        if (advance["production"] is JsonObject production && production["available"]?.GetValue<bool>() == true)
        {
            var rows = production["products"]?.AsArray().OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
            var complete = w.Single ? rows.Count(row => row["name"]?.ToString() == w.Keys[0]) == 1 : w.Keys.All(key => rows.Any(row => row["productRef"]?.ToString() == key));
            if (complete) return await SnapFromRows(w, advanceReply, production, date, advance["finances"] as JsonObject);
        }
        return await ReadDay(w, date, advance["finances"] as JsonObject);
    }

    private static async Task<bool> PluginAtLeast(HttpClient client, string session, int timeout, Version minimum)
    {
        var status = await Call(client, "status", "", null, session, timeout);
        return Version.TryParse(status["version"]?.ToString(), out var version) && version >= minimum;
    }

    private static async Task<(JsonObject Reply, Snap? Snap, JsonObject? Problem)> ReadDay(Watch w, string? date, JsonObject? advanceFinances)
    {
        if (w.Single) return await ReadPulse(w, w.Keys[0]);
        var reply = await Call(w.Client, "game.production-read", "", null, w.Session, w.Timeout);
        if (!TryData(reply, out var data)) return (reply, null, null);
        return await SnapFromRows(w, reply, data!, date, advanceFinances);
    }

    private static async Task<(JsonObject Reply, Snap? Snap, JsonObject? Problem)> SnapFromRows(Watch w, JsonObject reply, JsonObject data, string? date, JsonObject? advanceFinances)
    {
        ProductionRows.EnrichLines(data);
        var rows = data["products"]?.AsArray().OfType<JsonObject>().ToDictionary(row => row["productRef"]?.ToString() ?? "", StringComparer.Ordinal) ?? new Dictionary<string, JsonObject>();
        // Plugins >= 0.3.9 stamp production-read with the native date and the desktop finance line.
        var snap = new Snap { Date = data["date"]?.ToString() ?? date, Lines = ProductionRows.Lines(data) };
        foreach (var (key, row) in rows) snap.All[key] = ProductionRows.FromRow(row);
        foreach (var key in w.Keys)
        {
            // One product is keyed by its exact name (product-pulse), several by their productRefs.
            var row = w.Single ? rows.Values.SingleOrDefault(candidate => candidate["name"]?.ToString() == key) : rows.GetValueOrDefault(key);
            if (row != null) snap.Products[key] = ProductionRows.FromRow(row);
        }
        if (data["finances"]?["available"]?.GetValue<bool>() == true) snap.Finance = ProductionRows.Finance(pulseFinances: data["finances"] as JsonObject);
        else if (advanceFinances?["available"]?.GetValue<bool>() == true) snap.Finance = ProductionRows.Finance(advanceFinances: advanceFinances);
        else
        {
            var desktop = await Call(w.Client, "game.desktop-read", "", null, w.Session, w.Timeout);
            AppendNotifications(w.Notifications, desktop);
            snap.Finance = TryData(desktop, out var d) ? ProductionRows.Finance(desktopFinances: d!["finances"] as JsonObject) : new JsonObject { ["available"] = false };
        }
        return (reply, snap, null);
    }

    private static JsonObject? NotFound(JsonObject reply) => reply.At("operation", "error", "code").Str() is "not_found" or "ambiguous_target" ? CommandFailure("baseline_unavailable", reply) : null;

    // Null when every requested guard can be evaluated for at least one product or company metric.
    private static string? Unusable(Snap baseline, JsonObject options)
    {
        if (options["creditBelow"] != null && ProductionRows.Number(baseline.Finance["availableCredit"]) == null) return "Available Credit is unavailable for creditBelow.";
        if (options["balanceBelow"] != null && ProductionRows.Number(baseline.Finance["balancePerMonth"]) == null) return "The monthly balance is unavailable for balanceBelow.";
        if (options["stopOnDefault"]?.GetValue<bool>() == true && baseline.Finance["available"]?.GetValue<bool>() != true) return "Finances are unavailable for stopOnDefault.";
        var demandGuards = new[] { "demandUpPercent", "demandDownPercent", "minDemandToProduction", "maxDemandToProduction" }.Where(key => options[key] != null).ToArray();
        if (demandGuards.Length > 0 && baseline.Products.Values.All(p => p["demandPerMonth"] == null) && !CompanyGuard(options) && options["stockAbove"] == null) return "No watched product exposes a readable native demand for " + string.Join(", ", demandGuards) + ".";
        if (options["stockAbove"] != null && baseline.Products.Values.All(p => p["stockUnits"] == null) && !CompanyGuard(options) && demandGuards.Length == 0) return "No watched product exposes a readable native stock for stockAbove.";
        if (portfolioGuards.Any(key => options[key] != null) && baseline.All.Count == 0 && !CompanyGuard(options) && demandGuards.Length == 0 && options["stockAbove"] == null) return "Portfolio guards need the full Production table, which could not be read.";
        return null;
    }

    private static bool CompanyGuard(JsonObject options) => options["creditBelow"] != null || options["balanceBelow"] != null || options["stopOnDefault"]?.GetValue<bool>() == true || options["stopOnCompetitorRelease"]?.GetValue<bool>() == true || options["stopOnCompetitorPriceCutPercent"] != null || options["stopOnExpansionComplete"]?.GetValue<bool>() == true;

    private static JsonArray Evaluate(Watch w, Snap baseline, Snap previous, Snap current, JsonObject options, JsonArray notifications, bool compareChanges)
    {
        var triggers = new JsonArray();
        foreach (var (key, product) in current.Products)
        {
            var name = product["name"]?.ToString();
            var demand = ProductionRows.Number(product["demandPerMonth"]);
            var production = ProductionRows.Number(product["productionPerMonth"]);
            var stock = ProductionRows.Number(product["stockUnits"]);
            // The ratio is undefined while production is 0 (no lines): ratio guards skip that product instead of firing.
            double? ratio = demand != null && production > 0 ? demand / production : null;
            void Check(string metric, bool matched, double? observed) { if (options[metric] != null && matched) triggers.Add(new JsonObject { ["product"] = name, ["metric"] = metric, ["observed"] = observed, ["threshold"] = options[metric]!.DeepClone() }); }
            if (ratio != null)
            {
                Check("minDemandToProduction", ratio < Threshold(options, "minDemandToProduction"), Math.Round(ratio.Value, 4));
                Check("maxDemandToProduction", ratio > Threshold(options, "maxDemandToProduction"), Math.Round(ratio.Value, 4));
            }
            if (stock != null) Check("stockAbove", stock > Threshold(options, "stockAbove"), stock);
            if (!compareChanges || demand == null) continue;
            var sources = previous.Date == baseline.Date ? new[] { (Name: "previous_day", Snap: previous) } : new[] { (Name: "previous_day", Snap: previous), (Name: "start", Snap: baseline) };
            foreach (var source in sources)
            {
                if (!source.Snap.Products.TryGetValue(key, out var earlier) || ProductionRows.Number(earlier["demandPerMonth"]) is not double before) continue;
                var change = before == 0 ? demand > 0 ? double.PositiveInfinity : 0 : (demand.Value / before - 1) * 100;
                JsonNode? Percent() => double.IsFinite(change) ? JsonValue.Create(Math.Round(change, 2)) : null;
                if (options["demandUpPercent"] != null && change >= Threshold(options, "demandUpPercent")) triggers.Add(new JsonObject { ["product"] = name, ["metric"] = "demand_up_percent", ["comparedWith"] = source.Name, ["before"] = before, ["after"] = demand, ["changePercent"] = Percent(), ["threshold"] = options["demandUpPercent"]!.DeepClone() });
                if (options["demandDownPercent"] != null && -change >= Threshold(options, "demandDownPercent")) triggers.Add(new JsonObject { ["product"] = name, ["metric"] = "demand_down_percent", ["comparedWith"] = source.Name, ["before"] = before, ["after"] = demand, ["changePercent"] = Percent(), ["threshold"] = options["demandDownPercent"]!.DeepClone() });
            }
        }
        if (compareChanges)
            foreach (var (key, product) in baseline.Products.Where(pair => !current.Products.ContainsKey(pair.Key)))
                triggers.Add(new JsonObject { ["product"] = product["name"]?.DeepClone(), ["metric"] = "product_missing", ["note"] = "The product row is no longer listed in Production (retired or renamed)." });
        var finance = current.Finance;
        if (options["creditBelow"] != null && ProductionRows.Number(finance["availableCredit"]) is double credit && credit < Threshold(options, "creditBelow")) triggers.Add(new JsonObject { ["metric"] = "creditBelow", ["observed"] = credit, ["threshold"] = options["creditBelow"]!.DeepClone() });
        if (options["balanceBelow"] != null && ProductionRows.Number(finance["balancePerMonth"]) is double balance && balance < Threshold(options, "balanceBelow")) triggers.Add(new JsonObject { ["metric"] = "balanceBelow", ["observed"] = balance, ["threshold"] = options["balanceBelow"]!.DeepClone() });
        if (options["stopOnDefault"]?.GetValue<bool>() == true && finance["defaultActive"]?.GetValue<bool>() == true) triggers.Add(new JsonObject { ["metric"] = "company_in_default", ["default"] = finance["default"]?.DeepClone() });
        if (compareChanges && options["stopOnCompetitorRelease"]?.GetValue<bool>() == true)
            foreach (var item in notifications.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "competitor_cpu_released")) triggers.Add(new JsonObject { ["metric"] = "competitor_cpu_released", ["notification"] = item.DeepClone() });
        if (compareChanges && options["stopOnCompetitorPriceCutPercent"] != null)
        {
            var limit = Threshold(options, "stopOnCompetitorPriceCutPercent");
            // Compared with the price when the watch started, so a series of small cuts also trips the guard once.
            foreach (var cut in w.CutsSinceStart.Where(c => -c.ChangePercent >= limit && (options["competitorPriceScope"]?.ToString() != "near" || Near(w, current, c))))
            {
                if (!w.CutsReported.Add(cut.Company + "\u001f" + cut.Cpu)) continue;
                var change = MarketSignals.ChangeJson(cut);
                change["comparedWith"] = "watch_start";
                triggers.Add(new JsonObject { ["metric"] = "competitor_price_cut", ["scope"] = options["competitorPriceScope"]?.ToString() ?? "all", ["threshold"] = options["stopOnCompetitorPriceCutPercent"]!.DeepClone(), ["change"] = change });
            }
        }
        if (compareChanges && options["stopOnExpansionComplete"]?.GetValue<bool>() == true)
            foreach (var note in w.Synth.OfType<JsonObject>().Where(n => n["type"]?.ToString() == "factory_expansion_completed" && n["gameDate"]?.ToString() == current.Date))
                triggers.Add(new JsonObject { ["metric"] = "factory_expansion_completed", ["notification"] = note.DeepClone() });
        if (current.All.Count > 0) Portfolio(w, previous, current, options, notifications, compareChanges, triggers);
        return triggers;
    }

    // Guards over every own product (not only the watched ones): stock growth streaks, lines with near-zero demand,
    // stock worth many months of demand, and large day-over-day demand moves with their likely causes.
    private static void Portfolio(Watch w, Snap previous, Snap current, JsonObject options, JsonArray notifications, bool compareChanges, JsonArray triggers)
    {
        foreach (var (key, p) in current.All)
        {
            var name = p["name"]?.ToString();
            var demand = ProductionRows.Number(p["demandPerMonth"]);
            var stock = ProductionRows.Number(p["stockUnits"]);
            JsonObject T(string metric, JsonObject detail) { detail["product"] = name; detail["metric"] = metric; detail["scope"] = "portfolio"; return detail; }
            if (options["stockGrowthDays"] != null && compareChanges && previous.All.TryGetValue(key, out var before))
            {
                var grew = stock != null && ProductionRows.Number(before["stockUnits"]) is double lastStock && stock > lastStock;
                w.StockStreaks[key] = grew ? w.StockStreaks.GetValueOrDefault(key) + 1 : 0;
                if (w.StockStreaks[key] >= Threshold(options, "stockGrowthDays")) triggers.Add(T("stock_growing", new JsonObject { ["days"] = w.StockStreaks[key], ["stockUnits"] = stock, ["threshold"] = options["stockGrowthDays"]!.DeepClone() }));
            }
            // Idle lines and old stock fire when they newly appear (a condition that already held at the previous read is a
            // flag, so an old product does not block every watch); the start-of-watch conditions are reported as portfolioFlags.
            var prev = previous.All.GetValueOrDefault(key);
            var idle = IdleLines(p, options);
            if (idle != null)
            {
                if (compareChanges && (prev == null || IdleLines(prev, options) == null)) triggers.Add(T("lines_without_demand", idle));
                else if (!compareChanges) w.PortfolioFlags.Add(T("lines_without_demand", idle));
            }
            var old = OldStock(p, options);
            if (old != null)
            {
                if (compareChanges && (prev == null || OldStock(prev, options) == null)) triggers.Add(T("old_stock", old));
                else if (!compareChanges) w.PortfolioFlags.Add(T("old_stock", old));
            }
            if (options["anyDemandChangePercent"] != null && compareChanges && demand != null && previous.All.TryGetValue(key, out var prior) && ProductionRows.Number(prior["demandPerMonth"]) is double was && was > 0)
            {
                var change = (demand.Value / was - 1) * 100;
                if (Math.Abs(change) >= Threshold(options, "anyDemandChangePercent")) triggers.Add(T("demand_change", new JsonObject { ["before"] = was, ["after"] = demand, ["changePercent"] = Math.Round(change, 2), ["comparedWith"] = "previous_day", ["threshold"] = options["anyDemandChangePercent"]!.DeepClone(), ["likelyCauses"] = Causes(w, name, previous, current, notifications) }));
            }
        }
    }

    private static JsonObject? IdleLines(JsonObject p, JsonObject options)
    {
        var lines = ProductionRows.Number(p["lines"]) ?? 0;
        var demand = ProductionRows.Number(p["demandPerMonth"]);
        var made = ProductionRows.Number(p["productionPerMonth"]);
        if (lines <= 0 || demand == null) return null;
        if (options["idleDemandBelow"] != null && demand < Threshold(options, "idleDemandBelow")) return new JsonObject { ["lines"] = lines, ["demandPerMonth"] = demand, ["threshold"] = options["idleDemandBelow"]!.DeepClone() };
        if (options["idleDemandRatioBelow"] != null && made > 0 && demand / made < Threshold(options, "idleDemandRatioBelow")) return new JsonObject { ["lines"] = lines, ["demandPerMonth"] = demand, ["productionPerMonth"] = made, ["demandToProduction"] = Math.Round(demand.Value / made.Value, 4), ["threshold"] = options["idleDemandRatioBelow"]!.DeepClone() };
        return null;
    }

    private static JsonObject? OldStock(JsonObject p, JsonObject options)
    {
        var stock = ProductionRows.Number(p["stockUnits"]);
        var demand = ProductionRows.Number(p["demandPerMonth"]);
        if (options["stockMonthsAbove"] == null || !(stock > 0) || demand == null) return null;
        var months = demand > 0 ? stock!.Value / demand.Value : double.PositiveInfinity;
        if (months <= Threshold(options, "stockMonthsAbove")) return null;
        return new JsonObject { ["stockUnits"] = stock, ["demandPerMonth"] = demand, ["monthsOfDemand"] = double.IsFinite(months) ? Math.Round(months, 1) : null, ["threshold"] = options["stockMonthsAbove"]!.DeepClone(), ["note"] = "Stock worth this many months of current demand ages while newer CPUs arrive; retirement sells stock at roughly unit cost (game cleanup-preview)." };
    }

    // Tags plausible causes of a demand move from what happened between the two reads (not a causal proof).
    private static JsonArray Causes(Watch w, string? product, Snap previous, Snap current, JsonArray notifications)
    {
        var causes = new JsonArray();
        var own = PriceTools.ChangesBetween(previous.Date, current.Date);
        if (product != null && own.TryGetValue(product, out var mine)) causes.Add(new JsonObject { ["cause"] = "own_price_change", ["changes"] = new JsonArray(mine.Select(m => (JsonNode?)m.DeepClone()).ToArray()) });
        var siblings = own.Where(p => p.Key != product).Select(p => p.Key).ToArray();
        if (siblings.Length > 0) causes.Add(new JsonObject { ["cause"] = "own_sibling_price_change", ["products"] = new JsonArray(siblings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) });
        var released = current.All.Values.Select(p => p["name"]?.ToString()).Where(n => n != null && !previous.All.Values.Any(q => q["name"]?.ToString() == n)).ToArray();
        if (released.Length > 0) causes.Add(new JsonObject { ["cause"] = "own_release", ["products"] = new JsonArray(released.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) });
        var rivals = notifications.OfType<JsonObject>().Where(n => n["type"]?.ToString() == "competitor_cpu_released").Select(n => (JsonNode?)JsonValue.Create(n["text"]?.ToString())).ToArray();
        if (rivals.Length > 0) causes.Add(new JsonObject { ["cause"] = "rival_release", ["notifications"] = new JsonArray(rivals) });
        if (w.TodayPriceChanges.Count > 0) causes.Add(new JsonObject { ["cause"] = "rival_price_change", ["cpus"] = new JsonArray(w.TodayPriceChanges.Select(c => (JsonNode?)JsonValue.Create($"{c.Company} {c.Cpu} {c.ChangePercent.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)}%")).ToArray()) });
        if (MonthRolled(previous.Date, current.Date)) causes.Add(new JsonObject { ["cause"] = "month_rollover", ["note"] = "Demand and segment responses are recalculated at the start of a month." });
        if (causes.Count == 0) causes.Add(new JsonObject { ["cause"] = "unknown", ["note"] = w.Tracking == "daily" ? "No own change, release or tracked rival move was seen." : "No own change or release was seen; rival price moves are only checked daily with --track-competitor-prices daily." });
        return causes;
    }

    private static bool MonthRolled(string? before, string? after) => CliReads.Date(before) is DateTime a && CliReads.Date(after) is DateTime b && (a.Year != b.Year || a.Month != b.Month);

    // Factory expansion completion: capacity rose or the pending "(+N)" shrank between two reads.
    private static void Expansion(Watch w, Snap previous, Snap current)
    {
        if (previous.Lines == null || current.Lines == null) return;
        var capBefore = CliReads.Num(previous.Lines["capacity"]); var capAfter = CliReads.Num(current.Lines["capacity"]);
        var pendBefore = CliReads.Num(previous.Lines["pendingExpansion"]) ?? 0; var pendAfter = CliReads.Num(current.Lines["pendingExpansion"]) ?? 0;
        if (!(capAfter > capBefore || pendAfter < pendBefore)) return;
        var unassigned = CliReads.Num(current.Lines["unassigned"]);
        var note = MarketSignals.Note("factory_expansion_completed", current.Date ?? "", previous.Date, FormattableString.Invariant($"Factory expansion completed: {capBefore:0} -> {capAfter:0} lines; {unassigned:0} unassigned"), new JsonObject
        {
            ["capacityBefore"] = capBefore, ["capacityAfter"] = capAfter, ["pendingBefore"] = pendBefore, ["pendingAfter"] = pendAfter, ["unassignedLines"] = unassigned, ["exact"] = current.Lines["exact"]?.DeepClone(),
            ["note"] = "New lines start unassigned unless automation assigns them. Assigning them is your decision (product-production or plan-apply)."
        });
        note["id"] = "syn-watch-" + (w.Synth.Count + 1);
        w.Synth.Add(note);
    }

    // Reads the Analysis tables once (Specs, 3 markets), records synthesized notifications and restores the window layout.
    private static async Task TrackCompetitors(Watch w, Snap snap, bool recordToday, bool refreshSegments)
    {
        var own = (snap.All.Count > 0 ? snap.All.Values : snap.Products.Values).Select(p => p["name"]?.ToString()).Where(n => n != null).Select(n => n!).ToHashSet(StringComparer.Ordinal);
        var o = await MarketChanges.Observe(w.Client, w.Session, w.Timeout, own, snap.Date, shares: refreshSegments, marketView: false, MarketChanges.ReadThresholds(w.Options));
        await CloseOpenedViews(w);
        if (o.Catalogs.Count > 0) w.LastCatalogs = o.Catalogs;
        if (MarketSignals.Snapshot.FromJson(o.Store["snapshot"]) is MarketSignals.Snapshot stored) w.LastSegments = stored.Segments;
        var rivals = o.Catalogs.Values.SelectMany(rows => rows).Where(row => !own.Contains(row.Cpu) && row.Company != o.Player && row.Price > 0).ToList();
        if (w.StartPrices.Count == 0) foreach (var row in rivals) w.StartPrices.TryAdd(row.Company + "\u001f" + row.Cpu, row);
        else if (recordToday)
        {
            w.CutsSinceStart.Clear();
            var snapshot = MarketSignals.Snapshot.FromJson(o.Store["snapshot"]) ?? new MarketSignals.Snapshot();
            foreach (var group in rivals.GroupBy(row => row.Company + "\u001f" + row.Cpu))
            {
                var now = group.First();
                if (!w.StartPrices.TryGetValue(group.Key, out var then) || then.Price is not double from || now.Price is not double to || to >= from) continue;
                var (impact, summary) = MarketSignals.Impact(snapshot, now.Market, now.Mips, from, to, own, o.Player);
                w.CutsSinceStart.Add(new MarketSignals.PriceChange(now.Company, now.Cpu, group.Select(g => g.Market).Distinct().ToArray(), from, to, Math.Round((to / from - 1) * 100, 1), now.Mips, CliReads.Segment(w.LastSegments, now.Market, from), CliReads.Segment(w.LastSegments, now.Market, to), impact, summary) { ReferenceDate = w.StartDate });
            }
        }
        if (o.Diff == null) return;
        foreach (var change in o.Diff.PriceChanges) { w.PriceChanges.Add(change); if (recordToday) w.TodayPriceChanges.Add(change); }
        foreach (var note in o.Diff.Notifications) w.Synth.Add(note);
    }

    // Closes read-only views opened after the watch started (Analysis for catalogs, Research for digests); the pinned Production stays.
    private static async Task CloseOpenedViews(Watch w)
    {
        var now = Workspace.Scopes(await Workspace.Visible(w.Client, w.Session, w.Timeout));
        var opened = now.Where(scope => Workspace.Views.Contains(scope) && scope != "ProductionWindow" && !w.ScopesBefore.Contains(scope)).ToArray();
        if (opened.Length > 0) await Workspace.Close(w.Client, w.Session, w.Timeout, opened);
    }

    // A rival CPU near the watched products: within 0.5x..2x of a watched product's MIPS in its market, or in its price segment.
    private static bool Near(Watch w, Snap current, MarketSignals.PriceChange change)
    {
        var watched = (w.Keys.Count > 0 ? current.Products.Values : current.All.Values).Select(p => p["name"]?.ToString()).Where(n => n != null).ToHashSet();
        foreach (var market in change.Markets)
        {
            if (!w.LastCatalogs.TryGetValue(market, out var rows)) continue;
            foreach (var mine in rows.Where(r => watched.Contains(r.Cpu)))
            {
                if (mine.Mips > 0 && change.Mips > 0 && change.Mips / mine.Mips is >= 0.5 and <= 2) return true;
                if (change.NewSegment != null && CliReads.Segment(w.LastSegments, market, mine.Price) == change.NewSegment) return true;
            }
        }
        return false;
    }

    private static async Task AddDigest(Watch w, Snap current)
    {
        var digest = await Digest.Run(w.Client, w.Session, w.Timeout, current.Date);
        await CloseOpenedViews(w);
        w.Digests.Add(digest);
    }

    // Old single-product summary shape (kept for compatibility); out of stock is 0 units.
    private static JsonObject Summary(Snap snap, string key)
    {
        var p = snap.Products.GetValueOrDefault(key) ?? new JsonObject();
        return new JsonObject
        {
            ["date"] = snap.Date, ["lines"] = p["lines"]?.DeepClone(), ["demandPerMonth"] = p["demandPerMonth"]?.DeepClone(), ["productionPerMonth"] = p["productionPerMonth"]?.DeepClone(),
            ["stockUnits"] = p["stockUnits"]?.DeepClone(), ["outOfStock"] = p["outOfStock"]?.DeepClone(), ["balancePerMonth"] = snap.Finance["balancePerMonth"]?.DeepClone(),
            ["availableCredit"] = snap.Finance["availableCredit"]?.DeepClone(), ["salesPerMonth"] = snap.Finance["salesPerMonth"]?.DeepClone(), ["defaultDaysLeft"] = snap.Finance["defaultDaysLeft"]?.DeepClone()
        };
    }

    private static JsonArray Products(Watch w, Snap current, Snap? baseline)
    {
        var result = new JsonArray();
        var keys = w.Keys.Count > 0 ? w.Keys : current.Products.Keys.ToList();
        var duplicates = current.Products.Values.GroupBy(p => p["name"]?.ToString()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var key in keys)
        {
            if (!current.Products.TryGetValue(key, out var product)) { result.Add(new JsonObject { ["name"] = baseline?.Products.GetValueOrDefault(key)?["name"]?.DeepClone() ?? key, ["missing"] = true }); continue; }
            var item = product.DeepClone().AsObject();
            item.Remove("sliderMaxLines");
            if (!duplicates.Contains(item["name"]?.ToString())) item.Remove("productRef");
            if (item["manualLines"] == null) { item.Remove("manualLines"); item.Remove("contractLines"); }
            if (baseline != null && baseline.Date != current.Date && baseline.Products.TryGetValue(key, out var start) && ProductionRows.Number(start["demandPerMonth"]) is double before && ProductionRows.Number(product["demandPerMonth"]) is double after)
            {
                item["demandAtStart"] = before;
                item["demandChangePercent"] = before > 0 ? Math.Round((after / before - 1) * 100, 2) : null;
            }
            result.Add(item);
        }
        return result;
    }

    private static async Task<JsonObject> Stop(Watch w, string outcome, int requested, int advanced, string? startDate, Snap? baseline, Snap? previous, Snap? current, JsonArray triggers, JsonObject? nativeAdvance, JsonObject? blocking)
    {
        // A popup can appear a few frames after the last read (for example on the final day): check once before reporting.
        blocking ??= await Dialogs.Blocking(w.Client, w.Session, w.Timeout, nativeAdvance?["dialogs"] as JsonArray);
        if (current != null && blocking == null)
        {
            if (w.Single && current.Products.TryGetValue(w.Keys[0], out var single) && single["manualLines"] == null)
            {
                // Pulses from older plugins lack the manual/contract split; one Production read supplies it.
                var read = await Call(w.Client, "game.production-read", "", null, w.Session, w.Timeout);
                if (TryData(read, out var production))
                {
                    ProductionRows.EnrichLines(production!);
                    var rows = production!["products"]?.AsArray().OfType<JsonObject>().Where(row => row["name"]?.ToString() == w.Keys[0]).ToArray() ?? Array.Empty<JsonObject>();
                    if (rows.Length == 1) { single["manualLines"] = rows[0]["manualLines"]?.DeepClone(); single["contractLines"] = rows[0]["contractLines"]?.DeepClone(); }
                }
            }
            if (!w.Single && current.Finance["salesPerMonth"] == null)
            {
                var desktop = await Call(w.Client, "game.desktop-read", "", null, w.Session, w.Timeout);
                if (TryData(desktop, out var d) && ProductionRows.Finance(desktopFinances: d!["finances"] as JsonObject) is JsonObject finance && finance["available"]?.GetValue<bool>() == true) current.Finance = finance;
            }
            // End-of-watch competitor check (one Specs read per market) when not tracked daily.
            if (w.Tracking == "end" && advanced > 0) await TrackCompetitors(w, current, recordToday: false, refreshSegments: true);
        }
        var result = new JsonObject { ["ok"] = true, ["outcome"] = outcome };
        if (w.Single) result["product"] = w.Keys.FirstOrDefault();
        else result["watchedProducts"] = new JsonArray(((current ?? baseline)?.Products.Values.Select(p => p["name"]?.ToString() ?? "") ?? w.Requested).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        result["requestedDays"] = requested;
        result["daysAdvanced"] = advanced;
        result["startDate"] = startDate;
        result["stoppedDate"] = nativeAdvance?["stoppedDate"]?.DeepClone() ?? current?.Date ?? startDate;
        result["paused"] = true;
        if (current != null)
        {
            if (w.Single)
            {
                result["current"] = Summary(current, w.Keys[0]);
                if (baseline != null && baseline.Date != current.Date) result["baseline"] = Summary(baseline, w.Keys[0]);
                if (previous != null && baseline != null && previous.Date != baseline.Date && previous.Date != current.Date) result["previous"] = Summary(previous, w.Keys[0]);
            }
            else
            {
                var finance = current.Finance.DeepClone().AsObject();
                finance.Remove("available");
                if (finance["defaultActive"]?.GetValue<bool>() == false) finance.Remove("defaultActive");
                result["finances"] = finance;
            }
            if (current.Lines != null) result["lines"] = new JsonObject { ["capacity"] = current.Lines["capacity"]?.DeepClone(), ["unassigned"] = current.Lines["unassigned"]?.DeepClone(), ["pendingExpansion"] = current.Lines["pendingExpansion"]?.DeepClone(), ["exact"] = current.Lines["exact"]?["unassigned"]?.DeepClone() };
        }
        if (triggers.Count > 0)
        {
            result["triggers"] = triggers;
            var triggered = triggers.Select(t => t?["product"]?.ToString()).Where(name => name != null).Distinct().Select(name => (JsonNode?)JsonValue.Create(name)).ToArray();
            if (triggered.Length > 0) result["triggeredProducts"] = new JsonArray(triggered);
        }
        if (current != null) result["products"] = Products(w, current, baseline);
        MoveNotifications(w);
        var notes = new JsonArray(w.Seen.Skip(Math.Max(0, w.Seen.Count - 40)).Select(item => item?.DeepClone()).ToArray());
        foreach (var note in w.Synth) notes.Add(note?.DeepClone());
        if (notes.Count > 0) result["notifications"] = notes;
        if (w.Tracking != "off")
            result["competitorPrices"] = new JsonObject
            {
                ["tracking"] = w.Tracking, ["changes"] = new JsonArray(w.PriceChanges.Select(c => (JsonNode?)MarketSignals.ChangeJson(c)).ToArray()),
                ["note"] = w.Tracking == "daily" ? "Rival prices were read from the Analysis tables after every day." : "Rival prices were compared once at the end of the watch with the last stored snapshot, so changes are dated by that range (sinceDate). Use --track-competitor-prices daily for exact days and the --stop-on-competitor-price-cut-percent guard."
            };
        if (w.Digests.Count > 0) result["digests"] = w.Digests.DeepClone();
        if (w.PortfolioFlags.Count > 0) result["portfolioFlags"] = new JsonObject { ["atStart"] = w.PortfolioFlags.DeepClone(), ["note"] = "Portfolio conditions that already held when the watch started. They do not stop a watch; the guards fire when such a condition newly appears." };
        if (current != null && blocking == null)
        {
            var followUps = await PriceTools.DueFollowUps(w.Client, w.Session, w.Timeout, current.Date, current.All.Count > 0 ? current.All.Values : current.Products.Values);
            if (followUps.Count > 0) { result["priceFollowUps"] = followUps; await CloseOpenedViews(w); }
        }
        if (nativeAdvance != null) result["nativeAdvance"] = nativeAdvance.DeepClone();
        if (blocking != null) foreach (var key in new[] { "wakeReasons", "dialogs", "releaseForm" }) if (blocking[key] != null) result[key] = blocking[key]!.DeepClone();
        if (nativeAdvance?["wakeReasons"] is JsonArray nativeReasons)
        {
            var reasons = result["wakeReasons"] as JsonArray ?? new JsonArray();
            foreach (var reason in nativeReasons.Select(r => r?.ToString()).Where(r => r is not null and not "target_reached")) if (!reasons.Any(r => r?.ToString() == reason)) reasons.Add(reason);
            if (reasons.Count > 0) result["wakeReasons"] = reasons;
        }
        result["precision"] = "native-display-rounded";
        result["next"] = blocking?["next"]?.DeepClone() ?? (outcome switch
        {
            "completed" => "Time is paused after the requested days; no guard fired.",
            "game_event" => "A native event woke the advance (see nativeAdvance.wakeReasons). Time is paused; inspect it before continuing.",
            _ => "Time is paused. Inspect the trigger (triggers[].product names the product) and the current game state before deciding on a price or another action."
        });
        return result;
    }

    internal static async Task<JsonObject> ProbePrice(HttpClient client, JsonObject request, int timeout)
    {
        var target = request["target"]?.GetValue<string>() ?? "";
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        if (target.Length == 0 || target.StartsWith("product-ui:", StringComparison.Ordinal)) return Error("invalid_target", "Use one exact unique product name, not a transient productRef.");
        if (options.Select(pair => pair.Key).Except(new[] { "price", "speed" }, StringComparer.Ordinal).Any()) return Error("invalid_request", "price-probe accepts only price and optional speed.");
        if (!TryNumber(options["price"], out var amount) || amount is < 1 or > 9999 || amount != Math.Truncate(amount)) return Error("invalid_value", "price must be an integer from 1 to 9999.");
        var price = (int)amount;
        var speed = options["speed"] == null ? 3 : TryNumber(options["speed"], out var speedNumber) && speedNumber is >= 1 and <= 3 && speedNumber == Math.Truncate(speedNumber) ? (int)speedNumber : 0;
        if (speed == 0) return Error("invalid_value", "speed must be 1, 2 or 3.");
        var session = "probe-cli";
        var clockReply = await Call(client, "game.time-read", "", null, session, timeout);
        if (!TryData(clockReply, out var clock)) return CommandFailure("clock_unavailable", clockReply);
        if (clock!["paused"]?.GetValue<bool>() != true)
        {
            var pauseReply = await Call(client, "game.time-advance", "", new JsonObject { ["days"] = 0, ["speed"] = speed }, session, timeout);
            if (!TryData(pauseReply, out var pause) || pause!["paused"]?.GetValue<bool>() != true) return CommandFailure("pause_unverified", pauseReply);
        }
        var beforeReply = await Call(client, "game.product-pulse", target, null, session, timeout);
        if (!TryData(beforeReply, out var before) || !PulseReadable(before!)) return CommandFailure("baseline_unavailable", beforeReply);
        var previousPrice = await PriceTools.CurrentPrice(client, session, timeout, target);
        var setReply = await Call(client, "game.product-price", target, new JsonObject { ["price"] = price }, session, timeout);
        if (!TryData(setReply, out var set) || set!["outcome"]?.ToString() != "price_confirmed" || set["appliedValue"]?.GetValue<int>() != price) return new JsonObject { ["ok"] = false, ["outcome"] = "price_unverified", ["before"] = PulseSummary(before!), ["requestedPrice"] = price, ["commandReply"] = setReply.DeepClone(), ["next"] = "Inspect the native editor and current price. Do not repeat an uncertain price mutation blindly." };
        PriceTools.Record(target, previousPrice, price, set["date"]?.ToString(), "price-probe");
        var advanceReply = await Call(client, "game.time-advance", "", new JsonObject { ["days"] = 1, ["speed"] = speed }, session, timeout);
        if (!TryData(advanceReply, out var advance)) return new JsonObject { ["ok"] = false, ["outcome"] = "advance_unverified", ["priceApplied"] = true, ["price"] = price, ["commandReply"] = advanceReply.DeepClone(), ["next"] = "The price was applied. Inspect the active operation and clock before proceeding." };
        if (advance!["paused"]?.GetValue<bool>() != true) return new JsonObject { ["ok"] = false, ["outcome"] = "pause_unverified", ["priceApplied"] = true, ["price"] = price, ["nativeAdvance"] = advance.DeepClone() };
        if (advance["outcome"]?.ToString() != "completed" || advance["daysAdvanced"]?.GetValue<int>() != 1) return new JsonObject { ["ok"] = true, ["outcome"] = "interrupted", ["priceApplied"] = true, ["price"] = price, ["before"] = PulseSummary(before!), ["nativeAdvance"] = advance.DeepClone(), ["notifications"] = advanceReply["notifications"]?["items"]?.DeepClone(), ["next"] = "The price was applied, but one full day was not completed. Resolve the native interruption; do not assume demand was recalculated." };
        var afterReply = await Call(client, "game.product-pulse", target, null, session, timeout);
        if (!TryData(afterReply, out var after) || !PulseReadable(after!))
        {
            var blocking = await Dialogs.Blocking(client, session, timeout, checkPauseMenu: true, failedReply: afterReply);
            return new JsonObject { ["ok"] = blocking != null, ["outcome"] = blocking != null ? "interrupted" : "after_unavailable", ["priceApplied"] = true, ["price"] = price, ["daysAdvanced"] = 1, ["before"] = PulseSummary(before!), ["dialogs"] = blocking?["dialogs"]?.DeepClone(), ["releaseForm"] = blocking?["releaseForm"]?.DeepClone(), ["commandReply"] = blocking == null ? afterReply.DeepClone() : null, ["next"] = blocking?["next"]?.DeepClone() ?? "The game advanced one day and is paused. Read product-pulse manually; do not repeat the price change." };
        }
        if (after!["date"]?.ToString() != advance["stoppedDate"]?.ToString()) return new JsonObject { ["ok"] = false, ["outcome"] = "date_mismatch", ["priceApplied"] = true, ["price"] = price, ["nativeAdvance"] = advance.DeepClone(), ["after"] = PulseSummary(after) };
        var beforeDemand = ProductionRows.Number(before!["product"]?["demand"]?["value"]) ?? 0;
        var afterDemand = ProductionRows.Number(after!["product"]?["demand"]?["value"]) ?? 0;
        return new JsonObject { ["ok"] = true, ["outcome"] = "measured", ["product"] = target, ["priceApplied"] = true, ["price"] = price, ["daysAdvanced"] = 1, ["paused"] = true,
            ["before"] = PulseSummary(before), ["after"] = PulseSummary(after), ["demandChangePercent"] = beforeDemand == 0 ? null : JsonValue.Create((afterDemand / beforeDemand - 1) * 100),
            ["notifications"] = advanceReply["notifications"]?["items"]?.DeepClone(), ["precision"] = "native-display-rounded", ["next"] = "Compare sales, stock and company balance as well as demand. A single day does not prove a stable response." };
    }

    private static bool PulseReadable(JsonObject pulse) => pulse["product"]?["demand"]?["available"]?.GetValue<bool>() == true;

    private static JsonObject PulseSummary(JsonObject pulse)
    {
        var snap = new Snap { Date = pulse["date"]?.ToString(), Finance = ProductionRows.Finance(pulseFinances: pulse["finances"] as JsonObject) };
        var name = pulse["product"]?["name"]?.ToString() ?? "";
        snap.Products[name] = ProductionRows.FromPulse(pulse);
        return Summary(snap, name);
    }

    private static JsonObject Error(string code, string message, JsonNode? detail = null) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["detail"] = detail?.DeepClone() };
    private static JsonObject CommandFailure(string code, JsonObject reply, string? startDate = null, int advanced = 0) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = "A native command failed or could not be verified. No further days were requested." }, ["startDate"] = startDate, ["daysAdvanced"] = advanced, ["commandReply"] = reply.DeepClone(), ["next"] = "Inspect the operation and game clock. Never replay an uncertain mutation blindly." };
    private static double Threshold(JsonObject options, string key) => options[key] == null ? double.NaN : ProductionRows.Number(options[key]) ?? double.NaN;
    private static bool TryNumber(JsonNode? node, out double value) => double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    private static bool TryBool(JsonNode? node, out bool value) { value = false; return node == null || bool.TryParse(node.ToString(), out value); }

    internal static bool TryData(JsonObject reply, out JsonObject? data)
    {
        data = reply.At("operation", "state").Str() == "completed" ? reply.At("operation", "result", "data") as JsonObject : null;
        return reply.At("ok").Bool() == true && data != null;
    }

    private static void AppendNotifications(JsonArray destination, JsonObject reply)
    {
        foreach (var item in reply["notifications"]?["items"]?.AsArray() ?? new JsonArray())
            if (item != null && !destination.Any(existing => existing?["id"]?.ToString() == item["id"]?.ToString())) destination.Add(item.DeepClone());
    }

    internal static async Task<JsonObject> Call(HttpClient client, string command, string target, JsonObject? parameters, string session, int timeout, string? scope = null)
    {
        var request = new JsonObject { ["command"] = command, ["target"] = target, ["session"] = session };
        if (parameters != null) request["parameters"] = parameters;
        if (scope != null) { request["scope"] = scope; request["limit"] = 80; }
        JsonObject reply;
        try { reply = await Send(client, request); CollectAdvisories(reply); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return Error("response_unknown", error.Message); }
        var notices = new JsonArray();
        AppendNotifications(notices, reply);
        var captureAvailable = reply["notifications"]?["captureAvailable"]?.DeepClone();
        var captureError = reply["notifications"]?["captureError"]?.DeepClone();
        var id = reply["operation"]?["id"]?.ToString();
        if (id != null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            var delay = 15;
            while (reply["operation"]?["state"]?.ToString() is "accepted" or "running" && DateTime.UtcNow < deadline)
            {
                // Short polls keep composed loops fast; the bridge answers status queries between frames.
                await Task.Delay(delay);
                delay = Math.Min(100, delay + 10);
                try { reply = await Send(client, new JsonObject { ["command"] = "operation", ["target"] = id, ["session"] = session }); CollectAdvisories(reply); AppendNotifications(notices, reply); captureAvailable = reply["notifications"]?["captureAvailable"]?.DeepClone() ?? captureAvailable; captureError = reply["notifications"]?["captureError"]?.DeepClone() ?? captureError; }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return new JsonObject { ["ok"] = false, ["state"] = "unknown", ["operationId"] = id, ["error"] = new JsonObject { ["code"] = "response_unknown", ["message"] = error.Message } }; }
            }
            if (reply["operation"]?["state"]?.ToString() is "accepted" or "running") return new JsonObject { ["ok"] = false, ["state"] = "running", ["operationId"] = id, ["next"] = "Query this operation; do not repeat its mutation." };
        }
        reply["notifications"] = new JsonObject { ["items"] = notices, ["captureAvailable"] = captureAvailable, ["captureError"] = captureError };
        return reply;
    }

    private static async Task<JsonObject> Send(HttpClient client, JsonObject request)
    {
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("v1/command", content);
        response.EnsureSuccessStatusCode();
        return JsonSafe.ParseObject(await response.Content.ReadAsStringAsync());
    }
}
