using System.Text.Json.Nodes;
using ProcessorTycoonShared;

// Pure-logic tests only: nothing here talks to the game or writes the tools/ state files.
var failures = new List<string>();
var passed = 0;
void Check(string name, bool condition, string detail = "") { if (condition) passed++; else failures.Add(name + (detail.Length > 0 ? ": " + detail : "")); }
void Near(string name, double? actual, double expected, double tolerance = 1e-9) => Check(name, actual != null && Math.Abs(actual.Value - expected) <= tolerance, $"expected {expected}, got {actual?.ToString() ?? "null"}");

// ---------- DisplayNumber (P0 #1, #5) ----------
Near("K suffix", DisplayNumber.First("1.15K"), 1150);
Near("K suffix MIPS", DisplayNumber.First("149.31K"), 149310);
Near("money K", DisplayNumber.Money("$1.95K"), 1950);
Near("B suffix", DisplayNumber.First("2.05B"), 2_050_000_000);
Near("negative money M", DisplayNumber.Money("-$22.14M"), -22_140_000);
Near("thousands separator", DisplayNumber.First("1,234"), 1234);
Near("small decimal", DisplayNumber.First("0.09"), 0.09);
Check("decimal exact 4.06M", DisplayNumber.First("4.06M") == 4_060_000d, DisplayNumber.First("4.06M").ToString()!);
Near("field abbreviated", DisplayNumber.Field("Production lines: | 1.15K (+50)"), 1150);
Check("pending raw", DisplayNumber.Pending("Production lines: | 1.15K (+50)") == 50);
Check("pending absent", DisplayNumber.Pending("Production lines: | 12") == null);
Check("field int", DisplayNumber.FieldInt("Lines used: | 1.1K") == 1100);
Check("field reversed order", DisplayNumber.FieldInt("8 | Employees:") == 8);
Check("field plain", DisplayNumber.FieldInt("Production lines: | 1") == 1);
Check("tryparse rate", DisplayNumber.TryParse("1.15K/m", out var rate) && rate == 1150);
Check("tryparse percent", DisplayNumber.TryParse("85.00%", out var pct) && pct == 85);
Check("tryparse rejects text", !DisplayNumber.TryParse("Out of Stock!", out _));
Check("tryparse rejects two numbers", !DisplayNumber.TryParse("$80 -> $34", out _));
Near("unit word is not a suffix", DisplayNumber.First("5 KHz"), 5);
Near("KB unit is not a suffix", DisplayNumber.First("4KB"), 4);
var moneys = DisplayNumber.Moneys("$1.95K -> $1.20K");
Check("unit cost now->later", moneys.Length == 2 && moneys[0] == 1950 && moneys[1] == 1200, string.Join(",", moneys));
Check("identifier digits ignored", DisplayNumber.First("i5004") == null);
Near("precision abbreviated", DisplayNumber.FirstToken("1.15K")?.Precision, 5);
Near("precision integer", DisplayNumber.FirstToken("1,234")?.Precision, 0.5);
Check("abbreviated flag", DisplayNumber.FirstToken("1.15K")?.Abbreviated == true && DisplayNumber.FirstToken("950")?.Abbreviated == false);
Check("clean float noise", DisplayNumber.Clean(4059999.9999999995) == 4060000d);
Check("clean keeps value", DisplayNumber.Clean(0.123456) == 0.123456);
Near("negative rate", DisplayNumber.First("-1/d"), -1);
Near("over-assigned", DisplayNumber.All("1.15K (-12)")[1], -12);

// ---------- Production indicator parsing ----------
var ind = ProductionRows.Read(new[] { "Demand: | 1.15K/m", "Stock: | Out of Stock!" }, "Demand");
Check("indicator K", ind.Available && ind.Value == 1150);
Check("indicator out of stock", ProductionRows.Read(new[] { "Stock: | Out of Stock!" }, "Stock") is { Available: true, OutOfStock: true, Value: 0 });
Check("leading int suffix-aware", ProductionRows.LeadingInt("Production lines: | 1.15K (+50)") == 1150);

