using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// A small icon right after a text the game draws (a company name in a list, a card title…), for marking things in the
// game's own UI without changing the text itself (other mods, such as the Agent mod, read those texts). It follows the
// text while the text moves or changes; destroy its GameObject to remove it. Give it a tooltip with Tip.On or, on the
// game's canvases, the game layer's NativeTooltip.On.
internal sealed class TextBadge : MonoBehaviour
{
    private TMP_Text text = null!;
    private RectTransform rect = null!;

    public static Image Attach(TMP_Text text, Sprite? sprite, Paint paint = Paint.Cta, string name = "Badge")
    {
        var rect = Ui.Rect(name, text.transform);
        rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, .5f);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        Painted.Add(image, paint);
        var badge = rect.gameObject.AddComponent<TextBadge>();
        badge.text = text;
        badge.rect = rect;
        badge.LateUpdate();
        return image;
    }

    // Right after the rendered text, sized like it (12–18 px), inside the text's own rectangle when that is too narrow.
    private void LateUpdate()
    {
        if (text == null) { Destroy(gameObject); return; }
        var bounds = text.textBounds;
        var textRect = text.rectTransform.rect;
        var size = Mathf.Clamp(text.fontSize, 12, 18);
        rect.sizeDelta = new Vector2(size, size);
        var x = bounds.size.x > 0 ? bounds.max.x - textRect.xMin + 4 : 0;
        rect.anchoredPosition = new Vector2(Mathf.Min(x, textRect.width - size), bounds.size.y > 0 ? bounds.center.y - textRect.center.y : 0);
    }
}
