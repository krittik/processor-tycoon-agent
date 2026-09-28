using System.Text.Json.Nodes;

// CLI composition: every Production row from one Production read plus the desktop finance line, and (by default) one
// read of the Analysis "Your Sales" sheet for price, unit cost, popularity and last month's sold/missed units. Reads only.
internal static class ProductsPulse
{
    internal static async Task<JsonObject> Run(HttpClient client, JsonObject request, int timeout)
    {
        var options = request["parameters"] as JsonObject ?? new JsonObject();
        var unknown = options.Select(pair => pair.Key).Except(new[] { "products", "keepWindows", "keepOpen", "sales" }, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0 || (request["target"]?.ToString() ?? "").Length > 0) return Error("invalid_request", "products-pulse takes no target; options: --products A,B, --sales true|false, --keep-windows true, --keep-open true.");
        bool Flag(string key, bool fallback = false) => options[key] == null ? fallback : bool.TryParse(options[key]?.ToString(), out var value) && value;
        var filter = (options["products"]?.ToString() ?? "").Split(',').Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var session = "pulse-cli";
        var workspace = new JsonObject();
        var visible = await Workspace.Visible(client, session, timeout);
        var before = Workspace.Scopes(visible);
        if (!Flag("keepWindows"))
        {
            var tidy = await Workspace.Tidy(client, session, timeout, visible, "ProductionWindow");
            if (tidy["closed"]!.AsArray().Count > 0) workspace["closedStaleWindows"] = tidy["closed"]!.DeepClone();
            if (tidy["failed"]!.AsObject().Count > 0) workspace["staleWindowsNotClosed"] = tidy["failed"]!.DeepClone();
            foreach (var closed in tidy["closed"]!.AsArray()) before.Remove(closed!.ToString());
        }
        var reply = await GameWatch.Call(client, "game.production-read", "", null, session, timeout);
        if (!GameWatch.TryData(reply, out var production))
        {
            var blocking = await Dialogs.Blocking(client, session, timeout, checkPauseMenu: true, failedReply: reply);
            var popups = blocking?["dialogs"]?.AsArray().Any(d => !Dialogs.IsForm(d) && d?["source"]?.ToString() != "covering_window") == true;
            var failure = blocking == null ? Error("production_unavailable", "The native Production read failed.") : popups ? Error("blocked_by_dialog", "A native dialog blocks the Production read.") : Error("blocked_by_window", "An open form, editor or draft covers Production (it is never closed automatically).");
            if (blocking != null) foreach (var key in new[] { "dialogs", "releaseForm", "next" }) { if (blocking[key] != null) failure[key] = blocking[key]!.DeepClone(); }
            else
            {
                failure["commandReply"] = reply.DeepClone();
                // Name the windows that stay open and may cover Production (drafts, forms, menus): report precisely, close nothing.
                var kept = Workspace.Others(await Workspace.Visible(client, session, timeout));
                if (kept.Count > 0) { failure["windowsKeptOpen"] = kept; failure["next"] = "These windows are never closed automatically and may cover Production. Finish or close them explicitly (game window-close NAME), then retry."; }
            }
            return failure;
        }
        ProductionRows.EnrichLines(production!);
        // Plugins >= 0.3.9 include the date and the desktop finance line in production-read: one bridge read in total.
        JsonObject finance;
        if (production!["finances"]?["available"]?.GetValue<bool>() == true) finance = ProductionRows.Finance(pulseFinances: production["finances"] as JsonObject);
        else
        {
            var desktop = await GameWatch.Call(client, "game.desktop-read", "", null, session, timeout);
            finance = GameWatch.TryData(desktop, out var d) ? ProductionRows.Finance(desktopFinances: d!["finances"] as JsonObject) : new JsonObject { ["available"] = false };
        }
        finance.Remove("available");
        if (finance["defaultActive"]?.GetValue<bool>() == false) finance.Remove("defaultActive");
        JsonObject? time = null;
        if (production["date"] != null) time = new JsonObject { ["date"] = production["date"]!.DeepClone() };
        else GameWatch.TryData(await GameWatch.Call(client, "game.time-read", "", null, session, timeout), out time);

        var rows = production!["products"]?.AsArray().OfType<JsonObject>().Select(ProductionRows.FromRow).ToArray() ?? Array.Empty<JsonObject>();
        // Close Production when this read opened it or an earlier agent command left it behind; a player's own window stays.
        if ((!before.Contains("ProductionWindow") || Workspace.AgentOpened(Workspace.Window(visible, "ProductionWindow"))) && !Flag("keepOpen"))
        {
            var close = await GameWatch.Call(client, "game.window-close", "ProductionWindow", null, session, timeout);
            if (!GameWatch.TryData(close, out var closed) || closed!["closed"]?.GetValue<bool>() != true) workspace["productionLeftOpen"] = true;
        }
        Dictionary<string, CliReads.SalesRow>? sales = null;
        if (Flag("sales", fallback: true))
        {
            sales = await CliReads.Sales(client, session, timeout);
            if (sales == null) workspace["salesUnavailable"] = "The Analysis 'Your Sales' sheet could not be read; price, unit cost and popularity are missing.";
            if (!before.Contains("AnalysisWindow") && !Flag("keepOpen"))
            {
                var close = await GameWatch.Call(client, "game.window-close", "AnalysisWindow", null, session, timeout);
                if (!GameWatch.TryData(close, out var closed) || closed!["closed"]?.GetValue<bool>() != true) workspace["analysisLeftOpen"] = true;
            }
        }

        var duplicates = rows.GroupBy(row => row["name"]?.ToString()).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var products = new JsonArray();
        foreach (var row in rows.Where(row => filter.Length == 0 || filter.Contains(row["name"]?.ToString())))
        {
            if (!duplicates.Contains(row["name"]?.ToString())) row.Remove("productRef");
            if (row["manualLines"] == null) { row.Remove("manualLines"); row.Remove("contractLines"); }
            row.Remove("sliderMaxLines");
            Enrich(row, sales?.GetValueOrDefault(row["name"]?.ToString() ?? ""));
            products.Add(row);
        }
        var lineInfo = ProductionRows.Lines(production);
        var lines = new JsonObject
        {
            ["total"] = lineInfo["capacity"]?.DeepClone(), ["used"] = lineInfo["used"]?.DeepClone(), ["unassigned"] = lineInfo["unassigned"]?.DeepClone(),
            ["contracts"] = lineInfo["contracts"]?.DeepClone(), ["clients"] = lineInfo["clients"]?.DeepClone(), ["pendingExpansion"] = lineInfo["pendingExpansion"]?.DeepClone() ?? 0,
            ["overAssigned"] = lineInfo["overAssigned"]?.DeepClone(), ["exact"] = lineInfo["exact"]?.DeepClone(), ["basis"] = lineInfo["basis"]?.DeepClone(),
            ["productLinesTotal"] = lineInfo["rowsRequestedTotal"]?.DeepClone(), ["consistent"] = lineInfo["consistent"]?.DeepClone(), ["problems"] = lineInfo["problems"]?.DeepClone(), ["display"] = lineInfo["display"]?.DeepClone()
        };
        var result = new JsonObject
        {
            ["ok"] = true, ["kind"] = "products_pulse", ["date"] = time?["date"]?.DeepClone(), ["finances"] = finance, ["lines"] = lines, ["unassignedLines"] = lineInfo["unassigned"]?.DeepClone(),
            ["automation"] = production["automation"]?["enabled"]?.DeepClone()
        };
        if (production["settings"]?["upgradeLines"] is JsonNode upgrade) result["upgradeLines"] = upgrade["value"]?.DeepClone() ?? upgrade.DeepClone();
        result["products"] = products;
        if (sales != null)
        {
            result["totals"] = new JsonObject
            {
                ["stockValueAtUnitCost"] = Math.Round(products.OfType<JsonObject>().Sum(p => CliReads.Num(p["stockValueAtUnitCost"]) ?? 0)),
                ["soldLastMonth"] = products.OfType<JsonObject>().Sum(p => CliReads.Num(p["soldLastMonth"]) ?? 0),
                ["missedSalesLastMonth"] = products.OfType<JsonObject>().Sum(p => CliReads.Num(p["missedSalesLastMonth"]) ?? 0)
            };
        }
        var missing = filter.Where(name => !rows.Any(row => row["name"]?.ToString() == name)).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray();
        if (missing.Length > 0) result["missing"] = new JsonArray(missing);
        if (workspace.Count > 0) result["workspace"] = workspace;
        result["semantics"] = "Rates are current native row indicators (demand/production per month, balancePerDay = units/day stock flow), not completed sales. Out of stock counts as 0 stock. demandToProduction is null while production is 0. unitsPerLinePerMonth = production / lines. price, unitCost (current real unit cost), popularity (native highest popularity x100), soldLastMonth and missedSalesLastMonth (demand the product could not serve last month, e.g. while out of stock) come from the Analysis 'Your Sales' sheet. unservedDemandPerMonthEstimate = demand - production while out of stock (that demand overflows to your other CPUs or to rivals). lines.unassigned is exact (product slider ranges or the desktop icon); lines.consistent checks product rows against the displayed totals, which are abbreviated above 999 lines. " + ProductionRows.LinesSemantics;
        result["precision"] = "native-display-rounded";
        return result;
    }

