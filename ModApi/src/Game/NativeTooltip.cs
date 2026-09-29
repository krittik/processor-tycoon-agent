using ProcessorTycoon.TooltipSystem;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi.Game;

// The game's own tooltip (TooltipTrigger with a TooltipData made at runtime) on something in the game's UI, for example a
// TextBadge. Change the returned data's Header and Content to update it. On a mod's overlay use Tip instead: the game's
// tooltip canvas can be below it.
internal static class NativeTooltip
{
    public static TooltipData On(GameObject target, string header, string content = "", int width = 0)
    {
        var data = ScriptableObject.CreateInstance<TooltipData>();
        data.Header = header;
        data.Content = content;
        data.Delay = .15f;
        data.PreferedWidth = width;
        if (target.GetComponent<Graphic>() == null) target.AddComponent<Image>().color = Color.clear;
        target.AddComponent<TooltipTrigger>().tooltipData = data;
        return data;
    }
}
