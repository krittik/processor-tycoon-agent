using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// pt-agent companion start NAME | list | stop NAME: an agent's own game that plays in the multiplayer session the user
// hosts in this installation. A companion is a copy of this installation in Companions\NAME (game, BepInEx, mods and
// their settings, with its own multiplayer identity and agent bridge), started without a window by default, that joins
// through the host's local address (MpApi localAddress: loopback, also when the user hosts on Steam). The agent then
// plays only through the companion's own CLI; this installation's CLI keeps controlling the user's game. Several
// companions run side by side, one per name.
internal static class Companion
{
    internal const string Folder = "Companions", Marker = "companion.json";
    private static readonly string[] SkippedTop = { "Dev", Folder, "mp-dev", "artifacts", Marker };
    private static readonly string[] SkippedPaths = { @"BepInEx\cache", @"BepInEx\LogOutput.log", @"BepInEx\agent-reports", @"BepInEx\mp-reports", @"tools\endpoint.json", @"tools\agent-prompt-instance.txt" };

    internal static async Task<JsonObject> Run(string command, string name, IReadOnlyDictionary<string, string> options, ISet<string> flags, int timeout)
    {
        var root = GameHost.FindRoot();
        if (root == null) return Error("game_not_found", "No Processor Tycoon installation was found next to this CLI.");
        if (File.Exists(Path.Combine(root, Marker)))
            return Error("inside_companion", $"This CLI belongs to a companion. Run companion commands with the user's game CLI: {Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, "pt-agent.cmd")}");
        return command switch
        {
            "companion.list" => List(root),
            "companion.start" => await Start(root, name, options, flags, timeout),
            "companion.stop" => await Stop(root, name, timeout),
            _ => Error("invalid_request", "Use companion start NAME, companion list or companion stop NAME."),
        };
    }

