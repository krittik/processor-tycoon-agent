using System.Diagnostics;
using System.Text.Json.Nodes;

// pt-agent quit [--save NAME]: exits the game client through the native Pause Menu "Exit To Desktop" path WITHOUT saving
// (the native "unsaved progress will be lost" confirmation is accepted; nothing else is). --save NAME creates a native
// save first and aborts if it fails. Refuses while a native dialog or a time advance is active. If the in-game path fails
// or the bridge is unavailable, it asks the game window to close (WM_CLOSE); it never kills the process.
// While the game exits, the bridge may drop the connection or return an empty/partial reply: that is expected, and the
// result is decided by polling the process. It always returns one JSON object.
internal static class Quit
{
    // Everything that touches the real process is injected, so tests can run the whole flow against a mock bridge.
    internal sealed class Deps
    {
        public Func<Task<JsonObject>> Status = null!;
        public Func<Task<bool?>> ProcessRunning = null!;
        public Func<Task<JsonArray>> CloseWindows = null!;
        public HttpClient? Client;
        public int ExitWaitSeconds = 20, CloseWaitSeconds = 15, PollMilliseconds = 500;
    }

    internal static async Task<JsonObject> Run(string? endpointOverride, string? saveName, int timeout)
    {
        var status = await SafeStatus(() => GameHost.Status(endpointOverride));
        var endpoint = status.At("bridge", "endpoint").Str() ?? GameHost.Endpoint(GameHost.FindRoot());
        using var client = endpoint != null && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(timeout + 5) } : null;
        var deps = new Deps
        {
            Status = () => SafeStatus(() => GameHost.Status(endpointOverride)),
            ProcessRunning = async () => (await SafeStatus(() => GameHost.Status())).At("host", "processRunning").Bool(),
            CloseWindows = CloseMainWindows,
            Client = client
        };
        return await Core(deps, saveName, timeout, status);
    }

    internal static async Task<JsonObject> Core(Deps deps, string? saveName, int timeout, JsonObject? initialStatus = null)
    {
        var steps = new JsonArray();
        var save = string.IsNullOrWhiteSpace(saveName) ? null : saveName;
        var saved = false;
        try
        {
            var status = initialStatus ?? await deps.Status();
            if (status.At("host", "processRunning").Bool() != true) return Result(true, "not_running", "none", false, false, save, steps, "The game is not running; nothing was done.");
            var bridgeReady = status.At("bridge", "state").Str() == "ready" && deps.Client != null;
            var route = "native_exit_to_desktop";
            if (bridgeReady)
            {
                var running = status.At("timeAdvanceOperationId").Str();
                if (!string.IsNullOrEmpty(running) && running != "null") return Refuse("time_advance_active", $"A time advance is running (operation {running}). Wait for it or run game time-cancel; nothing was done.", null, steps);
                var dialogReply = await GameWatch.Call(deps.Client!, "game.dialog-read", "", null, CliSession.Aux, timeout);
                var open = (dialogReply.At("operation", "result", "data", "dialogs") as JsonArray)?.OfType<JsonObject>().Select(d => (JsonNode?)Dialogs.Compact(d)).ToArray() ?? Array.Empty<JsonNode?>();
                if (open.Length > 0) return Refuse("dialog_open", "A native dialog is open (for example the CPU release form or a completion popup). Resolve it with dialog-read / dialog-choose first; nothing was saved or quit.", new JsonArray(open), steps);
                if (save != null)
                {
                    var saveReply = await GameWatch.Call(deps.Client!, "game.save-create", save, null, CliSession.Aux, Math.Max(timeout, 30));
                    saved = GameWatch.TryData(saveReply, out _);
                    steps.Add(new JsonObject { ["step"] = "save-create", ["name"] = save, ["ok"] = saved, ["error"] = saved ? null : (saveReply.At("operation", "error") ?? saveReply.At("error"))?.DeepClone() });
                    if (!saved) return Refuse("save_failed", $"Saving '{save}' failed or was not verified (a native overwrite confirmation may be open). The game was NOT quit.", null, steps);
                }
                JsonObject exit;
                try { exit = await GameWatch.Call(deps.Client!, "game.session-exit", "desktop", null, CliSession.Aux, timeout); }
                catch (Exception error) { exit = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "response_unknown", ["message"] = error.Message } }; }
                var outcome = exit.At("operation", "result", "data", "outcome").Str();
                var errorCode = (exit.At("operation", "error", "code") ?? exit.At("error", "code")).Str();
                // A dropped connection or an empty/non-object reply while the game shuts down is expected.
                var dropped = errorCode == "response_unknown" || exit.At("state").Str() is "unknown" or "running";
                steps.Add(new JsonObject { ["step"] = "session-exit desktop", ["outcome"] = outcome ?? exit.At("operation", "state").Str() ?? errorCode, ["connectionDropped"] = dropped });
                if (outcome == "confirmation_required")
                    return Refuse("confirmation_required", "An unexpected native confirmation appeared; no choice was accepted and the game keeps running. Read it with dialog-read.", exit.At("operation", "result", "data", "dialogs")?.DeepClone() as JsonArray, steps, saved);
            }
            else
            {
                if (save != null) return Refuse("bridge_unavailable", "The mod bridge is not available, so no save can be made; the game was NOT quit. Retry without --save to close the window without saving.", null, steps);
                route = "window_close";
            }

            var stopped = await WaitStopped(deps, deps.ExitWaitSeconds);
            if (!stopped)
            {
                // Fallback: ask the game window to close (as its close button would). The process is never killed.
                JsonArray ids;
                try { ids = await deps.CloseWindows(); } catch (Exception error) { ids = new JsonArray(); steps.Add(new JsonObject { ["step"] = "close_main_window", ["error"] = error.Message }); }
                steps.Add(new JsonObject { ["step"] = "close_main_window", ["processIds"] = ids });
                route = route == "window_close" ? route : "native_exit_then_window_close";
                stopped = await WaitStopped(deps, deps.CloseWaitSeconds);
            }
            return Result(stopped, stopped ? "process_exited" : "still_running", route, !stopped, saved, save, steps, stopped ? "The game client has exited. Start it again with pt-agent launch." : "The game is still running. Check it on screen (a dialog may block closing); nothing was killed.");
        }
        catch (Exception error)
        {
            // Unexpected reply shapes must never crash quit: report what is known and the process state.
            bool? running = null;
            try { running = await deps.ProcessRunning(); } catch { }
            steps.Add(new JsonObject { ["step"] = "unexpected_error", ["type"] = error.GetType().Name, ["message"] = error.Message });
            return Result(running == false, running == false ? "process_exited" : "unknown", "native_exit_to_desktop", running != false, saved, save, steps, running == false ? "The game client has exited (an unexpected reply was ignored)." : "The quit outcome is unknown; check pt-agent status.");
        }
    }

    private static async Task<bool> WaitStopped(Deps deps, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            bool? running = null;
            try { running = await deps.ProcessRunning(); } catch { }
            if (running == false) return true;
            await Task.Delay(deps.PollMilliseconds);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    private static async Task<JsonObject> SafeStatus(Func<Task<JsonObject>> status)
    {
        try { return await status(); }
        catch (Exception error) { return new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "status_unavailable", ["message"] = error.Message } }; }
    }

    private static async Task<JsonArray> CloseMainWindows()
    {
        var status = await SafeStatus(() => GameHost.Status());
        var ids = (status.At("host", "processIds") as JsonArray)?.Select(i => i.Int()).Where(i => i != null).Select(i => i!.Value).ToArray() ?? Array.Empty<int>();
        foreach (var id in ids) { try { using var p = Process.GetProcessById(id); p.CloseMainWindow(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { } }
        return new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
    }

    private static JsonObject Result(bool ok, string outcome, string route, bool running, bool saved, string? save, JsonArray steps, string next) => new()
    {
        ["ok"] = ok, ["kind"] = "quit", ["outcome"] = outcome, ["route"] = route, ["method"] = route, ["processRunning"] = running, ["saved"] = saved, ["saveName"] = save, ["steps"] = steps, ["next"] = next
    };

    private static JsonObject Refuse(string code, string message, JsonArray? dialogs, JsonArray steps, bool saved = false) => new()
    {
        ["ok"] = false, ["kind"] = "quit", ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["processRunning"] = true, ["saved"] = saved, ["dialogs"] = dialogs, ["steps"] = steps
    };
}
