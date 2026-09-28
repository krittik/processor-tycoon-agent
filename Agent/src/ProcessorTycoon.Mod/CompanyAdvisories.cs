using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// Throttled strategic warnings attached to responses. Computed from the visible finance panel at most once per game date;
// each condition is emitted when it first appears and then every RepeatDays game days while it persists. Facts only; no action is taken.
internal static class CompanyAdvisories
{
    private const int RepeatDays = 30;
    private static decimal? lastProjectCost;
    private static string? checkedDate;
    private static JArray current = new();
    private static readonly Dictionary<string, DateTime> lastEmitted = new();

    internal static void NoteProjectCost(decimal cost) { if (cost > 0) lastProjectCost = cost; }
    internal static void Reset() { lastProjectCost = null; checkedDate = null; current = new JArray(); lastEmitted.Clear(); }

    // Returns advisories due now, or null.
    internal static JArray? Due()
    {
        string date;
        try { date = (string)TimeAdvanceController.ReadClock()["date"]!; } catch { return null; }
        if (date == checkedDate) return null;
        checkedDate = date;
        var today = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        JObject finances;
        try { finances = BalanceSnapshot.Read(); } catch { return null; }
        if ((bool?)finances["available"] != true) return null;
        var active = Evaluate(finances);
        foreach (var gone in lastEmitted.Keys.Where(k => active.All(a => (string?)a["kind"] != k)).ToArray()) lastEmitted.Remove(gone);
        var due = new JArray();
        foreach (var advisory in active)
        {
            var kind = (string)advisory["kind"]!;
            if (lastEmitted.TryGetValue(kind, out var last) && (today - last).TotalDays < RepeatDays) continue;
            lastEmitted[kind] = today;
            due.Add(advisory);
        }
        return due.Count > 0 ? due : null;
    }

    private static List<JObject> Evaluate(JObject finances)
    {
        var result = new List<JObject>();
        var items = finances["items"]!.OfType<JObject>().ToDictionary(i => (string)i["key"]!, i => BalanceSnapshot.Money((string?)i["display"]) ?? 0m);
        // Available Credit excludes positive cash; the reserve is Available Credit + cash on hand.
        var credit = (BalanceSnapshot.Money((string?)finances["availableCredit"]) ?? 0m) + Math.Max(0m, BalanceSnapshot.Money((string?)finances["cashDisplay"]) ?? 0m);
        var outflows = new[] { "interest", "production", "research", "development", "construction" }.Sum(k => Math.Abs(items.TryGetValue(k, out var v) ? v : 0m));
        if ((bool?)finances["default"]?["active"] == true)
            result.Add(new JObject { ["kind"] = "in_default", ["daysLeft"] = finances["default"]!["daysLeft"]?.DeepClone(), ["message"] = "The company is in default. The countdown runs to zero and does not clear early; Available Credit must be zero or more when it ends or the company goes bankrupt." });
        var reasons = new List<string>();
        if (lastProjectCost is decimal project && credit < project) reasons.Add($"below the cost of your last previewed CPU project (${Short(project)})");
        if (outflows > 0 && credit < 3 * outflows) reasons.Add($"below three months of current outflows (${Short(3 * outflows)})");
        var trap = finances["debt"]?["debtWhereInterestEqualsLimitGrowth"]?.Value<decimal?>();
        var debt = finances["debt"]?["debt"]?.Value<decimal?>();
        if (trap > 0 && debt >= 0.7m * trap) reasons.Add($"with debt (${Short(debt!.Value)}) near the level where interest cancels credit-limit growth (${Short(trap!.Value)})");
        if (reasons.Count > 0)
            result.Add(new JObject { ["kind"] = "thin_reserve", ["availableCredit"] = credit, ["lastProjectCost"] = lastProjectCost, ["monthlyOutflows"] = outflows,
                ["message"] = $"Available Credit plus cash on hand (${Short(credit)}) is " + string.Join("; ", reasons) + ". Markets move in boom and bust cycles: rival generation releases, price cuts and economic events can collapse your products' demand within days. Without a reserve that can fund a successor CPU, or carry the company for several months without sales, the next downturn may be fatal. Consider building a reserve, reducing debt or deferring expansion." });
        if (items.TryGetValue("research", out var research) && research == 0m)
            result.Add(new JObject { ["kind"] = "research_idle", ["message"] = "No research is running (Research $0/m). Rivals keep researching; every idle month widens the technology gap, shortens your current products' competitive window and makes your next CPU weaker. This is a long-term survival risk. Development of a CPU does not block research." });
        return result;
    }

    private static string Short(decimal value) => Math.Abs(value) >= 1000000m ? (value / 1000000m).ToString("0.00", CultureInfo.InvariantCulture) + "M" : (value / 1000m).ToString("0", CultureInfo.InvariantCulture) + "K";
}