// ---------- Line accounting (P0 #1 sanity check, #8 unassignedLines) ----------
var rows = new List<LineAccounting.Row> { new("A", 600, 600, 600, 610), new("B", 530, 530, 500, 510) };
var lines = LineAccounting.Compute("Production lines: | 1.14K (+50)", "Lines used: | 1.13K", "Used by contracts: | 30", "Used by clients: | 0", rows);
Check("unassigned exact from sliders", lines.Unassigned == 10 && lines.UnassignedExact);
Check("capacity from sliders", lines.Capacity == 1140 && lines.CapacityExact, $"{lines.Capacity}");
Check("used from rows", lines.Used == 1130);
Check("pending", lines.Pending == 50);
Check("consistent", lines.Consistent == true, string.Join("; ", lines.Problems));
var bad = LineAccounting.Compute("Production lines: | 1.14K", "Lines used: | 1.2K", "", "", rows);
Check("inconsistent flagged", bad.Consistent == false && bad.Problems.Count > 0);
var desktop = LineAccounting.Compute("Production lines: | 1.15K", "Lines used: | 1.14K", "", "", new List<LineAccounting.Row> { new("A", 1142, 1142, 1142, 1150) }, LineAccounting.DesktopLabel("1142/1150"));
Check("desktop exact", desktop.Capacity == 1150 && desktop.Used == 1142 && desktop.Unassigned == 8 && desktop.Basis == "desktop_icon_exact");
Check("desktop label rejects text", LineAccounting.DesktopLabel("Production") == null);
var small = LineAccounting.Compute("Production lines: | 12", "Lines used: | 10", "Used by contracts: | 0", "Used by clients: | 0", new List<LineAccounting.Row> { new("A", 10, 10, 10, 12) });
Check("small exact", small.Capacity == 12 && small.Unassigned == 2 && small.Basis == "native_summary_exact");
var noSliders = LineAccounting.Compute("Production lines: | 1.15K", "Lines used: | 1.15K", "", "", new List<LineAccounting.Row>());
Check("rounded fallback", noSliders.Capacity == 1150 && noSliders.Basis == "display_rounded" && !noSliders.UnassignedExact);
var full = LineAccounting.Compute("Production lines: | 1.15K", "Lines used: | 1.15K", "", "", new List<LineAccounting.Row> { new("A", 1150, 1150, 1150, 1150) });
Check("zero free lines", full.Unassigned == 0 && full.UnassignedExact);

