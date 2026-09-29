using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// For modders: writes the hierarchy of the UI objects whose name contains `filter` (active or not) to
// <dir>/uidump-<filter>.txt, with rect sizes, Image sprites, colours and corner scales, TMP font, size, colour and
// alignment, and component names, plus every sprite in use: how to find the game's exact styles and icon names.
internal static class UiDump
{
    public static string Write(string dir, string filter)
    {
        var sb = new StringBuilder();
        var roots = Resources.FindObjectsOfTypeAll<RectTransform>()
            .Where(r => r.gameObject.scene.IsValid() && r.name.Contains(filter) && (r.parent == null || !r.parent.name.Contains(filter)))
            .Take(6).ToList();
        foreach (var root in roots) Dump(root, 0, sb);
        sb.AppendLine("--- sprites in use: " + string.Join(", ", Resources.FindObjectsOfTypeAll<Image>().Where(i => i.sprite != null && i.gameObject.scene.IsValid())
            .Select(i => i.sprite.name).Distinct().OrderBy(n => n).Take(400)));
        string path = Path.Combine(dir, $"uidump-{filter}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static void Dump(RectTransform r, int depth, StringBuilder sb)
    {
        if (depth > 9) return;
        sb.Append(new string(' ', depth * 2)).Append(r.name).Append(r.gameObject.activeSelf ? "" : " [off]")
          .Append($" {r.rect.width:0}x{r.rect.height:0}");
        var img = r.GetComponent<Image>();
        if (img != null) sb.Append($" img({(img.sprite != null ? img.sprite.name : "-")},{Hex(img.color)},{img.type},ppu{(img.sprite != null ? img.sprite.pixelsPerUnit : 0):0.#}x{img.pixelsPerUnitMultiplier:0.##})");
        var text = r.GetComponent<TMP_Text>();
        if (text != null) sb.Append($" text(\"{Short(text.text)}\",{(text.font != null ? text.font.name : "-")},{text.fontSize:0.#},{Hex(text.color)},{text.fontStyle},{text.alignment})");
        var comps = r.GetComponents<MonoBehaviour>().Where(c => c != null && c is not Image && c is not TMP_Text).Select(c => c.GetType().Name);
        string names = string.Join("|", comps);
        if (names.Length > 0) sb.Append(" {").Append(names).Append('}');
        sb.AppendLine();
        foreach (Transform child in r) if (child is RectTransform rc) Dump(rc, depth + 1, sb);
    }

    private static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);
    private static string Short(string s) => s.Replace("\n", " ").Length > 30 ? s.Replace("\n", " ").Substring(0, 30) : s.Replace("\n", " ");
}
