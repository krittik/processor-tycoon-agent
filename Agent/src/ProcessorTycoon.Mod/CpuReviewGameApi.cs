using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// An evidence checkpoint, not a demand forecast or an automatic business decision.
internal sealed class CpuReviewGameApi : IGameModule
{
    private static Review? current;
    private static readonly string run = Guid.NewGuid().ToString("N");
    private readonly GenericUi native;
    private readonly JObject evidence = new();
    private readonly JArray gaps = new();
    private JObject? draft;
    private string? date;
    private sealed class Review
    {
        public string Id, Run, Date, Name;
        public JObject Draft, Finances;
        public bool Missing;
        public string[] Severe;
        public readonly float CreatedRealtime = UnityEngine.Time.realtimeSinceStartup;
        public Review(string id, string run, string date, string name, JObject draft, JObject finances, bool missing, string[] severe) { Id = id; Run = run; Date = date; Name = name; Draft = draft; Finances = finances; Missing = missing; Severe = severe; }
    }
    public string[] Commands => new[] { "game.cpu-review" };
    public static object Schema => new { command = "game cpu-review EXACT_DRAFT_NAME --target-market All Markets|Desktop|Mobile|Industries --planned-price PRICE", result = "Player-visible evidence, gaps, risk signals and reviewId. No game-time advance or business commitment." };
    public CpuReviewGameApi(GenericUi native) => this.native = native;
    internal static void Clear() => current = null;

