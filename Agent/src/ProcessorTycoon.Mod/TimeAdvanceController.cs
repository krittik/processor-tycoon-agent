using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

// Narrow bridge to public native player-time controls. It never invokes simulation ticks or assigns game dates.
[DefaultExecutionOrder(-32000)]
internal sealed class TimeAdvanceController : MonoBehaviour, IDisposable
{
    private const string DateTypeName = "ProcessorTycoon.TimeSystem.DateController";
    private const string PauseTriggerTypeName = "ProcessorTycoon.TimeSystem.PauseTrigger";
    private const string PauseMenuTypeName = "ProcessorTycoon.TimeSystem.PauseMenu";
    private const string ButtonsTypeName = "ProcessorTycoon.UI.SelectableButtonsHandler";
    private Func<bool>? agentPaused;
    private Func<JArray>? visibleDialogs;
    private Type dateType = null!;
    private Type pauseTriggerType = null!;
    private object? dateController;
    private EventInfo dayEvent = null!;
    private Action? dayHandler;
    private DateTime startDate;
    private DateTime targetDate;
    private DateTime stoppedDate;
    private int requestedSpeed;
    private bool active;
    private bool complete;
    private bool completingAtEndOfFrame;
    private string? pendingWake;
    private string? error;
    private readonly List<string> reasons = new();
    private JArray dialogs = new();
    private IReadOnlyDictionary<int, ResearchGameApi.ProjectState> startingProjects = new Dictionary<int, ResearchGameApi.ProjectState>();
    private JArray projectChanges = new();
    private JObject? waitedProject;
    private int? waitedProjectId;
    private bool uniqueProjectName;
    private bool readyForRelease;
    private bool waitingForProject;
    private bool startedInDefault;
    private decimal? creditBelow;

    public bool IsComplete => complete;

    public void Initialize(Func<bool>? agentPaused = null, Func<JArray>? visibleDialogs = null)
    {
        this.agentPaused = agentPaused;
        this.visibleDialogs = visibleDialogs;
        dateType = NativeType(DateTypeName);
        pauseTriggerType = NativeType(PauseTriggerTypeName);
        dayEvent = dateType.GetEvent("OnDayPassed", BindingFlags.Instance | BindingFlags.Public) ?? throw new AgentError("game_ui_mismatch", "Native DateController.OnDayPassed is unavailable in this build.");
    }

