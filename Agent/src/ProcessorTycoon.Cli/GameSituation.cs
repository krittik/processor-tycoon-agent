using System.Text.Json.Nodes;

// CLI composition of existing native reads: one company health snapshot with factual flags. It never chooses or performs a business action.
internal static class GameSituation
{
    internal static async Task<JsonObject> Run(HttpClient client, int timeout, JsonObject? options = null)
    {
        var session = "situation-cli";
        var flags = new JsonArray();
        void Flag(string kind, string note, JsonObject? detail = null) { var f = new JsonObject { ["kind"] = kind, ["note"] = note }; if (detail != null) f["detail"] = detail; flags.Add(f); }

        var desktopReply = await GameWatch.Call(client, "game.desktop-read", "", null, session, timeout);
        if (!GameWatch.TryData(desktopReply, out var desktop)) return Failure("desktop_unavailable", desktopReply);
        var fin = desktop!["finances"] as JsonObject ?? new JsonObject();
        double? Item(string key) => DisplayNumber.Money(fin["items"]?.AsArray().FirstOrDefault(i => i?["key"]?.ToString() == key)?["display"]?.ToString());
        var credit = DisplayNumber.Money(fin["availableCredit"]?.ToString()) ?? 0;
        var cash = DisplayNumber.Money(fin["cashDisplay"]?.ToString()) ?? 0;
        // Available Credit excludes positive cash (native rule): the reserve before default is credit + cash on hand.
        var headroom = credit + Math.Max(0, cash);
        var balance = DisplayNumber.Money(fin["balanceDisplay"]?.ToString()) ?? 0;
        var fixedOut = new[] { "interest", "production", "research", "development", "construction" }.Sum(k => Math.Abs(Item(k) ?? 0));
        var date = desktop["status"]?["date"]?.ToString();
        var finance = new JsonObject
        {
            ["date"] = date, ["cash"] = fin["cashDisplay"]?.DeepClone(), ["availableCredit"] = credit, ["cashOnHand"] = Math.Max(0, cash), ["headroomBeforeDefault"] = headroom, ["monthlyBalance"] = balance,
            ["monthlySales"] = Item("sales"), ["monthlyOutflows"] = fixedOut, ["reserveMonthsOfOutflows"] = fixedOut > 0 ? Math.Round(headroom / fixedOut, 1) : null,
            ["runwayMonthsAtCurrentBalance"] = balance < 0 ? Math.Round(headroom / -balance, 1) : null,
            ["default"] = fin["default"]?.DeepClone(), ["debt"] = fin["debt"]?.DeepClone(), ["creditTrend"] = fin["creditTrend"]?.DeepClone(),
            ["headroomNote"] = "Available Credit is the debt limit minus debt and does not include positive cash; headroomBeforeDefault = Available Credit + cash on hand."
        };
        if (fin["bankrupt"] is JsonObject fate) Flag("bankrupt", fate["note"]?.ToString() ?? "The company is bankrupt.", fate.DeepClone().AsObject());
        if (fin["default"]?["active"]?.GetValue<bool>() == true) Flag("in_default", "The bankruptcy countdown is running; Available Credit must be zero or more when it ends.", fin["default"]!.AsObject().DeepClone().AsObject());
        if (fixedOut > 0 && headroom < 3 * fixedOut) Flag("low_reserve", "Available Credit plus cash covers less than three months of current outflows (interest, factories, research, development, construction). A sudden demand collapse, such as a rival generation release, would leave little room to react.");
        var trap = CliReads.Num(fin["debt"]?["debtWhereInterestEqualsLimitGrowth"]);
        var debt = CliReads.Num(fin["debt"]?["debt"]);
        if (trap > 0 && debt >= 0.7 * trap) Flag("near_debt_trap", "Debt is at or above 70% of the level where interest cancels the credit limit's growth; new headroom is disappearing.", new JsonObject { ["debt"] = debt, ["debtWhereInterestEqualsLimitGrowth"] = trap });

        var (research, researchNotes) = await ResearchTools.Observe(client, session, timeout, date);
        var researchSummary = research == null ? new JsonObject { ["active"] = false } : new JsonObject { ["active"] = true, ["name"] = research["name"]?.DeepClone(), ["fundingPercent"] = research["fundingPercent"]?.DeepClone(), ["speed"] = research["researchSpeed"]?.DeepClone(), ["timeLeft"] = research["timeLeft"]?.DeepClone(), ["monthlyCost"] = research["fundingDisplay"]?.DeepClone() };
        if (research == null) Flag("research_inactive", "No research is running. Rivals keep researching; a product's competitive window usually ends when they release the next generation.");
        else if (CliReads.Num(research["fundingPercent"]) < 25) Flag("research_underfunded", "Research runs below 25% funding. Low funding is cheaper per technology but slow; compare the delay with how long your current products will stay competitive.");
        if (researchNotes.Count > 0) researchSummary["timeDrops"] = new JsonArray(researchNotes.Select(n => (JsonNode?)n).ToArray());

        var productionReply = await GameWatch.Call(client, "game.production-read", "", null, session, timeout);
        var products = new JsonArray();
        var ownNames = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? lines = null;
        if (GameWatch.TryData(productionReply, out var production))
        {
            ProductionRows.EnrichLines(production!);
            lines = ProductionRows.Lines(production!);
            var unassigned = (int?)CliReads.Num(lines["unassigned"]);
            var capacity = (int?)CliReads.Num(lines["capacity"]);
            if (unassigned > 0) Flag("idle_lines", $"{unassigned} of {capacity} production lines are unassigned; they still cost maintenance.", new JsonObject { ["unassignedLines"] = unassigned, ["capacity"] = capacity, ["exact"] = lines["exact"]?["unassigned"]?.DeepClone() });
            if (lines["consistent"]?.GetValueKind() == System.Text.Json.JsonValueKind.False) Flag("line_totals_inconsistent", "Product line assignments do not reconcile with the displayed totals; see lines.problems.", new JsonObject { ["problems"] = lines["problems"]?.DeepClone() });
            foreach (var row in production!["products"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var name = row["name"]?.ToString();
                if (name == null) continue;
                ownNames.Add(name);
                var compact = ProductionRows.FromRow(row);
                var demand = ProductionRows.Number(compact["demandPerMonth"]);
                var made = ProductionRows.Number(compact["productionPerMonth"]);
                var stock = ProductionRows.Number(compact["stockUnits"]);
                var p = new JsonObject { ["name"] = name, ["lines"] = row["productionLines"]?.DeepClone(), ["demandPerMonth"] = demand, ["productionPerMonth"] = made, ["stock"] = stock, ["outOfStock"] = compact["outOfStock"]?.DeepClone() };
                if (row["manualLines"] != null) { p["manualLines"] = row["manualLines"]!.DeepClone(); p["contractLines"] = row["contractLines"]?.DeepClone(); }
                products.Add(p);
                if (made > 0 && demand != null && made > demand * 1.2 && stock > demand) Flag("overproduction", $"{name}: production exceeds demand and stock already covers more than a month of demand.", p.DeepClone().AsObject());
            }
        }

        var o = await MarketChanges.Observe(client, session, timeout, ownNames, date, shares: true, marketView: false, MarketChanges.ReadThresholds(options));
        var overview = new JsonArray();
        foreach (var (name, m) in o.Shares?.Markets ?? new())
        {
            if (m.Size is not double size || size <= 0) continue;
            var pct = m.Unserved ?? 0;
            var own = o.Player != null && m.Companies.TryGetValue(o.Player, out var s) ? s : (double?)null;
            overview.Add(new JsonObject { ["market"] = name, ["unitsPerMonth"] = size, ["unservedPercent"] = pct, ["unservedUnitsPerMonth"] = Math.Round(size * pct / 100), ["yourSharePercent"] = own });
        }

        var markets = new JsonObject();
        foreach (var (market, rows) in o.Catalogs)
        {
            var rivals = rows.Where(r => !ownNames.Contains(r.Cpu) && r.Company != o.Player && r.Mips > 0).ToArray();
            if (rivals.Length == 0) continue;
            var best = rivals.OrderByDescending(r => r.Mips).First();
            var bestValue = rivals.Where(r => r.Price > 0).OrderByDescending(r => r.Mips / r.Price).FirstOrDefault();
            var own = new JsonArray();
            foreach (var mine in rows.Where(r => ownNames.Contains(r.Cpu) && r.Mips > 0))
            {
                var entry = new JsonObject { ["cpu"] = mine.Cpu, ["mips"] = mine.Mips, ["price"] = mine.Price, ["segment"] = CliReads.Segment(o.Shares?.SegmentPrices ?? new(), market, mine.Price), ["mipsVsBestRivalPercent"] = Math.Round(mine.Mips!.Value / best.Mips!.Value * 100, 1), ["rivalsFaster"] = rivals.Count(r => r.Mips > mine.Mips) };
                own.Add(entry);
                if (mine.Mips < best.Mips * 0.7) Flag("product_behind_market", $"{mine.Cpu} has {entry["mipsVsBestRivalPercent"]}% of the best {market} rival's MIPS ({best.Cpu}). Its demand depends on price and popularity and can fall sharply; plan the successor now.", entry.DeepClone().AsObject());
            }
            markets[market] = new JsonObject { ["bestRival"] = new JsonObject { ["cpu"] = best.Cpu, ["company"] = best.Company, ["mips"] = best.Mips, ["price"] = best.Price }, ["bestRivalValue"] = bestValue == null ? null : new JsonObject { ["cpu"] = bestValue.Cpu, ["company"] = bestValue.Company, ["mips"] = bestValue.Mips, ["price"] = bestValue.Price }, ["own"] = own };
        }

        var result = new JsonObject
        {
            ["ok"] = true, ["kind"] = "situation", ["finance"] = finance, ["research"] = researchSummary, ["lines"] = lines, ["products"] = products,
            ["marketChanges"] = MarketChanges.Report(o, "situation"),
            ["recentRivalPriceMoves"] = MarketSignals.Recent(o.Store, date, 30, "competitor_price_changed"),
            ["recentMarketSignals"] = MarketSignals.Recent(o.Store, date, 30, "rival_took_lead", "segment_price_changed", "unserved_demand_spike", "research_time_dropped"),
            ["newRivalCpus"] = new JsonArray((o.Store["popularity"] as JsonObject ?? new JsonObject()).Select(p => p.Value?.DeepClone()).Where(p => CliReads.Date(p?["firstSeen"]?.ToString()) is DateTime d && CliReads.Date(date) is DateTime t && (t - d).TotalDays <= 365).ToArray()),
            ["marketOverview"] = new JsonObject { ["markets"] = overview, ["segmentRecommendedPrices"] = o.Shares?.Flat.DeepClone(), ["segmentRecommendedPricesByMarket"] = o.Shares == null ? null : MarketChanges.SegmentsJson(o.Shares) },
            ["markets"] = markets, ["flags"] = flags,
            ["next"] = "Facts and flags only; no action was taken. Use them to decide research funding, reserves, capacity and the next CPU yourself. Run this regularly (for example monthly) and before large commitments."
        };
        return result;
    }

    private static JsonObject Failure(string code, JsonObject reply) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = "A native read failed; no situation snapshot was produced." }, ["commandReply"] = reply.DeepClone() };
}