// ---------- Market signals: competitor price diff (P1 #6, #10) ----------
MarketSignals.Snapshot Snap(string date, params (string Market, string Company, string Cpu, double Mips, double Price)[] entries)
{
    var s = new MarketSignals.Snapshot { Date = date };
    foreach (var e in entries)
    {
        if (!s.Markets.TryGetValue(e.Market, out var m)) s.Markets[e.Market] = m = new Dictionary<string, MarketSignals.Entry>();
        m[e.Cpu] = new MarketSignals.Entry(e.Company, e.Cpu, e.Mips, e.Price, 2017);
    }
    s.Segments["Desktop"] = new Dictionary<string, double> { ["High End"] = 500, ["Mid Range"] = 250, ["Low End"] = 0 };
    return s;
}
var own = new HashSet<string> { "C46" };
var before = Snap("2017-04-20", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "Inlet", "Kore i7 7700", 119660, 707), ("Desktop", "Inlet", "Kore i5 7400", 49430, 293));
var after = Snap("2017-04-21", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "Inlet", "Kore i7 7700", 119660, 459), ("Desktop", "Inlet", "Kore i5 7400", 49430, 293));
var diff = MarketSignals.Diff(before, after, own, "Me");
Check("one price change", diff.PriceChanges.Count == 1);
var change = diff.PriceChanges[0];
Check("change values", change.Cpu == "Kore i7 7700" && change.OldPrice == 707 && change.NewPrice == 459 && change.ChangePercent == -35.1, $"{change.ChangePercent}");
Check("segment moved", change.OldSegment == "High End" && change.NewSegment == "Mid Range", $"{change.OldSegment}->{change.NewSegment}");
Check("impact better MIPS/$", change.ImpactSummary?.Contains("now better MIPS/$ than C46") == true, change.ImpactSummary ?? "");
var note = diff.Notifications.First(n => n["type"]!.ToString() == "competitor_price_changed");
Check("synthesized label", note["source"]!.ToString() == "cli_synthesized" && note["native"]!.GetValue<bool>() == false);
Check("notification fields", note["oldPrice"]!.GetValue<double>() == 707 && note["newPrice"]!.GetValue<double>() == 459 && note["mips"]!.GetValue<double>() == 119660 && note["gameDate"]!.ToString() == "2017-04-21");
Check("own excluded", !diff.PriceChanges.Any(c => c.Cpu == "C46"));
var dominate = MarketSignals.Diff(before, Snap("2017-04-21", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "Inlet", "Kore i7 7700", 150000, 707), ("Desktop", "Inlet", "Kore i5 7400", 49430, 293)), own, "Me");
Check("no price change on spec change", dominate.PriceChanges.Count == 0);
var dom2 = MarketSignals.Diff(Snap("d1", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "R", "X", 150000, 700)), Snap("d2", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "R", "X", 150000, 580)), own, "Me");
Check("dominance hint", dom2.PriceChanges[0].ImpactSummary?.Contains("now dominates C46") == true, dom2.PriceChanges[0].ImpactSummary ?? "");
var lineupBefore = Snap("d1", ("Desktop", "Inlet", "A1", 1000, 100), ("Desktop", "Inlet", "A2", 2000, 200), ("Desktop", "Inlet", "A3", 3000, 300), ("Desktop", "Other", "B1", 1500, 150));
var lineupAfter = Snap("d2", ("Desktop", "Inlet", "A1", 1000, 90), ("Desktop", "Inlet", "A2", 2000, 180), ("Desktop", "Inlet", "A3", 3000, 270), ("Desktop", "Other", "B1", 1500, 150));
var lineup = MarketSignals.Diff(lineupBefore, lineupAfter, new HashSet<string>(), null);
var agg = lineup.Notifications.Where(n => n["type"]!.ToString() == "competitor_price_changed").ToList();
Check("aggregate lineup repricing", agg.Count == 1 && agg[0]["aggregate"]!.GetValue<bool>() && agg[0]["count"]!.GetValue<int>() == 3 && agg[0]["direction"]!.ToString() == "cut");
Check("aggregate keeps per-cpu changes", lineup.PriceChanges.Count == 3);
var multi = MarketSignals.Diff(
    new MarketSignals.Snapshot { Date = "d1", Markets = { ["Desktop"] = new() { ["Z"] = new("R", "Z", 500, 100, 2017) }, ["Mobile"] = new() { ["Z"] = new("R", "Z", 500, 100, 2017) } } },
    new MarketSignals.Snapshot { Date = "d2", Markets = { ["Desktop"] = new() { ["Z"] = new("R", "Z", 500, 80, 2017) }, ["Mobile"] = new() { ["Z"] = new("R", "Z", 500, 80, 2017) } } }, new HashSet<string>(), null);
Check("one event per cpu across markets", multi.PriceChanges.Count == 1 && multi.PriceChanges[0].Markets.Length == 2);
var small1 = MarketSignals.Diff(Snap("d1", ("Desktop", "R", "S", 100, 100)), Snap("d2", ("Desktop", "R", "S", 100, 99)), new HashSet<string>(), null, minChangePercent: 5);
Check("min percent threshold", small1.PriceChanges.Count == 0);
var tiny = MarketSignals.Diff(Snap("d1", ("Desktop", "R", "S", 100, 100)), Snap("d2", ("Desktop", "R", "S", 100, 100.4)), new HashSet<string>(), null);
Check("sub-dollar ignored", tiny.PriceChanges.Count == 0);
var lead = MarketSignals.Diff(Snap("d1", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "R", "N", 100000, 500)), Snap("d2", ("Desktop", "Me", "C46", 149310, 589), ("Desktop", "R", "N", 100000, 500), ("Desktop", "R", "New", 200000, 900)), own, "Me");
Check("rival took top MIPS", lead.Notifications.Any(n => n["type"]!.ToString() == "rival_took_lead" && n["metric"]!.ToString() == "top_mips" && n["tookFromPlayer"]!.GetValue<bool>()));
Check("new cpu listed", lead.Listed.Count == 1);
var segBefore = Snap("d1", ("Desktop", "R", "N", 100, 100)); var segAfter = Snap("d2", ("Desktop", "R", "N", 100, 100));
segAfter.Segments["Desktop"]["Mid Range"] = 280;
Check("segment price change", MarketSignals.Diff(segBefore, segAfter, new HashSet<string>(), null).Notifications.Any(n => n["type"]!.ToString() == "segment_price_changed" && n["newPrice"]!.GetValue<double>() == 280));
var spikeBefore = Snap("d1", ("Desktop", "R", "Old", 100, 100), ("Desktop", "R", "Keep", 200, 100)); spikeBefore.Unserved["Desktop"] = 2;
var spikeAfter = Snap("d2", ("Desktop", "R", "Keep", 200, 100)); spikeAfter.Unserved["Desktop"] = 12;
Check("unserved spike with delisting", MarketSignals.Diff(spikeBefore, spikeAfter, new HashSet<string>(), null).Notifications.Any(n => n["type"]!.ToString() == "unserved_demand_spike"));
var carried = Snap("d2"); carried.Markets.Clear(); carried.FillFrom(before);
Check("failed market read carries forward", carried.Markets.ContainsKey("Desktop"));
Check("snapshot json roundtrip", MarketSignals.Snapshot.FromJson(after.ToJson()) is { } rt && rt.Markets["Desktop"]["Kore i7 7700"].Price == 459 && rt.Segments["Desktop"]["Mid Range"] == 250);
// Debounce: a second change of the same CPU on the same day updates that day's notification.
var store = new JsonObject { ["events"] = new JsonArray(), ["seq"] = 0, ["cursors"] = new JsonObject() };
MarketSignals.Append(store, MarketSignals.Note("competitor_price_changed", "d2", "d1", "t", new JsonObject { ["company"] = "R", ["cpu"] = "S", ["oldPrice"] = 100, ["newPrice"] = 90 }));
MarketSignals.Append(store, MarketSignals.Note("competitor_price_changed", "d2", "d2", "t", new JsonObject { ["company"] = "R", ["cpu"] = "S", ["oldPrice"] = 90, ["newPrice"] = 80 }));
Check("debounce one per cpu per day", store["events"]!.AsArray().Count == 1 && CliReads.Num(store["events"]![0]!["newPrice"]) == 80 && CliReads.Num(store["events"]![0]!["changePercent"]) == -20);
Check("cursor take new", MarketSignals.TakeNew(store, "x").Count == 1 && MarketSignals.TakeNew(store, "x").Count == 0);

