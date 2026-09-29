using System.Diagnostics;
using System.Text.Json.Nodes;

// Runs the built CLI (pt-agent.dll) against MockBridge to cover Program.cs routing, option parsing and output cleaning.
// SAFETY: every invocation must target the mock (--endpoint is appended below and asserted). Never run commands that
// act on the local game process (quit, launch, status/help probes): a real game may be running while tests run.
internal static class ExeSmoke
{
    internal static async Task Run(Checker check)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "pt-agent.dll");
        if (!File.Exists(dll)) { check("exe smoke: pt-agent.dll present", false, dll); return; }
        var state = Path.Combine(Path.GetTempPath(), "pt-agent-exe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(state);
        using var bridge = new MockBridge();
        async Task<(int Code, JsonObject? Json, string Raw)> Cli(params string[] args)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (args.Length == 0 || args[0] is not ("game" or "notifications") || args.Contains("quit") || args.Contains("launch")) throw new InvalidOperationException("ExeSmoke may only run bridge commands against the mock: " + string.Join(" ", args));
            psi.ArgumentList.Add(dll);
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (args.Length > 0 && args[0] is "game" or "notifications") { psi.ArgumentList.Add("--endpoint"); psi.ArgumentList.Add(bridge.Url); }
            psi.Environment["PT_AGENT_STATE_DIR"] = state;
            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal)) ?? "";
            JsonObject? json = null;
            try { json = JsonNode.Parse(line) as JsonObject; } catch { }
            return (process.ExitCode, json, output);
        }
        try
        {
            var fast = await Cli("game", "situation", "--headless-fast", "--compact");
            check("exe fast bypasses UI composition and operation polling", fast.Code == 0 && fast.Json?["data"]?["executionMode"]?.ToString() == "headless-fast" && bridge.Log.Where(s => !s.StartsWith("status", StringComparison.Ordinal)).SequenceEqual(new[] { "fast game.situation" }), fast.Raw + " " + string.Join(",", bridge.Log));
            bridge.Log.Clear();
            bridge.HeadlessFastVersion = 0;
            var oldFast = await Cli("game", "product-price", "C46", "--price", "123", "--headless-fast");
            check("exe fast refuses old bridges without dispatch or UI fallback", oldFast.Code == 1 && oldFast.Json?["error"]?["code"]?.ToString() == "headless_fast_unavailable" && bridge.Products[0].Price == 589 && bridge.Log.All(s => s.StartsWith("status", StringComparison.Ordinal)), oldFast.Raw + " " + string.Join(",", bridge.Log));
            bridge.HeadlessFastVersion = 1;
            bridge.Log.Clear();
            var pulse = await Cli("game", "products-pulse", "--compact");
            check("exe products-pulse", pulse.Code == 0 && pulse.Json?["unassignedLines"]?.GetValue<int>() == 20, pulse.Raw);
            check("exe status checks keep the caller's notifications", bridge.StatusSessions.Count > 0 && bridge.StatusSessions.All(s => s == "local-cli-aux"), string.Join(",", bridge.StatusSessions));
            check("exe float noise cleaned", pulse.Json?["finances"]?["balancePerMonth"]?.ToJsonString() == "4060000", pulse.Json?["finances"]?.ToJsonString() ?? pulse.Raw);
            var dry = await Cli("game", "product-price", "C46", "--price", "280", "--dry-run");
            check("exe product-price dry-run", dry.Code == 0 && dry.Json?["dryRun"]?.GetValue<bool>() == true && bridge.Products[0].Price == 589, dry.Raw);
            var max = await Cli("game", "product-production", "C45", "--lines", "max");
            check("exe lines max", max.Code == 0 && bridge.Products[1].Manual == 520 && max.Json?["operation"]?["result"]?["data"]?["linesResolved"]?["applied"]?.GetValue<int>() == 520, max.Raw);
            var add = await Cli("game", "product-production", "C45", "--add", "-20");
            check("exe lines add", add.Code == 0 && bridge.Products[1].Manual == 500, add.Raw);
            var tooMany = await Cli("game", "product-production", "C45", "--lines", "900");
            check("exe lines out of range refused", tooMany.Code == 1 && bridge.Products[1].Manual == 500, tooMany.Raw);
            var clamp = await Cli("game", "product-production", "C45", "--lines", "900", "--clamp");
            check("exe lines clamp", clamp.Code == 0 && bridge.Products[1].Manual == 520, clamp.Raw);
            var expand = await Cli("game", "production-expand", "--lines", "10", "--count", "2");
            check("exe expand count", expand.Code == 0 && expand.Json?["completedBatches"]?.GetValue<int>() == 2 && bridge.Pending == 70, expand.Raw);
            var watch = await Cli("game", "watch-advance", "--products", "C46", "--days", "2", "--stop-on-competitor-price-cut-percent", "10", "--portfolio-guards");
            check("exe watch with new guards", watch.Code == 0 && watch.Json?["ok"]?.GetValue<bool>() == true && watch.Json?["competitorPrices"] != null, watch.Raw);
            var situation = await Cli("game", "situation", "--compact");
            check("exe situation", situation.Code == 0 && situation.Json?["markets"]?["Mobile"] != null, situation.Raw);
            var notes = await Cli("notifications", "--since", "0", "--type", "competitor_price_changed");
            check("exe notifications merge synthesized", notes.Code == 0 && notes.Json?["notifications"]?["items"]?.AsArray().Count > 0 && notes.Json?["notifications"]?["items"]?.AsArray().All(i => i?["type"]?.ToString() == "competitor_price_changed") == true, notes.Raw);
            var price = await Cli("game", "product-price", "C46", "--price", "600", "--skip-checks");
            check("exe price skip-checks", price.Code == 0 && bridge.Products[0].Price == 600, price.Raw);
            var history = await Cli("game", "price-history", "C46");
            check("exe price-history", history.Code == 0 && history.Json?["products"]?["C46"]?.AsArray().Count == 1, history.Raw);
            var compare = await Cli("game", "research-funding-compare", "--funding-percents", "25,50");
            check("exe research compare", compare.Code == 0 && compare.Json?["restored"]?.GetValue<bool>() == true, compare.Raw);
            var digest = await Cli("game", "monthly-digest");
            check("exe monthly digest", digest.Code == 0 && digest.Json?["kind"]?.ToString() == "monthly_digest", digest.Raw);
            var badSave = await Cli("game", "time-read", "--save", "x");
            check("exe --save only for quit", badSave.Code != 0, badSave.Raw);
            var minP = await Cli("game", "market-changes", "--competitor-price-min-percent", "5");
            check("exe market-changes threshold option", minP.Code == 0 && minP.Json?["ok"]?.GetValue<bool>() == true, minP.Raw);
            var ki = bridge.Rivals.FindIndex(r => r.Cpu == "Kore i7 7700"); bridge.Rivals[ki] = bridge.Rivals[ki] with { Price = 300 };
            var cut = await Cli("game", "market-changes");
            var cutText = string.Join(" ", cut.Json?["changes"]?["notifications"]?.AsArray().Where(n => n?["type"]?.ToString() == "competitor_price_changed").Select(n => n!["text"] + " " + n["impact"]?.ToJsonString()) ?? Array.Empty<string>());
            check("exe invariant decimals in hints", cut.Code == 0 && System.Text.RegularExpressions.Regex.IsMatch(cutText, @"\d\.\d+ vs \d") && !System.Text.RegularExpressions.Regex.IsMatch(cutText, @"\d,\d+ vs"), cutText);
            var options = await Cli("game", "cpu-options", "package");
            check("exe cpu-options passthrough", options.Code == 0, options.Raw);
        }
        finally { try { Directory.Delete(state, true); } catch { } }
    }
}