    public void Validate(Request request)
    {
        GameUi.RequiredTarget(request);
        GameUi.Parameters(request, "targetMarket", "plannedPrice");
        if (request.Value != null) throw new AgentError("invalid_request", "cpu-review uses named parameters.");
        if (request.Parameters?["targetMarket"]?.Value<string>() is not ("All Markets" or "Desktop" or "Mobile" or "Industries")) throw new AgentError("invalid_value", "targetMarket must be All Markets, Desktop, Mobile or Industries.");
        if (request.Parameters?["plannedPrice"]?.Type != JTokenType.Integer || request.Parameters["plannedPrice"]!.Value<int>() is < 1 or > 9999) throw new AgentError("invalid_value", "plannedPrice must be an integer from 1 to 9999.");
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        current = null;
        draft = new CpuGameApi(native, _ => { }).Preview();
        if ((string?)draft["settings"]?["name"]?["value"] != request.Target) throw new AgentError("context_changed", "Current CPU draft name differs from the requested review target.");
        date = (string?)TimeAdvanceController.ReadClock()["date"] ?? throw new AgentError("game_ui_mismatch", "Current game date is unavailable.");
        evidence["draft"] = DraftFingerprint(draft);
        evidence["finances"] = BalanceSnapshot.Read();
        if ((bool?)evidence["finances"]?["available"] != true) Gap("finances", (string?)evidence["finances"]?["reason"] ?? "Current finance UI unavailable.");
        var draftClose = GameUi.Controls(new GameUi(native).Read("CreateCpuWindow")).SingleOrDefault(c => (string?)c["role"] == "button" && (string?)c["label"] == "Close");
        var openScopes = new[] { "ResearchTreeWindow", "ProductionWindow", "AnalysisWindow" }.Where(scope => GameUi.Controls(new GameUi(native).Read(scope)).Length > 0).ToArray();
        var otherOpen = openScopes.Length > 0;
        if (draftClose == null || draftClose["blockedReason"] != null || otherOpen)
        {
            foreach (var key in new[] { "research", "production", "marketGuidance", "competitorSpecs", "competitorMarket" }) Gap(key, openScopes.Length > 0 ? "Other windows are open alongside the CPU draft (" + string.Join(", ", openScopes) + "). Close them with game window-close SCOPE and review again; the draft stays open." : "The CPU draft is covered or its Close control is unavailable; resolve the foreground window and review again.");
            yield break;
        }

        foreach (var step in CloseIfOpen("CreateCpuWindow", "Create CPU")) yield return step;

        var research = new ResearchGameApi(new GameUi(native));
        if (CanOpen("ResearchTreeWindow", "Research Desktop", "research")) foreach (var step in Capture("research", research, new Request { Command = "game.research-list", Parameters = new JObject { ["status"] = "available" } })) yield return step;
        foreach (var step in CloseIfOpen("ResearchTreeWindow", "Research")) yield return step;
        var production = new ProductionGameApi(native);
        if (CanOpen("ProductionWindow", "Production Desktop", "production"))
        {
            foreach (var step in Capture("production", production, new Request { Command = "game.production-read" })) yield return step;
            foreach (var step in Capture("capacity", new ProductionGameApi(native), new Request { Command = "game.production-capacity" })) yield return step;
        }
        foreach (var step in CloseIfOpen("FactoryManagementWindow", "FactoryManagementWindow")) yield return step;
        foreach (var step in CloseIfOpen("ProductionWindow", "Production")) yield return step;
        var market = new MarketGameApi(native, _ => { });
        var marketFilter = request.Parameters!["targetMarket"]!.Value<string>()!;
        var guidanceFilter = marketFilter is "Desktop" or "Mobile" ? marketFilter : "All";
        if (CanOpen("AnalysisWindow", "Analysis Desktop", "marketGuidance"))
        {
            foreach (var step in Capture("marketGuidance", market, new Request { Command = "game.market-share", Parameters = new JObject { ["market"] = guidanceFilter, ["category"] = "CPU Sales" } })) yield return step;
            var otherFilter = guidanceFilter == "All" ? "Desktop" : "All";
            foreach (var step in Capture("marketGuidanceOther", market, new Request { Command = "game.market-share", Parameters = new JObject { ["market"] = otherFilter, ["category"] = "CPU Sales" } })) yield return step;
            if (gaps.OfType<JObject>().FirstOrDefault(g => (string?)g["source"] == "marketGuidanceOther") is JObject otherGap) gaps.Remove(otherGap);
            foreach (var step in Capture("competitorSpecs", market, new Request { Command = "game.market-catalog", Parameters = new JObject { ["view"] = "specs", ["market"] = marketFilter, ["showRetired"] = false } })) yield return step;
            foreach (var step in Capture("competitorMarket", market, new Request { Command = "game.market-catalog", Parameters = new JObject { ["view"] = "market", ["market"] = marketFilter, ["showRetired"] = false } })) yield return step;
        }
        else { Gap("competitorSpecs", "Analysis is unavailable."); Gap("competitorMarket", "Analysis is unavailable."); }
        if (evidence["marketGuidance"] != null && (bool?)evidence["marketGuidance"]?["guidance"]?["complete"] != true) Gap("marketGuidance", "Native market preference guidance is incomplete; missing guidance is unknown, not no preference.");
        if (evidence["competitorSpecs"] != null && (bool?)evidence["competitorSpecs"]?["normalizedComplete"] != true) Gap("competitorSpecs", "The native CPU specs and current prices were incomplete; missing rows or columns are unknown, not zero competition.");
        foreach (var step in CloseIfOpen("AnalysisWindow", "Analysis")) yield return step;
        var reopen = new CpuGameApi(native, _ => { });
        foreach (var step in reopen.Prepare(new Request { Command = "game.cpu-preview" })) yield return step;
    }

    private IEnumerable<Request> CloseIfOpen(string scope, string name)
    {
        if (GameUi.Controls(new GameUi(native).Read(scope)).Length == 0) yield break;
        var session = new SessionGameApi(new GameUi(native));
        foreach (var step in session.Prepare(new Request { Command = "game.window-close", Target = name })) yield return step;
    }

    private bool CanOpen(string scope, string buttonName, string source)
    {
        var ui = new GameUi(native);
        if (GameUi.Controls(ui.Read(scope)).Length > 0) return true;
        var button = GameUi.Controls(ui.Read("DesktopButtons")).SingleOrDefault(c => (string?)c["name"] == buttonName && (string?)c["role"] == "button");
        if (button != null && button["blockedReason"] == null) return true;
        Gap(source, button == null ? "Native desktop button is unavailable." : "Native desktop button is covered or disabled: " + button["blockedReason"]);
        return false;
    }

