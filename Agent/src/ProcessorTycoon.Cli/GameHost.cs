using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// Local installation/process diagnostics work even when the in-game bridge does not.
internal static class GameHost
{
    internal const string Executable = "Processor Tycoon Beta.exe";

    internal static string? FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, Executable))) return directory.FullName;
            var nested = Path.Combine(directory.FullName, "Processor Tycoon");
            if (File.Exists(Path.Combine(nested, Executable))) return nested;
        }
        return null;
    }

    internal static string? Endpoint(string? root)
    {
        if (root == null) return null;
        var path = Path.Combine(root, "tools", "endpoint.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["endpoint"]?.GetValue<string>() : null;
    }

    internal static bool LocalSupportsFast(string endpoint)
    {
        var root = FindRoot();
        if (root == null) return false;
        var path = Path.Combine(root, "tools", "endpoint.json");
        if (!File.Exists(path)) return false;
        var discovery = JsonNode.Parse(File.ReadAllText(path));
        return string.Equals(discovery?["endpoint"]?.GetValue<string>()?.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && discovery?["headlessFastVersion"]?.GetValue<int>() == 1;
    }

    private static int[] Processes(string? root)
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
        {
            using (process)
            {
                try
                {
                    if (root == null || string.Equals(process.MainModule?.FileName, Path.Combine(root, Executable), StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { ids.Add(process.Id); }
            }
        }
        return ids.ToArray();
    }

    internal static async Task<JsonObject> Status(string? explicitEndpoint = null, string session = CliSession.Aux, string? root = null)
    {
        root ??= FindRoot();
        var processes = Processes(root);
        var installed = root != null && File.Exists(Path.Combine(root, "BepInEx", "plugins", "ProcessorTycoon.Mod", "ProcessorTycoon.Mod.dll"));
        var host = new JsonObject { ["gameDirectory"] = root, ["executable"] = root == null ? null : Path.Combine(root, Executable), ["processRunning"] = processes.Length > 0, ["processIds"] = new JsonArray(processes.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["modInstalled"] = installed, ["canLaunch"] = root != null && installed && processes.Length == 0 };
        string? endpoint = null;
        string? failure = null;
        try
        {
            endpoint = explicitEndpoint ?? Endpoint(root);
            if (endpoint != null && (explicitEndpoint != null || processes.Length > 0))
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback) throw new ArgumentException("Endpoint must be a loopback HTTP URL.");
                using var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(2) };
                using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { command = "status", session }), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("v1/command", content);
                response.EnsureSuccessStatusCode();
                var live = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
                if (live?["ok"]?.GetValue<bool>() == true && live["apiVersion"] != null)
                {
                    if (explicitEndpoint == null && root != null && live["gameDirectory"] is JsonValue returnedRoot && !string.Equals(Path.GetFullPath(returnedRoot.GetValue<string>()).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new IOException("The discovery endpoint belongs to a different game installation. Check the mod port/configuration; no gameplay command was sent by status.");
                    live["host"] = host;
                    live["bridge"] = new JsonObject { ["state"] = "ready", ["endpoint"] = endpoint };
                    // Compatibility with an older installed mod: never invent campaign readiness.
                    live["game"] ??= new JsonObject { ["state"] = live["scene"]?.GetValue<string>() == "Main Menu Scene" ? "main_menu" : "unknown", ["campaignLoaded"] = live["scene"]?.GetValue<string>() == "Main Menu Scene" ? JsonValue.Create(false) : null, ["next"] = "Use game session-new-preview or game save-list in the main menu; upgrade the mod for full readiness diagnostics." };
                    return live!;
                }
                failure = "The endpoint did not return a compatible mod status.";
            }
        }
        catch (Exception error) when (error is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { failure = error.Message; }
        var state = root == null ? "installation_not_found" : !installed ? "mod_not_installed" : processes.Length == 0 ? "not_running" : "bridge_unavailable";
        var next = state switch
        {
            "not_running" => "Run pt-agent launch. This starts the visible game client, not a new campaign.",
            "installation_not_found" => "Place the distributed CLI under the game directory (tools), or run it from the game directory.",
            "mod_not_installed" => "Install the mod distribution into this game directory, including BepInEx. No bridge plugin was found.",
            _ => "The game process exists but the mod is not responding yet. During startup, check status again shortly. If persistent, inspect BepInEx/LogOutput.log; do not launch a duplicate game."
        };
        return new JsonObject
        {
            ["ok"] = true, ["cli"] = new JsonObject { ["version"] = AgentInfo.Version, ["release"] = AgentInfo.Release, ["repository"] = AgentInfo.Repository }, ["host"] = host,
            ["bridge"] = new JsonObject { ["state"] = "unavailable", ["endpoint"] = endpoint, ["diagnostic"] = failure },
            ["game"] = new JsonObject { ["state"] = state, ["campaignLoaded"] = processes.Length == 0 ? JsonValue.Create(false) : null, ["canReadCampaign"] = false, ["reason"] = next, ["next"] = next }, ["next"] = next
        };
    }

    internal static async Task<JsonObject> Launch(int timeout)
    {
        var status = await Status();
        if (status["host"]?["processRunning"]?.GetValue<bool>() == true) { status["launch"] = "already_running"; return status; }
        if (status["host"]?["canLaunch"]?.GetValue<bool>() != true) { status["ok"] = false; status["launch"] = "unavailable"; return status; }
        var path = status["host"]!["executable"]!.GetValue<string>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
            if (process == null) throw new InvalidOperationException("The operating system did not return a game process.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            status["ok"] = false; status["launch"] = "failed"; status["error"] = error.Message; return status;
        }
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        do
        {
            await Task.Delay(300);
            status = await Status();
            if (status["bridge"]?["state"]?.GetValue<string>() == "ready" && status["game"]?["state"]?.GetValue<string>() is "main_menu" or "campaign") break;
            if (status["host"]?["processRunning"]?.GetValue<bool>() == false) { status["launch"] = "process_exited"; status["ok"] = false; return status; }
        } while (DateTime.UtcNow < deadline);
        status["launch"] = status["bridge"]?["state"]?.GetValue<string>() == "ready" && status["game"]?["state"]?.GetValue<string>() is "main_menu" or "campaign" ? "ready" : "started_not_ready";
        return status;
    }
}
