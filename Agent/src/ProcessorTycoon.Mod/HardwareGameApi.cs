using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// English 0.2.16a5 visible Create New Hardware / Socket workflow.
internal sealed class HardwareGameApi : IGameModule
{
    private const string ChooserScope = "CreateHardwareWindow";
    private const string FormScope = "CreateSocketWindow";
    private const string CatalogScope = "BasePackageSelectionWindow";
    private const string RenameScope = "SocketEditWindow";
    private readonly GameUi ui;
    private HashSet<int> projectsBeforeCommit = new();
    private int socketsWithResultNameBeforeCommit;
    private bool refinementProjectExpected;
    private int renameOldCountBefore;
    private int renameNewCountBefore;

    public HardwareGameApi(GameUi ui) => this.ui = ui;
    public string[] Commands => new[] { "game.hardware-read", "game.socket-list", "game.socket-read", "game.socket-preview", "game.socket-create", "game.socket-refine", "game.socket-rename" };

    public void Validate(Request request)
    {
        if (request.Value != null) throw new AgentError("unsupported_parameter", $"{request.Command} does not accept --value.");
        switch (request.Command)
        {
            case "game.hardware-read":
            case "game.socket-list":
                NoTarget(request); GameUi.Parameters(request); break;
            case "game.socket-read":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); break;
            case "game.socket-preview":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "name", "pins", "priority", "investmentPercent"); ValidateDraft(request, requireName: false, allowNameAndPins: true); break;
            case "game.socket-create":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "name", "pins", "priority", "investmentPercent"); ValidateDraft(request, requireName: true, allowNameAndPins: true); break;
            case "game.socket-refine":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "priority", "investmentPercent"); ValidateDraft(request, requireName: false, allowNameAndPins: false); break;
            case "game.socket-rename":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "name"); RequireName(request, required: true); break;
            default: throw new AgentError("unsupported_command", $"Hardware module does not implement {request.Command}.");
        }
        if (request.Hidden && request.Command is not ("game.hardware-read" or "game.socket-list" or "game.socket-read")) throw new AgentError("hidden_not_supported", "Socket mutations use visible native windows.");
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        if (request.Command == "game.hardware-read")
        {
            foreach (var step in EnsureChooser(request)) yield return step;
            yield break;
        }

        if (request.Command is "game.socket-list" or "game.socket-read")
        {
            foreach (var step in EnsureCatalog(request)) yield return step;
            if (request.Command == "game.socket-read") SocketCard(GameUi.RequiredTarget(request));
            yield break;
        }

        if (request.Command == "game.socket-rename")
        {
            foreach (var step in EnsureCatalog(request)) yield return step;
            var target = GameUi.RequiredTarget(request);
            var card = SocketCard(target);
            if (!(bool)card["custom"]!) throw new AgentError("not_interactable", $"'{target}' is a base package, not a renameable custom socket.");
            var wantedName = request.Parameters!["name"]!.Value<string>()!;
            var before = SocketCards(ui.Catalog(CatalogScope));
            renameOldCountBefore = before.Count(c => (string?)c["name"] == target);
            renameNewCountBefore = before.Count(c => (string?)c["name"] == wantedName);
            var edit = GameUi.One(GameUi.Controls(ui.Catalog(CatalogScope)).Where(c => (string?)c["group"] == (string?)card["group"] && (string?)c["name"] == "Edit"), $"Edit for socket '{target}'");
            yield return GameUi.Click(edit);
            var rename = ui.Read(RenameScope);
            var input = Control(rename, "Name Input", "input");
            var wanted = request.Parameters!["name"]!;
            if (!JToken.DeepEquals(input["value"], wanted)) yield return GameUi.Set(input, wanted);
            rename = ui.Read(RenameScope);
            if ((string?)Control(rename, "Name Input", "input")["value"] != wanted.Value<string>()) throw new AgentError("value_not_applied", "Native socket rename validation did not retain the requested name.");
            yield return GameUi.Click(Control(rename, "Confirm", "button"));
            yield break;
        }

        var targetName = GameUi.RequiredTarget(request);
        foreach (var step in EnsureCatalog(request)) yield return step;
        var selectedCard = SocketCard(targetName);
        var isCustom = (bool)selectedCard["custom"]!;
        if (request.Command == "game.socket-create" && isCustom) throw new AgentError("invalid_target", "socket-create requires a base package; use socket-refine for an existing custom socket.");
        if (request.Command == "game.socket-refine" && !isCustom) throw new AgentError("invalid_target", "socket-refine requires an existing custom socket; use socket-create for a base package.");
        if (request.Command == "game.socket-create")
        {
            var resultName = request.Parameters!["name"]!.Value<string>()!;
            socketsWithResultNameBeforeCommit = SocketCards(ui.Catalog(CatalogScope)).Count(c => (string?)c["name"] == resultName);
        }
        foreach (var step in SelectCard(selectedCard)) yield return step;
        foreach (var step in ApplyDraft(request)) yield return step;
        if (request.Command == "game.socket-preview") yield break;

        projectsBeforeCommit = ResearchGameApi.ProjectStates().Keys.ToHashSet();
        var commit = Control(ui.Read(FormScope), "Create", "button");
        if (commit["blockedReason"] != null) throw new AgentError("not_interactable", $"Native socket action is unavailable: {commit["label"]}. Adjust the draft; no commit was dispatched.");
        var commitLabel = (string?)commit["label"] ?? "";
        refinementProjectExpected = request.Command == "game.socket-refine" || commitLabel == "Create & Refine" || commitLabel.StartsWith("Refine (", StringComparison.Ordinal);
        yield return GameUi.Click(commit);
        foreach (var step in EnsureCatalog(new Request { Command = "game.socket-list" })) yield return step;
    }

    public JObject Result(Request request)
    {
        if (request.Command == "game.hardware-read") return HardwareRead();
        if (request.Command == "game.socket-list") return SocketList();
        if (request.Command == "game.socket-read") return SocketResult(SocketCard(GameUi.RequiredTarget(request)));
        if (request.Command == "game.socket-preview") return Preview();
        if (request.Command == "game.socket-rename")
        {
            var wanted = request.Parameters!["name"]!.Value<string>()!;
            var catalog = SocketCards(ui.Catalog(CatalogScope));
            var oldAfter = catalog.Count(c => (string?)c["name"] == request.Target);
            var matches = catalog.Where(c => (string?)c["name"] == wanted).ToArray();
            var editorClosed = GameUi.Controls(ui.Read(RenameScope)).Length == 0;
            var sameName = string.Equals(request.Target, wanted, StringComparison.Ordinal);
            var renamed = editorClosed && matches.Length == 1 && (sameName ? renameOldCountBefore == 1 : oldAfter == renameOldCountBefore - 1 && matches.Length == renameNewCountBefore + 1);
            return new JObject { ["method"] = "game", ["renamed"] = renamed, ["oldName"] = request.Target, ["name"] = wanted, ["editorClosed"] = editorClosed, ["socket"] = matches.Length == 1 ? SocketResult(matches[0]) : null, ["outcome"] = renamed ? "completed" : "outcome_unverified" };
        }

        var resultName = request.Command == "game.socket-create" ? request.Parameters!["name"]!.Value<string>()! : request.Target;
        var cards = SocketCards(ui.Catalog(CatalogScope));
        var matchesAfter = cards.Where(c => (string?)c["name"] == resultName).ToArray();
        var newProjects = ResearchGameApi.ProjectStates().Where(p => !projectsBeforeCommit.Contains(p.Key)).Select(p => new JObject { ["name"] = p.Value.Name, ["status"] = p.Value.Status, ["timeLeftDays"] = p.Value.TimeLeftDays }).ToArray();
        if (request.Command == "game.socket-create")
        {
            var countBefore = socketsWithResultNameBeforeCommit;
            var created = matchesAfter.Length == countBefore + 1;
            var createProjectMatches = newProjects.Where(p => (string?)p["name"] == resultName).ToArray();
            var refinementVerified = !refinementProjectExpected || createProjectMatches.Length == 1;
            var outcome = !created ? "outcome_unverified" : refinementVerified ? "completed" : "partial_failure";
            return new JObject { ["method"] = "game", ["created"] = created, ["name"] = resultName, ["socketCountBefore"] = countBefore, ["socketCountAfter"] = matchesAfter.Length, ["socket"] = matchesAfter.Length == 1 ? SocketResult(matchesAfter[0]) : null, ["refinementExpected"] = refinementProjectExpected, ["refinementStarted"] = createProjectMatches.Length == 1, ["projectsStarted"] = new JArray(newProjects), ["outcome"] = outcome, ["next"] = !created ? "Native commit was dispatched but the socket-card count did not increase as expected." : refinementProjectExpected && createProjectMatches.Length != 1 ? "Socket creation is verified, but its expected refinement project is not uniquely visible. Inspect Projects before retrying anything." : refinementProjectExpected ? "Socket created and native refinement project verified." : "Socket created immediately; the native commit label did not indicate a refinement project." };
        }
        var projectMatches = newProjects.Where(p => (string?)p["name"] == resultName).ToArray();
        return new JObject { ["method"] = "game", ["refinementStarted"] = projectMatches.Length == 1, ["name"] = resultName, ["project"] = projectMatches.Length == 1 ? projectMatches[0] : null, ["outcome"] = projectMatches.Length == 1 ? "completed" : "outcome_unverified", ["next"] = "Refinement is a timed project; completion is not implied." };
    }

    private IEnumerable<Request> EnsureChooser(Request request)
    {
        if (GameUi.Controls(ui.Read(ChooserScope)).Length > 0) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Create New Hardware is not already visible.");
        var desktop = ui.Read("DesktopButtons");
        yield return GameUi.Click(GameUi.One(GameUi.Controls(desktop).Where(c => (string?)c["name"] == "Create Hardware Desktop" && (string?)c["role"] == "button"), "Create Hardware desktop button"));
        if (GameUi.Controls(ui.Read(ChooserScope)).Length == 0) throw new AgentError("game_ui_mismatch", "Create New Hardware did not expose its native controls.");
    }

    private IEnumerable<Request> EnsureForm(Request request)
    {
        if (GameUi.Controls(ui.Read(FormScope)).Length > 0) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Socket Creation is not already visible.");
        foreach (var step in EnsureChooser(request)) yield return step;
        var chooser = ui.Read(ChooserScope);
        var open = GameUi.One(GameUi.Controls(chooser).Where(c => (string?)c["name"] == "New Socket" && (string?)c["role"] == "button"), "New Socket button");
        yield return GameUi.Click(open);
        if (GameUi.Controls(ui.Read(FormScope)).Length == 0) throw new AgentError("game_ui_mismatch", "Socket Creation did not expose its native controls.");
    }

    private IEnumerable<Request> EnsureCatalog(Request request)
    {
        if (GameUi.Controls(ui.Catalog(CatalogScope)).Any(c => (string?)c["name"] == "Select")) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Select Base Socket is not already visible.");
        foreach (var step in EnsureForm(request)) yield return step;
        yield return GameUi.Click(Control(ui.Read(FormScope), "Package", "button"));
        if (!GameUi.Controls(ui.Catalog(CatalogScope)).Any(c => (string?)c["name"] == "Select")) throw new AgentError("game_ui_mismatch", "Select Base Socket did not expose its native catalog.");
    }

    private IEnumerable<Request> SelectCard(JObject card)
    {
        yield return GameUi.Click((JObject)card["node"]!);
        var catalog = ui.Catalog(CatalogScope);
        yield return GameUi.Click(Control(catalog, "Select", "button"));
        if (BaseName(ui.Read(FormScope)) != (string?)card["name"]) throw new AgentError("value_not_applied", $"Native base selector did not retain '{card["name"]}'.");
    }

    private IEnumerable<Request> ApplyDraft(Request request)
    {
        if (request.Parameters?["name"] is JToken name)
        {
            var node = Control(ui.Read(FormScope), "Name Input", "input");
            if ((string?)node["value"] != name.Value<string>()) yield return GameUi.Set(node, name);
        }
        if (request.Parameters?["priority"] is JToken priority)
        {
            var node = Control(ui.Read(FormScope), "Priority Dropdown", "select");
            if ((string?)node["options"]![node["value"]!.Value<int>()] != priority.Value<string>()) yield return GameUi.Select(node, priority.Value<string>()!);
        }
        if (request.Parameters?["investmentPercent"] is JToken investment)
        {
            var raw = investment.Value<double>() / 100d;
            var node = Slider("InvestmentSlider");
            if (Math.Abs(node["value"]!.Value<double>() - raw) > 1e-7) yield return GameUi.Set(node, new JValue(raw));
        }
        if (request.Parameters?["pins"] is JToken pins)
        {
            foreach (var step in SetPins(pins.Value<int>())) yield return step;
        }
        var view = ui.Read(FormScope);
        if (request.Parameters?["name"] is JToken expectedName && (string?)Control(view, "Name Input", "input")["value"] != expectedName.Value<string>()) throw new AgentError("value_not_applied", "Native validation changed the requested socket name.");
        if (request.Parameters?["priority"] is JToken expectedPriority)
        {
            var node = Control(view, "Priority Dropdown", "select");
            if ((string?)node["options"]![node["value"]!.Value<int>()] != expectedPriority.Value<string>()) throw new AgentError("value_not_applied", "Native priority selection changed.");
        }
        if (request.Parameters?["investmentPercent"] is JToken expectedInvestment && Math.Abs(Slider("InvestmentSlider")["value"]!.Value<double>() * 100d - expectedInvestment.Value<double>()) > 1e-5) throw new AgentError("value_not_applied", "Native investment slider did not retain the requested normalized percentage.");
        if (request.Parameters?["pins"] is JToken expectedPins && DisplayPins(Slider("PinCountSlider")) != expectedPins.Value<int>()) throw new AgentError("value_not_applied", "Native pin count changed after form recomputation.");
    }

    private IEnumerable<Request> SetPins(int wanted)
    {
        var node = Slider("PinCountSlider");
        if (DisplayPins(node) == wanted) yield break;
        var original = node["value"]!.Value<int>();
        var low = node["range"]!["rawMin"]!.Value<int>();
        var high = node["range"]!["rawMax"]!.Value<int>();
        while (low <= high)
        {
            var raw = (low + high) / 2;
            yield return GameUi.Set(node, new JValue(raw));
            node = Slider("PinCountSlider");
            var actual = DisplayPins(node);
            if (actual == wanted) yield break;
            if (actual < wanted) low = raw + 1; else high = raw - 1;
        }
        yield return GameUi.Set(node, new JValue(original));
        throw new AgentError("unrepresentable_value", $"{wanted} pins is not representable by the current native socket slider. That slider was restored; earlier edits remain.");
    }

    private JObject HardwareRead()
    {
        var view = ui.Read(ChooserScope);
        var actions = new JArray(GameUi.Controls(view).Where(c => (string?)c["role"] == "button" && (string?)c["name"] is "New Cpu" or "New Socket" or "New Architecture" or "New Cooler").Select(c => new JObject { ["name"] = c["name"]!.DeepClone(), ["label"] = c["label"]!.DeepClone(), ["enabled"] = c["blockedReason"] == null, ["blockedReason"] = c["blockedReason"]?.DeepClone() }));
        return new JObject { ["method"] = "game", ["actions"] = actions, ["next"] = "Use CPU commands for New CPU, or socket-list/socket-preview for the native Socket workflow. Disabled actions remain dynamic game availability." };
    }

    private JObject SocketList()
    {
        var catalog = ui.Catalog(CatalogScope);
        var cards = SocketCards(catalog);
        var complete = !((bool?)catalog["more"] ?? false);
        return new JObject { ["method"] = "game", ["sockets"] = new JArray(cards.Select(SocketResult)), ["count"] = cards.Length, ["complete"] = complete, ["precision"] = "native-display", ["next"] = complete ? "Use socket-preview with an exact base name, socket-refine for a custom socket, or socket-rename for a custom socket." : "Catalog is truncated in this game build; do not treat this as all socket choices." };
    }

    private JObject Preview()
    {
        var view = ui.Read(FormScope);
        var input = Control(view, "Name Input", "input");
        var priority = Control(view, "Priority Dropdown", "select");
        var investment = Slider("InvestmentSlider");
        var pins = Slider("PinCountSlider");
        var commit = Control(view, "Create", "button");
        var preview = new JObject();
        foreach (var pair in new[] { ("unitCost", "/UnitCost"), ("minimumDieSize", "/MinimumDieSize"), ("safeTemperature", "/SafeTemperature"), ("thermalEfficiency", "/ThermalEfficiency"), ("minCacheCapacity", "/MinCacheCapacity"), ("maxCacheCapacity", "/MaxCacheCapacity"), ("projectCost", "/ProjectCost"), ("projectTime", "/ProjectTime"), ("conclusionDate", "/ConclusionDate") }) preview[pair.Item1] = Text(view, pair.Item2);
        return new JObject
        {
            ["method"] = "game", ["base"] = BaseName(view), ["mode"] = input["blockedReason"] == null ? "create" : "refine", ["name"] = input["value"]!.DeepClone(),
            ["pins"] = DisplayPins(pins), ["pinsDisplay"] = pins["displayValue"]!.DeepClone(), ["pinsEditable"] = pins["blockedReason"] == null,
            ["priority"] = priority["options"]![priority["value"]!.Value<int>()]!.DeepClone(), ["priorityOptions"] = priority["options"]!.DeepClone(),
            ["investmentPercent"] = investment["value"]!.Value<double>() * 100d, ["investmentDisplay"] = investment["displayValue"]!.DeepClone(), ["preview"] = preview,
            ["commit"] = new JObject { ["label"] = commit["label"]!.DeepClone(), ["enabled"] = commit["blockedReason"] == null, ["blockedReason"] = commit["blockedReason"]?.DeepClone() },
            ["committed"] = false, ["precision"] = "native-display", ["next"] = "Preview only. Use explicit socket-create or socket-refine to commit."
        };
    }

    private JObject SocketCard(string name)
    {
        var matches = SocketCards(ui.Catalog(CatalogScope)).Where(c => (string?)c["name"] == name).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No socket/base package named '{name}'. Use game socket-list.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several socket/base cards are named '{name}'. Rename a custom socket before exact-name actions.");
        return matches[0];
    }

    private static JObject[] SocketCards(JObject view)
    {
        var cards = new List<JObject>();
        foreach (var node in GameUi.Controls(view).Where(c => (string?)c["name"] == "Hardware Option_Big" && c["group"] != null))
        {
            var group = (string)node["group"]!;
            var texts = GameUi.Texts(view).Where(t => (string?)t["group"] == group).Select(t => (string?)t["text"]).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            if (texts.Length != 1) continue;
            var display = texts[0]!;
            var name = display.Split('|')[0].Trim();
            var edit = GameUi.Controls(view).SingleOrDefault(c => (string?)c["group"] == group && (string?)c["name"] == "Edit");
            var custom = edit != null;
            var actionable = (JObject)node.DeepClone();
            if (node.Annotation<GenericUi>() is GenericUi owner) actionable.AddAnnotation(owner);
            cards.Add(new JObject { ["name"] = name, ["display"] = display, ["custom"] = custom, ["canRename"] = edit != null && (string?)edit["blockedReason"] != "disabled_by_game", ["group"] = group, ["node"] = actionable });
        }
        if (cards.Count == 0) throw new AgentError("game_ui_mismatch", "Select Base Socket exposed no identifiable rendered cards.");
        return cards.ToArray();
    }

    private static JObject SocketResult(JObject card) => new() { ["name"] = card["name"]!.DeepClone(), ["custom"] = card["custom"]!.DeepClone(), ["display"] = card["display"]!.DeepClone(), ["canRename"] = card["canRename"]!.DeepClone(), ["supportsRefinement"] = card["custom"]!.DeepClone(), ["refinementAvailability"] = (bool)card["custom"]! ? "check socket-preview native commit state" : "not_applicable" };
    private JObject Slider(string suffix) => GameUi.One(GameUi.Controls(ui.Read(FormScope)).Where(c => (string?)c["role"] == "slider" && ((string?)c["context"] ?? "").EndsWith("/" + suffix, StringComparison.Ordinal)), suffix);
    private static JObject Control(JObject view, string name, string role) => GameUi.One(GameUi.Controls(view).Where(c => (string?)c["name"] == name && (string?)c["role"] == role), name);
    private static string BaseName(JObject view)
    {
        var label = (string?)Control(view, "Package", "button")["label"] ?? "";
        return label.StartsWith("Base | ", StringComparison.Ordinal) ? label.Substring(7).Trim() : label;
    }
    private static int DisplayPins(JObject node)
    {
        var match = Regex.Match((string?)node["displayValue"] ?? "", @"^([\d,]+)\s+pins$");
        if (!match.Success) throw new AgentError("game_ui_mismatch", "Native pin-count display is missing or ambiguous.");
        return int.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
    }
    private static string Text(JObject view, string suffix)
    {
        var matches = GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").EndsWith(suffix, StringComparison.Ordinal)).Select(t => (string?)t["text"]).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        if (matches.Length != 1 || matches[0]!.Contains('…')) throw new AgentError("game_ui_mismatch", $"Socket preview field {suffix} is missing, ambiguous or clipped.");
        return matches[0]!;
    }

    private static void ValidateDraft(Request request, bool requireName, bool allowNameAndPins)
    {
        if (!allowNameAndPins && (request.Parameters?["name"] != null || request.Parameters?["pins"] != null)) throw new AgentError("unsupported_parameter", "Socket refinement does not accept name or pins; those native controls are disabled.");
        RequireName(request, requireName);
        if (request.Parameters?["pins"] != null && (request.Parameters["pins"]!.Type != JTokenType.Integer || request.Parameters["pins"]!.Value<int>() <= 0)) throw new AgentError("invalid_value", "pins must be a positive integer representable by the current native slider.");
        if (request.Parameters?["priority"] != null && (request.Parameters["priority"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace(request.Parameters["priority"]!.Value<string>()))) throw new AgentError("invalid_value", "priority must be a non-empty native dropdown option; socket-preview reports the current choices.");
        if (request.Parameters?["investmentPercent"] != null)
        {
            var token = request.Parameters["investmentPercent"]!;
            if (token.Type is not (JTokenType.Integer or JTokenType.Float) || double.IsNaN(token.Value<double>()) || double.IsInfinity(token.Value<double>()) || token.Value<double>() is < 0 or > 100) throw new AgentError("invalid_value", "investmentPercent must be a normalized native level from 0 to 100, not currency.");
        }
    }
    private static void RequireName(Request request, bool required)
    {
        var token = request.Parameters?["name"];
        if (required && token == null) throw new AgentError("invalid_request", $"{request.Command} requires --name.");
        if (token != null && (token.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>()) || token.Value<string>()!.Length > 10)) throw new AgentError("invalid_value", "name must be a non-empty string up to the native 10-character limit.");
    }
    private static void NoTarget(Request request)
    {
        if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", $"{request.Command} does not take a target.");
    }
}