// ---------- 0.4.1: roll-up of small price steps (threshold 2% or $5 net since the last reported price) ----------
{
    var refs = new Dictionary<string, MarketSignals.PriceRef>();
    MarketSignals.DiffResult Step(double from, double to) => MarketSignals.Diff(Snap("d", ("Industries", "Hex", "13700", 5000, from)), Snap("d", ("Industries", "Hex", "13700", 5000, to)), new HashSet<string>(), null, MarketSignals.DefaultMinPercent, 3, refs, MarketSignals.DefaultMinDollars);
    var first = Step(252, 246);
    Check("rollup: 2.4% step reported", first.PriceChanges.Count == 1 && first.PriceChanges[0].OldPrice == 252 && first.PriceChanges[0].NewPrice == 246);
    var quiet = new[] { Step(246, 245), Step(245, 244), Step(244, 243) };
    Check("rollup: $1 steps suppressed", quiet.All(d => d.PriceChanges.Count == 0 && d.Notifications.Count == 0 && d.PendingMoves.Count == 1), string.Join(",", quiet.Select(d => d.PriceChanges.Count)));
    Check("rollup: pending shows net move", CliReads.Num(quiet[2].PendingMoves[0]["referencePrice"]) == 246 && CliReads.Num(quiet[2].PendingMoves[0]["steps"]) == 3);
    var rolled = Step(243, 240);
    Check("rollup: cumulative change reported once", rolled.PriceChanges.Count == 1 && rolled.PriceChanges[0].OldPrice == 246 && rolled.PriceChanges[0].NewPrice == 240 && rolled.PriceChanges[0].Steps == 4, rolled.PriceChanges.Count > 0 ? $"{rolled.PriceChanges[0].OldPrice}->{rolled.PriceChanges[0].NewPrice} x{rolled.PriceChanges[0].Steps}" : "none");
    var rolledNote = rolled.Notifications.Single();
    Check("rollup: notification says steps", rolledNote["rolledUp"]!.GetValue<bool>() && rolledNote["text"]!.ToString().Contains("4 steps") && CliReads.Num(rolledNote["oldPrice"]) == 246, rolledNote.ToJsonString());
    Check("rollup: reference moved on", refs.Values.Single().Price == 240 && refs.Values.Single().Steps == 0);
    Check("rollup: back-and-forth stays quiet", Step(240, 241).PriceChanges.Count == 0 && Step(241, 240).PriceChanges.Count == 0);
    // 0.4.2: both conditions must hold by default (>= 2% AND >= $5).
    Check("threshold: $1 on a cheap CPU is not enough", !MarketSignals.Significant(43, 42, 2, 5));
    Check("threshold: $5 on an expensive CPU is not enough", !MarketSignals.Significant(382, 377, 2, 5) && !MarketSignals.Significant(1000, 995, 2, 5));
    Check("threshold: both met", MarketSignals.Significant(382, 372, 2, 5) && MarketSignals.Significant(100, 93, 2, 5));
    Check("threshold: percent only when dollars disabled", MarketSignals.Significant(43, 42, 2, 0) && !MarketSignals.Significant(100, 99.5, 0, 0));
    var hex = new Dictionary<string, MarketSignals.PriceRef>();
    MarketSignals.DiffResult HexStep(double from, double to) => MarketSignals.Diff(Snap("d", ("Desktop", "Hex", "13800", 90000, from)), Snap("d", ("Desktop", "Hex", "13800", 90000, to)), new HashSet<string>(), null, MarketSignals.DefaultMinPercent, 3, hex, MarketSignals.DefaultMinDollars);
    var h1 = HexStep(382, 377); var h2 = HexStep(377, 372);
    Check("rollup: two -1.3% daily steps give one notice", h1.Notifications.Count == 0 && h2.PriceChanges.Count == 1 && h2.PriceChanges[0].OldPrice == 382 && h2.PriceChanges[0].NewPrice == 372 && h2.PriceChanges[0].Steps == 2 && h2.Notifications.Count == 1, h2.Notifications.Count > 0 ? h2.Notifications[0]["text"]!.ToString() : "none");
}