    private static async Task<JsonObject> Start(string root, string name, IReadOnlyDictionary<string, string> options, ISet<string> flags, int timeout)
    {
        if (!ValidName(name)) return Error("invalid_request", "companion start needs a NAME: letters, digits, spaces, - or _, at most 24 characters (it is also the player name).");
        if (!flags.Contains("explicit-user-request"))
            return Error("user_approval_required", "A companion runs a second copy of the game on the user's PC and joins their session as another player. Ask the user first; when they agree, repeat the command with --explicit-user-request.");
        var type = options.GetValueOrDefault("company-type", "cpu") switch { "cpu" => 0, "fabless" => 1, "foundry" => 2, _ => -1 };
        if (type < 0) return Error("invalid_request", "--company-type is cpu, fabless or foundry.");
        var headless = !flags.Contains("windowed");

        // The user's game must host a running session; its local address is where the companion joins.
        var user = await Bridge(root, "mp.status", "", null, timeout);
        if (user == null) return Error("game_not_running", "The user's game is not running or its agent bridge does not answer.");
        if (user["ok"].Bool() == false) return user;
        var session = user["data"] as JsonObject ?? new JsonObject();
        if (session["active"].Bool() != true || session["state"].Str() != "Running")
            return Error("no_session", "The user's game hosts no running multiplayer session. Ask the user to host one (Multiplayer window, Host a game, Steam or IP); a companion joins it.");
        if (session["host"].Bool() != true) return Error("not_host", "The user joined someone else's session. A companion can only join a session hosted in this game.");
        var address = session["localAddress"].Str() ?? "";
        if (address.Length == 0) return Error("no_local_address", "The user's session has no local address for game copies on this PC (update the Multiplayer mod, or a local port 27960-27969 is in use).");

        var dir = Path.Combine(root, Folder, name);
        var copied = Sync(root, dir);
        OwnBridgePort(root, dir);
        File.WriteAllText(Path.Combine(dir, Marker), new JsonObject { ["name"] = name, ["userGame"] = root }.ToJsonString());
        var cli = Path.Combine(dir, "pt-agent.cmd");

        var status = await GameHost.Status(root: dir);
        if (status["host"]?["processRunning"].Bool() != true)
        {
            var exe = Path.Combine(dir, GameHost.Executable);
            var arguments = headless ? $"-batchmode -nographics -logFile \"{Path.Combine(dir, "companion.log")}\"" : "-screen-fullscreen 0 -screen-width 1280 -screen-height 720";
            using var process = Process.Start(new ProcessStartInfo(exe, arguments) { WorkingDirectory = dir, UseShellExecute = true, WindowStyle = headless ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal });
            try { if (headless && process != null) process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
            var ready = DateTime.UtcNow.AddSeconds(Math.Max(timeout, 120));
            do
            {
                await Task.Delay(1000);
                status = await GameHost.Status(root: dir);
                if (status["host"]?["processRunning"].Bool() == false) return Error("companion_exited", $"The companion's game exited while starting; see {Path.Combine(dir, "BepInEx", "LogOutput.log")}.");
            } while (DateTime.UtcNow < ready && !(status["bridge"]?["state"].Str() == "ready" && status["game"]?["state"].Str() is "main_menu" or "campaign"));
            if (status["bridge"]?["state"].Str() != "ready") return Error("companion_not_ready", "The companion's game started but its agent bridge did not answer in time; run companion start again.");
        }

        var mp = await Bridge(dir, "mp.status", "", null, timeout);
        if (mp?["data"]?["state"].Str() != "Running")
        {
            var parameters = new JsonObject { ["company"] = options.GetValueOrDefault("company", name), ["companyType"] = type };
            var join = await Bridge(dir, "mp.join", address, parameters, timeout, name);
            if (join == null || join["ok"].Bool() == false) return join ?? Error("companion_not_ready", "The companion's bridge stopped answering.");
            var joined = DateTime.UtcNow.AddSeconds(180);
            do
            {
                await Task.Delay(1000);
                mp = await Bridge(dir, "mp.status", "", null, timeout);
                var error = mp?["data"]?["error"].Str();
                if (!string.IsNullOrEmpty(error) && mp?["data"]?["state"].Str() == "None") return Error("join_failed", error!);
            } while (DateTime.UtcNow < joined && mp?["data"]?["state"].Str() != "Running");
            if (mp?["data"]?["state"].Str() != "Running") return Error("join_timeout", "The companion did not finish joining in 3 minutes; read its state with companion list.");
        }
        return new JsonObject
        {
            ["ok"] = true, ["name"] = name, ["directory"] = dir, ["cli"] = cli, ["joined"] = address, ["headless"] = headless, ["filesCopied"] = copied,
            ["session"] = mp?["data"]?.DeepClone(),
            ["next"] = $"Play from now on only through \"{cli}\" (for example \"{cli}\" game situation --compact). \"{Path.Combine(root, "pt-agent.cmd")}\" controls the user's own game: never use it for your moves. The session clock is the user's (you cannot pause). " +
                       (headless ? "The companion has no window: screenshots do not work. " : "") + $"Talk with mp chat through your CLI. When done: companion stop {name} with the user's CLI."
        };
    }

    private static JsonObject List(string root)
    {
        var list = new JsonArray();
        var folder = Path.Combine(root, Folder);
        if (Directory.Exists(folder))
            foreach (var dir in Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, Marker))))
                list.Add(new JsonObject { ["name"] = Path.GetFileName(dir), ["running"] = Running(dir).Length > 0, ["cli"] = Path.Combine(dir, "pt-agent.cmd") });
        return new JsonObject { ["ok"] = true, ["companions"] = list, ["next"] = "Each running companion's own CLI reports its game and session (mp status)." };
    }

    // Leaves the session (the company stays in the world under the host's caretaker rules), then exits the companion's
    // game through its own CLI (quit); a copy that does not exit is ended, since it holds no unsaved player data.
    private static async Task<JsonObject> Stop(string root, string name, int timeout)
    {
        if (!ValidName(name)) return Error("invalid_request", "companion stop needs the companion's NAME (companion list).");
        var dir = Path.Combine(root, Folder, name);
        if (!File.Exists(Path.Combine(dir, Marker))) return Error("not_found", $"No companion named {name} (companion list).");
        if (Running(dir).Length == 0) return new JsonObject { ["ok"] = true, ["name"] = name, ["state"] = "not_running" };
        await Bridge(dir, "mp.leave", "", null, timeout);
        try
        {
            using var quit = Process.Start(new ProcessStartInfo(Path.Combine(dir, "tools", "pt-agent.exe"), "quit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
            if (quit != null) await quit.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        }
        catch (Exception error) when (error is OperationCanceledException or System.ComponentModel.Win32Exception) { }
        for (var i = 0; i < 20 && Running(dir).Length > 0; i++) await Task.Delay(1000);
        var ended = false;
        foreach (var process in Running(dir)) using (process) { try { process.Kill(); ended = true; } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        return new JsonObject { ["ok"] = true, ["name"] = name, ["state"] = ended ? "ended" : "exited", ["next"] = "Its company stays in the session (the host's caretaker rules apply); companion start " + name + " joins with it again." };
    }

    // Copies the installation, skipping what belongs to one game only; files already equal (size and time) are kept. Mod
    // settings are copied once (later changes in the companion stay), without the Multiplayer mod's ClientId: the
    // companion gets its own identity and so its own player slot.
    private static int Sync(string root, string dir)
    {
        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, source);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            if (SkippedTop.Contains(top, StringComparer.OrdinalIgnoreCase) || relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || SkippedPaths.Any(p => relative.Equals(p, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            var target = Path.Combine(dir, relative);
            var isConfig = relative.StartsWith(@"BepInEx\config\", StringComparison.OrdinalIgnoreCase);
            if (isConfig && File.Exists(target)) continue;
            var from = new FileInfo(source);
            var to = new FileInfo(target);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(to.DirectoryName!);
            if (isConfig && from.Name.Equals("processortycoon.multiplayer.cfg", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(target, Regex.Replace(File.ReadAllText(source), @"(?m)^ClientId = .*$", "ClientId = "), new UTF8Encoding(false));
            else { File.Copy(source, target, overwrite: true); File.SetLastWriteTimeUtc(target, from.LastWriteTimeUtc); }
            copied++;
        }
        return copied;
    }

    // The agent bridge listens on a fixed loopback port (Api.Port, 17616 by default): every companion needs its own, kept
    // in its settings, different from the user's game and the other companions.
    private static void OwnBridgePort(string root, string dir)
    {
        const string relative = @"BepInEx\config\local.processortycoon.mod.cfg";
        static int? PortOf(string file) => File.Exists(file) && Regex.Match(File.ReadAllText(file), @"(?m)^Port = (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
        var taken = new HashSet<int> { PortOf(Path.Combine(root, relative)) ?? 17616 };
        foreach (var other in Directory.GetDirectories(Path.Combine(root, Folder)).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
            if (PortOf(Path.Combine(other, relative)) is int used) taken.Add(used);
        var config = Path.Combine(dir, relative);
        var current = PortOf(config);
        if (current is int port && !taken.Contains(port)) return;
        var free = Enumerable.Range(17617, 200).First(p => !taken.Contains(p) && PortFree(p));
        var text = File.Exists(config) ? File.ReadAllText(config) : "";
        if (!Regex.IsMatch(text, @"(?m)^Port = \d+")) text += "\n[Api]\n\nPort = 17616\n";
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, Regex.Replace(text, @"(?m)^Port = \d+", "Port = " + free), new UTF8Encoding(false));
    }

    private static bool PortFree(int port)
    {
        try { var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port); listener.Start(); listener.Stop(); return true; }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    private static Process[] Running(string dir)
    {
        var exe = Path.Combine(dir, GameHost.Executable);
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameHost.Executable)).Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }).ToArray();
    }

    // One command on a game's agent bridge (the auxiliary session: it takes no notifications from the agent's own).
    private static async Task<JsonObject?> Bridge(string gameDir, string command, string target, JsonObject? parameters, int timeout, string? value = null)
    {
        var endpoint = GameHost.Endpoint(gameDir);
        if (endpoint == null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback) return null;
        using var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(timeout + 5) };
        var request = new JsonObject { ["command"] = command, ["target"] = target, ["session"] = CliSession.Aux };
        if (parameters != null) request["parameters"] = parameters;
        if (value != null) request["value"] = value;
        try
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("v1/command", content);
            return JsonSafe.ParseObject(await response.Content.ReadAsStringAsync());
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    private static bool ValidName(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 _-]{0,23}$");
    private static JsonObject Error(string code, string message) => new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
