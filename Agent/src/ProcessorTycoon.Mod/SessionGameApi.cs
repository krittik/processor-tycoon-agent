using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// Version-sensitive navigation/session mapping for the English 0.2.16a5 UI.
internal sealed class SessionGameApi : IGameModule
{
    private const string PauseScope = "PauseMenu";
    private const string SettingsScope = "SettingsWindow";
    private const string SaveScope = "SaveWindow";
    private const string LoadScope = "LoadWindow";
    private const string CpuConfirmationScope = "CpuConfirmationWindow";
    private readonly GameUi ui;
    private JObject? verified;
    private string? newCompanyColor;
    private string? chosenDialogScope;
    private bool? cpuHideUntilReloadRequested;
    private bool? cpuHideUntilReloadReadback;
    private bool openedPauseForSettings;
    private int readinessWaitFrames;
    private const int ReadinessFrameBudget = 600;
    private const int ReadinessChunkFrames = 15;

    private sealed class Setting
    {
        public string Key; public string Section; public string Role; public string Name; public string? Suffix;
        public Setting(string key, string section, string role, string name, string? suffix = null) { Key = key; Section = section; Role = role; Name = name; Suffix = suffix; }
    }

    private static readonly Setting[] settings =
    {
        new("dateFormat", "general", "select", "Date Format Dropdown"), new("autosave", "general", "select", "Autosave Dropdown"),
        new("theme", "general", "select", "Theme Dropdown"), new("customCursor", "general", "toggle", "Custom Cursor"),
        new("showExecutionTimes", "general", "toggle", "Execution Times"), new("contractSignedNotifications", "general", "toggle", "Contract Signed"),
        new("contractTerminatedNotifications", "general", "toggle", "Contract Terminated"), new("companyReleaseNotifications", "general", "toggle", "Company Releases"),
        new("fullScreenMode", "video", "select", "Fullscreen Mode Dropdown"), new("resolution", "video", "select", "Resolution Dropdown"),
        new("vsync", "video", "toggle", "Vsync"), new("msaa", "video", "toggle", "MSAA"),
        new("masterVolumePercent", "audio", "slider", "Slider", "/MasterSlider"), new("sfxVolumePercent", "audio", "slider", "Slider", "/SFXSlider"),
        new("musicVolumePercent", "audio", "slider", "Slider", "/MusicSlider")
    };

