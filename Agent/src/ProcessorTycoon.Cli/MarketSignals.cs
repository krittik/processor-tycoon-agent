using System.Globalization;
using System.Text.Json.Nodes;

// Mod-synthesized market notifications. The game shows popups for rival releases but not for price changes, leader
// changes or recommended-price moves; these are derived here by diffing player-visible Analysis tables between reads.
// Every item is labelled source "cli_synthesized", native false. Nothing is ever changed in the game.
internal static class MarketSignals
{
    internal const string StoreFile = "competitor-prices.json";
    private const int MaxEvents = 400;

    internal sealed record Entry(string Company, string Cpu, double? Mips, double? Price, int? Year);

    internal sealed class Snapshot
    {
        public string Date = "";
        // market -> cpu -> entry (only markets that were actually read)
        public Dictionary<string, Dictionary<string, Entry>> Markets = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, double>> Segments = new(StringComparer.Ordinal);
        public Dictionary<string, double> Unserved = new(StringComparer.Ordinal);

        internal static Snapshot From(string date, Dictionary<string, List<CliReads.CatalogRow>> catalogs, Dictionary<string, Dictionary<string, double>>? segments = null, Dictionary<string, double>? unserved = null)
        {
            var snap = new Snapshot { Date = date };
            foreach (var (market, rows) in catalogs)
            {
                var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var row in rows) map.TryAdd(row.Cpu, new Entry(row.Company, row.Cpu, row.Mips, row.Price, row.Year));
                snap.Markets[market] = map;
            }
            if (segments != null) foreach (var (k, v) in segments) snap.Segments[k] = new Dictionary<string, double>(v, StringComparer.Ordinal);
            if (unserved != null) foreach (var (k, v) in unserved) snap.Unserved[k] = v;
            return snap;
        }

        internal JsonObject ToJson()
        {
            var markets = new JsonObject();
            foreach (var (market, rows) in Markets)
            {
                var m = new JsonObject();
                foreach (var (cpu, e) in rows) m[cpu] = new JsonObject { ["company"] = e.Company, ["mips"] = e.Mips, ["price"] = e.Price, ["year"] = e.Year };
                markets[market] = m;
            }
            var segments = new JsonObject();
            foreach (var (market, map) in Segments) { var s = new JsonObject(); foreach (var (k, v) in map) s[k] = v; segments[market] = s; }
            var unserved = new JsonObject();
            foreach (var (k, v) in Unserved) unserved[k] = v;
            return new JsonObject { ["date"] = Date, ["markets"] = markets, ["segments"] = segments, ["unserved"] = unserved };
        }

