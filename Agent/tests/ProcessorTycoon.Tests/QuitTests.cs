using System.Text.Json.Nodes;

// Quit flow against MockBridge with a FAKE process state (bridge.Exited). Quit.Core never touches the real game here:
// the status, process check and window-close functions are all injected.
internal static class QuitTests
{
    internal static async Task Run(Checker check)
    {
        foreach (var mode in new[] { "ok", "drop", "empty", "nonobject", "string_error", "running_then_drop" })
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                bridge.ExitMode = mode;
                var r = await Quit.Core(deps, null, 5);
                check($"quit {mode}: JSON result, exited", r["ok"]?.GetValue<bool>() == true && r["processRunning"]?.GetValue<bool>() == false && r["outcome"]?.ToString() == "process_exited" && r["route"]?.ToString() == "native_exit_to_desktop" && r["saved"]?.GetValue<bool>() == false, r.ToJsonString());
            }
        }
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                bridge.ExitMode = "drop";
                var r = await Quit.Core(deps, "agent-checkpoint", 5);
                check("quit --save saves first", r["ok"]?.GetValue<bool>() == true && r["saved"]?.GetValue<bool>() == true && bridge.Log.IndexOf("game.save-create agent-checkpoint") >= 0 && bridge.Log.IndexOf("game.save-create agent-checkpoint") < bridge.Log.IndexOf("game.session-exit desktop"), r.ToJsonString());
            }
        }
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                bridge.ExitIgnored = true;
                var closed = false;
                deps.CloseWindows = () => { closed = true; bridge.Exited = true; return Task.FromResult(new JsonArray(4242)); };
                var r = await Quit.Core(deps, null, 5);
                check("quit falls back to window close", closed && r["ok"]?.GetValue<bool>() == true && r["route"]?.ToString() == "native_exit_then_window_close", r.ToJsonString());
            }
        }
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                bridge.DialogOpen = true;
                var r = await Quit.Core(deps, null, 5);
                check("quit refuses with a dialog open", r["ok"]?.GetValue<bool>() == false && r["error"]?["code"]?.ToString() == "dialog_open" && !bridge.Log.Any(l => l.StartsWith("game.session-exit")) && !bridge.Exited, r.ToJsonString());
            }
        }
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                deps.Status = () => Task.FromResult(new JsonObject { ["host"] = new JsonObject { ["processRunning"] = true }, ["bridge"] = new JsonObject { ["state"] = "unavailable" } });
                deps.CloseWindows = () => { bridge.Exited = true; return Task.FromResult(new JsonArray()); };
                var r = await Quit.Core(deps, null, 5);
                check("quit without bridge closes the window", r["ok"]?.GetValue<bool>() == true && r["route"]?.ToString() == "window_close" && !bridge.Log.Any(l => l.StartsWith("game.")), r.ToJsonString());
                var refused = await Quit.Core(deps, "x", 5);
                check("quit --save refused without bridge", refused["ok"]?.GetValue<bool>() == false && refused["error"]?["code"]?.ToString() == "bridge_unavailable", refused.ToJsonString());
            }
        }
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                deps.Status = () => Task.FromResult(new JsonObject { ["host"] = "not an object", ["bridge"] = new JsonArray() });
                var r = await Quit.Core(deps, null, 5);
                check("quit tolerates non-object status", r["kind"]?.ToString() == "quit" && r["outcome"]?.ToString() == "not_running", r.ToJsonString());
            }
        }
        {
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                bridge.ExitMode = "drop";
                var calls = 0;
                deps.ProcessRunning = () => { calls++; if (calls == 1) throw new InvalidOperationException("status probe failed"); return Task.FromResult<bool?>(!bridge.Exited); };
                var r = await Quit.Core(deps, null, 5);
                check("quit tolerates a failing process probe", r["ok"]?.GetValue<bool>() == true, r.ToJsonString());
            }
        }
        {
            // A non-object bridge body on an ordinary command is a handled response_unknown, never an exception.
            var (bridge, deps) = Start();
            using (bridge) using (deps.Client)
            {
                var reply = await GameWatch.Call(deps.Client!, "game.garbage", "", null, "t", 5);
                check("non-object reply handled", reply["ok"]?.GetValue<bool>() == false && reply["error"]?["code"]?.ToString() == "response_unknown" && !GameWatch.TryData(reply, out _), reply.ToJsonString());
            }
        }
    }

    private static (MockBridge Bridge, Quit.Deps Deps) Start()
    {
        var bridge = new MockBridge();
        var deps = new Quit.Deps
        {
            Client = new HttpClient { BaseAddress = new Uri(bridge.Url), Timeout = TimeSpan.FromSeconds(5) },
            Status = () => Task.FromResult(new JsonObject { ["host"] = new JsonObject { ["processRunning"] = true }, ["bridge"] = new JsonObject { ["state"] = "ready", ["endpoint"] = bridge.Url } }),
            ProcessRunning = () => Task.FromResult<bool?>(!bridge.Exited),
            CloseWindows = () => Task.FromResult(new JsonArray()),
            ExitWaitSeconds = 2, CloseWaitSeconds = 2, PollMilliseconds = 50
        };
        return (bridge, deps);
    }
}
