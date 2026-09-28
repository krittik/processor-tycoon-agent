using System;
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

// Version-sensitive mapping of the English 0.2.16a5 UI. All state and actions remain native and visible.
internal sealed class ResearchGameApi : IGameModule
{
    private const string ResearchScope = "ResearchTreeWindow";
    private const string DesktopScope = "DesktopButtons";
    private const string ReleaseScope = "ProjectReleaseWindow";
    private const string ProjectCancelScope = "ProjectCanceller";
    private const string ModifierScope = "ResearchProjectWindow";
    private const string ModifierWindowType = "ProcessorTycoon.ResearchSystem.ResearchProjectWindow";
    private const string ProjectRefPrefix = "project-ui:";
    private static readonly string ProjectRefSession = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly GameUi ui;
    private HashSet<int> modifierProjectsBefore = new();
    private JObject? modifierStartPreview;
    private JObject? researchBefore;
    private HashSet<string> releaseRowsBefore = new();

    public ResearchGameApi(GameUi ui) => this.ui = ui;
    public string[] Commands => new[] { "game.research-list", "game.research-topology", "game.research-find", "game.research-read", "game.research-start", "game.research-set", "game.research-pan", "game.research-cancel", "game.modifier-research-list", "game.modifier-research-preview", "game.modifier-research-start", "game.projects-list", "game.projects-read", "game.projects-pause", "game.projects-resume", "game.projects-pause-all", "game.projects-resume-all", "game.projects-cancel", "game.projects-release-read", "game.projects-release-preview", "game.projects-release" };

