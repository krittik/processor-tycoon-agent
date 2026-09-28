using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// Version-sensitive UI mapping; no game assembly reference, hidden state or method invocation.
internal sealed class CpuGameApi : IGameModule
{
    internal const string Scope = "CreateCpuWindow";
    private static readonly Dictionary<string, (string Button, string Scope)> selectors = new()
    {
        ["package"] = ("Package", "PackageSelectionWindow"), ["process-node"] = ("Lithography", "LithographySelectionWindow"),
        ["architecture"] = ("Architecture", "ArchitectureSelectionWindow"), ["memory"] = ("Memory Technology", "MemorySelectionWindow")
    };
    private readonly GenericUi ui;
    private readonly Action<string> log;
    private string? developingName;
    private readonly HashSet<string> previousProjectGroups = new();
    public string[] Commands => new[] { "game.cpu-preview", "game.cpu-options", "game.cpu-select", "game.cpu-develop" };
    void IGameModule.Validate(Request request) => Validate(request);
    private sealed class Field
    {
        public string Key; public string Role; public string Name; public string Suffix; public string? Unit;
        public Field(string key, string role, string name, string suffix = "", string? unit = null) { Key = key; Role = role; Name = name; Suffix = suffix; Unit = unit; }
    }
    private static readonly Field[] fields =
    {
        new("name", "input", "Name Input"), new("coreCount", "select", "Core Count Dropdown"), new("memoryController", "select", "Memory Controller Dropdown"),
        new("overclock", "toggle", "Overclock"), new("smt", "toggle", "Smt"), new("automateCache", "toggle", "Automate Cache"),
        new("dieSizeMm2", "slider", "Slider", "/DieSizeSlider", "mm²"), new("frequencyMHz", "slider", "Slider", "/FrequencySlider", "MHz"),
        new("l1CacheKB", "slider", "Slider", "/L1CacheSlider", "KB"), new("l2CacheKB", "slider", "Slider", "/L2CacheSlider", "KB"), new("l3CacheKB", "slider", "Slider", "/L3CacheSlider", "KB")
    };

    public CpuGameApi(GenericUi ui, Action<string> log) { this.ui = ui; this.log = log; }
    public static object Schema => new
    {
        command = "game.cpu-preview", mapping = "Processor Tycoon 0.2.16a5 / English UI",
        parameters = fields.Select(f => new { key = f.Key, type = f.Role == "input" || f.Role == "select" ? "string" : f.Role == "toggle" ? "boolean" : "number", unit = f.Unit }).ToArray(),
        hardware = new { list = "game cpu-options KIND", choose = "game cpu-select KIND --value EXACT_NAME", kinds = selectors.Keys.ToArray(), paging = "Whole active UI catalog including offscreen cards; no manual scroll needed", filters = new[] { "showObsolete", "licensed" } },
        review = "game cpu-review EXACT_DRAFT_NAME --target-market MARKET --planned-price PRICE; returns current evidence, risk signals and a reviewId",
        commit = "game cpu-develop EXACT_DRAFT_NAME --review-id ID --decision-reason TEXT [--accept-missing-evidence true]; rechecks review state before native Develop",
        behavior = "Open/reuse visible CPU form; edit supplied fields only; leave open; NEVER press Develop. Slider units are matched to displayed values, not raw indexes.",
        unsupported = new[] { "hidden mutations", "automatic development/affordability decisions" }
    };

