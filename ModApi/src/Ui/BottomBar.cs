using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProcessorTycoonModApi;

// A status entry in the game's bottom bar (bottom right, left of the clock): an icon and a short text such as
// "Multiplayer · hosting · 3 online"; a click runs Clicked (usually a window docked above the entry, Window.ShowAbove).
// Hover and press look like the game's buttons. Entries of every mod built on this API line up without overlapping:
// each entry keeps a marker named "<order>:<id>" with its width under the shared GameObject "Processor Tycoon Mod API
// bottom bar" and sits left of the markers with a lower order (plain names, because every mod compiles its own copy).
internal sealed class BarItem
{
    private const string Registry = "Processor Tycoon Mod API bottom bar";
    private readonly Overlay overlay;
    private readonly RectTransform marker;
    private readonly TextMeshProUGUI label;
    private readonly Image icon;
    private readonly int order;
    public readonly RectTransform Rect;
    public event Action? Clicked;
    // In the main menu too (the Multiplayer mod shows its entry only during a game; the menu has its own item).
    public bool ShowInMenu = true;
    public bool Visible = true;

    // order: lower sits further right (the Agent mod 0, the Multiplayer mod 10).
    public BarItem(Overlay overlay, string id, int order, Sprite? iconSprite, string text)
    {
        this.overlay = overlay;
        this.order = order;
        var registry = GameObject.Find(Registry);
        if (registry == null) { registry = new GameObject(Registry, typeof(RectTransform)); Object.DontDestroyOnLoad(registry); }
        marker = Ui.Rect($"{order:D4}:{id}", registry.transform);
        Rect = Ui.Rect("Bar item " + id, overlay.Root);
        Rect.anchorMin = Rect.anchorMax = Rect.pivot = new Vector2(1, 0);
        Rect.sizeDelta = new Vector2(200, 40);
        var hit = Rect.gameObject.AddComponent<Image>();
        hit.color = Color.white;
        var button = Rect.gameObject.AddComponent<Button>();
        button.targetGraphic = hit;
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = Color.clear;
        colors.highlightedColor = new Color(1, 1, 1, .12f);
        colors.pressedColor = new Color(1, 1, 1, .21f);
        colors.selectedColor = Color.clear;
        colors.fadeDuration = .1f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => Clicked?.Invoke());
        Rect.gameObject.AddComponent<HandCursor>();
        // The contents shrink on press like the game's buttons.
        var content = Ui.Rect("Content", Rect);
        Ui.Stretch(content, 0, 0);
        content.pivot = new Vector2(.5f, .5f);
        Rect.gameObject.AddComponent<PressFeedback>().Target = content;
        var row = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(10, 10, 0, 0);
        row.spacing = 8;
        row.childAlignment = TextAnchor.MiddleLeft;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        icon = Ui.Icon(content, iconSprite, 16, Paint.Tray);
        label = Ui.Label(content, "", 15, Paint.Tray);
        Text = text;
    }

    public string Text
    {
        get => label.text;
        set { if (label.text == value) return; label.text = value; Rect.sizeDelta = new Vector2(label.GetPreferredValues(value).x + 44, 40); }
    }

    public Sprite? Icon { set => icon.sprite = value; }

    // Every frame: visibility and the place after the entries with a lower order.
    public void Tick()
    {
        var show = Visible && (ShowInMenu || !Overlay.InMenu);
        if (Rect.gameObject.activeSelf != show) Rect.gameObject.SetActive(show);
        marker.sizeDelta = new Vector2(show ? Rect.sizeDelta.x : 0, 0);
        if (!show) return;
        // Native clock (in a game) or version text (main menu) at the right edge.
        var right = Overlay.InMenu ? 140f : 90f;
        foreach (RectTransform other in marker.parent)
            if (other != marker && other.sizeDelta.x > 0 && string.CompareOrdinal(other.name, marker.name) < 0) right += other.sizeDelta.x + 4;
        Rect.anchoredPosition = new Vector2(-right, 0);
    }

    public void Destroy()
    {
        Object.Destroy(marker.gameObject);
        Object.Destroy(Rect.gameObject);
    }
}