// ---------- 0.4.2: lineup notifications get a short merged summary per own CPU ----------
{
    var ownSet = new HashSet<string> { "C52", "C50" };
    var lineBefore = Snap("d1", ("Desktop", "Me", "C52", 100000, 140), ("Desktop", "Me", "C50", 120000, 150), ("Desktop", "Inlet", "U3", 231000, 300), ("Desktop", "Inlet", "U5", 250000, 320), ("Desktop", "Inlet", "U7", 300000, 400), ("Desktop", "Inlet", "U9", 90000, 200));
    var lineAfter = Snap("d2", ("Desktop", "Me", "C52", 100000, 140), ("Desktop", "Me", "C50", 120000, 150), ("Desktop", "Inlet", "U3", 231000, 140), ("Desktop", "Inlet", "U5", 250000, 145), ("Desktop", "Inlet", "U7", 300000, 390), ("Desktop", "Inlet", "U9", 90000, 100));
    var lineNote = MarketSignals.Diff(lineBefore, lineAfter, ownSet, "Me").Notifications.Single(n => n["type"]!.ToString() == "competitor_price_changed");
    var lineSummary = lineNote["impactSummary"]!.ToString();
    Check("aggregate summary merged per own CPU", lineNote["aggregate"]!.GetValue<bool>() && lineSummary.Contains("C50: dominated by U3, U5") && lineSummary.Contains("C52: dominated by U3;") && lineSummary.Contains("worse MIPS/$ than") && lineSummary.Contains("Inlet CPUs"), lineSummary);
    Check("aggregate summary deduplicated and short", System.Text.RegularExpressions.Regex.Matches(lineSummary, "C52:").Count == 1 && lineSummary.Length < 250, lineSummary);
    Check("aggregate impact per own CPU", lineNote["impact"]!.AsArray().Count == 2 && lineNote["impact"]![0]!["dominatedBy"] is JsonArray, lineNote["impact"]!.ToJsonString());
    Check("aggregate keeps per-cpu details", lineNote["cpus"]!.AsArray().Count == 4 && lineNote["cpus"]![0]!["impact"] is JsonArray);
}