    public void Validate(Request request)
    {
        if (request.Command != "game.research-pan" && request.Value != null) throw new AgentError("unsupported_parameter", $"{request.Command} does not accept --value.");
        if (request.Command == "game.research-topology")
        {
            GameUi.Parameters(request, "detailed");
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "research-topology does not take a target.");
            if (request.Parameters?["detailed"] != null && request.Parameters["detailed"]!.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "detailed must be boolean.");
        }
        else if (request.Command == "game.research-list")
        {
            GameUi.Parameters(request, "status", "query");
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "research-list does not take a target.");
            if (request.Parameters?["status"] != null && (request.Parameters["status"]!.Type != JTokenType.String || request.Parameters["status"]!.Value<string>() is not ("all" or "available" or "locked" or "current" or "researched"))) throw new AgentError("invalid_value", "status must be all, available, locked, current or researched.");
            if (request.Parameters?["query"] != null && (request.Parameters["query"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace(request.Parameters["query"]!.Value<string>()))) throw new AgentError("invalid_value", "query must be a non-empty string.");
        }
        else if (request.Command == "game.research-find")
        {
            GameUi.Parameters(request, "query", "status");
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "research-find does not take a target.");
            if (request.Parameters?["query"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(request.Parameters["query"]!.Value<string>())) throw new AgentError("invalid_request", "research-find requires a non-empty query parameter.");
            if (request.Parameters?["status"] != null && (request.Parameters["status"]!.Type != JTokenType.String || request.Parameters["status"]!.Value<string>() is not ("all" or "available" or "locked" or "current" or "researched"))) throw new AgentError("invalid_value", "status must be all, available, locked, current or researched.");
        }
        else if (request.Command is "game.research-read" or "game.research-cancel" or "game.projects-list" or "game.projects-pause-all" or "game.projects-resume-all" or "game.projects-release-read")
        {
            GameUi.Parameters(request);
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", $"{request.Command} does not take a target.");
        }
        else if (request.Command is "game.research-start" or "game.projects-read" or "game.projects-pause" or "game.projects-resume" or "game.projects-cancel")
        {
            GameUi.Parameters(request);
            GameUi.RequiredTarget(request);
        }
        else if (request.Command == "game.research-set")
        {
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "research-set does not take a target.");
            GameUi.Parameters(request, "fundingPercent", "innovationEffort");
            if (request.Parameters == null || request.Parameters.Count == 0) throw new AgentError("invalid_request", "research-set requires fundingPercent and/or innovationEffort.");
            if (request.Parameters["fundingPercent"] != null && request.Parameters["fundingPercent"]!.Type != JTokenType.Integer) throw new AgentError("invalid_value", "fundingPercent must be an integer supported by the current native slider.");
            if (request.Parameters["innovationEffort"] != null && request.Parameters["innovationEffort"]!.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "innovationEffort must be boolean.");
        }
        else if (request.Command == "game.research-pan")
        {
            GameUi.Parameters(request);
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "research-pan does not take a target.");
            var parts = request.Value?.ToString().Split(',');
            if (parts == null || parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) throw new AgentError("invalid_value", "research-pan requires --value 'dx,dy' in screen pixels.");
        }
        else if (request.Command == "game.modifier-research-list")
        {
            GameUi.Parameters(request);
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", "modifier-research-list does not take a target.");
        }
        else if (request.Command is "game.modifier-research-preview" or "game.modifier-research-start")
        {
            if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", $"{request.Command} does not take a target; use --type and --budget.");
            GameUi.Parameters(request, "type", "budget");
            foreach (var key in new[] { "type", "budget" }) if (request.Parameters?[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(request.Parameters[key]!.Value<string>())) throw new AgentError("invalid_request", $"{request.Command} requires a non-empty --{key} option from modifier-research-list.");
        }
        else if (request.Command is "game.projects-release-preview" or "game.projects-release")
        {
            GameUi.RequiredTarget(request);
            GameUi.Parameters(request, "name", "price", "sellOnMarket", "availableForContracts");
            var p = request.Parameters;
            if (p?["name"] != null && (p["name"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace(p["name"]!.Value<string>()))) throw new AgentError("invalid_value", "name must be a non-empty string.");
            if (p?["name"]?.Value<string>()?.Length > 20) throw new AgentError("invalid_value", "name exceeds the native 20-character limit.");
            if (p?["price"] != null && (p["price"]!.Type != JTokenType.Integer || p["price"]!.Value<int>() is < 1 or > 9999)) throw new AgentError("invalid_value", "price must be an integer from 1 to 9999.");
            foreach (var key in new[] { "sellOnMarket", "availableForContracts" }) if (p?[key] != null && p[key]!.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"{key} must be boolean.");
        }
        if (request.Hidden && request.Command is ("game.research-start" or "game.research-set" or "game.research-pan" or "game.research-cancel" or "game.modifier-research-preview" or "game.modifier-research-start" or "game.projects-pause" or "game.projects-resume" or "game.projects-pause-all" or "game.projects-resume-all" or "game.projects-cancel" or "game.projects-release-preview" or "game.projects-release")) throw new AgentError("hidden_not_supported", "Game mutations use visible native controls.");
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        if (request.Command.StartsWith("game.research-", StringComparison.Ordinal))
        {
            foreach (var step in OpenResearch(request)) yield return step;
            if (request.Command == "game.research-start")
            {
                var wanted = GameUi.RequiredTarget(request);
                var foreground = EnsureResearchForeground();
                var card = ResearchCardByName(wanted);
                if (card.Status != "available" || !card.Button.IsInteractable()) throw new AgentError("not_interactable", $"Research '{wanted}' is currently {card.Status}. Choose an available technology from research-list.");
                yield return GameUi.NativeStep($"Research {wanted}", () =>
                {
                    ValidateResearchForeground(foreground);
                    var current = ResearchCardByName(wanted);
                    if (current.Status != "available" || !current.Button.IsInteractable()) throw new AgentError("context_changed", $"Research '{wanted}' is no longer available. No native click was invoked.");
                    current.Button.onClick.Invoke();
                }, card.Button, true);
            }
            else if (request.Command == "game.research-set")
            {
                var foreground = EnsureResearchForeground();
                researchBefore = ActiveResearch();
                if (CurrentResearchPanel() == null) throw new AgentError("no_active_research", "Start research before changing its funding or innovation effort.");
                if (request.Parameters?["innovationEffort"] is JToken innovation)
                {
                    var wanted = innovation.Value<bool>();
                    var panel = CurrentResearchPanel()!;
                    var innovationTarget = (panel.Innovation as Component)?.GetComponent<Toggle>();
                    if (panel.InnovationOn != wanted) yield return GameUi.NativeStep($"Research innovation effort {wanted}", () =>
                    {
                        ValidateResearchForeground(foreground);
                        var current = CurrentResearchPanel() ?? throw new AgentError("context_changed", "Current research changed before innovation effort could be edited.");
                        if (!current.InnovationInteractable) throw new AgentError("not_interactable", "Native Innovation Effort toggle is disabled.");
                        SetProperty(current.Innovation, "ProcessorTycoon.UI.ToggleUI", "IsOn", wanted);
                    }, innovationTarget);
                }
                if (request.Parameters?["fundingPercent"] is JToken funding)
                {
                    var wanted = funding.Value<int>();
                    var slider = CurrentResearchPanel()!.FundingSlider;
                    var min = slider.minValue;
                    var max = slider.maxValue;
                    if (wanted < min || wanted > max) throw new AgentError("invalid_value", $"fundingPercent {wanted} is outside the current native range {min}-{max}. Earlier edits remain.");
                    if (Math.Abs(slider.value - wanted) > .001f) yield return GameUi.NativeStep($"Research funding {wanted}%", () =>
                    {
                        ValidateResearchForeground(foreground);
                        var current = CurrentResearchPanel() ?? throw new AgentError("context_changed", "Current research changed before funding could be edited.");
                        if (!current.FundingSlider.interactable) throw new AgentError("not_interactable", "Native research funding slider is disabled.");
                        if (wanted < current.FundingSlider.minValue || wanted > current.FundingSlider.maxValue) throw new AgentError("context_changed", $"Native funding range changed to {current.FundingSlider.minValue}-{current.FundingSlider.maxValue}.");
                        current.FundingSlider.value = wanted;
                    }, slider);
                }
            }
            else if (request.Command == "game.research-pan")
            {
                var tree = GameUi.One(GameUi.Controls(ui.Read(ResearchScope)).Where(c => (string?)c["name"] == "Cpu Technology Tree" && c["actions"]!.Values<string>().Contains("drag")), "CPU technology tree drag surface");
                yield return GameUi.Action("ui.drag", tree, request.Value);
            }
            else if (request.Command == "game.research-cancel")
            {
                var stop = GameUi.Controls(ui.Read(ResearchScope)).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Stop Researching").ToArray();
                if (stop.Length == 0) throw new AgentError("no_active_research", "No exposed current research can be cancelled.");
                yield return GameUi.Click(GameUi.One(stop, "Stop Researching button"));
            }
            yield break;
        }

        if (request.Command.StartsWith("game.modifier-research-", StringComparison.Ordinal))
        {
            foreach (var step in OpenModifierResearch(request)) yield return step;
            if (request.Command == "game.modifier-research-list") yield break;
            foreach (var step in ApplyModifierResearchOptions(request)) yield return step;
            if (request.Command == "game.modifier-research-preview") yield break;
            var panel = ModifierPanel.Read(EnsureModifierWindow());
            var commit = GameUi.One(GameUi.Controls(ui.Read(ModifierScope)).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Research"), "modifier Research button");
            if (commit["blockedReason"] != null) throw new AgentError("not_interactable", $"Native modifier research is unavailable: {commit["label"]} ({commit["blockedReason"]}).");
            modifierProjectsBefore = ProjectStates().Keys.ToHashSet();
            modifierStartPreview = panel.Result();
            yield return GameUi.Click(commit);
            yield break;
        }

        if (request.Command is "game.projects-list" or "game.projects-read" or "game.projects-pause" or "game.projects-resume" or "game.projects-pause-all" or "game.projects-resume-all" or "game.projects-cancel")
        {
            foreach (var step in OpenProjects(request)) yield return step;
            if (request.Command == "game.projects-read") ProjectCardByTarget(GameUi.RequiredTarget(request));
            else if (request.Command is "game.projects-pause" or "game.projects-resume")
            {
                var wanted = GameUi.RequiredTarget(request);
                var card = ProjectCardByTarget(wanted);
                var shouldClick = request.Command == "game.projects-pause" ? !card.Paused : card.Paused;
                if (shouldClick) yield return GameUi.NativeStep($"{request.Command.Substring(14)} project {wanted}", () =>
                {
                    var current = ProjectCardByTarget(wanted);
                    if (!current.Pause.interactable) throw new AgentError("not_interactable", $"Project '{wanted}' pause control is disabled.");
                    current.Pause.onClick.Invoke();
                }, card.Pause, true);
            }
            else if (request.Command == "game.projects-cancel")
            {
                var wanted = GameUi.RequiredTarget(request);
                var card = ProjectCardByTarget(wanted);
                if (!card.Cancel.interactable) throw new AgentError("not_interactable", $"Project '{wanted}' cancellation is disabled.");
                yield return GameUi.NativeStep($"Cancel project {wanted}", () =>
                {
                    var current = ProjectCardByTarget(wanted);
                    if (!current.Cancel.interactable) throw new AgentError("not_interactable", $"Project '{wanted}' cancellation is no longer available.");
                    current.Cancel.onClick.Invoke();
                }, card.Cancel, true);
            }
            else if (request.Command == "game.projects-pause-all")
            {
                if (ProjectCards().Any(card => !card.Paused)) yield return GameUi.Click(AllProjectsControl());
            }
            else if (request.Command == "game.projects-resume-all")
            {
                var cards = ProjectCards();
                if (cards.Length > 0 && cards.Any(card => card.Paused) && cards.Any(card => !card.Paused))
                {
                    yield return GameUi.Click(AllProjectsControl());
                    cards = ProjectCards();
                    if (!cards.All(card => card.Paused)) throw new AgentError("value_not_applied", "Native Pause All did not pause every active project; Resume All was not attempted.");
                }
                if (cards.Length > 0 && cards.All(card => card.Paused)) yield return GameUi.Click(AllProjectsControl());
            }
            yield break;
        }

        if (request.Command == "game.projects-release-read")
        {
            ReleaseControls();
            yield break;
        }
        foreach (var step in ApplyReleaseDraft(request)) yield return step;
        if (request.Command == "game.projects-release-preview") yield break;
        releaseRowsBefore = ProductionGameApi.SnapshotProductRowRefs();
        yield return GameUi.Click(ReleaseControls().Release);
        var products = new ProductionGameApi(ui.Native);
        foreach (var step in products.Prepare(new Request { Command = "game.product-list" })) yield return step;
    }

    private IEnumerable<Request> ApplyReleaseDraft(Request request)
    {
        var release = ReleaseControls();
        var currentName = release.Name["value"]!.Value<string>()!;
        if (!string.Equals(currentName, GameUi.RequiredTarget(request), StringComparison.Ordinal)) throw new AgentError("target_mismatch", $"The visible release form is for '{currentName}', not '{request.Target}'. No input dispatched.");
        foreach (var key in new[] { "name", "price", "sellOnMarket", "availableForContracts" })
        {
            release = ReleaseControls();
            var node = key switch { "name" => release.Name, "price" => release.Price, "sellOnMarket" => release.Market, _ => release.Contracts };
            var wanted = request.Parameters?[key];
            if (wanted == null || string.Equals(node["value"]?.ToString(), wanted.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            yield return GameUi.Set(node, wanted);
            release = ReleaseControls();
            var actual = key switch { "name" => release.Name["value"], "price" => release.Price["value"], "sellOnMarket" => release.Market["value"], _ => release.Contracts["value"] };
            if (!string.Equals(actual?.ToString(), wanted.ToString(), StringComparison.OrdinalIgnoreCase)) throw new AgentError("value_not_applied", $"Native validation changed {key} to '{actual}'. Release was not clicked.");
        }
    }

    public JObject Result(Request request)
    {
        if (request.Command == "game.research-topology") return ResearchTopology.Read(request.Parameters?["detailed"]?.Value<bool>() ?? false);
        if (request.Command is "game.research-list" or "game.research-find") return ResearchList(request);
        if (request.Command == "game.research-read") return ResearchRead(true);
        if (request.Command == "game.research-start")
        {
            var active = ActiveResearch();
            var started = string.Equals((string?)active?["name"], request.Target, StringComparison.Ordinal);
            return new JObject
            {
                ["method"] = "game", ["research"] = active, ["started"] = started,
                ["outcome"] = started ? "completed" : "confirmation_required",
                ["next"] = started ? "Research is active; use game research-read to monitor it." : "The native click did not make this technology current. Use game dialog-read and dialog-choose to resolve the native warning; it was not auto-accepted."
            };
        }
        if (request.Command == "game.research-set")
        {
            var result = ResearchRead(false);
            result["before"] = researchBefore;
            result["changeNote"] = "Compare before with active: monthly funding, research speed and time left are native readbacks. Changing funding is a real edit, not a simulation or rollback.";
            return result;
        }
        if (request.Command == "game.research-pan") return ResearchList(request);
        if (request.Command == "game.research-cancel")
        {
            var active = ActiveResearch();
            return new JObject { ["method"] = "game", ["cancelled"] = active == null, ["research"] = active, ["precision"] = "display-rounded" };
        }
        if (request.Command is "game.modifier-research-list" or "game.modifier-research-preview") return ModifierPanel.Read(EnsureModifierWindow()).Result();
        if (request.Command == "game.modifier-research-start")
        {
            var started = ProjectCards().Where(project => !modifierProjectsBefore.Contains(project.InstanceId)).Select(project => project.Result()).ToArray();
            var verified = started.Length == 1;
            return new JObject { ["method"] = "game", ["started"] = verified, ["selection"] = modifierStartPreview?.DeepClone(), ["project"] = verified ? started[0] : null, ["newProjectCount"] = started.Length, ["outcome"] = verified ? "completed" : "outcome_unverified", ["next"] = verified ? "Modifier research started as a normal scheduled project; use projects-read to monitor it." : "The native Research action was invoked, but exactly one new rendered project row was not observed. Inspect Projects before retrying." };
        }
        if (request.Command == "game.projects-list") return ProjectsList();
        if (request.Command == "game.projects-read") return ProjectCardByTarget(GameUi.RequiredTarget(request)).Result();
        if (request.Command is "game.projects-pause" or "game.projects-resume") return ProjectCardByTarget(GameUi.RequiredTarget(request)).Result();
        if (request.Command is "game.projects-pause-all" or "game.projects-resume-all") return ProjectsBulkResult(request.Command);
        if (request.Command == "game.projects-cancel")
        {
            var dialog = ui.Read(ProjectCancelScope);
            var choices = new JArray(GameUi.Controls(dialog).Where(c => (string?)c["role"] == "button").Select(c => new JObject { ["name"] = c["name"]!.DeepClone(), ["label"] = c["label"]!.DeepClone(), ["enabled"] = c["blockedReason"] == null }));
            return new JObject { ["method"] = "game", ["target"] = request.Target, ["outcome"] = "confirmation_required", ["cancelled"] = false, ["dialog"] = new JObject { ["texts"] = new JArray(GameUi.Texts(dialog).Select(t => t["text"]!.DeepClone())), ["choices"] = choices }, ["next"] = "The native project-cancellation dialog is visible. Confirm or decline it explicitly; this command did not auto-accept." };
        }
        if (request.Command is "game.projects-release-read" or "game.projects-release-preview") return ReleaseDraftResult(request.Command == "game.projects-release-preview");
        var stillOpen = TryReleaseControls(out _);
        if (stillOpen) return new JObject { ["method"] = "game", ["target"] = request.Target, ["released"] = false, ["outcome"] = "input_dispatched", ["releaseFormVisible"] = true, ["next"] = "The native release form remains visible; inspect its validation state before retrying." };
        var releasedName = request.Parameters?["name"]?.Value<string>() ?? request.Target;
        var products = new ProductionGameApi(ui.Native).Result(new Request { Command = "game.product-list" });
        var matches = products["products"]?.OfType<JObject>().Where(p => string.Equals((string?)p["name"], releasedName, StringComparison.Ordinal) && p["productRef"]?.Value<string>() is string productRef && !releaseRowsBefore.Contains(productRef)).ToArray() ?? Array.Empty<JObject>();
        if (matches.Length == 1) return new JObject { ["method"] = "game", ["target"] = request.Target, ["releasedName"] = releasedName, ["released"] = true, ["outcome"] = "completed", ["product"] = matches[0].DeepClone(), ["catalogComplete"] = products["catalogComplete"]?.DeepClone(), ["next"] = "Released product verified in the native Production catalog." };
        return new JObject { ["method"] = "game", ["target"] = request.Target, ["releasedName"] = releasedName, ["released"] = false, ["outcome"] = "outcome_unverified", ["newMatchingProducts"] = matches.Length, ["next"] = "Release input was dispatched, but exactly one newly rendered Production row with the expected name was not observed. Inspect before retrying; existing same-name products are not proof of release." };
    }

    private IEnumerable<Request> OpenResearch(Request request)
    {
        if (TryResearchWindow() != null) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Research is not already visible; a read would have to open it.");
        // Research is full-screen: opening it over a native popup would bury the popup.
        EnsureNoBlockingDialog();
        var desktop = ui.Read(DesktopScope);
        var open = GameUi.One(GameUi.Controls(desktop).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Research Desktop"), "Research desktop button");
        yield return GameUi.Click(open);
        EnsureResearchForeground();
    }

    private IEnumerable<Request> OpenModifierResearch(Request request)
    {
        if (TryModifierWindow() != null) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Research Project is not already visible; a read would have to open it.");
        EnsureNoBlockingDialog();
        var bottom = ui.Read("DESKTOP -> Bottom/BottomBar");
        var open = GameUi.One(GameUi.Controls(bottom).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Research Project"), "Research Project bottom-bar button");
        yield return GameUi.Click(open);
        EnsureModifierWindow();
    }

    private IEnumerable<Request> ApplyModifierResearchOptions(Request request)
    {
        foreach (var key in new[] { "type", "budget" })
        {
            var wanted = request.Parameters![key]!.Value<string>()!;
            var name = key == "type" ? "Type Dropdown" : "Budget Dropdown";
            var dropdown = GameUi.One(GameUi.Controls(ui.Read(ModifierScope)).Where(c => (string?)c["role"] == "select" && (string?)c["name"] == name), $"modifier research {name}");
            var options = dropdown["options"]!.Values<string>().ToArray();
            var matches = options.Where(option => option == wanted).ToArray();
            if (matches.Length == 0) throw new AgentError("invalid_value", $"Unknown modifier research {key} '{wanted}'. Use modifier-research-list for exact native options. Earlier edits remain.");
            if (matches.Length != 1) throw new AgentError("ambiguous_option", $"Several native modifier research {key} options are named '{wanted}'. Earlier edits remain.");
            if ((string?)dropdown["options"]![dropdown["value"]!.Value<int>()] == wanted) continue;
            yield return GameUi.Select(dropdown, wanted);
        }
        var result = ModifierPanel.Read(EnsureModifierWindow());
        if (result.TypeValue != request.Parameters!["type"]!.Value<string>() || result.BudgetValue != request.Parameters["budget"]!.Value<string>()) throw new AgentError("value_not_applied", "Native modifier research selectors did not retain the requested choices.");
    }

    private sealed class ModifierPanel
    {
        public TMP_Dropdown TypeDropdown = null!;
        public TMP_Dropdown BudgetDropdown = null!;
        public Button ResearchButton = null!;
        public string ResearchLabel = "";
        public string ModifierTitle = "";
        public string ModifierValue = "";
        public string MonthlyCost = "";
        public string ProjectCost = "";
        public string ProjectTime = "";
        public string FinishDate = "";
        public string TypeValue => TypeDropdown.options[TypeDropdown.value].text;
        public string BudgetValue => BudgetDropdown.options[BudgetDropdown.value].text;

        public JObject Result() => new()
        {
            ["method"] = "game", ["type"] = TypeValue, ["typeOptions"] = new JArray(TypeDropdown.options.Select(o => o.text)),
            ["budget"] = BudgetValue, ["budgetOptions"] = new JArray(BudgetDropdown.options.Select(o => o.text)),
            ["modifier"] = new JObject { ["title"] = ModifierTitle, ["value"] = ModifierValue }, ["monthlyCost"] = MonthlyCost,
            ["projectCost"] = ProjectCost, ["projectTime"] = ProjectTime, ["finishDate"] = FinishDate,
            ["start"] = new JObject { ["enabled"] = ResearchButton.IsInteractable(), ["label"] = ResearchLabel },
            ["complete"] = true, ["precision"] = "native-display", ["committed"] = false,
            ["next"] = ResearchButton.IsInteractable() ? "Use modifier-research-start with explicit --type and --budget to commit this native project." : $"Native modifier research is unavailable: {ResearchLabel}."
        };

        public static ModifierPanel Read(MonoBehaviour window)
        {
            var type = Field<object>(window, ModifierWindowType, "typeDropdown");
            var budget = Field<object>(window, ModifierWindowType, "budgetDropdown");
            if (type is not Component typeComponent || budget is not Component budgetComponent) throw new AgentError("game_ui_mismatch", "Research Project dropdown wrappers are not active UI components.");
            var typeDropdown = typeComponent.GetComponent<TMP_Dropdown>();
            var budgetDropdown = budgetComponent.GetComponent<TMP_Dropdown>();
            if (typeDropdown == null || budgetDropdown == null || typeDropdown.options.Count == 0 || budgetDropdown.options.Count == 0) throw new AgentError("game_ui_mismatch", "Research Project exposed no complete native dropdown options.");
            var buttonWrapper = Field<object>(window, ModifierWindowType, "researchButton");
            return new ModifierPanel
            {
                TypeDropdown = typeDropdown, BudgetDropdown = budgetDropdown, ResearchButton = NativeButton(buttonWrapper), ResearchLabel = Property<string>(buttonWrapper, "ProcessorTycoon.UI.ButtonUI", "Text"),
                ModifierTitle = InfoTitle(window, ModifierWindowType, "modifierText"), ModifierValue = InfoValue(window, ModifierWindowType, "modifierText"),
                MonthlyCost = InfoValue(window, ModifierWindowType, "monthlyCostText"), ProjectCost = InfoValue(window, ModifierWindowType, "projectCostText"),
                ProjectTime = InfoValue(window, ModifierWindowType, "projectTimeText"), FinishDate = InfoValue(window, ModifierWindowType, "finishDateText")
            };
        }
    }

    private static MonoBehaviour? TryModifierWindow()
    {
        var matches = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == ModifierWindowType && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy).ToArray();
        if (matches.Length > 1) throw new AgentError("game_ui_mismatch", $"Expected at most one active ResearchProjectWindow, found {matches.Length}.");
        return matches.SingleOrDefault();
    }

    private static MonoBehaviour EnsureModifierWindow() => TryModifierWindow() ?? throw new AgentError("game_ui_mismatch", "The native Research Project window is not active.");

    private IEnumerable<Request> OpenProjects(Request request)
    {
        EnsureNoBlockingDialog();
        var side = SideWindow();
        var isOpen = PrivateValue<bool>(side, "ProcessorTycoon.Desktop.SideWindow.SideWindow", "isOpen");
        var projects = Field<object>(side, "ProcessorTycoon.Desktop.SideWindow.SideWindow", "projectsButton");
        var selected = !Property<bool>(projects, "ProcessorTycoon.UI.ButtonUI", "Interactable");
        if (isOpen && selected) yield break;
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Projects are not already visible; a read would have to focus the native side window.");
        yield return GameUi.NativeStep("Focus Projects side window", () =>
        {
            EnsureNoBlockingDialog();
            var current = SideWindow();
            if (!PrivateValue<bool>(current, "ProcessorTycoon.Desktop.SideWindow.SideWindow", "isOpen")) NativeButton(Field<object>(current, "ProcessorTycoon.Desktop.SideWindow.SideWindow", "sideButton")).onClick.Invoke();
            var tab = Field<object>(current, "ProcessorTycoon.Desktop.SideWindow.SideWindow", "projectsButton");
            if (Property<bool>(tab, "ProcessorTycoon.UI.ButtonUI", "Interactable")) NativeButton(tab).onClick.Invoke();
        });
    }

    private JObject ResearchList(Request request)
    {
        EnsureResearchForeground();
        var cards = ResearchCards();
        IEnumerable<JObject> results = cards.Select(c => c.Result());
        var status = request.Parameters?["status"]?.Value<string>() ?? (request.Command == "game.research-find" ? "all" : "available");
        var query = request.Parameters?["query"]?.Value<string>();
        if (status != "all") results = results.Where(r => (string?)r["state"] == status);
        if (!string.IsNullOrWhiteSpace(query)) results = results.Where(r => r.Properties().Any(p => p.Value.Type == JTokenType.String && p.Value.Value<string>()!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        var counts = new JObject { ["total"] = cards.Length };
        foreach (var state in new[] { "available", "locked", "current", "researched" }) counts[state] = cards.Count(c => c.Status == state);
        return new JObject { ["method"] = "game", ["catalog"] = "complete player-browsable active research tree", ["status"] = status, ["query"] = query, ["counts"] = counts, ["technologies"] = new JArray(results), ["active"] = ActiveResearch(), ["planning"] = "Card costDisplay is a monthly rate, not total project cost. After game research-start NAME, use game research-set --funding-percent N to adjust spending/speed; --innovation-effort true enables higher funding. Active research exposes current ranges and native readbacks. Compare both monthly spending and duration, not cash alone; game desktop-read includes available credit.", ["complete"] = true, ["precision"] = "display-rounded", ["next"] = status == "all" ? "Use game research-topology for rendered prerequisites; start one exact available name with game research-start NAME." : "Use --status all and game research-topology for future planning, or start one exact available name." };
    }

    private JObject ResearchRead(bool ensureForeground)
    {
        if (ensureForeground) EnsureResearchForeground();
        var active = ActiveResearch();
        return new JObject { ["method"] = "game", ["active"] = active, ["canCancel"] = active != null, ["precision"] = "display-rounded", ["next"] = active == null ? "Use game research-list, then game research-start NAME." : "Use game research-set or research-cancel, or leave game time running to progress." };
    }

    private sealed class ResearchCard
    {
        public Button Button = null!;
        public string Name = "";
        public string Year = "";
        public string Cost = "";
        public string Time = "";
        public string Status = "locked";
        public JObject Result() => new()
        {
            ["name"] = Name, ["year"] = Year, ["costDisplay"] = Cost, ["timeDisplay"] = Time, ["state"] = Status,
            ["available"] = Status == "available"
        };
    }

    private ResearchCard[] ResearchCards()
    {
        var cards = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == "ProcessorTycoon.ResearchSystem.ResearchButton" && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy && HasActiveResearchTree(c.transform)).Select(ReadResearchCard).OrderBy(c => ParseYear(c.Year)).ThenBy(c => c.Name, StringComparer.Ordinal).ToArray();
        if (cards.Length == 0) throw new AgentError("game_ui_mismatch", "The active Research window exposed no ResearchButton UI cards. No hidden data fallback was used.");
        return cards;
    }

    private ResearchCard ResearchCardByName(string name)
    {
        var matches = ResearchCards().Where(c => string.Equals(c.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No player-browsable research technology is named '{name}'. Use game research-list or research-find.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several research cards are named '{name}'. No native action was invoked.");
        return matches[0];
    }

    private static ResearchCard ReadResearchCard(MonoBehaviour component)
    {
        var nameText = Field<TMP_Text>(component, "technologyNameText");
        var currentText = Field<TMP_Text>(component, "currentResearchText");
        var researched = Field<GameObject>(component, "researchedBackground");
        var locked = Field<GameObject>(component, "lockedBackground");
        var button = Field<Button>(component, "button");
        var isCurrent = currentText.gameObject.activeSelf && !string.IsNullOrWhiteSpace(currentText.text);
        var status = isCurrent ? "current" : researched.activeSelf ? "researched" : locked.activeSelf ? "locked" : button.IsInteractable() ? "available" : "locked";
        return new ResearchCard { Button = button, Name = UiText(nameText), Year = InfoValue(component, "yearText"), Cost = InfoValue(component, "costText"), Time = InfoValue(component, "timeText"), Status = status };
    }

    private static T Field<T>(MonoBehaviour component, string name) where T : class => Field<T>(component, "ProcessorTycoon.ResearchSystem.ResearchButton", name);
    private static T Field<T>(MonoBehaviour component, string expectedType, string name) where T : class
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI field '{name}' is unavailable in this game version.");
        return value;
    }

    private static string InfoValue(MonoBehaviour component, string name) => InfoValue(component, "ProcessorTycoon.ResearchSystem.ResearchButton", name);
    private static string InfoValue(MonoBehaviour component, string expectedType, string name)
    {
        var info = Field<object>(component, expectedType, name);
        if (info.GetType().FullName != "ProcessorTycoon.UI.InfoText") throw new AgentError("game_ui_mismatch", $"{expectedType} field '{name}' is not the expected InfoText UI component.");
        var property = info.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(info) is not string value) throw new AgentError("game_ui_mismatch", $"{expectedType} InfoText '{name}' has no rendered value.");
        return value.Trim();
    }

    private static string InfoTitle(MonoBehaviour component, string expectedType, string name)
    {
        var info = Field<object>(component, expectedType, name);
        if (info.GetType().FullName != "ProcessorTycoon.UI.InfoText") throw new AgentError("game_ui_mismatch", $"{expectedType} field '{name}' is not the expected InfoText UI component.");
        var property = info.GetType().GetProperty("Title", BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(info) is not string value) throw new AgentError("game_ui_mismatch", $"{expectedType} InfoText '{name}' has no rendered title.");
        return value.Trim();
    }

    private static T Property<T>(object component, string expectedType, string name)
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI property '{name}' is unavailable in this game version.");
        return value;
    }

    private static void SetProperty<T>(object component, string expectedType, string name, T value)
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite != true) throw new AgentError("game_ui_mismatch", $"{expectedType} UI property '{name}' is not writable in this game version.");
        property.SetValue(component, value);
    }

    private static string UiText(TMP_Text text) => text.text.Trim();
    private static int ParseYear(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : int.MaxValue;
    private static bool HasActiveResearchTree(Transform transform)
    {
        for (var parent = transform; parent != null; parent = parent.parent)
        {
            if (!parent.gameObject.activeInHierarchy) return false;
            if (parent.GetComponents<MonoBehaviour>().Any(c => c != null && c.GetType().FullName == "ProcessorTycoon.ResearchSystem.ResearchTreeWindow")) return true;
        }
        return false;
    }

    private static MonoBehaviour? TryResearchWindow()
    {
        var matches = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == "ProcessorTycoon.ResearchSystem.ResearchTreeWindow" && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy).ToArray();
        if (matches.Length > 1) throw new AgentError("game_ui_mismatch", $"Expected at most one active ResearchTreeWindow, found {matches.Length}.");
        return matches.SingleOrDefault();
    }

    private static MonoBehaviour EnsureResearchForeground()
    {
        EnsureNoBlockingDialog();
        return TryResearchWindow() ?? throw new AgentError("game_ui_mismatch", "The native Research window is not active.");
    }

    private static void ValidateResearchForeground(MonoBehaviour expected)
    {
        EnsureNoBlockingDialog();
        var current = TryResearchWindow();
        if (current == null || current != expected) throw new AgentError("context_changed", "The native Research window changed before the action. No input was invoked.");
    }

    private sealed class CurrentPanel
    {
        public string Name = "";
        public string Speed = "";
        public string Secondary = "";
        public string TimeLeft = "";
        public string FundingDisplay = "";
        public object Innovation = null!;
        public bool InnovationOn;
        public bool InnovationInteractable;
        public Slider FundingSlider = null!;
        public JArray Tooltips = new();
    }

    private CurrentPanel? CurrentResearchPanel()
    {
        var matches = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == "ProcessorTycoon.ResearchSystem.CurrentResearchUI" && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy && HasActiveResearchTree(c.transform)).ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active CurrentResearchUI, found {matches.Length}.");
        var component = matches[0];
        var canvas = Field<CanvasGroup>(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "canvasGroup");
        if (!canvas.blocksRaycasts || canvas.alpha <= 0f) return null;
        var innovation = Field<object>(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "innovationEffortToggle");
        var funding = Field<object>(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "fundingSlider");
        return new CurrentPanel
        {
            Name = UiText(Field<TMP_Text>(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "currentResearchText")),
            Speed = InfoValue(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "researchSpeedText"),
            Secondary = InfoValue(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "secondaryText"),
            TimeLeft = InfoValue(component, "ProcessorTycoon.ResearchSystem.CurrentResearchUI", "timeLeftText"),
            FundingDisplay = Clean(Property<string>(funding, "ProcessorTycoon.UI.SliderUI", "Text")),
            FundingSlider = Property<Slider>(funding, "ProcessorTycoon.UI.SliderUI", "Slider"),
            Innovation = innovation,
            InnovationOn = Property<bool>(innovation, "ProcessorTycoon.UI.ToggleUI", "IsOn"),
            InnovationInteractable = Property<bool>(innovation, "ProcessorTycoon.UI.ToggleUI", "Interactable"),
            Tooltips = ResearchTooltips(component)
        };
    }

    private JObject? ActiveResearch()
    {
        var panel = CurrentResearchPanel();
        if (panel == null) return null;
        return new JObject
        {
            ["name"] = panel.Name, ["researchSpeed"] = panel.Speed, ["secondary"] = panel.Secondary, ["timeLeft"] = panel.TimeLeft,
            ["fundingPercent"] = panel.FundingSlider.value, ["fundingDisplay"] = panel.FundingDisplay, ["innovationEffort"] = panel.InnovationOn,
            ["nativeGuidance"] = panel.Tooltips,
            ["controls"] = new JObject
            {
                ["funding"] = new JObject { ["command"] = "game research-set --funding-percent N", ["min"] = panel.FundingSlider.minValue, ["max"] = panel.FundingSlider.maxValue, ["wholeNumbers"] = panel.FundingSlider.wholeNumbers, ["available"] = panel.FundingSlider.interactable, ["effect"] = "Monthly funding and research speed are different quantities. Compare fundingDisplay, researchSpeed and timeLeft after changes; a lower monthly budget is not necessarily the same reduction in total cost." },
                ["innovationEffort"] = new JObject { ["command"] = "game research-set --innovation-effort true --funding-percent N", ["available"] = panel.InnovationInteractable, ["effect"] = "Enables the native higher funding range (up to 500% in this build; normal maximum 100%). Enabling it alone does not select a higher funding value. Inspect returned range and actual speed/cost before advancing time." }
            }
        };
    }

    private static string Clean(string value) => Regex.Replace(value, "<[^>]+>", "").Trim();

    private static JArray ResearchTooltips(MonoBehaviour panel)
    {
        var tips = new JArray();
        foreach (var trigger in panel.GetComponentsInChildren<MonoBehaviour>().Where(c => c != null && c.isActiveAndEnabled && c.GetType().FullName == "ProcessorTycoon.TooltipSystem.TooltipTrigger"))
        {
            if (!Property<bool>(trigger, "ProcessorTycoon.TooltipSystem.TooltipTrigger", "IsActive")) continue;
            var data = Field<object>(trigger, "ProcessorTycoon.TooltipSystem.TooltipTrigger", "tooltipData");
            var content = Clean(Property<string>(data, "ProcessorTycoon.TooltipSystem.TooltipData", "Content"));
            tips.Add(new JObject { ["title"] = Clean(Property<string>(data, "ProcessorTycoon.TooltipSystem.TooltipData", "Header")), ["text"] = content, ["complete"] = !Regex.IsMatch(content, @"#[A-Za-z][A-Za-z0-9]*\b") });
        }
        return tips;
    }

    private JObject ProjectsList()
    {
        var projects = ProjectCards();
        // With no running project the native All Projects aggregate is not shown; an empty list is still a complete answer.
        JToken aggregate;
        try { aggregate = ProjectAggregate(); }
        catch (AgentError) when (projects.Length == 0) { aggregate = JValue.CreateNull(); }
        return new JObject { ["method"] = "game", ["projects"] = new JArray(projects.Select(p => p.Result())), ["count"] = projects.Length, ["aggregate"] = aggregate, ["complete"] = true, ["precision"] = "display-rounded", ["next"] = "Inspect, pause, resume or cancel one exact name; pause/resume all uses the native aggregate control. Completed CPU projects open their native release form automatically." };
    }

    private JObject ProjectsBulkResult(string command)
    {
        var projects = ProjectCards();
        var wantedPaused = command == "game.projects-pause-all";
        var completed = projects.Length == 0 || projects.All(project => project.Paused == wantedPaused);
        return new JObject
        {
            ["method"] = "game", ["operation"] = wantedPaused ? "pause-all" : "resume-all", ["completed"] = completed,
            ["outcome"] = completed ? "completed" : "outcome_unverified", ["projectCount"] = projects.Length,
            ["pausedCount"] = projects.Count(project => project.Paused), ["aggregate"] = ProjectAggregate(),
            ["projects"] = new JArray(projects.Select(project => project.Result())),
            ["next"] = completed ? "All active projects now have the requested pause state." : "The rendered project rows did not all reach the requested pause state; inspect before retrying."
        };
    }

    private JObject ProjectAggregate()
    {
        var view = ui.Read("AllProjectsButton");
        var values = GameUi.Texts(view).Select(t => (string?)t["text"]).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        if (values.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one native All Projects aggregate text, found {values.Length}.");
        var control = AllProjectsControl(view);
        return new JObject { ["remainingCostDisplay"] = values[0], ["bulkControlEnabled"] = control["blockedReason"] == null, ["blockedReason"] = control["blockedReason"]?.DeepClone() };
    }

    private JObject AllProjectsControl() => AllProjectsControl(ui.Read("AllProjectsButton"));
    private static JObject AllProjectsControl(JObject view) => GameUi.One(GameUi.Controls(view).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Pause" && ((string?)c["context"] ?? "").EndsWith("/AllProjectsButton", StringComparison.Ordinal)), "All Projects pause/resume button");

    private sealed class ProjectCard
    {
        public int InstanceId;
        public string Ref = "";
        public string Name = "";
        public string Secondary = "";
        public string TimeLeft = "";
        public string MonthlyCost = "";
        public string Status = "";
        public Button Pause = null!;
        public Button Cancel = null!;
        public bool Paused => string.Equals(Status, "Paused", StringComparison.Ordinal);
        public JObject Result()
        {
            var result = new JObject { ["projectRef"] = Ref, ["name"] = Name, ["secondary"] = Secondary, ["timeLeft"] = TimeLeft, ["monthlyCost"] = MonthlyCost, ["status"] = Status, ["paused"] = Paused, ["canPauseOrResume"] = Pause.interactable, ["canCancel"] = Cancel.interactable };
            var time = Regex.Match(TimeLeft, @"(\d+)\s*days?", RegexOptions.IgnoreCase);
            if (time.Success) result["timeLeftDays"] = int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);
            return result;
        }
    }

    internal static JObject[] ProjectCatalog() => ProjectCards().Select(p => p.Result()).ToArray();
    internal static (int Id, JObject Card) ProjectWaitTarget(string target)
    {
        var card = ProjectCardByTarget(target);
        if (card.Paused) throw new AgentError("project_paused", $"Project '{card.Name}' is paused. Use game projects-resume with its name or projectRef before waiting; funding is not resumed automatically.");
        return (card.InstanceId, card.Result());
    }

    internal static string? ReadyReleaseName()
    {
        const string type = "ProcessorTycoon.ProjectSystem.ProjectReleaseWindow";
        var windows = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == type && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy).ToArray();
        if (windows.Length == 0) return null;
        if (windows.Length != 1) throw new AgentError("game_ui_mismatch", "Multiple native CPU release forms are active.");
        return InfoValue(windows[0], type, "cpuNameText");
    }

    internal sealed class ProjectState
    {
        public string Name { get; }
        public string Status { get; }
        public int? TimeLeftDays { get; }
        public ProjectState(string name, string status, int? timeLeftDays) { Name = name; Status = status; TimeLeftDays = timeLeftDays; }
    }
    internal static IReadOnlyDictionary<int, ProjectState> ProjectStates()
    {
        const string type = "ProcessorTycoon.ProjectSystem.UI.ProjectUI";
        return ProjectComponents().ToDictionary(c => c.GetInstanceID(), c => new ProjectState(UiText(Field<TMP_Text>(c, type, "projectNameText")), UiText(Field<TMP_Text>(c, type, "statusText")), ParseDays(UiText(Field<TMP_Text>(c, type, "timeLeftText")))));
    }

    private static ProjectCard[] ProjectCards()
    {
        return ProjectComponents().Select(ReadProjectCard).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
    }

    private static MonoBehaviour[] ProjectComponents() => Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == "ProcessorTycoon.ProjectSystem.UI.ProjectUI" && c.gameObject.scene.IsValid() && HasActiveSideWindow(c.transform)).ToArray();

    private static ProjectCard ProjectCardByTarget(string target)
    {
        var cards = ProjectCards();
        if (target.StartsWith(ProjectRefPrefix, StringComparison.Ordinal))
        {
            if (!Regex.IsMatch(target, @"^project-ui:[0-9a-f]{8}:-?\d+:-?\d+$", RegexOptions.CultureInvariant)) throw new AgentError("invalid_target", $"Malformed projectRef '{target}'. Use an exact current projectRef from projects-list.");
            var referenced = cards.Where(p => p.Ref == target).ToArray();
            if (referenced.Length == 0) throw new AgentError("stale_reference", $"Project reference '{target}' is not present in the current scene. Re-run projects-list; no name fallback was attempted.");
            if (referenced.Length != 1) throw new AgentError("game_ui_mismatch", $"Project reference '{target}' matched several rendered rows.");
            return referenced[0];
        }
        var matches = cards.Where(p => string.Equals(p.Name, target, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No active project named '{target}'. Use game projects-list.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several active projects are named '{target}'. Use one exact projectRef returned by projects-list.");
        return matches[0];
    }

    private static ProjectCard ReadProjectCard(MonoBehaviour component)
    {
        const string type = "ProcessorTycoon.ProjectSystem.UI.ProjectUI";
        return new ProjectCard
        {
            InstanceId = component.GetInstanceID(),
            Ref = $"{ProjectRefPrefix}{ProjectRefSession}:{component.gameObject.scene.handle}:{component.GetInstanceID()}",
            Name = UiText(Field<TMP_Text>(component, type, "projectNameText")), Secondary = UiText(Field<TMP_Text>(component, type, "secondaryText")),
            TimeLeft = UiText(Field<TMP_Text>(component, type, "timeLeftText")), MonthlyCost = UiText(Field<TMP_Text>(component, type, "remainingCostText")), Status = UiText(Field<TMP_Text>(component, type, "statusText")),
            Pause = NativeButton(Field<object>(component, type, "pauseButton")), Cancel = NativeButton(Field<object>(component, type, "cancelButton"))
        };
    }

    private static int? ParseDays(string value)
    {
        var match = Regex.Match(value, @"(\d+)\s*days?", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static bool HasActiveSideWindow(Transform transform)
    {
        for (var parent = transform.parent; parent != null; parent = parent.parent) if (parent.gameObject.activeInHierarchy && parent.GetComponents<MonoBehaviour>().Any(c => c != null && c.GetType().FullName == "ProcessorTycoon.Desktop.SideWindow.SideWindow")) return true;
        return false;
    }

    private static MonoBehaviour SideWindow()
    {
        var matches = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == "ProcessorTycoon.Desktop.SideWindow.SideWindow" && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy).ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active native SideWindow, found {matches.Length}.");
        return matches[0];
    }

    private static Button NativeButton(object wrapper)
    {
        if (wrapper.GetType().FullName != "ProcessorTycoon.UI.ButtonUI" || wrapper is not Component component) throw new AgentError("game_ui_mismatch", "Expected native ButtonUI wrapper.");
        return component.GetComponent<Button>() ?? throw new AgentError("game_ui_mismatch", "Native ButtonUI has no Unity Button.");
    }

    private static T PrivateValue<T>(MonoBehaviour component, string expectedType, string name)
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI field '{name}' is unavailable in this game version.");
        return value;
    }

    private static void EnsureNoBlockingDialog()
    {
        var blockingTypes = new HashSet<string>
        {
            "ProcessorTycoon.ProjectSystem.UI.ProjectCanceller", "ProcessorTycoon.ProjectSystem.ProjectReleaseWindow",
            "ProcessorTycoon.ResearchSystem.ResearchCompletedWindow", "ProcessorTycoon.ResearchSystem.UnnecessaryTechnologyWarning",
            "ProcessorTycoon.Hardware.Creation.CreatorCpuConfirmationWindow"
        };
        var blocker = Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(c => c != null && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy && blockingTypes.Contains(c.GetType().FullName ?? ""));
        if (blocker != null) throw new AgentError("not_interactable", $"Native popup '{blocker.gameObject.name}' is open. Resolve it first with game dialog-read and game dialog-choose.");
        var pauseMenu = Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().FullName == "ProcessorTycoon.TimeSystem.PauseMenu" && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy);
        if (pauseMenu != null && Property<bool>(pauseMenu, "ProcessorTycoon.TimeSystem.PauseMenu", "IsOpen")) throw new AgentError("not_interactable", "The native Pause Menu is open. Close it first.");
    }

    private sealed class ReleaseSet
    {
        public JObject Name = null!; public JObject Price = null!; public JObject Market = null!; public JObject Contracts = null!; public JObject Release = null!;
    }

    private ReleaseSet ReleaseControls()
    {
        if (!TryReleaseControls(out var set)) throw new AgentError("not_found", "No complete native CPU release form is visible. Wait for a CPU project to complete; the game opens it automatically.");
        return set!;
    }

    private JObject ReleaseDraftResult(bool edited)
    {
        const string type = "ProcessorTycoon.ProjectSystem.ProjectReleaseWindow";
        var window = ReleaseWindow();
        var controls = ReleaseControls();
        var budgets = new JArray();
        foreach (var field in new[] { "highEndText", "midRangeText", "lowEndText" })
        {
            var value = VisibleInfo(window, type, field);
            if (value != null) budgets.Add(value);
        }
        return new JObject
        {
            ["method"] = "game", ["cpu"] = new JObject
            {
                ["company"] = InfoTitle(window, type, "cpuNameText"), ["name"] = InfoValue(window, type, "cpuNameText"),
                ["powerConsumption"] = InfoValue(window, type, "powerConsumption"), ["frequency"] = InfoValue(window, type, "frequencyText"),
                ["ips"] = InfoValue(window, type, "ipsText"), ["ipsUnit"] = InfoTitle(window, type, "ipsText"),
                ["unitCost"] = InfoValue(window, type, "unitCostText"), ["unitCostCurrency"] = "$"
            },
            ["draft"] = new JObject
            {
                ["name"] = controls.Name["value"]!.DeepClone(), ["price"] = controls.Price["value"]!.DeepClone(),
                ["sellOnMarket"] = controls.Market["value"]!.DeepClone(), ["availableForContracts"] = controls.Contracts["value"]!.DeepClone()
            },
            ["marketBudgets"] = budgets, ["release"] = new JObject { ["enabled"] = controls.Release["blockedReason"] == null, ["label"] = controls.Release["label"]!.DeepClone(), ["blockedReason"] = controls.Release["blockedReason"]?.DeepClone() },
            ["edited"] = edited, ["committed"] = false, ["precision"] = "native-display",
            ["draftSideEffects"] = "Native name and market/contract toggles update the pending CPU immediately; price updates the rendered unit-cost preview and is committed by Release.",
            ["next"] = "Use projects-release with this exact draft name to commit, or projects-release-preview to revise the still-open native form."
        };
    }

    private static JObject? VisibleInfo(MonoBehaviour window, string expectedType, string field)
    {
        var info = Field<object>(window, expectedType, field);
        if (info is not Component component || !component.gameObject.activeInHierarchy) return null;
        return new JObject { ["title"] = InfoTitle(window, expectedType, field), ["value"] = InfoValue(window, expectedType, field) };
    }

    private static MonoBehaviour ReleaseWindow()
    {
        const string type = "ProcessorTycoon.ProjectSystem.ProjectReleaseWindow";
        var matches = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().FullName == type && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy).ToArray();
        if (matches.Length != 1) throw new AgentError(matches.Length == 0 ? "not_found" : "game_ui_mismatch", $"Expected one active native CPU release form, found {matches.Length}.");
        return matches[0];
    }

    private bool TryReleaseControls(out ReleaseSet? set)
    {
        var view = ui.Read(ReleaseScope);
        var controls = GameUi.Controls(view);
        var release = controls.Where(c => (string?)c["role"] == "button" && (string?)c["label"] == "Release").ToArray();
        var names = controls.Where(c => (string?)c["role"] == "input" && (int?)c["range"]?["characterLimit"] == 20).ToArray();
        var prices = controls.Where(c => (string?)c["role"] == "input" && (string?)c["range"]?["contentType"] == "IntegerNumber" && (int?)c["range"]?["characterLimit"] == 4).ToArray();
        var markets = controls.Where(c => (string?)c["role"] == "toggle" && (((string?)c["name"] ?? "") + " " + ((string?)c["label"] ?? "")).IndexOf("market", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        var contracts = controls.Where(c => (string?)c["role"] == "toggle" && (((string?)c["name"] ?? "") + " " + ((string?)c["label"] ?? "")).IndexOf("contract", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        if (release.Length != 1 || names.Length != 1 || prices.Length != 1 || markets.Length != 1 || contracts.Length != 1) { set = null; return false; }
        set = new ReleaseSet { Name = names[0], Price = prices[0], Market = markets[0], Contracts = contracts[0], Release = release[0] };
        return true;
    }
}
