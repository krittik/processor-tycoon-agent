using System.Text.Json.Nodes;

// End-to-end runs of the CLI compositions against MockBridge. State files go to a temporary directory.
internal static class Smoke
{
    internal static async Task Run(Checker check)
    {
        var state = Path.Combine(Path.GetTempPath(), "pt-agent-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(state);
        Environment.SetEnvironmentVariable("PT_AGENT_STATE_DIR", state);
        try
        {
            await Pulse(check);
            await Situation(check);
            await CompetitorWatch(check);
            await PortfolioWatch(check);
            await ExpansionWatch(check);
            await Pricing(check);
            await Plan(check);
            await Others(check);
        }
        catch (Exception error) { check("smoke exception", false, error.ToString()); }
        finally { Environment.SetEnvironmentVariable("PT_AGENT_STATE_DIR", null); try { Directory.Delete(state, true); } catch { } }
    }

    private static (MockBridge Bridge, HttpClient Client) Start()
    {
        var bridge = new MockBridge();
        return (bridge, new HttpClient { BaseAddress = new Uri(bridge.Url), Timeout = TimeSpan.FromSeconds(20) });
    }

    private static void ResetState() { foreach (var f in Directory.GetFiles(Environment.GetEnvironmentVariable("PT_AGENT_STATE_DIR")!)) File.Delete(f); }
    private static JsonObject Req(string target, JsonObject parameters) => new() { ["target"] = target, ["parameters"] = parameters, ["session"] = "test" };
    private static double? N(JsonNode? n) => CliReads.Num(n);
    private static JsonObject? P(JsonObject result, string name) => result["products"]?.AsArray().OfType<JsonObject>().FirstOrDefault(p => p["name"]?.ToString() == name);

    private static async Task Pulse(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var r = await ProductsPulse.Run(client, Req("", new JsonObject()), 10);
            check("pulse ok", r["ok"]?.GetValue<bool>() == true, r.ToJsonString());
            check("pulse lines total/unassigned", N(r["lines"]?["total"]) == 1150 && N(r["unassignedLines"]) == 20 && N(r["lines"]?["pendingExpansion"]) == 50, r["lines"]?.ToJsonString() ?? "");
            check("pulse lines consistent", r["lines"]?["consistent"]?.GetValue<bool>() == true, r["lines"]?.ToJsonString() ?? "");
            var c46 = P(r, "C46");
            check("pulse sales join", N(c46?["price"]) == 589 && N(c46?["unitCost"]) == 300 && N(c46?["popularity"]) == 80, c46?.ToJsonString() ?? "");
            check("pulse units per line", N(c46?["unitsPerLinePerMonth"]) == 25, c46?.ToJsonString() ?? "");
            check("pulse unserved estimate", N(c46?["unservedDemandPerMonthEstimate"]) == 5000);
            check("pulse headroom", N(r["finances"]?["headroomBeforeDefault"]) == 1.55e9, r["finances"]?.ToJsonString() ?? "");
            check("pulse stock value", N(r["totals"]?["stockValueAtUnitCost"]) == 5000 * 200 + 90000 * 1, r["totals"]?.ToJsonString() ?? "");
        }
    }

