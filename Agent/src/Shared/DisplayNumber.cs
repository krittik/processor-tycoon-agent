using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProcessorTycoonShared;

// Parses numbers exactly as the game renders them (en-US): "1,234", "0.09", "12.34", "1.15K", "149.31K", "$1.95K",
// "-$22.14M", "2.05B", "1.15K/m", "85.00%", "Production lines: | 1.15K (+50)". Abbreviated values (K/M/B/T) always
// show two decimals, so "1.15K" means 1,145..1,155: Precision reports that half-step. Shared by the plugin and the CLI
// (linked source), so every display number is parsed the same way. Values are computed in decimal to avoid float noise.
internal static class DisplayNumber
{
    internal readonly struct Token
    {
        public Token(double value, double precision, bool money, bool abbreviated, string text) { Value = value; Precision = precision; Money = money; Abbreviated = abbreviated; Text = text; }
        public double Value { get; }
        // Half of the last displayed digit's unit (5 for "1.15K", 0.5 for "1,234", 0.005 for "0.09").
        public double Precision { get; }
        public bool Money { get; }
        public bool Abbreviated { get; }
        public string Text { get; }
    }

    // A suffix letter counts only when it is not the start of a unit word (so "5 KHz"/"4KB" stay 5/4).
    private static readonly Regex Pattern = new(@"(?<![\w.,])(?<neg1>-)?\s*(?<cur>\$)?\s*(?<neg2>-)?(?<num>\d[\d,]*(?:\.\d+)?|\.\d+)(?<suf>[KMBT](?![A-Za-z]))?", RegexOptions.CultureInvariant);

    internal static IEnumerable<Token> Tokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var clean = Regex.Replace(text, "<[^>]+>", "");
        foreach (Match match in Pattern.Matches(clean))
        {
            var digits = match.Groups["num"].Value.Replace(",", "");
            if (!decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) continue;
            var suffix = match.Groups["suf"].Value;
            var multiplier = suffix switch { "K" => 1_000m, "M" => 1_000_000m, "B" => 1_000_000_000m, "T" => 1_000_000_000_000m, _ => 1m };
            var negative = match.Groups["neg1"].Success || match.Groups["neg2"].Success;
            var dot = digits.IndexOf('.');
            var decimals = dot < 0 ? 0 : digits.Length - dot - 1;
            var precision = (double)(0.5m * multiplier / Pow10(decimals));
            var value = (double)(number * multiplier * (negative ? -1 : 1));
            yield return new Token(value, precision, match.Groups["cur"].Success, suffix.Length > 0, match.Value.Trim());
        }
    }

    private static decimal Pow10(int power) { var result = 1m; for (var i = 0; i < power; i++) result *= 10m; return result; }

    // First number anywhere in the text ("1.15K (+50)" -> 1150; "$80 -> $34" -> 80).
    internal static double? First(string? text) { foreach (var token in Tokens(text)) return token.Value; return null; }
    internal static Token? FirstToken(string? text) { foreach (var token in Tokens(text)) return token; return null; }
    internal static double[] All(string? text) => Tokens(text).Select(t => t.Value).ToArray();

    // Whole-text parse: exactly one number, optionally with a unit/period suffix such as "/m", "/d", "%", " W", " units".
    internal static bool TryParse(string? text, out double value)
    {
        value = 0;
        var tokens = Tokens(text).ToArray();
        if (tokens.Length != 1) return false;
        var rest = Regex.Replace(text!, "<[^>]+>", "").Trim();
        var at = rest.IndexOf(tokens[0].Text, StringComparison.Ordinal);
        if (at != 0) return false;
        var tail = rest.Substring(tokens[0].Text.Length).Trim();
        if (tail.Length > 0 && !Regex.IsMatch(tail, @"^(/m|/d|/y|%|[A-Za-zµ²°/ ]+)$", RegexOptions.CultureInvariant)) return false;
        value = tokens[0].Value;
        return true;
    }

    internal static double? Parse(string? text) => TryParse(text, out var value) ? value : null;

    // A native "Label: | value" field (either order): the first number of the first cell that is not a label.
    internal static Token? FieldToken(string? text)
    {
        if (text == null) return null;
        foreach (var cell in text.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0 && !c.EndsWith(":", StringComparison.Ordinal)))
            if (FirstToken(cell) is Token token) return token;
        return null;
    }

    internal static double? Field(string? text) => FieldToken(text)?.Value;
    internal static int? FieldInt(string? text) => Field(text) is double value ? (int)Math.Round(value) : null;

    // "Production lines: | 1.15K (+50)" -> 50 (the pending expansion is rendered as a raw integer).
    internal static int? Pending(string? text)
    {
        var match = Regex.Match(Regex.Replace(text ?? "", "<[^>]+>", ""), @"\(\s*\+\s*([\d,]+(?:\.\d+)?[KMBT]?)\s*\)", RegexOptions.CultureInvariant);
        return match.Success && First(match.Groups[1].Value) is double pending ? (int)Math.Round(pending) : null;
    }

    // Money anywhere in a text ("$1.95K -> $1.20K" -> 1950). Plain numbers are not money.
    internal static double? Money(string? text) { foreach (var token in Tokens(text)) if (token.Money) return token.Value; return null; }
    internal static double[] Moneys(string? text) => Tokens(text).Where(t => t.Money).Select(t => t.Value).ToArray();

    // Removes binary float noise from derived values (4059999.9999999995 -> 4060000), keeping 12 significant digits.
    internal static double Clean(double value) => double.IsFinite(value) && value != 0 ? double.Parse(value.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : value;
}
