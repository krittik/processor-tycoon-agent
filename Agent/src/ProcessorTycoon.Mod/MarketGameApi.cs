using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonMod;

// Player-browsable Analysis and Inspector workflows for the English 0.2.16a5 UI.
internal sealed class MarketGameApi : IGameModule
{
    private const string AnalysisScope = "AnalysisWindow";
    private const string InspectorScope = "InspectorWindow";
    private readonly GameUi ui;
    private readonly Action<string> log;
    private bool catalogRefreshed;
    public string[] Commands => new[] { "game.market-catalog", "game.market-sales", "game.market-top", "game.market-share", "game.inspector-read", "game.analysis-companies", "game.finance-read" };

    private static readonly Dictionary<string, string> tabs = new()
    {
        ["game.market-catalog"] = "Button Tab Hardwares", ["game.market-sales"] = "Button Tab Sales",
        ["game.market-top"] = "Button Tab Market", ["game.market-share"] = "Button Tab Market Share",
        ["game.analysis-companies"] = "Button Tab Companies", ["game.finance-read"] = "Button Tab Finance"
    };

    public MarketGameApi(GenericUi native, Action<string> log) { ui = new GameUi(native); this.log = log; }

    public static object Schema => new
    {
        commands = new object[]
        {
            new { command = "game market-catalog", parameters = new { view = "specs|market", market = "All Markets|Desktop|Mobile|Industries", showRetired = "boolean", highlightPlayer = "boolean", search = "case-insensitive text", scroll = "optional fallback wheel units" } },
            new { command = "game market-sales", parameters = new { view = "summary|segments", market = "All Markets|Desktop|Mobile|Industries", showRetired = "boolean", search = "case-insensitive text", scroll = "optional fallback wheel units" } },
            new { command = "game market-top", parameters = new { period = "30 Years|20 Years|15 Years|10 Years|5 Years|3 Years|1 Year", server = "boolean", desktop = "boolean", mobile = "boolean", industries = "boolean", contracts = "boolean", history = "boolean, optional" } },
            new { command = "game market-share", parameters = new { market = "All|Desktop|Mobile", category = "CPU Sales|Manufacturing" } },
            new { command = "game inspector-read [EXACT_CPU]", parameters = new { company = "exact visible company", variant = "zero-based CPU option index; pair with EXACT_CPU to disambiguate duplicate labels", detailed = "boolean" } },
            new { command = "game analysis-companies", parameters = new { search = "case-insensitive text", scroll = "optional fallback wheel units" } },
            new { command = "game finance-read", parameters = new { company = "exact visible company; omitted selects player", yearly = "boolean", detailed = "boolean", history = "boolean, optional" } }
        },
        behavior = "Opens/reuses native windows and applies only supplied filters. Tables contain every active instantiated player-browsable row, including offscreen rows; structurally incomplete rows remain raw and marked partial.",
        graphLimit = "Legend totals are always available. --history true reads only the exact active Graph.Values render range; inactive/RawValues/model histories are never exposed."
    };