// ---------- 0.4.2: generated text is culture-invariant ----------
{
    var saved = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
    try
    {
        var ownC = new HashSet<string> { "C52" };
        var ru = MarketSignals.Diff(Snap("d1", ("Desktop", "Me", "C52", 102760.5, 140), ("Desktop", "Inlet", "U3", 231420.5, 300)), Snap("d2", ("Desktop", "Me", "C52", 102760.5, 140), ("Desktop", "Inlet", "U3", 231420.5, 139.5)), ownC, "Me");
        var text = ru.Notifications.First(n => n["type"]!.ToString() == "competitor_price_changed")["text"]!.ToString() + " " + ru.PriceChanges.Single().Impact.ToJsonString();
        Check("invariant decimals under ru-RU", text.Contains("1658.93 vs 734") && !System.Text.RegularExpressions.Regex.IsMatch(text, @"\d,\d+ vs"), text);
        var rs = ResearchTools.Diff(new ResearchTools.Snapshot { Date = "2017-01-01", FundingPercent = 100, Active = new("7nm", 200.5, 1) }, new ResearchTools.Snapshot { Date = "2017-01-11", FundingPercent = 100, Active = new("7nm", 100.5, 1) });
        Check("invariant research text", rs.Count == 1 && !System.Text.RegularExpressions.Regex.IsMatch(rs[0]["text"]!.ToString(), "[0-9],[0-9]"), rs.Count > 0 ? rs[0]["text"]!.ToString() : "none");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
}

// ---------- 0.4.2: leader changes ----------
{
    var ownL = new HashSet<string> { "C46" };
    var sameBefore = Snap("d1", ("Desktop", "Me", "C46", 100000, 500), ("Desktop", "Inlet", "U5", 200000, 600));
    var sameAfter = Snap("d2", ("Desktop", "Me", "C46", 100000, 500), ("Desktop", "Inlet", "U5", 200000, 600), ("Desktop", "Inlet", "U7", 250000, 900));
    Check("same-company lead change quiet by default", !MarketSignals.Diff(sameBefore, sameAfter, ownL, "Me").Notifications.Any(n => n["type"]!.ToString() == "rival_took_lead"));
    Check("same-company lead change with option", MarketSignals.Diff(sameBefore, sameAfter, ownL, "Me", sameCompanyLeads: true).Notifications.Any(n => n["type"]!.ToString() == "rival_took_lead" && n["sameCompany"]!.GetValue<bool>()));
    var otherAfter = Snap("d2", ("Desktop", "Me", "C46", 100000, 500), ("Desktop", "Inlet", "U5", 200000, 600), ("Desktop", "AMG", "R9", 260000, 900));
    Check("company change reported", MarketSignals.Diff(sameBefore, otherAfter, ownL, "Me").Notifications.Any(n => n["type"]!.ToString() == "rival_took_lead" && n["metric"]!.ToString() == "top_mips" && !n["sameCompany"]!.GetValue<bool>()));
    var mineAfter = Snap("d2", ("Desktop", "Me", "C46", 100000, 500), ("Desktop", "Inlet", "U5", 200000, 600), ("Desktop", "Me", "C47", 300000, 700));
    Check("player took lead", MarketSignals.Diff(sameBefore, mineAfter, new HashSet<string> { "C46", "C47" }, "Me").Notifications.Any(n => n["type"]!.ToString() == "player_took_lead" && n["leader"]!["cpu"]!.ToString() == "C47"));
}

// ---------- 0.4.1: impact hints describe the current position (dominance, MIPS/$) ----------
{
    var ownSet = new HashSet<string> { "C52", "C50", "C54", "C53" };
    var beforeUltra = Snap("d1", ("Desktop", "Me", "C52", 102760, 140), ("Desktop", "Me", "C50", 125860, 150), ("Desktop", "Me", "C54", 180210, 190), ("Desktop", "Me", "C53", 215600, 220), ("Desktop", "Inlet", "Kore Ultra 3 115", 231420, 260));
    var afterUltra = Snap("d2", ("Desktop", "Me", "C52", 102760, 140), ("Desktop", "Me", "C50", 125860, 150), ("Desktop", "Me", "C54", 180210, 190), ("Desktop", "Me", "C53", 215600, 220), ("Desktop", "Inlet", "Kore Ultra 3 115", 231420, 140));
    var ultra = MarketSignals.Diff(beforeUltra, afterUltra, ownSet, "Me").PriceChanges.Single();
    var summary = ultra.ImpactSummary ?? "";
    Check("hint: dominates C52", summary.StartsWith("now dominates ") && summary.Contains("C52 (more MIPS, same price)") && summary.Contains("C50 (more MIPS, lower price)"), summary);
    Check("hint: better MIPS/$ list", summary.Contains("better MIPS/$ than") && new[] { "C52", "C50", "C54", "C53" }.All(c => summary.Contains(c)), summary);
    Check("hint: every item has a hint", ultra.Impact.OfType<JsonObject>().All(i => i["hint"] != null && i["rivalBetterValue"]!.GetValue<bool>()), ultra.Impact.ToJsonString());
    Check("segment known", ultra.OldSegment == "Mid Range" && ultra.NewSegment == "Low End", $"{ultra.OldSegment}->{ultra.NewSegment}");
    var noSeg = Snap("d1", ("Industries", "T", "TMX", 100, 50)); var noSeg2 = Snap("d2", ("Industries", "T", "TMX", 100, 40));
    var unknown = MarketSignals.ChangeJson(MarketSignals.Diff(noSeg, noSeg2, new HashSet<string>(), null).PriceChanges.Single());
    Check("segment unknown has reason", unknown["segment"] == null && unknown["segmentUnknownReason"] != null, unknown.ToJsonString());
    var industrial = new CliReads.Shares();
    CliReads.ParseShares(industrial, "All", new JsonObject { ["guidance"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["market"] = "Industrial", ["recommendations"] = new JsonObject { ["Recommended Price"] = "$45" } }) } });
    Check("industries segment name normalized", CliReads.Segment(industrial.SegmentPrices, "Industries", 40) == "Industrial");
}

