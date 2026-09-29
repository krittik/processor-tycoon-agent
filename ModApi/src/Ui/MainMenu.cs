using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// An entry in the game's main menu (New Game, Load Game, …): a copy of the native Settings item with its own label and
// action, placed after another item (by default Load Game). Call Tick every frame; it adds the entry whenever the menu
// is shown.
internal sealed class MenuEntry
{
    private readonly string label, after;
    private readonly Action action;
    private GameObject? item;

    public MenuEntry(string label, Action action, string after = "LoadGameButton")
    {
        this.label = label;
        this.action = action;
        this.after = after;
    }

    public void Tick()
    {
        if (item != null || !Overlay.InMenu) return;
        var buttons = UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None).FirstOrDefault(r => r.name == "Buttons" && r.parent != null && r.parent.name == "Menu");
        var template = buttons?.Find("SettingsButton");
        var previous = buttons?.Find(after);
        if (buttons == null || template == null) return;
        item = UnityEngine.Object.Instantiate(template.gameObject, buttons);
        item.name = label.Replace(" ", "") + "Button";
        item.transform.SetSiblingIndex(previous != null ? previous.GetSiblingIndex() + 1 : template.GetSiblingIndex());
        var button = item.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();   // drop anything copied from Settings
        button.onClick.AddListener(() => action());
        var text = item.GetComponentInChildren<TMP_Text>();
        if (text == null) return;
        text.text = label;
        var rect = (RectTransform)item.transform;
        rect.sizeDelta = new Vector2(text.GetPreferredValues(label).x + 10, rect.sizeDelta.y);
    }

    // The main menu's own button with this label (New Game, Load Game, …), pressed as a click would.
    public static bool Press(string nativeLabel)
    {
        var button = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
            .FirstOrDefault(b => b.isActiveAndEnabled && b.transform.parent != null && b.transform.parent.name == "Buttons" && b.GetComponentInChildren<TMP_Text>()?.text.Trim() == nativeLabel);
        button?.onClick.Invoke();
        return button != null;
    }
}