    public static void Validate(Request request)
    {
        if (request.Value != null) throw new AgentError("invalid_request", "Time operations do not take value.");
        if (request.Command == "game.projects-wait")
        {
            GameUi.RequiredTarget(request);
            GameUi.Parameters(request, "maxDays", "speed", "creditBelow");
            var maximum = request.Parameters?["maxDays"];
            if (maximum != null && (maximum.Type != JTokenType.Integer || maximum.Value<int>() < 1)) throw new AgentError("invalid_value", "maxDays must be a positive integer; the default is 365 days.");
        }
        else
        {
            if (request.Target.Length > 0) throw new AgentError("invalid_request", "time-advance uses named parameters, not target.");
            GameUi.Parameters(request, "days", "targetDate", "speed", "creditBelow", "includeProduction");
            var include = request.Parameters?["includeProduction"];
            if (include != null && include.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "includeProduction must be a boolean.");
            var days = request.Parameters?["days"];
            var target = request.Parameters?["targetDate"];
            if ((days == null) == (target == null)) throw new AgentError("invalid_request", "time-advance requires exactly one of days or targetDate.");
            if (days != null && (days.Type != JTokenType.Integer || days.Value<int>() < 0)) throw new AgentError("invalid_value", "days must be a non-negative integer.");
            if (target != null && (target.Type != JTokenType.String || !DateTime.TryParseExact(target.Value<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw new AgentError("invalid_value", "targetDate must be an ISO date in yyyy-MM-dd form.");
        }
        var credit = request.Parameters?["creditBelow"];
        if (credit != null && credit.Type is not (JTokenType.Integer or JTokenType.Float)) throw new AgentError("invalid_value", "creditBelow must be a number in dollars (it may be negative).");
        var speed = request.Parameters?["speed"];
        if (speed != null && (speed.Type != JTokenType.Integer || speed.Value<int>() is < 1 or > 3)) throw new AgentError("invalid_value", "speed must be 1, 2 or 3.");
    }

    internal static DateTime Deadline(Request request, DateTime start) => request.Command == "game.projects-wait" ? start.AddDays(request.Parameters?["maxDays"]?.Value<int>() ?? 365) : request.Parameters?["days"] != null ? start.AddDays(request.Parameters["days"]!.Value<int>()) : DateTime.ParseExact(request.Parameters!["targetDate"]!.Value<string>()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Begin(Request request)
    {
        if (active) throw new AgentError("operation_in_progress", "A time advance is already active.");
        if (complete) throw new AgentError("invalid_operation", "This controller already completed an advance; create a new controller.");
        Validate(request);
        EnsureInitialized();
        dateController = Instance(dateType);
        if (!Alive(dateController)) throw new AgentError("not_available", "Native game time is not available in the current scene.");
        startDate = CurrentDate();
        targetDate = Deadline(request, startDate);
        if (targetDate < startDate) throw new AgentError("invalid_value", $"targetDate {Iso(targetDate)} is earlier than current date {Iso(startDate)}.");
        requestedSpeed = request.Parameters?["speed"]?.Value<int>() ?? 3;
        startingProjects = ResearchGameApi.ProjectStates();
        waitingForProject = request.Command == "game.projects-wait";
        startedInDefault = (bool?)BalanceSnapshot.Default()["active"] == true;
        creditBelow = request.Parameters?["creditBelow"]?.Value<decimal>();
        if (waitingForProject)
        {
            // A repeated name-based wait on an already-open release form needs no extra simulation day.
            readyForRelease = !startingProjects.Values.Any(p => p.Name == request.Target) && ResearchGameApi.ReadyReleaseName() == request.Target;
            if (readyForRelease) waitedProject = new JObject { ["name"] = request.Target, ["status"] = "Ready for release" };
            else
            {
                var selected = ResearchGameApi.ProjectWaitTarget(request.Target);
                waitedProjectId = selected.Id;
                waitedProject = new JObject { ["projectRef"] = selected.Card["projectRef"]!.DeepClone(), ["name"] = selected.Card["name"]!.DeepClone(), ["status"] = selected.Card["status"]!.DeepClone(), ["timeLeftDays"] = selected.Card["timeLeftDays"]?.DeepClone(), ["stillListed"] = true };
                uniqueProjectName = startingProjects.Values.Count(p => p.Name == (string?)waitedProject["name"]) == 1;
            }
        }
        active = true;
        try
        {
            dayHandler = OnDayPassed;
            dayEvent.AddEventHandler(dateController, dayHandler);
            SafePauseInstance();
            if (readyForRelease) { Complete("project_ready"); return; }
            if (targetDate == startDate) { Complete("target_reached"); return; }
            if (creditBelow != null && CurrentCredit() < creditBelow) { Complete("credit_below"); return; }
            CaptureDialogs();
            if (ActivePauseTrigger() || dialogs.Count > 0) { Complete(OnlyForms() ? "form_open" : "popup_already_open"); return; }
            // Native speed buttons deselect in Update; reselecting the previous speed in this frame is ignored.
            completingAtEndOfFrame = true;
            StartCoroutine(SettleAfterDay());
        }
        catch
        {
            try { SafePauseInstance(); } catch { }
            active = false;
            Unsubscribe();
            throw;
        }
    }

    public JObject Result()
    {
        if (!complete) throw new AgentError("operation_not_complete", "Time advance is still active.");
        var speed = SelectedSpeed();
        // Multiplayer peer: the host keeps time running; there is no local pause to confirm.
        bool hostClock = !MultiplayerInterop.CanControlTime;
        if (speed != "pause" && !hostClock) error ??= "The native pause postcondition was not confirmed. Read time state and explicitly pause before continuing.";
        var outcome = error != null ? "failed" : readyForRelease ? "ready_for_release" : waitingForProject && reasons.Contains("limit_reached") ? "limit_reached" : reasons.Contains("target_reached") ? "completed" : reasons.Contains("cancelled") || reasons.Contains("agent_paused") ? "cancelled" : "woken";
        var result = new JObject
        {
            ["method"] = "game", ["outcome"] = outcome, ["startDate"] = Iso(startDate), ["targetDate"] = Iso(targetDate),
            ["stoppedDate"] = Iso(stoppedDate), ["daysAdvanced"] = (stoppedDate - startDate).Days, ["requestedSpeed"] = requestedSpeed,
            ["selectedSpeed"] = speed, ["paused"] = speed == "pause", ["wakeReasons"] = new JArray(reasons),
            ["dialogs"] = dialogs.DeepClone(), ["error"] = error == null ? null : new JValue(error),
            ["projectChanges"] = projectChanges.DeepClone(), ["finances"] = FinanceSummary(),
            ["next"] = (hostClock && error == null ? "Multiplayer: days keep passing at the host's pace while you act. " : "") + (error != null ? "Read the error and current time state; explicitly pause if needed. Do not repeat this advance blindly." : readyForRelease ? "The CPU release form is open and time is paused. Use game projects-release-read to inspect, then game projects-release with the exact draft name to commit; waiting never releases automatically." : OnlyForms() ? "An open form (dialogs[].kind form, for example the CPU designer) keeps game time paused; it is not a popup and has no dialog choice. Close it with game window-close NAME (dialogs[].name) or finish it, then advance again." : dialogs.Count > 0 ? "Time is left paused and the remembered resume speed was reset. Resolve the exact native dialog choice explicitly; this advance will not resume automatically." : reasons.Contains("project_due") ? "A project reached 0 days but may still be Testing, not complete. Use game projects-wait with its name or projectRef to wait beyond zero for the native release form or project-list change without guessing extra days." : waitingForProject ? "Time is paused. Check the returned project and wake reason. A disappeared row alone does not verify completion; inspect the affected workflow. If the day limit was reached, continue projects-wait with an appropriate --max-days." : "The advance is finished and will not resume automatically.")
        };
        if (reasons.Contains("default_started")) result["next"] = "Available Credit fell below zero: the company is now in default with a native bankruptcy countdown (finances.default.daysLeft). The countdown runs to zero regardless; Available Credit must be zero or more when it ends or the company goes bankrupt. Reassess spending before advancing further. " + result["next"];
        else if (reasons.Contains("credit_below")) result["next"] = "Available Credit crossed the requested creditBelow threshold. Reassess project/research/factory spending before advancing further. " + result["next"];
        if (waitingForProject)
        {
            result["project"] = waitedProject?.DeepClone();
            result["readyForRelease"] = readyForRelease;
            result["completionVerified"] = readyForRelease;
            result["released"] = false;
        }
        return result;
    }

    public void Cancel(string reason)
    {
        if (!active) return;
        var wake = string.IsNullOrWhiteSpace(reason) ? "cancelled" : reason;
        SafePauseInstance();
        if (!reasons.Contains(wake)) reasons.Add(wake);
        pendingWake ??= wake;
        // A speed resume may have happened after the last button Update. Complete only after pause can reselect.
        if (!completingAtEndOfFrame) { completingAtEndOfFrame = true; StartCoroutine(SettleAfterDay()); }
    }

    public void Dispose()
    {
        try { if (active) Complete("disposed"); }
        catch
        {
            active = false;
            complete = true;
            if (!reasons.Contains("disposed")) reasons.Add("disposed");
        }
        finally
        {
            try { Unsubscribe(); } catch { }
            try { StopAllCoroutines(); } catch { }
        }
    }

    private void OnDestroy() => Dispose();

    private static decimal? CurrentCredit()
    {
        var finances = BalanceSnapshot.Read();
        return (bool?)finances["available"] == true ? BalanceSnapshot.Money((string?)finances["availableCredit"]) : null;
    }

    private static JObject FinanceSummary()
    {
        try
        {
            var finances = BalanceSnapshot.Read();
            if ((bool?)finances["available"] != true) return new JObject { ["available"] = false };
            return new JObject { ["available"] = true, ["cash"] = finances["cashDisplay"]?.DeepClone(), ["balance"] = finances["balanceDisplay"]?.DeepClone(), ["availableCredit"] = finances["availableCredit"]?.DeepClone(), ["default"] = finances["default"]?.DeepClone() };
        }
        catch { return new JObject { ["available"] = false }; }
    }

    internal static void SafePause()
    {
        // Multiplayer peer: the host drives the clock; a local speed change would only mislabel the host's speed.
        if (!MultiplayerInterop.CanControlTime) return;
        var type = NativeType(DateTypeName);
        var controller = Instance(type);
        Invoke(type, controller, "ManualSetTimeSpeed", 0, false);
        Invoke(type, controller, "ManualPause", false);
    }

    internal static JObject ReadClock()
    {
        var type = NativeType(DateTypeName);
        var controller = Instance(type);
        var date = (DateTime)(type.GetProperty("CurrentDate", BindingFlags.Instance | BindingFlags.Public)?.GetValue(controller) ?? throw new MissingMemberException(DateTypeName, "CurrentDate"));
        var selectedControl = SelectedButtonName(type, controller);
        var speed = NormalizeSpeed(selectedControl);
        var clock = new JObject { ["date"] = Iso(date), ["selectedSpeed"] = speed, ["selectedControl"] = selectedControl, ["paused"] = speed == "pause" };
        // A Multiplayer session runs one shared clock: open forms never pause it, and only the host may.
        if (MultiplayerInterop.Active) clock["multiplayerSession"] = true;
        return clock;
    }

    private void Update()
    {
        if (!active) return;
        try
        {
            if (!Alive(dateController)) { Fail("Native DateController disappeared during the advance."); return; }
            var wake = agentPaused?.Invoke() == true ? "agent_paused" : ActivePauseTrigger() ? "popup" : PlayerInput() ? "player_input" : null;
            if (wake != null)
            {
                SafePauseInstance();
                if (!reasons.Contains(wake)) reasons.Add(wake);
                pendingWake ??= wake;
                if (!completingAtEndOfFrame) { completingAtEndOfFrame = true; StartCoroutine(SettleAfterDay()); }
            }
        }
        catch (Exception exception) { Fail(exception.InnerException?.Message ?? exception.Message); }
    }

    private void OnDayPassed()
    {
        if (!active) return;
        try
        {
            var current = CurrentDate();
            SafePauseInstance();
            if (current >= targetDate)
            {
                if (current > targetDate) error = $"Native date overshot the requested target: {Iso(current)} > {Iso(targetDate)}.";
                pendingWake = current > targetDate ? "overshoot" : waitingForProject ? "limit_reached" : "target_reached";
            }
            else if (ActivePauseTrigger()) pendingWake = "popup";
            completingAtEndOfFrame = true;
            StartCoroutine(SettleAfterDay());
        }
        catch (Exception exception) { Fail(exception.InnerException?.Message ?? exception.Message); }
    }

    private IEnumerator SettleAfterDay()
    {
        yield return new WaitForEndOfFrame();
        if (!active) yield break;
        var failed = false;
        try
        {
            SafePauseInstance();
            CaptureDialogs();
            if (ActivePauseTrigger() || dialogs.Count > 0) { var blocked = BlockReason(); pendingWake ??= blocked; if (!reasons.Contains(blocked)) reasons.Add(blocked); }
        }
        catch (Exception exception) { Fail(exception.InnerException?.Message ?? exception.Message); failed = true; }
        if (failed || !active) yield break;
        // Keep time paused through one further Update so release/popup handlers that ran before DateController
        // on the completion frame can expose their native UI without allowing another date tick.
        yield return null;
        if (!active) yield break;
        try
        {
            SafePauseInstance();
            CaptureDialogs();
            if (ActivePauseTrigger() || dialogs.Count > 0) { var blocked = BlockReason(); pendingWake ??= blocked; if (!reasons.Contains(blocked)) reasons.Add(blocked); }
            if (!reasons.Contains("player_input"))
            {
                var currentProjects = ResearchGameApi.ProjectStates();
                foreach (var entry in startingProjects.Where(p => !currentProjects.ContainsKey(p.Key)))
                    projectChanges.Add(new JObject { ["name"] = entry.Value.Name, ["change"] = "no_longer_listed", ["previousStatus"] = entry.Value.Status, ["completionVerified"] = false });
                if (projectChanges.Count > 0) { pendingWake ??= "project_changed"; if (!reasons.Contains("project_changed")) reasons.Add("project_changed"); }
                foreach (var entry in startingProjects.Where(p => !waitingForProject && p.Value.TimeLeftDays > 0 && currentProjects.TryGetValue(p.Key, out var current) && current.TimeLeftDays == 0))
                    projectChanges.Add(new JObject { ["name"] = entry.Value.Name, ["change"] = "time_left_zero", ["timeLeftDays"] = 0, ["completionVerified"] = false });
                if (projectChanges.OfType<JObject>().Any(p => (string?)p["change"] == "time_left_zero")) { pendingWake ??= "project_due"; if (!reasons.Contains("project_due")) reasons.Add("project_due"); }
                if (waitedProjectId is int projectId && waitedProject != null)
                {
                    var present = currentProjects.TryGetValue(projectId, out var current);
                    waitedProject["stillListed"] = present;
                    waitedProject["status"] = present ? current!.Status : "No longer listed";
                    waitedProject["timeLeftDays"] = present && current!.TimeLeftDays.HasValue ? new JValue(current.TimeLeftDays.Value) : JValue.CreateNull();
                    readyForRelease = !present && uniqueProjectName && ResearchGameApi.ReadyReleaseName() == (string?)waitedProject["name"];
                    if (readyForRelease)
                    {
                        waitedProject["status"] = "Ready for release";
                        foreach (var change in projectChanges.OfType<JObject>().Where(p => (string?)p["name"] == (string?)waitedProject["name"])) { change["completionVerified"] = true; change["evidence"] = "matching_native_release_form"; }
                        pendingWake ??= "project_ready";
                        if (!reasons.Contains("project_ready")) reasons.Add("project_ready");
                    }
                }
            }
            var inDefault = (bool?)BalanceSnapshot.Default()["active"] == true;
            if (inDefault && !startedInDefault) { pendingWake ??= "default_started"; if (!reasons.Contains("default_started")) reasons.Add("default_started"); }
            startedInDefault = inDefault;
            if (creditBelow != null && CurrentCredit() < creditBelow) { pendingWake ??= "credit_below"; if (!reasons.Contains("credit_below")) reasons.Add("credit_below"); }
            if (pendingWake != null) { var reason = pendingWake; pendingWake = null; Complete(reason); }
            else { if (MultiplayerInterop.CanControlTime) Invoke(dateType, dateController!, "ManualSetTimeSpeed", requestedSpeed, false); completingAtEndOfFrame = false; }
        }
        catch (Exception exception) { Fail(exception.InnerException?.Message ?? exception.Message); }
    }

    private void Complete(string reason)
    {
        if (!active) return;
        try { SafePauseInstance(); }
        catch (Exception exception) { error ??= exception.InnerException?.Message ?? exception.Message; }
        if (!reasons.Contains(reason)) reasons.Add(reason);
        try { CaptureDialogs(); } catch { dialogs = new JArray(); }
        AddDialogReasons();
        if (dialogs.Count > 0 && !OnlyForms() && !reasons.Contains("popup")) reasons.Add("popup");
        try { stoppedDate = Alive(dateController) ? CurrentDate() : startDate; }
        catch { stoppedDate = startDate; }
        active = false;
        complete = true;
        Unsubscribe();
    }

    private void Fail(string message)
    {
        error = message;
        try { SafePauseInstance(); } catch { }
        if (!reasons.Contains("controller_error")) reasons.Add("controller_error");
        try { CaptureDialogs(); } catch { dialogs = new JArray(); }
        try { stoppedDate = Alive(dateController) ? CurrentDate() : startDate; } catch { stoppedDate = startDate; }
        active = false;
        complete = true;
        Unsubscribe();
    }

    private void SafePauseInstance()
    {
        if (!Alive(dateController) || !MultiplayerInterop.CanControlTime) return;
        Invoke(dateType, dateController!, "ManualSetTimeSpeed", 0, false);
        Invoke(dateType, dateController!, "ManualPause", false);
    }

    private bool OnlyForms() => dialogs.Count > 0 && dialogs.OfType<JObject>().All(d => (string?)d["kind"] == "form");

    // "form_open" when only open forms (e.g. the CPU designer) hold time; "popup" for native popups/dialogs.
    private string BlockReason() => OnlyForms() ? "form_open" : "popup";

    private void AddDialogReasons()
    {
        if (dialogs.OfType<JObject>().Any(d => (string?)d["kind"] == "form") && !reasons.Contains("form_open")) reasons.Add("form_open");
        foreach (var name in dialogs.OfType<JObject>().Where(d => (string?)d["kind"] != "form").Select(d => ((string?)d["name"] ?? (string?)d["scope"] ?? "").ToLowerInvariant()))
        {
            if (name.Contains("research") && name.Contains("complete") && !reasons.Contains("research_completed")) reasons.Add("research_completed");
            if ((name.Contains("project") || name.Contains("release")) && !reasons.Contains("project_completed")) reasons.Add("project_completed");
        }
    }

    private bool ActivePauseTrigger()
    {
        var property = pauseTriggerType.GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingMemberException(PauseTriggerTypeName, "IsActive");
        foreach (var item in Resources.FindObjectsOfTypeAll(pauseTriggerType)) if (item != null && (bool)property.GetValue(item)!) return true;
        return false;
    }

    // Window/menu names currently holding an active native PauseTrigger: the game's own time-blocking popups
    // (completion popups, the CPU release form and similar). Used by dialog-read/window-list; empty if unavailable.
    internal static HashSet<string> PauseTriggerScopes()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var type = NativeType(PauseTriggerTypeName);
            var activeProperty = type.GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public);
            var childProperty = type.GetProperty("IsChild", BindingFlags.Instance | BindingFlags.Public);
            if (activeProperty == null) return result;
            var pauseMenuOpen = PauseMenuOpen();
            foreach (var item in Resources.FindObjectsOfTypeAll(type))
                if (item is Component component && component.gameObject.scene.IsValid() && (bool)activeProperty.GetValue(item)!) result.Add(PauseTriggerOwner(component, childProperty, pauseMenuOpen));
        }
        catch (Exception) { }
        return result;
    }

    // The object that brings the window of an active trigger to the front when clicked (native WindowHierarchyBehaviour, or the
    // window root), for a popup covered by windows raised after it.
    internal static GameObject? WindowRaiser(string scope)
    {
        try
        {
            var type = NativeType(PauseTriggerTypeName);
            var activeProperty = type.GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public);
            var childProperty = type.GetProperty("IsChild", BindingFlags.Instance | BindingFlags.Public);
            if (activeProperty == null) return null;
            foreach (var item in Resources.FindObjectsOfTypeAll(type))
            {
                if (item is not Component component || !component.gameObject.scene.IsValid() || !(bool)activeProperty.GetValue(item)! || PauseTriggerOwner(component, childProperty, false) != scope) continue;
                var window = component.transform;
                while (window != null && window.name != scope) window = window.parent;
                if (window == null) continue;
                var raiser = window.GetComponentsInChildren<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().FullName == "ProcessorTycoon.UI.WindowHierarchyBehaviour");
                return raiser != null ? raiser.gameObject : window.gameObject;
            }
        }
        catch (Exception) { }
        return null;
    }

    // Name of the window/menu holding a trigger. The Pause Menu's own trigger sits on the desktop bottom bar, outside
    // any named window: while that menu is open, such an unattributed trigger is reported as "PauseMenu", not as the bar.
    private static string PauseTriggerOwner(Component component, PropertyInfo? childProperty, bool pauseMenuOpen)
    {
        var start = (childProperty?.GetValue(component) is true ? component.transform.parent : component.transform) ?? component.transform;
        for (var cursor = start; cursor != null; cursor = cursor.parent)
            if (cursor.name.EndsWith("Window", StringComparison.Ordinal) || cursor.name.EndsWith("Menu", StringComparison.Ordinal)) return cursor.name;
        return pauseMenuOpen ? "PauseMenu" : start.name;
    }

    internal static bool PauseMenuOpen()
    {
        try
        {
            var type = NativeType(PauseMenuTypeName);
            var open = type.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
            return open != null && Resources.FindObjectsOfTypeAll(type).Any(item => item is Component c && c != null && c.gameObject.scene.IsValid() && (bool)open.GetValue(item)!);
        }
        catch (Exception) { return false; }
    }

    private JArray PauseTriggerDialogs()
    {
        var result = new JArray();
        var activeProperty = pauseTriggerType.GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingMemberException(PauseTriggerTypeName, "IsActive");
        var childProperty = pauseTriggerType.GetProperty("IsChild", BindingFlags.Instance | BindingFlags.Public);
        var pauseMenuType = NativeType(PauseMenuTypeName);
        var open = pauseMenuType.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingMemberException(PauseMenuTypeName, "IsOpen");
        var pauseMenuOpen = Resources.FindObjectsOfTypeAll(pauseMenuType).Any(item => item != null && (bool)open.GetValue(item)!);
        foreach (var item in Resources.FindObjectsOfTypeAll(pauseTriggerType))
        {
            if (item is not Component component || !(bool)activeProperty.GetValue(item)!) continue;
            var owner = PauseTriggerOwner(component, childProperty, pauseMenuOpen);
            if (owner == "PauseMenu" || result.OfType<JObject>().Any(d => (string?)d["scope"] == owner)) continue;
            // An open form such as the CPU designer also pauses time, but it is not a popup and has no dialog choice.
            if (SessionGameApi.FormScopes.Contains(owner)) result.Add(new JObject { ["name"] = SessionGameApi.WindowName(owner), ["scope"] = owner, ["kind"] = "form", ["source"] = "native_pause_trigger", ["note"] = SessionGameApi.TimeBlockingFormNote(owner) });
            else result.Add(new JObject { ["name"] = Human(owner), ["scope"] = owner, ["source"] = "native_pause_trigger" });
        }
        if (pauseMenuOpen) result.Add(new JObject { ["name"] = "Pause Menu", ["scope"] = "PauseMenu", ["source"] = "native_pause_menu" });
        return result;
    }

    private DateTime CurrentDate() => (DateTime)(dateType.GetProperty("CurrentDate", BindingFlags.Instance | BindingFlags.Public)?.GetValue(dateController) ?? throw new MissingMemberException(DateTypeName, "CurrentDate"));

    private string SelectedSpeed() => !Alive(dateController) ? "unavailable" : SelectedSpeed(dateType, dateController!);

    private static string SelectedSpeed(Type dateType, object dateController)
    {
        try { return NormalizeSpeed(SelectedButtonName(dateType, dateController)); }
        catch { return "unknown"; }
    }

    private static string SelectedButtonName(Type dateType, object dateController)
    {
        var uniqueId = (int)(dateType.GetProperty("UniqueID", BindingFlags.Instance | BindingFlags.Public)?.GetValue(dateController) ?? throw new MissingMemberException(DateTypeName, "UniqueID"));
        var buttonsType = NativeType(ButtonsTypeName);
        var handler = Instance(buttonsType);
        var method = buttonsType.GetMethod("CurrentButtonWithID", BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingMethodException(ButtonsTypeName, "CurrentButtonWithID");
        var button = method.Invoke(handler, new object[] { uniqueId }) as Component ?? throw new AgentError("game_ui_mismatch", "Native current speed button is unavailable.");
        return button.gameObject.name;
    }

    private static string NormalizeSpeed(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (key.Contains("pause")) return "pause";
        if (key.Contains("speed1")) return "speed-1";
        if (key.Contains("speed2")) return "speed-2";
        if (key.Contains("speed3")) return "speed-3";
        return "unknown";
    }

    private JArray ReadDialogs()
    {
        if (visibleDialogs == null) return new JArray();
        return visibleDialogs.Invoke() ?? new JArray();
    }

    private JArray ReadDialogsSafe()
    {
        try { return ReadDialogs(); }
        catch { return new JArray(); }
    }

    private void CaptureDialogs()
    {
        var items = PauseTriggerDialogs().OfType<JObject>().Concat(ReadDialogsSafe().OfType<JObject>()).GroupBy(d => (string?)d["scope"] ?? (string?)d["name"] ?? "").Select(g => g.First());
        dialogs = new JArray(items);
    }

    private void Unsubscribe()
    {
        if (dayHandler != null && Alive(dateController)) try { dayEvent.RemoveEventHandler(dateController, dayHandler); } catch { }
        dayHandler = null;
    }

    private void EnsureInitialized()
    {
        if (dateType != null) return;
        Initialize(agentPaused, visibleDialogs);
    }

    private static bool PlayerInput() => Application.isFocused && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2) || Input.anyKeyDown || Input.mouseScrollDelta.sqrMagnitude > 0);
    private static bool Alive(object? value) => value is UnityEngine.Object unityObject ? unityObject != null : value != null;
    private static string Iso(DateTime value) => value == default ? "" : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static string Human(string value) => string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]) ? " " + c : c.ToString())).Trim();

    private static Type NativeType(string fullName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);
        return type ?? throw new AgentError("game_ui_mismatch", $"Native type {fullName} is unavailable in this build.");
    }

    private static object Instance(Type type) => type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) ?? throw new AgentError("not_available", $"Native {type.Name} is not active in the current scene.");

    private static object? Invoke(Type type, object instance, string method, params object[] arguments)
    {
        var parameterTypes = arguments.Select(a => a.GetType()).ToArray();
        var target = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null) ?? throw new MissingMethodException(type.FullName, method);
        return target.Invoke(instance, arguments);
    }
}