// ---------- 0.4.1: frequency units for the cpu-preview sweep ----------
{
    var late = GameVariants.Frequencies("3.2,3.5", 3000);
    Check("GHz assumed late game", late.Values!.SequenceEqual(new[] { 3200d, 3500d }) && late.Note!.Contains("GHz"));
    var early = GameVariants.Frequencies("3,3.5", 3);
    Check("MHz early game", early.Values!.SequenceEqual(new[] { 3d, 3.5 }));
    var units = GameVariants.Frequencies("3.5GHz, 3500MHz, 800KHz", 3);
    Check("explicit units", units.Values!.SequenceEqual(new[] { 3500d, 3500d, 0.8 }));
    Check("MHz values unchanged late", GameVariants.Frequencies("3200,3500", 3000).Values!.SequenceEqual(new[] { 3200d, 3500d }));
    Check("bad frequency rejected", GameVariants.Frequencies("fast", 3000).Values == null);
}

// ---------- Research time drops (P1 #11) ----------
var r1 = new ResearchTools.Snapshot { Date = "2017-01-01", FundingPercent = 100, Active = new("7nm", 200, 1e6) }; r1.Available["L3 16MB"] = new("L3 16MB", 300, 1e6);
var r2 = new ResearchTools.Snapshot { Date = "2017-01-11", FundingPercent = 100, Active = new("7nm", 120, 1e6) }; r2.Available["L3 16MB"] = new("L3 16MB", 200, 1e6);
var rd = ResearchTools.Diff(r1, r2);
Check("active drop beyond elapsed", rd.Any(n => n["technology"]!.ToString() == "7nm" && n["dropDays"]!.GetValue<double>() == 70));
Check("available drop", rd.Any(n => n["technology"]!.ToString() == "L3 16MB"));
var r3 = new ResearchTools.Snapshot { Date = "2017-01-11", FundingPercent = 200, Active = new("7nm", 90, 2e6) };
Check("funding change suppresses", ResearchTools.Diff(r1, r3).Count == 0);
var r4 = new ResearchTools.Snapshot { Date = "2017-01-11", FundingPercent = 100, Active = new("7nm", 190, 1e6) };
Check("normal ticking is quiet", ResearchTools.Diff(r1, r4).Count == 0);
Near("days text", ResearchTools.Days("126 days"), 126);

