using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// Player-visible desktop and email information. Email selection uses the native card action and read semantics.
internal sealed class UtilityGameApi : IGameModule
{
    private const string EmailScope = "EmailWindow";
    private readonly GameUi ui;
    private readonly Action<string> log;
    private string? selectedTitle;
    private string? selectedDate;
    private string? requestedDifficulty;
    private string? requestedWallpaper;
    private bool difficultyChanged;
    private bool pausedForNotificationSettings;
    private string? companyNameReadback;
    private string? founderNameReadback;
    private string? companyColorReadback;
    private JObject? companyLimits;
    private bool companyEditorInitiallyOpen;
    private bool companyPickerInitiallyOpen;
    private bool companyEditorLeftOpen;
    private bool companyOpenedPauseMenu;
    private bool companyPauseCleanupVerified;
    public string[] Commands => new[] { "game.desktop-read", "game.email-list", "game.email-read", "game.notification-companies", "game.notification-company", "game.statistics-read", "game.company-read", "game.company-rename", "game.company-set", "game.gameplay-read", "game.gameplay-set", "game.wallpaper-list", "game.wallpaper-set" };

    public UtilityGameApi(GenericUi native, Action<string> log) { ui = new GameUi(native); this.log = log; }

    public static object Schema => new
    {
        commands = new object[]
        {
            new { command = "game desktop-read", behavior = "Read current player-visible desktop status and exact native app availability; no navigation." },
            new { command = "game email-list", parameters = new { category = "All|Finance|Companies|Economic Events" }, behavior = "Open/reuse Email and return every instantiated native message card. Opening Email can natively mark messages read." },
            new { command = "game email-read EXACT_NAME", behavior = "Choose an exact key returned by email-list, show the message through its native card, and return subject/date/author/body." },
            new { command = "game notification-companies", behavior = "Open/reuse native Notification Settings and return every company toggle. Opening it through Pause Menu pauses game time and leaves the menu/window open." },
            new { command = "game notification-company EXACT_COMPANY", parameters = new { enabled = "boolean" }, behavior = "Set one exact native company notification toggle and verify it." },
            new { command = "game statistics-read", behavior = "Open/reuse native Statistics and return displayed company lifetime totals and research modifiers." },
            new { command = "game company-read", behavior = "Read name, founder and color through a fresh native Company editor and Color Picker. A preexisting editor is treated as a player draft and left open." },
            new { command = "game company-set", parameters = new { companyName = "string, optional", founderName = "string, optional", companyColorHex = "RRGGBB or #RRGGBB, optional" }, behavior = "Edit only supplied native fields, Apply color when supplied, Confirm once, then reopen native UI for readback." },
            new { command = "game company-rename", parameters = new { companyName = "string, optional", founderName = "string, optional", companyColorHex = "RRGGBB or #RRGGBB, optional" }, behavior = "Compatibility alias for company-set." },
            new { command = "game gameplay-read", behavior = "Open/reuse Gameplay Settings and return the current difficulty, exact choices and displayed AI behavior." },
            new { command = "game gameplay-set", parameters = new { difficulty = "exact option from gameplay-read" }, behavior = "Select one exact native difficulty and verify the dropdown readback." },
            new { command = "game wallpaper-list", behavior = "Open/reuse Desktop Customization and return every instantiated player wallpaper card." },
            new { command = "game wallpaper-set EXACT_NAME", behavior = "Select an exact wallpaper card and press the native Apply button." }
        },
        unsupported = new[] { "delete/archive email: no such native action", "unread state: card color is not exposed semantically", "native help: no evidenced help window/control" }
    };