    private IEnumerable<Request> Capture(string key, IGameModule module, Request request)
    {
        IEnumerator<Request>? iterator = null;
        try { module.Validate(request); iterator = module.Prepare(request).GetEnumerator(); }
        catch (Exception error) { Gap(key, error.Message); }
        if (iterator == null) yield break;
        using (iterator)
        {
            while (true)
            {
                Request? step = null;
                try { if (!iterator.MoveNext()) break; step = iterator.Current; }
                catch (Exception error) { Gap(key, error.Message); break; }
                yield return step!;
            }
        }
        if (gaps.OfType<JObject>().Any(g => (string?)g["source"] == key)) yield break;
        try { evidence[key] = module.Result(request); }
        catch (Exception error) { Gap(key, error.Message); }
    }

    private void Gap(string source, string reason) => gaps.Add(new JObject { ["source"] = source, ["reason"] = reason });

    public JObject Result(Request request)
    {
        if (draft == null || date == null) throw new AgentError("outcome_unverified", "CPU review did not capture the draft and date.");
        var currentDate = (string?)TimeAdvanceController.ReadClock()["date"];
        var currentDraft = new CpuGameApi(native, _ => { }).Preview();
        var finances = BalanceSnapshot.Read();
        // Multiplayer peer: the host's clock runs during collection, so only the draft must stay the same.
        bool timeMoves = MultiplayerInterop.Active;   // in a session no form pauses the game, on the host either
        if ((!timeMoves && currentDate != date) || !SameDraft(evidence["draft"], DraftFingerprint(currentDraft), timeMoves)) throw new AgentError("context_changed", $"Game date or CPU draft changed while collecting review evidence ({Changed(evidence["draft"], DraftFingerprint(currentDraft), timeMoves, currentDate != date)}). Run cpu-review again.");
        if (!timeMoves && !JToken.DeepEquals(finances, evidence["finances"])) throw new AgentError("context_changed", "Current finances changed while collecting review evidence. Run cpu-review again.");
        var price = request.Parameters!["plannedPrice"]!.Value<int>();
        var signals = Signals(price);
        var id = Guid.NewGuid().ToString("N");
        var severe = signals.OfType<JObject>().Select(s => (string?)s["kind"]).Where(k => k != null && SevereKinds.Contains(k)).Select(k => k!).Distinct().ToList();
        if (evidence["competitivePosition"] == null) { severe.Add("competitive_position_unknown"); signals.Add(new JObject { ["kind"] = "competitive_position_unknown", ["note"] = "Competitor specs were not collected, so the review could not compare the draft with the market. Close other windows and review again; do not develop a CPU without knowing how it compares with the best rivals." }); }
        current = new Review(id, run, date, request.Target, DraftFingerprint(currentDraft), finances, gaps.Count > 0, severe.ToArray());
        return new JObject
        {
            ["method"] = "game", ["outcome"] = gaps.Count == 0 ? "review_ready" : "review_incomplete", ["reviewId"] = id,
            ["date"] = date, ["name"] = request.Target, ["targetMarket"] = request.Parameters["targetMarket"]!.DeepClone(), ["plannedPrice"] = price,
            ["draftRestored"] = GameUi.Controls(new GameUi(native).Read("CreateCpuWindow")).Length > 0,
            ["evidence"] = evidence, ["gaps"] = gaps, ["riskSignals"] = signals, ["requiredAcknowledgements"] = new JArray(severe.ToArray()), ["precision"] = "display-rounded",
            ["projectCostNote"] = "Every CPU project pays the listed cost of all its selected components again; components used by earlier CPUs do not make later projects cheaper. Check the next draft's project cost rather than assuming a cheap follow-up.",
            ["next"] = gaps.Count == 0 ? severe.Count > 0 ? "Severe risks: " + string.Join(", ", severe) + ". Reconsider the design or timing. To proceed anyway, run cpu-develop NAME --review-id ID --acknowledge-risks " + string.Join(",", severe) + " --decision-reason TEXT that explains how each risk is handled." : "Evaluate this evidence yourself. If you choose to proceed, run cpu-develop NAME --review-id ID --decision-reason TEXT." : "Resolve the listed gaps and review again, or proceed with cpu-develop NAME --review-id ID --decision-reason TEXT --accept-missing-evidence true" + (severe.Count > 0 ? " --acknowledge-risks " + string.Join(",", severe) + " (severe risks: " + string.Join(", ", severe) + "; reconsider first)." : ".")
        };
    }

