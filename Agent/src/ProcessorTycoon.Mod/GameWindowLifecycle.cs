using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProcessorTycoonMod;

// Only non-draft workspaces are eligible. Dialogs, CPU drafts, editors and negotiations are never auto-closed.
internal sealed class GameWindowLifecycle
{
    internal sealed class BeginState
    {
        public HashSet<int> Before = new();
        public string[] Closed = Array.Empty<string>();
        public bool CloseOwnedPauseMenu;
        internal PauseState PauseBefore = PauseState.None;
    }

    private sealed class Window
    {
        public Transform Root = null!;
        public Button Close = null!;
        public string Family = "";
    }

    internal readonly struct PauseState
    {
        public static readonly PauseState None = new(0, "", false);
        public readonly int Id;
        public readonly string Scene;
        public readonly bool Open;
        public PauseState(int id, string scene, bool open) { Id = id; Scene = scene; Open = open; }
    }

    private static readonly Type? pauseMenuType = Type.GetType("ProcessorTycoon.TimeSystem.PauseMenu, Assembly-CSharp");
    private static readonly PropertyInfo? pauseMenuOpen = pauseMenuType?.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
    // The plugin's single lifecycle, so window-list can report which visible windows the agent opened.
    internal static GameWindowLifecycle? Current { get; private set; }
    private readonly Dictionary<int, Window> owned = new();
    // Every workspace the agent opened (owned ones, explicit window-open pins and factory drafts) until the player adopts it.
    private readonly Dictionary<int, Window> agentOpened = new();
    private readonly List<RaycastResult> hits = new();
    private long playerCursor;
    private int ownedPauseId;
    private string ownedPauseScene = "";
    private string ownedPauseFamily = "";

    private static readonly Dictionary<string, string> families = new()
    {
        ["ResearchTreeWindow"] = "research", ["ProductionWindow"] = "production", ["AnalysisWindow"] = "market", ["CreateHardwareWindow"] = "hardware",
        ["FactoryManagementWindow"] = "factory-management",
        ["InspectorWindow"] = "inspector", ["ContractWindow"] = "contracts", ["ContractManagerWindow"] = "contracts-management",
        ["BusinessWindow"] = "business", ["ContractInspectorWindow"] = "business", ["SettingsWindow"] = "settings", ["EmailWindow"] = "email",
        ["NotificationSettingsWindow"] = "settings", ["StatisticsWindow"] = "settings", ["GameplaySettingsWindow"] = "settings", ["LoadWindow"] = "saves"
    };

    private static readonly HashSet<string> coveredCloseable = new(StringComparer.Ordinal) { "ResearchTreeWindow", "ProductionWindow", "AnalysisWindow", "InspectorWindow", "ContractWindow", "ContractManagerWindow", "BusinessWindow", "EmailWindow", "StatisticsWindow" };