// ---------- Price checks (P2 #14) and line targets (P2 #15) ----------
var ctx = new PriceTools.Context { Date = "2017-04-21" };
ctx.Rows["C46"] = new JsonObject { ["name"] = "C46", ["demandPerMonth"] = 20000.0, ["productionPerMonth"] = 15000.0, ["stockUnits"] = 0.0, ["outOfStock"] = true };
ctx.Rows["C45"] = new JsonObject { ["name"] = "C45", ["demandPerMonth"] = 40000.0, ["productionPerMonth"] = 40000.0, ["stockUnits"] = 5000.0, ["outOfStock"] = false };
ctx.Sales["C46"] = new CliReads.SalesRow("C46", 1e6, 15000, 5000, 0, 1e7, 5e6, 300, 589, 80);
ctx.Sales["C45"] = new CliReads.SalesRow("C45", 1e6, 40000, 0, 5000, 1e7, 5e6, 200, 349, 90);
ctx.Catalogs["Desktop"] = new List<CliReads.CatalogRow> { new("Desktop", "Me", "C46", 149310, 589, 2017, null), new("Desktop", "Me", "C45", 96920, 349, 2016, null) };
ctx.Segments["Desktop"] = new Dictionary<string, double> { ["High End"] = 500, ["Mid Range"] = 250, ["Low End"] = 0 };
var warns = PriceTools.Warnings(ctx, "C46", 280).OfType<JsonObject>().Select(w => w["kind"]!.ToString()).ToList();
Check("below unit cost", warns.Contains("below_unit_cost"), string.Join(",", warns));
Check("supply limited cut", warns.Contains("supply_limited"));
Check("segment crossed", warns.Contains("segment_threshold_crossed"));
Check("cannibalizes high-volume sibling", warns.Contains("cannibalizes_high_volume_sibling"));
var calm = PriceTools.Warnings(ctx, "C46", 600).OfType<JsonObject>().Select(w => w["kind"]!.ToString()).ToList();
Check("price rise without crossing is quiet", calm.Count == 0, string.Join(",", calm));
var loss = PriceTools.Warnings(ctx, "C46", 280).OfType<JsonObject>().First(w => w["kind"]!.ToString() == "below_unit_cost");
Check("monthly loss at current volume", loss["estimatedMonthlyLoss"]!.GetValue<double>() == 20 * 15000);
var dominated = PriceTools.Warnings(ctx, "C45", 700).OfType<JsonObject>().Select(w => w["kind"]!.ToString()).ToList();
Check("dominated by own product", dominated.Contains("dominated_by_own_product"), string.Join(",", dominated));
var row = new JsonObject { ["manualLines"] = 10, ["availableRange"] = new JsonObject { ["minLines"] = 0, ["maxLines"] = 25 } };
Check("lines max", PriceTools.TargetLines(row, "max", null, false).Lines == 25);
Check("lines add", PriceTools.TargetLines(row, null, 5, false).Lines == 15);
Check("lines add negative", PriceTools.TargetLines(row, null, -4, false).Lines == 6);
Check("lines over max refused", PriceTools.TargetLines(row, "40", null, false).Problem != null);
Check("lines over max clamped", PriceTools.TargetLines(row, "40", null, true) is { Lines: 25, Problem: null });

// ---------- Reads parsing ----------
var sales = CliReads.ParseSales(new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["cPU"] = "C46", ["allTime"] = "1.2M", ["sold"] = "15.3K", ["missedSales"] = "4.1K", ["stock"] = "0", ["income"] = "$9.01M", ["profit"] = "$4.5M", ["unitCost"] = "$300", ["price"] = "$589", ["popularity"] = "80" }) });
Check("sales row parsed", sales["C46"] is { SoldLastMonth: 15300, MissedLastMonth: 4100, UnitCost: 300, Price: 589, Popularity: 80, AllTime: 1_200_000 });
var segs = new Dictionary<string, Dictionary<string, double>> { ["Desktop"] = new() { ["High End"] = 234, ["Mid Range"] = 134, ["Low End"] = 0 }, ["Industries"] = new() { ["Industries"] = 70 } };
Check("segment high", CliReads.Segment(segs, "Desktop", 250) == "High End");
Check("segment mid", CliReads.Segment(segs, "Desktop", 200) == "Mid Range");
Check("segment single", CliReads.Segment(segs, "Industries", 10) == "Industries");
var shares = new CliReads.Shares();
CliReads.ParseShares(shares, "Desktop", new JsonObject { ["guidance"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["market"] = "High End", ["recommendations"] = new JsonObject { ["Recommended Price"] = "$1.2K" } }) } });
Check("guidance price with suffix", shares.SegmentPrices["Desktop"]["High End"] == 1200);

// ---------- End-to-end compositions against a mock bridge ----------
await Smoke.Run((name, ok, detail) => Check(name, ok, detail.Length > 600 ? detail[..600] + "..." : detail));
await QuitTests.Run((name, ok, detail) => Check(name, ok, detail.Length > 600 ? detail[..600] + "..." : detail));
await ExeSmoke.Run((name, ok, detail) => Check(name, ok, detail.Length > 600 ? detail[..600] + "..." : detail));

Console.WriteLine($"{passed} passed, {failures.Count} failed");
foreach (var failure in failures) Console.WriteLine("FAIL " + failure);
return failures.Count == 0 ? 0 : 1;
