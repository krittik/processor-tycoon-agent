using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("Processor Tycoon Beta.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "local.processortycoon.mod";
    public const string PluginName = "Processor Tycoon Agent";
    public const string PluginVersion = AgentInfo.Version;
    private readonly ConcurrentQueue<Request> requests = new();
    private readonly Queue<(Request request, Operation operation)> pending = new();
    private readonly Dictionary<string, Operation> operations = new();
    private readonly List<(Request request, DateTime deadline)> waiters = new();
    private readonly GenericUi ui = new();
    private readonly InteractionJournal journal = new();
    private readonly GameNotifications notifications = new();
    private readonly GameWindowLifecycle windows = new();
    private LocalApi? api;
    private AgentOverlay? overlay;
    private TimeAdvanceController? timeAdvance;
    private string? timeAdvanceOperationId;
    private bool timeAdvancePreparing;
    private bool timeAdvanceCancellationRequested;
    private ConfigEntry<int> delay = null!;
    private ConfigEntry<bool> showFeed = null!;
    private bool paused;
    private bool busy;
    private bool oldRunInBackground;
    private float lastAction;
    private DateTime lastActivity = DateTime.MinValue;
    private string session = "";
    private string? startupError;
    private string? temporaryScreenshot;
    internal bool Paused => paused;
    internal int Delay => delay.Value;
    internal bool ShowFeed => showFeed.Value;
    internal string Connection => startupError != null ? "Bridge error" : lastActivity == DateTime.MinValue ? "Waiting for agent" : DateTime.UtcNow - lastActivity < TimeSpan.FromMinutes(5) ? paused ? "Agent Paused" : "Agent Connected" : "Agent Inactive";

    private void Awake()
    {
        ui.VisualAction = (point, click) => overlay?.ShowAction(point, click);
        delay = Config.Bind("Agent", "ActionDelayMs", 0, new ConfigDescription("Extra delay between UI actions, not model response time.", new AcceptableValueRange<int>(0, 60000)));
        showFeed = Config.Bind("Agent", "ShowFeed", true, "Show the unobtrusive action feed.");
        var port = Config.Bind("Api", "Port", 17616, "Loopback-only HTTP port. Change for another game instance.");
        oldRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        notifications.Initialize();
        try
        {
            api = new LocalApi(port.Value, Paths.GameRootPath, requests);
            Logger.LogInfo($"{PluginName} {PluginVersion}; generic UI API: {api.Endpoint}");
        }
        catch (Exception error) { startupError = error.Message; Logger.LogError(error); }
    }

    private void Update()
    {
        journal.SamplePlayerInput();
        windows.PlayerInput(journal.PlayerInputCursor);
        if (overlay == null) overlay = AgentOverlay.TryCreate(this);
        if (Input.GetKeyDown(KeyCode.F8)) overlay?.TogglePanel();
        notifications.PollChat();
        for (var count = 0; count < 16 && requests.TryDequeue(out var request); count++)
        {
            lastActivity = DateTime.UtcNow;
            session = request.Session;
            try
            {
                if (request.Command == "wait-until-resumed" && paused)
                {
                    var seconds = Math.Max(1, Math.Min(60, request.Value?.Value<int>() ?? 60));
                    waiters.Add((request, DateTime.UtcNow.AddSeconds(seconds)));
                }
                else request.Completion.TrySetResult(notifications.Wrap(request, Handle(request)));
            }
            catch (AgentError error) { Feed(error.Message, true, "error"); request.Completion.TrySetResult(notifications.Wrap(request, Wire.Error(error.Code, error.Message))); }
            catch (Exception error) { Logger.LogError(error); request.Completion.TrySetResult(notifications.Wrap(request, Wire.Error("command_error", error.Message))); }
        }
        for (var index = waiters.Count - 1; index >= 0; index--)
        {
            lastActivity = DateTime.UtcNow;
            if (paused && waiters[index].deadline > DateTime.UtcNow) continue;
            waiters[index].request.Completion.TrySetResult(notifications.Wrap(waiters[index].request, new { ok = true, state = paused ? "paused_by_user" : "resumed", next = paused ? "Wait again; do not issue game actions or resume yourself." : "Re-observe before acting." }));
            waiters.RemoveAt(index);
        }
        if (!paused && !busy && pending.Count > 0 && Time.realtimeSinceStartup - lastAction >= delay.Value / 1000f)
        {
            var next = pending.Dequeue();
            StartCoroutine(next.request.Command is "game.time-advance" or "game.projects-wait" ? ExecuteAdvance(next.request, next.operation) : next.request.Command.StartsWith("game.", StringComparison.Ordinal) ? ExecuteGame(next.request, next.operation) : Execute(next.request, next.operation));
        }
    }

    private void LateUpdate() => overlay?.Refresh();

    private IGameModule[] GameModules() => new IGameModule[] { new CpuGameApi(ui, _ => { }), new CpuReviewGameApi(ui), new ResearchGameApi(new GameUi(ui)), new ProductionGameApi(ui), new SessionGameApi(new GameUi(ui)), new ContractsGameApi(new GameUi(ui)), new MarketGameApi(ui, _ => { }), new UtilityGameApi(ui, _ => { }), new HardwareGameApi(new GameUi(ui)) };
    private IGameModule GameModule(string command) => GameModules().SingleOrDefault(m => m.Commands.Contains(command)) ?? throw new AgentError("unknown_command", "Use capabilities or guide for supported game commands.");

    private object Handle(Request request)
    {
        switch (request.Command)
        {
            case "notifications": return new { ok = true, notifications = notifications.Read(request.Since) };
            case "status": return new { ok = true, version = PluginVersion, release = AgentInfo.Release, repository = AgentInfo.Repository, apiVersion = 1, instance = api?.Instance, gameDirectory = Paths.GameRootPath, scene = ui.Scene, game = GameSessionState.Read(ui), state = Connection, paused, actionDelayMs = delay.Value, session, queued = pending.Count, busy, timeAdvanceOperationId, startupError, activity = journal.Status, multiplayer = MultiplayerInterop.Status() };
            case "events": return new { ok = true, data = journal.Since(request.Since) };
            case "guide":
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentGuide"))
                using (var reader = new StreamReader(stream!)) return new { ok = true, guide = reader.ReadToEnd() };
            case "capabilities": return new
            {
                ok = true, defaultMethod = "game", reason = "Use semantic Game API for supported ordinary gameplay. Generic UI is detailed discovery/fallback for unmapped or modified content. Game actions preserve native rules and expose player-browsable UI data.",
                localCliCommands = new[] { "help", "guide", "prompt", "status", "launch" },
                notificationReplay = "notifications --since ID; replies also include new native popup messages automatically. IDs belong to this game process instance.",
                commands = new[] { "status", "guide", "capabilities", "observe", "events", "notifications", "screenshot", "game.cpu-schema", "game.cpu-read", "game.time-advance", "game.time-cancel", "game.projects-wait", "ui.inspect", "ui.click", "ui.set", "ui.select", "ui.scroll", "ui.hover", "ui.focus", "ui.drag", "operation", "agent.pause", "agent.resume", "agent.delay", "agent.panel", "agent.say", "wait-until-resumed", "mp.status", "mp.host", "mp.join", "mp.resume", "mp.leave", "mp.chat", "mp.chat-read" }.Concat(GameModules().SelectMany(m => m.Commands)).ToArray(),
                gameApi = new { cpu = CpuGameApi.Schema, cpuReview = CpuReviewGameApi.Schema, contracts = ContractsGameApi.Schema, market = MarketGameApi.Schema, utility = UtilityGameApi.Schema }, hidden = new[] { "observe", "ui.inspect", "game.cpu-read" },
                windowPolicy = "Keep current workspace; close prior agent-opened workspaces on a domain change. Preserve player windows, explicit window-open pins, unfinished forms and dialogs.",
                coverage = "106 Game commands cover inventoried ordinary workflows in English 0.2.16a5. Representative native lifecycles and independent gameplay were tested; not every combination or future version. Native-disabled/dormant features are not bypassed. Generic remains discovery/fallback.",
                explicitUserRequestOnly = new[] { "agent.resume", "agent.delay" }
            };
            case "operation":
                if (!operations.TryGetValue(request.Target, out var operation)) throw new AgentError("unknown_operation", "Unknown/expired operation; do not blindly replay a mutation.");
                return new { ok = true, operation };
            case "agent.pause": SetPaused(true); return new { ok = true, state = "paused_by_user", next = "Wait until resumed; do not retry game actions." };
            case "agent.resume": RequireUserRequest(request); SetPaused(false); return new { ok = true, state = "resumed", next = "Re-observe before acting." };
            case "agent.delay":
                RequireUserRequest(request);
                if (!int.TryParse(request.Value?.ToString(), out var milliseconds) || milliseconds < 0 || milliseconds > 60000) throw new AgentError("invalid_value", "Delay must be an integer between 0 and 60000 ms.");
                SetDelay(milliseconds); return new { ok = true, actionDelayMs = delay.Value };
            case "agent.say": Feed(request.Value?.ToString() ?? "", false, "message"); return new { ok = true };
            case "agent.panel":
                if (overlay != null && request.Value?.ToString() == "about") { overlay.SetAbout(true); return new { ok = true, visible = true, window = "about" }; }
                if (!bool.TryParse(request.Value?.ToString(), out var panelVisible)) throw new AgentError("invalid_value", "Panel value must be true, false or about.");
                if (overlay == null) throw new AgentError("ui_not_ready", "Agent overlay is not ready yet.");
                overlay.SetPanel(panelVisible); return new { ok = true, visible = panelVisible };
            case "wait-until-resumed": return new { ok = true, state = "resumed", next = "Re-observe before acting." };
            case "mp.status": return new { ok = true, data = MultiplayerInterop.Require() };
            case "mp.host":
                MultiplayerInterop.Call("Host", request.Target, request.Value?.ToString() ?? "");
                return new { ok = true, data = MultiplayerInterop.Require(), next = "Hosting starts from the current game; other players join with mp join ADDRESS. Read mp status." };
            case "mp.join":
                if (request.Target.Length == 0) throw new AgentError("invalid_request", "mp join requires the host address (ip:port).");
                // With --company / --company-type (and in a game without a window) the company is set up at once.
                if (request.Parameters?["company"] != null || request.Parameters?["companyType"] != null)
                    MultiplayerInterop.Call("JoinAs", request.Target, request.Value?.ToString() ?? "", request.Parameters?["company"]?.ToString() ?? "", request.Parameters?["companyType"]?.Value<int>() ?? 0);
                else MultiplayerInterop.Call("Join", request.Target, request.Value?.ToString() ?? "");
                return new { ok = true, data = MultiplayerInterop.Require(), next = "Joining loads the shared world (a few seconds). Poll mp status until state is Running." };
            case "mp.resume":
                MultiplayerInterop.Call("Resume", request.Target, "", request.Value?.ToString() ?? "");
                return new { ok = true, data = MultiplayerInterop.Require(), next = "The saved session loads and is hosted from this machine; other players rejoin with mp join." };
            case "mp.leave": MultiplayerInterop.Call("Leave"); return new { ok = true, data = MultiplayerInterop.Require() };
            case "mp.chat":
                var chatText = request.Target.Length > 0 ? request.Target : request.Value?.ToString() ?? "";
                if (chatText.Length == 0) throw new AgentError("invalid_request", "mp chat requires the message text.");
                var chatBefore = MultiplayerInterop.ChatLast;
                MultiplayerInterop.Call("SendChat", chatText);
                // The Multiplayer mod answers an unknown @name with a note instead of sending.
                var note = MultiplayerInterop.ChatSince(chatBefore).FirstOrDefault(l => (string?)l["from"] == "");
                if (note != null) throw new AgentError("chat_not_sent", (string?)note["text"] ?? "The message was not sent.");
                return new { ok = true };
            case "mp.chat-read":
                MultiplayerInterop.Require();
                // The conversation, own lines included (new messages from others also arrive in every reply's notifications).
                var chatLines = MultiplayerInterop.ChatSince((int)request.Since);
                var shownLines = new JArray(chatLines.Skip(Math.Max(0, chatLines.Count - request.Limit)));
                return new { ok = true, data = new { lines = shownLines, last = MultiplayerInterop.ChatLast, omitted = chatLines.Count - shownLines.Count }, next = "Reply with mp chat TEXT (everyone) or mp chat \"@Name TEXT\" (one player, arrives as their email). Lines with from \"\" are notes from the Multiplayer mod." };
            case "game.cpu-schema": return new { ok = true, data = CpuGameApi.Schema };
            case "game.time-cancel":
                if (request.Target.Length > 0 || request.Value != null || request.Parameters?.Count > 0) throw new AgentError("invalid_request", "time-cancel takes no arguments.");
                var advancing = timeAdvanceOperationId != null && (timeAdvance == null || !timeAdvance.IsComplete);
                if (advancing) timeAdvanceCancellationRequested = true;
                timeAdvance?.Cancel("cancelled");
                return new { ok = true, cancellationRequested = advancing, next = "Read the original time-advance operation for its final stopped date." };
        }
        GameSessionState.RequireCampaign(ui, request.Command);
        if (paused) throw new AgentError("paused_by_user", "Stop game commands. Use wait-until-resumed; do not resume without an explicit user request. Re-observe after resume.");
        if (request.Command == "game.time-read" && timeAdvanceOperationId != null)
        {
            GameModule(request.Command).Validate(request);
            var clock = TimeAdvanceController.ReadClock();
            clock["method"] = "game";
            clock["activeOperationId"] = timeAdvanceOperationId;
            clock["advancing"] = !timeAdvancePreparing;
            clock["preparing"] = timeAdvancePreparing;
            clock["pauseMayBeDayBoundaryGate"] = !timeAdvancePreparing;
            clock["next"] = "Query the active operation or use game time-cancel. This clock read did not queue or alter the advance.";
            return new { ok = true, data = clock };
        }
        if (request.Command == "game.cpu-read")
        {
            CpuGameApi.Validate(request);
            return new { ok = true, data = new CpuGameApi(ui, message => Feed(message, true, "read")).Preview() };
        }
        if (request.Command == "observe")
        {
            var data = ui.Observe(request.Scope, request.Offset, request.Limit);
            Feed("Read · " + (request.Scope.Length > 0 ? request.Scope.Split('/').Last() : "Screen"), true, request.Scope.Length > 0 ? "search" : "read");
            return new { ok = true, data, updates = journal.Observe(request, data) };
        }
        if (request.Command == "ui.inspect")
        {
            var data = ui.Inspect(request.Target);
            Feed(ui.Node(request.Target).label, true, "read");
            return new { ok = true, data };
        }
        if (request.Command == "screenshot")
        {
            var capture = new Operation { command = request.Command };
            operations[capture.id] = capture;
            TrimOperations(capture.id);
            StartCoroutine(Capture(capture));
            return new { ok = true, operation = capture };
        }
        var gameCommand = request.Command.StartsWith("game.", StringComparison.Ordinal);
        if (!gameCommand && !new[] { "ui.click", "ui.set", "ui.select", "ui.scroll", "ui.hover", "ui.focus", "ui.drag" }.Contains(request.Command)) throw new AgentError("unknown_command", "Use capabilities or guide for supported commands.");
        if (request.Hidden) throw new AgentError("hidden_not_supported", "UI navigation and mutations are visible. No action was executed. Hidden currently supports non-navigating observations only.");
        if (request.Command is "game.time-advance" or "game.projects-wait") TimeAdvanceController.Validate(request);
        else if (gameCommand) GameModule(request.Command).Validate(request);
        else ui.Validate(request);
        if (timeAdvanceOperationId != null) throw new AgentError("operation_in_progress", $"Time advance {timeAdvanceOperationId} is active. Query operation {timeAdvanceOperationId}, read time-read, or use time-cancel. This request was not queued.");
        var created = new Operation { command = request.Command };
        operations[created.id] = created;
        pending.Enqueue((request, created));
        TrimOperations(created.id);
        return new { ok = true, operation = created };
    }

    private void TrimOperations(string retainedId)
    {
        // Dictionary slot reuse is not chronological. Never evict the result we are about to return.
        foreach (var id in operations.Where(p => p.Key != retainedId && (p.Value.state is "completed" or "failed" or "partial_failure" or "interrupted")).OrderBy(p => p.Value.sequence).Take(Math.Max(0, operations.Count - 256)).Select(p => p.Key).ToArray()) operations.Remove(id);
    }

    private IEnumerator Execute(Request request, Operation operation, bool workflowStep = false)
    {
        busy = true;
        operation.state = "running";
        object? before = null;
        string label = request.Target;
        var inputPrepared = false;
        UiNode? beforeNode = null;
        int? dispatchFrame = null;
        try { label = ui.Validate(request); before = ui.Inspect(request.Target); beforeNode = ui.Node(request.Target); inputPrepared = ui.PrepareInput(request); }
        catch (Exception error) { Fail(operation, error, false); }
        if (inputPrepared)
        {
            yield return null; // TMP activates editing during its next update; end-edit validation needs that lifecycle.
            if (paused)
            {
                operation.state = "interrupted";
                operation.error = new { code = "paused_by_user", message = "Input was focused but not changed. Re-observe after resume." };
            }
        }
        if (operation.state == "running" && inputPrepared)
        {
            try { ui.VerifyPreparedInput(request.Target); }
            catch (Exception error) { Fail(operation, error, false); }
        }
        if (operation.state == "running")
        {
            try
            {
                ui.Act(request);
                dispatchFrame = Time.frameCount;
            }
            catch (Exception error) { Fail(operation, error, true); }
        }
        if (operation.state == "running")
        {
            var signature = ui.RegionSignature(request.Target);
            var stableFrames = 0;
            var deadline = Time.realtimeSinceStartup + 1.5f;
            do
            {
                yield return null;
                var next = ui.RegionSignature(request.Target);
                stableFrames = next == signature ? stableFrames + 1 : 0;
                signature = next;
            } while (stableFrames < 3 && Time.realtimeSinceStartup < deadline);
            try
            {
                var after = ui.After(request.Target);
                object? observation = null;
                object? updates = null;
                if (request.ObserveAfter != null)
                {
                    var read = new Request { Session = request.Session, Scope = request.ObserveAfter == "screen" ? "" : request.ObserveAfter, Limit = request.Limit, Changes = request.Changes };
                    observation = ui.Observe(read.Scope, read.Offset, read.Limit);
                    updates = journal.Observe(read, observation);
                }
                operation.result = new { before, after, dispatchFrame, uiSettled = stableFrames >= 3, observation, updates, next = "Input dispatched. Use refreshed handles; UI settling is not business success." };
                operation.state = "completed";
                var changed = JObject.FromObject(after)["control"];
                var previous = beforeNode?.displayValue ?? beforeNode?.value?.ToString();
                var current = changed?["displayValue"]?.Value<string>() ?? (changed?["value"]?.Type == JTokenType.Null ? null : changed?["value"]?.ToString());
                var edited = request.Command is "ui.set" or "ui.select";
                var icon = edited ? "edit" : request.Command == "ui.hover" ? "search" : label.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0 ? "close" : request.Command is "ui.focus" or "ui.drag" ? "open" : "click";
                var contextLabel = beforeNode != null && beforeNode.window.Length > 0 && beforeNode.window != label ? $"{beforeNode.window} > {label}" : label;
                if (!workflowStep) Feed(edited && previous != null && current != null ? $"{contextLabel}: {previous} -> {current}" : contextLabel, false, icon);
                journal.Add("agent", request.Command, new { operationId = operation.id, session = request.Session, target = contextLabel, before = previous, after = current });
            }
            catch (Exception error) { Fail(operation, error, true); }
        }
        lastAction = Time.realtimeSinceStartup;
        if (!workflowStep) busy = false;
    }

    private sealed class WorkflowProgress
    {
        public readonly List<object> Steps = new();
        public bool Completed;
        public bool Cancelled;
        public bool Dispatched;
        public string StepName = "prepare";
    }

    private IEnumerator RunWorkflowSteps(IEnumerator<Request> iterator, Request request, Operation operation, long inputCursor, WorkflowProgress progress, Func<bool>? cancelled = null, bool allowWhenAgentPaused = false, bool skipDelay = false)
    {
        try
        {
            while (operation.state == "running")
            {
                if (cancelled?.Invoke() == true) { progress.Cancelled = true; break; }
                Request? step = null;
                try
                {
                    if (paused && !allowWhenAgentPaused) throw new AgentError("paused_by_user", "Game command interrupted. Re-observe after user resumes; no remaining steps will be replayed.");
                    if (journal.PlayerInputCursor != inputCursor) throw new AgentError("player_input_conflict", "Local player input occurred during the command. Remaining steps stopped; inspect the current state before retrying.");
                    if (!iterator.MoveNext()) { progress.Completed = true; break; }
                    step = iterator.Current;
                    step.Session = request.Session;
                    progress.StepName = step.NativeAction != null ? step.Target : ui.Node(step.Target).label;
                }
                catch (Exception error) { Fail(operation, error, progress.Dispatched); }
                if (step == null || operation.state != "running") break;
                while (!skipDelay && (!paused || allowWhenAgentPaused) && cancelled?.Invoke() != true && Time.realtimeSinceStartup - lastAction < delay.Value / 1000f) yield return null;
                if (cancelled?.Invoke() == true) { progress.Cancelled = true; break; }
                if ((paused && !allowWhenAgentPaused) || journal.PlayerInputCursor != inputCursor) continue;
                var child = new Operation { command = step.Command };
                operations[child.id] = child;
                if (step.NativeAction == null) yield return Execute(step, child, true);
                else
                {
                    child.state = "running";
                    try
                    {
                        Vector2? visualPoint = !step.NativeVisualHandled && step.NativeVisualTarget != null && ui.TryVisualPoint(step.NativeVisualTarget, out var point) ? point : null;
                        step.NativeAction();
                        if (!step.NativeVisualHandled) overlay?.ShowAction(visualPoint, step.NativeVisualClick);
                        child.result = new { dispatchFrame = Time.frameCount, target = step.Target, next = "Native adapter dispatched; module must verify the gameplay postcondition." };
                        child.state = "completed";
                        journal.Add("agent", step.Command, new { operationId = child.id, session = request.Session, target = step.Target });
                    }
                    catch (Exception error) { Fail(child, error, true); }
                    lastAction = Time.realtimeSinceStartup;
                    for (var frame = 0; frame < step.SettleFrames; frame++) yield return null;
                }
                progress.Steps.Add(new { action = step.Command, target = progress.StepName, state = child.state });
                progress.Dispatched |= child.state is "completed" or "partial_failure" || JObject.FromObject(child.result ?? new { })["dispatchFrame"] != null;
                if (child.state != "completed") { operation.state = progress.Dispatched ? "partial_failure" : child.state; operation.error = child.error; break; }
                yield return null;
            }
        }
        finally { iterator.Dispose(); }
    }

    private JObject CancelledBeforeAdvance(Request request, string startText)
    {
        var clock = TimeAdvanceController.ReadClock();
        var start = DateTime.ParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var target = TimeAdvanceController.Deadline(request, start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new JObject
        {
            ["method"] = "game", ["outcome"] = "cancelled", ["startDate"] = startText, ["targetDate"] = target, ["stoppedDate"] = startText,
            ["daysAdvanced"] = 0, ["requestedSpeed"] = request.Parameters?["speed"]?.Value<int>() ?? 3, ["selectedSpeed"] = clock["selectedSpeed"]!.DeepClone(),
            ["paused"] = clock["paused"]!.DeepClone(), ["wakeReasons"] = new JArray("cancelled_before_start"), ["dialogs"] = new JArray(), ["projectChanges"] = new JArray(),
            ["error"] = (bool)clock["paused"]! || !MultiplayerInterop.CanControlTime ? JValue.CreateNull() : new JValue("The native pause postcondition was not confirmed after cancellation."),
            ["next"] = (bool)clock["paused"]! ? "The advance was cancelled during workspace cleanup before simulation started. Time is left paused. Inspect the current workspace before continuing." : "The advance was cancelled before simulation started, but the native pause postcondition was not confirmed. Read time state and explicitly pause before continuing."
        };
    }

    private IEnumerator ExecuteAdvance(Request request, Operation operation)
    {
        var execution = ExecuteAdvanceSteps(request, operation);
        try
        {
            while (true)
            {
                bool next;
                try { next = execution.MoveNext(); }
                catch (Exception error) { Fail(operation, error, true); next = false; }
                if (!next) break;
                yield return execution.Current;
            }
        }
        finally
        {
            (execution as IDisposable)?.Dispose();
            if (timeAdvance != null) { timeAdvance.Dispose(); Destroy(timeAdvance); timeAdvance = null; }
            timeAdvanceOperationId = null;
            timeAdvancePreparing = false;
            timeAdvanceCancellationRequested = false;
            lastAction = Time.realtimeSinceStartup;
            busy = false;
        }
    }

    private IEnumerator ExecuteAdvanceSteps(Request request, Operation operation)
    {
        busy = true;
        operation.state = "running";
        var started = Time.realtimeSinceStartup;
        var inputCursor = journal.PlayerInputCursor;
        var workspaceFinished = false;
        GameWindowLifecycle.BeginState? workspace = null;
        WorkflowProgress? preparation = null;
        string? requestedStartDate = null;
        timeAdvanceOperationId = operation.id;
        timeAdvancePreparing = true;
        timeAdvanceCancellationRequested = false;
        Feed("/" + request.Command.Substring(5), false, "click");
        try
        {
            GameSessionState.RequireCampaign(ui, request.Command);
            workspace = windows.Begin(request, ui);
        }
        catch (Exception error) { Fail(operation, error, false); }
        try
        {
            if (operation.state == "running" && workspace != null)
            {
                try { requestedStartDate = (string?)TimeAdvanceController.ReadClock()["date"] ?? throw new AgentError("game_ui_mismatch", "Native current date was unavailable before time advance preparation."); }
                catch (Exception error) { Fail(operation, error, workspace.Closed.Length > 0); }
            }
            if (operation.state == "running")
            {
                var initialPause = GameUi.NativeStep("Pause before preparing time advance", TimeAdvanceController.SafePause);
                initialPause.SettleFrames = 3;
                preparation = new WorkflowProgress { Dispatched = workspace != null && workspace.Closed.Length > 0 };
                yield return RunWorkflowSteps(new[] { initialPause }.AsEnumerable().GetEnumerator(), request, operation, inputCursor, preparation, skipDelay: true);
                if (operation.state == "running")
                {
                    try { VerifyAdvanceStartClock(requestedStartDate!); }
                    catch (Exception error) { Fail(operation, error, preparation.Dispatched); }
                }
            }
            if (operation.state == "running" && workspace != null && !timeAdvanceCancellationRequested)
            {
                IEnumerable<Request> PrepareCleanup()
                {
                    foreach (var step in windows.CleanupParent(workspace, ui)) yield return step;
                    var closedParent = windows.CompleteParentCleanup(workspace);
                    if (closedParent != null)
                    {
                        workspace.Closed = workspace.Closed.Concat(new[] { closedParent }).ToArray();
                        journal.Add("agent", "workspace_cleanup", new { operationId = operation.id, window = closedParent, clockPaused = true });
                    }
                }
                preparation ??= new WorkflowProgress { Dispatched = workspace.Closed.Length > 0 };
                yield return RunWorkflowSteps(PrepareCleanup().GetEnumerator(), request, operation, inputCursor, preparation, () => timeAdvanceCancellationRequested);
            }
            if (workspace != null)
            {
                windows.Finish(request, workspace, journal.PlayerInputCursor != inputCursor);
                workspaceFinished = true;
            }
            if (operation.state == "running" && timeAdvanceCancellationRequested)
            {
                var pause = GameUi.NativeStep("Pause cancelled time advance", TimeAdvanceController.SafePause);
                pause.SettleFrames = 3;
                preparation ??= new WorkflowProgress { Dispatched = workspace != null && workspace.Closed.Length > 0 };
                yield return RunWorkflowSteps(new[] { pause }.AsEnumerable().GetEnumerator(), request, operation, journal.PlayerInputCursor, preparation, allowWhenAgentPaused: true, skipDelay: true);
                if (operation.state == "running") try
                {
                    VerifyAdvanceStartClock(requestedStartDate!);
                    var data = CancelledBeforeAdvance(request, requestedStartDate!);
                    if (workspace?.Closed.Length > 0) data["closedAgentWindows"] = new JArray(workspace.Closed);
                    operation.result = new { data, elapsedMs = (int)((Time.realtimeSinceStartup - started) * 1000) };
                    operation.state = (bool)data["paused"]! || !MultiplayerInterop.CanControlTime ? "completed" : "partial_failure";
                    if (operation.state == "partial_failure") operation.error = new { code = "time_advance_failed", message = (string?)data["error"] };
                    journal.Add("agent", request.Command, new { operationId = operation.id, session = request.Session, data });
                }
                catch (Exception error) { Fail(operation, error, preparation?.Dispatched == true); }
            }
            if (operation.state == "running")
            {
                try
                {
                    VerifyAdvanceStartClock(requestedStartDate!);
                    timeAdvance = gameObject.AddComponent<TimeAdvanceController>();
                    timeAdvance.Initialize(() => paused);
                    timeAdvancePreparing = false;
                    timeAdvance.Begin(request);
                }
                catch (Exception error) { Fail(operation, error, preparation?.Dispatched == true); }
            }
            while (operation.state == "running" && timeAdvance != null && !timeAdvance.IsComplete) yield return null;
            if (operation.state == "running")
            {
                try
                {
                    var data = timeAdvance!.Result();
                    if (workspace?.Closed.Length > 0) data["closedAgentWindows"] = new JArray(workspace.Closed);
                    // Optional: the already-open Production rows at the stopped date (watch-advance reads them without a second call).
                    if (request.Command == "game.time-advance" && request.Parameters?["includeProduction"]?.Type == JTokenType.Boolean && request.Parameters["includeProduction"]!.Value<bool>()) data["production"] = ProductionGameApi.OpenProductionSnapshot(ui);
                    operation.result = new { data, elapsedMs = (int)((Time.realtimeSinceStartup - started) * 1000) };
                    if ((string?)data["outcome"] == "failed")
                    {
                        operation.state = "partial_failure";
                        operation.error = new { code = "time_advance_failed", message = (string?)data["error"] };
                    }
                    else operation.state = "completed";
                    Feed($"/{request.Command.Substring(5)} · {data["stoppedDate"]} · {string.Join(", ", data["wakeReasons"]!.Values<string>())}", false, "pause");
                    journal.Add("agent", request.Command, new { operationId = operation.id, session = request.Session, data });
                }
                catch (Exception error) { Fail(operation, error, true); }
            }
            if (operation.state != "completed" && operation.result == null && preparation != null)
                operation.result = new { steps = preparation.Steps, stoppedAt = preparation.StepName, next = "Workspace cleanup did not complete. Inspect current windows and time state before retrying; the advance was not started." };
        }
        finally
        {
            if (!workspaceFinished && workspace != null) windows.Finish(request, workspace, journal.PlayerInputCursor != inputCursor);
        }
    }

    private static void VerifyAdvanceStartClock(string expectedDate)
    {
        // Multiplayer peer: the host drives the clock, so days may pass during preparation; the advance only waits.
        if (!MultiplayerInterop.CanControlTime) return;
        var clock = TimeAdvanceController.ReadClock();
        var actualDate = (string?)clock["date"];
        if ((bool?)clock["paused"] != true) throw new AgentError("game_ui_mismatch", "Native time did not remain paused during time advance preparation; the requested advance was not started.");
        if (actualDate != expectedDate) throw new AgentError("time_advance_start_drift", $"Native date changed during time advance preparation ({expectedDate} -> {actualDate ?? "unknown"}); the requested advance was not started.");
    }

    private IEnumerator ExecuteGame(Request request, Operation operation)
    {
        var execution = ExecuteGameSteps(request, operation);
        try
        {
            while (true)
            {
                bool next;
                try { next = execution.MoveNext(); }
                catch (Exception error) { Fail(operation, error, true); next = false; }
                if (!next) break;
                yield return execution.Current;
            }
        }
        finally { (execution as IDisposable)?.Dispose(); lastAction = Time.realtimeSinceStartup; busy = false; }
    }

    private IEnumerator ExecuteGameSteps(Request request, Operation operation)
    {
        busy = true;
        operation.state = "running";
        var started = Time.realtimeSinceStartup;
        var inputCursor = journal.PlayerInputCursor;
        GameSessionState.RequireCampaign(ui, request.Command);
        var workspace = windows.Begin(request, ui);
        // Cleanup is native input too: let close handlers and raycast layout settle before navigation.
        if (workspace.Closed.Length > 0) for (var frame = 0; frame < 3; frame++) yield return null;
        var game = GameModule(request.Command);
        IEnumerable<Request> PrepareWithCleanup()
        {
            foreach (var step in windows.CleanupParent(workspace, ui)) yield return step;
            var closedParent = windows.CompleteParentCleanup(workspace);
            if (closedParent != null)
            {
                workspace.Closed = workspace.Closed.Concat(new[] { closedParent }).ToArray();
                journal.Add("agent", "workspace_cleanup", new { operationId = operation.id, window = closedParent, clockPaused = true });
            }
            foreach (var step in game.Prepare(request)) yield return step;
        }
        var progress = new WorkflowProgress { Dispatched = workspace.Closed.Length > 0 };
        var commandName = "/" + request.Command.Substring(5);
        var feedIcon = GameFeedIcon(request.Command);
        var feedTarget = request.Target.Contains("-ui:") ? "" : request.Target;
        Feed(commandName + (feedTarget.Length > 0 ? " · " + feedTarget : ""), false, feedIcon);
        yield return RunWorkflowSteps(PrepareWithCleanup().GetEnumerator(), request, operation, inputCursor, progress);
        windows.Finish(request, workspace, journal.PlayerInputCursor != inputCursor);
        if (progress.Completed)
        {
            try
            {
                var data = game.Result(request);
                if (workspace.Closed.Length > 0) data["closedAgentWindows"] = new JArray(workspace.Closed);
                operation.result = new { data, steps = progress.Steps.Count, elapsedMs = (int)((Time.realtimeSinceStartup - started) * 1000) };
                operation.state = "completed";
                var summary = (string?)data["outcome"] ?? (data["project"] is JObject project && project["costDisplay"] != null ? $"Ready · {project["costDisplay"]} · {project["duration"]}" : "Ready");
                Feed($"{commandName} · {summary}", false, feedIcon);
                journal.Add("agent", request.Command, new { operationId = operation.id, session = request.Session, steps = progress.Steps.Count, outcome = (string?)data["outcome"] });
            }
            catch (Exception error) { Fail(operation, error, progress.Dispatched); }
        }
        if (operation.state != "completed")
        {
            operation.result = new { steps = progress.Steps, stoppedAt = progress.StepName, next = "Earlier actions may have taken effect. Inspect current state before retrying; no remaining steps will be replayed." };
            Feed($"{commandName} · Stopped · {progress.StepName}", false, "error");
        }
        TrimOperations(operation.id);
        lastAction = Time.realtimeSinceStartup;
        busy = false;
    }

    private static string GameFeedIcon(string command)
    {
        var verb = command.Substring(5);
        if (verb.EndsWith("-find") || verb.EndsWith("-search")) return "search";
        if (verb.EndsWith("-read") || verb.EndsWith("-list") || verb.EndsWith("-status") || verb.EndsWith("-topology") || verb.EndsWith("-choices") || verb is "contracts-active" or "business-contracts" or "production-history" || verb.StartsWith("market-") || verb.StartsWith("sales-") || verb.StartsWith("finance-") || verb.StartsWith("inspector-")) return "read";
        if (verb == "window-close") return "close";
        if (verb.EndsWith("-pause") || verb.EndsWith("-pause-all")) return "pause";
        if (verb == "window-open" || verb == "dialog-choose" || verb.EndsWith("-start") || verb.EndsWith("-develop") || verb.EndsWith("-release")) return "click";
        return "edit";
    }

    private void Fail(Operation operation, Exception error, bool dispatched)
    {
        operation.state = dispatched ? "partial_failure" : "failed";
        operation.error = new { code = error is AgentError agentError ? agentError.Code : "command_error", message = error.Message };
        journal.Add("agent", "command_failed", new { operationId = operation.id, command = operation.command, dispatched, operation.error });
        Feed(error.Message, false, "error");
        if (error is not AgentError) Logger.LogError(error);
    }

    internal void SetPaused(bool value)
    {
        paused = value;
        if (paused)
        {
            while (pending.Count > 0)
            {
                var (_, operation) = pending.Dequeue();
                operation.state = "interrupted";
                operation.error = new { code = "paused_by_user", message = "Not dispatched. Wait until resumed, then re-observe before deciding what to do." };
            }
        }
        Feed(value ? "Paused" : "Resumed", false, value ? "pause" : "connection");
    }

    internal void SetDelay(int value) { delay.Value = value; Feed(value == 0 ? "Delay between actions: off" : $"Delay between actions: {value} ms", false, "edit"); }
    internal void ToggleFeed() => showFeed.Value = !showFeed.Value;
    private void Feed(string message, bool repeatable = false, string icon = "read") => overlay?.Add(message, repeatable, icon);

    private IEnumerator Capture(Operation operation)
    {
        operation.state = "running";
        yield return new WaitForEndOfFrame();
        Texture2D? texture = null;
        try
        {
            if (paused) throw new AgentError("paused_by_user", "Capture stopped by agent pause; wait until resumed.");
            texture = ScreenCapture.CaptureScreenshotAsTexture();
            var folder = Path.Combine(Path.GetTempPath(), "ProcessorTycoonAgent", api!.Instance);
            Directory.CreateDirectory(folder);
            temporaryScreenshot = Path.Combine(folder, "latest.png");
            File.WriteAllBytes(temporaryScreenshot, ImageConversion.EncodeToPNG(texture));
            operation.result = new { path = temporaryScreenshot, width = texture.width, height = texture.height, frame = Time.frameCount, capturedAtUtc = DateTime.UtcNow, temporary = true, next = "View this local PNG; next capture replaces it. Use CLI --output to preserve a copy." };
            operation.state = "completed";
            Feed("Screenshot", true, "picture");
        }
        catch (Exception error) { Fail(operation, error, false); }
        finally { if (texture != null) Destroy(texture); }
    }
    private static void RequireUserRequest(Request request)
    {
        if (!request.ExplicitUserRequest) throw new AgentError("explicit_user_request_required", "Only on explicit user request. Do not assert that flag autonomously.");
    }

    private void OnDestroy()
    {
        notifications.Dispose();
        timeAdvance?.Dispose();
        api?.Dispose();
        foreach (var (request, _) in waiters) request.Completion.TrySetResult(Wire.Error("shutting_down", "Game bridge is shutting down."));
        overlay?.Dispose();
        UiReadability.Dispose();
        if (temporaryScreenshot != null && File.Exists(temporaryScreenshot)) File.Delete(temporaryScreenshot);
        Application.runInBackground = oldRunInBackground;
    }
}