    private static readonly Dictionary<string, string> explicitPins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Analysis"] = "AnalysisWindow", ["Notification Settings"] = "NotificationSettingsWindow", ["Statistics"] = "StatisticsWindow",
        ["Gameplay Settings"] = "GameplaySettingsWindow", ["Load"] = "LoadWindow"
    };

    public GameWindowLifecycle() => Current = this;

    public void PlayerInput(long cursor)
    {
        if (cursor == playerCursor) return;
        playerCursor = cursor;
        // Ownership transfer per window: the player adopts the window under the pointer (click/scroll) or holding keyboard
        // focus; it is then never cleaned up automatically. Other agent-opened windows stay eligible, so unrelated input
        // (desktop, speed buttons, another window) no longer leaves stale agent views behind. The parent menu stays conservative.
        var touched = TouchedWindow();
        if (touched != null)
            foreach (var map in new[] { owned, agentOpened })
                foreach (var id in map.Where(pair => pair.Value.Root == touched).Select(pair => pair.Key).ToArray()) map.Remove(id);
        ClearPauseOwnership();
    }

    private Transform? TouchedWindow()
    {
        GameObject? target = null;
        var pointer = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2) || Input.mouseScrollDelta.sqrMagnitude > 0;
        if (pointer && EventSystem.current != null)
        {
            hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = Input.mousePosition }, hits);
            // Input on the agent's own tray/overlay adopts nothing.
            target = hits.Select(hit => hit.gameObject).FirstOrDefault(hit => hit != null);
            if (target != null && target.GetComponentInParent<AgentOverlayMarker>() != null) return null;
        }
        else if (!pointer && EventSystem.current != null) target = EventSystem.current.currentSelectedGameObject;
        for (var current = target != null ? target.transform : null; current != null; current = current.parent)
            if (families.ContainsKey(current.name)) return current;
        return null;
    }

    // "owned" = agent-opened and closed automatically on the next domain change; "agent" = agent-opened but kept
    // (explicit window-open pin or a factory draft); null = opened by the player, adopted by player input, or unknown.
    internal Dictionary<string, string> Openers()
    {
        Prune(Snapshot());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var window in agentOpened.Values.Where(window => window.Root != null)) result[window.Root.name] = "agent";
        foreach (var window in owned.Values.Where(window => window.Root != null)) result[window.Root.name] = "owned";
        return result;
    }

    // Every open workspace window of a known family (active, with its title-bar Close button), visible or fully covered.
    internal static HashSet<string> OpenScopes() => new(Snapshot().Values.Select(window => window.Root.name), StringComparer.Ordinal);

    private void Prune(Dictionary<int, Window> current)
    {
        foreach (var map in new[] { owned, agentOpened })
            foreach (var id in map.Keys.Where(id => !current.ContainsKey(id)).ToArray()) map.Remove(id);
    }

    private static Dictionary<int, Window> Snapshot()
    {
        var result = new Dictionary<int, Window>();
        foreach (var button in Resources.FindObjectsOfTypeAll<Button>())
        {
            if (!button.gameObject.activeInHierarchy || !button.gameObject.scene.IsValid() || button.transform.parent?.name != "TopBar" || !button.name.Contains("Close")) continue;
            for (var root = button.transform.parent; root != null; root = root.parent)
                if (families.TryGetValue(root.name, out var family)) { result[root.GetInstanceID()] = new Window { Root = root, Close = button, Family = family }; break; }
        }
        return result;
    }

    private static PauseState CurrentPause()
    {
        if (pauseMenuType == null || pauseMenuOpen == null) return PauseState.None;
        var menus = Resources.FindObjectsOfTypeAll(pauseMenuType).OfType<Component>().Where(c => c.gameObject.scene.IsValid()).ToArray();
        var menu = menus.FirstOrDefault(c => (bool)pauseMenuOpen.GetValue(c)!) ?? menus.FirstOrDefault();
        return menu == null ? PauseState.None : new PauseState(menu.GetInstanceID(), menu.gameObject.scene.name, (bool)pauseMenuOpen.GetValue(menu)!);
    }

    private static string? Family(string command)
    {
        var verb = command.Substring(5);
        if (verb == "production-history") return "market";
        if (verb == "projects-wait") return "time";
        if (verb is "production-capacity" or "production-expansion-preview" or "production-expand" or "production-line-sale-preview" or "production-sell-lines" or "production-division-sale-preview" or "production-sell-division") return "factory-management";
        if (verb.StartsWith("research-") || verb.StartsWith("modifier-research-") || verb.StartsWith("projects-")) return "research";
        if (verb.StartsWith("product-") || verb.StartsWith("production-")) return "production";
        if (verb.StartsWith("market-") || verb.StartsWith("sales-") || verb.StartsWith("analysis-") || verb.StartsWith("finance-")) return "market";
        if (verb.StartsWith("inspector-")) return "inspector";
        if (verb is "contracts-active" or "contracts-manage" or "contracts-break") return "contracts-management";
        if (verb.StartsWith("contracts-")) return "contracts";
        if (verb.StartsWith("business-")) return "business";
        if (verb.StartsWith("settings-") || verb.StartsWith("notification-") || verb.StartsWith("statistics-") || verb.StartsWith("gameplay-")) return "settings";
        if (verb is "save-list" or "save-load" or "save-delete" or "save-create") return "saves";
        if (verb.StartsWith("email-")) return "email";
        if (verb.StartsWith("cpu-")) return "cpu";
        if (verb.StartsWith("socket-") || verb.StartsWith("hardware-")) return "hardware";
        if (verb == "time-advance") return "time";
        return null;
    }

    private static bool MayOpenSafePause(string command)
    {
        var verb = command.Substring(5);
        return verb.StartsWith("settings-") || verb.StartsWith("notification-") || verb.StartsWith("statistics-") || verb.StartsWith("gameplay-") || verb is "save-list" or "save-load" or "save-delete" or "save-create";
    }

    private static bool MayLeaveFactoryDraft(string command) => command is "game.production-expansion-preview" or "game.production-expand" or "game.production-line-sale-preview" or "game.production-sell-lines" or "game.production-division-sale-preview" or "game.production-sell-division";

    public BeginState Begin(Request request, GenericUi ui)
    {
        var current = Snapshot();
        Prune(current);
        if (MayLeaveFactoryDraft(request.Command))
            foreach (var pair in owned.Where(pair => pair.Value.Root.name == "FactoryManagementWindow").ToArray()) owned.Remove(pair.Key);
        var pause = CurrentPause();
        if (!pause.Open || pause.Id != ownedPauseId || pause.Scene != ownedPauseScene) ClearPauseOwnership();
        var closed = new List<string>();
        var family = Family(request.Command);
        if (family != null)
        {
            // Close reachable owned windows top-down. A player/modal window can prevent cleanup; never bypass it.
            bool changed;
            do
            {
                changed = false;
                foreach (var pair in owned.ToArray())
                    if (!Compatible(pair.Value.Family, family) && ui.TryCloseWorkspace(pair.Value.Close))
                    {
                        closed.Add(pair.Value.Root.name); owned.Remove(pair.Key); agentOpened.Remove(pair.Key); changed = true;
                    }
            } while (changed);
            // Owned read-only views still hidden behind a window the agent does not own (a pin, a player window or a
            // draft) are closed through their own native Close button so they do not pile up behind it. Never while a
            // native time-blocking popup or the Pause Menu is open (an open form such as the CPU designer is no popup);
            // Manage Lines (possible pending draft) is excluded.
            if (TimeAdvanceController.PauseTriggerScopes().All(SessionGameApi.FormScopes.Contains))
                foreach (var pair in owned.ToArray())
                    if (!Compatible(pair.Value.Family, family) && coveredCloseable.Contains(pair.Value.Root.name) && pair.Value.Close != null && pair.Value.Close.gameObject.activeInHierarchy && pair.Value.Close.interactable)
                    {
                        pair.Value.Close.onClick.Invoke();
                        closed.Add(pair.Value.Root.name); owned.Remove(pair.Key); agentOpened.Remove(pair.Key);
                    }
        }
        var closePause = family != null && ownedPauseId != 0 && ownedPauseFamily != family && !owned.Values.Any(window => window.Family == ownedPauseFamily);
        return new BeginState { Before = new HashSet<int>(Snapshot().Keys), Closed = closed.ToArray(), CloseOwnedPauseMenu = closePause, PauseBefore = pause };
    }

    private static bool Compatible(string openFamily, string requestedFamily) => openFamily == requestedFamily || requestedFamily == "contracts-management" && openFamily == "contracts" || requestedFamily == "factory-management" && openFamily == "production";

    // Prepend these bounded steps to module Prepare after child-close layout has settled.
    public IEnumerable<Request> CleanupParent(BeginState state, GenericUi ui)
    {
        if (!state.CloseOwnedPauseMenu) yield break;
        var pause = CurrentPause();
        if (!pause.Open || pause.Id != ownedPauseId || pause.Scene != ownedPauseScene) { ClearPauseOwnership(); yield break; }
        yield return GameUi.NativeStep("Keep game time paused before closing agent-opened Pause Menu", TimeAdvanceController.SafePause);
        var view = new GameUi(ui).Read("PauseMenu");
        var button = GameUi.One(GameUi.Controls(view).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Continue"), "agent-owned Pause Menu Continue button");
        yield return GameUi.Click(button);
    }

    // Call after CleanupParent is exhausted; the final Continue action has then received scheduler settling.
    public string? CompleteParentCleanup(BeginState state)
    {
        if (!state.CloseOwnedPauseMenu || ownedPauseId == 0) { state.PauseBefore = CurrentPause(); return null; }
        var pause = CurrentPause();
        var paused = (bool?)TimeAdvanceController.ReadClock()["paused"] == true;
        if (pause.Open && pause.Id == ownedPauseId && pause.Scene == ownedPauseScene) throw new AgentError("game_ui_mismatch", "The agent-owned Pause Menu remained open after native Continue; the incoming command was not started.");
        if (!paused) throw new AgentError("game_ui_mismatch", "Pause Menu closed but the native clock was not paused; the incoming command was not started.");
        ClearPauseOwnership();
        state.PauseBefore = pause;
        return "PauseMenu";
    }

    public void Finish(Request request, BeginState state, bool playerIntervened)
    {
        var after = Snapshot();
        Prune(after);
        // Windows that appeared during a command interrupted by player input are left to the player; earlier ownership stays.
        if (playerIntervened) { ClearPauseOwnership(); return; }
        var pause = CurrentPause();
        if (request.Command == "game.window-open")
        {
            // Explicit opening is a pin, including its logical Pause Menu parent. A pin stays agent-opened (window-list
            // reports it) only when the agent opened or already owned the window; a player's window stays the player's.
            var pinnedScope = explicitPins.TryGetValue(request.Target, out var mappedScope) ? mappedScope : request.Target;
            foreach (var pair in after)
            {
                var isNew = !state.Before.Contains(pair.Key);
                if (!isNew && !pair.Value.Root.name.Equals(pinnedScope, StringComparison.OrdinalIgnoreCase) && !pair.Value.Family.Equals(request.Target, StringComparison.OrdinalIgnoreCase)) continue;
                var agentWindow = owned.Remove(pair.Key) | agentOpened.ContainsKey(pair.Key);
                if (isNew || agentWindow) agentOpened[pair.Key] = pair.Value;
            }
            if (pause.Open) ClearPauseOwnership();
            return;
        }
        foreach (var pair in after.Where(pair => !state.Before.Contains(pair.Key)))
        {
            agentOpened[pair.Key] = pair.Value;
            if (!MayLeaveFactoryDraft(request.Command) || pair.Value.Root.name != "FactoryManagementWindow") owned[pair.Key] = pair.Value;
        }
        var family = Family(request.Command);
        if (!state.PauseBefore.Open && pause.Open && family != null && MayOpenSafePause(request.Command) && pause.Id == state.PauseBefore.Id && pause.Scene == state.PauseBefore.Scene)
        {
            ownedPauseId = pause.Id;
            ownedPauseScene = pause.Scene;
            ownedPauseFamily = family;
        }
        else if (!pause.Open && pause.Id == ownedPauseId) ClearPauseOwnership();
    }

    private void ClearPauseOwnership()
    {
        ownedPauseId = 0;
        ownedPauseScene = "";
        ownedPauseFamily = "";
    }
}
