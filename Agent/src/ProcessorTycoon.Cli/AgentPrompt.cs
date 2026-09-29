using System.Text.Json.Nodes;

internal static class AgentPrompt
{
    // The text lives in Shared/ so the plugin can copy the same prompt (Agent window, Copy prompt).
    internal const string Text = AgentPromptText.Text;

    internal static void PrintOnce(JsonObject status, TextWriter? writer = null)
    {
        if (status["bridge"]?["state"]?.GetValue<string>() != "ready" || status["instance"] is not JsonValue instanceValue) return;
        var root = status["host"]?["gameDirectory"]?.GetValue<string>();
        if (root == null) return;
        var instance = instanceValue.GetValue<string>();
        var marker = Path.Combine(root, "tools", "agent-prompt-instance.txt");
        using var mutex = new Mutex(false, "Local\\ProcessorTycoonAgentPrompt");
        mutex.WaitOne();
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == instance) return;
            (writer ?? Console.Error).WriteLine(Text);
            (writer ?? Console.Error).Flush();
            File.WriteAllText(marker, instance);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