    private static async Task Situation(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var s = await GameSituation.Run(client, 10);
            check("situation ok", s["ok"]?.GetValue<bool>() == true, s.ToJsonString());
            check("situation has Mobile", s["markets"]?["Mobile"] != null && s["markets"]?["Industries"] != null && s["markets"]?["Desktop"] != null, s["markets"]?.ToJsonString() ?? "");
            check("situation idle lines exact", s["flags"]!.AsArray().Any(f => f?["kind"]?.ToString() == "idle_lines" && N(f?["detail"]?["unassignedLines"]) == 20), s["flags"]!.ToJsonString());
            check("situation segment prices by market", N(s["marketOverview"]?["segmentRecommendedPricesByMarket"]?["Mobile"]?["High End"]) == 300 && N(s["marketOverview"]?["segmentRecommendedPricesByMarket"]?["Industries"]?["Industries"]) == 40, s["marketOverview"]?.ToJsonString() ?? "");
            check("situation player share", s["marketOverview"]?["markets"]?.AsArray().Any(m => m?["market"]?.ToString() == "Desktop" && N(m?["yourSharePercent"]) == 45) == true, s["marketOverview"]?.ToJsonString() ?? "");
            // A rival price cut between two reads shows up in market-changes and as a synthesized notification.
            var i = bridge.Rivals.FindIndex(r => r.Cpu == "M1"); bridge.Rivals[i] = bridge.Rivals[i] with { Price = 80 };
            var m = await MarketChanges.Run(client, 10);
            var change = m["changes"]?["priceChanges"]?.AsArray().OfType<JsonObject>().FirstOrDefault(c => c["cpu"]?.ToString() == "M1");
            check("market-changes mobile price change", change != null && N(change["from"]) == 100 && N(change["to"]) == 80 && N(change["changePercent"]) == -20, m.ToJsonString());
            check("market-changes synthesized", m["changes"]?["notifications"]?.AsArray().Any(n => n?["type"]?.ToString() == "competitor_price_changed" && n?["native"]?.GetValue<bool>() == false) == true);
            var again = await MarketChanges.Run(client, 10);
            check("market-changes cursor", again["changes"]?["priceChanges"]?.AsArray().Count == 0, again.ToJsonString());
        }
    }

    private static async Task CompetitorWatch(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var w = await GameWatch.Run(client, Req("", new JsonObject { ["products"] = "C46,C45", ["days"] = 5, ["stopOnCompetitorPriceCutPercent"] = 10 }), 10);
            check("competitor watch ok", w["ok"]?.GetValue<bool>() == true, w.ToJsonString());
            check("competitor watch stops on day 2", w["outcome"]?.ToString() == "threshold" && w["stoppedDate"]?.ToString() == "2017-04-23", $"{w["outcome"]} {w["stoppedDate"]}");
            var trigger = w["triggers"]?.AsArray().OfType<JsonObject>().FirstOrDefault(t => t["metric"]?.ToString() == "competitor_price_cut");
            check("competitor trigger detail", trigger != null && trigger["change"]?["cpu"]?.ToString() == "Kore i7 7700" && N(trigger["change"]?["changePercent"]) == -35.1, trigger?.ToJsonString() ?? w.ToJsonString());
            check("competitor impact hint", trigger?["change"]?["impactSummary"]?.ToString()?.Contains("C46") == true, trigger?["change"]?["impactSummary"]?.ToString() ?? "");
            check("competitor notification in watch", w["notifications"]?.AsArray().Any(n => n?["type"]?.ToString() == "competitor_price_changed" && n?["gameDate"]?.ToString() == "2017-04-23") == true, w["notifications"]?.ToJsonString() ?? "");
            check("competitor tracking daily", w["competitorPrices"]?["tracking"]?.ToString() == "daily");
            var near = await GameWatch.Run(client, Req("R25", new JsonObject { ["days"] = 1, ["stopOnCompetitorPriceCutPercent"] = 10, ["competitorPriceScope"] = "near" }), 10);
            check("near scope ignores far rivals", near["ok"]?.GetValue<bool>() == true && near["outcome"]?.ToString() == "completed", near.ToJsonString());
        }
    }

    private static async Task PortfolioWatch(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var w = await GameWatch.Run(client, Req("", new JsonObject { ["allProducts"] = true, ["days"] = 6, ["portfolioGuards"] = true, ["trackCompetitorPrices"] = "off" }), 10);
            check("portfolio watch ok", w["ok"]?.GetValue<bool>() == true, w.ToJsonString());
            check("portfolio flags at start", w["portfolioFlags"]?["atStart"]?.AsArray().Any(f => f?["product"]?.ToString() == "R25" && f?["metric"]?.ToString() == "old_stock") == true, w["portfolioFlags"]?.ToJsonString() ?? w.ToJsonString());
            check("portfolio stops on demand change", w["outcome"]?.ToString() == "threshold" && w["stoppedDate"]?.ToString() == "2017-04-25", $"{w["outcome"]} {w["stoppedDate"]} {w["triggers"]?.ToJsonString()}");
            var t = w["triggers"]?.AsArray().OfType<JsonObject>().FirstOrDefault(x => x["metric"]?.ToString() == "demand_change");
            check("demand change trigger", t != null && t["product"]?.ToString() == "C45" && t["scope"]?.ToString() == "portfolio" && t["likelyCauses"]?.AsArray().Count > 0, t?.ToJsonString() ?? "");
            check("expansion notification", w["notifications"]?.AsArray().Any(n => n?["type"]?.ToString() == "factory_expansion_completed" && N(n?["unassignedLines"]) == 70) == true, w["notifications"]?.ToJsonString() ?? "");
            check("watch lines at stop", N(w["lines"]?["capacity"]) == 1200 && N(w["lines"]?["unassigned"]) == 70, w["lines"]?.ToJsonString() ?? "");
        }
    }

    private static async Task ExpansionWatch(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var w = await GameWatch.Run(client, Req("C46", new JsonObject { ["days"] = 5, ["stopOnExpansionComplete"] = true, ["trackCompetitorPrices"] = "off" }), 10);
            check("expansion watch stops", w["ok"]?.GetValue<bool>() == true && w["outcome"]?.ToString() == "threshold" && w["stoppedDate"]?.ToString() == "2017-04-24", w.ToJsonString());
            check("expansion trigger", w["triggers"]?.AsArray().Any(t => t?["metric"]?.ToString() == "factory_expansion_completed") == true);
            check("single watch keeps old summary", w["current"]?["demandPerMonth"] != null && w["product"]?.ToString() == "C46");
            // End-of-watch competitor comparison (default "end" tracking) records a baseline without failing.
            var e = await GameWatch.Run(client, Req("C46", new JsonObject { ["days"] = 1, ["demandDownPercent"] = 99 }), 10);
            check("end tracking ok", e["ok"]?.GetValue<bool>() == true && e["competitorPrices"]?["tracking"]?.ToString() == "end", e.ToJsonString());
        }
    }

    private static async Task Pricing(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var dry = await PriceTools.Price(client, Req("C46", new JsonObject { ["price"] = 280, ["dryRun"] = true }), 10);
            var kinds = dry["check"]?["warnings"]?.AsArray().Select(x => x?["kind"]?.ToString()).ToList() ?? new();
            check("dry-run warnings", dry["ok"]?.GetValue<bool>() == true && kinds.Contains("below_unit_cost") && kinds.Contains("supply_limited") && kinds.Contains("segment_threshold_crossed") && kinds.Contains("cannibalizes_high_volume_sibling"), dry.ToJsonString());
            check("dry-run does not commit", bridge.Products[0].Price == 589 && !bridge.Log.Contains("game.product-price C46"));
            var commit = await PriceTools.Price(client, Req("C46", new JsonObject { ["price"] = 600, ["followUpDays"] = 2 }), 10);
            var data = commit["operation"]?["result"]?["data"];
            check("commit ok", data?["outcome"]?.ToString() == "price_confirmed" && N(data?["oldPrice"]) == 589 && bridge.Products[0].Price == 600, commit.ToJsonString());
            var history = PriceTools.History("C46", null);
            check("history recorded", history["products"]?["C46"]?.AsArray().Count == 1 && N(history["products"]?["C46"]?[0]?["from"]) == 589 && history["pendingFollowUps"]?.AsArray().Count == 1, history.ToJsonString());
            var w = await GameWatch.Run(client, Req("C46", new JsonObject { ["days"] = 2, ["demandDownPercent"] = 99, ["trackCompetitorPrices"] = "off" }), 10);
            check("follow-up reported", w["priceFollowUps"]?.AsArray().Count == 1 && w["priceFollowUps"]?[0]?["demand"]?.AsArray().Count >= 2, w["priceFollowUps"]?.ToJsonString() ?? w.ToJsonString());
            var revert = await PriceTools.Revert(client, Req("C46", new JsonObject()), 10);
            check("revert", bridge.Products[0].Price == 589 && revert["operation"]?["result"]?["data"]?["revertedTo"] != null, revert.ToJsonString());
            // --skip-checks still records the previous price (one sales-sheet read), so a revert works.
            var skip = await PriceTools.Price(client, Req("C46", new JsonObject { ["price"] = 610, ["skipChecks"] = true }), 10);
            var last = PriceTools.History("C46", 1)["products"]?["C46"]?[0];
            check("skip-checks records previous price", bridge.Products[0].Price == 610 && N(last?["from"]) == 589 && N(last?["to"]) == 610, last?.ToJsonString() ?? skip.ToJsonString());
            var revert2 = await PriceTools.Revert(client, Req("C46", new JsonObject { ["skipChecks"] = true }), 10);
            check("revert after skip-checks", bridge.Products[0].Price == 589, revert2.ToJsonString());
        }
    }

    private static async Task Plan(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            JsonObject Items() => new() { ["items"] = new JsonArray(new JsonObject { ["product"] = "C45", ["add"] = -100 }, new JsonObject { ["product"] = "C46", ["lines"] = "max" }, new JsonObject { ["product"] = "R25", ["price"] = 3 }) };
            var dryParameters = Items(); dryParameters["dryRun"] = true;
            var dry = await PriceTools.Plan(client, Req("", dryParameters), 10);
            var steps = dry["steps"]?.AsArray().OfType<JsonObject>().ToList() ?? new();
            check("plan dry-run steps", dry["ok"]?.GetValue<bool>() == true && steps.Count == 3 && N(steps[0]["new"]) == 400 && N(steps[1]["new"]) == 720 && N(steps[2]["new"]) == 3, dry.ToJsonString());
            check("plan dry-run no change", bridge.Products[1].Manual == 500 && bridge.Products[2].Price == 2);
            var apply = await PriceTools.Plan(client, Req("", Items()), 10);
            check("plan applied", apply["ok"]?.GetValue<bool>() == true && bridge.Products[1].Manual == 400 && bridge.Products[0].Manual == 720 && bridge.Products[2].Price == 3, apply.ToJsonString());
            check("plan readback verified", apply["steps"]?.AsArray().All(s => s?["verified"]?.GetValue<bool>() == true) == true);
            var tooMuch = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["product"] = "C45", ["lines"] = 5000 }) };
            var refused = await PriceTools.Plan(client, Req("", tooMuch), 10);
            check("plan refuses over capacity", refused["ok"]?.GetValue<bool>() == false && refused["error"]?["code"]?.ToString() == "insufficient_lines" && bridge.Products[1].Manual == 400, refused.ToJsonString());
            tooMuch = tooMuch.DeepClone().AsObject(); tooMuch["clamp"] = true;
            var clamped = await PriceTools.Plan(client, Req("", tooMuch), 10);
            check("plan clamps", clamped["ok"]?.GetValue<bool>() == true && bridge.Products[1].Manual == 400 && clamped["steps"]?[0]?["clamped"]?.GetValue<bool>() == true, clamped.ToJsonString());
        }
    }

    private static async Task Others(Checker check)
    {
        ResetState();
        var (bridge, client) = Start();
        using (bridge) using (client)
        {
            var cleanup = await PriceTools.Cleanup(client, Req("", new JsonObject()), 10);
            var r25 = cleanup["candidates"]?.AsArray().OfType<JsonObject>().FirstOrDefault(c => c["product"]?.ToString() == "R25");
            check("cleanup candidate", cleanup["ok"]?.GetValue<bool>() == true && r25 != null && N(r25["estimatedLiquidationValue"]) == 90000, cleanup.ToJsonString());
            var digest = await Digest.Run(client, "t", 10);
            check("digest", digest["kind"]?.ToString() == "monthly_digest" && digest["marketShare"]?.AsArray().Count > 0 && digest["upcomingCompletions"]?.AsArray().Count == 1, digest.ToJsonString());
            var compare = await ResearchTools.Compare(client, Req("", new JsonObject { ["fundingPercents"] = "25,50,200" }), 10);
            var levels = compare["levels"]?.AsArray().OfType<JsonObject>().ToList() ?? new();
            check("research compare", compare["ok"]?.GetValue<bool>() == true && levels.Count == 3 && levels[2]["skipped"] != null && N(levels[0]["timeLeftDays"]) == 800 && bridge.ResearchFunding == 100, compare.ToJsonString());
            // Research time drop is recorded between two observations.
            var (_, first) = await ResearchTools.Observe(client, "t", 10, "2017-04-21");
            bridge.Date = new DateTime(2017, 4, 23);
            var (_, notes) = await ResearchTools.Observe(client, "t", 10, "2017-04-23");
            check("research time drop", first.Count == 0 && notes.Any(n => n["type"]?.ToString() == "research_time_dropped" && n["technology"]?.ToString() == "L3 16MB"), string.Join(" ", notes.Select(n => n.ToJsonString())));
        }
    }
}

internal delegate void Checker(string name, bool ok, string detail = "");
