using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProcessorTycoonShared;

// Production line totals from the native Production summary, the product rows and (when rendered) the exact
// "used/capacity" label of the desktop Production icon. Above 999 lines the summary is abbreviated ("1.15K"), so exact
// values come from the desktop label or from each row's slider range: a manual slider accepts 0..(manual + unassigned),
// hence unassigned = maxLines - manualLines exactly. Totals are checked against the display within its rounding.
internal static class LineAccounting
{
    internal readonly struct Row
    {
        public Row(string name, int requested, int effective, int? manual, int? max) { Name = name; Requested = requested; Effective = effective; Manual = manual; Max = max; }
        public string Name { get; }
        public int Requested { get; }
        public int Effective { get; }
        public int? Manual { get; }
        public int? Max { get; }
    }

    internal sealed class Result
    {
        public int? Capacity, Used, Unassigned, Pending, Contracts, Clients, OverAssigned, RowsRequested, RowsEffective;
        public double? CapacityDisplay, UsedDisplay, CapacityPrecision, UsedPrecision;
        public bool CapacityExact, UsedExact, UnassignedExact;
        public string Basis = "";
        public bool? Consistent;
        public List<string> Problems = new();
    }

    // Exact "used/capacity" from the desktop Production icon label (non-abbreviated), e.g. "1142/1150".
    internal static (int Used, int Capacity)? DesktopLabel(string? label)
    {
        var match = Regex.Match(Regex.Replace(label ?? "", "<[^>]+>", ""), @"^\s*(\d[\d,]*)\s*/\s*(\d[\d,]*)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value.Replace(",", "")), int.Parse(match.Groups[2].Value.Replace(",", "")));
    }

    internal static Result Compute(string? productionLines, string? linesUsed, string? usedByContracts, string? usedByClients, IReadOnlyList<Row> rows, (int Used, int Capacity)? desktop = null)
    {
        var r = new Result();
        var capacityToken = DisplayNumber.FieldToken(productionLines);
        var usedTokens = DisplayNumber.Tokens(SecondCell(linesUsed) ?? linesUsed).ToArray();
        r.CapacityDisplay = capacityToken?.Value; r.CapacityPrecision = capacityToken?.Precision;
        r.UsedDisplay = usedTokens.Length > 0 ? usedTokens[0].Value : null; r.UsedPrecision = usedTokens.Length > 0 ? usedTokens[0].Precision : null;
        // Over-assignment renders as "capacity (-N)".
        if (usedTokens.Length > 1 && usedTokens[1].Value < 0) r.OverAssigned = (int)Math.Round(-usedTokens[1].Value);
        r.Pending = DisplayNumber.Pending(productionLines) ?? (capacityToken != null ? 0 : null);
        r.Contracts = DisplayNumber.FieldInt(usedByContracts);
        r.Clients = DisplayNumber.FieldInt(usedByClients);
        r.RowsRequested = rows.Sum(row => row.Requested);
        r.RowsEffective = rows.Sum(row => row.Effective);

        var sliderFree = rows.Where(row => row.Manual != null && row.Max != null).Select(row => row.Max!.Value - row.Manual!.Value).Distinct().ToArray();
        if (sliderFree.Length == 1 && sliderFree[0] >= 0) { r.Unassigned = sliderFree[0]; r.UnassignedExact = true; }
        else if (sliderFree.Length > 1) r.Problems.Add("Product sliders disagree on the unassigned line count: " + string.Join(", ", sliderFree) + ".");

        var usedFromRows = (r.RowsRequested ?? 0) + (r.Clients ?? 0);
        if (desktop != null)
        {
            r.Capacity = desktop.Value.Capacity; r.CapacityExact = true;
            // The icon clamps used lines to capacity; rows keep an over-assignment visible.
            r.Used = r.OverAssigned > 0 ? usedFromRows : desktop.Value.Used; r.UsedExact = true;
            r.Basis = "desktop_icon_exact";
        }
        else if (capacityToken is DisplayNumber.Token cap && !cap.Abbreviated)
        {
            r.Capacity = (int)Math.Round(cap.Value); r.CapacityExact = true;
            r.Used = usedFromRows; r.UsedExact = true;
            r.Basis = "native_summary_exact";
        }
        else if (r.UnassignedExact && rows.Count > 0)
        {
            r.Used = usedFromRows; r.UsedExact = true;
            // With free lines the slider range fixes capacity exactly; at zero free lines capacity is only known to display precision.
            if (r.Unassigned > 0 || r.OverAssigned == null && capacityToken is DisplayNumber.Token c0 && Math.Abs(usedFromRows - c0.Value) <= c0.Precision) { r.Capacity = usedFromRows + r.Unassigned; r.CapacityExact = r.Unassigned > 0; }
            else if (capacityToken is DisplayNumber.Token c1) r.Capacity = (int)Math.Round(c1.Value);
            r.Basis = "product_sliders_exact";
        }
        else if (capacityToken is DisplayNumber.Token rounded)
        {
            r.Capacity = (int)Math.Round(rounded.Value);
            r.Used = r.UsedDisplay is double u ? (int)Math.Round(u) : null;
            r.Basis = "display_rounded";
        }
        if (r.Unassigned == null && r.Capacity != null && r.Used != null) { r.Unassigned = Math.Max(0, r.Capacity.Value - r.Used.Value); r.UnassignedExact = r.CapacityExact && r.UsedExact; }

        // Sanity check: product lines (+ foundry clients) must match the displayed "Lines used" within its rounding,
        // and capacity - used must match the unassigned lines the sliders offer.
        var checks = new List<bool>();
        if (r.UsedDisplay is double shownUsed && r.OverAssigned == null && rows.Count > 0)
        {
            var ok = Math.Abs(usedFromRows - shownUsed) <= (r.UsedPrecision ?? 0.5) + 0.5;
            checks.Add(ok);
            if (!ok) r.Problems.Add($"Product rows add up to {usedFromRows} lines (with clients) but the summary shows Lines used {shownUsed}.");
        }
        if (r.CapacityDisplay is double shownCapacity && r.Capacity is int capacity)
        {
            var ok = Math.Abs(capacity - shownCapacity) <= (r.CapacityPrecision ?? 0.5) + 0.5;
            checks.Add(ok);
            if (!ok) r.Problems.Add($"Derived capacity {capacity} does not match the displayed {shownCapacity}.");
        }
        r.Consistent = checks.Count == 0 ? null : checks.All(x => x);
        return r;
    }

    // "Lines used: | 1.15K (-12)" -> "1.15K (-12)"; the label cell is skipped.
    private static string? SecondCell(string? text) => text?.Split('|').Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0 && !c.EndsWith(":", StringComparison.Ordinal));
}
