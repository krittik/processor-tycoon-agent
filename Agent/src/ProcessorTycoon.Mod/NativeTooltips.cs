using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

// Reads the text of active native TooltipTrigger payloads (what hovering would show) without hovering.
internal static class NativeTooltips
{
    private const string TriggerType = "ProcessorTycoon.TooltipSystem.TooltipTrigger";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static JObject Under(string pathContains)
    {
        var result = new JObject();
        foreach (var trigger in Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.isActiveAndEnabled && c.gameObject.scene.IsValid() && c.GetType().FullName == TriggerType))
        {
            var path = trigger.transform.name;
            for (var parent = trigger.transform.parent; parent != null && path.Length < 300; parent = parent.parent) path = parent.name + "/" + path;
            var index = path.IndexOf(pathContains, System.StringComparison.Ordinal);
            if (index < 0) continue;
            if (trigger.GetType().GetProperty("IsActive", Flags)?.GetValue(trigger) is false) continue;
            var data = trigger.GetType().GetField("tooltipData", Flags)?.GetValue(trigger);
            var content = data?.GetType().GetProperty("Content", Flags)?.GetValue(data) as string;
            if (string.IsNullOrWhiteSpace(content)) continue;
            var key = path.Substring(index + pathContains.Length).Trim('/').Split('/').FirstOrDefault(s => s.Length > 0) ?? "tooltip";
            result[key] = Regex.Replace(content, "<[^>]+>", "").Trim();
        }
        return result;
    }
}
