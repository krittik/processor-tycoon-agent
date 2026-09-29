using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProcessorTycoon.Desktop.Email;
using ProcessorTycoon.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProcessorTycoonModApi.Game;

// A mod's email in the game's Email app: title, sender line, rich-text body and up to two answers on the Email window's
// own Accept / Decline buttons (the game ships them unused). Posting a letter again with the same key updates it and
// brings it to the top of the list.
internal sealed class Letter
{
    public string Key = "", Title = "", From = "", Body = "";
    // Label and action of the window's Accept (blue) and Decline buttons; null hides the button.
    public (string label, Action action)? Accept { get; set; }
    public (string label, Action action)? Decline { get; set; }
    internal Email Email = null!;
    internal EmailMessage Message = null!;
}

// Letters live in memory only: a save keeps just an email's ID, date and read flag and rebuilds the text from the game's own
// messages, so a mod's email would load back empty. Letters are therefore left out of every save (a Harmony postfix on
// EmailWindow.GetEmails, which only saving uses) and Clear removes them, for example when a session ends. The patches
// exist only while a letter does. Each mod compiles its own copy of this class: Owner keeps their email IDs and patches
// apart, and each copy only touches its own letters and the buttons while it shows one.
internal static class Mail
{
    private static readonly Dictionary<string, Letter> letters = new();
    private static Harmony? harmony;
    private static int nextId;
    private static Letter? shown;
    private static Button? accept, decline;

    // Names this mod's patches and its block of email IDs; set it once (for example the plugin GUID).
    public static string Owner = "mod";

    public static bool Available => EmailWindow.Instance != null;
    public static IEnumerable<Letter> Letters => letters.Values;

    public static Letter? Find(string key) => letters.TryGetValue(key, out var letter) ? letter : null;

    // Creates or updates the letter with this key; unread marks it (and the desktop's Email badge) as new.
    public static Letter? Post(string key, string title, string from, string body, bool unread = true)
    {
        var window = EmailWindow.Instance;
        if (window == null) return null;
        Install();
        if (!letters.TryGetValue(key, out var letter))
        {
            letter = new Letter { Key = key, Message = ScriptableObject.CreateInstance<EmailMessage>(), Email = new Email() };
            letter.Title = title; letter.From = from; letter.Body = body;
            letter.Message.Title = title;
            letter.Message.Text = body;
            letter.Email.Initialize(letter.Message, NextId());
            letters[key] = letter;
            window.emails.Add(letter.Email);
            window.InstantiateButton(letter.Email);
            FitTitle(ButtonOf(window, letter));
        }
        else
        {
            letter.Title = title; letter.From = from; letter.Body = body;
            Write(letter);
            letter.Email.SetDate();
            ButtonOf(window, letter)?.transform.SetAsFirstSibling();
        }
        if (unread) letter.Email.HasBeenRead = false;
        Refresh(window, letter);
        return letter;
    }

    // After changing a letter's fields (answers, body) without making it new.
    public static void Update(Letter letter)
    {
        var window = EmailWindow.Instance;
        if (window == null || !letters.ContainsKey(letter.Key)) return;
        Write(letter);
        Refresh(window, letter);
    }

    public static void Remove(string key)
    {
        if (!letters.TryGetValue(key, out var letter)) return;
        letters.Remove(key);
        var window = EmailWindow.Instance;
        if (window != null)
        {
            window.emails.Remove(letter.Email);
            var button = ButtonOf(window, letter);
            if (button != null) { window.emailButtons.Remove(button); Object.Destroy(button.gameObject); }
            if (window.lastEmail == letter.Email)
            {
                window.lastEmail = null;
                window.titleText.text = "Select Email";
                window.authorText.text = window.contentText.text = "";
                ShowButtons(null);
            }
            window.UpdateEmailAmountText();
            window.UpdateNotification();
        }
        Object.Destroy(letter.Message);
        if (letters.Count == 0) Uninstall();
    }

    public static void Clear() { foreach (var key in letters.Keys.ToList()) Remove(key); Uninstall(); }

    private static void Write(Letter letter)
    {
        letter.Message.Title = letter.Title;
        letter.Message.Text = letter.Body;
        letter.Email.FormatTextAgain();
    }

