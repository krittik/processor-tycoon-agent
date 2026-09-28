using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

// Processor Tycoon 0.2.16a5 English UI mapping. All state and mutations pass through visible native UI.
internal sealed class ProductionGameApi : IGameModule
{
    private const string ProductionScope = "ProductionWindow";
    private const string AnalysisScope = "AnalysisWindow";
    private const string EditScope = "CpuEditWindow";
    private const string SalesScope = "MarketSalesSpreadsheetWindow";
    private const string CapacityScope = "FactoryManagementWindow";
    private const string FablessScope = "FablessConfirmationWindow";
    private const string ProductRefPrefix = "product-ui:";
    private static readonly string ProductRefSession = Guid.NewGuid().ToString("N").Substring(0, 8);
    private static readonly string[] summaryNames = { "ProductionLines", "LinesUsed", "UsedByContracts", "UsedByClients", "OperatingCost", "WaferSize", "Lithography", "Employees", "Throughput", "UpgradeCost" };
    private static readonly string[][] salesSchemas =
    {
        new[] { "CPU", "All Time", "Sold", "Missed Sales", "Stock", "Income", "Profit", "Unit Cost", "Price", "Popularity" },
        new[] { "CPU", "Highest", "Sold", "Server", "Desktop", "Mobile", "Industries", "Profit", "Price", "Popularity" },
        new[] { "CPU", "Highest", "Sold", "High End", "Mid Range", "Low End", "Missed", "Profit", "Price", "Popularity" }
    };
    private readonly GameUi ui;
    private ProductIdentity? selectedProduct;
    private JObject? productPreview;
    private JObject? expansionVerification;
    private bool salesWasOpen;

    public ProductionGameApi(GenericUi native) => ui = new GameUi(native);
    public string[] Commands { get; } = new[] { "game.production-read", "game.production-history", "game.production-settings", "game.production-automation", "game.production-capacity", "game.production-expansion-preview", "game.production-expand", "game.production-line-sale-preview", "game.production-sell-lines", "game.production-division-sale-preview", "game.production-sell-division", "game.product-list", "game.product-pulse", "game.product-read", "game.product-preview", "game.product-production", "game.product-price", "game.product-set", "game.sales-read" };