    public void Validate(Request request)
    {
        if (!Commands.Contains(request.Command)) throw new AgentError("invalid_request", $"Unsupported market command '{request.Command}'.");
        if (request.Command != "game.inspector-read" && request.Target.Length > 0) throw new AgentError("invalid_request", $"{request.Command} does not take a target.");
        if (request.Value != null) throw new AgentError("invalid_request", "Use named parameters; no input was dispatched.");
        var allowed = request.Command switch
        {
            "game.market-catalog" => new[] { "view", "market", "showRetired", "highlightPlayer", "search", "scroll" },
            "game.market-sales" => new[] { "view", "market", "showRetired", "search", "scroll" },
            "game.market-top" => new[] { "period", "server", "desktop", "mobile", "industries", "contracts", "history" },
            "game.market-share" => new[] { "market", "category" },
            "game.inspector-read" => new[] { "company", "variant", "detailed" },
            "game.analysis-companies" => new[] { "search", "scroll" },
            _ => new[] { "company", "yearly", "detailed", "history" }
        };
        GameUi.Parameters(request, allowed);
        foreach (var property in request.Parameters?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            var boolean = property.Name is "showRetired" or "highlightPlayer" or "server" or "desktop" or "mobile" or "industries" or "contracts" or "detailed" or "yearly" or "history";
            if (boolean && property.Value.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"{property.Name} must be boolean.");
            if (property.Name == "variant" && (property.Value.Type != JTokenType.Integer || property.Value.Value<long>() < 0 || property.Value.Value<long>() > int.MaxValue)) throw new AgentError("invalid_value", "variant must be a non-negative zero-based integer CPU option index.");
            if (property.Name == "scroll" && !TryFiniteNumber(property.Value, out _)) throw new AgentError("invalid_value", "scroll must be a finite JSON number or invariant numeric string.");
            if (!boolean && property.Name is not "scroll" and not "variant" && (property.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)property.Value))) throw new AgentError("invalid_value", $"{property.Name} must be a non-empty string.");
        }
        if (request.Command == "game.inspector-read" && request.Parameters?["variant"] != null && string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "variant requires the exact CPU target label.");
        if (request.Parameters?["view"] is JToken view)
        {
            var allowedViews = request.Command == "game.market-catalog" ? new[] { "specs", "market" } : new[] { "summary", "segments" };
            if (!allowedViews.Contains(view.Value<string>())) throw new AgentError("invalid_value", $"view must be one of: {string.Join(", ", allowedViews)}.");
        }
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        if (request.Command == "game.inspector-read")
        {
            foreach (var step in Open(InspectorScope, "Inspector Desktop")) yield return step;
            foreach (var step in SelectIfRequested(InspectorScope, "Company Dropdown", request.Parameters?["company"])) yield return step;
            if (!string.IsNullOrWhiteSpace(request.Target)) foreach (var step in SelectInspectorCpu(request.Target, request.Parameters?["variant"])) yield return step;
            if (request.Parameters?["detailed"] is JToken wanted)
            {
                var button = Control(InspectorScope, "Simple Or Detailed", "button");
                var current = string.Equals((string?)button["label"], "Detailed", StringComparison.Ordinal);
                if (current != wanted.Value<bool>()) yield return GameUi.Click(button);
            }
            yield break;
        }

        foreach (var step in Open(AnalysisScope, "Analysis Desktop")) yield return step;
        var tab = Control(AnalysisScope, tabs[request.Command], "button");
        if ((string?)tab["blockedReason"] != "disabled_by_game") yield return GameUi.Click(tab);

        switch (request.Command)
        {
            case "game.market-catalog":
            {
                foreach (var step in SetTableView(request.Parameters?["view"], "HardwaresWindow", "SpreadsheetGroupSpecs", "SpreadsheetGroupMarket", "Specs", "specs", "market")) yield return step;
                // The native table re-renders only when opened, at month end or on a filter change; an already open table
                // keeps the prices of its last render (for example your own price after product-price). A filter change
                // re-renders it; otherwise Highlight Player is toggled twice (display style only) to re-render current values.
                var rendered = false;
                foreach (var step in SelectIfRequested(AnalysisScope, "Market Dropdown", request.Parameters?["market"], "HardwaresWindow")) { rendered = true; yield return step; }
                foreach (var step in ToggleIfRequested(AnalysisScope, "Show Retired", request.Parameters?["showRetired"], "HardwaresWindow")) { rendered = true; yield return step; }
                foreach (var step in ToggleIfRequested(AnalysisScope, "Highlight Player", request.Parameters?["highlightPlayer"], "HardwaresWindow")) { rendered = true; yield return step; }
                var highlights = GameUi.Controls(ui.Read(AnalysisScope)).Where(c => (string?)c["name"] == "Highlight Player" && (string?)c["role"] == "toggle" && ((string?)c["context"] ?? "").Contains("HardwaresWindow") && c["blockedReason"] == null).ToArray();
                if (!rendered && !request.Hidden && highlights.Length == 1)
                {
                    var highlight = highlights[0];
                    var original = highlight["value"]!.Value<bool>();
                    yield return GameUi.Set(highlight, new JValue(!original));
                    yield return GameUi.Set(Control(AnalysisScope, "Highlight Player", "toggle", "HardwaresWindow"), new JValue(original));
                    if (Control(AnalysisScope, "Highlight Player", "toggle", "HardwaresWindow")["value"]!.Value<bool>() != original) throw new AgentError("value_not_applied", "Refreshing the catalog did not restore the native Highlight Player toggle.");
                    catalogRefreshed = true;
                }
                foreach (var step in ScrollIfRequested(AnalysisScope, "All Hardwares Spreadsheet", request.Parameters?["scroll"])) yield return step;
                break;
            }
            case "game.market-sales":
                foreach (var step in SetTableView(request.Parameters?["view"], "MarketSalesSpreadsheetWindow", "SpreadsheetGroupOne", "SpreadsheetGroupTwo", "Variant One", "summary", "segments")) yield return step;
                foreach (var step in SelectIfRequested(AnalysisScope, "Market Dropdown", request.Parameters?["market"], "MarketSalesSpreadsheetWindow")) yield return step;
                foreach (var step in ToggleIfRequested(AnalysisScope, "Show Retired", request.Parameters?["showRetired"], "MarketSalesSpreadsheetWindow")) yield return step;
                foreach (var step in ScrollIfRequested(AnalysisScope, "Sales Spreadsheet", request.Parameters?["scroll"])) yield return step;
                break;
            case "game.market-top":
                foreach (var step in SelectIfRequested(AnalysisScope, "Sales Period Dropdown", request.Parameters?["period"])) yield return step;
                foreach (var pair in new[] { ("server", "High End"), ("desktop", "Mid Range"), ("mobile", "Low End"), ("industries", "Industries"), ("contracts", "Contracts") })
                    foreach (var step in ToggleIfRequested(AnalysisScope, pair.Item2, request.Parameters?[pair.Item1])) yield return step;
                break;
            case "game.market-share":
                foreach (var step in SelectIfRequested(AnalysisScope, "Market Dropdown", request.Parameters?["market"], "MarketShareWindow")) yield return step;
                foreach (var step in SelectIfRequested(AnalysisScope, "Category Dropdown", request.Parameters?["category"])) yield return step;
                break;
            case "game.analysis-companies":
                foreach (var step in ScrollIfRequested(AnalysisScope, "Companies Spreadsheet", request.Parameters?["scroll"])) yield return step;
                break;
            case "game.finance-read":
                foreach (var step in SelectFinanceCompany(request.Parameters?["company"])) yield return step;
                foreach (var step in SetFinanceMode(request.Parameters?["yearly"], true)) yield return step;
                foreach (var step in SetFinanceMode(request.Parameters?["detailed"], false)) yield return step;
                break;
        }
    }

    public JObject Result(Request request)
    {
        var result = request.Command switch
        {
            "game.inspector-read" => Inspector(),
            "game.market-catalog" => CatalogResult(request),
            "game.market-sales" => TableResult("sales", "MarketSalesSpreadsheetWindow", "Sales Spreadsheet", request.Parameters?["search"]),
            "game.analysis-companies" => TableResult("companies", "CompaniesSpreadsheetWindow", "Companies Spreadsheet", request.Parameters?["search"]),
            "game.market-top" => TopSales(request),
            "game.market-share" => MarketShare(),
            _ => Finance(request)
        };
        if (request.Command.StartsWith("game.market-", StringComparison.Ordinal) && request.Command != "game.market-share") result["marketGuidance"] = "Use game market-share for native market preference tooltips (description, recommended packages/price, optimal power). --market All, Desktop and Mobile expose different chart groups; prices are current native recommendations, not guaranteed profitable prices.";
        log("/" + request.Command.Substring(5) + " · read");
        if (request.Command.StartsWith("game.market-", StringComparison.Ordinal)) result["analysisOptions"] = JObject.FromObject(new
        {
            preferences = "game market-share --market All|Desktop|Mobile --category \"CPU Sales\" (native segment preferences and recommended prices)",
            competitors = "game market-catalog --view specs|market --market \"All Markets\"|Desktop|Mobile|Industries --search TEXT --show-retired false --highlight-player true (specs or commercial metrics)",
            inspect = "game inspector-read EXACT_CPU --company EXACT_COMPANY --detailed true (use names from catalog)",
            history = "game market-top --period \"1 Year\" --desktop true --mobile true --industries true --server true --contracts true --history true",
            ownSales = "game sales-read (own sales, not the whole market)",
            finance = "game finance-read --detailed true --history true (historical flows); game desktop-read (current player balance and credit)",
            note = "Use exact returned filter options. Reads with filters change the visible native view. Missing/failed table data is not zero sales; report gaps before drawing conclusions. market-sales currently has a known navigation mismatch; use market-catalog --view market for available commercial metrics."
        });
        return result;
    }

    private IEnumerable<Request> Open(string scope, string desktopButton)
    {
        if (GameUi.Controls(ui.Read(scope)).Length > 0) yield break;
        var shortcut = ui.CpuDraftShortcut(scope == InspectorScope ? "Inspector" : "Analysis");
        if (shortcut != null)
        {
            yield return GameUi.Click(shortcut);
            if (GameUi.Controls(ui.Read(scope)).Length == 0) throw new AgentError("game_ui_mismatch", $"The CPU draft shortcut did not expose {scope}; the draft was preserved.");
            yield break;
        }
        var desktop = ui.Read("DesktopButtons");
        yield return GameUi.Click(GameUi.One(GameUi.Controls(desktop).Where(c => (string?)c["name"] == desktopButton && (string?)c["role"] == "button"), desktopButton));
        if (GameUi.Controls(ui.Read(scope)).Length == 0) throw new AgentError("game_ui_mismatch", $"{scope} did not become visible after the native desktop action.");
    }

    private JObject Control(string scope, string name, string? role = null, string? context = null)
    {
        return GameUi.One(GameUi.Controls(ui.Read(scope)).Where(c => (string?)c["name"] == name && (role == null || (string?)c["role"] == role) && (context == null || ((string?)c["context"] ?? "").Contains(context))), name);
    }

    private IEnumerable<Request> SelectIfRequested(string scope, string name, JToken? wanted, string? context = null)
    {
        if (wanted == null) yield break;
        var node = Control(scope, name, "select", context);
        var option = wanted.Value<string>()!;
        var options = node["options"]!.Values<string>().ToArray();
        if (options.Count(v => v == option) != 1) throw new AgentError("invalid_value", $"{name} has no unique option '{option}'. Available: {string.Join(", ", options)}.");
        if (options[node["value"]!.Value<int>()] != option) yield return GameUi.Select(node, option);
    }

    private IEnumerable<Request> SelectInspectorCpu(string cpu, JToken? variant)
    {
        var node = Control(InspectorScope, "Cpu Dropdown", "select");
        var options = node["options"]!.Values<string>().ToArray();
        var matches = options.Select((label, index) => new { label, index }).Where(option => option.label == cpu).Select(option => option.index).ToArray();
        int index;
        if (variant != null)
        {
            index = checked((int)variant.Value<long>());
            if (index >= options.Length) throw new AgentError("invalid_value", $"CPU variant index {index} is outside the current zero-based range 0..{options.Length - 1}.");
            if (options[index] != cpu) throw new AgentError("context_changed", $"CPU option {index} is now '{options[index]}', not requested exact label '{cpu}'. Reread cpuChoices; no input was invoked.");
        }
        else
        {
            if (matches.Length == 0) throw new AgentError("invalid_value", $"Cpu Dropdown has no option '{cpu}'. Available: {IndexedOptions(options)}.");
            if (matches.Length > 1) throw new AgentError("ambiguous_target", $"Cpu Dropdown has several options named '{cpu}' at indexes {string.Join(", ", matches)}. Repeat with --variant INDEX from cpuChoices; no input was invoked.");
            index = matches[0];
        }

        if (node["value"]!.Value<int>() != index) yield return variant == null ? GameUi.Select(node, cpu) : GameUi.Action("ui.select", node, new JValue(index));
        var current = Control(InspectorScope, "Cpu Dropdown", "select");
        var currentOptions = current["options"]!.Values<string>().ToArray();
        var currentIndex = current["value"]!.Value<int>();
        if (currentIndex != index || currentIndex < 0 || currentIndex >= currentOptions.Length || currentOptions[currentIndex] != cpu)
            throw new AgentError("value_not_applied", $"Native Cpu Dropdown did not retain option {index} with exact label '{cpu}'. Reread cpuChoices.");
    }

    private static string IndexedOptions(string?[] options) => string.Join(", ", options.Select((label, index) => $"{index}:{label}"));

    private IEnumerable<Request> ToggleIfRequested(string scope, string name, JToken? wanted, string? context = null)
    {
        if (wanted == null) yield break;
        var node = Control(scope, name, "toggle", context);
        if (node["value"]!.Value<bool>() != wanted.Value<bool>()) yield return GameUi.Set(node, wanted);
    }

    private IEnumerable<Request> ScrollIfRequested(string scope, string name, JToken? wanted)
    {
        if (wanted == null) yield break;
        if (!TryFiniteNumber(wanted, out var value)) throw new AgentError("invalid_value", "scroll must be a finite JSON number or invariant numeric string.");
        yield return GameUi.Action("ui.scroll", Control(scope, name, "scroll"), new JValue(value));
    }

    private IEnumerable<Request> SetFinanceMode(JToken? wanted, bool period)
    {
        if (wanted == null) yield break;
        var view = ui.Read(AnalysisScope);
        var current = FinanceMode(view, period);
        if (current == wanted.Value<bool>()) yield break;
        var currentModeButton = period ? (current ? "Yearly" : "Monthly") : (current ? "Detailed" : "Total");
        yield return GameUi.Click(Control(AnalysisScope, currentModeButton, "button"));
    }

    private IEnumerable<Request> SelectFinanceCompany(JToken? requested)
    {
        var node = Control(AnalysisScope, "Companies Dropdown", "select");
        var options = node["options"]!.Values<string>().ToArray();
        if (options.Length == 0) throw new AgentError("game_ui_mismatch", "Finance exposed no company choices.");
        var company = requested?.Value<string>() ?? options[0]!;
        if (options.Count(option => option == company) != 1) throw new AgentError("invalid_value", $"Companies Dropdown has no unique option '{company}'. Available: {string.Join(", ", options)}.");
        if (options[node["value"]!.Value<int>()] != company) yield return GameUi.Select(node, company);
    }

    private IEnumerable<Request> SetTableView(JToken? wanted, string context, string firstGroup, string secondGroup, string button, string firstValue, string secondValue)
    {
        if (wanted == null) yield break;
        var catalog = ui.Catalog(AnalysisScope);
        var paths = GameUi.Controls(catalog).Select(c => (string?)c["context"] ?? "").Concat(GameUi.Texts(catalog).Select(t => (string?)t["context"] ?? "")).Where(c => c.Contains(context)).ToArray();
        var first = paths.Any(c => c.Contains(firstGroup));
        var second = paths.Any(c => c.Contains(secondGroup));
        if (first == second) throw new AgentError("game_ui_mismatch", $"Cannot identify the active {context} view.");
        var current = first ? firstValue : secondValue;
        if (current == wanted.Value<string>()) yield break;
        var names = context == "HardwaresWindow" ? new[] { "Specs", "Market" } : new[] { button, "Variant Two" };
        var toggle = GameUi.One(GameUi.Controls(catalog).Where(c => (string?)c["role"] == "button" && names.Contains((string?)c["name"] ?? "") && ((string?)c["context"] ?? "").Contains(context)), "spreadsheet view toggle");
        yield return GameUi.Click(toggle);
    }

    private JObject Inspector()
    {
        var view = ui.Catalog(InspectorScope);
        var company = ControlValue(view, "Company Dropdown");
        var cpu = ControlValue(view, "Cpu Dropdown");
        var fields = new JObject();
        foreach (var text in cpu == "None" ? Enumerable.Empty<JObject>() : GameUi.Texts(view))
        {
            if (!((string?)text["context"] ?? "").Contains("/CpuUI/") || ((string?)text["text"] ?? "").Contains('…')) continue;
            var cells = Split((string)text["text"]!);
            if (cells.Length != 2) continue;
            var key = Key(cells[0]);
            if (key is "core" or "market" or "memory") continue;
            if (!fields.ContainsKey(key)) fields[key] = Typed(cells[1]);
        }
        var mode = (string?)Control(InspectorScope, "Simple Or Detailed", "button")["label"] ?? "Unknown";
        var companyNode = Control(InspectorScope, "Company Dropdown", "select");
        var cpuNode = Control(InspectorScope, "Cpu Dropdown", "select");
        var cpuOptions = cpuNode["options"]!.Values<string>().ToArray();
        var cpuIndex = cpuNode["value"]!.Value<int>();
        var cpuChoices = new JArray(cpuOptions.Select((label, index) => new JObject { ["index"] = index, ["label"] = label, ["selected"] = index == cpuIndex }));
        var duplicateCpuLabels = new JArray(cpuOptions.GroupBy(label => label).Where(group => group.Count() > 1).Select(group => group.Key));
        return Base("inspector", new JObject
        {
            ["company"] = company, ["cpu"] = cpu, ["mode"] = mode.ToLowerInvariant(), ["specs"] = fields,
            ["companyOptions"] = companyNode["options"]!.DeepClone(), ["cpuOptions"] = cpuNode["options"]!.DeepClone(),
            ["cpuOptionIndex"] = cpuIndex, ["cpuChoices"] = cpuChoices, ["duplicateCpuLabels"] = duplicateCpuLabels,
            ["available"] = cpu != "None", ["unavailableReason"] = cpu == "None" ? "The selected company has no CPU in the native Inspector." : null,
            ["next"] = duplicateCpuLabels.Count > 0
                ? "Use game inspector-read EXACT_CPU --company EXACT_COMPANY --variant ZERO_BASED_INDEX [--detailed true] for a duplicate label; omit --variant for unique labels."
                : "Use game inspector-read EXACT_CPU --company EXACT_COMPANY [--detailed true]."
        });
    }

    private JObject CatalogResult(Request request)
    {
        var result = TableResult("catalog", "HardwaresWindow", "All Hardwares Spreadsheet", request.Parameters?["search"]);
        result["refreshedBeforeRead"] = catalogRefreshed || !request.Hidden;
        result["refreshMethod"] = catalogRefreshed ? "highlight_player_toggled_twice" : "opened_or_filter_changed";
        result["rowLimit"] = "The native Specs view lists at most the 50 CPUs with the highest MIPS in the selected market (the Market view: the 50 best-selling); weaker CPUs are not shown.";
        return result;
    }

    private JObject TableResult(string kind, string context, string scrollName, JToken? search)
    {
        var view = ui.Catalog(AnalysisScope);
        var titles = TableTitles(view, context);
        if (titles.Select(Key).Distinct().Count() != titles.Length) titles = Array.Empty<string>();
        var columnsSource = titles.Length > 0 ? "native-catalog" : "unavailable";
        if (titles.Length == 0 && context == "MarketSalesSpreadsheetWindow")
        {
            titles = SalesTitles(view);
            if (titles.Length > 0) columnsSource = "version-mapped-native-layout";
        }
        var rows = new JArray();
        foreach (var text in GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains(context) && ((string?)t["context"] ?? "").Contains("/Spreadsheet/Row/Column")))
        {
            var display = (string)text["text"]!;
            if (search != null && display.IndexOf(search.Value<string>()!, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var cells = Split(display);
            var row = new JObject { ["display"] = display, ["partial"] = display.Contains('…') || titles.Length == 0 || cells.Length != titles.Length };
            if (!(bool)row["partial"]!)
            {
                var values = new JObject();
                for (var i = 0; i < titles.Length; i++) values[Key(titles[i])] = Typed(cells[i]);
                row["values"] = values;
            }
            else row["cells"] = JArray.FromObject(cells);
            rows.Add(row);
        }
        var filters = Fields(view, context);
        var scroll = GameUi.Controls(view).SingleOrDefault(c => (string?)c["name"] == scrollName);
        var clipped = rows.OfType<JObject>().Any(r => (bool)r["partial"]!);
        var capped = (bool?)view["more"] ?? false;
        var activeView = context == "HardwaresWindow" ? ActiveView(view, "SpreadsheetGroupSpecs", "SpreadsheetGroupMarket", "specs", "market") : context == "MarketSalesSpreadsheetWindow" ? ActiveView(view, "SpreadsheetGroupOne", "SpreadsheetGroupTwo", "summary", "segments") : null;
        return Base(kind, new JObject
        {
            ["view"] = activeView, ["viewOptions"] = activeView == null ? null : context == "HardwaresWindow" ? new JArray("specs", "market") : new JArray("summary", "segments"),
            ["columns"] = JArray.FromObject(titles.Select(Key)), ["columnsSource"] = columnsSource, ["rows"] = rows, ["filters"] = filters,
            ["search"] = search?.DeepClone(), ["catalogScope"] = "active-instantiated-player-browsable", ["offscreenIncluded"] = true,
            ["complete"] = !capped, ["normalizedComplete"] = !capped && !clipped && titles.Length > 0,
            ["incompleteReason"] = capped ? "The 10,000-node catalog cap was reached." : null, ["scroll"] = scroll?["value"]?.DeepClone(), ["truncated"] = capped,
            ["next"] = "All native active rows, including offscreen rows, are returned. partial:true means that row could not be safely normalized; scroll is normally unnecessary."
        });
    }

    private static string? ActiveView(JObject view, string firstGroup, string secondGroup, string firstValue, string secondValue)
    {
        var paths = GameUi.Controls(view).Select(c => (string?)c["context"] ?? "").Concat(GameUi.Texts(view).Select(t => (string?)t["context"] ?? "")).ToArray();
        var first = paths.Any(c => c.Contains(firstGroup));
        var second = paths.Any(c => c.Contains(secondGroup));
        return first != second ? first ? firstValue : secondValue : null;
    }

    private static string[] TableTitles(JObject view, string context)
    {
        var controls = GameUi.Controls(view).Where(c => (string?)c["name"] == "Spreadsheet Title Variant" && ((string?)c["context"] ?? "").Contains(context)).Select(c => (string)c["label"]!).ToArray();
        if (controls.Length > 0) return controls;
        var text = GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains(context) && ((string?)t["context"] ?? "").Contains("/TopTitles")).Select(t => (string)t["text"]!).ToArray();
        return text.Length == 1 ? Split(text[0]) : text;
    }

    private static string[] SalesTitles(JObject view)
    {
        var row = GameUi.Texts(view).FirstOrDefault(t => ((string?)t["context"] ?? "").Contains("MarketSalesSpreadsheetWindow") && ((string?)t["context"] ?? "").Contains("/Spreadsheet/Row/Column"));
        var context = (string?)row?["context"] ?? "";
        if (context.Contains("SpreadsheetGroupOne")) return new[] { "CPU", "All Time", "Sold", "Missed Sales", "Stock", "Income", "Profit", "Unit Cost", "Price", "Popularity" };
        if (!context.Contains("SpreadsheetGroupTwo")) return Array.Empty<string>();
        var marketNode = GameUi.Controls(view).SingleOrDefault(c => (string?)c["name"] == "Market Dropdown" && ((string?)c["context"] ?? "").Contains("MarketSalesSpreadsheetWindow"));
        if (marketNode == null) return Array.Empty<string>();
        var market = (string)marketNode["options"]![marketNode["value"]!.Value<int>()]!;
        if (market == "Industries") return Array.Empty<string>();
        var segments = market == "All Markets" ? new[] { "Server", "Desktop", "Mobile", "Industries" } : new[] { "High End", "Mid Range", "Low End", "Missed" };
        return new[] { "CPU", "Highest", "Sold" }.Concat(segments).Concat(new[] { "Profit", "Price", "Popularity" }).ToArray();
    }

    private JObject TopSales(Request request)
    {
        var view = ui.Catalog(AnalysisScope);
        var rankings = new JArray();
        foreach (var text in GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains("MarketSalesGraphWindow/LegendSales")))
        {
            var cells = Split((string)text["text"]!);
            if (cells.Length != 2 || cells[0] == "None" || cells.Any(c => c.Contains('…'))) continue;
            rankings.Add(new JObject { ["cpu"] = cells[0], ["sales"] = Typed(cells[1]) });
        }
        var historyRequested = request.Parameters?["history"]?.Value<bool>() == true;
        var history = historyRequested ? History("ProcessorTycoon.MarketSystem.UI.MarketSalesGraphWindow", "month", "units-sold") : null;
        var result = Base("top-sales", new JObject
        {
            ["filters"] = TopSalesFilters(view), ["rankings"] = rankings,
            ["seriesAvailable"] = (bool?)history?["available"] ?? false,
            ["limit"] = "Legend totals are always returned. Exact rendered-range series are optional with --history true."
        });
        if (history != null) result["history"] = history;
        return result;
    }

    private static JObject TopSalesFilters(JObject view)
    {
        var names = new Dictionary<string, string>
        {
            ["Sales Period Dropdown"] = "period", ["High End"] = "server", ["Mid Range"] = "desktop",
            ["Low End"] = "mobile", ["Industries"] = "industries", ["Contracts"] = "contracts"
        };
        var result = new JObject();
        foreach (var node in GameUi.Controls(view).Where(control => ((string?)control["context"] ?? "").Contains("MarketSalesGraphWindow") && names.ContainsKey((string?)control["name"] ?? "")))
        {
            var field = new JObject { ["value"] = (string?)node["role"] == "select" ? node["options"]![node["value"]!.Value<int>()]!.DeepClone() : node["value"]!.DeepClone() };
            if (node["options"] != null) field["options"] = node["options"]!.DeepClone();
            if (node["blockedReason"] != null) field["blockedReason"] = node["blockedReason"]!.DeepClone();
            result[names[(string)node["name"]!]] = field;
        }
        return result;
    }

    private JObject MarketShare()
    {
        var view = ui.Catalog(AnalysisScope);
        var markets = new JObject();
        var unparsed = new JArray();
        var partial = false;
        var summaries = GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains("MarketShareWindow") && ((string?)t["context"] ?? "").EndsWith("/Texts", StringComparison.Ordinal)).ToArray();
        foreach (var summary in summaries)
        {
            var cells = Split((string)summary["text"]!);
            if (cells.Length != 2)
            {
                partial = true;
                unparsed.Add(new JObject { ["display"] = summary["text"]!.DeepClone(), ["context"] = summary["context"]!.DeepClone() });
                continue;
            }
            partial |= cells.Any(c => c.Contains('…'));
            var chartPath = ((string)summary["context"]!).Substring(0, ((string)summary["context"]!).Length - 6);
            var shares = new JArray();
            foreach (var legend in GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").StartsWith(chartPath + "/Legend", StringComparison.Ordinal)))
            {
                var item = Split((string)legend["text"]!);
                var itemPartial = item.Length != 2 || item.Any(c => c.Contains('…'));
                partial |= itemPartial;
                shares.Add(item.Length == 2
                    ? new JObject { ["company"] = item[0], ["share"] = Typed(item[1]), ["partial"] = itemPartial }
                    : new JObject { ["display"] = legend["text"]!.DeepClone(), ["partial"] = true });
            }
            var key = Key(cells[0]);
            if (markets.ContainsKey(key))
            {
                partial = true;
                unparsed.Add(new JObject { ["display"] = summary["text"]!.DeepClone(), ["reason"] = "duplicate_normalized_market_key" });
                continue;
            }
            markets[key] = new JObject { ["displayName"] = cells[0], ["size"] = Typed(cells[1]), ["shares"] = shares };
        }
        var capped = (bool?)view["more"] ?? false;
        return Base("market-share", new JObject
        {
            ["filters"] = Fields(view, "MarketShareWindow"), ["markets"] = markets, ["unparsed"] = unparsed,
            ["guidance"] = MarketGuidance(),
            ["catalogScope"] = "selected-filter-active-charts", ["complete"] = !capped && summaries.Length > 0, ["normalizedComplete"] = !capped && summaries.Length > 0 && !partial,
            ["incompleteReason"] = capped ? "The 10,000-node catalog cap was reached." : summaries.Length == 0 ? "No active native chart summary was exposed; an empty result is not verified market data." : null,
            ["normalizedIncompleteReason"] = partial ? "One or more active native chart labels were clipped, malformed or normalized to a duplicate key." : null,
            ["inactiveFilterAlternativesIncluded"] = false, ["seriesAvailable"] = false,
            ["limit"] = "Complete applies to the currently selected native market/category charts. Inactive filter alternatives and unlabeled pie geometry are outside this result."
        });
    }

    private static JObject MarketGuidance()
    {
        const string windowType = "ProcessorTycoon.MarketSystem.UI.MarketShareWindow";
        const string triggerType = "ProcessorTycoon.TooltipSystem.TooltipTrigger";
        const string dataType = "ProcessorTycoon.TooltipSystem.TooltipData";
        var items = new JArray();
        try
        {
            var window = ActiveWindow(windowType);
            var triggers = Items(Field<object>(window, windowType, "tooltipTriggers"), "MarketShareWindow.tooltipTriggers");
            foreach (var value in triggers)
            {
                if (value is not MonoBehaviour trigger || trigger.GetType().FullName != triggerType) throw new AgentError("game_ui_mismatch", "Unexpected market tooltip UI binding.");
                if (!trigger.isActiveAndEnabled || !ActiveUnder(trigger.transform, windowType) || !Property<bool>(trigger, triggerType, "IsActive")) continue;
                if (trigger.GetComponentsInParent<CanvasGroup>().Any(group => group.alpha < .01f)) continue;
                var data = Field<object>(trigger, triggerType, "tooltipData");
                var header = Property<string>(data, dataType, "Header");
                var content = Property<string>(data, dataType, "Content");
                if (content.Contains("#segmentBudget"))
                {
                    // The same read-only UI formatter used by Tooltip.SetText; never read Market/model fields here.
                    var arguments = Field<List<int>>(trigger, triggerType, "arguments");
                    if (arguments.Count != 1) throw new AgentError("game_ui_mismatch", "Market tooltip has no unique native segment argument.");
                    var provider = trigger.GetType().Assembly.GetType("ProcessorTycoon.TooltipSystem.TooltipDynamicTextProvider");
                    var formatter = provider?.GetMethod("SegmentBudget", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(List<int>) }, null);
                    if (formatter?.Invoke(null, new object[] { new List<int>(arguments) }) is not string budget) throw new AgentError("game_ui_mismatch", "Native market tooltip price formatter is unavailable.");
                    content = content.Replace("#segmentBudget", budget);
                }
                var text = Regex.Replace(content, "<[^>]+>", "").Trim();
                var unresolved = Regex.IsMatch(text, @"#[A-Za-z][A-Za-z0-9]*\b");
                var preferences = new JObject();
                foreach (var line in text.Split('\n'))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0) preferences[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
                items.Add(new JObject { ["market"] = Regex.Replace(header, "<[^>]+>", "").Trim(), ["text"] = text, ["recommendations"] = preferences, ["complete"] = !unresolved });
            }
            return new JObject { ["source"] = "native-market-tooltip", ["items"] = items, ["complete"] = items.Count > 0 && items.All(item => (bool?)item["complete"] == true), ["scope"] = "selected-filter-player-browsable-tooltips", ["next"] = "Read All, Desktop and Mobile market-share filters for their respective preferences. Recommended price is dynamic native guidance, not a guaranteed selling price." };
        }
        catch (Exception error) when (error is AgentError or TargetInvocationException)
        {
            return new JObject { ["source"] = "native-market-tooltip", ["items"] = items, ["complete"] = false, ["error"] = error.InnerException?.Message ?? error.Message, ["next"] = "Tooltip mapping is incomplete; do not treat missing recommendations as no preferences. Generic hover/observe or a screenshot remains fallback." };
        }
    }

    private JObject Finance(Request request)
    {
        var view = ui.Catalog(AnalysisScope);
        var company = ControlValue(view, "Companies Dropdown");
        var yearly = FinanceMode(view, true);
        var detail = FinanceMode(view, false);
        var legendRead = FinanceLegendRows(view, detail);
        var historyRequested = request.Parameters?["history"]?.Value<bool>() == true;
        var history = historyRequested ? History("ProcessorTycoon.Desktop.Finance.FinanceGraphWindow", yearly ? "year" : "month", "currency-$", detail) : null;
        var result = Base("finance", new JObject
        {
            ["company"] = company, ["companyOptions"] = Control(AnalysisScope, "Companies Dropdown", "select")["options"]!.DeepClone(),
            ["playerCurrentFinances"] = BalanceSnapshot.Read(),
            ["periodNote"] = "Graph legend/history describes the selected company's historical periods. playerCurrentFinances always describes YOUR company, even when a competitor is selected. Zero historical values do not establish zero current expenses.",
            ["companyScope"] = "selected-company", ["companySelection"] = request.Parameters?["company"] == null ? "player-default" : "requested-exact",
            ["period"] = yearly ? "yearly" : "monthly", ["detail"] = detail ? "detailed" : "total",
            ["latestVisible"] = legendRead["rows"]!.DeepClone(), ["latestMapping"] = legendRead["mapping"]!.DeepClone(),
            ["latestMappingLimit"] = legendRead["limit"]?.DeepClone(), ["latestGrouping"] = "ordered native legend slots; duplicate names are distinct and not merged",
            ["seriesAvailable"] = (bool?)history?["available"] ?? false,
            ["limit"] = "Visible native legend values are always returned. Exact rendered-range series are optional with --history true."
        });
        if (history != null) result["history"] = history;
        return result;
    }

    private static JObject FinanceLegendRows(JObject view, bool detailed)
    {
        const string windowType = "ProcessorTycoon.Desktop.Finance.FinanceGraphWindow";
        const string holderType = "ProcessorTycoon.GraphSystem.LegendHolder";
        const string legendType = "ProcessorTycoon.GraphSystem.LegendText";
        try
        {
            var holders = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == holderType && component.gameObject.scene.IsValid() && ActiveUnder(component.transform, windowType)).ToArray();
            if (holders.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active Finance LegendHolder, found {holders.Length}.");
            var window = ActiveWindow(windowType);
            var positive = Field<string>(window, windowType, "positiveColorHex");
            var negative = Field<string>(window, windowType, "negativeColorHex");
            var neutral = Field<string>(window, windowType, "neutralColorHex");
            var rows = new JArray();
            var legends = Items(Field<object>(holders[0], holderType, "legendTexts"), "LegendHolder.legendTexts");
            for (var index = 0; index < legends.Length; index++)
            {
                if (legends[index] is not MonoBehaviour legend || legend.GetType().FullName != legendType || !legend.gameObject.activeInHierarchy) continue;
                var name = Regex.Replace(Field<TMP_Text>(legend, legendType, "nameText").text, "<[^>]+>", "").Trim();
                var rawNumber = Field<TMP_Text>(legend, legendType, "numberText").text;
                var number = Regex.Replace(rawNumber, "<[^>]+>", "").Trim();
                var color = (Color32)Field<Image>(legend, legendType, "dot").color;
                if (name.Length == 0 || number.Length == 0) continue;
                rows.Add(new JObject
                {
                    ["id"] = "series-" + index, ["index"] = index, ["name"] = name, ["flow"] = FinanceFlow(index, detailed),
                    ["valueStyle"] = FinanceValueStyle(rawNumber, positive, negative, neutral), ["value"] = Typed(number),
                    ["seriesColor"] = "#" + ColorUtility.ToHtmlStringRGBA(color)
                });
            }
            return new JObject { ["rows"] = rows, ["mapping"] = "native-legend-slots" };
        }
        catch (AgentError error)
        {
            var rows = new JArray();
            var index = 0;
            foreach (var text in GameUi.Texts(view).Where(item => ((string?)item["context"] ?? "").Contains("FinanceGraphWindow/Legend")))
            {
                var cells = Split((string)text["text"]!);
                if (cells.Length == 2 && !cells.Any(cell => cell.Contains('…'))) rows.Add(new JObject
                {
                    ["id"] = "rendered-row-" + index, ["renderedIndex"] = index, ["name"] = cells[0], ["flow"] = "not_exposed",
                    ["valueStyle"] = "not_exposed", ["value"] = Typed(cells[1]), ["seriesColor"] = null
                });
                index++;
            }
            return new JObject { ["rows"] = rows, ["mapping"] = "catalog-render-order", ["limit"] = error.Message };
        }
    }

    internal static JObject ProductionHistory(string cadence) => History("ProcessorTycoon.Desktop.Production.ProductionGraphWindow", cadence, "lines");

    private static JObject History(string allowedWindowType, string cadence, string unit, bool? financeDetailed = null)
    {
        try { return RenderedHistory(allowedWindowType, cadence, unit, financeDetailed); }
        catch (AgentError error)
        {
            return new JObject
            {
                ["available"] = false, ["source"] = "native-render-backing", ["unavailableReason"] = error.Message,
                ["boundary"] = "No RawValues, reports, CPU market histories or inactive graph data were read."
            };
        }
    }

    private static JObject RenderedHistory(string allowedWindowType, string cadence, string unit, bool? financeDetailed)
    {
        const string chartType = "ProcessorTycoon.GraphSystem.GraphChart";
        const string holderType = "ProcessorTycoon.GraphSystem.LegendHolder";
        var charts = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == chartType && component.gameObject.scene.IsValid() && ActiveUnder(component.transform, allowedWindowType)).ToArray();
        if (charts.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active rendered GraphChart under {allowedWindowType}, found {charts.Length}.");
        var holders = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == holderType && component.gameObject.scene.IsValid() && ActiveUnder(component.transform, allowedWindowType)).ToArray();
        if (holders.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active rendered LegendHolder under {allowedWindowType}, found {holders.Length}.");

        var graphObjects = Items(Field<object>(charts[0], chartType, "graphs"), "GraphChart.graphs");
        var legendObjects = Items(Field<object>(holders[0], holderType, "legendTexts"), "LegendHolder.legendTexts");
        if (legendObjects.Length < graphObjects.Length) throw new AgentError("game_ui_mismatch", $"Rendered legend has {legendObjects.Length} rows for {graphObjects.Length} graph slots.");
        var financeWindow = financeDetailed == null ? null : ActiveWindow(allowedWindowType);
        var positive = financeWindow == null ? null : Field<string>(financeWindow, allowedWindowType, "positiveColorHex");
        var negative = financeWindow == null ? null : Field<string>(financeWindow, allowedWindowType, "negativeColorHex");
        var neutral = financeWindow == null ? null : Field<string>(financeWindow, allowedWindowType, "neutralColorHex");
        var series = new JArray();
        for (var index = 0; index < graphObjects.Length; index++)
        {
            var graph = graphObjects[index];
            if (graph.GetType().FullName != "ProcessorTycoon.GraphSystem.Graph") throw new AgentError("game_ui_mismatch", "GraphChart contains an unexpected graph UI type.");
            var values = Items(Property<object>(graph, "ProcessorTycoon.GraphSystem.Graph", "Values"), "Graph.Values").Select(Numeric).ToArray();
            if (values.Length == 0) continue;
            if (legendObjects[index] is not MonoBehaviour legend || legend.GetType().FullName != "ProcessorTycoon.GraphSystem.LegendText" || !legend.gameObject.activeInHierarchy) throw new AgentError("game_ui_mismatch", $"Rendered graph slot {index} has no active matching legend row.");
            var name = Regex.Replace(Field<TMP_Text>(legend, "ProcessorTycoon.GraphSystem.LegendText", "nameText").text, "<[^>]+>", "").Trim();
            var rawNumber = Field<TMP_Text>(legend, "ProcessorTycoon.GraphSystem.LegendText", "numberText").text;
            var number = Regex.Replace(rawNumber, "<[^>]+>", "").Trim();
            var dot = Field<Image>(legend, "ProcessorTycoon.GraphSystem.LegendText", "dot");
            var graphColor = Property<Color32>(graph, "ProcessorTycoon.GraphSystem.Graph", "LineColor");
            var legendColor = (Color32)dot.color;
            if (!graphColor.Equals(legendColor)) throw new AgentError("game_ui_mismatch", $"Rendered graph slot {index} color does not match its native legend row.");
            if (string.IsNullOrWhiteSpace(name) || name == "None") throw new AgentError("game_ui_mismatch", $"Rendered graph slot {index} has no named native legend row.");
            var item = new JObject
            {
                ["id"] = "series-" + index, ["index"] = index, ["name"] = name, ["legendValue"] = number,
                ["seriesColor"] = "#" + ColorUtility.ToHtmlStringRGBA(graphColor),
                ["pointCount"] = values.Length, ["values"] = JArray.FromObject(values)
            };
            if (financeDetailed != null)
            {
                item["flow"] = FinanceFlow(index, financeDetailed.Value);
                item["valueStyle"] = FinanceValueStyle(rawNumber, positive!, negative!, neutral!);
            }
            series.Add(item);
        }
        var display = Field<RectTransform>(charts[0], chartType, "displayWindow");
        var labels = new JArray(display.GetComponentsInChildren<TMP_Text>().Where(text => text.gameObject.activeInHierarchy && text.color.a >= .01f && !string.IsNullOrWhiteSpace(text.text)).Select(text =>
        {
            var position = (Vector2)display.InverseTransformPoint(text.rectTransform.position);
            return new JObject { ["text"] = text.text.Trim(), ["x"] = position.x, ["y"] = position.y };
        }).OrderBy(label => label["y"]!.Value<double>()).ThenBy(label => label["x"]!.Value<double>()));
        return new JObject
        {
            ["available"] = true, ["source"] = "native-render-backing", ["precision"] = "native-float-render-range",
            ["order"] = "oldest-to-newest", ["x"] = new JObject { ["cadence"] = cadence, ["semantics"] = cadence == "year" ? "native-calendar-year-axis" : "native-relative-period-axis" },
            ["y"] = new JObject { ["unit"] = unit }, ["axisLabels"] = labels, ["series"] = series, ["graphSlotCount"] = graphObjects.Length,
            ["seriesCount"] = series.Count, ["emptyGraphSlots"] = graphObjects.Length - series.Count,
            ["seriesGrouping"] = "ordered-native-graph-slots; duplicate names are distinct and not merged",
            ["emptySlotMeaning"] = "A native active graph slot whose rendered Values list is empty; it is not an omitted named history.",
            ["complete"] = true, ["boundary"] = "Only active Graph.Values after the native TakeLast render-range selection were read; RawValues and model histories were not accessed."
        };
    }

    private static bool ActiveUnder(Transform transform, string ancestorType)
    {
        if (!transform.gameObject.scene.IsValid()) return false;
        for (var current = transform; current != null; current = current.parent)
        {
            if (!current.gameObject.activeInHierarchy) return false;
            if (current.GetComponents<MonoBehaviour>().Any(component => component != null && component.GetType().FullName == ancestorType)) return true;
        }
        return false;
    }

    private static MonoBehaviour ActiveWindow(string windowType)
    {
        var windows = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == windowType && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy).ToArray();
        if (windows.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active {windowType}, found {windows.Length}.");
        return windows[0];
    }

    private static string FinanceFlow(int index, bool detailed) => detailed ? (index <= 4 ? "income" : "expense") : (index == 0 ? "income" : "expense");

    private static string FinanceValueStyle(string renderedText, string positive, string negative, string neutral)
    {
        var color = Regex.Match(renderedText, @"<color=(#[0-9A-Fa-f]{6,8})>", RegexOptions.IgnoreCase).Groups[1].Value;
        if (color.Equals(positive, StringComparison.OrdinalIgnoreCase)) return "positive";
        if (color.Equals(negative, StringComparison.OrdinalIgnoreCase)) return "negative";
        if (color.Equals(neutral, StringComparison.OrdinalIgnoreCase)) return "neutral";
        return "unknown";
    }

    private static object[] Items(object value, string field)
    {
        if (value is not IEnumerable items) throw new AgentError("game_ui_mismatch", $"Native rendered UI field {field} is not enumerable.");
        return items.Cast<object>().Where(item => item != null).ToArray();
    }

    private static float Numeric(object value)
    {
        if (value is not float number || float.IsNaN(number) || float.IsInfinity(number)) throw new AgentError("game_ui_mismatch", "Native rendered Graph.Values contains a non-finite or unexpected value.");
        return number;
    }

    private static T Field<T>(object component, string expectedType, string name) where T : class
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI field '{name}' is unavailable in this game version.");
        return value;
    }

    private static T Property<T>(object component, string expectedType, string name)
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI property '{name}' is unavailable in this game version.");
        return value;
    }

    private static bool FinanceMode(JObject view, bool period)
    {
        var contexts = GameUi.Texts(view).Select(t => (string?)t["context"] ?? "").Where(c => c.Contains("FinanceGraphWindow")).ToArray();
        if (period)
        {
            if (contexts.Any(c => c.Contains("Yearly"))) return true;
            if (contexts.Any(c => c.Contains("Monthly"))) return false;
            throw new AgentError("game_ui_mismatch", "The visible finance period cannot be identified from the native graph.");
        }
        if (contexts.Any(c => c.Contains("/LegendDetailed/"))) return true;
        if (contexts.Any(c => c.Contains("/LegendTotal/"))) return false;
        throw new AgentError("game_ui_mismatch", "The visible finance detail mode cannot be identified from the native legend.");
    }

    private static JObject Fields(JObject view, string context)
    {
        var result = new JObject();
        foreach (var node in GameUi.Controls(view).Where(c => ((string?)c["context"] ?? "").Contains(context) && (string?)c["role"] is "select" or "toggle"))
        {
            var key = Key((string)node["name"]!);
            var value = (string?)node["role"] == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
            var field = new JObject { ["value"] = value!.DeepClone() };
            if (node["options"] != null) field["options"] = node["options"]!.DeepClone();
            if (node["blockedReason"] != null) field["blockedReason"] = node["blockedReason"]!.DeepClone();
            result[key] = field;
        }
        return result;
    }

    private static string ControlValue(JObject view, string name)
    {
        var node = GameUi.One(GameUi.Controls(view).Where(c => (string?)c["name"] == name && (string?)c["role"] == "select"), name);
        return (string)node["options"]![node["value"]!.Value<int>()]!;
    }

    private static JObject Base(string kind, JObject content)
    {
        content.AddFirst(new JProperty("kind", kind));
        content.AddFirst(new JProperty("method", "game"));
        content["precision"] = "display-rounded";
        return content;
    }

    private static string[] Split(string text) => text.Split('|').Select(c => c.Trim()).ToArray();
    private static string Key(string text)
    {
        var words = Regex.Matches(text.Replace("C/T", "coresThreads").Replace("Pwr", "power"), @"[A-Za-z0-9]+").Cast<Match>().Select(m => m.Value).ToArray();
        if (words.Length == 0) return "value";
        return words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant()));
    }

    private static JToken Typed(string display)
    {
        var clean = Regex.Replace(display, "<[^>]+>", "").Trim();
        if (clean.Contains('…')) return clean;
        if (clean.EndsWith("%", StringComparison.Ordinal) && ParseNumber(clean.Substring(0, clean.Length - 1), out var percent)) return new JObject { ["value"] = percent, ["unit"] = "percent", ["display"] = clean };
        if (clean.StartsWith("$", StringComparison.Ordinal) || clean.StartsWith("-$", StringComparison.Ordinal))
        {
            var perMonth = clean.EndsWith("/m", StringComparison.Ordinal);
            var number = clean.TrimEnd('m', '/');
            var sign = number.StartsWith("-", StringComparison.Ordinal) ? -1 : 1;
            number = number.TrimStart('-').TrimStart('$');
            if (ParseNumber(number, out var money)) return new JObject { ["value"] = sign * money, ["currency"] = "$", ["period"] = perMonth ? "month" : null, ["display"] = clean };
        }
        var match = Regex.Match(clean, @"^([\d,.]+(?:[KMBT])?)\s*(KHz|kHz|MHz|GHz|Hz|W|mm²|KB|MB|GB|/m)?$");
        if (match.Success && ParseNumber(match.Groups[1].Value, out var value))
        {
            var unit = match.Groups[2].Value;
            if (unit == "/m") unit = "per-month";
            return unit.Length == 0 ? new JValue(value) : new JObject { ["value"] = value, ["unit"] = unit, ["display"] = clean };
        }
        return clean;
    }

    internal static JToken TypedDisplay(string display) => Typed(display);

    private static bool ParseNumber(string text, out double value)
    {
        text = text.Replace(",", "").Trim();
        var multiplier = 1m;
        if (text.Length > 0) multiplier = text[text.Length - 1] switch { 'K' => 1e3m, 'M' => 1e6m, 'B' => 1e9m, 'T' => 1e12m, _ => 1m };
        if (multiplier != 1m) text = text.Substring(0, text.Length - 1);
        // Decimal arithmetic: "4.06M" is 4060000, not 4059999.9999999995.
        var parsed = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number);
        value = parsed ? (double)(number * multiplier) : 0;
        return parsed;
    }

    private static bool TryFiniteNumber(JToken token, out double value)
    {
        if (token.Type is JTokenType.Integer or JTokenType.Float) value = token.Value<double>();
        else if (token.Type == JTokenType.String)
        {
            if (!double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        }
        else { value = 0; return false; }
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