    public static void Validate(Request request)
    {
        if (request.Command == "game.cpu-develop")
        {
            GameUi.RequiredTarget(request);
            GameUi.Parameters(request, "reviewId", "decisionReason", "acceptMissingEvidence", "acknowledgeRisks");
            if (request.Parameters?["acknowledgeRisks"] != null && request.Parameters["acknowledgeRisks"]!.Type != JTokenType.String) throw new AgentError("invalid_value", "acknowledgeRisks must be a comma-separated list of risk signal kinds.");
            if (request.Value != null) throw new AgentError("invalid_request", "cpu-develop takes the current draft name only.");
            if (request.Parameters?["reviewId"] != null && (request.Parameters["reviewId"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)request.Parameters["reviewId"]))) throw new AgentError("invalid_value", "reviewId must be a non-empty string from cpu-review.");
            if (request.Parameters?["decisionReason"] != null && (request.Parameters["decisionReason"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)request.Parameters["decisionReason"]))) throw new AgentError("invalid_value", "decisionReason must explain the agent's decision.");
            if (request.Parameters?["acceptMissingEvidence"] != null && request.Parameters["acceptMissingEvidence"]!.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "acceptMissingEvidence must be boolean.");
            return;
        }
        if (request.Command == "game.cpu-select")
        {
            if (request.Target.Length == 0 || request.Parameters?.Count > 0) throw new AgentError("invalid_request", "cpu-select takes KIND --value EXACT_NAME, or a legacy option handle, without parameters.");
            if (selectors.ContainsKey(request.Target) && (request.Value?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)request.Value))) throw new AgentError("invalid_value", "cpu-select KIND requires --value EXACT_NAME from cpu-options.");
            if (!selectors.ContainsKey(request.Target) && request.Value != null) throw new AgentError("invalid_request", "Unknown hardware kind. Use package, process-node, architecture or memory.");
            return;
        }
        if (request.Command == "game.cpu-options")
        {
            if (!selectors.ContainsKey(request.Target)) throw new AgentError("invalid_request", "cpu-options requires package, process-node, architecture or memory.");
            foreach (var p in request.Parameters?.Properties() ?? Enumerable.Empty<JProperty>()) if (p.Name is not ("showObsolete" or "licensed") || p.Value.Type != JTokenType.Boolean) throw new AgentError("invalid_request", "cpu-options filters: showObsolete/ licensed booleans only.");
            if (request.Value != null && (!double.TryParse(request.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var delta) || double.IsNaN(delta) || double.IsInfinity(delta))) throw new AgentError("invalid_value", "Scroll value must be finite mouse-wheel units.");
            return;
        }
        if (request.Target.Length > 0) throw new AgentError("invalid_request", "Game commands do not take a handle. Use --json or --file for parameters.");
        if (request.Command == "game.cpu-read" && request.Parameters?.Count > 0) throw new AgentError("invalid_request", "cpu-read does not edit. Use cpu-preview for parameters.");
        foreach (var property in request.Parameters?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            var field = fields.SingleOrDefault(f => f.Key == property.Name);
            if (field == null) throw new AgentError("unsupported_parameter", $"Unknown CPU parameter '{property.Name}'. Use game cpu-schema; no input was dispatched.");
            var type = property.Value.Type;
            var valid = field.Role == "toggle" ? type == JTokenType.Boolean : field.Role is "input" or "select" ? type == JTokenType.String : type is JTokenType.Integer or JTokenType.Float && !double.IsNaN(property.Value.Value<double>()) && !double.IsInfinity(property.Value.Value<double>()) && property.Value.Value<double>() >= 0;
            if (!valid) throw new AgentError("invalid_value", $"Invalid value/type for {field.Key}. See game cpu-schema.");
        }
    }

    private JObject Read(string scope) => scope == "DesktopButtons" ? new GameUi(ui).Catalog(scope) : JObject.FromObject(ui.Observe(scope, 0, 200), JsonSerializer.Create(Wire.Settings));
    private static JObject[] Controls(JObject view) => view["controls"]!.OfType<JObject>().ToArray();
    private static JObject One(IEnumerable<JObject> candidates, string description)
    {
        var matches = candidates.ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one {description}, found {matches.Length}. Read Generic UI; no guessed fallback was used.");
        return matches[0];
    }
    private static JObject Control(JObject view, Field field) => One(Controls(view).Where(c => (string?)c["role"] == field.Role && (string?)c["name"] == field.Name && ((string?)c["context"] ?? "").EndsWith(field.Suffix, StringComparison.Ordinal)), field.Key);
    private static Request Action(string command, JObject node, JToken? value = null)
    {
        if (node["blockedReason"] != null) throw new AgentError("not_interactable", $"{node["label"]}: {node["blockedReason"]}. Use generic UI to resolve the native window/disabled state.");
        return GameUi.Action(command, node, value);
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        if (request.Command != "game.cpu-select" || selectors.ContainsKey(request.Target))
        {
            var wantedScope = request.Command is "game.cpu-options" or "game.cpu-select" ? selectors[request.Target].Scope : null;
            foreach (var selector in selectors.Values.Where(s => s.Scope != wantedScope))
            {
                var opened = Read(selector.Scope);
                if (Controls(opened).Length == 0) continue;
                yield return Action("ui.click", One(Controls(opened).Where(c => (string?)c["name"] == "Close" && (string?)c["role"] == "button"), "CPU selector Close"));
            }
        }
        if (request.Command == "game.cpu-develop")
        {
            if (request.Parameters?["reviewId"] == null) yield break;
            var draft = Preview();
            developingName = (string?)draft["settings"]!["name"]!["value"];
            if (developingName != request.Target) throw new AgentError("context_changed", "Current draft name differs from the requested CPU. Read/preview it before developing.");
            CpuReviewGameApi.RequireCurrent(request, draft);
            foreach (var text in GameUi.Texts(Read("Side Window (dark)"))) if (text["group"] != null) previousProjectGroups.Add((string)text["group"]!);
            var develop = One(Controls(Read(Scope)).Where(c => (string?)c["name"] == "Create" && (string?)c["label"] == "Develop"), "Develop button");
            yield return Action("ui.click", develop);
            if (!Controls(Read(Scope)).Any(c => (string?)c["name"] == "Create"))
                foreach (var step in new ResearchGameApi(new GameUi(ui)).Prepare(new Request { Command = "game.projects-list" })) yield return step;
            yield break;
        }
        if (request.Command == "game.cpu-select")
        {
            var target = request.Target;
            if (selectors.ContainsKey(request.Target))
            {
                foreach (var step in Prepare(new Request { Command = "game.cpu-options", Target = request.Target })) yield return step;
                var catalog = new GameUi(ui).Catalog(selectors[request.Target].Scope);
                var candidates = OptionRows(catalog).OfType<JObject>().Where(o => (string?)o["name"] == (string?)request.Value && (bool?)o["partial"] == false).ToArray();
                if (candidates.Length == 0) throw new AgentError("not_found", $"No card '{request.Value}' in the current {request.Target} catalog. Obsolete and licensed cards are hidden by the picker filters: list them with game cpu-options {request.Target} --show-obsolete true (or --licensed true), or add --show-obsolete true / --licensed true to cpu-select (CLI). No card was selected.");
                var option = One(candidates, $"catalog {request.Target} '{request.Value}'");
                yield return GameUi.Click(One(Controls(catalog).Where(c => (string?)c["handle"] == (string?)option["handle"]), "exact hardware card"));
                yield return Action("ui.click", One(Controls(Read(selectors[request.Target].Scope)).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Select"), "native Select button"));
                var picker = Read(selectors[request.Target].Scope);
                if (Controls(picker).Length > 0) yield return Action("ui.click", One(Controls(picker).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Close"), "selected CPU picker Close"));
                yield break;
            }
            ui.Validate(new Request { Command = "ui.click", Target = target });
            var node = JObject.FromObject(ui.Inspect(target), JsonSerializer.Create(Wire.Settings))["control"]!.ToObject<JObject>()!;
            var selector = selectors.Values.SingleOrDefault(s => ((string?)node["context"] ?? "").Contains("/" + s.Scope + "/"));
            if (selector.Scope == null || !((string?)node["name"] ?? "").StartsWith("Hardware Option", StringComparison.Ordinal)) throw new AgentError("invalid_target", "cpu-select requires a hardware option handle from cpu-options.");
            yield return Action("ui.click", node);
            var selected = Read(selector.Scope);
            yield return Action("ui.click", One(Controls(selected).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Select"), "native Select button"));
            selected = Read(selector.Scope);
            if (Controls(selected).Length > 0) yield return Action("ui.click", One(Controls(selected).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Close"), "selected CPU picker Close"));
            yield break;
        }
        if (request.Command == "game.cpu-options" && Controls(Read(selectors[request.Target].Scope)).Length > 0)
        {
            foreach (var step in OptionFilters(request)) yield return step;
            yield break;
        }
        var view = Read(Scope);
        if (Controls(view).Length == 0)
        {
            var chooser = Read("CreateHardwareWindow");
            if (Controls(chooser).Length == 0)
            {
                var desktop = Read("DesktopButtons");
                yield return Action("ui.click", One(Controls(desktop).Where(c => (string?)c["name"] == "Create Hardware Desktop" && (string?)c["role"] == "button"), "Create Hardware desktop button"));
                chooser = Read("CreateHardwareWindow");
            }
            yield return Action("ui.click", One(Controls(chooser).Where(c => (string?)c["name"] == "New Cpu" && (string?)c["role"] == "button"), "New CPU button"));
            view = Read(Scope);
        }
        // Require the known form structure before editing it; do not reinterpret an unfamiliar UI.
        foreach (var field in fields) Control(view, field);
        if (request.Command == "game.cpu-options")
        {
            yield return Action("ui.click", One(Controls(view).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == selectors[request.Target].Button), request.Target));
            foreach (var step in OptionFilters(request)) yield return step;
            yield break;
        }
        if (request.Parameters?["name"] is JValue name && name.Value<string>()!.Length > Control(view, fields[0])["range"]!["characterLimit"]!.Value<int>()) throw new AgentError("invalid_value", "CPU name exceeds the native character limit; no fields edited.");
        foreach (var field in fields)
        {
            var value = request.Parameters?[field.Key];
            if (value == null) continue;
            var node = Control(Read(Scope), field);
            if (field.Role == "slider")
            {
                foreach (var step in SetDisplayed(field, value.Value<double>(), node)) yield return step;
            }
            else
            {
                var current = field.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
                if (JToken.DeepEquals(current, value)) continue;
                yield return Action(field.Role == "select" ? "ui.select" : "ui.set", node, value);
                var after = Control(Read(Scope), field);
                var actual = field.Role == "select" ? after["options"]![after["value"]!.Value<int>()] : after["value"];
                if (!JToken.DeepEquals(actual, value)) throw new AgentError("value_not_applied", $"Native validation changed {field.Key} to '{actual}'. Inspect the draft before continuing.");
            }
        }
        // A later toggle/core setting can alter previous values. Check all requested values again.
        view = Read(Scope);
        foreach (var field in fields.Where(f => request.Parameters?[f.Key] != null))
        {
            var node = Control(view, field);
            var expected = request.Parameters![field.Key]!;
            var matches = field.Role == "slider" ? Match(DisplayNumber(node, field.Unit!), expected.Value<double>()) : JToken.DeepEquals(field.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"], expected);
            if (!matches) throw new AgentError("value_not_applied", $"{field.Key} changed during native recomputation. Inspect the current draft; development was not started.");
        }
    }

    private IEnumerable<Request> OptionFilters(Request request)
    {
        var scope = selectors[request.Target].Scope;
        foreach (var pair in new[] { ("showObsolete", "Show Obsolete"), ("licensed", "Show Licensed") })
        {
            if (request.Parameters?[pair.Item1] is not JToken value) continue;
            var node = One(Controls(Read(scope)).Where(c => (string?)c["role"] == "toggle" && (string?)c["name"] == pair.Item2), pair.Item1);
            if (!JToken.DeepEquals(node["value"], value)) yield return Action("ui.set", node, value);
        }
        if (request.Value != null) yield return Action("ui.scroll", One(Controls(Read(scope)).Where(c => (string?)c["role"] == "scroll"), "hardware options scroll"), request.Value);
    }

    public JObject Options(string kind)
    {
        var view = new GameUi(ui).Catalog(selectors[kind].Scope);
        return new JObject { ["method"] = "game", ["kind"] = kind, ["offscreenIncluded"] = true, ["catalogScope"] = "active-player-browsable", ["complete"] = !(bool)view["more"]!, ["truncated"] = view["more"]!.DeepClone(), ["options"] = OptionRows(view), ["filters"] = JArray.FromObject(Controls(view).Where(c => (string?)c["role"] == "toggle").Select(c => new { name = (string?)c["label"], value = c["value"], blockedReason = c["blockedReason"] })), ["next"] = $"Choose game cpu-select {kind} --value EXACT_NAME. No manual scroll is needed for returned cards." };
    }

    private static JArray OptionRows(JObject view)
    {
        var items = new JArray();
        foreach (var node in Controls(view).Where(c => ((string?)c["name"] ?? "").StartsWith("Hardware Option", StringComparison.Ordinal) && c["group"] != null))
        {
            var texts = view["texts"]!.OfType<JObject>().Where(t => (string?)t["group"] == (string?)node["group"]).Select(t => t["text"]!.Value<string>()!).ToArray();
            var content = texts.Length > 0 ? string.Join(" | ", texts) : (string)node["label"]!;
            var segments = content.Split('|').Select(s => s.Trim()).ToArray();
            var properties = new JObject();
            foreach (var segment in segments.Skip(1))
            {
                var separator = segment.IndexOf(':');
                if (separator > 0 && !properties.ContainsKey(segment.Substring(0, separator))) properties[segment.Substring(0, separator)] = segment.Substring(separator + 1).Trim();
            }
            items.Add(new JObject { ["handle"] = node["handle"]!.DeepClone(), ["name"] = segments[0], ["properties"] = properties, ["display"] = content, ["partial"] = content.Contains('…') });
        }
        return items;
    }

    public JObject Result(Request request)
    {
        if (request.Command == "game.cpu-options") return Options(request.Target);
        if (request.Command != "game.cpu-develop") return Preview();
        if (request.Parameters?["reviewId"] == null) return new JObject { ["method"] = "game", ["outcome"] = "review_required", ["developmentStarted"] = false, ["next"] = "Run game cpu-review EXACT_DRAFT_NAME --target-market MARKET --planned-price PRICE, inspect its evidence, then repeat cpu-develop with --review-id and --decision-reason. No Develop input was sent." };
        var side = new GameUi(ui).Catalog("Side Window (dark)");
        var title = GameUi.Texts(side).Where(t => ((string?)t["context"] ?? "").Contains("/ProjectUI(") && ((string?)t["text"] ?? "").Split('|')[0].Trim() == developingName && !previousProjectGroups.Contains((string?)t["group"] ?? "")).ToArray();
        if (title.Length == 1)
        {
            var rows = new JArray(GameUi.Texts(side).Where(t => (string?)t["group"] == (string?)title[0]["group"]).Select(t => t["text"]!.DeepClone()));
            var researchSpend = BalanceSnapshot.Read()["items"]?.OfType<JObject>().FirstOrDefault(i => (string?)i["key"] == "research")?["display"]?.ToString();
            var researchIdle = BalanceSnapshot.Money(researchSpend) is null or 0m;
            return new JObject { ["method"] = "game", ["outcome"] = "development_started", ["developmentStarted"] = true, ["name"] = developingName, ["project"] = rows, ["researchSpend"] = researchSpend,
                ["next"] = "Development is active. Inspect projects and manage game time; do not repeat cpu-develop." + (researchIdle ? " No research is running: development does not block research, and technologies finished before release (for example larger wafers, which upgrade existing lines) can already help this product." : "") };
        }
        var confirmation = Read("Confirmation");
        var present = Controls(confirmation).Length > 0;
        return new JObject { ["method"] = "game", ["outcome"] = present ? "confirmation_required" : "input_dispatched", ["name"] = developingName, ["confirmation"] = GameUi.Compact(confirmation), ["next"] = present ? "Read the native confirmation and choose its explicit action through game dialog-choose. Development is not yet verified." : "Develop was dispatched but a new project row is not visible. Inspect projects; do not blindly repeat." };
    }

    private IEnumerable<Request> SetDisplayed(Field field, double wanted, JObject initial)
    {
        var originalDisplay = DisplayNumber(initial, field.Unit!);
        if (Match(originalDisplay, wanted)) yield break;
        var original = initial["value"]!.Value<double>();
        var rawMin = initial["range"]!["rawMin"]!.Value<double>();
        var rawMax = initial["range"]!["rawMax"]!.Value<double>();
        var integral = initial["range"]!["wholeNumbers"]!.Value<bool>();
        yield return Action("ui.set", initial, new JValue(rawMax));
        var node = Control(Read(Scope), field);
        var physicalMax = DisplayNumber(node, field.Unit!);
        if (Match(physicalMax, wanted)) yield break;
        yield return Action("ui.set", node, new JValue(rawMin));
        node = Control(Read(Scope), field);
        var physicalMin = DisplayNumber(node, field.Unit!);
        if (Match(physicalMin, wanted)) yield break;

        if (wanted > physicalMin && wanted < physicalMax)
        {
            var low = integral ? rawMin + 1 : rawMin;
            var high = integral ? rawMax - 1 : rawMax;
            // Guess from observed endpoint displays, never a hidden formula; verify natively before accepting.
            var estimate = rawMin + (wanted - physicalMin) / (physicalMax - physicalMin) * (rawMax - rawMin);
            if (integral) estimate = Math.Round(estimate);
            estimate = Math.Max(low, Math.Min(high, estimate));
            if (low <= high)
            {
                yield return Action("ui.set", node, new JValue(estimate));
                node = Control(Read(Scope), field);
                var actual = DisplayNumber(node, field.Unit!);
                if (Match(actual, wanted)) yield break;
                if (actual < wanted) low = integral ? estimate + 1 : estimate;
                else high = integral ? estimate - 1 : estimate;
            }
            for (var attempt = 0; attempt < 22 && low <= high; attempt++)
            {
                var raw = integral ? Math.Floor((low + high) / 2) : (low + high) / 2;
                yield return Action("ui.set", node, new JValue(raw));
                node = Control(Read(Scope), field);
                var actual = DisplayNumber(node, field.Unit!);
                if (Match(actual, wanted)) yield break;
                if (actual < wanted) low = integral ? raw + 1 : raw;
                else high = integral ? raw - 1 : raw;
            }
        }

        yield return Action("ui.set", node, new JValue(original));
        var restored = Control(Read(Scope), field);
        if (!Match(restored["value"]!.Value<double>(), original) || !Match(DisplayNumber(restored, field.Unit!), originalDisplay)) throw new AgentError("outcome_unverified", $"{field.Key} could not be represented and its native slider did not restore exactly. Development was not started; inspect the current draft.");
        var range = $"{physicalMin.ToString(CultureInfo.InvariantCulture)}..{physicalMax.ToString(CultureInfo.InvariantCulture)} {field.Unit}";
        var reason = wanted < physicalMin || wanted > physicalMax ? "is outside" : "is inside but not representable within";
        throw new AgentError("unrepresentable_value", $"{field.Key}={wanted.ToString(CultureInfo.InvariantCulture)} {field.Unit} {reason} the current native physical range {range}, observed at both slider endpoints. That slider was restored; earlier draft edits remain and development was not started. Use cpu-read for the current draft.");
    }

    private static bool Match(double actual, double wanted) => Math.Abs(actual - wanted) <= Math.Max(1e-7, Math.Abs(wanted) * 1e-7);
    private static double DisplayNumber(JObject node, string unit)
    {
        var display = (string?)node["displayValue"] ?? "";
        var match = Regex.Match(display, @"^\s*([\d,.]+)\s*(GHz|MHz|KHz|kHz|Hz|GB|MB|KB|B|mm²)(?:\s|$)");
        if (!match.Success || display.Contains('…')) throw new AgentError("game_ui_mismatch", $"Cannot read an unambiguous {unit} value from '{display}'. Use generic inspect.");
        var value = double.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        var source = match.Groups[2].Value;
        var factor = unit == "MHz" ? source switch { "GHz" => 1000, "MHz" => 1, "kHz" or "KHz" => .001, "Hz" => .000001, _ => double.NaN } : unit == "KB" ? source switch { "GB" => 1048576, "MB" => 1024, "KB" => 1, "B" => 1d / 1024, _ => double.NaN } : source == unit ? 1 : double.NaN;
        if (double.IsNaN(factor)) throw new AgentError("game_ui_mismatch", $"Unexpected unit in '{display}'.");
        return value * factor;
    }

    public JObject Preview()
    {
        // Read the active draft's complete UI labels even when its hardware picker overlaps it.
        var view = new GameUi(ui).Catalog(Scope);
        var settings = new JObject();
        foreach (var field in fields)
        {
            var node = Control(view, field);
            var item = new JObject { ["value"] = field.Role == "slider" ? new JValue(DisplayNumber(node, field.Unit!)) : field.Role == "select" ? node["options"]![node["value"]!.Value<int>()]!.DeepClone() : node["value"]!.DeepClone() };
            if (field.Unit != null) { item["unit"] = field.Unit; item["display"] = node["displayValue"]!.DeepClone(); }
            if (node["options"] != null) item["options"] = node["options"]!.DeepClone();
            if (node["blockedReason"] != null) item["blockedReason"] = node["blockedReason"]!.DeepClone();
            settings[field.Key] = item;
        }
        string Text(string suffix, bool wholeRow = false)
        {
            var matches = view["texts"]!.OfType<JObject>().Where(t => ((string?)t["context"] ?? "").EndsWith(suffix, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || ((string?)matches[0]["text"] ?? "").Contains('…')) throw new AgentError("game_ui_mismatch", $"CPU result {suffix} is missing, ambiguous or clipped. Expose the form and use generic observe.");
            var content = matches[0]["text"]!.Value<string>()!;
            return wholeRow ? content : content.Split('|').Last().Trim();
        }
        var cost = Text("/ProjectCost");
        var costs = Regex.Matches(cost, @"\$[\d,.]+[KMBT]?").Cast<Match>().Select(m => Money(m.Value)).ToArray();
        if (costs.Length != 2) throw new AgentError("game_ui_mismatch", "Expected total and monthly project cost; use generic UI for this game version.");
        var develop = One(Controls(view).Where(c => (string?)c["name"] == "Create" && (string?)c["role"] == "button" && (string?)c["label"] == "Develop"), "Develop button");
        var specs = new JObject();
        foreach (var pair in new[] { ("mips", "/IPS"), ("instructionsPerCycle", "/IPC"), ("power", "/PowerConsumption"), ("temperature", "/Temperature"), ("yield", "/FinalYield"), ("unitCost", "/UnitCost") }) specs[pair.Item1] = Text(pair.Item2);
        var hardware = new JObject();
        foreach (var selector in selectors) hardware[selector.Key] = One(Controls(view).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == selector.Value.Button), selector.Key)["label"]!.DeepClone();
        var details = new JObject();
        foreach (var pair in new[] { ("package", "/Texts/Package"), ("transistors", "/Transistors"), ("safeTemperature", "/SafeTemperature"), ("multicoreRatio", "/MulticoreRatio"), ("bottleneck", "/Bottleneck"), ("cacheCapacity", "/CacheCapacity"), ("cacheEfficiency", "/CacheEfficiency") }) details[pair.Item1] = Text(pair.Item2, true);
        log("/cpu-read · Draft");
        CompanyAdvisories.NoteProjectCost(costs[0]);
        return new JObject
        {
            ["method"] = "game", ["developmentStarted"] = false, ["settings"] = settings, ["hardware"] = hardware, ["specs"] = specs, ["details"] = details,
            ["project"] = new JObject { ["totalCost"] = costs[0], ["monthlyCost"] = costs[1], ["currency"] = "$", ["costDisplay"] = cost, ["duration"] = Text("/ProjectTime"), ["completionDate"] = Text("/ConclusionDate") },
            ["develop"] = new JObject { ["handle"] = develop["handle"]!.DeepClone(), ["enabled"] = develop["blockedReason"] == null },
            ["precision"] = "display-rounded", ["next"] = "Draft only. Run game cpu-review EXACT_DRAFT_NAME --target-market MARKET --planned-price PRICE; inspect evidence and risks before cpu-develop with its reviewId."
        };
    }

    private static decimal Money(string display)
    {
        var suffix = display.Last();
        var multiplier = suffix switch { 'K' => 1000m, 'M' => 1000000m, 'B' => 1000000000m, 'T' => 1000000000000m, _ => 1m };
        return decimal.Parse(display.Substring(1, display.Length - 1 - (multiplier == 1 ? 0 : 1)).Replace(",", ""), CultureInfo.InvariantCulture) * multiplier;
    }
}