    private static void Refresh(EmailWindow window, Letter letter)
    {
        ButtonOf(window, letter)?.GetComponent<EmailButton>().Initialize(letter.Email);
        window.UpdateEmailAmountText();
        window.UpdateNotification();
        if (window.lastEmail == letter.Email) window.UpdateEmailText(letter.Email);
    }

    // The list shows the title and the date on one line; a long title ends in "…" before the date instead of running into it.
    private static void FitTitle(SelectableButton? button)
    {
        var title = button?.GetComponent<EmailButton>()?.emailText;
        if (title == null) return;
        title.textWrappingMode = TextWrappingModes.NoWrap;
        title.overflowMode = TextOverflowModes.Ellipsis;
        title.rectTransform.offsetMax = new Vector2(title.rectTransform.offsetMax.x - 90, title.rectTransform.offsetMax.y);
    }

    private static SelectableButton? ButtonOf(EmailWindow window, Letter letter) =>
        window.emailButtons.FirstOrDefault(b => b != null && b.GetComponent<EmailButton>()?.Email == letter.Email);

    // IDs by owner: 1,000,000 + 10,000 × (hash of Owner mod 10,000) + running number; the game's own emails use 0.
    private static int NextId()
    {
        var hash = Owner.Aggregate(17, (h, c) => unchecked(h * 31 + c)) & 0x7fffffff;
        return 1_000_000 + hash % 10_000 * 10_000 + nextId++ % 10_000;
    }

    private static bool Owns(Email email) => letters.Values.Any(l => l.Email == email);

    // The window shows an email (UpdateEmailText postfix): the sender line and the answer buttons of a letter.
    private static void Shown(EmailWindow window, Email email)
    {
        var letter = letters.Values.FirstOrDefault(l => l.Email == email);
        if (letter == null) { if (shown != null) ShowButtons(null); shown = null; return; }
        shown = letter;
        window.authorText.text = letter.From;
        FindButtons(window);
        ShowButtons(letter);
    }

    private static void FindButtons(EmailWindow window)
    {
        if (accept != null && decline != null) return;
        foreach (var button in window.GetComponentsInChildren<Button>(true))
        {
            if (button.name == "AcceptButton") accept = button;
            if (button.name == "DeclineButton") decline = button;
        }
        foreach (var button in new[] { accept, decline })
        {
            if (button == null) continue;
            // The first mod to use them drops whatever the prefab wired to these unused buttons; later mods only add.
            if (button.transform.Find("ModApi") == null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                new GameObject("ModApi").transform.SetParent(button.transform, false);
            }
        }
        accept?.onClick.AddListener(() => { if (shown?.Accept is { } a) a.action(); });
        decline?.onClick.AddListener(() => { if (shown?.Decline is { } d) d.action(); });
    }

    private static void ShowButtons(Letter? letter)
    {
        Set(accept, letter?.Accept);
        Set(decline, letter?.Decline);

        static void Set(Button? button, (string label, Action action)? answer)
        {
            if (button == null) return;
            button.gameObject.SetActive(answer != null);
            var text = button.GetComponentInChildren<TMP_Text>(true);
            if (answer != null && text != null) text.text = answer.Value.label;
        }
    }

    private static void Install()
    {
        if (harmony != null) return;
        harmony = new Harmony("processortycoon.modapi.mail." + Owner);
        harmony.CreateClassProcessor(typeof(Hooks)).Patch();
    }

    private static void Uninstall()
    {
        harmony?.UnpatchSelf();
        harmony = null;
        shown = null;
    }

    [HarmonyPatch]
    private static class Hooks
    {
        [HarmonyPatch(typeof(EmailWindow), nameof(EmailWindow.UpdateEmailText)), HarmonyPostfix]
        private static void UpdateEmailText(EmailWindow __instance, Email email) => Shown(__instance, email);

        // Saving is the only caller of GetEmails.
        [HarmonyPatch(typeof(EmailWindow), nameof(EmailWindow.GetEmails)), HarmonyPostfix]
        private static void GetEmails(List<Email> __result) => __result.RemoveAll(Owns);
    }
}