        internal static Snapshot? FromJson(JsonNode? node)
        {
            if (node is not JsonObject o || o["date"] == null) return null;
            var snap = new Snapshot { Date = o["date"]!.ToString() };
            foreach (var (market, rows) in o["markets"]?.AsObject() ?? new JsonObject())
            {
                var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var (cpu, e) in rows?.AsObject() ?? new JsonObject()) map[cpu] = new Entry(e?["company"]?.ToString() ?? "", cpu, Num(e?["mips"]), Num(e?["price"]), (int?)Num(e?["year"]));
                snap.Markets[market] = map;
            }
            foreach (var (market, s) in o["segments"]?.AsObject() ?? new JsonObject())
            {
                var map = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (k, v) in s?.AsObject() ?? new JsonObject()) if (Num(v) is double d) map[k] = d;
                snap.Segments[market] = map;
            }
            foreach (var (k, v) in o["unserved"]?.AsObject() ?? new JsonObject()) if (Num(v) is double d) snap.Unserved[k] = d;
            return snap;
        }

        // Carries forward markets/segments/unserved that this read did not capture, so a failed read is not "everything delisted".
        internal void FillFrom(Snapshot? previous)
        {
            if (previous == null) return;
            foreach (var (k, v) in previous.Markets) Markets.TryAdd(k, v);
            foreach (var (k, v) in previous.Segments) Segments.TryAdd(k, v);
            foreach (var (k, v) in previous.Unserved) Unserved.TryAdd(k, v);
        }
    }

    // One rival price change before notification shaping (guards evaluate these).
    internal sealed record PriceChange(string Company, string Cpu, string[] Markets, double OldPrice, double NewPrice, double ChangePercent, double? Mips, string? OldSegment, string? NewSegment, JsonArray Impact, string? ImpactSummary)
    {
        // Number of observed moves rolled into this change and the date of the reference (last reported) price.
        public int Steps { get; init; } = 1;
        public string? ReferenceDate { get; init; }
        public string? SegmentUnknownReason { get; init; }
    }

    // Last reported price per rival CPU ("company\u001fcpu"). Small moves accumulate against it until the net change
    // crosses the threshold; then one notification reports reference price -> latest price and the reference moves on.
    internal sealed class PriceRef { public double Price; public string Date = ""; public int Steps; }

    // Default notification threshold: a net move of at least 2% AND at least $5 since the last reported price (both must hold,
    // so $5 steps on an expensive CPU and 2% steps on a cheap one roll up). A threshold of 0 disables that condition.
    internal const double DefaultMinPercent = 2, DefaultMinDollars = 5;

    internal static bool Significant(double from, double to, double minPercent, double minDollars)
    {
        if (Math.Abs(to - from) < 1) return false;
        return (minPercent <= 0 || Math.Abs(to / from - 1) * 100 >= minPercent - 1e-9) && (minDollars <= 0 || Math.Abs(to - from) >= minDollars - 1e-9);
    }

    internal sealed class DiffResult
    {
        public List<PriceChange> PriceChanges = new();
        // Moves below the threshold, kept pending against their reference price (not notified).
        public List<JsonObject> PendingMoves = new();
        public List<JsonObject> Notifications = new();
        public List<JsonObject> Listed = new();
        public List<JsonObject> Delisted = new();
    }

    // Pure diff of two snapshots. `own` are the player's CPU names; `player` the player's company name (if known).
    internal static DiffResult Diff(Snapshot previous, Snapshot current, ISet<string> own, string? player, double minChangePercent = 0, int aggregateThreshold = 3, Dictionary<string, PriceRef>? refs = null, double minDollars = 0, bool sameCompanyLeads = false)
    {
        refs ??= new Dictionary<string, PriceRef>(StringComparer.Ordinal);
        var result = new DiffResult();
        var changes = new Dictionary<string, (string Company, string Cpu, List<string> Markets, double Old, double New, double? Mips)>(StringComparer.Ordinal);
        foreach (var (market, now) in current.Markets)
        {
            if (!previous.Markets.TryGetValue(market, out var then)) continue;
            foreach (var (cpu, e) in now)
            {
                if (own.Contains(cpu) || player != null && e.Company == player) continue;
                if (!then.TryGetValue(cpu, out var old)) { result.Listed.Add(new JsonObject { ["market"] = market, ["cpu"] = cpu, ["company"] = e.Company, ["mips"] = e.Mips, ["price"] = e.Price }); continue; }
                if (old.Price is not double from || e.Price is not double to || from <= 0 || to <= 0 || Math.Abs(to - from) < 0.5) continue;
                var key = e.Company + "\u001f" + cpu;
                if (changes.TryGetValue(key, out var existing)) existing.Markets.Add(market);
                else changes[key] = (e.Company, cpu, new List<string> { market }, from, to, e.Mips);
            }
            foreach (var (cpu, old) in then)
                if (!now.ContainsKey(cpu) && !own.Contains(cpu) && (player == null || old.Company != player)) result.Delisted.Add(new JsonObject { ["market"] = market, ["cpu"] = cpu, ["company"] = old.Company, ["mips"] = old.Mips, ["price"] = old.Price });
        }

        foreach (var (key, c) in changes)
        {
            // Net change since the last reported price of this CPU (small same-direction steps roll up).
            var reference = refs.TryGetValue(key, out var known) ? known : new PriceRef { Price = c.Old, Date = previous.Date };
            var steps = reference.Steps + 1;
            if (!Significant(reference.Price, c.New, minChangePercent, minDollars))
            {
                refs[key] = new PriceRef { Price = reference.Price, Date = reference.Date, Steps = steps };
                result.PendingMoves.Add(new JsonObject { ["company"] = c.Company, ["cpu"] = c.Cpu, ["referencePrice"] = reference.Price, ["referenceDate"] = reference.Date, ["latestPrice"] = c.New, ["netChangePercent"] = Math.Round((c.New / reference.Price - 1) * 100, 1), ["steps"] = steps });
                continue;
            }
            refs[key] = new PriceRef { Price = c.New, Date = current.Date, Steps = 0 };
            var percent = Math.Round((c.New / reference.Price - 1) * 100, 1);
            var market = c.Markets[0];
            var (impact, summary) = Impact(current, market, c.Mips, reference.Price, c.New, own, player);
            var segmentReason = current.Segments.TryGetValue(market, out var seg) && seg.Count > 0 ? null : $"No native recommended segment prices for {market} are stored yet (situation, market-changes and watch-advance read them from market-share).";
            result.PriceChanges.Add(new PriceChange(c.Company, c.Cpu, c.Markets.ToArray(), reference.Price, c.New, percent, c.Mips, CliReads.Segment(current.Segments, market, reference.Price), CliReads.Segment(current.Segments, market, c.New), impact, summary) { Steps = steps, ReferenceDate = reference.Date, SegmentUnknownReason = segmentReason });
        }

        // Lineup-wide repricing: one aggregated notification per company when it changed several CPUs in one step.
        foreach (var group in result.PriceChanges.GroupBy(p => p.Company))
        {
            var items = group.ToList();
            if (items.Count >= aggregateThreshold)
            {
                var cuts = items.Count(p => p.NewPrice < p.OldPrice);
                var direction = cuts == items.Count ? "cut" : cuts == 0 ? "raise" : "mixed";
                var median = items.Select(p => p.ChangePercent).OrderBy(x => x).ElementAt(items.Count / 2);
                var merged = AggregateImpact(group.Key, items);
                result.Notifications.Add(Note("competitor_price_changed", current.Date, previous.Date, $"{group.Key} repriced {items.Count} CPUs ({direction}, median {median.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)}%)", new JsonObject
                {
                    ["aggregate"] = true, ["company"] = group.Key, ["count"] = items.Count, ["direction"] = direction, ["medianChangePercent"] = median,
                    ["cpus"] = new JsonArray(items.Select(p => (JsonNode?)ChangeJson(p)).ToArray()),
                    ["impact"] = merged.Impact, ["impactSummary"] = merged.Summary
                }));
            }
            else foreach (var p in items) result.Notifications.Add(Note("competitor_price_changed", current.Date, p.ReferenceDate ?? previous.Date, FormattableString.Invariant($"{p.Company} {(p.NewPrice < p.OldPrice ? "cut" : "raised")} {p.Cpu} from ${p.OldPrice:0} to ${p.NewPrice:0} ({p.ChangePercent.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)}%") + (p.Steps > 1 ? FormattableString.Invariant($", {p.Steps} steps since {p.ReferenceDate}") : "") + FormattableString.Invariant($") in {string.Join("/", p.Markets)}") + (p.ImpactSummary != null ? ": " + p.ImpactSummary : ""), ChangeJson(p)));
        }

        Leaders(previous, current, own, player, result.Notifications, sameCompanyLeads);
        SegmentPriceChanges(previous, current, result.Notifications);
        foreach (var (market, now) in current.Unserved)
        {
            if (!previous.Unserved.TryGetValue(market, out var then) || now - then < 5) continue;
            var delisted = result.Delisted.Where(d => string.Equals(d["market"]?.ToString(), market, StringComparison.OrdinalIgnoreCase)).Select(d => d!.DeepClone()).ToArray();
            if (delisted.Length == 0) continue;
            result.Notifications.Add(Note("unserved_demand_spike", current.Date, previous.Date, FormattableString.Invariant($"{market} unserved demand rose from {then:0.#}% to {now:0.#}% while {delisted.Length} rival CPU(s) left the table"), new JsonObject { ["market"] = market, ["fromPercent"] = then, ["toPercent"] = now, ["likelyCause"] = "rival_cpus_no_longer_listed", ["noLongerListed"] = new JsonArray(delisted), ["note"] = "Rival CPUs that disappear from the table were retired or fell out of its top 50. Their buyers may be unserved until someone supplies them." }));
        }
        return result;
    }

    // Lineup repricing: one short, deduplicated line per affected own CPU ("C52: dominated by A, B; worse MIPS/$ than 4 Inlet
    // CPUs"). Per-CPU details stay in cpus[].impact.
    internal static (JsonArray Impact, string Summary) AggregateImpact(string company, List<PriceChange> items)
    {
        var perOwn = new Dictionary<string, (List<string> DominatedBy, List<string> BetterValue)>(StringComparer.Ordinal);
        foreach (var change in items)
            foreach (var item in change.Impact.OfType<JsonObject>())
            {
                var ownCpu = item["ownCpu"]?.ToString();
                if (ownCpu == null) continue;
                if (!perOwn.TryGetValue(ownCpu, out var entry)) perOwn[ownCpu] = entry = (new List<string>(), new List<string>());
                if (item["rivalDominates"]?.GetValue<bool>() == true && !entry.DominatedBy.Contains(change.Cpu)) entry.DominatedBy.Add(change.Cpu);
                if (item["rivalBetterValue"]?.GetValue<bool>() == true && !entry.BetterValue.Contains(change.Cpu)) entry.BetterValue.Add(change.Cpu);
            }
        static string Names(List<string> names) => names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + FormattableString.Invariant($" +{names.Count - 3} more");
        var impact = new JsonArray();
        var lines = new List<string>();
        foreach (var (ownCpu, entry) in perOwn.Where(p => p.Value.DominatedBy.Count > 0 || p.Value.BetterValue.Count > 0).OrderByDescending(p => p.Value.DominatedBy.Count).ThenByDescending(p => p.Value.BetterValue.Count))
        {
            impact.Add(new JsonObject { ["ownCpu"] = ownCpu, ["dominatedBy"] = new JsonArray(entry.DominatedBy.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()), ["worseMipsPerDollarThan"] = new JsonArray(entry.BetterValue.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) });
            var parts = new List<string>();
            if (entry.DominatedBy.Count > 0) parts.Add("dominated by " + Names(entry.DominatedBy));
            if (entry.BetterValue.Count > 0) parts.Add(FormattableString.Invariant($"worse MIPS/$ than {entry.BetterValue.Count} {company} CPU{(entry.BetterValue.Count == 1 ? "" : "s")}"));
            lines.Add(ownCpu + ": " + string.Join("; ", parts));
        }
        var summary = lines.Count == 0 ? "no change to your CPUs' dominance or MIPS/$ position" : string.Join(" | ", lines.Take(5)) + (lines.Count > 5 ? FormattableString.Invariant($" | +{lines.Count - 5} more of your CPUs") : "");
        return (impact, summary);
    }

    internal static JsonObject ChangeJson(PriceChange p) => new()
    {
        ["company"] = p.Company, ["cpu"] = p.Cpu, ["market"] = p.Markets[0], ["markets"] = new JsonArray(p.Markets.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        ["oldPrice"] = p.OldPrice, ["newPrice"] = p.NewPrice, ["changePercent"] = p.ChangePercent, ["mips"] = p.Mips,
        ["mipsPerDollarOld"] = p.Mips is double m1 ? Math.Round(m1 / p.OldPrice, 4) : null, ["mipsPerDollarNew"] = p.Mips is double m2 ? Math.Round(m2 / p.NewPrice, 4) : null,
        ["previousSegment"] = p.OldSegment, ["segment"] = p.NewSegment, ["segmentUnknownReason"] = p.OldSegment == null && p.NewSegment == null ? p.SegmentUnknownReason : null,
        ["steps"] = p.Steps, ["rolledUp"] = p.Steps > 1, ["referenceDate"] = p.ReferenceDate,
        ["impact"] = p.Impact.DeepClone(), ["impactSummary"] = p.ImpactSummary
    };

    // How a rival price move compares with the player's CPUs in the same market (nearest by MIPS, dominance, MIPS per dollar).
    internal static (JsonArray Impact, string? Summary) Impact(Snapshot current, string market, double? rivalMips, double oldPrice, double newPrice, ISet<string> own, string? player)
    {
        var impact = new JsonArray();
        if (rivalMips is not double r || r <= 0 || !current.Markets.TryGetValue(market, out var rows)) return (impact, null);
        var mine = rows.Values.Where(e => (own.Contains(e.Cpu) || player != null && e.Company == player) && e.Mips > 0 && e.Price > 0).ToList();
        if (mine.Count == 0) return (impact, null);
        var vNew = r / newPrice; var vOld = r / oldPrice;
        var dominates = new List<string>(); var nowDominates = new List<string>(); var lostDominance = new List<string>();
        var better = new List<string>(); var nowBetter = new List<string>(); var lostBetter = new List<string>();
        foreach (var o in mine.OrderBy(o => Math.Abs(Math.Log(o.Mips!.Value / r))))
        {
            var vo = o.Mips!.Value / o.Price!.Value;
            // Dominance: at least the MIPS for no more money, strictly better in one of them.
            bool Dom(double price) => r >= o.Mips && price <= o.Price && (r > o.Mips || price < o.Price);
            var domNew = Dom(newPrice); var domOld = Dom(oldPrice);
            var hints = new List<string>();
            var detail = $"{o.Cpu} ({(r > o.Mips ? "more" : "same")} MIPS, {(newPrice < o.Price ? "lower" : "same")} price)";
            if (domNew) { hints.Add((domOld ? "dominates " : "now dominates ") + detail); (domOld ? dominates : nowDominates).Add(detail); }
            else if (domOld) { hints.Add($"no longer dominates {o.Cpu}"); lostDominance.Add(o.Cpu); }
            if (vNew > vo) { hints.Add(FormattableString.Invariant($"{(vOld > vo ? "" : "now ")}better MIPS/$ than {o.Cpu} ({vNew:0.##} vs {vo:0.##})")); (vOld > vo ? better : nowBetter).Add(o.Cpu); }
            else if (vOld > vo) { hints.Add($"no longer better MIPS/$ than {o.Cpu}"); lostBetter.Add(o.Cpu); }
            var near = o.Mips!.Value / r is >= 0.5 and <= 2;
            if (hints.Count == 0 && !near) continue;
            impact.Add(new JsonObject { ["ownCpu"] = o.Cpu, ["ownMips"] = o.Mips, ["ownPrice"] = o.Price, ["ownMipsPerDollar"] = Math.Round(vo, 4), ["rivalMipsPerDollar"] = Math.Round(vNew, 4), ["rivalDominates"] = domNew, ["rivalBetterValue"] = vNew > vo, ["hint"] = hints.Count > 0 ? string.Join("; ", hints) : null });
        }
        var parts = new List<string>();
        if (nowDominates.Count > 0) parts.Add("now dominates " + string.Join(", ", nowDominates));
        if (dominates.Count > 0) parts.Add("dominates " + string.Join(", ", dominates));
        if (nowBetter.Count > 0) parts.Add("now better MIPS/$ than " + string.Join(", ", nowBetter));
        if (better.Count > 0) parts.Add("better MIPS/$ than " + string.Join(", ", better));
        if (lostDominance.Count > 0) parts.Add("no longer dominates " + string.Join(", ", lostDominance));
        if (lostBetter.Count > 0) parts.Add("no longer better MIPS/$ than " + string.Join(", ", lostBetter));
        return (impact, parts.Count > 0 ? string.Join("; ", parts) : impact.Count > 0 ? $"near {impact[0]!["ownCpu"]} in MIPS but neither dominates nor beats its MIPS/$" : null);
    }

    // Leadership changes. By default only when the leading company changes or your CPU is involved (a rival takes the lead
    // from you: rival_took_lead with tookFromPlayer; you take it from a rival: player_took_lead); `sameCompanyLeads` also
    // reports a rival replacing its own leader.
    private static void Leaders(Snapshot previous, Snapshot current, ISet<string> own, string? player, List<JsonObject> notes, bool sameCompanyLeads = false)
    {
        bool Mine(Entry e) => own.Contains(e.Cpu) || player != null && e.Company == player;
        foreach (var (market, now) in current.Markets)
        {
            if (!previous.Markets.TryGetValue(market, out var then)) continue;
            var groups = new List<(string Scope, Func<Entry, bool> In)> { ("market", _ => true) };
            if (current.Segments.TryGetValue(market, out var seg) && seg.Count > 1)
                foreach (var name in seg.Keys) groups.Add((name, e => CliReads.Segment(current.Segments, market, e.Price) == name));
            foreach (var (scope, member) in groups)
            {
                foreach (var metric in new[] { "top_mips", "best_mips_per_dollar" })
                {
                    Entry? Best(IEnumerable<Entry> rows) => metric == "top_mips" ? rows.Where(e => e.Mips > 0).OrderByDescending(e => e.Mips).FirstOrDefault() : rows.Where(e => e.Mips > 0 && e.Price > 0).OrderByDescending(e => e.Mips / e.Price).FirstOrDefault();
                    // The previous leader is judged with the current segment boundaries, so a recommended-price move alone is not a takeover.
                    var before = Best(then.Values.Where(member)); var after = Best(now.Values.Where(member));
                    if (after == null || before == null || after.Cpu == before.Cpu) continue;
                    if (Mine(after))
                    {
                        if (Mine(before)) continue;
                        var where0 = scope == "market" ? market : $"{market} {scope}";
                        notes.Add(Note("player_took_lead", current.Date, previous.Date, $"Your {after.Cpu} took {(metric == "top_mips" ? "the top MIPS" : "the best MIPS/$")} position in {where0} from {before.Company} {before.Cpu}", new JsonObject
                        {
                            ["market"] = market, ["segment"] = scope == "market" ? null : scope, ["metric"] = metric,
                            ["leader"] = new JsonObject { ["company"] = after.Company, ["cpu"] = after.Cpu, ["mips"] = after.Mips, ["price"] = after.Price },
                            ["previousLeader"] = new JsonObject { ["company"] = before.Company, ["cpu"] = before.Cpu, ["mips"] = before.Mips, ["price"] = before.Price }
                        }));
                        continue;
                    }
                    if (!sameCompanyLeads && !Mine(before) && after.Company == before.Company) continue;
                    // Segment-level leads matter only where the player sells (or just lost the lead).
                    if (scope != "market" && !Mine(before) && !now.Values.Where(member).Any(Mine)) continue;
                    var value = metric == "top_mips" ? after.Mips : Math.Round(after.Mips!.Value / after.Price!.Value, 4);
                    var where = scope == "market" ? market : $"{market} {scope}";
                    notes.Add(Note("rival_took_lead", current.Date, previous.Date, $"{after.Company} {after.Cpu} took {(metric == "top_mips" ? "the top MIPS" : "the best MIPS/$")} position in {where}" + (Mine(before) ? $" from your {before.Cpu}" : $" from {before.Company} {before.Cpu}"), new JsonObject
                    {
                        ["market"] = market, ["segment"] = scope == "market" ? null : scope, ["metric"] = metric, ["tookFromPlayer"] = Mine(before), ["sameCompany"] = after.Company == before.Company,
                        ["leader"] = new JsonObject { ["company"] = after.Company, ["cpu"] = after.Cpu, ["mips"] = after.Mips, ["price"] = after.Price, ["value"] = value },
                        ["previousLeader"] = new JsonObject { ["company"] = before.Company, ["cpu"] = before.Cpu, ["mips"] = before.Mips, ["price"] = before.Price }
                    }));
                }
            }
        }
    }

    private static void SegmentPriceChanges(Snapshot previous, Snapshot current, List<JsonObject> notes)
    {
        foreach (var (market, map) in current.Segments)
        {
            if (!previous.Segments.TryGetValue(market, out var old)) continue;
            foreach (var (segment, price) in map)
                if (old.TryGetValue(segment, out var before) && Math.Abs(price - before) >= 1)
                    notes.Add(Note("segment_price_changed", current.Date, previous.Date, FormattableString.Invariant($"{market} {segment} recommended price moved from ${before:0} to ${price:0}"), new JsonObject { ["market"] = market, ["segment"] = segment, ["oldPrice"] = before, ["newPrice"] = price, ["changePercent"] = before > 0 ? Math.Round((price / before - 1) * 100, 1) : null, ["note"] = "Native recommended price (market-share tooltip). Products priced near a segment boundary can switch segment when it moves." }));
        }
    }

    internal static JsonObject Note(string type, string date, string? since, string text, JsonObject detail)
    {
        var note = new JsonObject { ["type"] = type, ["source"] = "cli_synthesized", ["native"] = false, ["gameDate"] = date, ["sinceDate"] = since, ["text"] = text };
        foreach (var (k, v) in detail) note[k] = v?.DeepClone();
        return note;
    }

    // ---- Persistent store: latest snapshot, synthesized event log (with ids), per-consumer cursors, rival popularity. ----

    internal static JsonObject Load(string? today)
    {
        var store = CliReads.LoadJson(StoreFile) ?? new JsonObject();
        // Continue from the pre-0.4 market-changes snapshot (Desktop/Industries only) instead of starting a new baseline.
        if (store["snapshot"] == null && CliReads.LoadJson("market-snapshot.json") is JsonObject legacy && legacy["date"] != null)
            store["snapshot"] = new JsonObject { ["date"] = legacy["date"]!.DeepClone(), ["markets"] = legacy["markets"]?.DeepClone() ?? new JsonObject(), ["segments"] = new JsonObject(), ["unserved"] = new JsonObject() };
        var then = CliReads.Date(store["snapshot"]?["date"]?.ToString());
        var now = CliReads.Date(today);
        // Another campaign or an earlier save: the old snapshot and events do not belong to this timeline.
        if (then != null && now != null && then > now) store = new JsonObject();
        store["events"] ??= new JsonArray();
        store["cursors"] ??= new JsonObject();
        store["popularity"] ??= new JsonObject();
        store["seq"] ??= 0;
        return store;
    }

    internal static void Save(JsonObject store) => CliReads.SaveJson(StoreFile, store);

    // Diffs a new snapshot against the stored one, appends its notifications (debounced: one per CPU per game day) and
    // stores the snapshot. Returns the diff (null on the first, baseline observation).
    internal static DiffResult? Observe(JsonObject store, Snapshot current, ISet<string> own, string? player, double minPercent = DefaultMinPercent, double minDollars = DefaultMinDollars, bool sameCompanyLeads = false)
    {
        var previous = Snapshot.FromJson(store["snapshot"]);
        current.FillFrom(previous);
        store["snapshot"] = current.ToJson();
        if (player != null) store["player"] = player;
        if (previous == null || previous.Date == current.Date && previous.Markets.Count == 0) return null;
        var refs = new Dictionary<string, PriceRef>(StringComparer.Ordinal);
        foreach (var (key, value) in store["priceRefs"] as JsonObject ?? new JsonObject())
            if (CliReads.Num(value?["price"]) is double price) refs[key] = new PriceRef { Price = price, Date = value?["date"]?.ToString() ?? "", Steps = (int)(CliReads.Num(value?["steps"]) ?? 0) };
        var diff = Diff(previous, current, own, player ?? store["player"]?.ToString(), minPercent, 3, refs, minDollars, sameCompanyLeads);
        var saved = new JsonObject();
        foreach (var (key, value) in refs) saved[key] = new JsonObject { ["price"] = value.Price, ["date"] = value.Date, ["steps"] = value.Steps };
        store["priceRefs"] = saved;
        store["priceThreshold"] = new JsonObject { ["minPercent"] = minPercent, ["minDollars"] = minDollars };
        foreach (var note in diff.Notifications) Append(store, note);
        return diff;
    }

    internal static void Append(JsonObject store, JsonObject note)
    {
        var events = store["events"]!.AsArray();
        // Debounce: a later change of the same CPU on the same game day updates that day's notification.
        if (note["type"]?.ToString() == "competitor_price_changed" && note["aggregate"] == null)
        {
            var same = events.OfType<JsonObject>().LastOrDefault(e => e["type"]?.ToString() == "competitor_price_changed" && e["aggregate"] == null && e["gameDate"]?.ToString() == note["gameDate"]?.ToString() && e["cpu"]?.ToString() == note["cpu"]?.ToString() && e["company"]?.ToString() == note["company"]?.ToString());
            if (same != null)
            {
                var from = CliReads.Num(same["oldPrice"]) ?? 0; var to = CliReads.Num(note["newPrice"]) ?? 0;
                foreach (var key in new[] { "newPrice", "mipsPerDollarNew", "segment", "impact", "impactSummary" }) same[key] = note[key]?.DeepClone();
                same["changePercent"] = from > 0 ? Math.Round((to / from - 1) * 100, 1) : null;
                same["text"] = FormattableString.Invariant($"{same["company"]} changed {same["cpu"]} from ${from:0} to ${to:0} ({CliReads.Num(same["changePercent"])?.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)}%) (several changes this day)");
                // A new sequence number makes consumers that already reported this event (market-changes, situation) see the update.
                var bumped = (store["seq"]?.GetValue<int>() ?? 0) + 1;
                store["seq"] = bumped;
                same["seq"] = bumped;
                events.Remove(same);
                events.Add(same);
                return;
            }
        }
        var seq = (store["seq"]?.GetValue<int>() ?? 0) + 1;
        store["seq"] = seq;
        var item = note.DeepClone().AsObject();
        item["id"] = "syn-" + seq;
        item["seq"] = seq;
        events.Add(item);
        while (events.Count > MaxEvents) events.RemoveAt(0);
    }

    // Events not yet reported to this consumer (market-changes, situation, ...); advances its cursor.
    internal static JsonArray TakeNew(JsonObject store, string consumer)
    {
        var cursor = store["cursors"]?[consumer]?.GetValue<int>() ?? 0;
        var items = store["events"]!.AsArray().OfType<JsonObject>().Where(e => (e["seq"]?.GetValue<int>() ?? 0) > cursor).Select(e => (JsonNode?)e.DeepClone()).ToArray();
        store["cursors"]![consumer] = store["seq"]?.GetValue<int>() ?? 0;
        return new JsonArray(items);
    }

    internal static JsonArray Recent(JsonObject store, string? today, int days, params string[] types)
    {
        var now = CliReads.Date(today);
        return new JsonArray(store["events"]!.AsArray().OfType<JsonObject>().Where(e => (types.Length == 0 || types.Contains(e["type"]?.ToString())) && (now == null || CliReads.Date(e["gameDate"]?.ToString()) is DateTime d && (now.Value - d).TotalDays <= days)).Select(e => (JsonNode?)e.DeepClone()).ToArray());
    }

    // Popularity of rival CPUs first seen after tracking started (market view "Popularity" = native highest popularity x100).
    internal static JsonArray TrackPopularity(JsonObject store, string date, Dictionary<string, List<CliReads.CatalogRow>> marketViews, ISet<string> own, string? player)
    {
        var pop = store["popularity"]!.AsObject();
        var baseline = store["popularityBaseline"] == null;
        var known = store["knownRivals"] as JsonObject ?? new JsonObject();
        foreach (var (market, rows) in marketViews)
            foreach (var row in rows.Where(r => !own.Contains(r.Cpu) && (player == null || r.Company != player)))
            {
                var key = row.Company + " | " + row.Cpu;
                if (baseline) { known[key] = true; continue; }
                if (known.ContainsKey(key) && pop[key] == null) continue;
                known[key] = true;
                var entry = pop[key] as JsonObject ?? new JsonObject { ["company"] = row.Company, ["cpu"] = row.Cpu, ["market"] = market, ["firstSeen"] = date, ["points"] = new JsonArray() };
                pop[key] = entry;
                var points = entry["points"]!.AsArray();
                if (row.Popularity is double p && points.OfType<JsonObject>().LastOrDefault()?["date"]?.ToString() != date) points.Add(new JsonObject { ["date"] = date, ["popularity"] = p });
                while (points.Count > 24) points.RemoveAt(0);
            }
        store["knownRivals"] = known;
        store["popularityBaseline"] ??= date;
        return new JsonArray(pop.Select(p => p.Value?.DeepClone()).Where(p => CliReads.Date(p?["firstSeen"]?.ToString()) is DateTime d && CliReads.Date(date) is DateTime t && (t - d).TotalDays <= 365).ToArray());
    }

    private static double? Num(JsonNode? node) => CliReads.Num(node);
}