    private static readonly Dictionary<string, (string Scope, string Control)> windows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Research"] = ("DesktopButtons", "Research Desktop"), ["Production"] = ("DesktopButtons", "Production Desktop"),
        ["Hardware"] = ("DesktopButtons", "Create Hardware Desktop"), ["Contracts"] = ("DesktopButtons", "Contracts Desktop"),
        ["Business"] = ("DesktopButtons", "Business Desktop"), ["Marketing"] = ("DesktopButtons", "Marketing Desktop"),
        ["Employees"] = ("DesktopButtons", "Manage Employees Desktop"), ["Bank"] = ("DesktopButtons", "Bank Desktop"),
        ["Inspector"] = ("DesktopButtons", "Inspector Desktop"), ["Email"] = ("DesktopButtons", "Email Desktop"),
        ["Browser"] = ("DesktopButtons", "Browser Desktop"), ["Analysis"] = ("DesktopButtons", "Analysis Desktop"),
        ["Create CPU"] = ("DESKTOP -> Bottom/BottomBar", "Create Cpu"), ["Research Project"] = ("DESKTOP -> Bottom/BottomBar", "Research Project"),
        ["Desktop Customization"] = ("DESKTOP -> Bottom/BottomBar", "Desktop Customization"),
        ["Settings"] = (PauseScope, "Settings"), ["Save"] = (PauseScope, "Save"), ["Load"] = (PauseScope, "Load"),
        ["Gameplay Settings"] = (PauseScope, "Gameplay Settings"), ["Notification Settings"] = (PauseScope, "Notification Settings"),
        ["Statistics"] = (PauseScope, "Statistics"), ["Company"] = (PauseScope, "Company"), ["Credits"] = ("Menu", "Credits")
    };

    // Result scopes proven by existing domain modules, runtime snapshots or the native class mapping.
    private static readonly Dictionary<string, string> windowScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Research"] = "ResearchTreeWindow", ["Production"] = "ProductionWindow", ["Hardware"] = "CreateHardwareWindow",
        ["Contracts"] = "ContractWindow", ["Business"] = "BusinessWindow", ["Inspector"] = "InspectorWindow",
        ["Email"] = "EmailWindow", ["Analysis"] = "AnalysisWindow", ["Create CPU"] = "CreateCpuWindow",
        ["Settings"] = SettingsScope, ["Save"] = SaveScope, ["Load"] = LoadScope,
        ["Gameplay Settings"] = "GameplaySettingsWindow", ["Notification Settings"] = "NotificationSettingsWindow", ["Statistics"] = "StatisticsWindow",
        ["Company"] = "CompanyEditWindow", ["Desktop Customization"] = "DesktopCustomizationWindow", ["Research Project"] = "ResearchProjectWindow",
        ["Credits"] = "CreditsWindow"
    };

    public string[] Commands { get; } =
    {
        "game.window-list", "game.window-open", "game.window-close", "game.dialog-read", "game.dialog-choose",
        "game.time-read", "game.time-set", "game.settings-read", "game.settings-set", "game.save-list", "game.save-create", "game.save-load",
        "game.save-delete", "game.session-exit", "game.session-new-read", "game.session-new-preview", "game.session-new-start"
    };

    public SessionGameApi(GameUi ui) => this.ui = ui;

    public void Validate(Request request)
    {
        verified = null;
        newCompanyColor = null;
        chosenDialogScope = null;
        cpuHideUntilReloadRequested = null;
        cpuHideUntilReloadReadback = null;
        openedPauseForSettings = false;
        readinessWaitFrames = 0;
        switch (request.Command)
        {
            case "game.window-list": case "game.dialog-read": case "game.time-read": case "game.save-list": case "game.session-new-read":
                NoInput(request); break;
            case "game.window-open": case "game.window-close":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); RejectValue(request); break;
            case "game.dialog-choose":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "dialog", "hideUntilReload"); RequireString(request.Parameters?["dialog"], "dialog"); RejectValue(request);
                if (request.Parameters?["hideUntilReload"] is JToken hideUntilReload && hideUntilReload.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "hideUntilReload must be a boolean.");
                break;
            case "game.time-set":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); RejectValue(request);
                if (request.Target is not ("pause" or "speed-1" or "speed-2" or "speed-3")) throw new AgentError("invalid_request", "time-set target must be pause, speed-1, speed-2 or speed-3.");
                MultiplayerInterop.RequireTimeControl("time-set");
                break;
            case "game.settings-read":
                GameUi.Parameters(request); RejectValue(request);
                if (request.Target.Length > 0 && request.Target is not ("general" or "video" or "audio")) throw new AgentError("invalid_request", "settings-read target must be general, video or audio.");
                break;
            case "game.settings-set": ValidateSettings(request); break;
            case "game.save-create":
                GameUi.Parameters(request); RejectValue(request); ValidateSaveName(GameUi.RequiredTarget(request)); break;
            case "game.save-load":
                GameUi.Parameters(request); RejectValue(request); GameUi.RequiredTarget(request); break;
            case "game.save-delete":
                GameUi.Parameters(request); RejectValue(request); GameUi.RequiredTarget(request); break;
            case "game.session-exit":
                GameUi.Parameters(request); RejectValue(request); GameUi.RequiredTarget(request);
                if (request.Target is not ("menu" or "desktop")) throw new AgentError("invalid_request", "session-exit target must be menu or desktop.");
                break;
            case "game.session-new-preview": case "game.session-new-start": ValidateNewSession(request); break;
            default: throw new AgentError("unknown_command", request.Command);
        }
        if (request.Hidden && request.Command is not ("game.window-list" or "game.dialog-read" or "game.time-read" or "game.session-new-read")) throw new AgentError("hidden_not_supported", "This command may navigate or mutate native UI; hidden execution is not supported.");
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        switch (request.Command)
        {
            case "game.window-open": foreach (var step in OpenWindow(request.Target)) yield return step; break;
            case "game.window-close":
                if (request.Target is "Pause Menu" or "PauseMenu")
                {
                    yield return GameUi.Click(GameUi.Find(ui.Read(PauseScope), "Continue", "button"));
                    var pauseClosed = !PauseMenuOpen(ui.Read(PauseScope));
                    verified = WindowActionResult(request.Target, false, pauseClosed, PauseScope, pauseClosed ? "continue_control_absent" : "action_dispatched");
                    break;
                }
                var surface = FindSurface(request.Target);
                if (surface == null)
                {
                    // Fully covered: no part of the window is exposed, so it is absent from the visible surfaces.
                    var hiddenScope = CoveredScope(request.Target) ?? throw new AgentError("not_found", $"No visible or covered window named '{request.Target}'. Use game window-list (visibleWindows and coveredWindows).");
                    if (!readOnlyViews.Contains(hiddenScope)) throw new AgentError("not_interactable", $"Window '{request.Target}' (scope {hiddenScope}) is open but fully covered by other windows. It may hold a draft or form, so it is not closed through its hidden button; close or resolve the covering windows first (game window-list).");
                    if (DialogOrMenuVisible()) throw new AgentError("not_interactable", $"Window '{request.Target}' (scope {hiddenScope}) is fully covered and a native dialog or the Pause Menu is open. Resolve that first; nothing was closed.");
                    var hiddenClose = CoveredCloseButton(hiddenScope) ?? throw new AgentError("game_ui_mismatch", $"The covered window {hiddenScope} exposes no single native Close button; nothing was closed.");
                    yield return GameUi.NativeStep($"Close {request.Target} (covered)", () => hiddenClose.onClick.Invoke(), hiddenClose, true);
                    var hiddenClosed = !GameWindowLifecycle.OpenScopes().Contains(hiddenScope);
                    verified = WindowActionResult(request.Target, false, hiddenClosed, hiddenScope, hiddenClosed ? "window_inactive" : "action_dispatched");
                    verified["wasCovered"] = true;
                    break;
                }
                var closedScope = (string)surface["scope"]!;
                var closeControls = GameUi.Controls(ui.Read(closedScope)).Where(c => (string?)c["role"] == "button" && (string?)c["label"] == "Close").ToArray();
                // A read-only workspace view whose Close button another window covers (partly: blocked; fully: not exposed at all),
                // for example a CPU draft, is closed through its own native Close button; nothing but that view is affected.
                // Never while a native dialog or the Pause Menu is open.
                var coveredClose = closeControls.Length == 0 || closeControls.Length == 1 && (string?)closeControls[0]["blockedReason"] == "covered_or_not_raycastable";
                if (coveredClose && readOnlyViews.Contains(closedScope) && !DialogOrMenuVisible() && CoveredCloseButton(closedScope) is UnityEngine.UI.Button nativeClose)
                    yield return GameUi.NativeStep($"Close {request.Target} (covered)", () => nativeClose.onClick.Invoke(), nativeClose, true);
                else yield return GameUi.Click(Exact(closeControls, $"Close in {request.Target}"));
                var closed = !Surfaces(ui.Read(closedScope)).Any(s => (string?)s["scope"] == closedScope);
                verified = WindowActionResult(request.Target, false, closed, closedScope, closed ? "surface_absent" : "action_dispatched");
                break;
            case "game.dialog-choose":
                var dialog = Dialog(request.Parameters!["dialog"]!.Value<string>()!);
                var dialogScope = (string)dialog["scope"]!;
                chosenDialogScope = dialogScope;
                var hideRequested = request.Parameters["hideUntilReload"];
                if (hideRequested != null && dialogScope != CpuConfirmationScope) throw new AgentError("unsupported_parameter", "hideUntilReload is supported only for the exact active CPU development confirmation.");
                if (dialog["covered"]?.Value<bool>() == true)
                {
                    var raise = TimeAdvanceController.WindowRaiser(dialogScope) ?? throw new AgentError("not_interactable", $"'{dialog["title"]}' is covered by other windows and cannot be brought to the front. Close the covering windows (game window-list) and retry; nothing was chosen.");
                    yield return GameUi.NativeStep($"Bring {dialog["title"]} to the front", () => UnityEngine.EventSystems.ExecuteEvents.Execute(raise, new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left }, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler));
                    if (!Surfaces(ui.Read("")).Any(s => (string?)s["scope"] == dialogScope)) throw new AgentError("not_interactable", $"'{dialog["title"]}' stayed covered after bringing it to the front. Close the covering windows (game window-list) and retry; nothing was chosen.");
                }
                var dialogView = dialogScope is "SaveConfirmationWindow" or "QuitConfirmationWindow" or CpuConfirmationScope ? ui.Catalog(dialogScope) : ui.Read(dialogScope);
                var choice = DialogChoice(dialogView, request.Target);
                if (hideRequested != null)
                {
                    cpuHideUntilReloadRequested = hideRequested.Value<bool>();
                    var toggle = CpuConfirmationToggle(dialogView);
                    if (toggle["value"]!.Value<bool>() != cpuHideUntilReloadRequested.Value)
                    {
                        yield return GameUi.Set(toggle, hideRequested);
                        dialogView = CpuConfirmationView() ?? throw new AgentError("game_ui_mismatch", "The exact CPU development confirmation disappeared before its choice was dispatched.");
                        choice = DialogChoice(dialogView, request.Target);
                    }
                    cpuHideUntilReloadReadback = CpuConfirmationToggle(dialogView)["value"]!.Value<bool>();
                    if (cpuHideUntilReloadReadback != cpuHideUntilReloadRequested) throw new AgentError("value_not_applied", "The native 'Hide this until next reload' toggle did not retain the requested value; no confirmation choice was dispatched.");
                }
                yield return GameUi.Click(choice);
                break;
            case "game.time-set":
                if (request.Target == "pause") { yield return GameUi.NativeStep("Pause and clear notification resume speed", TimeAdvanceController.SafePause); break; }
                var controlName = request.Target == "pause" ? "Pause" : "Speed" + request.Target.Substring(6);
                yield return GameUi.Click(Exact(GameUi.Controls(ui.Read("SpeedButtons")).Where(c => (string?)c["name"] == controlName), controlName));
                break;
            case "game.settings-read": foreach (var step in OpenSettings(request.Target.Length == 0 ? "general" : request.Target)) yield return step; break;
            case "game.settings-set": foreach (var step in SetSettings(request)) yield return step; break;
            case "game.save-list": foreach (var step in OpenLoad()) yield return step; break;
            case "game.save-create": foreach (var step in Save(request.Target)) yield return step; break;
            case "game.save-load": CpuReviewGameApi.Clear(); BalanceSnapshot.ResetTrend(); CompanyAdvisories.Reset(); foreach (var step in Load(request.Target)) yield return step; break;
            case "game.save-delete": foreach (var step in DeleteSave(request.Target)) yield return step; break;
            case "game.session-exit": foreach (var step in ExitSession(request.Target)) yield return step; break;
            case "game.session-new-preview": foreach (var step in EditNewSession(request, false)) yield return step; break;
            case "game.session-new-start": CpuReviewGameApi.Clear(); BalanceSnapshot.ResetTrend(); CompanyAdvisories.Reset(); foreach (var step in EditNewSession(request, true)) yield return step; break;
        }
    }

    public JObject Result(Request request)
    {
        if (verified != null) return verified;
        return request.Command switch
        {
            "game.window-list" => WindowState(),
            "game.window-open" or "game.window-close" => throw new AgentError("game_ui_mismatch", "The native window action ended without a postcondition result."),
            "game.dialog-read" => DialogState(),
            "game.dialog-choose" => DialogChoiceResult(request),
            "game.time-read" or "game.time-set" => TimeState(request),
            "game.settings-read" => SettingsState(request.Target.Length == 0 ? "general" : request.Target),
            "game.save-list" => SaveList(),
            "game.save-create" => SaveResult(request.Target),
            "game.save-load" => LoadResult(request.Target),
            "game.save-delete" => throw new AgentError("game_ui_mismatch", "The delete workflow ended without a verification result."),
            "game.session-exit" => ExitResult(request.Target),
            "game.session-new-read" or "game.session-new-preview" => NewSessionState(false),
            "game.session-new-start" => NewSessionState(true),
            _ => throw new AgentError("unknown_command", request.Command)
        };
    }

    private static void NoInput(Request request)
    {
        if (request.Target.Length > 0 || request.Parameters?.Count > 0 || request.Value != null) throw new AgentError("invalid_request", $"{request.Command} takes no target, value or parameters.");
    }

    private static void RejectValue(Request request)
    {
        if (request.Value != null) throw new AgentError("invalid_request", $"{request.Command} does not use value.");
    }

    private static string RequireString(JToken? token, string name)
    {
        if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>())) throw new AgentError("invalid_value", $"{name} must be a non-empty string.");
        return token.Value<string>()!;
    }

    private static JObject Exact(IEnumerable<JObject> candidates, string description)
    {
        var matches = candidates.ToArray();
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one exposed {description}, found {matches.Length}. Use Generic UI to inspect the current foreground state; no guessed fallback was used.");
        return matches[0];
    }

    private static bool Is(JObject c, string role, string name) => (string?)c["role"] == role && (string?)c["name"] == name;

    private IEnumerable<Request> OpenWindow(string name)
    {
        var expectedScope = windowScopes.TryGetValue(name, out var mappedScope) ? mappedScope : name;
        var existing = Surfaces(ui.Read(expectedScope)).FirstOrDefault(s => (string?)s["title"] == name || (string?)s["scope"] == expectedScope);
        if (existing != null)
        {
            verified = WindowActionResult(name, true, true, (string?)existing["scope"], "already_visible");
            yield break;
        }
        if (!windows.TryGetValue(name, out var route)) throw new AgentError("invalid_target", $"Unknown window '{name}'. Use game window-list for exact available names.");
        var shortcut = ui.CpuDraftShortcut(name);
        if (shortcut != null) yield return GameUi.Click(shortcut);
        else if (route.Scope == PauseScope)
        {
            var menuName = name == "Load" ? "Load Game" : name;
            var menuControl = GameUi.Controls(ui.Read("Menu")).SingleOrDefault(c => Is(c, "button", menuName));
            if (menuControl != null) yield return GameUi.Click(menuControl);
            else
            {
                foreach (var step in OpenPause()) yield return step;
                yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(PauseScope)).Where(c => (string?)c["name"] == route.Control), name));
            }
        }
        else yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(route.Scope)).Where(c => (string?)c["name"] == route.Control), name));
        var after = Surfaces(ui.Read(expectedScope));
        var exact = after.FirstOrDefault(s => (string?)s["title"] == name || (string?)s["scope"] == expectedScope);
        verified = WindowActionResult(name, true, exact != null, exact == null ? null : (string?)exact["scope"], exact != null ? "surface_visible" : "action_dispatched");
    }

    private static JObject WindowActionResult(string window, bool opening, bool postcondition, string? scope, string nativePostcondition)
    {
        var result = new JObject { ["method"] = "game", ["window"] = window, [opening ? "opened" : "closed"] = postcondition, ["completed"] = postcondition, ["outcome"] = postcondition ? "completed" : "input_dispatched", ["nativePostcondition"] = nativePostcondition };
        if (!string.IsNullOrEmpty(scope)) result["scope"] = scope;
        if (!postcondition) result["next"] = "The native input was dispatched but the requested surface postcondition was not yet observable. Re-read window-list before retrying.";
        return result;
    }

    private IEnumerable<Request> OpenPause()
    {
        if (PauseMenuOpen(ui.Read(PauseScope))) yield break;
        yield return GameUi.Click(Exact(GameUi.Controls(ui.Read("DESKTOP -> Bottom/BottomBar")).Where(c => (string?)c["name"] == "Door"), "Pause Menu button"));
    }

    private IEnumerable<Request> OpenPauseItem(string item, string resultScope)
    {
        if (GameUi.Controls(ui.Read(resultScope)).Length > 0) yield break;
        foreach (var step in OpenPause()) yield return step;
        yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(PauseScope)).Where(c => Is(c, "button", item)), item));
    }

    private IEnumerable<Request> OpenLoad()
    {
        if (GameUi.Controls(ui.Read(LoadScope)).Length > 0) yield break;
        var menuLoad = GameUi.Controls(ui.Read("Menu")).SingleOrDefault(c => Is(c, "button", "Load Game"));
        if (menuLoad != null) { yield return GameUi.Click(menuLoad); yield break; }
        foreach (var step in OpenPauseItem("Load", LoadScope)) yield return step;
    }

    private JObject? FindSurface(string target)
    {
        var mapped = windowScopes.TryGetValue(target, out var mappedScope);
        var expectedScope = mapped ? mappedScope! : target;
        bool Match(JObject surface) => (string?)surface["scope"] == expectedScope || (!mapped && (string?)surface["title"] == target);
        var matches = Surfaces(ui.Read(expectedScope)).Where(Match).ToArray();
        if (matches.Length == 0) matches = Surfaces(ui.Read("")).Where(Match).ToArray();
        if (matches.Length > 1) throw new AgentError("ambiguous_target", $"Several visible windows are titled '{target}'. Use the exact scope from game window-list.");
        return matches.FirstOrDefault();
    }

    // Scope of an open but fully covered workspace window named by its window name (e.g. Production) or exact scope.
    private static string? CoveredScope(string target)
    {
        var scope = windowScopes.TryGetValue(target, out var mapped) ? mapped : target;
        return GameWindowLifecycle.OpenScopes().Contains(scope) ? scope : null;
    }

    private static JObject[] Surfaces(JObject view) => view["surfaces"]!.OfType<JObject>().ToArray();

    // Time-blocking popups that are open but covered completely by windows raised after them: a workspace opened over a
    // popup (Research is full-screen), or a form the agent kept editing while a Multiplayer clock ran and a research
    // completed. Read through their own controls; dialog-choose brings one to the front first like a player's click.
    private (string Scope, string Title, JObject View)[] CoveredDialogs(HashSet<string> pauseScopes, JObject[] surfaces) => pauseScopes
        .Where(scope => scope != PauseScope && !FormScopes.Contains(scope) && !surfaces.Any(s => (string?)s["scope"] == scope))
        .OrderBy(scope => scope, StringComparer.Ordinal)
        .Select(scope =>
        {
            var view = ui.Catalog(scope);
            var title = (string?)Surfaces(view).FirstOrDefault(s => (string?)s["scope"] == scope)?["title"] ?? TimeAdvanceController.Human(scope);
            return (scope, title, view);
        }).ToArray();

    // Known workspaces, editors, pickers and drafts. Some hold a native PauseTrigger (the CPU designer keeps game time paused
    // while open), but they are forms, not popups: never reported as dialogs, and dialog-choose never presses their buttons
    // (for example the CPU designer's Develop, which is guarded by cpu-review/cpu-develop).
    internal static readonly HashSet<string> FormScopes = new(StringComparer.Ordinal)
    {
        "CreateCpuWindow", "CpuEditWindow", "PackageSelectionWindow", "LithographySelectionWindow", "ArchitectureSelectionWindow", "MemorySelectionWindow", "BackgroundSelection",
        "MarketSalesSpreadsheetWindow", "FactoryManagementWindow", "CreateHardwareWindow", "CreateSocketWindow", "BasePackageSelectionWindow", "SocketEditWindow",
        "ContractWindow", "ContractManagerWindow", "BusinessWindow", "ContractInspectorWindow", "ContractNegotiationWindow", "ResearchTreeWindow", "ResearchProjectWindow",
        "AnalysisWindow", "InspectorWindow", "ProductionWindow", "EmailWindow", SettingsScope, SaveScope, LoadScope, "GameplaySettingsWindow", "NotificationSettingsWindow",
        "StatisticsWindow", "CompanyEditWindow", "DesktopCustomizationWindow", "CreditsWindow"
    };

    // window-list name of a scope (e.g. CreateCpuWindow -> Create CPU), else a readable form of the scope.
    internal static string WindowName(string scope) => windowScopes.FirstOrDefault(pair => pair.Value == scope).Key ?? TimeAdvanceController.Human(scope);

    // Workspace views that hold no draft, form or decision of their own (Manage Lines only a pending, uncommitted selection).
    private static readonly HashSet<string> readOnlyViews =new(StringComparer.Ordinal) { "AnalysisWindow", "ResearchTreeWindow", "ProductionWindow", "FactoryManagementWindow", "InspectorWindow", "ContractWindow", "ContractManagerWindow", "BusinessWindow", "EmailWindow", "StatisticsWindow" };

    private bool DialogOrMenuVisible()
    {
        var pauseScopes = TimeAdvanceController.PauseTriggerScopes();
        return PauseMenuOpen(ui.Read(PauseScope)) || Surfaces(ui.Read("")).Any(s => IsDialogSurface((string?)s["scope"] ?? "", pauseScopes)) || SaveConfirmationView() != null || QuitConfirmationView() != null || CpuConfirmationView() != null;
    }

    // The window's own title-bar Close button (the same control GameWindowLifecycle uses for agent cleanup).
    private static UnityEngine.UI.Button? CoveredCloseButton(string scope)
    {
        var buttons = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>().Where(button => button != null && button.gameObject.activeInHierarchy && button.gameObject.scene.IsValid() && button.interactable && button.transform.parent?.name == "TopBar" && button.name.Contains("Close") && HasAncestor(button.transform, scope)).ToArray();
        return buttons.Length == 1 ? buttons[0] : null;
    }

    private static bool HasAncestor(UnityEngine.Transform transform, string name)
    {
        for (var current = transform.parent; current != null; current = current.parent) if (current.name == name) return true;
        return false;
    }

    private static bool PauseMenuOpen(JObject view) => GameUi.Controls(view).Any(c => Is(c, "button", "Continue"));

    private JObject WindowState()
    {
        var view = ui.Read("");
        var openers = GameWindowLifecycle.Current?.Openers() ?? new Dictionary<string, string>();
        var pauseScopes = TimeAdvanceController.PauseTriggerScopes();
        JObject Visible(JObject s)
        {
            var scope = (string)s["scope"]!;
            var item = new JObject { ["name"] = windowScopes.FirstOrDefault(pair => pair.Value == scope).Key ?? (string?)s["title"], ["title"] = s["title"]!.DeepClone(), ["scope"] = scope };
            // agentOpened: opened by an agent command and not adopted by player input since; autoClose: closed on the next domain change.
            if (openers.TryGetValue(scope, out var opener)) { item["agentOpened"] = true; if (opener == "owned") item["autoClose"] = true; }
            if (IsDialogSurface(scope, pauseScopes)) item["dialog"] = true;
            // A form (not a popup) that keeps game time paused while open, such as the CPU designer.
            else if (scope != PauseScope && pauseScopes.Contains(scope)) item["blocksTime"] = true;
            return item;
        }
        var visible = new JArray(Surfaces(view).Select(Visible));
        // Open workspace windows that other windows cover completely (no exposed part, so absent from visibleWindows).
        var shown = new HashSet<string>(visible.OfType<JObject>().Select(s => (string?)s["scope"] ?? ""), StringComparer.Ordinal);
        var covered = new JArray();
        foreach (var scope in GameWindowLifecycle.OpenScopes().Where(scope => !shown.Contains(scope)).OrderBy(scope => scope, StringComparer.Ordinal))
        {
            var item = new JObject { ["name"] = windowScopes.FirstOrDefault(pair => pair.Value == scope).Key ?? TimeAdvanceController.Human(scope), ["scope"] = scope, ["covered"] = true };
            if (openers.TryGetValue(scope, out var opener)) { item["agentOpened"] = true; if (opener == "owned") item["autoClose"] = true; }
            covered.Add(item);
        }
        foreach (var popup in CoveredDialogs(pauseScopes, Surfaces(view)))
            covered.Add(new JObject { ["name"] = popup.Title, ["scope"] = popup.Scope, ["covered"] = true, ["dialog"] = true });
        var available = new JArray();
        var cache = new Dictionary<string, JObject>();
        JObject Read(string scope) { if (!cache.TryGetValue(scope, out var result)) cache[scope] = result = ui.Read(scope); return result; }
        if (PauseMenuOpen(Read(PauseScope)) && !visible.OfType<JObject>().Any(s => (string?)s["scope"] == PauseScope)) visible.Add(new JObject { ["name"] = "Pause Menu", ["scope"] = PauseScope, ["logical"] = true });
        var pauseReachable = GameUi.Controls(Read("DESKTOP -> Bottom/BottomBar")).Any(c => (string?)c["name"] == "Door" && c["blockedReason"] == null);
        foreach (var pair in windows)
        {
            var controls = GameUi.Controls(Read(pair.Value.Scope));
            var node = controls.SingleOrDefault(c => (string?)c["name"] == pair.Value.Control);
            var item = new JObject { ["name"] = pair.Key };
            if (node != null) { item["available"] = node["blockedReason"] == null; if (node["blockedReason"] != null) item["blockedReason"] = node["blockedReason"]!.DeepClone(); }
            else if (pair.Value.Scope == PauseScope)
            {
                var menuName = pair.Key == "Load" ? "Load Game" : pair.Key;
                var menu = GameUi.Controls(Read("Menu")).SingleOrDefault(c => Is(c, "button", menuName));
                item["available"] = menu?.Property("blockedReason") == null && (menu != null || pauseReachable);
            }
            else item["available"] = false;
            available.Add(item);
        }
        return new JObject { ["method"] = "game", ["visibleWindows"] = visible, ["coveredWindows"] = covered, ["openableWindows"] = available, ["next"] = "Use game window-open NAME or game window-close exact visible name/scope. coveredWindows are open workspace windows hidden completely behind others; window-close closes a covered read-only view through its own native Close button, and game window-tidy closes stale ones." };
    }

    internal static bool DialogScope(string scope) => scope is "BreakContractWindow" or "ProjectCanceller" or "UnnecessaryResearchWarning" or "EndGameWindow" || scope.Contains("ConfirmationWindow", StringComparison.Ordinal) || scope.Contains("ConfirmWindow", StringComparison.Ordinal) || scope.Contains("WarningWindow", StringComparison.Ordinal) || scope.Contains("CompletedWindow", StringComparison.Ordinal) || scope.Contains("ReleaseWindow", StringComparison.Ordinal) || scope.Contains("BankruptcyWindow", StringComparison.Ordinal);

    private static bool DialogNameMatches(JObject surface, string name)
    {
        var title = (string?)surface["title"] ?? "";
        if ((string?)surface["scope"] == name || title == name) return true;
        const string suffix = " Window";
        return name.EndsWith(suffix, StringComparison.Ordinal) && title == name.Substring(0, name.Length - suffix.Length);
    }

    private static bool KnownDialogName(string name, string title, string scope) => name == title || name == scope || name == title + " Window";

    // Recognized dialog scopes plus any window holding an active native PauseTrigger (the game's own time-blocking popups).
    internal static bool IsDialogSurface(string scope, HashSet<string> pauseScopes) => DialogScope(scope) || scope != PauseScope && pauseScopes.Contains(scope) && !FormScopes.Contains(scope);

    private JObject Dialog(string name)
    {
        var pauseScopes = TimeAdvanceController.PauseTriggerScopes();
        var matches = Surfaces(ui.Read("")).Where(s => IsDialogSurface((string?)s["scope"] ?? "", pauseScopes) && DialogNameMatches(s, name)).ToArray();
        if (!matches.Any(s => (string?)s["scope"] == CpuConfirmationScope) && KnownDialogName(name, "Confirmation", CpuConfirmationScope) && CpuConfirmationView() != null) matches = matches.Append(new JObject { ["scope"] = CpuConfirmationScope, ["title"] = "Confirmation" }).ToArray();
        if (matches.Length == 0 && KnownDialogName(name, "Save Delete Confirmation", "SaveConfirmationWindow"))
        {
            if (SaveConfirmationView() != null) return new JObject { ["scope"] = "SaveConfirmationWindow", ["title"] = "Save Delete Confirmation" };
        }
        if (matches.Length == 0 && KnownDialogName(name, "Quit Confirmation", "QuitConfirmationWindow"))
        {
            if (QuitConfirmationView() != null) return new JObject { ["scope"] = "QuitConfirmationWindow", ["title"] = "Quit Confirmation" };
        }
        if (matches.Length == 0)
        {
            var covered = CoveredDialogs(pauseScopes, Surfaces(ui.Read(""))).Where(c => KnownDialogName(name, c.Title, c.Scope) || name == TimeAdvanceController.Human(c.Scope)).ToArray();
            if (covered.Length == 1) return new JObject { ["scope"] = covered[0].Scope, ["title"] = covered[0].Title, ["covered"] = true };
        }
        if (matches.Length != 1) throw new AgentError(matches.Length == 0 ? "not_found" : "ambiguous_target", $"Expected one visible native dialog '{name}', found {matches.Length}. Use game dialog-read.");
        return matches[0];
    }

    private JObject DialogState()
    {
        var dialogs = new JArray();
        var pauseScopes = TimeAdvanceController.PauseTriggerScopes();
        var surfaces = Surfaces(ui.Read(""));
        foreach (var surface in surfaces.Where(s => IsDialogSurface((string?)s["scope"] ?? "", pauseScopes)))
        {
            var view = ui.Read((string)surface["scope"]!);
            var dialog = DialogDescription((string)surface["title"]!, (string)surface["scope"]!, view);
            if (!DialogScope((string)surface["scope"]!)) dialog["source"] = "native_pause_trigger";
            dialogs.Add(dialog);
        }
        // A time-blocking popup covered by windows raised after it is reported with its own texts and choices.
        foreach (var covered in CoveredDialogs(pauseScopes, surfaces))
        {
            var dialog = DialogDescription(covered.Title, covered.Scope, covered.View);
            dialog["source"] = "native_pause_trigger";
            dialog["covered"] = true;
            var choices = dialog["choices"]!.OfType<JObject>().ToArray();
            foreach (var choice in choices.Where(c => (string?)c["blockedReason"] == "covered_or_not_raycastable")) { choice["enabled"] = true; choice.Remove("blockedReason"); }
            dialog["note"] = choices.Length > 0 ? "Other windows cover this popup. game dialog-choose brings it to the front first, as clicking it would." : "Native time-blocking popup without readable controls; inspect it with observe --scope " + covered.Scope + " or a screenshot.";
            dialogs.Add(dialog);
        }
        var saveConfirmation = SaveConfirmationView();
        if (saveConfirmation != null && !dialogs.OfType<JObject>().Any(d => (string?)d["scope"] == "SaveConfirmationWindow")) dialogs.Add(DialogDescription("Save Delete Confirmation", "SaveConfirmationWindow", saveConfirmation));
        var quitConfirmation = QuitConfirmationView();
        if (quitConfirmation != null && !dialogs.OfType<JObject>().Any(d => (string?)d["scope"] == "QuitConfirmationWindow")) dialogs.Add(DialogDescription("Quit Confirmation", "QuitConfirmationWindow", quitConfirmation));
        var cpuConfirmation = CpuConfirmationView();
        if (cpuConfirmation != null && !dialogs.OfType<JObject>().Any(d => (string?)d["scope"] == CpuConfirmationScope)) dialogs.Add(CpuConfirmationDescription(cpuConfirmation));
        var result = new JObject { ["method"] = "game", ["dialogs"] = dialogs, ["next"] = dialogs.Count == 0 ? "No recognized native dialog is visible." : "Choose only an exact returned choice with game dialog-choose CHOICE and dialog parameter." };
        // Open forms that keep game time paused (for example the CPU designer) are not dialogs; they are reported separately.
        var forms = pauseScopes.Where(FormScopes.Contains).OrderBy(scope => scope, StringComparer.Ordinal).Select(scope => new JObject { ["name"] = WindowName(scope), ["scope"] = scope, ["note"] = TimeBlockingFormNote(scope) }).ToArray();
        if (forms.Length > 0) result["timeBlockingWindows"] = new JArray(forms);
        return result;
    }

    internal static string TimeBlockingFormNote(string scope) => $"'{WindowName(scope)}' (scope {scope}) is an open form, not a popup: the game keeps time paused while it is open. It is never closed automatically and dialog-choose does not operate it; finish it with its own commands or close it with game window-close \"{WindowName(scope)}\", then advance time.";

    private JObject? SaveConfirmationView()
    {
        var view = ui.Catalog("SaveConfirmationWindow");
        return GameUi.Controls(view).Any(c => ((string?)c["context"] ?? "").Contains("SaveConfirmationWindow", StringComparison.Ordinal)) ? view : null;
    }

    private JObject? QuitConfirmationView()
    {
        var view = ui.Catalog("QuitConfirmationWindow");
        return GameUi.Controls(view).Any(c => ((string?)c["context"] ?? "").Contains("QuitConfirmationWindow", StringComparison.Ordinal)) ? view : null;
    }

    private JObject? CpuConfirmationView()
    {
        var view = ui.Catalog(CpuConfirmationScope);
        var controls = GameUi.Controls(view).Where(c => ((string?)c["context"] ?? "").Contains("/CpuConfirmationWindow/", StringComparison.Ordinal)).ToArray();
        var labels = controls.Where(c => (string?)c["role"] == "button").Select(c => (string?)c["label"]).ToArray();
        return labels.Contains("Go Back", StringComparer.Ordinal) && labels.Contains("Confirm", StringComparer.Ordinal) && labels.Contains("Close", StringComparer.Ordinal) ? view : null;
    }

    private static JObject CpuConfirmationToggle(JObject view) => Exact(GameUi.Controls(view).Where(c => (string?)c["role"] == "toggle" && (string?)c["label"] == "Hide this until next reload" && ((string?)c["context"] ?? "").Contains("/CpuConfirmationWindow/", StringComparison.Ordinal)), "CPU confirmation toggle 'Hide this until next reload'");

    private static JObject CpuConfirmationDescription(JObject view)
    {
        var result = DialogDescription("Confirmation", CpuConfirmationScope, view);
        var toggle = CpuConfirmationToggle(view);
        result["hideUntilReload"] = toggle["value"]!.DeepClone();
        result["hideUntilReloadEnabled"] = toggle["blockedReason"] == null;
        result["hideUntilReloadParameter"] = "Optional boolean on dialog-choose; omitted leaves the native toggle unchanged.";
        return result;
    }

    private static JObject DialogDescription(string name, string scope, JObject view) => new()
    {
        ["name"] = name, ["scope"] = scope,
        ["nativeEffect"] = scope == "UnnecessaryResearchWarning" ? "Confirming this warning also suppresses further unnecessary-technology warnings for the current native session." : null,
        ["text"] = new JArray(GameUi.Texts(view).Select(t => t["text"]!.DeepClone())),
        ["choices"] = new JArray(GameUi.Controls(view).Where(c => (string?)c["role"] == "button").Select(Choice))
    };

    private static JObject DialogChoice(JObject view, string label)
    {
        var choices = GameUi.Controls(view).Where(c => (string?)c["role"] == "button").ToArray();
        var matches = choices.Where(c => (string?)c["label"] == label).ToArray();
        if (matches.Length == 0) throw new AgentError("invalid_choice", $"No native dialog choice '{label}'. Available labels: {string.Join(", ", choices.Select(c => (string?)c["label"]))}. Use game dialog-read for the current choices; no input was dispatched.");
        return Exact(matches, $"dialog choice label '{label}'");
    }

    private static JObject Choice(JObject c)
    {
        var item = new JObject { ["name"] = c["label"]!.DeepClone(), ["enabled"] = c["blockedReason"] == null };
        if (c["blockedReason"] != null) item["blockedReason"] = c["blockedReason"]!.DeepClone();
        return item;
    }

    private JObject DialogChoiceResult(Request request)
    {
        var dialogName = request.Parameters!["dialog"]!.Value<string>()!;
        var stillVisible = chosenDialogScope == "SaveConfirmationWindow" ? SaveConfirmationView() != null : chosenDialogScope == "QuitConfirmationWindow" ? QuitConfirmationView() != null : chosenDialogScope == CpuConfirmationScope ? CpuConfirmationView() != null : Surfaces(ui.Read("")).Any(s => DialogScope((string?)s["scope"] ?? "") && DialogNameMatches(s, dialogName));
        if (chosenDialogScope == "ResearchCompletedWindow" && request.Target == "Select New Research")
        {
            stillVisible = Surfaces(ui.Read(chosenDialogScope)).Any(s => (string?)s["scope"] == chosenDialogScope);
            var researchWindowOpen = Surfaces(ui.Read("ResearchTreeWindow")).Any(s => (string?)s["scope"] == "ResearchTreeWindow");
            var clock = TimeAdvanceController.ReadClock();
            var paused = (bool?)clock["paused"] == true;
            var completed = !stillVisible && researchWindowOpen && paused;
            return new JObject
            {
                ["method"] = "game", ["outcome"] = completed ? "completed" : "input_dispatched", ["choice"] = request.Target,
                ["dialogScope"] = chosenDialogScope, ["dialogStillVisible"] = stillVisible, ["researchWindowOpen"] = researchWindowOpen,
                ["selectedSpeed"] = clock["selectedSpeed"]!.DeepClone(), ["selectedControl"] = clock["selectedControl"]!.DeepClone(), ["paused"] = paused,
                ["completed"] = completed, ["nativePostcondition"] = completed ? "research_completion_dialog_absent_and_research_window_open_and_clock_paused" : null,
                ["next"] = completed ? "The known native completion choice opened Research and left the visible native clock paused. Choose the next research explicitly." : "The choice input was dispatched, but the full known UI postcondition was not observed. Re-read dialogs, Research, and time before continuing."
            };
        }
        var result = new JObject { ["method"] = "game", ["outcome"] = "input_dispatched", ["choice"] = request.Target, ["dialogScope"] = chosenDialogScope, ["dialogStillVisible"] = stillVisible, ["completed"] = false, ["next"] = "Re-read the affected workflow. A dismissed dialog confirms the choice input, not the business outcome; quit-to-desktop may disconnect before a result." };
        if (chosenDialogScope == CpuConfirmationScope && cpuHideUntilReloadRequested != null)
        {
            result["hideUntilReloadRequested"] = cpuHideUntilReloadRequested;
            result["hideUntilReload"] = cpuHideUntilReloadReadback;
            result["nativeSetting"] = "Hide this until next reload";
            result["nativeEffect"] = cpuHideUntilReloadReadback == true ? "The native CPU confirmation suppression toggle was on when this choice was dispatched; its label limits the setting to the current reload." : "The native CPU confirmation suppression toggle was off when this choice was dispatched.";
        }
        return result;
    }

    private JObject TimeState(Request request)
    {
        var view = ui.Read("DateAndTimeControl");
        var dates = GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").EndsWith("/DateAndTimeControl", StringComparison.Ordinal)).Select(t => t["text"]!.Value<string>()!).Distinct().ToArray();
        if (dates.Length == 0) return new JObject { ["method"] = "game", ["available"] = false, ["dateDisplay"] = JValue.CreateNull(), ["selectedSpeed"] = "unknown", ["speedReadbackAvailable"] = false, ["next"] = "Game time controls are not visible in the current scene." };
        if (dates.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one visible date, found {dates.Length}.");
        var result = TimeAdvanceController.ReadClock();
        result["method"] = "game"; result["dateDisplay"] = dates[0]; result["speedReadbackAvailable"] = true;
        if (request.Command == "game.time-set") { result["outcome"] = "input_dispatched"; result["requested"] = request.Target; }
        result["next"] = "Use game time-advance --days N or --target-date yyyy-MM-dd to advance in-process and leave time paused on the target or a wake condition.";
        return result;
    }

    private static void ValidateSettings(Request request)
    {
        if (request.Target.Length > 0 || request.Value != null || request.Parameters == null || request.Parameters.Count == 0) throw new AgentError("invalid_request", "settings-set requires one or more named parameters and no target/value.");
        foreach (var property in request.Parameters.Properties())
        {
            var field = settings.SingleOrDefault(s => s.Key == property.Name);
            if (field == null) throw new AgentError("unsupported_parameter", $"Unknown setting '{property.Name}'. No input dispatched.");
            var valid = field.Role == "toggle" ? property.Value.Type == JTokenType.Boolean : field.Role == "select" ? property.Value.Type == JTokenType.String : property.Value.Type is JTokenType.Integer or JTokenType.Float && property.Value.Value<double>() >= 0 && property.Value.Value<double>() <= 100;
            if (!valid) throw new AgentError("invalid_value", $"Invalid value for {field.Key}; expected {(field.Role == "toggle" ? "boolean" : field.Role == "select" ? "exact option string" : "number 0..100")}.");
        }
    }

    private IEnumerable<Request> OpenSettings(string section)
    {
        if (GameUi.Controls(ui.Read(SettingsScope)).Length == 0)
        {
            var menuSettings = GameUi.Controls(ui.Read("Menu")).SingleOrDefault(c => Is(c, "button", "Settings"));
            if (menuSettings != null) yield return GameUi.Click(menuSettings);
            else
            {
                openedPauseForSettings = !PauseMenuOpen(ui.Read(PauseScope));
                foreach (var step in OpenPauseItem("Settings", SettingsScope)) yield return step;
            }
        }
        var view = ui.Read(SettingsScope);
        var tabName = char.ToUpperInvariant(section[0]) + section.Substring(1) + " Settings";
        var tab = Exact(GameUi.Controls(view).Where(c => Is(c, "button", tabName)), section + " settings tab");
        if (tab["blockedReason"] == null) yield return GameUi.Click(tab);
    }

    private JObject SettingControl(Setting field)
    {
        var matches = GameUi.Controls(ui.Read(SettingsScope)).Where(c => (string?)c["role"] == field.Role && (string?)c["name"] == field.Name && (field.Suffix == null || ((string?)c["context"] ?? "").EndsWith(field.Suffix, StringComparison.Ordinal)));
        return Exact(matches, field.Key);
    }

    private IEnumerable<Request> SetSettings(Request request)
    {
        var applied = new JObject();
        foreach (var section in new[] { "general", "video", "audio" })
        {
            var fields = settings.Where(s => s.Section == section && request.Parameters?[s.Key] != null).ToArray();
            if (fields.Length == 0) continue;
            foreach (var step in OpenSettings(section)) yield return step;
            foreach (var field in fields)
            {
                var wanted = request.Parameters![field.Key]!;
                var node = SettingControl(field);
                var actual = field.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
                if (!Same(actual, wanted, field.Role)) yield return field.Role == "select" ? GameUi.Select(node, wanted.Value<string>()!) : GameUi.Set(node, wanted);
                node = SettingControl(field);
                actual = field.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
                if (!Same(actual, wanted, field.Role)) throw new AgentError("value_not_applied", $"Native UI reports {field.Key}='{actual}', not requested '{wanted}'. Earlier setting changes remain.");
                applied[field.Key] = actual!.DeepClone();
            }
        }
        verified = new JObject { ["method"] = "game", ["outcome"] = "completed", ["settings"] = applied, ["nativeReadback"] = true, ["windowLeftOpen"] = true, ["pauseMenuOpenedByCommand"] = openedPauseForSettings };
    }

    private static bool Same(JToken? actual, JToken wanted, string role) => role == "slider" ? actual != null && Math.Abs(actual.Value<double>() - wanted.Value<double>()) < 1e-6 : JToken.DeepEquals(actual, wanted);

    private JObject SettingsState(string section)
    {
        var values = new JObject();
        foreach (var field in settings.Where(s => s.Section == section))
        {
            var node = SettingControl(field);
            var value = field.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
            var item = new JObject { ["value"] = value!.DeepClone(), ["enabled"] = node["blockedReason"] == null };
            if (node["options"] != null) item["options"] = node["options"]!.DeepClone();
            if (field.Role == "slider") { item["unit"] = "percent"; item["display"] = node["displayValue"]!.DeepClone(); }
            if (node["blockedReason"] != null) item["blockedReason"] = node["blockedReason"]!.DeepClone();
            values[field.Key] = item;
        }
        return new JObject { ["method"] = "game", ["section"] = section, ["settings"] = values, ["windowLeftOpen"] = true, ["pauseMenuOpenedByCommand"] = openedPauseForSettings, ["next"] = openedPauseForSettings ? "Settings is the current workspace. A different gameplay domain cleans up agent-owned Settings and Pause Menu while keeping time paused; explicit/player-owned windows remain pinned." : "Settings remains the current workspace." };
    }

    private static void ValidateSaveName(string name)
    {
        if (name.Length > 20) throw new AgentError("invalid_value", $"Save name must be at most 20 characters; received {name.Length}. Shorten it by at least {name.Length - 20} characters. No UI was opened.");
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) throw new AgentError("invalid_value", "Save name contains a character invalid for a Windows filename.");
    }

    private IEnumerable<Request> Save(string name)
    {
        foreach (var step in OpenPauseItem("Save", SaveScope)) yield return step;
        var input = Exact(GameUi.Controls(ui.Read(SaveScope)).Where(c => Is(c, "input", "Save Name Input")), "save name input");
        if ((string?)input["value"] != name) yield return GameUi.Set(input, new JValue(name));
        yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(SaveScope)).Where(c => Is(c, "button", "Save")), "Save button"));
    }

    private JObject SaveResult(string name)
    {
        var dialogs = DialogState()["dialogs"]!;
        if (dialogs.Any()) return new JObject { ["method"] = "game", ["outcome"] = "confirmation_required", ["save"] = name, ["dialogs"] = dialogs.DeepClone(), ["completed"] = false, ["next"] = "Use game dialog-choose with the exact native overwrite/cancel choice. The API never overwrites implicitly." };
        var listed = GameUi.Controls(ui.Catalog(SaveScope)).Any(c => (string?)c["name"] == "Load Option" && (string?)c["label"] == name);
        return new JObject { ["method"] = "game", ["outcome"] = listed ? "completed" : "input_dispatched", ["save"] = name, ["presentInCatalog"] = listed, ["completed"] = listed, ["windowLeftOpen"] = true, ["next"] = listed ? "Save appears in the refreshed native catalog." : "Re-open save-list to verify persistence." };
    }

    private IEnumerable<Request> Load(string name)
    {
        foreach (var step in OpenLoad()) yield return step;
        var row = ExactSaveRow(ui.Catalog(LoadScope), name);
        yield return GameUi.Click(row);
        yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(LoadScope)).Where(c => Is(c, "button", "Load")), "Load button"));
        foreach (var step in WaitForGameplayReady()) yield return step;
    }

    private JObject LoadResult(string name)
    {
        if (GameplayReady(out var date)) return new JObject { ["method"] = "game", ["outcome"] = "ready", ["save"] = name, ["loaded"] = true, ["completed"] = true, ["dateDisplay"] = date, ["nativePostcondition"] = "gameplay_clock_and_bottom_bar_reachable", ["readinessWaitFrames"] = readinessWaitFrames, ["readinessFrameBudget"] = ReadinessFrameBudget };
        return new JObject { ["method"] = "game", ["outcome"] = "loading_requested", ["save"] = name, ["loaded"] = false, ["completed"] = false, ["readinessWaitFrames"] = readinessWaitFrames, ["readinessFrameBudget"] = ReadinessFrameBudget, ["next"] = "The bounded rendered-frame wait ended before the gameplay clock and ordinary BottomBar were both reachable. Re-read game state later; do not repeat the load command blindly." };
    }

    private JObject SaveList()
    {
        var catalog = ui.Catalog(LoadScope);
        var saves = new JArray(SaveRows(catalog).Select(c => (string)c["label"]!));
        return new JObject { ["method"] = "game", ["activeCatalog"] = true, ["completeCatalog"] = !((bool?)catalog["more"] ?? false), ["saves"] = saves, ["count"] = saves.Count, ["details"] = new JArray(GameUi.Texts(ui.Read(LoadScope)).Select(t => t["text"]!.DeepClone())), ["truncated"] = (bool?)catalog["more"] ?? false, ["next"] = "Use an exact case-sensitive returned name with game save-load or game save-delete. Loading is asynchronous; deletion is explicit and verified." };
    }

    private static IEnumerable<JObject> SaveRows(JObject view) => GameUi.Controls(view).Where(c => (string?)c["name"] == "Load Option" && !string.IsNullOrWhiteSpace((string?)c["label"]) && (string?)c["label"] != "Load Option");

    private static JObject ExactSaveRow(JObject catalog, string name)
    {
        var matches = SaveRows(catalog).Where(c => (string?)c["label"] == name).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No save named '{name}' exists in the active native catalog. Use game save-list for exact case-sensitive names.");
        if (matches.Length != 1) throw new AgentError("game_ui_mismatch", $"The native catalog exposed {matches.Length} saves named '{name}'; no input was dispatched.");
        return matches[0];
    }

    private IEnumerable<Request> DeleteSave(string name)
    {
        foreach (var step in OpenLoad()) yield return step;
        var row = ExactSaveRow(ui.Catalog(LoadScope), name);
        yield return GameUi.Click(row);
        yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(LoadScope)).Where(c => Is(c, "button", "Delete")), "Delete button"));
        var confirmation = ui.Catalog("SaveConfirmationWindow");
        var accept = Exact(GameUi.Controls(confirmation).Where(c => (string?)c["role"] == "button" && (string?)c["label"] == "Yes, delete"), "SaveConfirmationWindow choice 'Yes, delete'");
        if (!GameUi.Controls(confirmation).Any(c => (string?)c["role"] == "button" && (string?)c["label"] == "No, cancel")) throw new AgentError("game_ui_mismatch", "The native save-delete confirmation did not expose the expected cancel choice. Nothing was accepted.");
        var expectedText = "Permantly delete " + name + "?";
        if (!GameUi.Texts(confirmation).Any(t => (string?)t["text"] == expectedText)) throw new AgentError("game_ui_mismatch", $"The native save-delete confirmation did not contain the exact expected text '{expectedText}'. Nothing was accepted.");
        yield return GameUi.Click(accept);
        var remains = SaveRows(ui.Catalog(LoadScope)).Any(c => (string?)c["label"] == name);
        verified = new JObject { ["method"] = "game", ["outcome"] = remains ? "input_dispatched" : "completed", ["save"] = name, ["completed"] = !remains, ["presentAfterDelete"] = remains, ["nativeConfirmation"] = "Yes, delete", ["next"] = remains ? "The exact save is still present in the refreshed native catalog; re-read before deciding whether to retry." : "The exact save is absent from the refreshed native catalog." };
    }

    private IEnumerable<Request> ExitSession(string target)
    {
        var menu = ui.Read("Menu");
        var atMenu = GameUi.Controls(menu).Any(c => Is(c, "button", "New Game"));
        if (target == "menu" && atMenu)
        {
            verified = new JObject { ["method"] = "game", ["outcome"] = "already_at_menu", ["target"] = target, ["completed"] = true };
            yield break;
        }
        if (atMenu)
        {
            yield return GameUi.Click(Exact(GameUi.Controls(menu).Where(c => Is(c, "button", "Quit")), "main-menu Quit button"));
        }
        else
        {
            foreach (var step in OpenPause()) yield return step;
            var button = target == "menu" ? "Exit To Menu" : "Exit To Desktop";
            yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(PauseScope)).Where(c => Is(c, "button", button)), button));
        }
        var confirmation = QuitConfirmationView();
        if (confirmation != null && KnownQuitBody(confirmation, target))
        {
            var accept = Exact(GameUi.Controls(confirmation).Where(c => (string?)c["role"] == "button" && (string?)c["label"] == "Yes"), "QuitConfirmationWindow choice 'Yes'");
            if (!GameUi.Controls(confirmation).Any(c => (string?)c["role"] == "button" && (string?)c["label"] == "No")) throw new AgentError("game_ui_mismatch", "The native quit confirmation did not expose the expected No choice. Nothing was accepted.");
            yield return GameUi.Click(accept);
        }
    }

    private static bool KnownQuitBody(JObject confirmation, string target)
    {
        if (target != "desktop") return false;
        const string prefix = "Are you sure you want to quit to desktop? Any unsaved progress will be lost. | Days since last save: ";
        var matches = GameUi.Texts(confirmation).Select(t => (string?)t["text"]).Where(t => t?.StartsWith(prefix, StringComparison.Ordinal) == true).ToArray();
        return matches.Length == 1 && int.TryParse(matches[0]!.Substring(prefix.Length), out var days) && days >= 0;
    }

    private JObject ExitResult(string target)
    {
        var view = ui.Read("");
        var atMenu = GameUi.Controls(ui.Read("Menu")).Any(c => Is(c, "button", "New Game"));
        if (target == "menu" && atMenu) return new JObject { ["method"] = "game", ["outcome"] = "arrived_menu", ["target"] = target, ["completed"] = true, ["scene"] = view["scene"]!.DeepClone() };
        var dialogs = DialogState()["dialogs"]!;
        if (dialogs.Any()) return new JObject { ["method"] = "game", ["outcome"] = "confirmation_required", ["target"] = target, ["completed"] = false, ["dialogs"] = dialogs.DeepClone(), ["next"] = "Choose an exact native confirmation choice with game dialog-choose. No quit choice was accepted implicitly." };
        return new JObject { ["method"] = "game", ["outcome"] = target == "desktop" ? "exit_requested" : "transition_requested", ["target"] = target, ["completed"] = false, ["expectedDisconnect"] = target == "desktop", ["scene"] = view["scene"]!.DeepClone(), ["next"] = target == "desktop" ? "The native quit input was dispatched. A disconnect is expected but is not proof of process exit." : "Wait for the menu scene transition, then re-read session state. Do not repeat blindly." };
    }

    // New-game methods are completed below from the native MenuGameSetup mapping.
    private static void ValidateNewSession(Request request)
    {
        if (request.Command == "game.session-new-read") return;
        if (request.Target.Length > 0 || request.Value != null) throw new AgentError("invalid_request", "New-session commands use named parameters, not target/value.");
        GameUi.Parameters(request, "companyName", "founderName", "companyType", "difficulty", "startDate", "enableCheats", "randomCompetitors", "companyColorHex");
        foreach (var p in request.Parameters?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            var valid = p.Name is "enableCheats" or "randomCompetitors" ? p.Value.Type == JTokenType.Boolean : p.Value.Type == JTokenType.String;
            if (!valid) throw new AgentError("invalid_value", $"{p.Name} has the wrong type.");
        }
        var company = (string?)request.Parameters?["companyName"];
        var founder = (string?)request.Parameters?["founderName"];
        if (company?.Length > 22 || founder?.Length > 24) throw new AgentError("invalid_value", "Company name may contain at most 22 characters and founder name at most 24.");
        if (request.Parameters?["companyType"] is JToken type && !new[] { "CPU", "CPU, Fabless", "Foundry" }.Contains(type.Value<string>())) throw new AgentError("invalid_value", "companyType must be one exact option: 'CPU', 'CPU, Fabless', or 'Foundry'.");
        if (request.Parameters?["difficulty"] is JToken difficulty && !new[] { "Very Easy", "Easy", "Normal", "Hard", "Very Hard", "Impossible" }.Contains(difficulty.Value<string>())) throw new AgentError("invalid_value", "Unknown difficulty.");
        if (request.Parameters?["startDate"] is JToken startDate && !new[] { "1975", "1980", "1985", "1990", "1995", "2000" }.Contains(startDate.Value<string>())) throw new AgentError("invalid_value", "startDate must be an exact native year option from 1975 through 2000.");
        if (request.Parameters?["companyColorHex"] is JToken color && !ValidHex(color.Value<string>()!)) throw new AgentError("invalid_value", "companyColorHex must contain exactly six hexadecimal RGB digits, with an optional leading #.");
    }

    private IEnumerable<Request> EditNewSession(Request request, bool start)
    {
        const string scope = "GameSetupWindow";
        var view = ui.Read(scope);
        if (GameUi.Controls(view).Length == 0)
        {
            var scene = ui.Native.Scene;
            var menu = ui.Read("Menu");
            var newGameButtons = GameUi.Controls(menu).Where(c => Is(c, "button", "New Game")).ToArray();
            if (newGameButtons.Length == 0 && scene == "Game Scene") throw new AgentError("wrong_scene", "New-session setup is available only from the main menu. The current game and any open draft were left unchanged; run 'game session-exit menu' explicitly, resolve any native confirmation, wait for the menu transition, then retry this command.");
            if (newGameButtons.Length == 0 && scene == "Main Menu Scene") throw new AgentError("not_interactable", "Main Menu Scene is active, but New Game is not currently exposed. Use 'game window-list' and explicitly close the foreground menu window if appropriate, then retry; no input was dispatched. If no foreground window explains it, treat this as a UI mapping mismatch.");
            if (newGameButtons.Length == 0) throw new AgentError("not_interactable", $"New-session setup is unavailable while scene '{scene}' is active. A scene transition may still be in progress; wait and re-read session state before retrying. No input was dispatched.");
            if (newGameButtons.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one exposed New Game button on the main menu, found {newGameButtons.Length}; no input was dispatched.");
            yield return GameUi.Click(newGameButtons[0]);
            view = ui.Read(scope);
        }
        var map = new Dictionary<string, (string Role, string Name)>
        {
            ["companyName"] = ("input", "Company Name Input"), ["founderName"] = ("input", "Founder Name Input"),
            ["companyType"] = ("select", "Company Type Dropdown"), ["difficulty"] = ("select", "Difficulty Dropdown"),
            ["startDate"] = ("select", "Start Date Dropdown"), ["enableCheats"] = ("toggle", "Enable Cheats"),
            ["randomCompetitors"] = ("toggle", "Random Competitors")
        };
        foreach (var p in request.Parameters?.Properties().Where(p => p.Name != "companyColorHex") ?? Enumerable.Empty<JProperty>())
        {
            var spec = map[p.Name];
            var node = Exact(GameUi.Controls(ui.Read(scope)).Where(c => Is(c, spec.Role, spec.Name)), p.Name);
            var actual = spec.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
            if (!JToken.DeepEquals(actual, p.Value)) yield return spec.Role == "select" ? GameUi.Select(node, p.Value.Value<string>()!) : GameUi.Set(node, p.Value);
        }
        if (request.Parameters?["companyColorHex"] is JToken color)
        {
            var normalized = color.Value<string>()!.TrimStart('#').ToUpperInvariant();
            if (GameUi.Controls(ui.Read("ColorPickerWindow")).Length == 0) yield return GameUi.Click(Exact(GameUi.Controls(ui.Read(scope)).Where(c => Is(c, "button", "Open Color Picker")), "company color picker"));
            var input = Exact(GameUi.Controls(ui.Read("ColorPickerWindow")).Where(c => Is(c, "input", "Hex Input")), "HEX color input");
            if (!string.Equals((string?)input["value"], normalized, StringComparison.OrdinalIgnoreCase)) yield return GameUi.Set(input, new JValue(normalized));
            input = Exact(GameUi.Controls(ui.Read("ColorPickerWindow")).Where(c => Is(c, "input", "Hex Input")), "HEX color input");
            if (!string.Equals((string?)input["value"], normalized, StringComparison.OrdinalIgnoreCase)) throw new AgentError("value_not_applied", "Native color picker did not retain the requested HEX value. The game was not started.");
            newCompanyColor = normalized;
            yield return GameUi.Click(Exact(GameUi.Controls(ui.Read("ColorPickerWindow")).Where(c => (string?)c["role"] == "button" && (string?)c["label"] == "Apply"), "Apply color button"));
        }
        foreach (var p in request.Parameters?.Properties().Where(p => p.Name != "companyColorHex") ?? Enumerable.Empty<JProperty>())
        {
            var spec = map[p.Name];
            var node = Exact(GameUi.Controls(ui.Read(scope)).Where(c => Is(c, spec.Role, spec.Name)), p.Name);
            var actual = spec.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"];
            if (!JToken.DeepEquals(actual, p.Value)) throw new AgentError("value_not_applied", $"Native setup reports {p.Name}='{actual}', not requested '{p.Value}'. The game was not started.");
        }
        if (start)
        {
            var readyForm = ui.Read(scope);
            var company = Exact(GameUi.Controls(readyForm).Where(c => Is(c, "input", "Company Name Input")), "companyName");
            var founder = Exact(GameUi.Controls(readyForm).Where(c => Is(c, "input", "Founder Name Input")), "founderName");
            if (string.IsNullOrWhiteSpace((string?)company["value"]) || string.IsNullOrWhiteSpace((string?)founder["value"])) throw new AgentError("invalid_request", "The native setup form requires non-empty company and founder names before Start. Preview or supply them first; the game was not started.");
            yield return GameUi.Click(Exact(GameUi.Controls(readyForm).Where(c => Is(c, "button", "Start")), "Start button"));
            foreach (var step in WaitForGameplayReady()) yield return step;
        }
    }

    private IEnumerable<Request> WaitForGameplayReady()
    {
        while (readinessWaitFrames < ReadinessFrameBudget && !GameplayReady(out _))
        {
            var wait = GameUi.NativeStep("Wait for gameplay readiness", () => { });
            wait.SettleFrames = ReadinessChunkFrames;
            wait.NativeVisualHandled = true;
            yield return wait;
            readinessWaitFrames += ReadinessChunkFrames;
        }
    }

    private bool GameplayReady(out string? dateDisplay)
    {
        var clock = ui.Read("DateAndTimeControl");
        var dates = GameUi.Texts(clock).Where(t => ((string?)t["context"] ?? "").EndsWith("/DateAndTimeControl", StringComparison.Ordinal)).Select(t => (string?)t["text"]).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
        var bottom = ui.Read("DESKTOP -> Bottom/BottomBar");
        var bottomReachable = GameUi.Controls(bottom).Any(c => (string?)c["name"] == "Door" && c["blockedReason"] == null);
        dateDisplay = dates.Length == 1 ? dates[0] : null;
        return dateDisplay != null && bottomReachable;
    }

    private JObject NewSessionState(bool startRequested)
    {
        const string scope = "GameSetupWindow";
        var view = ui.Read(scope);
        if (GameUi.Controls(view).Length == 0)
        {
            if (startRequested && GameplayReady(out var date)) return new JObject { ["method"] = "game", ["outcome"] = "ready", ["gameStarted"] = true, ["completed"] = true, ["dateDisplay"] = date, ["nativePostcondition"] = "gameplay_clock_and_bottom_bar_reachable", ["readinessWaitFrames"] = readinessWaitFrames, ["readinessFrameBudget"] = ReadinessFrameBudget };
            return new JObject { ["method"] = "game", ["outcome"] = startRequested ? "loading_requested" : "not_open", ["gameStarted"] = false, ["completed"] = false, ["readinessWaitFrames"] = readinessWaitFrames, ["readinessFrameBudget"] = ReadinessFrameBudget, ["next"] = startRequested ? "The bounded rendered-frame wait ended before the gameplay clock and ordinary BottomBar were both reachable. Re-read game state later; do not retry Start." : "Use game session-new-preview with named fields to open and prepare the form." };
        }
        var fields = new JObject();
        foreach (var pair in new Dictionary<string, (string Role, string Name)>
        {
            ["companyName"] = ("input", "Company Name Input"), ["founderName"] = ("input", "Founder Name Input"),
            ["companyType"] = ("select", "Company Type Dropdown"), ["difficulty"] = ("select", "Difficulty Dropdown"),
            ["startDate"] = ("select", "Start Date Dropdown"), ["enableCheats"] = ("toggle", "Enable Cheats"), ["randomCompetitors"] = ("toggle", "Random Competitors")
        })
        {
            var node = Exact(GameUi.Controls(view).Where(c => Is(c, pair.Value.Role, pair.Value.Name)), pair.Key);
            var item = new JObject { ["value"] = (pair.Value.Role == "select" ? node["options"]![node["value"]!.Value<int>()] : node["value"])!.DeepClone(), ["enabled"] = node["blockedReason"] == null };
            if (node["options"] != null) item["options"] = node["options"]!.DeepClone();
            if (node["blockedReason"] != null) item["blockedReason"] = node["blockedReason"]!.DeepClone();
            fields[pair.Key] = item;
        }
        var result = new JObject { ["method"] = "game", ["outcome"] = startRequested ? "loading_requested" : "preview", ["gameStarted"] = false, ["completed"] = false, ["fields"] = fields, ["summary"] = new JArray(GameUi.Texts(view).Select(t => t["text"]!.DeepClone())), ["next"] = startRequested ? "The bounded readiness wait ended while the setup form was still visible. Re-read later and do not retry Start blindly." : "Review the native preview. session-new-start is an explicit commit and may begin asynchronous scene loading." };
        if (startRequested) { result["readinessWaitFrames"] = readinessWaitFrames; result["readinessFrameBudget"] = ReadinessFrameBudget; }
        if (newCompanyColor != null) { result["companyColorHexRequested"] = newCompanyColor; result["colorApplyDispatched"] = true; }
        return result;
    }

    private static bool ValidHex(string value)
    {
        var text = value.StartsWith("#", StringComparison.Ordinal) ? value.Substring(1) : value;
        return text.Length == 6 && text.All(Uri.IsHexDigit);
    }
}