    // Adds per-line output, sales-sheet values and derived money/unserved fields to one compact product row.
    internal static void Enrich(JsonObject row, CliReads.SalesRow? sale)
    {
        var lines = CliReads.Num(row["lines"]);
        var produced = CliReads.Num(row["productionPerMonth"]);
        var demand = CliReads.Num(row["demandPerMonth"]);
        var stock = CliReads.Num(row["stockUnits"]);
        row["unitsPerLinePerMonth"] = lines > 0 && produced != null ? Math.Round(produced.Value / lines.Value, 1) : null;
        row["unservedDemandPerMonthEstimate"] = row["outOfStock"]?.GetValue<bool>() == true && demand > produced ? Math.Round(demand!.Value - (produced ?? 0)) : 0;
        if (sale == null) return;
        row["price"] = sale.Price; row["unitCost"] = sale.UnitCost; row["popularity"] = sale.Popularity;
        row["marginPerUnit"] = sale.Price != null && sale.UnitCost != null ? Math.Round(sale.Price.Value - sale.UnitCost.Value, 2) : null;
        row["soldLastMonth"] = sale.SoldLastMonth; row["missedSalesLastMonth"] = sale.MissedLastMonth; row["allTimeSold"] = sale.AllTime;
        row["profitLastMonth"] = sale.ProfitLastMonth;
        row["stockValueAtUnitCost"] = stock != null && sale.UnitCost != null ? Math.Round(stock.Value * sale.UnitCost.Value) : null;
    }

    private static JsonObject Error(string code, string message) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