    public void Validate(Request request)
    {
        if (!Commands.Contains(request.Command)) throw new AgentError("invalid_request", $"Unsupported utility command '{request.Command}'.");
        GameUi.Parameters(request, request.Command == "game.email-list" ? new[] { "category" } : request.Command == "game.notification-company" ? new[] { "enabled" } : request.Command is "game.company-rename" or "game.company-set" ? new[] { "companyName", "founderName", "companyColorHex" } : request.Command == "game.gameplay-set" ? new[] { "difficulty" } : Array.Empty<string>());
        if (request.Value != null) throw new AgentError("invalid_request", "Utility read commands do not take --value.");
        if (request.Parameters?["category"] is JToken category && (category.Type != JTokenType.String || string.IsNullOrWhiteSpace(category.Value<string>()))) throw new AgentError("invalid_value", "category must be a non-empty exact option.");
        if (request.Command is "game.email-read" or "game.notification-company" or "game.wallpaper-set") GameUi.RequiredTarget(request);
        else if (request.Target.Length > 0) throw new AgentError("invalid_request", $"{request.Command} does not take a target.");
        if (request.Command == "game.notification-company" && request.Parameters?["enabled"]?.Type != JTokenType.Boolean) throw new AgentError("invalid_value", "notification-company requires --enabled true|false.");
        if (request.Command == "game.gameplay-set" && (request.Parameters?["difficulty"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(request.Parameters["difficulty"]!.Value<string>()))) throw new AgentError("invalid_value", "gameplay-set requires --difficulty EXACT_OPTION.");
        if (request.Command is "game.company-rename" or "game.company-set")
        {
            if (request.Parameters == null || request.Parameters.Count == 0) throw new AgentError("invalid_request", $"{request.Command.Substring(5)} requires companyName, founderName and/or companyColorHex.");
            foreach (var property in request.Parameters.Properties()) if (property.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(property.Value.Value<string>())) throw new AgentError("invalid_value", $"{property.Name} must be a non-empty string.");
            if (request.Parameters?["companyName"]?.Value<string>()?.Length > 22) throw new AgentError("invalid_value", "companyName exceeds the native 22-character limit.");
            if (request.Parameters?["founderName"]?.Value<string>()?.Length > 24) throw new AgentError("invalid_value", "founderName exceeds the native 24-character limit.");
            if (request.Parameters?["companyColorHex"] is JToken color && !ValidHex(color.Value<string>()!)) throw new AgentError("invalid_value", "companyColorHex must contain exactly six hexadecimal RGB digits, with an optional leading #.");
        }
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        selectedTitle = null;
        selectedDate = null;
        requestedDifficulty = null;
        requestedWallpaper = null;
        difficultyChanged = false;
        companyNameReadback = null; founderNameReadback = null; companyColorReadback = null; companyLimits = null; companyEditorInitiallyOpen = false; companyPickerInitiallyOpen = false; companyEditorLeftOpen = false; companyOpenedPauseMenu = false; companyPauseCleanupVerified = false;
        if (request.Command == "game.desktop-read") yield break;
        if (request.Command is "game.gameplay-read" or "game.gameplay-set")
        {
            if (ui.Native.Scene != "Game Scene") throw new AgentError("wrong_scene", "Gameplay settings require a loaded game. From the main menu use game session-new-read to inspect starting difficulty, or game save-load to load a game; no gameplay setting was changed.");
            pausedForNotificationSettings = false;
            foreach (var step in OpenPauseWindow("GameplaySettingsWindow", "Gameplay Settings")) yield return step;
            if (request.Command == "game.gameplay-read") yield break;
            var difficulty = GameUi.One(GameUi.Controls(ui.Read("GameplaySettingsWindow")).Where(c => (string?)c["name"] == "Difficulty Dropdown" && (string?)c["role"] == "select"), "Difficulty Dropdown");
            requestedDifficulty = request.Parameters!["difficulty"]!.Value<string>();
            var options = difficulty["options"]!.Values<string>().ToArray();
            if (options.Count(option => option == requestedDifficulty) != 1) throw new AgentError("invalid_value", $"Unknown difficulty. Available: {string.Join(", ", options)}.");
            difficultyChanged = options[difficulty["value"]!.Value<int>()] != requestedDifficulty;
            if (difficultyChanged) yield return GameUi.Select(difficulty, requestedDifficulty!);
            yield break;
        }
        if (request.Command is "game.wallpaper-list" or "game.wallpaper-set")
        {
            pausedForNotificationSettings = false;
            foreach (var step in OpenPauseWindow("DesktopCustomizationWindow", "Desktop Customization")) yield return step;
            if (request.Command == "game.wallpaper-list") yield break;
            requestedWallpaper = request.Target;
            var cards = WallpaperCards(ui.Catalog("DesktopCustomizationWindow"));
            var card = GameUi.One(cards.Where(c => (string?)c["label"] == request.Target), $"wallpaper '{request.Target}'");
            yield return GameUi.Click(card);
            var apply = GameUi.One(GameUi.Controls(ui.Read("DesktopCustomizationWindow")).Where(c => (string?)c["name"] == "Apply" && (string?)c["role"] == "button"), "Wallpaper Apply button");
            if (apply["blockedReason"] != null) throw new AgentError("game_ui_mismatch", "Wallpaper Apply remained disabled after selecting the exact card; no apply action was dispatched.");
            yield return GameUi.Click(apply);
            yield break;
        }
        if (request.Command == "game.statistics-read")
        {
            pausedForNotificationSettings = false;
            foreach (var step in OpenPauseWindow("StatisticsWindow", "Statistics")) yield return step;
            yield break;
        }
        if (request.Command is "game.company-read" or "game.company-rename" or "game.company-set")
        {
            pausedForNotificationSettings = false;
            companyEditorInitiallyOpen = GameUi.Controls(ui.Read("CompanyEditWindow")).Length > 0;
            companyPickerInitiallyOpen = GameUi.Controls(ui.Read("ColorPickerWindow")).Length > 0;
            if (companyPickerInitiallyOpen && !companyEditorInitiallyOpen) throw new AgentError("not_interactable", "A Color Picker unrelated to an open Company editor is already visible. It was left untouched.");
            if (request.Command != "game.company-read" && (companyEditorInitiallyOpen || companyPickerInitiallyOpen)) throw new AgentError("open_draft", "Company editor or Color Picker was already open. Its player-owned draft was left untouched; close or cancel it before committing company changes.");
            companyOpenedPauseMenu = !companyEditorInitiallyOpen && !companyPickerInitiallyOpen && !GameUi.Controls(ui.Read("DESKTOP -> Bottom")).Any(c => (string?)c["role"] == "button" && (string?)c["name"] == "Continue");
            foreach (var step in OpenPauseWindow("CompanyEditWindow", "Company")) yield return step;
            if (request.Command == "game.company-read")
            {
                foreach (var step in CaptureCompanyEditor(!companyEditorInitiallyOpen, companyPickerInitiallyOpen)) yield return step;
                foreach (var step in CleanupCompanyPauseMenu()) yield return step;
                yield break;
            }
            var company = Input("CompanyEditWindow", "Company");
            var founder = Input("CompanyEditWindow", "Founder");
            foreach (var pair in new[] { (Node: company, Value: request.Parameters?["companyName"]), (Node: founder, Value: request.Parameters?["founderName"]) })
            {
                if (pair.Value == null) continue;
                var limit = pair.Node["range"]?["characterLimit"]?.Value<int>() ?? 0;
                if (limit > 0 && pair.Value.Value<string>()!.Length > limit) throw new AgentError("invalid_value", $"{pair.Node["name"]} exceeds the native {limit}-character limit; no further input was dispatched.");
                if ((string?)pair.Node["value"] != pair.Value.Value<string>()) yield return GameUi.Set(pair.Node, pair.Value);
            }
            if (request.Parameters?["companyColorHex"] is JToken requestedColor)
            {
                var normalized = NormalizeHex(requestedColor.Value<string>()!);
                yield return GameUi.Click(GameUi.One(GameUi.Controls(ui.Read("CompanyEditWindow")).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Open Color Picker"), "Company Color button"));
                var hex = ColorHexInput();
                if (!string.Equals((string?)hex["value"], normalized, StringComparison.OrdinalIgnoreCase)) yield return GameUi.Set(hex, new JValue(normalized));
                hex = ColorHexInput();
                if (!string.Equals((string?)hex["value"], normalized, StringComparison.OrdinalIgnoreCase)) throw new AgentError("value_not_applied", "Native Color Picker did not retain companyColorHex; Company Confirm was not dispatched.");
                yield return GameUi.Click(ColorPickerButton("Apply", "/Background/ColorPicker"));
            }
            var confirm = GameUi.One(GameUi.Controls(ui.Read("CompanyEditWindow")).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Confirm"), "Company Confirm button");
            yield return GameUi.Click(confirm);
            if (GameUi.Controls(ui.Read("CompanyEditWindow")).Length > 0) throw new AgentError("partial_failure", "Native Company Confirm was dispatched, but the editor remained open. Read back before retrying.");
            foreach (var step in OpenPauseWindow("CompanyEditWindow", "Company")) yield return step;
            foreach (var step in CaptureCompanyEditor(true, false)) yield return step;
            foreach (var step in CleanupCompanyPauseMenu()) yield return step;
            yield break;
        }
        if (request.Command is "game.notification-companies" or "game.notification-company")
        {
            pausedForNotificationSettings = false;
            foreach (var step in OpenNotificationSettings()) yield return step;
            if (request.Command == "game.notification-companies") yield break;
            var toggles = GameUi.Controls(ui.Catalog("NotificationSettingsWindow")).Where(c => (string?)c["name"] == "Company Notification Option Variant" && (string?)c["role"] == "toggle" && (string?)c["label"] == request.Target).ToArray();
            var toggle = GameUi.One(toggles, $"company notification '{request.Target}'");
            if (toggle["value"]!.Value<bool>() != request.Parameters!["enabled"]!.Value<bool>()) yield return GameUi.Set(toggle, request.Parameters["enabled"]!);
            yield break;
        }
        foreach (var step in OpenEmail()) yield return step;
        if (request.Command == "game.email-list" && request.Parameters?["category"] is JToken category)
        {
            var filter = GameUi.One(GameUi.Controls(ui.Read(EmailScope)).Where(c => (string?)c["name"] == "Email Filter Dropdown" && (string?)c["role"] == "select"), "Email Filter Dropdown");
            var options = filter["options"]!.Values<string>().ToArray();
            if (options.Count(o => o == category.Value<string>()) != 1) throw new AgentError("invalid_value", $"Unknown email category. Available: {string.Join(", ", options)}.");
            if (options[filter["value"]!.Value<int>()] != category.Value<string>()) yield return GameUi.Select(filter, category.Value<string>()!);
        }
        if (request.Command == "game.email-list") yield break;
        var matches = EmailCards(ui.Catalog(EmailScope)).Where(card => (string?)card["key"] == request.Target).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No email named '{request.Target}'. Run game email-list and use an exact key.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several emails share '{request.Target}'. Reread the list; no message was opened.");
        selectedTitle = (string)matches[0]["title"]!;
        selectedDate = (string)matches[0]["date"]!;
        yield return GameUi.Click((JObject)matches[0]["control"]!);
    }

    public JObject Result(Request request)
    {
        var result = request.Command switch
        {
            "game.desktop-read" => Desktop(),
            "game.email-list" => EmailList(),
            "game.email-read" => EmailRead(),
            "game.statistics-read" => Statistics(),
            "game.company-read" or "game.company-rename" or "game.company-set" => Company(request),
            "game.gameplay-read" or "game.gameplay-set" => Gameplay(request),
            "game.wallpaper-list" or "game.wallpaper-set" => Wallpapers(request),
            _ => Notifications(request)
        };
        log("/" + request.Command.Substring(5) + " · read");
        return result;
    }

    private IEnumerable<Request> OpenEmail()
    {
        if (GameUi.Controls(ui.Read(EmailScope)).Length > 0) yield break;
        var desktop = ui.Read("DesktopButtons");
        var button = GameUi.One(GameUi.Controls(desktop).Where(c => (string?)c["name"] == "Email Desktop" && (string?)c["role"] == "button"), "Email Desktop button");
        yield return GameUi.Click(button);
        if (GameUi.Controls(ui.Read(EmailScope)).Length == 0) throw new AgentError("game_ui_mismatch", "EmailWindow did not become visible after the native Email action.");
    }

    private IEnumerable<Request> OpenNotificationSettings()
    {
        if (GameUi.Controls(ui.Read("NotificationSettingsWindow")).Length > 0) yield break;
        var bottom = ui.Read("DESKTOP -> Bottom");
        var settings = GameUi.Controls(bottom).SingleOrDefault(c => (string?)c["name"] == "Notification Settings" && (string?)c["role"] == "button");
        if (settings == null)
        {
            var door = GameUi.One(GameUi.Controls(bottom).Where(c => (string?)c["name"] == "Door" && (string?)c["role"] == "button"), "native Pause Menu button");
            yield return GameUi.Click(door);
            pausedForNotificationSettings = true;
            bottom = ui.Read("DESKTOP -> Bottom");
            settings = GameUi.One(GameUi.Controls(bottom).Where(c => (string?)c["name"] == "Notification Settings" && (string?)c["role"] == "button"), "Notification Settings button");
        }
        yield return GameUi.Click(settings);
        if (GameUi.Controls(ui.Read("NotificationSettingsWindow")).Length == 0) throw new AgentError("game_ui_mismatch", "Notification Settings did not become visible after its native action.");
    }

    private IEnumerable<Request> OpenPauseWindow(string scope, string buttonName)
    {
        if (GameUi.Controls(ui.Read(scope)).Length > 0) yield break;
        var bottom = ui.Read("DESKTOP -> Bottom");
        var button = GameUi.Controls(bottom).SingleOrDefault(c => (string?)c["name"] == buttonName && (string?)c["role"] == "button");
        if (button == null)
        {
            var door = GameUi.One(GameUi.Controls(bottom).Where(c => (string?)c["name"] == "Door" && (string?)c["role"] == "button"), "native Pause Menu button");
            yield return GameUi.Click(door);
            pausedForNotificationSettings = true;
            bottom = ui.Read("DESKTOP -> Bottom");
            button = GameUi.One(GameUi.Controls(bottom).Where(c => (string?)c["name"] == buttonName && (string?)c["role"] == "button"), buttonName + " button");
        }
        yield return GameUi.Click(button);
        if (GameUi.Controls(ui.Read(scope)).Length == 0) throw new AgentError("game_ui_mismatch", $"{scope} did not become visible after the native {buttonName} action.");
    }

    private JObject Input(string scope, string keyword)
    {
        return GameUi.One(GameUi.Controls(ui.Read(scope)).Where(c => (string?)c["role"] == "input" && ((string?)c["name"] ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0), keyword + " input");
    }

    private IEnumerable<Request> CaptureCompanyEditor(bool closeEditor, bool pickerInitiallyOpen)
    {
        var company = Input("CompanyEditWindow", "Company");
        var founder = Input("CompanyEditWindow", "Founder");
        companyNameReadback = (string?)company["value"];
        founderNameReadback = (string?)founder["value"];
        companyLimits = new JObject { ["companyName"] = company["range"]?["characterLimit"]?.DeepClone(), ["founderName"] = founder["range"]?["characterLimit"]?.DeepClone(), ["companyColorHex"] = 6 };
        if (!pickerInitiallyOpen) yield return GameUi.Click(GameUi.One(GameUi.Controls(ui.Read("CompanyEditWindow")).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Open Color Picker"), "Company Color button"));
        var rawColor = (string?)ColorHexInput()["value"] ?? "";
        if (!ValidHex(rawColor)) throw new AgentError("game_ui_mismatch", $"Native Color Picker exposed an invalid HEX value '{rawColor}'. Nothing was confirmed.");
        companyColorReadback = NormalizeHex(rawColor);
        if (!pickerInitiallyOpen) yield return GameUi.Click(ColorPickerButton("Close", "/TopBar"));
        if (closeEditor)
        {
            yield return GameUi.Click(GameUi.One(GameUi.Controls(ui.Read("CompanyEditWindow")).Where(c => (string?)c["role"] == "button" && (string?)c["name"] == "Cancel"), "Company Cancel button"));
            companyEditorLeftOpen = false;
        }
        else companyEditorLeftOpen = true;
    }

    private JObject ColorHexInput() => GameUi.One(GameUi.Controls(ui.Read("ColorPickerWindow")).Where(c => (string?)c["role"] == "input" && (string?)c["name"] == "Hex Input"), "Color Picker HEX input");
    private JObject ColorPickerButton(string label, string contextSuffix) => GameUi.One(GameUi.Controls(ui.Read("ColorPickerWindow")).Where(c => (string?)c["role"] == "button" && (string?)c["label"] == label && ((string?)c["context"] ?? "").EndsWith(contextSuffix, StringComparison.Ordinal)), $"Color Picker {label} button");

    private IEnumerable<Request> CleanupCompanyPauseMenu()
    {
        if (!companyOpenedPauseMenu || companyEditorInitiallyOpen || companyPickerInitiallyOpen) yield break;
        yield return GameUi.NativeStep("Keep game time paused before closing agent-opened Pause Menu", TimeAdvanceController.SafePause);
        var bottom = ui.Read("DESKTOP -> Bottom");
        var continueButton = GameUi.Controls(bottom).SingleOrDefault(c => (string?)c["role"] == "button" && (string?)c["name"] == "Continue");
        if (continueButton == null) yield break;
        yield return GameUi.Click(continueButton);
        companyPauseCleanupVerified = (bool?)TimeAdvanceController.ReadClock()["paused"] == true && !GameUi.Controls(ui.Read("DESKTOP -> Bottom")).Any(c => (string?)c["role"] == "button" && (string?)c["name"] == "Continue");
    }

    private JObject Desktop()
    {
        var desktop = ui.Catalog("DesktopButtons");
        var main = ui.Catalog("DESKTOP -> Main");
        var apps = new JArray();
        foreach (var node in GameUi.Controls(desktop).Where(c => (string?)c["role"] == "button" && ((string?)c["name"] ?? "").EndsWith(" Desktop", StringComparison.Ordinal)))
        {
            apps.Add(new JObject
            {
                ["name"] = node["name"]!.DeepClone(), ["label"] = node["label"]!.DeepClone(),
                ["available"] = node["blockedReason"] == null, ["unavailableReason"] = node["blockedReason"]?.DeepClone()
            });
        }
        var status = new JObject();
        foreach (var text in GameUi.Texts(main))
        {
            var context = (string?)text["context"] ?? "";
            var key = context.Contains("DateAndTimeControl") ? "date" : context.Contains("/MoneyAndBalance/Money") ? "money" : context.Contains("/MoneyAndBalance/Balance") ? "balance" : context.EndsWith("/TopBar", StringComparison.Ordinal) && ((string?)text["text"] ?? "").Contains("Market Share") ? "marketShare" : null;
            if (key != null && !status.ContainsKey(key)) status[key] = text["text"]!.DeepClone();
        }
        var unavailable = new JObject();
        foreach (var name in new[] { "Bank Desktop", "Manage Employees Desktop", "Marketing Desktop", "Browser Desktop" })
        {
            var app = apps.OfType<JObject>().SingleOrDefault(a => (string?)a["name"] == name);
            unavailable[name] = app == null ? "not_exposed_in_current_desktop" : (bool)app["available"]! ? "available_now" : (string?)app["unavailableReason"] ?? "disabled_by_game";
        }
        return Base("desktop", new JObject
        {
            ["status"] = status, ["finances"] = BalanceSnapshot.Read(), ["apps"] = apps, ["notableAvailability"] = unavailable,
            ["help"] = new JObject { ["available"] = false, ["reason"] = "No native Help desktop control or Help window is evidenced in this game build." },
            ["catalogScope"] = "active-player-browsable"
        });
    }

    private JObject EmailList()
    {
        var view = ui.Catalog(EmailScope);
        var cards = EmailCards(view);
        var emails = new JArray(cards.Select(card => new JObject
        {
            ["key"] = card["key"]!.DeepClone(), ["title"] = card["title"]!.DeepClone(), ["date"] = card["date"]!.DeepClone(),
            ["readState"] = "not_exposed"
        }));
        var filter = GameUi.One(GameUi.Controls(view).Where(c => (string?)c["name"] == "Email Filter Dropdown" && (string?)c["role"] == "select"), "Email Filter Dropdown");
        return Base("email-list", new JObject
        {
            ["emails"] = emails, ["count"] = emails.Count, ["complete"] = !((bool?)view["more"] ?? false),
            ["filter"] = new JObject { ["value"] = filter["options"]![filter["value"]!.Value<int>()]!.DeepClone(), ["options"] = filter["options"]!.DeepClone(), ["blockedReason"] = filter["blockedReason"]?.DeepClone() },
            ["nativeEffect"] = "Opening Email can natively mark all messages read; unread state is not exposed by this API.",
            ["next"] = "Use game email-read EXACT_KEY from this list."
        });
    }

    private JObject EmailRead()
    {
        var view = ui.Catalog(EmailScope);
        var panel = GameUi.Texts(view).SingleOrDefault(t => ((string?)t["context"] ?? "").Contains("EmailWindow/Background/BackgroundRight/Texts"));
        var fields = panel == null ? Array.Empty<string>() : MessageFields((string)panel["text"]!);
        if (fields.Length < 3 || fields[0] != selectedTitle) throw new AgentError("game_ui_mismatch", "The selected native email fields were not exposed unambiguously. Use Generic UI; no hidden message data was read.");
        return Base("email", new JObject
        {
            ["key"] = EmailKey(selectedTitle!, selectedDate!), ["title"] = selectedTitle, ["date"] = selectedDate,
            ["author"] = fields[1], ["body"] = fields[2],
            ["nativeEffect"] = "Selected the native message card; the game marks that message read."
        });
    }

    private JObject Notifications(Request request)
    {
        var view = ui.Catalog("NotificationSettingsWindow");
        var companies = new JArray(GameUi.Controls(view).Where(c => (string?)c["name"] == "Company Notification Option Variant" && (string?)c["role"] == "toggle").Select(c => new JObject
        {
            ["company"] = c["label"]!.DeepClone(), ["enabled"] = c["value"]!.DeepClone(), ["blockedReason"] = c["blockedReason"]?.DeepClone()
        }));
        if (request.Command == "game.notification-company")
        {
            var actual = companies.OfType<JObject>().SingleOrDefault(c => (string?)c["company"] == request.Target);
            if (actual == null || (bool?)actual["enabled"] != request.Parameters!["enabled"]!.Value<bool>()) throw new AgentError("value_not_applied", "The native company notification value did not match after the action.");
        }
        return Base("company-notifications", new JObject
        {
            ["companies"] = companies, ["complete"] = !((bool?)view["more"] ?? false),
            ["nativeEffect"] = pausedForNotificationSettings ? "Opened Pause Menu and paused game time; Notification Settings and Pause Menu remain open." : "Reused open Notification Settings; game-time state was not changed by this command.",
            ["next"] = "Use game notification-company EXACT_COMPANY --enabled true|false."
        });
    }

    private JObject Statistics()
    {
        var view = ui.Catalog("StatisticsWindow");
        var identity = new JObject();
        var values = new JObject();
        foreach (var text in GameUi.Texts(view).Where(t => !((string?)t["context"] ?? "").Contains("/TopBar")))
        {
            var context = (string?)text["context"] ?? "";
            var content = (string?)text["text"] ?? "";
            var cells = Split(content);
            if (context.EndsWith("/CompanyTexts", StringComparison.Ordinal) && cells.Length >= 2)
            {
                identity["company"] = cells[0];
                identity["founder"] = cells[1];
                continue;
            }
            var key = StatisticKey(context);
            if (key != null && cells.Length >= 2) values[key] = cells[cells.Length - 1];
        }
        return Base("statistics", new JObject
        {
            ["identity"] = identity, ["values"] = values,
            ["nativeEffect"] = PauseEffect("Statistics"), ["precision"] = "native-display"
        });
    }

    private JObject Company(Request request)
    {
        var committed = request.Command != "game.company-read";
        if (request.Parameters?["companyName"] is JToken expectedCompany && companyNameReadback != expectedCompany.Value<string>()) throw new AgentError("value_not_applied", $"Native Company editor reads '{companyNameReadback}' after Confirm, not requested companyName.");
        if (request.Parameters?["founderName"] is JToken expectedFounder && founderNameReadback != expectedFounder.Value<string>()) throw new AgentError("value_not_applied", $"Native Company editor reads '{founderNameReadback}' after Confirm, not requested founderName.");
        if (request.Parameters?["companyColorHex"] is JToken expectedColor && !string.Equals(companyColorReadback, NormalizeHex(expectedColor.Value<string>()!), StringComparison.OrdinalIgnoreCase)) throw new AgentError("value_not_applied", $"Native Color Picker reads '{companyColorReadback}' after Confirm, not requested companyColorHex.");
        return Base("company", new JObject
        {
            ["companyName"] = companyNameReadback, ["founderName"] = founderNameReadback, ["companyColorHex"] = companyColorReadback == null ? null : "#" + companyColorReadback,
            ["limits"] = companyLimits, ["committed"] = committed, ["colorChanged"] = committed && request.Parameters?["companyColorHex"] != null,
            ["editorInitiallyOpen"] = companyEditorInitiallyOpen, ["editorLeftOpen"] = companyEditorLeftOpen, ["readback"] = committed ? "reopened_native_editor_and_color_picker" : companyEditorInitiallyOpen ? "preexisting_visible_draft" : "fresh_native_editor",
            ["pauseMenuOpenedByCommand"] = companyOpenedPauseMenu, ["pauseMenuCleanupVerified"] = companyOpenedPauseMenu ? new JValue(companyPauseCleanupVerified) : JValue.CreateNull(), ["clockPausedAfterCleanup"] = companyOpenedPauseMenu ? new JValue(companyPauseCleanupVerified) : JValue.CreateNull(),
            ["nativeEffect"] = CompanyEffect()
        });
    }

    private string CompanyEffect()
    {
        if (companyEditorLeftOpen) return companyPickerInitiallyOpen ? "A preexisting player Company editor and Color Picker draft was read and left open; no Confirm, Apply, Cancel or Close action was dispatched." : "A preexisting player Company editor draft was read and left open; an agent-opened Color Picker was closed without Apply, and no Company Confirm/Cancel was dispatched.";
        if (companyOpenedPauseMenu) return companyPauseCleanupVerified ? "Agent-opened Color Picker and Company editor were closed through native Close/Cancel; time was reset to pause before native Continue closed the agent-opened Pause Menu, and the paused clock was verified." : "Agent-opened Company UI was closed, but scoped Pause Menu cleanup was not fully verified; game time was not intentionally resumed.";
        return "Agent-opened Color Picker and Company editor were closed through native Close/Cancel; the preexisting Pause Menu state was preserved and no pause/resume action was dispatched.";
    }

    private JObject Gameplay(Request request)
    {
        var view = ui.Catalog("GameplaySettingsWindow");
        var dropdown = GameUi.One(GameUi.Controls(view).Where(c => (string?)c["name"] == "Difficulty Dropdown" && (string?)c["role"] == "select"), "Difficulty Dropdown");
        var options = dropdown["options"]!.Values<string>().ToArray();
        var current = options[dropdown["value"]!.Value<int>()];
        if (requestedDifficulty != null && current != requestedDifficulty) throw new AgentError("value_not_applied", $"Native difficulty is '{current}' after selecting '{requestedDifficulty}'.");
        var ai = GameUi.Texts(view).SingleOrDefault(t => ((string?)t["context"] ?? "").EndsWith("/Texts/AI", StringComparison.Ordinal));
        var aiFields = ai == null ? Array.Empty<string>() : Split((string)ai["text"]!);
        return Base("gameplay-settings", new JObject
        {
            ["difficulty"] = current, ["difficultyIndex"] = dropdown["value"]!.DeepClone(), ["options"] = dropdown["options"]!.DeepClone(),
            ["aiBehavior"] = aiFields.LastOrDefault(), ["changed"] = request.Command == "game.gameplay-set" && difficultyChanged,
            ["nativeEffect"] = PauseEffect("Gameplay Settings")
        });
    }

    private JObject Wallpapers(Request request)
    {
        var view = ui.Catalog("DesktopCustomizationWindow");
        var cards = WallpaperCards(view);
        var options = new JArray(cards.Select(card => card["label"]!.DeepClone()));
        if (requestedWallpaper != null && !cards.Any(card => (string?)card["label"] == requestedWallpaper)) throw new AgentError("value_not_applied", "The applied wallpaper card is no longer present in the native catalog.");
        return Base("wallpapers", new JObject
        {
            ["options"] = options, ["count"] = options.Count, ["complete"] = !((bool?)view["more"] ?? false),
            ["current"] = null, ["currentEvidence"] = "not_semantically_exposed", ["requested"] = requestedWallpaper,
            ["selectedOption"] = requestedWallpaper, ["outcome"] = request.Command == "game.wallpaper-set" ? "input_dispatched" : "read",
            ["applyDispatched"] = request.Command == "game.wallpaper-set", ["nativeEffect"] = PauseEffect("Desktop Customization"),
            ["verification"] = requestedWallpaper == null ? null : "The exact card remained cataloged after the native Apply action. This UI exposes no semantic current-wallpaper label for independent visual readback."
        });
    }

    private static JObject[] WallpaperCards(JObject view)
    {
        return GameUi.Controls(view).Where(c => (string?)c["name"] == "Wallpaper Button Variant" && c["actions"]!.Values<string>().Contains("click") && !string.IsNullOrWhiteSpace((string?)c["label"])).ToArray();
    }

    private string PauseEffect(string window)
    {
        return pausedForNotificationSettings ? $"Opened Pause Menu and paused game time; {window} remains open unless its native Confirm closed it." : $"Opened or reused {window} through its currently exposed native control; no pause/resume action was dispatched.";
    }

    private static JObject[] EmailCards(JObject view)
    {
        var texts = GameUi.Texts(view);
        var cards = new List<JObject>();
        foreach (var node in GameUi.Controls(view).Where(c => c["group"] != null && c["actions"]!.Values<string>().Contains("click")))
        {
            var group = (string)node["group"]!;
            var content = texts.Where(t => (string?)t["group"] == group).Select(t => (string)t["text"]!).ToArray();
            if (content.Length == 0) continue;
            var cells = content.SelectMany(Split).ToArray();
            var date = cells.LastOrDefault(IsDate);
            if (date == null) continue;
            var title = string.Join(" | ", cells.Where(c => c != date && c.Length > 0));
            if (title.Length == 0) continue;
            cards.Add(new JObject { ["key"] = EmailKey(title, date), ["title"] = title, ["date"] = date, ["group"] = group, ["control"] = node });
        }
        return cards.ToArray();
    }

    private static bool IsDate(string text) => Regex.IsMatch(text, @"^(?:\d{1,4}[./-]){2}\d{1,4}$") || Regex.IsMatch(text, @"^[A-Za-z]{3,9}\s+\d{1,2},?\s+\d{4}$");
    private static bool ValidHex(string value)
    {
        var text = value.StartsWith("#", StringComparison.Ordinal) ? value.Substring(1) : value;
        return text.Length == 6 && text.All(Uri.IsHexDigit);
    }
    private static string NormalizeHex(string value) => value.TrimStart('#').ToUpperInvariant();
    private static string[] Split(string text) => text.Split('|').Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
    private static string[] MessageFields(string text)
    {
        const string separator = " | ";
        var first = text.IndexOf(separator, StringComparison.Ordinal);
        var second = first < 0 ? -1 : text.IndexOf(separator, first + separator.Length, StringComparison.Ordinal);
        return first < 0 || second < 0 ? Array.Empty<string>() : new[] { text.Substring(0, first).Trim(), text.Substring(first + separator.Length, second - first - separator.Length).Trim(), text.Substring(second + separator.Length).Trim() };
    }
    private static string EmailKey(string title, string date) => title + " — " + date;
    private static string? StatisticKey(string context)
    {
        var leaf = context.Substring(context.LastIndexOf('/') + 1);
        return leaf switch
        {
            "ProductsReleased" => "productsReleased",
            "TotaSales" => "totalSales",
            "TotalProfit" => "totalProfit",
            "TotalIncome" => "totalIncome",
            "TotalExpenses" => "totalExpenses",
            "Taxes" => "taxes",
            "Frequency" => "frequencyModifier",
            "Consumption" => "consumptionModifier",
            "Density" => "densityModifier",
            _ => null
        };
    }
    private static JObject Base(string kind, JObject content)
    {
        content.AddFirst(new JProperty("kind", kind));
        content.AddFirst(new JProperty("method", "game"));
        content["precision"] = "native-display";
        return content;
    }
}
