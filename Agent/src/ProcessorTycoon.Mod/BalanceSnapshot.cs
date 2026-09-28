using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace ProcessorTycoonMod;

// Reads only the native desktop tooltip's already-rendered fields, including when collapsed, plus visible top-bar texts.
internal static class BalanceSnapshot
{
    public static JObject Read()
    {
        var matches = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == "ProcessorTycoon.CompanySystem.PlayerUI.MoneyBalance" && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy).ToArray();
        if (matches.Length != 1) return new JObject { ["available"] = false, ["reason"] = "Native desktop balance UI unavailable; missing credit is not zero credit." };
        var panel = matches[0];
        var rows = new JArray();
        foreach (var key in new[] { "credit", "sales", "contracts", "interest", "royalties", "outsourcing", "production", "research", "development", "construction" })
        {
            var info = panel.GetType().GetField(key + "Text", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(panel);
            if (info == null) throw new AgentError("game_ui_mismatch", "Missing native balance field: " + key);
            rows.Add(new JObject { ["key"] = key, ["label"] = Clean(info.GetType().GetProperty("Title")?.GetValue(info)?.ToString()), ["display"] = Clean(info.GetType().GetProperty("Text")?.GetValue(info)?.ToString()), ["period"] = key == "credit" ? "current-credit-availability" : "month-equivalent", ["kind"] = key == "credit" ? "credit" : key is "production" or "research" or "development" or "construction" ? "expense" : "net-flow" });
        }
        var balance = panel.GetType().GetField("balanceText", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(panel) as TMP_Text;
        var cash = TopBarText("MoneyAndBalance/Money");
        var result = new JObject { ["available"] = true, ["source"] = "native-desktop-balance-tooltip", ["companyScope"] = "player", ["cashDisplay"] = cash, ["balanceDisplay"] = Clean(balance?.text), ["availableCredit"] = rows[0]["display"]!.DeepClone(), ["default"] = Default(), ["items"] = rows };
        if (Bankrupt() is JObject fate) result["bankrupt"] = fate;
        var cashValue = Money(cash);
        var debt = -(cashValue ?? 0m);
        var interest = Money((string?)rows[3]["display"]);
        var credit = Money((string?)rows[0]["display"]);
        // Native rule (MoneyBalance.UpdateUI): Available Credit = debt limit + cash only while cash is negative. Positive cash
        // is NOT included, so spendable headroom before default is Available Credit + positive cash.
        var trend = cashValue != null && credit != null ? Trend(cashValue.Value < 0 ? credit.Value - cashValue.Value : credit.Value) : null;
        if (cashValue != null && credit != null)
            result["headroom"] = new JObject { ["availableCredit"] = credit, ["cashOnHand"] = Math.Max(0m, cashValue.Value), ["beforeDefault"] = credit.Value + Math.Max(0m, cashValue.Value), ["note"] = "Default starts when Available Credit falls below zero. Available Credit is the debt limit minus debt; positive cash is not part of it, so money you can spend before default = Available Credit + cash on hand (beforeDefault)." };
        if (debt > 0 && interest != null && credit != null)
        {
            var rate = Math.Abs(interest.Value) / debt;
            var debtInfo = new JObject { ["debt"] = debt, ["creditLimitEstimate"] = debt + credit.Value, ["impliedMonthlyInterestRatePercent"] = Math.Round(rate * 100m, 2), ["monthlyInterest"] = Math.Abs(interest.Value), ["note"] = "Derived from displayed cash, Available Credit and Interest: negative cash is borrowed against the credit limit and interest is charged on all of it. Display-rounded arithmetic, not native loan terms." };
            var growth = trend?["limitGrowthPerMonth"]?.Value<decimal?>();
            if (growth > 0 && rate > 0)
            {
                debtInfo["debtWhereInterestEqualsLimitGrowth"] = Math.Round(growth.Value / rate / 10000m) * 10000m;
                debtInfo["headroomGrowthAfterInterestPerMonth"] = Math.Round(growth.Value - Math.Abs(interest.Value));
                debtInfo["trapNote"] = "The credit limit's measured growth funds spending only while it exceeds interest. Near debtWhereInterestEqualsLimitGrowth, new headroom stops; beyond it, interest alone shrinks Available Credit.";
            }
            result["debt"] = debtInfo;
        }
        if (trend != null) result["creditTrend"] = trend;
        result["precision"] = "display-rounded";
        result["periodNote"] = "Latest native desktop rates expressed per month, not the Finance graph's historical period. Expense rows are costs even when displayed without a minus sign.";
        result["creditNote"] = "Available Credit is separate from cash. Below zero the company is in default (see default). Do not infer insolvency from cash alone or from the disabled Bank app.";
        return result;
    }

    // Credit limit (Available Credit minus cash) sampled whenever finances are read in this game process; one sample per game date.
    private static readonly SortedDictionary<DateTime, decimal> limitSamples = new();
    internal static void ResetTrend() => limitSamples.Clear();

    private static JObject? Trend(decimal limit)
    {
        DateTime today;
        try { today = DateTime.ParseExact((string)TimeAdvanceController.ReadClock()["date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch { return null; }
        if (limitSamples.Count > 0 && today < limitSamples.Keys.Last()) limitSamples.Clear();
        limitSamples[today] = limit;
        foreach (var old in limitSamples.Keys.Where(d => d < today.AddDays(-400)).ToArray()) limitSamples.Remove(old);
        var window = limitSamples.Where(s => s.Key >= today.AddDays(-365)).ToArray();
        var first = window.First();
        var days = (today - first.Key).Days;
        var trend = new JObject { ["creditLimitEstimate"] = limit, ["samples"] = window.Length };
        if (days < 20) { trend["note"] = "Not enough history yet: the credit limit trend is measured from finance reads in this game process over at least 20 game days."; return trend; }
        trend["fromDate"] = first.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        trend["fromLimit"] = first.Value;
        trend["limitGrowthPerMonth"] = Math.Round((limit - first.Value) / days * 30m);
        trend["note"] = "The credit limit grows over time, including jumps such as around the new year. Growth is measured from finance reads in this game process (reset by new game, load or restart) over up to 365 days. It funds spending only while it exceeds interest; see debt.debtWhereInterestEqualsLimitGrowth.";
        return trend;
    }

    // Game over for the player's company: the native Game Over window (BankruptcyWindow) is open, or in a Multiplayer session
    // the Multiplayer mod reports it bankrupt (it keeps the player in the session as a spectator instead of Game Over).
    public static JObject? Bankrupt()
    {
        if (MultiplayerInterop.LocalBankrupt) return new JObject { ["bankrupt"] = true, ["source"] = "multiplayer_session", ["note"] = "Your company is bankrupt: the game is over for it. In this Multiplayer session you stay as a spectator; its projects were cancelled, its products retired and nothing new can start. Stop playing it; reads still work. Leave with pt-agent mp leave when you are done." };
        if (TimeAdvanceController.PauseTriggerScopes().Contains("BankruptcyWindow")) return new JObject { ["bankrupt"] = true, ["source"] = "native_game_over", ["note"] = "Your company is bankrupt: the native Game Over window is open and the campaign cannot continue. Its only choice returns to the main menu (game dialog-read)." };
        return null;
    }

    // Native top-bar event text "Bankruptcy in N days", visible only while the company is in default.
    public static JObject Default()
    {
        var text = TopBarText("Events/BankruptcyEvent");
        if (string.IsNullOrEmpty(text)) return new JObject { ["active"] = false };
        // The Multiplayer mod relabels the stale "Bankruptcy in 0 days" of a bankrupt company it keeps watching.
        if (!text.StartsWith("Bankruptcy", StringComparison.Ordinal)) return new JObject { ["active"] = false, ["display"] = text, ["bankrupt"] = text.StartsWith("Bankrupt", StringComparison.Ordinal) };
        var days = Regex.Match(text, @"(\d+)\s*day");
        return new JObject { ["active"] = true, ["display"] = text, ["daysLeft"] = days.Success ? int.Parse(days.Groups[1].Value, CultureInfo.InvariantCulture) : null, ["note"] = "Default began when Available Credit fell below zero. Observed: the countdown always runs to zero and does not clear early. The company survives if Available Credit is zero or more when it ends; otherwise it goes bankrupt (game over). A new default can start later, and the native warning popup appeared only for the first one." };
    }

    private static string? TopBarText(string suffix)
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || !text.gameObject.scene.IsValid() || !text.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(text.text)) continue;
            var path = text.transform.name;
            for (var parent = text.transform.parent; parent != null && path.Length < 200; parent = parent.parent) path = parent.name + "/" + path;
            if (path.Contains("TopBar/" + suffix)) return Clean(text.text);
        }
        return null;
    }

    internal static decimal? Money(string? text)
    {
        var match = Regex.Match(text ?? "", @"(-?)\$\s*([\d,.]+)\s*([KMBT]?)", RegexOptions.IgnoreCase);
        if (!match.Success || !decimal.TryParse(match.Groups[2].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) return null;
        var multiplier = match.Groups[3].Value.ToUpperInvariant() switch { "K" => 1000m, "M" => 1000000m, "B" => 1000000000m, "T" => 1000000000000m, _ => 1m };
        return amount * multiplier * (match.Groups[1].Value == "-" ? -1 : 1);
    }

    private static string Clean(string? value) => Regex.Replace(value ?? "", "<[^>]+>", "").Trim();
}