    private JArray Signals(int price)
    {
        var result = new JArray();
        var cost = Money((string?)draft!["specs"]?["unitCost"]);
        if (cost != null && price <= cost * 1.1m) result.Add(new JObject { ["kind"] = "thin_or_negative_unit_margin", ["plannedPrice"] = price, ["initialUnitCost"] = cost, ["note"] = "The displayed initial unit cost leaves at most 10% gross margin before other expenses; this is not a profit forecast." });
        var availableCredit = Money((string?)evidence["finances"]?["availableCredit"]);
        var cashOnHand = Math.Max(0m, Money((string?)evidence["finances"]?["cashDisplay"]) ?? 0m);
        // Available Credit excludes positive cash (native rule), so the money spendable before default is credit + cash on hand.
        var credit = availableCredit == null ? null : availableCredit + cashOnHand;
        var balance = Money((string?)evidence["finances"]?["balanceDisplay"]);
        var monthly = draft["project"]?["monthlyCost"]?.Value<decimal>();
        var days = Regex.Match((string?)draft["project"]?["duration"] ?? "", @"\d+");
        if (credit != null && balance != null && monthly != null && days.Success)
        {
            var durationDays = int.Parse(days.Value, CultureInfo.InvariantCulture);
            var monthlyBurn = Math.Max(0, monthly.Value - balance.Value);
            var baselineBurn = monthlyBurn * durationDays / 30m;
            var runway = new JObject { ["availableCredit"] = availableCredit, ["cashOnHand"] = cashOnHand, ["headroomBeforeDefault"] = credit, ["currentMonthlyBalance"] = balance, ["projectMonthlyCost"] = monthly, ["projectDays"] = durationDays, ["estimatedBurnToCompletion"] = Math.Round(baselineBurn, 0),
                ["estimatedHeadroomAtCompletion"] = Math.Round(credit.Value - baselineBurn, 0), ["estimatedCreditAtCompletion"] = Math.Round(Math.Min(availableCredit!.Value, credit.Value - baselineBurn), 0),
                ["note"] = "Scenario at the current monthly balance plus project expense, with no future sales, credit-limit growth or cost changes. Positive cash is spent before credit: headroomBeforeDefault = Available Credit + cash on hand, and the shortfall check uses it. After release, revenue must still cover interest, factories and research. Not a demand or bankruptcy forecast." };
            if (monthlyBurn > 0 && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var today))
            {
                var zero = today.AddDays((double)(Math.Max(0, credit.Value) / monthlyBurn * 30m));
                runway["estimatedCompletion"] = today.AddDays(durationDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (baselineBurn > credit) { runway["estimatedDefaultStart"] = zero.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); runway["estimatedBankruptcyDeadline"] = zero.AddDays(120).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            }
            evidence["runway"] = runway;
            if (baselineBurn > credit) result.Add(new JObject { ["kind"] = "baseline_credit_shortfall", ["availableCredit"] = availableCredit, ["cashOnHand"] = cashOnHand, ["headroomBeforeDefault"] = credit, ["estimatedBurnToCompletion"] = Math.Round(baselineBurn, 0), ["estimatedDefaultStart"] = runway["estimatedDefaultStart"]?.DeepClone(), ["estimatedBankruptcyDeadline"] = runway["estimatedBankruptcyDeadline"]?.DeepClone(), ["note"] = "Available Credit would fall below zero before completion: the company would enter default: a 120-day countdown that does not clear early, with bankruptcy if Available Credit is still below zero when it ends. Plan exactly how that recovery is funded; emergency sales may be disabled or small." });
        }
        if ((bool?)evidence["finances"]?["default"]?["active"] == true) result.Add(new JObject { ["kind"] = "company_in_default", ["default"] = evidence["finances"]!["default"]!.DeepClone(), ["note"] = "The bankruptcy countdown is already running; new spending deepens the hole that must be repaid before it ends." });
        CapacityEstimate();
        MarketSupply(result);
        MarketOverview();
        // Exact totals from production-read `lines` (plugin 0.4.0); the abbreviated summary ("1.15K") is only a fallback.
        var summary = evidence["production"]?["summary"];
        var exactLines = evidence["production"]?["lines"] as JObject;
        var lines = (int?)exactLines?["capacity"] ?? Integer((string?)summary?["productionLines"]);
        var used = (int?)exactLines?["used"] ?? Integer((string?)summary?["linesUsed"]);
        var unassigned = (int?)exactLines?["unassigned"];
        if (unassigned == 0 || unassigned == null && lines != null && used != null && used >= lines) result.Add(new JObject { ["kind"] = "no_idle_production_lines", ["productionLines"] = lines, ["linesUsed"] = used, ["note"] = "Current lines are occupied; reassignment or expansion remains a player decision." });
        var ownMips = Number((string?)draft["specs"]?["mips"]);
        var specs = evidence["competitorSpecs"]?["rows"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
        if (ownMips != null && (bool?)evidence["competitorSpecs"]?["normalizedComplete"] == true)
        {
            var own = new HashSet<string>((evidence["production"]?["products"] as JArray ?? new JArray()).Select(p => (string?)p["name"]).Where(n => n != null).Select(n => n!), StringComparer.Ordinal);
            var rivals = new List<(string Cpu, string Company, double Mips, decimal? Price)>();
            foreach (var row in specs)
            {
                var values = row["values"] as JObject;
                var cpu = (string?)values?["cpu"];
                if (cpu == null || own.Contains(cpu)) continue;
                var rivalMips = Mips(values?["mips"]);
                var rivalPrice = (values?["price"] as JObject)?["value"]?.Value<decimal>() ?? Money((string?)values?["price"]);
                if (rivalMips == null) continue;
                rivals.Add((cpu, (string?)values?["company"] ?? "", rivalMips.Value, rivalPrice));
                if (rivalMips >= ownMips && rivalPrice < price) result.Add(new JObject { ["kind"] = "apparent_mips_price_disadvantage", ["cpu"] = cpu, ["rivalMips"] = rivalMips, ["rivalPrice"] = rivalPrice, ["draftMips"] = ownMips, ["plannedPrice"] = price, ["note"] = "Compare package, power and segment fit before deciding; this is not a demand prediction." });
            }
            if (rivals.Count > 0)
            {
                var best = rivals.OrderByDescending(r => r.Mips).First();
                var priced = rivals.Where(r => r.Price > 0).OrderByDescending(r => r.Mips / (double)r.Price!.Value).ToArray();
                var position = new JObject
                {
                    ["draftMips"] = ownMips, ["plannedPrice"] = price, ["draftMipsPerDollar"] = Math.Round(ownMips.Value / price, 6),
                    ["rivalCount"] = rivals.Count, ["rivalsFaster"] = rivals.Count(r => r.Mips > ownMips), ["bestRival"] = new JObject { ["cpu"] = best.Cpu, ["company"] = best.Company, ["mips"] = best.Mips, ["price"] = best.Price },
                    ["draftMipsVsBestPercent"] = Math.Round(ownMips.Value / best.Mips * 100, 1),
                    ["note"] = "Rivals present in the target market today, excluding your own released CPUs. They keep researching and releasing during development; compare against where they will plausibly be at release, not only now."
                };
                if (priced.Length > 0) position["bestRivalValue"] = new JObject { ["cpu"] = priced[0].Cpu, ["company"] = priced[0].Company, ["mips"] = priced[0].Mips, ["price"] = priced[0].Price, ["mipsPerDollar"] = Math.Round(priced[0].Mips / (double)priced[0].Price!.Value, 6) };
                evidence["competitivePosition"] = position;
                if (ownMips < best.Mips * 0.5) result.Add(new JObject { ["kind"] = "far_below_market_performance", ["draftMips"] = ownMips, ["bestRivalMips"] = best.Mips, ["rivalsFaster"] = position["rivalsFaster"]!.DeepClone(), ["rivalCount"] = rivals.Count, ["note"] = "The draft has under half the performance of the best current rival in this market. Customers mostly buy the best offer; a product this far behind may earn too little to repay its development, especially with borrowed money." });
            }
        }
        return result;
    }

    internal static void RequireCurrent(Request request, JObject draft)
    {
        var review = current;
        if (review == null || review.Run != run || review.Id != (string?)request.Parameters?["reviewId"] || review.Name != request.Target) throw new AgentError("review_required", "No matching CPU review exists in this game process. Run cpu-review before developing; no Develop input was sent.");
        if (string.IsNullOrWhiteSpace((string?)request.Parameters?["decisionReason"])) throw new AgentError("review_required", "Supply --decision-reason explaining why this project is worth the risk; no Develop input was sent.");
        if (review.Missing && (bool?)request.Parameters?["acceptMissingEvidence"] != true) throw new AgentError("review_incomplete", "The review has missing evidence. Resolve it or use --accept-missing-evidence true with your decision reason; no Develop input was sent.");
        var acknowledged = ((string?)request.Parameters?["acknowledgeRisks"] ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var missingAck = review.Severe.Where(k => !acknowledged.Contains(k)).ToArray();
        if (missingAck.Length > 0) throw new AgentError("risk_acknowledgement_required", "The review raised severe risks: " + string.Join(", ", missingAck) + ". Reread their notes in the review and reconsider. To develop anyway, add --acknowledge-risks " + string.Join(",", review.Severe) + " and make --decision-reason explain how each risk is handled; no Develop input was sent.");
        // Multiplayer peer: the host's clock keeps running (open forms do not pause a session), so the date and finances
        // move between review and develop at the host's pace; the review stays valid for 3 real minutes while the draft is
        // unchanged.
        var today = (string?)TimeAdvanceController.ReadClock()["date"];
        bool timeMoves = MultiplayerInterop.Active;   // in a session no form pauses the game, on the host either
        bool dateStale = timeMoves ? UnityEngine.Time.realtimeSinceStartup - review.CreatedRealtime > 180f : review.Date != today;
        if (dateStale || !SameDraft(review.Draft, DraftFingerprint(draft), timeMoves) || (!timeMoves && !JToken.DeepEquals(review.Finances, BalanceSnapshot.Read()))) throw new AgentError("stale_review", timeMoves ? "The CPU draft changed or the review is more than 3 minutes old. Run cpu-review again; no Develop input was sent." : "Game date, CPU draft or player finances changed. Run cpu-review again; no Develop input was sent.");
        current = null;
    }

    // Native "Potential Sales" share is demand no company currently supplies.
    private void MarketSupply(JArray signals)
    {
        if (evidence["marketGuidance"]?["markets"] is not JObject markets) return;
        var target = (string?)evidence["competitorSpecs"]?["filters"]?["marketDropdown"]?["value"] ?? "";
        var market = markets.Properties().Select(p => p.Value as JObject).FirstOrDefault(m => string.Equals((string?)m?["displayName"], target, StringComparison.OrdinalIgnoreCase))
            ?? markets["total"] as JObject ?? markets.Properties().Select(p => p.Value as JObject).FirstOrDefault(m => (string?)m?["displayName"] == "Desktop");
        var share = market?["shares"]?.OfType<JObject>().FirstOrDefault(s => (string?)s["company"] == "Potential Sales")?["share"]?["value"]?.Value<double?>();
        var size = MarketSize(market);
        if (share == null || size == null) return;
        var unserved = Math.Round(size.Value * share.Value / 100);
        evidence["marketSupply"] = new JObject { ["market"] = market!["displayName"]!.DeepClone(), ["marketUnitsPerMonth"] = size, ["unservedPercent"] = share, ["unservedUnitsPerMonth"] = unserved };
        if (share >= 10) signals.Add(new JObject { ["kind"] = "supply_constrained_market", ["unservedPercent"] = share, ["unservedUnitsPerMonth"] = unserved, ["estimatedUnitsPerMonthAllLines"] = evidence["capacityEstimate"]?["estimatedUnitsPerMonthAllLines"]?.DeepClone(), ["note"] = "A large share of this market's demand is currently unserved. While supply is short, sales can be limited by your output more than by small performance differences, and output per line scales roughly inversely with die area. Compare die sizes by estimated monthly units × margin (game cpu-variants) as well as by MIPS; die size is fixed once development starts." });
    }

    // All markets side by side (size, unserved demand, segment recommended prices), so the architecture/market choice is compared rather than assumed.
    private void MarketOverview()
    {
        var all = new[] { evidence["marketGuidance"], evidence["marketGuidanceOther"] }.OfType<JObject>().ToArray();
        var allFilter = all.FirstOrDefault(v => (string?)v["filters"]?["marketDropdown"]?["value"] == "All");
        if (allFilter?["markets"] is not JObject markets) return;
        var prices = new JObject();
        foreach (var view in all) foreach (var item in view["guidance"]?["items"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) if (item["market"] != null) prices[(string)item["market"]!] = item["recommendations"]?["Recommended Price"]?.DeepClone();
        var overview = new JArray();
        foreach (var market in markets.Properties().Select(p => p.Value).OfType<JObject>())
        {
            var size = MarketSize(market);
            if (size is null or <= 0) continue;
            var unserved = market["shares"]?.OfType<JObject>().FirstOrDefault(s => (string?)s["company"] == "Potential Sales")?["share"]?["value"]?.Value<double?>();
            overview.Add(new JObject { ["market"] = market["displayName"]?.DeepClone(), ["unitsPerMonth"] = size, ["unservedPercent"] = unserved, ["unservedUnitsPerMonth"] = unserved == null ? null : Math.Round(size.Value * unserved.Value / 100) });
        }
        evidence["marketOverview"] = new JObject { ["markets"] = overview, ["segmentRecommendedPrices"] = prices, ["note"] = "Architecture decides the market (CISC Desktop, RISC Industries). Compare markets by size, unserved demand, segment price levels and rival strength before choosing; revenue per unit of capacity can differ several-fold between them." };
    }

    private static double? MarketSize(JObject? market) => market?["size"] switch { JObject o => o["value"]?.Value<double?>(), JValue v when v.Type is JTokenType.Float or JTokenType.Integer => v.Value<double>(), _ => null };

    // Rough monthly output of the current lines for this draft, scaled from the native per-line reference (its tooltip names the reference die and yield).
    private void CapacityEstimate()
    {
        var expansion = evidence["capacity"]?["expansion"];
        var reference = Regex.Match((string?)expansion?["details"]?["production"] ?? "", @"([\d,.]+)\s*([KM]?)\s*units", RegexOptions.IgnoreCase);
        var basis = Regex.Match((string?)expansion?["detailTooltips"]?["Production"] ?? "", @"([\d.]+)\s*mm.*?([\d.]+)%", RegexOptions.IgnoreCase);
        var lines = evidence["capacity"]?["currentLines"]?.Value<int?>();
        var die = draft?["settings"]?["dieSizeMm2"]?["value"]?.Value<double?>();
        var yield = Number((string?)draft?["specs"]?["yield"]);
        if (!reference.Success || !basis.Success || lines == null || die is null or <= 0 || yield == null) return;
        var perLine = double.Parse(reference.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) * (reference.Groups[2].Value.ToUpperInvariant() switch { "K" => 1e3, "M" => 1e6, _ => 1 });
        var refDie = double.Parse(basis.Groups[1].Value, CultureInfo.InvariantCulture);
        var refYield = double.Parse(basis.Groups[2].Value, CultureInfo.InvariantCulture);
        var draftPerLine = perLine * refDie / die.Value * yield.Value / refYield;
        evidence["capacityEstimate"] = new JObject
        {
            ["currentLines"] = lines, ["nativeReferencePerLinePerMonth"] = perLine, ["referenceBasis"] = expansion!["detailTooltips"]!["Production"]!.DeepClone(),
            ["draftDieMm2"] = die, ["draftYieldPercent"] = yield, ["estimatedUnitsPerLinePerMonth"] = Math.Round(draftPerLine / 10) * 10, ["estimatedUnitsPerMonthAllLines"] = Math.Round(draftPerLine * lines.Value / 10) * 10,
            ["note"] = "Rough estimate scaled by die area and yield from the native per-line monthly reference; small dies can come out somewhat higher. Compare it with the units per month a market share target or contract would require. Real output appears in production-read after lines are assigned."
        };
    }

    private static readonly string[] SevereKinds = { "far_below_market_performance", "baseline_credit_shortfall", "company_in_default" };
    // While time moves (Multiplayer peer) specs and project cost are recalculated daily (unit cost, inflation): only
    // the design itself (settings and hardware) must match.
    private static bool SameDraft(JToken? reviewed, JObject current, bool timeMoves) =>
        timeMoves ? JToken.DeepEquals(reviewed?["settings"], current["settings"]) && JToken.DeepEquals(Parts(reviewed?["hardware"]), Parts(current["hardware"])) : JToken.DeepEquals(reviewed, current);
    // The first differing draft field, so a client sees what moved (e.g. "settings.l1CacheKB").
    private static string Changed(JToken? reviewed, JObject current, bool timeMoves, bool dateMoved)
    {
        foreach (var section in timeMoves ? new[] { "settings", "hardware" } : new[] { "settings", "hardware", "specs", "project" })
        {
            var before = timeMoves && section == "hardware" ? Parts(reviewed?[section]) : reviewed?[section];
            var after = timeMoves && section == "hardware" ? Parts(current[section]) : current[section];
            if (JToken.DeepEquals(before, after)) continue;
            var keys = ((before as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Concat((after as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Name);
            var key = keys.FirstOrDefault(k => !JToken.DeepEquals(before?[k], after?[k]));
            return "changed: " + section + (key != null ? "." + key : "");
        }
        return dateMoved && !timeMoves ? "changed: date" : "changed: draft";
    }

    // Hardware labels carry values that move with the date ("Process Node | 93.58% | 6µm": node maturity), so while a
    // session clock runs only the chosen parts are compared: the label tokens without numbers.
    private static JToken? Parts(JToken? hardware) => hardware is not JObject parts ? hardware : new JObject(parts.Properties().Select(p => new JProperty(p.Name,
        p.Value.Type == JTokenType.String ? string.Join(" | ", ((string)p.Value!).Split('|').Select(t => t.Trim()).Where(t => !System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\d.,]+\s*%?$"))) : p.Value.DeepClone())));
    private static JObject DraftFingerprint(JObject draft) => new() { ["settings"] = draft["settings"]!.DeepClone(), ["hardware"] = draft["hardware"]!.DeepClone(), ["specs"] = draft["specs"]!.DeepClone(), ["project"] = draft["project"]!.DeepClone() };
    private static decimal? Money(string? text) => BalanceSnapshot.Money(text);
    // Numeric JSON must not be stringified with the OS culture: ru-RU "0,09" previously parsed as 9.
    private static double? Mips(JToken? token) => token switch { JValue { Type: JTokenType.Float or JTokenType.Integer } value => value.Value<double>(), JObject obj => Number((string?)obj["display"]), _ => Number((string?)token) };
    // Suffix-aware: draft MIPS "149.31K" is 149310 (it used to be read as 149.31 and raised a false far_below_market_performance).
    private static double? Number(string? text) => DisplayNumber.First(text);
    private static int? Integer(string? text) => DisplayNumber.FieldInt(text);
}