    public void Validate(Request request)
    {
        NoValue(request);
        if (request.Hidden && request.Command is not ("game.production-read" or "game.production-history" or "game.production-capacity" or "game.product-list" or "game.product-read" or "game.sales-read")) throw new AgentError("hidden_not_supported", "Game API mutations always use visible native UI; retry without --hidden.");
        switch (request.Command)
        {
            case "game.production-read":
            case "game.product-list":
                NoTarget(request); GameUi.Parameters(request); break;
            case "game.production-history":
                NoTarget(request); GameUi.Parameters(request, "company", "yearly"); ValidateProductionHistory(request); break;
            case "game.production-capacity":
                NoTarget(request); GameUi.Parameters(request); break;
            case "game.production-expansion-preview":
                NoTarget(request); GameUi.Parameters(request, "lines", "waferSize"); RequireInteger(request, "lines", 1, 50); ValidateWafer(request, required: false);
                break;
            case "game.production-expand":
                NoTarget(request); GameUi.Parameters(request, "lines", "waferSize", "acknowledgeRisks"); RequireInteger(request, "lines", 1, 50); ValidateWafer(request, required: false);
                if (request.Parameters?["acknowledgeRisks"] != null && request.Parameters["acknowledgeRisks"]!.Type != JTokenType.String) throw new AgentError("invalid_value", "acknowledgeRisks must be a comma-separated list of risk kinds.");
                break;
            case "game.production-line-sale-preview":
            case "game.production-sell-lines":
                NoTarget(request); GameUi.Parameters(request, "lines", "waferSize"); RequireInteger(request, "lines", 1, int.MaxValue); ValidateWafer(request, required: true);
                break;
            case "game.production-division-sale-preview":
                NoTarget(request); GameUi.Parameters(request); break;
            case "game.production-sell-division":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); break;
            case "game.sales-read":
                NoTarget(request); GameUi.Parameters(request, "market", "showRetired", "variant", "sortBy", "scroll"); ValidateSales(request); break;
            case "game.production-automation":
                NoTarget(request); GameUi.Parameters(request, "enabled"); RequireBoolean(request, "enabled"); break;
            case "game.production-settings":
                NoTarget(request); GameUi.Parameters(request, "foundryServices", "upgradeWafer", "upgradeLines", "automation"); ValidateBooleans(request, "foundryServices", "upgradeWafer", "upgradeLines", "automation");
                if (request.Parameters?["upgradeWafer"] != null && request.Parameters["upgradeLines"] != null) throw new AgentError("invalid_request", "upgradeLines and upgradeWafer are the same native Upgrade lines toggle; supply one.");
                break;
            case "game.product-read":
            case "game.product-pulse":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); break;
            case "game.product-preview":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "name", "price", "retire", "sellOnMarket", "availableForContracts"); ValidateProductFields(request, requireAny: false); break;
            case "game.product-production":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "lines"); RequireInteger(request, "lines", 0, int.MaxValue); break;
            case "game.product-price":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "price"); RequireInteger(request, "price", 1, 9999); break;
            case "game.product-set":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "name", "price", "retire", "sellOnMarket", "availableForContracts"); ValidateProductSet(request); break;
            default:
                throw new AgentError("unsupported_command", $"Production module does not implement {request.Command}.");
        }
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        if (request.Command == "game.production-history")
        {
            foreach (var step in EnsureProductionHistory(request)) yield return step;
            foreach (var step in ConfigureProductionHistory(request)) yield return step;
            yield break;
        }

        if (request.Command == "game.sales-read")
        {
            foreach (var step in EnsureSales(request)) yield return step;
            foreach (var step in ConfigureSales(request)) yield return step;
            EnsureSalesSchema(ui.Catalog(SalesScope));
            yield break;
        }

        if (request.Command is "game.production-capacity" or "game.production-expansion-preview" or "game.production-expand")
        {
            foreach (var step in EnsureWindow(request, ProductionScope, "Production Desktop", "Production")) yield return step;
            foreach (var step in EnsureExpansion(request)) yield return step;
            if (request.Command == "game.production-capacity") yield break;
            foreach (var step in ConfigureExpansion(request)) yield return step;
            if (request.Command == "game.production-expansion-preview") yield break;
            ExpansionRiskGuard(request);
            var before = PendingExpansionLines();
            var projectsBefore = SafeProjectStates();
            yield return GameUi.Click(CapacityControl("Expand", "button"));
            var after = PendingExpansionLines();
            var projectsAfter = SafeProjectStates();
            var requested = request.Parameters!["lines"]!.Value<int>();
            // Two independent postconditions: the pending "(+N)" grew by the request, or a new project card appeared.
            var newProjects = projectsAfter.Where(p => !projectsBefore.ContainsKey(p.Key)).Select(p => p.Value.Name).ToArray();
            expansionVerification = new JObject { ["pendingBefore"] = before, ["pendingAfter"] = after, ["pendingIncreasedByRequest"] = after - before == requested, ["projectCountBefore"] = projectsBefore.Count, ["projectCountAfter"] = projectsAfter.Count, ["newProjects"] = new JArray(newProjects) };
            if (after - before != requested && newProjects.Length != 1) throw new AgentError("outcome_unverified", $"Native Build was dispatched, but pending production lines changed from {before} to {after} (not by {requested}) and {newProjects.Length} new project card(s) appeared. Inspect Production and Projects before retrying.");
            expansionVerification["verifiedBy"] = after - before == requested ? newProjects.Length == 1 ? "pending_lines_and_new_project" : "pending_lines" : "new_project";
            yield break;
        }

        if (request.Command is "game.production-line-sale-preview" or "game.production-sell-lines")
        {
            foreach (var step in EnsureWindow(request, ProductionScope, "Production Desktop", "Production")) yield return step;
            foreach (var step in EnsureSale(request)) yield return step;
            foreach (var step in ConfigureSale(request)) yield return step;
            if (request.Command == "game.production-line-sale-preview") yield break;
            var wafer = request.Parameters!["waferSize"]!.Value<string>()!;
            var lines = request.Parameters!["lines"]!.Value<int>();
            var beforeWafer = WaferLineCount(wafer);
            var beforeCapacity = CapacityNumbers();
            yield return GameUi.Click(CapacityControl("Sell", "button"));
            var afterWafer = WaferLineCount(wafer);
            var afterCapacity = CapacityNumbers();
            // An abbreviated total ("1.15K") cannot show a small decrease; the per-wafer inventory is always exact.
            var capacityChecked = beforeCapacity.Exact && afterCapacity.Exact;
            if (beforeWafer - afterWafer != lines || capacityChecked && beforeCapacity.Current - afterCapacity.Current != lines) throw new AgentError("outcome_unverified", $"Native Sell was dispatched, but wafer lines changed {beforeWafer}->{afterWafer} and total capacity {beforeCapacity.Current}->{afterCapacity.Current}; expected both to decrease by {lines}. Do not retry blindly.");
            yield break;
        }

        if (request.Command is "game.production-division-sale-preview" or "game.production-sell-division")
        {
            foreach (var step in EnsureWindow(request, ProductionScope, "Production Desktop", "Production")) yield return step;
            foreach (var step in EnsureSale(request)) yield return step;
            foreach (var step in EnsureFablessConfirmation(request)) yield return step;
            if (request.Command == "game.production-division-sale-preview") yield break;
            var provider = GameUi.RequiredTarget(request);
            var dropdown = GameUi.Find(ui.Read(FablessScope), "Manufacturer Dropdown", "select");
            var selected = dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>();
            if (selected != provider) yield return GameUi.Select(dropdown, provider);
            dropdown = GameUi.Find(ui.Read(FablessScope), "Manufacturer Dropdown", "select");
            if (dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>() != provider) throw new AgentError("value_not_applied", "Native manufacturer selection did not persist; division was not sold.");
            yield return GameUi.Click(GameUi.Find(ui.Read(FablessScope), "Button_Call To Action", "button"));
            if (GameUi.Controls(ui.Read(FablessScope)).Length > 0) throw new AgentError("outcome_unverified", "Native Sell Division was dispatched but the confirmation remained open. Do not retry blindly.");
            if (CapacityNumbers().Current != 0) throw new AgentError("outcome_unverified", "Native confirmation closed, but manufacturing capacity is not zero. Do not retry blindly.");
            yield break;
        }

        foreach (var step in EnsureWindow(request, ProductionScope, "Production Desktop", "Production")) yield return step;
        if (request.Command is "game.production-read" or "game.product-list") yield break;

        if (request.Command is "game.production-automation" or "game.production-settings")
        {
            foreach (var setting in ProductionSettings(request)) yield return setting;
            yield break;
        }

        var target = GameUi.RequiredTarget(request);
        var product = ResolveProduct(target);
        selectedProduct = product;
        productPreview = null;
        if (request.Command == "game.product-production")
        {
            var wanted = request.Parameters!["lines"]!.Value<int>();
            var slider = ProductControl(product, "slider");
            if (slider["blockedReason"] != null)
            {
                var automation = ProductionControl("Automate Production", "toggle");
                if ((bool)automation["value"]! && automation["blockedReason"] == null) throw new AgentError("automation_enabled", $"Manual allocation for '{product.Name}' is disabled because native production automation is enabled. Run game production-settings --automation false, then retry product-production.");
                throw new AgentError("not_interactable", $"Manual allocation for '{product.Name}' is unavailable: {slider["blockedReason"]}. Native automation is {((bool)automation["value"]! ? "on" : "off")} and its control is {(automation["blockedReason"] == null ? "available" : automation["blockedReason"]!.Value<string>())}; no automation recovery was assumed.");
            }
            var range = slider["range"]!;
            if (!(bool)range["wholeNumbers"]! || wanted < range["rawMin"]!.Value<double>() || wanted > range["rawMax"]!.Value<double>()) throw new AgentError("invalid_value", $"{product.Name} accepts {range["rawMin"]}-{range["rawMax"]} manual production lines in the current native UI (the maximum is the current manual lines plus unassigned lines; lines dedicated to contracts are separate and not set by --lines). No input dispatched.");
            if (slider["value"]!.Value<int>() != wanted) yield return GameUi.Set(slider, new JValue(wanted));
            if (ProductControl(product, "slider")["value"]!.Value<int>() != wanted) throw new AgentError("value_not_applied", $"Native production allocation for {product.Name} did not retain {wanted} lines.");
            yield break;
        }

        if (request.Command == "game.product-pulse") yield break;
        foreach (var step in OpenProduct(request, product, target.StartsWith(ProductRefPrefix, StringComparison.Ordinal))) yield return step;
        if (request.Command == "game.product-read") yield break;

        if (request.Command == "game.product-preview")
        {
            foreach (var step in ApplyProductDraft(request)) yield return step;
            var preview = ProductEditor(product);
            productPreview = preview;
            preview["commits"] = false;
            preview["draftDiscarded"] = true;
            preview["visibleEditorLeftOpen"] = false;
            preview["next"] = "Preview was cancelled natively. Reissue product-set with the intended fields to commit them.";
            yield return GameUi.Click(EditControl("Cancel", "button"));
            if (GameUi.Controls(ui.Read(EditScope)).Length > 0) throw new AgentError("outcome_unverified", "Native Cancel was dispatched but the CPU editor remained open. No Confirm was pressed.");
            var current = ProductIdentities(ui.Catalog(ProductionScope)).Where(row => row.Group == product.Group).ToArray();
            if (current.Length != 1 || current[0].Name != product.Name) throw new AgentError("outcome_unverified", "The preview editor closed, but the selected rendered production row did not retain its original identity/name.");
            yield break;
        }

        foreach (var step in ApplyProduct(request, product)) yield return step;
    }

    public JObject Result(Request request)
    {
        return request.Command switch
        {
            "game.production-history" => ProductionHistory(request),
            "game.sales-read" => Sales(request),
            "game.production-capacity" => Capacity(),
            "game.production-expansion-preview" => Capacity(),
            "game.production-expand" => WithVerification(AddOutcome(Capacity(), "expansion_started", null, request.Parameters!)),
            "game.production-line-sale-preview" => LineSale(),
            "game.production-sell-lines" => AddOutcome(LineSale(), "lines_sold", null, request.Parameters!),
            "game.production-division-sale-preview" => DivisionConfirmation(),
            "game.production-sell-division" => DivisionSold(GameUi.RequiredTarget(request)),
            "game.product-read" => ProductEditor(selectedProduct!),
            "game.product-pulse" => ProductPulse(selectedProduct!),
            "game.product-preview" => productPreview ?? throw new AgentError("outcome_unverified", "Product preview result was not captured."),
            "game.product-price" => new JObject { ["method"] = "game", ["outcome"] = "price_confirmed", ["product"] = selectedProduct!.Name, ["appliedValue"] = request.Parameters!["price"]!.DeepClone(), ["date"] = TimeAdvanceController.ReadClock()["date"]!.DeepClone(), ["next"] = "The price was confirmed in the native editor. Demand is recalculated by the game later; use product-pulse after advancing time to measure the response." },
            "game.product-set" => AddOutcome(Production(), (bool?)request.Parameters!["retire"] == true ? "retirement_confirmed" : "product_changes_confirmed", selectedProduct!.Name, request.Parameters!),
            "game.product-production" => AddOutcome(Production(), "production_applied", selectedProduct!.Name, request.Parameters!["lines"]!),
            "game.production-settings" => AddOutcome(Production(), "settings_applied", null, request.Parameters!),
            "game.production-read" => WithDateAndFinances(Production()),
            "game.production-automation" => AddOutcome(Production(), "automation_applied", null, request.Parameters!["enabled"]!),
            _ => Production()
        };
    }

    private IEnumerable<Request> EnsureWindow(Request request, string scope, string desktopName, string label)
    {
        if (GameUi.Controls(ui.Read(scope)).Length > 0) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", $"{request.Command} must open the visible {label} window. Retry without --hidden.");
        var desktop = ui.Read("DesktopButtons");
        var button = GameUi.One(GameUi.Controls(desktop).Where(c => (string?)c["name"] == desktopName && (string?)c["role"] == "button"), $"{label} desktop button");
        yield return GameUi.Click(button);
        if (GameUi.Controls(ui.Read(scope)).Length == 0) throw new AgentError("game_ui_mismatch", $"The native {label} button was clicked, but {scope} did not become visible.");
    }

    private IEnumerable<Request> EnsureSales(Request request)
    {
        salesWasOpen = GameUi.Controls(ui.Read(SalesScope)).Length > 0;
        if (salesWasOpen) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "game.sales-read must expose the visible Analysis / Your Sales tab. Retry without --hidden.");
        var analysis = ui.Read("AnalysisWindow");
        if (GameUi.Controls(analysis).Length > 0)
        {
            var tab = GameUi.One(GameUi.Controls(analysis).Where(c => (string?)c["name"] == "Button Tab Sales" && (string?)c["role"] == "button"), "Your Sales tab");
            yield return GameUi.Click(tab);
        }
        else
        {
            var button = ui.CpuDraftShortcut("Analysis") ?? GameUi.One(GameUi.Controls(ui.Read("DesktopButtons")).Where(c => (string?)c["name"] == "Analysis Desktop" && (string?)c["role"] == "button"), "Analysis desktop button");
            yield return GameUi.Click(button);
            if (GameUi.Controls(ui.Read(SalesScope)).Length == 0)
            {
                analysis = ui.Read("AnalysisWindow");
                var tab = GameUi.One(GameUi.Controls(analysis).Where(c => (string?)c["name"] == "Button Tab Sales" && (string?)c["role"] == "button"), "Your Sales tab");
                yield return GameUi.Click(tab);
            }
        }
        if (GameUi.Controls(ui.Read(SalesScope)).Length == 0) throw new AgentError("game_ui_mismatch", "Your Sales did not become visible after the native Analysis navigation.");
    }

    private IEnumerable<Request> EnsureProductionHistory(Request request)
    {
        var analysis = ui.Read(AnalysisScope);
        if (!ProductionGraphVisible(analysis))
        {
            if (request.Hidden) throw new AgentError("hidden_not_supported", "game.production-history must expose the visible Analysis / Production tab. Retry without --hidden.");
            if (GameUi.Controls(analysis).Length == 0)
            {
                var button = ui.CpuDraftShortcut("Analysis") ?? GameUi.One(GameUi.Controls(ui.Read("DesktopButtons")).Where(c => (string?)c["name"] == "Analysis Desktop" && (string?)c["role"] == "button"), "Analysis desktop button");
                yield return GameUi.Click(button);
                analysis = ui.Read(AnalysisScope);
            }
            if (!ProductionGraphVisible(analysis)) yield return GameUi.Click(GameUi.Find(analysis, "Button Tab Production", "button"));
        }
        if (!ProductionGraphVisible(ui.Read(AnalysisScope))) throw new AgentError("game_ui_mismatch", "Production history did not become visible after the native Analysis navigation.");
    }

    private IEnumerable<Request> ConfigureProductionHistory(Request request)
    {
        var view = ui.Read(AnalysisScope);
        var dropdown = GameUi.Find(view, "Companies Dropdown", "select");
        var wantedCompany = request.Parameters?["company"]?.Value<string>() ?? dropdown["options"]![0]!.Value<string>()!;
        var selected = dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>();
        if (selected != wantedCompany) yield return GameUi.Select(dropdown, wantedCompany);
        dropdown = GameUi.Find(ui.Read(AnalysisScope), "Companies Dropdown", "select");
        if (dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>() != wantedCompany) throw new AgentError("value_not_applied", "The native production-history company filter did not retain the requested exact option.");

        var yearly = request.Parameters?["yearly"]?.Value<bool>() ?? false;
        var currentYearly = ProductionHistoryYearly(ui.Read(AnalysisScope));
        if (currentYearly != yearly) yield return GameUi.Click(GameUi.Find(ui.Read(AnalysisScope), currentYearly ? "Yearly" : "Monthly", "button"));
        if (ProductionHistoryYearly(ui.Read(AnalysisScope)) != yearly) throw new AgentError("value_not_applied", $"The native production history did not switch to {(yearly ? "Yearly" : "Monthly")}.");
    }

    private IEnumerable<Request> EnsureExpansion(Request request)
    {
        foreach (var step in EnsureFactory(request)) yield return step;
        var view = ui.Read(CapacityScope);
        if (GameUi.Controls(view).Any(c => ((string?)c["context"] ?? "").Contains("/ExpandLines", StringComparison.Ordinal))) yield break;
        yield return GameUi.Click(GameUi.Find(view, "Button Tab Expansion", "button"));
        if (!GameUi.Controls(ui.Read(CapacityScope)).Any(c => ((string?)c["context"] ?? "").Contains("/ExpandLines", StringComparison.Ordinal))) throw new AgentError("game_ui_mismatch", "Native Expand tab did not expose the factory expansion form.");
    }

    private IEnumerable<Request> EnsureSale(Request request)
    {
        foreach (var step in EnsureFactory(request)) yield return step;
        var view = ui.Read(CapacityScope);
        if (GameUi.Controls(view).Any(c => ((string?)c["context"] ?? "").Contains("/ManageLines", StringComparison.Ordinal))) yield break;
        yield return GameUi.Click(GameUi.Find(view, "Button Tab Manage", "button"));
        if (!GameUi.Controls(ui.Read(CapacityScope)).Any(c => ((string?)c["context"] ?? "").Contains("/ManageLines", StringComparison.Ordinal))) throw new AgentError("game_ui_mismatch", "Native Sell tab did not expose the line-sale form.");
    }

    private IEnumerable<Request> EnsureFactory(Request request)
    {
        if (GameUi.Controls(ui.Read(CapacityScope)).Length > 0) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", $"{request.Command} must open visible Manage Lines. Retry without --hidden.");
        yield return GameUi.Click(ProductionControl("Open Expansion Window", "button"));
        if (GameUi.Controls(ui.Read(CapacityScope)).Length == 0) throw new AgentError("game_ui_mismatch", "Native Manage button did not open FactoryManagementWindow.");
    }

    private IEnumerable<Request> EnsureFablessConfirmation(Request request)
    {
        if (GameUi.Controls(ui.Read(FablessScope)).Length > 0) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", $"{request.Command} must open the visible manufacturing-division confirmation. Retry without --hidden.");
        yield return GameUi.Click(CapacityControl("Sell Division", "button"));
        if (GameUi.Controls(ui.Read(FablessScope)).Length == 0) throw new AgentError("game_ui_mismatch", "Native Sell Division did not open the known Fabless confirmation.");
    }

    private IEnumerable<Request> ConfigureExpansion(Request request)
    {
        if (request.Parameters?["waferSize"] is JToken wafer)
        {
            var dropdown = CapacityControl("Wafer Dropdown", "select");
            var wanted = wafer.Value<string>()!;
            var current = dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>();
            if (current != wanted) yield return GameUi.Select(dropdown, wanted);
            dropdown = CapacityControl("Wafer Dropdown", "select");
            if (dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>() != wanted) throw new AgentError("value_not_applied", "Native expansion wafer selection did not persist.");
        }
        var lines = request.Parameters!["lines"]!.Value<int>();
        var slider = CapacitySlider("/ExpandLines/ProductionLinesSlider");
        ValidateLineRange(slider, lines, "expansion");
        if (slider["value"]!.Value<int>() != lines) yield return GameUi.Set(slider, new JValue(lines));
        if (CapacitySlider("/ExpandLines/ProductionLinesSlider")["value"]!.Value<int>() != lines) throw new AgentError("value_not_applied", "Native expansion line count did not persist.");
    }

    private IEnumerable<Request> ConfigureSale(Request request)
    {
        var wafer = request.Parameters!["waferSize"]!.Value<string>()!;
        var dropdown = CapacityControl("Wafer Dropdown", "select");
        var option = WaferOption(dropdown, wafer);
        var current = dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>();
        if (current != option) yield return GameUi.Select(dropdown, option);
        dropdown = CapacityControl("Wafer Dropdown", "select");
        if (dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>() != option) throw new AgentError("value_not_applied", "Native sale wafer selection did not persist.");
        var lines = request.Parameters!["lines"]!.Value<int>();
        var slider = CapacitySlider("/ManageLines/ProductionLinesSlider");
        ValidateLineRange(slider, lines, "line sale");
        if (slider["value"]!.Value<int>() != lines) yield return GameUi.Set(slider, new JValue(lines));
        if (CapacitySlider("/ManageLines/ProductionLinesSlider")["value"]!.Value<int>() != lines) throw new AgentError("value_not_applied", "Native sale line count did not persist.");
    }

    private IEnumerable<Request> ProductionSettings(Request request)
    {
        var mappings = request.Command == "game.production-automation"
            ? new[] { (Key: "enabled", Control: "Automate Production") }
            : new[] { (Key: "foundryServices", Control: "Foundry Services"), (Key: "upgradeWafer", Control: "Upgrade Wafer"), (Key: "upgradeLines", Control: "Upgrade Wafer"), (Key: "automation", Control: "Automate Production") };
        foreach (var mapping in mappings)
        {
            if (request.Parameters?[mapping.Key] is not JToken value) continue;
            var wanted = value.Value<bool>();
            var toggle = ProductionControl(mapping.Control, "toggle");
            if ((bool)toggle["value"]! != wanted) yield return GameUi.Set(toggle, value);
            if ((bool)ProductionControl(mapping.Control, "toggle")["value"]! != wanted) throw new AgentError("value_not_applied", $"The native {mapping.Control} toggle did not retain {wanted}.");
        }
    }

    private IEnumerable<Request> ConfigureSales(Request request)
    {
        var changed = false;
        if (request.Parameters?["market"] is JToken market)
        {
            var dropdown = GameUi.Find(ui.Catalog(SalesScope), "Market Dropdown", "select");
            var selected = dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>();
            if (selected != market.Value<string>()) { changed = true; yield return GameUi.Select(dropdown, market.Value<string>()!); }
            dropdown = GameUi.Find(ui.Catalog(SalesScope), "Market Dropdown", "select");
            if (dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>() != market.Value<string>()) throw new AgentError("value_not_applied", "The native market filter did not retain the requested option.");
        }
        if (request.Parameters?["showRetired"] is JToken showRetired)
        {
            var toggle = GameUi.Find(ui.Catalog(SalesScope), "Show Retired", "toggle");
            if (!JToken.DeepEquals(toggle["value"], showRetired)) { changed = true; yield return GameUi.Set(toggle, showRetired); }
            if (!JToken.DeepEquals(GameUi.Find(ui.Catalog(SalesScope), "Show Retired", "toggle")["value"], showRetired)) throw new AgentError("value_not_applied", "The native Show Retired filter did not retain the requested value.");
        }
        if (request.Parameters?["variant"] is JToken variant)
        {
            var wanted = variant.Value<int>();
            var current = SalesVariant(ui.Catalog(SalesScope));
            if (current != wanted) yield return GameUi.Click(GameUi.Find(ui.Catalog(SalesScope), "Variant One", "button"));
            if (SalesVariant(ui.Catalog(SalesScope)) != wanted) throw new AgentError("value_not_applied", $"The native sales sheet did not switch to variant {wanted}.");
        }
        // The native sheet re-renders only when opened, at month end or on a filter change, so a sheet that was already
        // open still shows the prices/stock of its last render. Toggling Show Retired twice re-renders it with current values.
        var retiredToggles = GameUi.Controls(ui.Catalog(SalesScope)).Where(c => (string?)c["name"] == "Show Retired" && (string?)c["role"] == "toggle" && c["blockedReason"] == null).ToArray();
        if (salesWasOpen && !changed && !request.Hidden && retiredToggles.Length == 1)
        {
            var retired = retiredToggles[0];
            var original = retired["value"]!.Value<bool>();
            yield return GameUi.Set(retired, new JValue(!original));
            yield return GameUi.Set(GameUi.Find(ui.Catalog(SalesScope), "Show Retired", "toggle"), new JValue(original));
            if (GameUi.Find(ui.Catalog(SalesScope), "Show Retired", "toggle")["value"]!.Value<bool>() != original) throw new AgentError("value_not_applied", "Refreshing the sales sheet did not restore the native Show Retired filter.");
        }
        if (request.Parameters?["sortBy"] is JToken sortBy)
        {
            var header = GameUi.One(GameUi.Controls(ui.Catalog(SalesScope)).Where(c => (string?)c["name"] == "Spreadsheet Title Variant" && (string?)c["label"] == sortBy.Value<string>()), $"sales column '{sortBy}'");
            yield return GameUi.Click(header);
        }
        if (request.Parameters?["scroll"] is JToken scroll) yield return GameUi.Action("ui.scroll", GameUi.Find(ui.Read(SalesScope), "Sales Spreadsheet", "scroll"), scroll);
    }

    private IEnumerable<Request> ApplyProduct(Request request, ProductIdentity product)
    {
        var parameters = request.Parameters!;
        var newName = (string?)parameters["name"] ?? product.Name;
        foreach (var step in ApplyProductDraft(request)) yield return step;
        yield return GameUi.Click(EditControl("Confirm", "button"));
        if (GameUi.Controls(ui.Read(EditScope)).Length > 0) throw new AgentError("outcome_unverified", "Native Confirm was dispatched but the CPU editor remained open. Inspect the visible form or dialog.");

        var rows = ProductIdentities(ui.Catalog(ProductionScope)).Where(row => row.Group == product.Group).ToArray();
        if ((bool?)parameters["retire"] == true)
        {
            if (rows.Length != 0) throw new AgentError("outcome_unverified", $"Native Confirm closed, but selected rendered row {product.Ref} still exists after retirement.");
            yield break;
        }
        if (rows.Length != 1 || rows[0].Name != newName) throw new AgentError("outcome_unverified", $"Native Confirm closed, but selected rendered row {product.Ref} did not retain name '{newName}'.");
        selectedProduct = product = rows[0];
        foreach (var step in OpenProduct(request, product, targetWasRef: true)) yield return step;
        foreach (var mapping in new[] { (Key: "name", Control: "Name Input"), (Key: "price", Control: "Price Input Field"), (Key: "sellOnMarket", Control: "Sell On Market"), (Key: "availableForContracts", Control: "Available For Contracts") })
        {
            if (parameters[mapping.Key] is not JToken value) continue;
            var role = mapping.Key is "name" or "price" ? "input" : "toggle";
            var actual = EditControl(mapping.Control, role)["value"]!;
            var matches = role == "input" ? actual.ToString() == value.ToString() : JToken.DeepEquals(actual, value);
            if (!matches) throw new AgentError("value_not_applied", $"After native Confirm, {mapping.Key} did not persist as requested on {product.Ref}.");
        }
        yield return GameUi.Click(EditControl("Cancel", "button"));
    }

    private IEnumerable<Request> ApplyProductDraft(Request request)
    {
        var parameters = request.Parameters ?? new JObject();
        foreach (var mapping in new[] { (Key: "name", Control: "Name Input"), (Key: "price", Control: "Price Input Field") })
        {
            if (parameters[mapping.Key] is not JToken value) continue;
            var control = EditControl(mapping.Control, "input");
            if (control["value"]!.ToString() != value.ToString()) yield return GameUi.Set(control, value);
            if (EditControl(mapping.Control, "input")["value"]!.ToString() != value.ToString()) throw new AgentError("value_not_applied", $"Native validation changed {mapping.Key}; Confirm was not pressed.");
        }
        foreach (var mapping in new[] { (Key: "sellOnMarket", Control: "Sell On Market"), (Key: "availableForContracts", Control: "Available For Contracts"), (Key: "retire", Control: "Retire") })
        {
            if (parameters[mapping.Key] is not JToken value) continue;
            var control = EditControl(mapping.Control, "toggle");
            if (!JToken.DeepEquals(control["value"], value)) yield return GameUi.Set(control, value);
            if (!JToken.DeepEquals(EditControl(mapping.Control, "toggle")["value"], value)) throw new AgentError("value_not_applied", $"The native {mapping.Control} toggle did not retain the requested value; Confirm was not pressed.");
        }
    }

    private IEnumerable<Request> OpenProduct(Request request, ProductIdentity product, bool targetWasRef)
    {
        var editor = ui.Read(EditScope);
        if (GameUi.Controls(editor).Length > 0)
        {
            var openName = EditorProduct(editor);
            if (request.Command == "game.product-preview") throw new AgentError("conflicting_window", $"The CPU editor for '{openName}' is already open. Preview refused to overwrite or cancel that existing draft. Run game window-close CpuEditWindow after deciding what to do with it, then retry.");
            var sameNameRows = ProductIdentities(ui.Catalog(ProductionScope)).Count(row => row.Name == product.Name);
            if (openName != product.Name || targetWasRef || sameNameRows != 1) throw new AgentError("conflicting_window", $"The open CPU editor cannot be proven to belong to rendered row {product.Ref}. Close or cancel it, then retry the exact product target.");
            yield break;
        }
        if (request.Hidden) throw new AgentError("hidden_not_supported", $"{request.Command} must open the visible editor for '{product.Name}'. Retry without --hidden.");
        yield return GameUi.Click(ProductControl(product, "button"));
        editor = ui.Read(EditScope);
        if (GameUi.Controls(editor).Length == 0 || EditorProduct(editor) != product.Name) throw new AgentError("outcome_unverified", $"The native edit action did not open the editor for rendered product {product.Ref}.");
    }

    private JObject ProductionControl(string name, string role) => GameUi.One(GameUi.Controls(ui.Read(ProductionScope)).Where(c => (string?)c["name"] == name && (string?)c["role"] == role), name);
    private JObject EditControl(string name, string role) => GameUi.One(GameUi.Controls(ui.Read(EditScope)).Where(c => (string?)c["name"] == name && (string?)c["role"] == role), name);
    private JObject CapacityControl(string name, string role) => GameUi.One(GameUi.Controls(ui.Read(CapacityScope)).Where(c => (string?)c["name"] == name && (string?)c["role"] == role), name);
    private JObject CapacitySlider(string contextSuffix) => GameUi.One(GameUi.Controls(ui.Read(CapacityScope)).Where(c => (string?)c["role"] == "slider" && ((string?)c["context"] ?? "").EndsWith(contextSuffix, StringComparison.Ordinal)), "production-lines slider");

    private JObject ProductControl(ProductIdentity product, string role)
    {
        var view = ui.Catalog(ProductionScope);
        var current = ProductIdentities(view).Where(row => row.Group == product.Group && row.Ref == product.Ref).ToArray();
        if (current.Length == 0) throw new AgentError("stale_reference", $"Rendered product reference '{product.Ref}' is no longer present. Re-run product-list.");
        if (current.Length != 1) throw new AgentError("game_ui_mismatch", $"Rendered product reference '{product.Ref}' matched several rows.");
        return GameUi.One(GameUi.Controls(view).Where(c => (string?)c["group"] == product.Group && (string?)c["role"] == role), $"{role} in production row {product.Ref}");
    }

    private JObject Production()
    {
        var view = ui.Read(ProductionScope);
        var catalog = ui.Catalog(ProductionScope);
        var summary = new JObject();
        foreach (var name in summaryNames)
        {
            var values = GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains("/" + name, StringComparison.Ordinal)).Select(t => (string?)t["text"]).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToArray();
            if (values.Length > 0) summary[LowerFirst(name)] = string.Join(" | ", values);
        }
        var automation = ProductionControl("Automate Production", "toggle");
        var settings = new JObject();
        foreach (var (key, name) in new[] { ("upgradeLines", "Upgrade Wafer"), ("foundryServices", "Foundry Services"), ("automation", "Automate Production") })
        {
            var toggle = GameUi.Controls(view).FirstOrDefault(c => (string?)c["name"] == name && (string?)c["role"] == "toggle");
            settings[key] = toggle == null ? JValue.CreateNull() : new JObject { ["value"] = toggle["value"]!.DeepClone(), ["label"] = toggle["label"]?.DeepClone(), ["enabled"] = toggle["blockedReason"] == null };
        }
        var products = ProductRows(catalog, automation);
        return new JObject
        {
            ["method"] = "game", ["playerBrowsableCatalog"] = true, ["summary"] = summary, ["lines"] = SafeLines(summary, products),
            ["automation"] = new JObject { ["enabled"] = automation["value"]!.DeepClone(), ["blockedReason"] = automation["blockedReason"]?.DeepClone() },
            ["settings"] = settings,
            ["products"] = products, ["catalogComplete"] = !((bool?)catalog["more"] ?? false), ["precision"] = "native-display",
            ["productIndicatorSemantics"] = "Production, Demand, Stock and Balance are current native production-row indicators. They are not completed-period sales.",
            ["linesSemantics"] = "productionLines = total lines producing the CPU (manual + contract). manualLines = your own (non-contract, market) allocation: the Production slider value that product-production --lines sets. contractLines = lines the game dedicates to active contracts for that CPU; --lines never sets them. The slider accepts 0..(manual lines + unassigned lines).",
            ["next"] = "Use a unique exact name, or productRef when names repeat, with product-read, product-preview, product-production, product-price or product-set."
        };
    }

    // time-advance includeProduction: the rows of an already-open Production window, read right after the advance
    // (never navigating or opening anything), so a day-by-day watch needs one bridge call per day.
    internal static JObject OpenProductionSnapshot(GenericUi native)
    {
        try
        {
            var api = new ProductionGameApi(native);
            if (GameUi.Controls(api.ui.Read(ProductionScope)).Length == 0) return new JObject { ["available"] = false, ["reason"] = "Production is not open; nothing was opened during the advance." };
            var result = WithDateAndFinances(api.Production());
            result["available"] = true;
            return result;
        }
        catch (Exception error) { return new JObject { ["available"] = false, ["reason"] = error.Message }; }
    }

    // production-read also carries the native date and the desktop finance line, so one read serves a multi-product pulse.
    private static JObject WithDateAndFinances(JObject production)
    {
        production["date"] = TimeAdvanceController.ReadClock()["date"]!.DeepClone();
        production["finances"] = PulseFinances();
        return production;
    }

    private JObject ProductPulse(ProductIdentity product)
    {
        var row = ProductRows(ui.Catalog(ProductionScope)).OfType<JObject>().SingleOrDefault(item => (string?)item["productRef"] == product.Ref) ?? throw new AgentError("stale_reference", $"Product row {product.Ref} disappeared before its pulse could be read.");
        var displays = row["display"]!.Values<string>().Where(text => text != null).Select(text => text!).ToArray();
        return new JObject
        {
            ["method"] = "game", ["kind"] = "product_pulse", ["date"] = TimeAdvanceController.ReadClock()["date"]!.DeepClone(),
            ["product"] = new JObject { ["name"] = product.Name, ["productRef"] = product.Ref, ["productionLines"] = row["productionLines"]!.DeepClone(), ["manualLines"] = row["manualLines"]?.DeepClone(), ["contractLines"] = row["contractLines"]?.DeepClone(),
                ["requestedTotalLines"] = row["requestedTotalLines"]?.DeepClone(), ["capacityLimited"] = row["capacityLimited"]?.DeepClone(), ["availableRange"] = row["availableRange"]?.DeepClone(),
                ["balance"] = UnitIndicator(displays, "Balance", "day"), ["demand"] = UnitIndicator(displays, "Demand", "month"),
                ["production"] = UnitIndicator(displays, "Production", "month"), ["stock"] = UnitIndicator(displays, "Stock", null) },
            ["finances"] = PulseFinances(), ["precision"] = "native-display-rounded",
            ["next"] = "Demand and production are current native rates, not completed sales. A changed price may require a game day before demand reflects it."
        };
    }

    // Compact typed desktop finance values shared by product-pulse and production-read.
    private static JObject PulseFinances()
    {
        var finances = BalanceSnapshot.Read();
        var money = new JObject { ["available"] = finances["available"]!.DeepClone() };
        if ((bool)finances["available"]!)
        {
            var items = finances["items"]!.OfType<JObject>().ToDictionary(item => (string)item["key"]!, StringComparer.Ordinal);
            money["cash"] = TypedMoneyDisplay((string?)finances["cashDisplay"]);
            money["balance"] = TypedMoneyDisplay((string?)finances["balanceDisplay"]);
            foreach (var key in new[] { "credit", "sales", "interest", "production", "research" }) money[key] = TypedMoneyDisplay((string?)items[key]["display"]);
            money["default"] = finances["default"]!.DeepClone();
        }
        else money["reason"] = finances["reason"]?.DeepClone();
        return money;
    }

    private static JObject UnitIndicator(string[] displays, string name, string? period)
    {
        var values = displays.Select(text => Regex.Match(text, $@"^{Regex.Escape(name)}:\s*\|\s*(.+)$")).Where(match => match.Success).Select(match => match.Groups[1].Value.Trim()).Distinct().ToArray();
        if (values.Length != 1) return new JObject { ["available"] = false, ["reason"] = $"Expected one native {name} indicator, found {values.Length}." };
        var display = values[0];
        // Out of stock is reported as 0 units (flagged), so stock guards and summaries need no special case.
        if (name == "Stock" && display.StartsWith("Out of Stock", StringComparison.OrdinalIgnoreCase)) return new JObject { ["available"] = true, ["value"] = 0, ["outOfStock"] = true, ["unit"] = "units", ["display"] = display };
        var match = Regex.Match(display, @"^(-?[\d,]+(?:\.\d+)?)([KMBT]?)(?:/(m|d))?$");
        if (!match.Success || (period == "month" && match.Groups[3].Value != "m") || (period == "day" && match.Groups[3].Value != "d") || (period == null && match.Groups[3].Success))
            return new JObject { ["available"] = false, ["reason"] = "Native indicator format was not recognized.", ["display"] = display };
        var multiplier = match.Groups[2].Value switch { "K" => 1e3m, "M" => 1e6m, "B" => 1e9m, "T" => 1e12m, _ => 1m };
        var value = (double)(decimal.Parse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture) * multiplier);
        return new JObject { ["available"] = true, ["value"] = value, ["unit"] = "units", ["period"] = period, ["display"] = display };
    }

    private static JObject TypedMoneyDisplay(string? display)
    {
        var typed = MarketGameApi.TypedDisplay(display ?? "");
        return typed is JObject money && money["value"]?.Type is JTokenType.Integer or JTokenType.Float ? new JObject { ["available"] = true, ["value"] = money["value"]!.DeepClone(), ["display"] = display, ["period"] = money["period"]?.DeepClone() } : new JObject { ["available"] = false, ["display"] = display, ["reason"] = "Native money display was not a recognized amount." };
    }

    private JObject ProductionHistory(Request request)
    {
        var view = ui.Read(AnalysisScope);
        var dropdown = GameUi.Find(view, "Companies Dropdown", "select");
        var company = dropdown["options"]![dropdown["value"]!.Value<int>()]!.Value<string>();
        var yearly = ProductionHistoryYearly(view);
        var latest = new JArray(GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains("/ProductionGraphWindow/LegendTotal/", StringComparison.Ordinal)).Select(t =>
        {
            var cells = ((string)t["text"]!).Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length != 2) throw new AgentError("game_ui_mismatch", "A native production-history legend row does not contain a name and value.");
            return new JObject { ["name"] = cells[0], ["value"] = cells[1] };
        }));
        var history = MarketGameApi.ProductionHistory(yearly ? "year" : "month");
        return new JObject
        {
            ["method"] = "game", ["company"] = company,
            ["companySelection"] = request.Parameters?["company"] == null ? "player-default" : "requested-exact",
            ["companyOptions"] = dropdown["options"]!.DeepClone(), ["period"] = yearly ? "yearly" : "monthly",
            ["latestVisible"] = latest, ["history"] = history, ["seriesAvailable"] = (bool?)history["available"] ?? false,
            ["precision"] = "native-rendered-float-history-and-display-legend",
            ["limit"] = "History contains only the currently active native Production graph render range. Monthly points use the native relative-period axis; yearly points are native averages of complete 12-month groups."
        };
    }

    // Project cards are a secondary expansion postcondition; an unreadable side window must not block the expansion itself.
    private static IReadOnlyDictionary<int, ResearchGameApi.ProjectState> SafeProjectStates()
    {
        try { return ResearchGameApi.ProjectStates(); }
        catch (Exception) { return new Dictionary<int, ResearchGameApi.ProjectState>(); }
    }

    private JObject WithVerification(JObject result)
    {
        if (expansionVerification != null) result["verification"] = expansionVerification;
        return result;
    }

    private JObject Capacity()
    {
        var numbers = CapacityNumbers();
        var view = ui.Read(CapacityScope);
        var slider = CapacitySlider("/ExpandLines/ProductionLinesSlider");
        var wafer = CapacityControl("Wafer Dropdown", "select");
        var build = CapacityControl("Expand", "button");
        var details = new JObject();
        foreach (var pair in new[] { (Key: "production", Suffix: "/Production"), (Key: "maintenanceCost", Suffix: "/OperatingCost"), (Key: "monthlyCost", Suffix: "/MonthlyCost"), (Key: "totalCost", Suffix: "/TotalCost"), (Key: "constructionTime", Suffix: "/ConstructionTime"), (Key: "finishDate", Suffix: "/ConclusionDate") }) details[pair.Key] = CapacityText(view, pair.Suffix);
        var days = Regex.Match((string)details["constructionTime"]!, @"([\d,]+)\s+days?");
        return new JObject
        {
            ["method"] = "game", ["currentLines"] = numbers.Current, ["pendingExpansionLines"] = numbers.Pending, ["capacityDisplay"] = numbers.Display, ["currentLinesExact"] = numbers.Exact,
            ["expansion"] = new JObject
            {
                ["lines"] = slider["value"]!.DeepClone(), ["lineRange"] = new JObject { ["min"] = slider["range"]!["rawMin"]!.DeepClone(), ["max"] = slider["range"]!["rawMax"]!.DeepClone() },
                ["waferSize"] = wafer["options"]![wafer["value"]!.Value<int>()]!.DeepClone(), ["waferSizes"] = wafer["options"]!.DeepClone(), ["details"] = details,
                ["detailTooltips"] = NativeTooltips.Under("ExpandLines/DetailsTexts"),
                ["productionNote"] = "The native Production figure is one line's monthly output for the reference die in detailTooltips (not your CPU and not per day). Output per line scales roughly inversely with die area and with yield; once a CPU is assigned lines, production-read shows its real monthly production.",
                ["constructionDays"] = days.Success ? new JValue(int.Parse(days.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture)) : JValue.CreateNull(), ["buildEnabled"] = build["blockedReason"] == null, ["blockedReason"] = build["blockedReason"]?.DeepClone()
            },
            ["commits"] = false, ["next"] = "Use production-expansion-preview to change the draft or production-expand for the native timed project."
        };
    }

    private JObject LineSale()
    {
        var numbers = CapacityNumbers();
        var view = ui.Read(CapacityScope);
        var slider = CapacitySlider("/ManageLines/ProductionLinesSlider");
        var wafer = CapacityControl("Wafer Dropdown", "select");
        var sell = CapacityControl("Sell", "button");
        var incomeDisplay = CapacityText(view, "/ManageLines/Income");
        return new JObject
        {
            ["method"] = "game", ["currentLines"] = numbers.Current, ["pendingExpansionLines"] = numbers.Pending, ["capacityDisplay"] = numbers.Display,
            ["sale"] = new JObject
            {
                ["lines"] = slider["value"]!.DeepClone(), ["lineRange"] = new JObject { ["min"] = slider["range"]!["rawMin"]!.DeepClone(), ["max"] = slider["range"]!["rawMax"]!.DeepClone() },
                ["waferSize"] = WaferName(wafer["options"]![wafer["value"]!.Value<int>()]!.Value<string>()!), ["waferInventory"] = new JArray(wafer["options"]!.Values<string>().Where(value => value != null).Select(value => value!)),
                ["incomeDisplay"] = incomeDisplay, ["income"] = TypedMoney(incomeDisplay, "line-sale income"), ["sellEnabled"] = sell["blockedReason"] == null, ["blockedReason"] = sell["blockedReason"]?.DeepClone()
            },
            ["divisionSale"] = new JObject { ["supported"] = true, ["confirmationRequired"] = true, ["previewCommand"] = "game production-division-sale-preview", ["commitCommand"] = "game production-sell-division EXACT_MANUFACTURER" }, ["precision"] = "display-rounded",
            ["commits"] = false, ["next"] = "Use production-line-sale-preview to change the draft or production-sell-lines for the irreversible native sale."
        };
    }

    private JObject DivisionConfirmation()
    {
        var view = ui.Read(FablessScope);
        var dropdown = GameUi.Find(view, "Manufacturer Dropdown", "select");
        var sale = GameUi.One(GameUi.Controls(ui.Catalog(CapacityScope)).Where(c => (string?)c["name"] == "Sell Division" && (string?)c["role"] == "button"), "underlying native Sell Division value");
        var capacity = CapacityNumbers();
        var saleValueDisplay = sale["label"]!.Value<string>()!.Replace("Sell Division:", "").Trim();
        return new JObject
        {
            ["method"] = "game", ["confirmationRequired"] = true, ["irreversible"] = true,
            ["saleValueDisplay"] = saleValueDisplay, ["saleValue"] = TypedMoney(saleValueDisplay, "division-sale value"), ["precision"] = "display-rounded",
            ["currentLines"] = capacity.Current, ["pendingExpansionLines"] = capacity.Pending,
            ["manufacturer"] = dropdown["options"]![dropdown["value"]!.Value<int>()]!.DeepClone(), ["manufacturers"] = dropdown["options"]!.DeepClone(),
            ["message"] = new JArray(GameUi.Texts(view).Where(t => !((string?)t["context"] ?? "").EndsWith("/TopBar", StringComparison.Ordinal)).Select(t => t["text"]!.DeepClone())),
            ["warning"] = capacity.Pending > 0 ? "A native factory expansion is already pending; selling the division does not claim to cancel that project." : null,
            ["commits"] = false, ["next"] = "Use production-sell-division EXACT_MANUFACTURER only if changing to Fabless and selling all manufacturing lines is intended. Use target None to retain current CPU manufacturers."
        };
    }

    private JObject DivisionSold(string provider)
    {
        var capacity = CapacityNumbers();
        var rows = ProductRows(ui.Catalog(ProductionScope));
        var providerVisible = provider != "None" && rows.OfType<JObject>().SelectMany(row => row["display"]!.Values<string>()).Any(text => text != null && text.EndsWith(", " + provider, StringComparison.Ordinal));
        var wafer = ProductionText("/ProductionInfo/Info 2/WaferSize");
        return new JObject
        {
            ["method"] = "game", ["outcome"] = "manufacturing_division_sold", ["commits"] = true, ["companyType"] = "Fabless",
            ["currentLines"] = capacity.Current, ["pendingExpansionLines"] = capacity.Pending, ["capacityDisplay"] = capacity.Display,
            ["manufacturerSelected"] = provider, ["manufacturerDisplayVerified"] = providerVisible, ["waferDisplay"] = wafer, ["fablessDisplaySettled"] = wafer.Contains("n/a", StringComparison.OrdinalIgnoreCase), ["precision"] = "native-visible-postcondition",
            ["next"] = "Manufacturing capacity is zero. Use production-capacity to inspect the native fixed 50-line Build Manufacturing Division project, or arrange production through the selected provider."
        };
    }

    // Capacity from the native summary ("Production lines: | 1.15K (+50)"). Above 999 lines the display is abbreviated;
    // Exact is then taken from the desktop Production icon ("used/capacity") when it is rendered, otherwise Exact=false.
    private (int Current, int Pending, string Display, bool Exact) CapacityNumbers()
    {
        var texts = GameUi.Texts(ui.Catalog(ProductionScope)).Where(t => ((string?)t["context"] ?? "").EndsWith("/ProductionInfo/Info/ProductionLines", StringComparison.Ordinal)).Select(t => (string)t["text"]!).Distinct().ToArray();
        if (texts.Length != 1) throw new AgentError("game_ui_mismatch", "Expected one native Production lines summary.");
        var token = DisplayNumber.FieldToken(texts[0]) ?? throw new AgentError("game_ui_mismatch", $"Cannot parse native capacity display '{texts[0]}'.");
        var pending = DisplayNumber.Pending(texts[0]) ?? 0;
        if (!token.Abbreviated) return ((int)Math.Round(token.Value), pending, texts[0], true);
        var desktop = DesktopLines();
        if (desktop != null && Math.Abs(desktop.Value.Capacity - token.Value) <= token.Precision + 0.5) return (desktop.Value.Capacity, pending, texts[0], true);
        return ((int)Math.Round(token.Value), pending, texts[0], false);
    }

    // The desktop Production icon shows the exact "used/capacity" (not abbreviated) once CPUs exist.
    private (int Used, int Capacity)? DesktopLines()
    {
        try
        {
            var view = ui.Read("DesktopButtons");
            var labels = GameUi.Controls(view).Where(c => (string?)c["name"] == "Production Desktop").Select(c => (string?)c["label"])
                .Concat(GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains("Production Desktop")).Select(t => (string?)t["text"]));
            var parsed = labels.Select(LineAccounting.DesktopLabel).Where(v => v != null).Distinct().ToArray();
            return parsed.Length == 1 ? parsed[0] : null;
        }
        catch (Exception) { return null; }
    }

    private JObject SafeLines(JObject summary, JArray products)
    {
        try { return LinesJson(summary, products, DesktopLines()); }
        catch (Exception error) { return new JObject { ["available"] = false, ["reason"] = error.Message }; }
    }

    internal static JObject LinesJson(JObject summary, JArray products, (int Used, int Capacity)? desktop)
    {
        var rows = products.OfType<JObject>().Select(p => new LineAccounting.Row((string?)p["name"] ?? "", (int?)p["requestedTotalLines"] ?? 0, (int?)p["productionLines"] ?? 0, (int?)p["manualLines"], (int?)p["availableRange"]?["maxLines"])).ToList();
        var r = LineAccounting.Compute((string?)summary["productionLines"], (string?)summary["linesUsed"], (string?)summary["usedByContracts"], (string?)summary["usedByClients"], rows, desktop);
        return new JObject
        {
            ["capacity"] = r.Capacity, ["used"] = r.Used, ["unassigned"] = r.Unassigned, ["pendingExpansion"] = r.Pending, ["contracts"] = r.Contracts, ["clients"] = r.Clients, ["overAssigned"] = r.OverAssigned,
            ["rowsRequestedTotal"] = r.RowsRequested, ["rowsEffectiveTotal"] = r.RowsEffective,
            ["exact"] = new JObject { ["capacity"] = r.CapacityExact, ["used"] = r.UsedExact, ["unassigned"] = r.UnassignedExact }, ["basis"] = r.Basis,
            ["display"] = new JObject { ["capacity"] = r.CapacityDisplay, ["capacityPrecision"] = r.CapacityPrecision, ["used"] = r.UsedDisplay, ["usedPrecision"] = r.UsedPrecision },
            ["consistent"] = r.Consistent, ["problems"] = r.Problems.Count > 0 ? new JArray(r.Problems) : null,
            ["note"] = "Totals above 999 are abbreviated in the native summary (1.15K = 1,145..1,155). unassigned is exact from the product sliders (maxLines - manualLines) or the desktop icon; consistent checks product rows against the displayed totals."
        };
    }

    private int PendingExpansionLines() => CapacityNumbers().Pending;

    private string ProductionText(string suffix)
    {
        var matches = GameUi.Texts(ui.Catalog(ProductionScope)).Where(t => ((string?)t["context"] ?? "").EndsWith(suffix, StringComparison.Ordinal)).Select(t => (string)t["text"]!).Distinct().ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one Production field at {suffix}.");
        return matches[0];
    }

    private int WaferLineCount(string wafer)
    {
        var dropdown = CapacityControl("Wafer Dropdown", "select");
        var option = WaferOption(dropdown, wafer);
        var match = Regex.Match(option, @"\(([\d,]+)\)$");
        if (!match.Success) throw new AgentError("game_ui_mismatch", $"Cannot parse line inventory from '{option}'.");
        return int.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
    }

    // Same speed bump as cpu-develop: a construction project that would take Available Credit below zero (or start while in default) needs explicit acknowledgement.
    private void ExpansionRiskGuard(Request request)
    {
        var view = ui.Read(CapacityScope);
        var monthly = BalanceSnapshot.Money(CapacityText(view, "/MonthlyCost"));
        var days = Regex.Match(CapacityText(view, "/ConstructionTime"), @"([\d,]+)\s+days?");
        var finances = BalanceSnapshot.Read();
        if ((bool?)finances["available"] != true || monthly == null || !days.Success) return;
        // Available Credit excludes positive cash; spendable headroom before default is credit + cash on hand.
        var credit = (BalanceSnapshot.Money((string?)finances["availableCredit"]) ?? 0m) + Math.Max(0m, BalanceSnapshot.Money((string?)finances["cashDisplay"]) ?? 0m);
        var balance = BalanceSnapshot.Money((string?)finances["balanceDisplay"]) ?? 0m;
        var burn = Math.Max(0m, Math.Abs(monthly.Value) - balance) * int.Parse(days.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) / 30m;
        var risks = new List<string>();
        if (burn > credit) risks.Add("baseline_credit_shortfall");
        if ((bool?)finances["default"]?["active"] == true) risks.Add("company_in_default");
        var acknowledged = ((string?)request.Parameters?["acknowledgeRisks"] ?? "").Split(',').Select(k => k.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = risks.Where(k => !acknowledged.Contains(k)).ToArray();
        if (missing.Length > 0) throw new AgentError("risk_acknowledgement_required", $"Expansion risks: {string.Join(", ", risks)}. At the current monthly balance, construction would consume about {(burn / 1000000m).ToString("0.00", CultureInfo.InvariantCulture)}M against {(credit / 1000000m).ToString("0.00", CultureInfo.InvariantCulture)}M of Available Credit plus cash on hand, leaving no reserve if demand for your products falls (for example when rivals release a stronger generation). Reconsider size and timing; to build anyway add --acknowledge-risks {string.Join(",", risks)}. No Expand input was sent.");
    }

    private static string CapacityText(JObject view, string suffix)
    {
        var matches = GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").EndsWith(suffix, StringComparison.Ordinal)).Select(t => (string)t["text"]!).Distinct().ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one capacity field at {suffix}.");
        return matches[0];
    }

    private static void ValidateLineRange(JObject slider, int lines, string operation)
    {
        var range = slider["range"]!;
        if (!(bool)range["wholeNumbers"]! || lines < range["rawMin"]!.Value<int>() || lines > range["rawMax"]!.Value<int>()) throw new AgentError("invalid_value", $"Native {operation} currently accepts {range["rawMin"]}-{range["rawMax"]} lines. No input dispatched.");
        if (slider["blockedReason"] != null && slider["value"]!.Value<int>() != lines) throw new AgentError("not_interactable", $"Native {operation} fixes the line count at {slider["value"]}; requested {lines}.");
    }

    private static string WaferOption(JObject dropdown, string wafer)
    {
        var matches = dropdown["options"]!.Values<string>().Where(option => option != null && WaferName(option) == wafer).Select(option => option!).ToArray();
        if (matches.Length != 1) throw new AgentError(matches.Length == 0 ? "invalid_value" : "ambiguous_target", $"Expected one wafer size '{wafer}' in current native options.");
        return matches[0];
    }

    private static string WaferName(string option)
    {
        var marker = option.LastIndexOf(" (", StringComparison.Ordinal);
        return marker < 0 ? option : option.Substring(0, marker);
    }

    private sealed class ProductIdentity
    {
        public string Name = "";
        public string Group = "";
        public string Ref = "";
    }

    internal static HashSet<string> SnapshotProductRowRefs() => RenderedProductRows().Select(row => row.Ref).ToHashSet(StringComparer.Ordinal);

    private ProductIdentity ResolveProduct(string target)
    {
        var rows = ProductIdentities(ui.Catalog(ProductionScope));
        if (target.StartsWith(ProductRefPrefix, StringComparison.Ordinal))
        {
            if (!Regex.IsMatch(target, @"^product-ui:[0-9a-f]{8}:-?\d+:-?\d+$", RegexOptions.CultureInvariant)) throw new AgentError("invalid_target", $"Malformed productRef '{target}'. Use an exact current productRef from product-list.");
            var referenced = rows.Where(row => row.Ref == target).ToArray();
            if (referenced.Length == 0) throw new AgentError("stale_reference", $"Product reference '{target}' is not present in the current scene. Re-run product-list; no name fallback was attempted.");
            if (referenced.Length != 1) throw new AgentError("game_ui_mismatch", $"Product reference '{target}' matched several rendered rows.");
            return referenced[0];
        }
        var matches = rows.Where(row => row.Name == target).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No production row has exact product name '{target}'. Use game product-list; it may be unreleased or retired.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several production rows are named '{target}'. Use one exact productRef returned by product-list: {string.Join(", ", matches.Select(row => row.Ref))}.");
        return matches[0];
    }

    private static ProductIdentity[] ProductIdentities(JObject view)
    {
        var rendered = RenderedProductRows().ToDictionary(row => row.Group, StringComparer.Ordinal);
        var result = new List<ProductIdentity>();
        foreach (var group in GameUi.Texts(view).Where(t => t["group"] != null && ((string?)t["context"] ?? "").EndsWith("/ProductionUI(Clone)", StringComparison.Ordinal)).GroupBy(t => (string)t["group"]!))
        {
            var names = group.Select(t => FirstCell((string?)t["text"])).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToArray();
            if (names.Length != 1 || !rendered.TryGetValue(group.Key, out var row)) throw new AgentError("game_ui_mismatch", $"Rendered production group '{group.Key}' could not be mapped to exactly one visible name and ProductionUI clone.");
            row.Name = names[0]!;
            result.Add(row);
        }
        return result.ToArray();
    }

    private static ProductIdentity[] RenderedProductRows() => RenderedProductComponents().Select(component => new ProductIdentity
    {
        Group = "g" + component.transform.GetInstanceID(),
        Ref = $"{ProductRefPrefix}{ProductRefSession}:{component.gameObject.scene.handle}:{component.transform.GetInstanceID()}"
    }).ToArray();

    private static MonoBehaviour[] RenderedProductComponents() => Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == "ProcessorTycoon.Production.UI.ProductionUI" && component.gameObject.scene.IsValid() && UnderProductionWindow(component.transform)).ToArray();

    private static bool UnderProductionWindow(Transform transform)
    {
        for (var current = transform.parent; current != null; current = current.parent) if (current.GetComponents<MonoBehaviour>().Any(component => component != null && component.GetType().FullName == "ProcessorTycoon.Production.UI.ProductionWindow")) return true;
        return false;
    }

    private static JArray ProductRows(JObject view, JObject? automation = null)
    {
        var result = new JArray();
        var identities = ProductIdentities(view).ToDictionary(row => row.Group, StringComparer.Ordinal);
        foreach (var slider in GameUi.Controls(view).Where(c => (string?)c["role"] == "slider" && c["group"] != null))
        {
            var group = (string)slider["group"]!;
            if (!identities.TryGetValue(group, out var identity)) throw new AgentError("game_ui_mismatch", $"Production slider group '{group}' has no rendered product identity.");
            var rowTexts = GameUi.Texts(view).Where(t => (string?)t["group"] == group).ToArray();
            var texts = rowTexts.Select(t => (string)t["text"]!).Where(t => t.Length > 0).Distinct().ToArray();
            var allocation = DisplayedProductionLines(slider);
            var manualAvailable = slider["blockedReason"] == null;
            // The slider value is the product-production --lines value; the displayed total adds lines dedicated to contracts.
            int? manualLines = slider["value"]?.Type is JTokenType.Integer or JTokenType.Float ? (int)Math.Round(slider["value"]!.Value<double>()) : null;
            int? contractLines = manualLines != null && allocation.Requested >= manualLines ? allocation.Requested - manualLines : null;
            var manual = new JObject { ["available"] = manualAvailable };
            if (slider["blockedReason"] != null)
            {
                var recoverableAutomation = automation != null && (bool?)automation["value"] == true && automation["blockedReason"] == null;
                manual["reason"] = recoverableAutomation ? "automation_enabled" : slider["blockedReason"]!.DeepClone();
                if (recoverableAutomation) manual["recovery"] = new JObject { ["command"] = "game.production-settings", ["parameters"] = new JObject { ["automation"] = false } };
                else if (automation != null) manual["automation"] = new JObject { ["enabled"] = automation["value"]!.DeepClone(), ["blockedReason"] = automation["blockedReason"]?.DeepClone() };
            }
            result.Add(new JObject
            {
                ["productRef"] = identity.Ref, ["name"] = identity.Name, ["productionLines"] = allocation.Effective, ["effectiveLines"] = allocation.Effective, ["requestedTotalLines"] = allocation.Requested,
                ["capacityLimited"] = allocation.Requested != allocation.Effective, ["productionDisplay"] = slider["displayValue"]?.DeepClone(),
                ["manualLines"] = manualLines, ["contractLines"] = contractLines, ["linesBasis"] = manualLines == null ? "unknown" : "native_slider",
                ["availableRange"] = manualAvailable ? new JObject { ["minLines"] = slider["range"]?["rawMin"]?.DeepClone(), ["maxLines"] = slider["range"]?["rawMax"]?.DeepClone() } : null,
                ["manualAllocation"] = manual, ["blockedReason"] = slider["blockedReason"]?.DeepClone(), ["display"] = new JArray(texts)
            });
        }
        return result;
    }

    private static (int Requested, int Effective) DisplayedProductionLines(JObject slider)
    {
        var display = (string?)slider["displayValue"] ?? "";
        var match = Regex.Match(display, @"^\s*([\d,]+)(?:\s+\(([\d,]+)\))?\s+lines?\s+\(");
        if (!match.Success) throw new AgentError("game_ui_mismatch", $"Cannot read allocated production lines from native display '{display}'.");
        var requested = int.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        var effective = match.Groups[2].Success ? int.Parse(match.Groups[2].Value.Replace(",", ""), CultureInfo.InvariantCulture) : requested;
        return (requested, effective);
    }

    private JObject ProductEditor(ProductIdentity product)
    {
        var view = ui.Read(EditScope);
        if (EditorProduct(view) != product.Name) throw new AgentError("context_changed", $"The visible CPU editor no longer belongs to rendered row {product.Ref}.");
        var name = EditControl("Name Input", "input");
        var price = EditControl("Price Input Field", "input");
        var toggles = new JObject();
        foreach (var node in GameUi.Controls(view).Where(c => (string?)c["role"] == "toggle")) toggles[LowerFirst(((string)node["name"]!).Replace(" ", ""))] = node["value"]!.DeepClone();
        var unitCost = GameUi.Texts(view).Where(text => ((string?)text["context"] ?? "").EndsWith("/UnitCostAndPrice/UnitCost", StringComparison.Ordinal)).Select(text => (string?)text["text"]).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        if (unitCost.Length != 1) throw new AgentError("game_ui_mismatch", "Expected one native CPU editor Unit Cost display.");
        var segments = GameUi.Texts(view).SelectMany(text => ((string?)text["text"] ?? "").Split('|')).Select(text => text.Trim()).Where(text => text.Length > 0).ToArray();
        var storage = segments.Where(text => text.StartsWith("Storage:", StringComparison.Ordinal)).ToArray();
        var fines = segments.Where(text => text.StartsWith("Fines from contracts:", StringComparison.Ordinal)).ToArray();
        if (storage.Length > 1 || fines.Length > 1) throw new AgentError("game_ui_mismatch", "CPU retirement preview exposed duplicate native Storage/Fines displays.");
        var confirm = EditControl("Confirm", "button");
        var retiring = (bool?)toggles["retire"] == true;
        if (retiring && storage.Length != 1) throw new AgentError("game_ui_mismatch", "Native Retire mode did not expose one Storage sale display.");
        var retirement = retiring ? new JObject
        {
            ["storageSaleDisplay"] = storage[0], ["storageSale"] = TypedMoney(storage[0], "retirement storage sale"),
            ["contractFinesDisplay"] = fines.SingleOrDefault(), ["contractFines"] = fines.Length == 0 ? JValue.CreateNull() : TypedMoney(fines[0], "retirement contract fines")
        } : null;
        return new JObject
        {
            ["method"] = "game", ["productRef"] = product.Ref, ["name"] = name["value"]!.DeepClone(), ["price"] = int.Parse((string)price["value"]!), ["currency"] = "$",
            ["toggles"] = toggles, ["unitCostDisplay"] = unitCost[0], ["unitCost"] = TypedMoney(unitCost[0]!, "unit cost"), ["confirmMode"] = confirm["label"]!.DeepClone(),
            ["retirement"] = retirement, ["precision"] = "display-rounded",
            ["visibleEditorLeftOpen"] = true, ["next"] = $"Use game product-preview {product.Ref} for a cancelled quote, product-set to commit, or game window-close CpuEditWindow."
        };
    }

    private static JToken TypedMoney(string display, string field)
    {
        var tokens = Regex.Matches(display, @"(?:^|[\s|])([^\s|]*\$[^\s|]*)(?=$|[\s|])").Cast<Match>().Select(match => match.Groups[1].Value).Distinct().ToArray();
        if (tokens.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one unambiguous native money token for {field}, found {tokens.Length} in '{display}'.");
        var typed = MarketGameApi.TypedDisplay(tokens[0]);
        if (typed is not JObject money || (string?)money["currency"] != "$" || money["value"]?.Type is not (JTokenType.Integer or JTokenType.Float)) throw new AgentError("game_ui_mismatch", $"Native money token '{tokens[0]}' for {field} is truncated or unsupported; no numeric prefix was accepted.");
        return money;
    }

    private JObject Sales(Request request)
    {
        var view = ui.Catalog(SalesScope);
        var schema = EnsureSalesSchema(view);
        var rows = new JArray();
        foreach (var group in GameUi.Texts(view).Where(t => t["group"] != null && ((string?)t["context"] ?? "").Contains("/Spreadsheet/Row/Column", StringComparison.Ordinal)).GroupBy(t => (string)t["group"]!))
        {
            var records = group.Select(t => (string)t["text"]!).Where(t => t.Length > 0).ToArray();
            if (records.Length != 1) continue;
            var values = records[0].Split('|').Select(value => value.Trim()).ToArray();
            if (values.Length != schema.Length) continue;
            var cells = new JObject();
            for (var i = 0; i < schema.Length; i++) cells[LowerFirst(schema[i].Replace(" ", ""))] = values[i];
            rows.Add(cells);
        }
        var filters = new JArray(GameUi.Controls(view).Where(c => (string?)c["role"] is "toggle" or "select").Select(c => new JObject { ["name"] = c["name"]!.DeepClone(), ["value"] = c["value"]!.DeepClone(), ["options"] = c["options"]?.DeepClone() }));
        var result = new JObject
        {
            ["method"] = "game", ["playerBrowsableCatalog"] = true, ["catalogComplete"] = !((bool?)view["more"] ?? false),
            ["columns"] = new JArray(schema), ["rows"] = rows, ["filters"] = filters, ["precision"] = "native-display",
            ["columnSemantics"] = SalesColumnSemantics(schema),
            ["interpretation"] = "This native sheet mixes reporting periods by column. A zero prior-month Sold value does not describe current production throughput, demand or stock, and no causal explanation is inferred."
        };
        if (request.Parameters?.Count > 0) result["appliedView"] = request.Parameters.DeepClone();
        if (request.Parameters?["sortBy"] != null) result["sort"] = "native column order toggled once; direction is not exposed by the visible Generic UI";
        return result;
    }

    private static JObject SalesColumnSemantics(string[] schema)
    {
        var result = new JObject();
        foreach (var column in schema)
        {
            var key = LowerFirst(column.Replace(" ", ""));
            result[key] = column switch
            {
                "CPU" => "identity",
                "All Time" => "native cumulative sold history",
                "Highest" => "native historical maximum sold",
                "Sold" or "Missed Sales" or "Missed" or "Income" or "Profit" or "Server" or "Desktop" or "Mobile" or "Industries" or "High End" or "Mid Range" or "Low End" => "prior completed monthly report",
                "Stock" or "Unit Cost" or "Price" or "Popularity" => "current value when the native sheet rendered",
                _ => "native display"
            };
        }
        return result;
    }

    private static string[] EnsureSalesSchema(JObject view)
    {
        var headers = GameUi.Controls(view).Where(c => (string?)c["name"] == "Spreadsheet Title Variant" && (string?)c["role"] == "button").Select(c => (string)c["label"]!).ToArray();
        var matches = salesSchemas.Where(schema => headers.SequenceEqual(schema)).ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", "The visible Analysis sales table does not match a proven native column layout. Switch to Sales, expose all columns, or use Generic UI.");
        return matches[0];
    }

    private static int SalesVariant(JObject view)
    {
        var headers = GameUi.Controls(view).Where(c => (string?)c["name"] == "Spreadsheet Title Variant").Select(c => (string?)c["label"]).ToArray();
        if (headers.Contains("All Time")) return 1;
        if (headers.Contains("Highest")) return 2;
        throw new AgentError("game_ui_mismatch", "Cannot identify the visible native sales-sheet variant.");
    }

    private static bool ProductionGraphVisible(JObject view) => GameUi.Controls(view).Any(c => (string?)c["name"] == "Companies Dropdown" && ((string?)c["context"] ?? "").Contains("/ProductionTab/ProductionGraphWindow", StringComparison.Ordinal));

    private static bool ProductionHistoryYearly(JObject view)
    {
        var names = GameUi.Controls(view).Where(c => (string?)c["role"] == "button" && ((string?)c["context"] ?? "").Contains("/ProductionGraphWindow/Buttons", StringComparison.Ordinal)).Select(c => (string?)c["name"]).ToArray();
        if (names.Contains("Yearly")) return true;
        if (names.Contains("Monthly")) return false;
        throw new AgentError("game_ui_mismatch", "The active production-history period cannot be identified from the native Monthly/Yearly control.");
    }

    private static JObject AddOutcome(JObject result, string outcome, string? product, JToken value)
    {
        result["outcome"] = outcome;
        if (result["commits"] != null) result["commits"] = true;
        if (outcome == "expansion_started") result["next"] = "Expansion is scheduled. Inspect Projects or advance game time; do not repeat unless another expansion is intended.";
        if (outcome == "lines_sold") result["next"] = "Line sale is complete. Re-read capacity before any further irreversible sale.";
        if (product != null) result["product"] = product;
        result["appliedValue"] = value.DeepClone();
        return result;
    }

    private static string EditorProduct(JObject view)
    {
        var titles = GameUi.Texts(view).Select(t => (string?)t["text"]).Where(t => t != null && t.StartsWith("CPU - ", StringComparison.Ordinal)).Distinct().ToArray();
        if (titles.Length != 1) throw new AgentError("game_ui_mismatch", "Expected one visible 'CPU - NAME' editor title.");
        return titles[0]!.Substring(6);
    }

    private static void NoTarget(Request request)
    {
        if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", $"{request.Command} does not take a target.");
    }

    private static void NoValue(Request request)
    {
        if (request.Value != null) throw new AgentError("unsupported_parameter", $"{request.Command} does not use generic value. Use its named parameters.");
    }

    private static void ValidateBooleans(Request request, params string[] keys)
    {
        if (request.Parameters == null || request.Parameters.Count == 0) throw new AgentError("invalid_request", $"{request.Command} requires at least one named setting.");
        foreach (var key in keys) if (request.Parameters[key] != null && request.Parameters[key]!.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"--{key} must be boolean.");
    }

    private static void ValidateProductSet(Request request)
    {
        ValidateProductFields(request, requireAny: true);
    }

    private static void ValidateProductFields(Request request, bool requireAny)
    {
        if (requireAny && (request.Parameters == null || request.Parameters.Count == 0)) throw new AgentError("invalid_request", "game.product-set requires at least one product field.");
        var parameters = request.Parameters ?? new JObject();
        if (parameters["name"] is JToken name && (name.Type != JTokenType.String || string.IsNullOrWhiteSpace(name.Value<string>()) || name.Value<string>()!.Length > 20)) throw new AgentError("invalid_value", "--name must be a non-empty string of at most 20 characters.");
        if (parameters["price"] != null) RequireInteger(request, "price", 1, 9999);
        foreach (var key in new[] { "retire", "sellOnMarket", "availableForContracts" }) if (parameters[key] != null && parameters[key]!.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"--{key} must be boolean.");
        if ((bool?)parameters["retire"] == true && parameters.Properties().Any(p => p.Name != "retire")) throw new AgentError("invalid_request", "Retirement is exclusive: do not combine --retire true with edits that native retirement mode ignores.");
    }

    private static void ValidateSales(Request request)
    {
        if (request.Parameters?["market"] is JToken market && (market.Type != JTokenType.String || string.IsNullOrWhiteSpace(market.Value<string>()))) throw new AgentError("invalid_value", "--market must be an exact native option string.");
        if (request.Parameters?["showRetired"] is JToken retired && retired.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "--showRetired must be boolean.");
        if (request.Parameters?["variant"] is JToken variant && (variant.Type != JTokenType.Integer || variant.Value<int>() is < 1 or > 2)) throw new AgentError("invalid_value", "--variant must be 1 or 2.");
        if (request.Parameters?["sortBy"] is JToken sort && (sort.Type != JTokenType.String || string.IsNullOrWhiteSpace(sort.Value<string>()))) throw new AgentError("invalid_value", "--sortBy must be an exact visible column name.");
        if (request.Parameters?["scroll"] is JToken scroll && (scroll.Type is not (JTokenType.Integer or JTokenType.Float) || double.IsNaN(scroll.Value<double>()) || double.IsInfinity(scroll.Value<double>()))) throw new AgentError("invalid_value", "--scroll must be finite mouse-wheel units.");
    }

    private static void ValidateProductionHistory(Request request)
    {
        if (request.Parameters?["company"] is JToken company && (company.Type != JTokenType.String || string.IsNullOrWhiteSpace(company.Value<string>()))) throw new AgentError("invalid_value", "--company must be a non-empty exact native company option.");
        if (request.Parameters?["yearly"] is JToken yearly && yearly.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "--yearly must be boolean.");
    }

    private static void ValidateWafer(Request request, bool required)
    {
        var wafer = request.Parameters?["waferSize"];
        if (required && wafer == null) throw new AgentError("invalid_request", $"{request.Command} requires exact --wafer-size from the current capacity result.");
        if (wafer != null && (wafer.Type != JTokenType.String || string.IsNullOrWhiteSpace(wafer.Value<string>()))) throw new AgentError("invalid_value", "--wafer-size must be a non-empty exact native wafer size.");
    }

    private static void RequireBoolean(Request request, string key)
    {
        if (request.Parameters?[key]?.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"{request.Command} requires boolean --{key}.");
    }

    private static void RequireInteger(Request request, string key, int min, int max)
    {
        var value = request.Parameters?[key];
        if (value?.Type != JTokenType.Integer || value.Value<long>() < min || value.Value<long>() > max) throw new AgentError("invalid_value", $"{request.Command} requires integer --{key} from {min} to {max}.");
    }

    private static string LowerFirst(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
    private static string? FirstCell(string? value) => value?.Split('|')[0].Trim();
}
