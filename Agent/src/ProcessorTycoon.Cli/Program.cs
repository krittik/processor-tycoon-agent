using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
try { return await Run(args); }
catch (Exception error) when (error is ArgumentException or IOException or HttpRequestException or TaskCanceledException or JsonException or FormatException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { code = "cli_error", message = error.Message }, next = "Use pt-agent guide. If a mutation lost its response, do not blindly repeat it." }));
    return 2;
}
catch (Exception error)
{
    // Last resort: an unexpected reply shape (for example while the game shuts down) still yields one JSON result on stdout.
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { code = "internal_error", type = error.GetType().Name, message = error.Message }, next = "The CLI hit an unexpected response shape. The outcome of a mutation may be unknown: check status and the game state before repeating anything." }));
    return 2;
}

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        AgentPrompt.PrintOnce(await GameHost.Status());
        Console.WriteLine($$"""
        Processor Tycoon Agent {{AgentInfo.Short}} — start here

          pt-agent status          Diagnose installation, process, bridge and campaign (also works offline).
          pt-agent launch          Start the visible game if needed; never starts/loads a campaign or a duplicate client.
          pt-agent quit [--save NAME]   Exit the game client WITHOUT saving (or save as NAME first); refuses while a dialog is open.
          pt-agent guide           Full gameplay/API reference, available offline.
          pt-agent prompt          Print the game description and agent prompt.
          pt-agent capabilities    Live command inventory (requires the game bridge).
          pt-agent notifications --since 0 --limit 20 [--type competitor_cpu_released]   Replay recent native popup messages.
          pt-agent game products-pulse --compact   All products + finance in one read (lines, demand, production, stock).
          pt-agent game window-tidy                Close stale read-only views (never drafts or dialogs).
          pt-agent game watch-advance --all-products --days 30 --demand-down-percent 20   Guarded day-by-day advance (or --products A,B / one NAME).
                   add --portfolio-guards, --stop-on-competitor-price-cut-percent 10, --stop-on-expansion-complete, --monthly-digest
          pt-agent game situation --compact        Company health, markets (Desktop/Mobile/Industries), rival moves, flags.
          pt-agent game market-changes --compact   Rival price changes, leader changes and recommended-price moves since last call.
          pt-agent game product-price NAME --price P --dry-run   Pre-commit warnings (drop --dry-run to commit); price-history / price-revert NAME.
          pt-agent game plan-apply --file plan.json [--dry-run]   Several line/price changes with readback (lines: N, max or add).
          pt-agent game monthly-digest | cleanup-preview | research-funding-compare --funding-percents 25,50,100
          pt-agent mp status | host [PORT] | join IP:PORT | resume [SESSION] | leave | chat TEXT | chat-read   Multiplayer mod (if installed).
          pt-agent companion start NAME --explicit-user-request [--company N] [--company-type cpu|fabless|foundry] [--windowed] | list | stop NAME
                                                   Your own headless game copy that joins the session the user hosts (ask first).

        First read status.game.state and status.game.next:
          not_running -> launch; main_menu -> game session-new-preview OR game save-list / save-load.
          campaign -> game desktop-read for money/date, then relevant game commands.
          bridge_unavailable -> process exists but mod is not responding; do not start another game.

        Prefer Game API: semantic targets, units, complete catalogs and verified action results.
        Generic (observe / ui) is verbose discovery/fallback for modified or unmapped UI, not ordinary play.
        Reads may open native windows. Run gameplay commands sequentially. Use screenshot for visual checks.
        Respect paused_by_user; never self-resume or change action delay without an explicit user request.
        The first live CLI connection in each game process prints the agent prompt to stderr before the command response.
        Full guide: pt-agent guide (also printed by a specific command's --help).
        Source, releases and issues: {{AgentInfo.Repository}}  (MIT license, unofficial fan mod)
        """);
        return 0;
    }
    if (args[0] == "prompt")
    {
        AgentPrompt.PrintOnce(await GameHost.Status(), TextWriter.Null);
        Console.WriteLine(AgentPrompt.Text);
        return 0;
    }
    if (args[0] == "guide" || args[^1] is "--help" or "-h")
    {
        AgentPrompt.PrintOnce(await GameHost.Status());
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentGuide");
        using var reader = new StreamReader(stream!);
        Console.Write(await reader.ReadToEndAsync());
        return 0;
    }
    // Boolean composition options may be written bare (--all-products) or with an explicit true/false.
    var bareBooleans = new[] { "--all-products", "--keep-windows", "--keep-open", "--dry-run", "--skip-checks", "--portfolio-guards", "--monthly-digest", "--clamp", "--rollback-on-failure", "--stop-on-expansion-complete", "--close-designer", "--include-same-company-leads" };
    args = args.SelectMany((arg, i) => bareBooleans.Contains(arg) && (i + 1 >= args.Length || args[i + 1] is not ("true" or "false" or "True" or "False")) ? new[] { arg, "true" } : new[] { arg }).ToArray();
    var positional = new List<string>();
    var options = new Dictionary<string, string>();
    var flags = new HashSet<string>();
    var flagNames = new[] { "hidden", "explicit-user-request", "no-wait", "changes", "compact", "windowed" };
    var compact = args.Contains("--compact");
    var gameOptions = new Dictionary<string, (string Key, string Type)>
    {
        ["name"] = ("name", "string"), ["core-count"] = ("coreCount", "string"), ["memory-controller"] = ("memoryController", "string"),
        ["days"] = ("days", "integer"), ["target-date"] = ("targetDate", "string"), ["speed"] = ("speed", "integer"),
        ["demand-up-percent"] = ("demandUpPercent", "number"), ["demand-down-percent"] = ("demandDownPercent", "number"),
        ["min-demand-to-production"] = ("minDemandToProduction", "number"), ["max-demand-to-production"] = ("maxDemandToProduction", "number"),
        ["stock-above"] = ("stockAbove", "number"), ["credit-below"] = ("creditBelow", "number"), ["balance-below"] = ("balanceBelow", "number"),
        ["stop-on-competitor-release"] = ("stopOnCompetitorRelease", "boolean"), ["stop-on-default"] = ("stopOnDefault", "boolean"),
        ["max-days"] = ("maxDays", "integer"), ["include-production"] = ("includeProduction", "boolean"),
        ["show-obsolete"] = ("showObsolete", "boolean"), ["licensed"] = ("licensed", "boolean"),
        ["wafer-size"] = ("waferSize", "string"),
        ["pins"] = ("pins", "integer"), ["priority"] = ("priority", "string"), ["investment-percent"] = ("investmentPercent", "number"),
        ["frequency-mhz"] = ("frequencyMHz", "number"), ["die-size-mm2"] = ("dieSizeMm2", "number"),
        ["l1-cache-kb"] = ("l1CacheKB", "number"), ["l2-cache-kb"] = ("l2CacheKB", "number"), ["l3-cache-kb"] = ("l3CacheKB", "number"),
        ["overclock"] = ("overclock", "boolean"), ["smt"] = ("smt", "boolean"), ["automate-cache"] = ("automateCache", "boolean"),
        ["price"] = ("price", "integer"), ["lines"] = ("lines", "integer"), ["enabled"] = ("enabled", "boolean"),
        ["automation"] = ("automation", "boolean"), ["foundry-services"] = ("foundryServices", "boolean"), ["upgrade-wafer"] = ("upgradeWafer", "boolean"), ["upgrade-lines"] = ("upgradeWafer", "boolean"),
        ["products"] = ("products", "string"), ["all-products"] = ("allProducts", "boolean"), ["keep-windows"] = ("keepWindows", "boolean"), ["keep-open"] = ("keepOpen", "boolean"),
        ["retire"] = ("retire", "boolean"), ["sell-on-market"] = ("sellOnMarket", "boolean"), ["available-for-contracts"] = ("availableForContracts", "boolean"),
        ["market"] = ("market", "string"), ["show-retired"] = ("showRetired", "boolean"), ["variant"] = ("variant", "integer"),
        ["target-market"] = ("targetMarket", "string"), ["planned-price"] = ("plannedPrice", "integer"), ["die-sizes"] = ("dieSizes", "string"), ["frequencies"] = ("frequencies", "string"),
        ["review-id"] = ("reviewId", "string"), ["decision-reason"] = ("decisionReason", "string"), ["accept-missing-evidence"] = ("acceptMissingEvidence", "boolean"), ["acknowledge-risks"] = ("acknowledgeRisks", "string"),
        ["sort-by"] = ("sortBy", "string"), ["scroll"] = ("scroll", "number"), ["status"] = ("status", "string"), ["query"] = ("query", "string"),
        ["funding-percent"] = ("fundingPercent", "integer"), ["innovation-effort"] = ("innovationEffort", "boolean"),
        ["budget"] = ("budget", "string"),
        ["history"] = ("history", "boolean"),
        ["server"] = ("server", "boolean"),
        ["company"] = ("company", "string"), ["cpu"] = ("cpu", "string"), ["type"] = ("type", "string"), ["filter"] = ("filter", "string"),
        ["view"] = ("view", "string"), ["match"] = ("match", "string"), ["eligible-only"] = ("eligibleOnly", "boolean"), ["confirm"] = ("confirm", "boolean"),
        ["player-role"] = ("playerRole", "string"), ["duration-years"] = ("durationYears", "integer"), ["production-percent"] = ("productionPercent", "integer"),
        ["markup-percent"] = ("markupPercent", "integer"), ["exclusivity"] = ("exclusivity", "boolean"), ["renew"] = ("renew", "boolean"),
        ["highlight-player"] = ("highlightPlayer", "boolean"), ["search"] = ("search", "string"), ["period"] = ("period", "string"),
        ["desktop"] = ("desktop", "boolean"), ["mobile"] = ("mobile", "boolean"), ["industries"] = ("industries", "boolean"), ["contracts"] = ("contracts", "boolean"), ["contract"] = ("contract", "string"),
        ["category"] = ("category", "string"), ["detailed"] = ("detailed", "boolean"), ["yearly"] = ("yearly", "boolean"), ["dialog"] = ("dialog", "string"), ["hide-until-reload"] = ("hideUntilReload", "boolean"),
        ["company-name"] = ("companyName", "string"), ["founder-name"] = ("founderName", "string"), ["company-type"] = ("companyType", "string"),
        ["difficulty"] = ("difficulty", "string"), ["start-date"] = ("startDate", "string"), ["enable-cheats"] = ("enableCheats", "boolean"),
        ["random-competitors"] = ("randomCompetitors", "boolean"), ["company-color-hex"] = ("companyColorHex", "string"),
        ["date-format"] = ("dateFormat", "string"), ["autosave"] = ("autosave", "string"), ["theme"] = ("theme", "string"), ["custom-cursor"] = ("customCursor", "boolean"),
        ["show-execution-times"] = ("showExecutionTimes", "boolean"), ["contract-signed-notifications"] = ("contractSignedNotifications", "boolean"),
        ["contract-terminated-notifications"] = ("contractTerminatedNotifications", "boolean"), ["company-release-notifications"] = ("companyReleaseNotifications", "boolean"),
        ["full-screen-mode"] = ("fullScreenMode", "string"), ["resolution"] = ("resolution", "string"), ["vsync"] = ("vsync", "boolean"), ["msaa"] = ("msaa", "boolean"),
        ["master-volume-percent"] = ("masterVolumePercent", "number"), ["sfx-volume-percent"] = ("sfxVolumePercent", "number"), ["music-volume-percent"] = ("musicVolumePercent", "number"),
        // CLI 0.4 compositions and guards
        ["sales"] = ("sales", "boolean"),
        ["track-competitor-prices"] = ("trackCompetitorPrices", "string"), ["competitor-price-scope"] = ("competitorPriceScope", "string"),
        ["stop-on-competitor-price-cut-percent"] = ("stopOnCompetitorPriceCutPercent", "number"), ["competitor-min-change-percent"] = ("competitorMinChangePercent", "number"),
        ["competitor-price-min-percent"] = ("competitorPriceMinPercent", "number"), ["competitor-price-min-dollars"] = ("competitorPriceMinDollars", "number"), ["include-same-company-leads"] = ("includeSameCompanyLeads", "boolean"),
        ["stock-growth-days"] = ("stockGrowthDays", "integer"), ["idle-demand-below"] = ("idleDemandBelow", "number"), ["idle-demand-ratio-below"] = ("idleDemandRatioBelow", "number"),
        ["stock-months-above"] = ("stockMonthsAbove", "number"), ["any-demand-change-percent"] = ("anyDemandChangePercent", "number"),
        ["stop-on-expansion-complete"] = ("stopOnExpansionComplete", "boolean"), ["monthly-digest"] = ("monthlyDigest", "boolean"), ["portfolio-guards"] = ("portfolioGuards", "boolean"),
        ["dry-run"] = ("dryRun", "boolean"), ["skip-checks"] = ("skipChecks", "boolean"), ["follow-up-days"] = ("followUpDays", "integer"),
        ["add"] = ("add", "integer"), ["clamp"] = ("clamp", "boolean"), ["steps"] = ("steps", "integer"), ["count"] = ("count", "integer"),
        ["core-counts"] = ("coreCounts", "string"), ["funding-percents"] = ("fundingPercents", "string"), ["demand-below"] = ("demandBelow", "number"),
        ["rollback-on-failure"] = ("rollbackOnFailure", "boolean"), ["close-designer"] = ("closeDesigner", "boolean")
    };
    var optionNames = new[] { "value", "scope", "offset", "limit", "endpoint", "timeout", "session", "output", "since", "observe", "json", "file", "save", "company", "company-type" };
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) { positional.Add(args[i]); continue; }
        var key = args[i][2..];
        var equals = key.IndexOf('=');
        if (equals >= 0)
        {
            var option = key[..equals];
            if (!optionNames.Contains(option) && !gameOptions.ContainsKey(option)) throw new ArgumentException($"Unknown option: --{option}");
            if (options.ContainsKey(option)) throw new ArgumentException($"Duplicate option: --{option}");
            options[option] = key[(equals + 1)..]; continue;
        }
        if (flagNames.Contains(key)) { flags.Add(key); continue; }
        if ((!optionNames.Contains(key) && !gameOptions.ContainsKey(key)) || i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException($"Unknown option or missing value: --{key}");
        if (options.ContainsKey(key)) throw new ArgumentException($"Duplicate option: --{key}");
        options[key] = args[++i];
    }
    if (positional.Count == 0) throw new ArgumentException("A command is required. Use help.");
    var command = positional[0];
    var index = 1;
    if (command is "ui" or "agent" or "game" or "mp" or "companion")
    {
        if (positional.Count < 2) throw new ArgumentException("A subcommand is required. Use help.");
        command += "." + positional[index++];
    }
    var target = positional.Count > index ? positional[index++] : "";
    if (positional.Count != index && positional.Count > index) throw new ArgumentException("Unexpected positional arguments. Values must use --value.");
    var timeout = options.TryGetValue("timeout", out var timeoutValue) ? int.Parse(timeoutValue) : 30;
    if (timeout < 1 || timeout > 60) throw new ArgumentException("--timeout must be 1..60 seconds.");
    if ((options.ContainsKey("company") || options.ContainsKey("company-type")) && command is not ("mp.join" or "companion.start")) throw new ArgumentException("--company and --company-type belong to mp join and companion start.");
    if (flags.Contains("windowed") && command != "companion.start") throw new ArgumentException("--windowed belongs to companion start.");
    if (command.StartsWith("companion.", StringComparison.Ordinal))
    {
        var companion = await Companion.Run(command, target, options, flags, timeout);
        Console.WriteLine(Output(companion, compact));
        return companion["ok"]?.GetValue<bool>() == false ? 1 : 0;
    }
    if (command is "quit" or "game.quit")
    {
        if (target.Length > 0 || flags.Count > 0 || options.Keys.Any(k => k is not ("timeout" or "save"))) throw new ArgumentException("quit takes only --save NAME (optional) and --timeout; it always acts on the local game installation (no --endpoint).");
        var quit = await Quit.Run(null, options.GetValueOrDefault("save"), timeout);
        Console.WriteLine(Output(quit, false));
        return quit["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }
    if (options.ContainsKey("save")) throw new ArgumentException("--save is only used by quit (use game save-create NAME to save).");
    if (command is "status" or "launch")
    {
        if (target.Length > 0 || flags.Count > 0 || options.Keys.Any(k => k != "timeout" && k != "endpoint" && (command != "status" || k != "session"))) throw new ArgumentException("status/launch take no target or gameplay parameters; status also accepts --session.");
        if (command == "launch" && options.ContainsKey("endpoint")) throw new ArgumentException("launch starts the local installation and does not accept --endpoint.");
        var diagnostic = command == "launch" ? await GameHost.Launch(timeout) : await GameHost.Status(options.GetValueOrDefault("endpoint"), options.GetValueOrDefault("session", "local-cli"));
        AgentPrompt.PrintOnce(diagnostic);
        Console.WriteLine(diagnostic.ToJsonString());
        return diagnostic["ok"]?.GetValue<bool>() == false ? 1 : 0;
    }
    var request = new JsonObject
    {
        ["command"] = command, ["target"] = target, ["session"] = options.GetValueOrDefault("session", "local-cli"),
        ["scope"] = options.GetValueOrDefault("scope", ""), ["hidden"] = flags.Contains("hidden"),
        ["changes"] = flags.Contains("changes"),
        ["explicitUserRequest"] = flags.Contains("explicit-user-request"),
        ["offset"] = options.TryGetValue("offset", out var offset) ? int.Parse(offset) : 0,
        ["limit"] = options.TryGetValue("limit", out var limit) ? int.Parse(limit) : 60
    };
    if (options.TryGetValue("since", out var since)) request["since"] = long.Parse(since);
    if (options.TryGetValue("observe", out var observe)) request["observeAfter"] = observe;
    if (options.ContainsKey("json") && options.ContainsKey("file")) throw new ArgumentException("Use either --json or --file, not both.");
    if (options.ContainsKey("json") || options.ContainsKey("file"))
    {
        var json = options.TryGetValue("file", out var file) ? file == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(file) : options["json"];
        request["parameters"] = JsonNode.Parse(json.TrimStart('\uFEFF')) as JsonObject ?? throw new ArgumentException("Game parameters must be a JSON object.");
    }
    // notifications filters are applied locally to the replayed list: --type TYPE[,TYPE] and --limit N (most recent N).
    string? notificationType = null;
    if (command == "notifications" && options.Remove("type", out var typeFilter)) notificationType = typeFilter;
    if (command == "notifications" && options.TryGetValue("limit", out var notificationLimit) && (!int.TryParse(notificationLimit, out var parsedLimit) || parsedLimit < 1)) throw new ArgumentException("--limit must be a positive integer.");
    foreach (var option in options.Where(o => gameOptions.ContainsKey(o.Key)))
    {
        if (!command.StartsWith("game.", StringComparison.Ordinal)) throw new ArgumentException($"--{option.Key} is a Game API parameter.");
        var field = gameOptions[option.Key];
        var parameters = request["parameters"] as JsonObject;
        if (parameters == null) request["parameters"] = parameters = new JsonObject();
        if (parameters.ContainsKey(field.Key)) throw new ArgumentException($"Parameter {field.Key} was supplied twice (in JSON and --{option.Key}, or through two alias flags such as --upgrade-lines and --upgrade-wafer); choose one.");
        parameters[field.Key] = field.Type switch
        {
            "boolean" => JsonValue.Create(bool.Parse(option.Value)),
            "integer" when option.Key == "lines" && option.Value == "max" => JsonValue.Create("max"),
            "integer" => JsonValue.Create(long.Parse(option.Value, CultureInfo.InvariantCulture)),
            "number" => JsonValue.Create(double.Parse(option.Value, NumberStyles.Float, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(option.Value)
        };
    }
    if (options.TryGetValue("value", out var value)) request["value"] = value;
    if (command == "mp.join" && (options.ContainsKey("company") || options.ContainsKey("company-type")))
    {
        var companyType = options.GetValueOrDefault("company-type", "cpu") switch { "cpu" => 0, "fabless" => 1, "foundry" => 2, _ => throw new ArgumentException("--company-type is cpu, fabless or foundry.") };
        request["parameters"] = new JsonObject { ["company"] = options.GetValueOrDefault("company", value ?? ""), ["companyType"] = companyType };
    }
    if (command == "wait-until-resumed") request["value"] = timeout;
    var endpoint = options.GetValueOrDefault("endpoint") ?? GameHost.Endpoint(GameHost.FindRoot());
    if (endpoint == null)
    {
        var diagnostic = await GameHost.Status();
        Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "bridge_unavailable", ["message"] = "No live bridge endpoint is available. No command was sent." }, ["status"] = diagnostic, ["next"] = diagnostic["next"]?.DeepClone() }.ToJsonString());
        return 1;
    }
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback) throw new ArgumentException("Endpoint must be a loopback HTTP URL.");
    // The brief check runs in the auxiliary session: a status reply in the caller's session would take the notifications
    // (popups, chat) that belong in this command's reply.
    AgentPrompt.PrintOnce(await GameHost.Status(options.GetValueOrDefault("endpoint"), CliSession.Aux));
    using var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(timeout + 5) };
    // cpu-preview with lists (--die-sizes/--frequencies/--core-counts containing commas) is a design sweep (cpu-variants).
    if (command == "game.cpu-preview" && request["parameters"] is JsonObject sweep && (sweep["dieSizes"] != null || sweep["coreCounts"] != null || sweep["frequencies"] != null))
    {
        command = "game.cpu-variants";
        request["command"] = command;
    }
    if (command is "game.cpu-variants" or "game.market-changes" or "game.situation" or "game.monthly-digest" or "game.research-funding-compare" or "game.cleanup-preview")
    {
        if (target.Length > 0) throw new ArgumentException($"{command} takes no target.");
        if (command is "game.market-changes" or "game.situation" or "game.monthly-digest" && options.Keys.Any(key => key is not ("session" or "endpoint" or "timeout" or "compact") && (command == "game.monthly-digest" || key is not ("competitor-price-min-percent" or "competitor-price-min-dollars" or "include-same-company-leads")))) throw new ArgumentException($"{command} takes no parameters.");
        // Compositions close the read-only views they opened (the last workspace would otherwise stay behind other windows).
        var before = Workspace.Scopes(await Workspace.Visible(client, CliSession.Aux, timeout));
        if (command == "game.cpu-variants") await EnsureCpuDesigner(client, timeout, before);
        var composed = command switch
        {
            "game.cpu-variants" => await GameVariants.Run(client, request, timeout),
            "game.market-changes" => await MarketChanges.Run(client, timeout, request["parameters"] as JsonObject),
            "game.monthly-digest" => WithOk(await Digest.Run(client, "digest-cli", timeout)),
            "game.research-funding-compare" => await ResearchTools.Compare(client, request, timeout),
            "game.cleanup-preview" => await PriceTools.Cleanup(client, request, timeout),
            _ => await GameSituation.Run(client, timeout, request["parameters"] as JsonObject)
        };
        var cleanup = await Workspace.CloseOpenedSince(client, CliSession.Aux, timeout, before);
        if (Workspace.Any(cleanup)) composed["closedWindows"] = cleanup;
        Console.WriteLine(Output(composed, compact));
        return composed["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }
    if (command is "game.product-price" or "game.price-revert" or "game.plan-apply" or "game.price-history")
    {
        if (command == "game.price-history") { var limitValue = options.TryGetValue("limit", out var l) ? int.Parse(l, CultureInfo.InvariantCulture) : (int?)null; Console.WriteLine(Output(PriceTools.History(target, limitValue), compact)); return 0; }
        if (command == "game.plan-apply" && target.Length > 0) throw new ArgumentException("plan-apply takes no target; give the plan with --file PLAN.json or --json.");
        var before = Workspace.Scopes(await Workspace.Visible(client, CliSession.Aux, timeout));
        var composed = command switch
        {
            "game.product-price" => await PriceTools.Price(client, request, timeout),
            "game.price-revert" => await PriceTools.Revert(client, request, timeout),
            _ => await PriceTools.Plan(client, request, timeout)
        };
        GameWatch.CollectAdvisories(composed);
        var cleanup = await Workspace.CloseOpenedSince(client, CliSession.Aux, timeout, new HashSet<string>(before.Append("ProductionWindow").Append("CpuEditWindow")));
        if (Workspace.Any(cleanup)) (composed["operation"]?["result"]?["data"] as JsonObject ?? composed)["closedWindows"] = cleanup;
        Console.WriteLine(Output(composed, compact));
        return composed["ok"]?.GetValue<bool>() == false || composed["operation"]?["state"]?.GetValue<string>() is "failed" or "partial_failure" ? 1 : 0;
    }
    if (command == "game.production-expand" && request["parameters"] is JsonObject expandParameters && expandParameters["count"] != null)
    {
        var count = (int)(CliReads.Num(Take(expandParameters, "count")) ?? 0);
        if (count < 1 || count > 20) throw new ArgumentException("--count must be 1..20 expansion projects.");
        var batches = new JsonArray();
        var ok = true;
        for (var i = 0; i < count && ok; i++)
        {
            var reply = await GameWatch.Call(client, "game.production-expand", "", expandParameters.DeepClone().AsObject(), request["session"]!.ToString(), timeout);
            ok = GameWatch.TryData(reply, out var data);
            batches.Add(new JsonObject { ["batch"] = i + 1, ["ok"] = ok, ["outcome"] = data?["outcome"]?.DeepClone(), ["pendingExpansionLines"] = data?["pendingExpansionLines"]?.DeepClone(), ["verification"] = data?["verification"]?.DeepClone(), ["error"] = ok ? null : reply["operation"]?["error"]?.DeepClone() ?? reply["error"]?.DeepClone() });
        }
        var summary = new JsonObject { ["ok"] = ok, ["kind"] = "production_expand_batches", ["requestedBatches"] = count, ["completedBatches"] = batches.Count(b => b?["ok"]?.GetValue<bool>() == true), ["linesPerBatch"] = expandParameters["lines"]?.DeepClone(), ["batches"] = batches, ["next"] = ok ? "All expansion projects were started and verified. Their lines arrive unassigned when construction completes (watch-advance reports factory_expansion_completed)." : "Stopped at the first failed batch; earlier batches were started. Inspect projects-list before retrying." };
        Console.WriteLine(Output(summary, compact));
        return ok ? 0 : 1;
    }
    if (command is "game.products-pulse" or "game.production-pulse")
    {
        var pulse = await ProductsPulse.Run(client, request, timeout);
        Console.WriteLine(Output(pulse, compact));
        return pulse["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }
    if (command == "game.window-tidy")
    {
        if (target.Length > 0 || request["parameters"] != null) throw new ArgumentException("game window-tidy takes no target or parameters.");
        var tidy = await Workspace.Tidy(client, CliSession.Aux, timeout);
        var tidyResult = new JsonObject { ["ok"] = tidy["error"] == null && tidy["failed"]?.AsObject().Count == 0, ["kind"] = "window_tidy" };
        foreach (var pair in tidy.ToArray()) { tidy.Remove(pair.Key); tidyResult[pair.Key] = pair.Value; }
        tidyResult["next"] = "Only read-only workspace views were closed. keptOpen lists drafts, dialogs, menus and other windows that are never closed automatically; close them explicitly with game window-close or resolve them with dialog commands.";
        Console.WriteLine(Output(tidyResult, compact));
        return tidyResult["ok"]!.GetValue<bool>() ? 0 : 1;
    }
    if (command is "game.watch-advance" or "game.price-probe")
    {
        if (flags.Any(flag => flag != "compact") || options.Keys.Any(key => key is "value" or "scope" or "offset" or "limit" or "observe" or "since" or "output")) throw new ArgumentException($"{command} accepts only its product target, command parameters, session, endpoint and timeout.");
        var composed = command == "game.watch-advance" ? await GameWatch.Run(client, request, timeout) : await GameWatch.ProbePrice(client, request, timeout);
        Console.WriteLine(Output(composed, compact));
        return composed["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }
    // Local guards and helpers around single native commands. `preface` is attached to the final result data.
    var preface = new JsonObject();
    var commandParameters = request["parameters"] as JsonObject;
    if (command == "game.cpu-review" && Take(commandParameters, "keepWindows")?.GetValue<bool>() != true)
    {
        var tidy = await Workspace.Tidy(client, CliSession.Aux, timeout);
        if (tidy["closed"]?.AsArray().Count > 0) preface["closedStaleWindows"] = tidy["closed"]!.DeepClone();
        if (tidy["failed"]?.AsObject().Count > 0) preface["staleWindowsNotClosed"] = tidy["failed"]!.DeepClone();
    }
    if (command == "game.cpu-select" && commandParameters != null && (commandParameters.ContainsKey("showObsolete") || commandParameters.ContainsKey("licensed")))
    {
        // The native picker filters are toggled first; cpu-select then finds the card in the filtered catalog.
        var filters = new JsonObject();
        foreach (var key in new[] { "showObsolete", "licensed" }) if (Take(commandParameters, key) is JsonNode node) filters[key] = node;
        if (commandParameters.Count == 0) request.Remove("parameters");
        var applied = await GameWatch.Call(client, "game.cpu-options", target, filters.DeepClone().AsObject(), CliSession.Aux, timeout);
        if (!GameWatch.TryData(applied, out _)) { Console.WriteLine(Output(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "filter_unavailable", ["message"] = "The native catalog filter could not be set; nothing was selected." }, ["commandReply"] = applied }, compact)); return 1; }
        preface["catalogFilters"] = filters;
    }
    if (command == "game.dialog-choose")
    {
        var releaseKeys = new[] { "price", "name", "sellOnMarket", "availableForContracts" };
        var wantsRelease = commandParameters != null && releaseKeys.Any(commandParameters.ContainsKey);
        if (target == "Release" || wantsRelease)
        {
            var dialogName = commandParameters?["dialog"]?.ToString() ?? "";
            var read = await GameWatch.Call(client, "game.dialog-read", "", null, CliSession.Aux, timeout);
            var match = GameWatch.TryData(read, out var dialogData) ? dialogData!["dialogs"]?.AsArray().FirstOrDefault(d => d?["scope"]?.ToString() == dialogName || d?["name"]?.ToString() == dialogName || d?["name"] + " Window" == dialogName) : null;
            // When the named dialog cannot be identified but a release form is open, treat it as the release form (never release blindly).
            var releaseDialog = Dialogs.IsRelease(match) || match == null && await Dialogs.ReleaseForm(client, CliSession.Aux, timeout) != null;
            if (!releaseDialog || target != "Release")
            {
                if (wantsRelease) throw new ArgumentException("--price, --name, --sell-on-market and --available-for-contracts apply only to dialog-choose Release on the CPU release form ('Project Completed').");
            }
            else if (wantsRelease)
            {
                var form = await Dialogs.ReleaseForm(client, CliSession.Aux, timeout);
                if (form == null) { Console.WriteLine(Output(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "not_found", ["message"] = "The release form could not be read; nothing was released." } }, compact)); return 1; }
                var releaseParameters = new JsonObject();
                foreach (var key in releaseKeys) if (Take(commandParameters, key) is JsonNode node) releaseParameters[key] = node;
                command = "game.projects-release"; target = form["name"]!.ToString();
                request["command"] = command; request["target"] = target; request["parameters"] = releaseParameters;
                preface["routedFrom"] = "dialog-choose Release on the release form; committed through projects-release with the requested fields";
            }
            else if (Take(commandParameters, "confirm")?.GetValue<bool>() != true)
            {
                var form = await Dialogs.ReleaseForm(client, CliSession.Aux, timeout);
                Console.WriteLine(Output(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "release_price_unconfirmed", ["message"] = "'Release' on the 'Project Completed' dialog releases the CPU immediately with the form's current price and toggles. No input was dispatched." }, ["releaseForm"] = form, ["next"] = "Commit with an explicit price: game projects-release \"NAME\" --price P [--sell-on-market true|false] [--available-for-contracts true|false] (or dialog-choose Release --dialog \"Project Completed\" --price P, routed the same way). To keep the form's current values deliberately, add --confirm true." }, compact));
                return 1;
            }
        }
    }
    if (command == "game.product-production" && await PriceTools.ResolveLines(client, request, timeout) is JsonObject resolved)
    {
        if (resolved["ok"]?.GetValue<bool>() == false) { Console.WriteLine(Output(resolved, compact)); return 1; }
        preface["linesResolved"] = resolved;
    }
    // CPU windows: pickers opened by cpu-options/cpu-select, and the designer opened only to list components, are closed
    // again afterwards (the draft's settings persist); the designer is reopened for commands that need it.
    HashSet<string>? cpuWindowsBefore = null;
    var closeDesigner = command is "game.cpu-preview" or "game.cpu-options" or "game.cpu-select" && Take(commandParameters, "closeDesigner")?.GetValue<bool>() == true;
    var keepCpuWindows = command is "game.cpu-options" or "game.cpu-select" or "game.cpu-preview" && Take(commandParameters, "keepOpen")?.GetValue<bool>() == true;
    if (commandParameters?.Count == 0) request.Remove("parameters");
    if (command is "game.cpu-options" or "game.cpu-select" or "game.cpu-preview") cpuWindowsBefore = Workspace.Scopes(await Workspace.Visible(client, CliSession.Aux, timeout));
    if (command is "game.cpu-review" or "game.cpu-develop" && await EnsureCpuDesigner(client, timeout, Workspace.Scopes(await Workspace.Visible(client, CliSession.Aux, timeout)))) preface["reopenedCpuDesigner"] = "The CPU designer was closed; it was reopened on the saved draft (game cpu-preview without fields) before this command.";
    if (command is "game.projects-release" or "game.projects-release-preview" && target.Length == 0)
    {
        var form = await Dialogs.ReleaseForm(client, CliSession.Aux, timeout);
        if (form == null) { Console.WriteLine(Output(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "not_found", ["message"] = "No CPU release form ('Project Completed') is open; nothing was released. Use game projects-wait NAME to reach it." } }, compact)); return 1; }
        target = form["name"]!.ToString();
        request["target"] = target;
        preface["targetFromReleaseForm"] = target;
    }
    JsonObject result;
    try { result = await Send(client, request); GameWatch.CollectAdvisories(result); }
    catch (Exception error) when (command == "game.session-exit" && target == "desktop" && error is HttpRequestException or TaskCanceledException or JsonException)
    {
        if (await GameProcessStopped()) { Console.WriteLine(JsonSerializer.Serialize(new { ok = true, state = "process_exited", requestAccepted = false, nativePostcondition = "game_process_stopped" })); return 0; }
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, state = "exit_unverified", expectedDisconnect = true, next = "No usable exit response arrived. The game may have closed before replying, but request acceptance and process exit are unverified. Check whether the game remains open; do not repeat blindly.", error = error.Message }));
        return 2;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
    {
        var diagnostic = await GameHost.Status(options.GetValueOrDefault("endpoint"));
        Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "bridge_unavailable", ["message"] = error.Message }, ["status"] = diagnostic, ["next"] = "The request outcome is unknown. Use status for recovery; do not blindly repeat a mutation." }.ToJsonString());
        return 2;
    }
    var receivedNotifications = new JsonObject();
    MergeNotifications(receivedNotifications, result);
    if (!flags.Contains("no-wait") && result["operation"] is JsonObject operation && operation["id"] != null && command != "operation")
    {
        var id = operation["id"]!.GetValue<string>();
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        var noticeAt = DateTime.UtcNow.AddSeconds(3);
        var waitingNoticeWritten = false;
        while (result.At("operation", "state").Str() is "accepted" or "running" && DateTime.UtcNow < deadline)
        {
            if (!waitingNoticeWritten && DateTime.UtcNow >= noticeAt)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { state = "waiting", operationId = id, next = $"Still running. Query pt-agent operation {id}; do not resend this command. --no-wait returns the ID immediately." }));
                waitingNoticeWritten = true;
            }
            await Task.Delay(100);
            try { result = await Send(client, new JsonObject { ["command"] = "operation", ["target"] = id, ["session"] = request["session"]!.DeepClone() }); GameWatch.CollectAdvisories(result); MergeNotifications(receivedNotifications, result); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (command == "game.session-exit" && target == "desktop")
                {
                    if (await GameProcessStopped()) { Console.WriteLine(JsonSerializer.Serialize(new { ok = true, operationId = id, state = "process_exited", requestAccepted = true, nativePostcondition = "game_process_stopped" })); return 0; }
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, operationId = id, state = "exit_unverified", expectedDisconnect = true, next = "The exit request was accepted and the connection closed. This is expected on exit, but does not prove the native action ran or the process ended; check whether the game remains open. Do not repeat blindly.", error = error.Message }));
                    return 2;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, operationId = id, state = "unknown", notifications = receivedNotifications, next = "Connection lost. Query this operation if the game is still running; do not repeat it blindly.", error = error.Message }));
                return 2;
            }
        }
    }
    if (!flags.Contains("no-wait")) result = await Enrich(client, command, target, request, result, receivedNotifications, timeout, preface.ContainsKey("catalogFilters"));
    if (cpuWindowsBefore != null && !keepCpuWindows && !flags.Contains("no-wait"))
    {
        var pickers = new[] { "PackageSelectionWindow", "LithographySelectionWindow", "ArchitectureSelectionWindow", "MemorySelectionWindow" };
        var now = Workspace.Scopes(await Workspace.Visible(client, CliSession.Aux, timeout));
        var toClose = now.Where(scope => pickers.Contains(scope) && !cpuWindowsBefore.Contains(scope)).ToList();
        if (now.Contains("CreateCpuWindow") && (closeDesigner || command == "game.cpu-options" && !cpuWindowsBefore.Contains("CreateCpuWindow"))) toClose.Add("CreateCpuWindow");
        if (toClose.Count > 0)
        {
            var closed = await Workspace.Close(client, CliSession.Aux, timeout, toClose);
            preface["closedCpuWindows"] = closed;
            preface["cpuWindowsNote"] = "Pickers (and the CPU designer when this command only opened it to list options, or with --close-designer true) were closed so they do not cover later commands; the draft's settings persist and cpu-preview reopens it. Use --keep-open true to keep them.";
        }
    }
    if (preface.Count > 0)
    {
        var holder = result["operation"]?["result"]?["data"] as JsonObject ?? result;
        foreach (var pair in preface.ToArray()) { preface.Remove(pair.Key); holder[pair.Key] = pair.Value; }
    }
    if (command == "screenshot" && options.TryGetValue("output", out var destination) && result["operation"]?["state"]?.GetValue<string>() == "completed")
    {
        var data = result["operation"]!["result"]!;
        var outputPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(data["path"]!.GetValue<string>(), outputPath, overwrite: true);
        data["path"] = outputPath;
        data["temporary"] = false;
        data["next"] = "View this preserved local PNG; subsequent captures do not replace it.";
    }
    if (receivedNotifications.Count > 0) result["notifications"] = receivedNotifications;
    if (command == "notifications" && result["notifications"] is JsonObject replay && replay["items"] is JsonArray replayItems)
    {
        // Mod-synthesized notifications (competitor_price_changed, rival_took_lead, ...) are stored by the CLI and appended
        // after the native popups; they carry source "cli_synthesized", native false and "syn-N" ids (not part of --since).
        var synthesized = CliReads.LoadJson(MarketSignals.StoreFile)?["events"]?.AsArray().OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        if (synthesized.Length > 0)
        {
            var merged = replayItems.Select(i => i?.DeepClone()).Concat(synthesized.Select(i => (JsonNode?)i.DeepClone())).Select((item, index) => (Item: item, Index: index)).OrderBy(x => x.Item?["gameDate"]?.ToString() ?? "", StringComparer.Ordinal).ThenBy(x => x.Index).Select(x => x.Item).ToArray();
            replayItems.Clear();
            foreach (var item in merged) replayItems.Add(item);
            replay["synthesizedCount"] = synthesized.Length;
        }
        var types = notificationType?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        var kept = replayItems.Where(item => types.Length == 0 || types.Contains(item?["type"]?.ToString())).ToList();
        var total = kept.Count;
        if (options.TryGetValue("limit", out var keepLast)) kept = kept.Skip(Math.Max(0, kept.Count - int.Parse(keepLast))).ToList();
        replay["items"] = new JsonArray(kept.Select(item => item?.DeepClone()).ToArray());
        if (types.Length > 0 || options.ContainsKey("limit")) replay["filter"] = new JsonObject { ["type"] = notificationType, ["limit"] = options.TryGetValue("limit", out var shown) ? int.Parse(shown) : null, ["matched"] = total, ["returned"] = replay["items"]!.AsArray().Count, ["retained"] = replayItems.Count };
    }
    Console.WriteLine(Output(result, compact));
    if (result.At("ok").Bool() == false || result.At("operation", "state").Str() is "failed" or "partial_failure" or "interrupted") return 1;
    return 0;
}

static JsonObject WithOk(JsonObject data) { data["ok"] = true; return data; }

// Reopens the saved CPU draft when a command that reads it runs while the designer is closed. Returns whether it reopened.
static async Task<bool> EnsureCpuDesigner(HttpClient client, int timeout, HashSet<string> open)
{
    if (open.Contains("CreateCpuWindow")) return false;
    var reply = await GameWatch.Call(client, "game.cpu-preview", "", null, CliSession.Aux, timeout);
    return GameWatch.TryData(reply, out _);
}

// Removes binary float noise from every number (4059999.9999999995 -> 4060000) before printing.
static void CleanNumbers(JsonNode? node)
{
    switch (node)
    {
        case JsonObject obj:
            foreach (var (key, value) in obj.ToArray()) { if (Cleaned(value) is JsonNode replacement) obj[key] = replacement; else CleanNumbers(value); }
            break;
        case JsonArray array:
            for (var i = 0; i < array.Count; i++) { if (Cleaned(array[i]) is JsonNode replacement) array[i] = replacement; else CleanNumbers(array[i]); }
            break;
    }
}

static JsonNode? Cleaned(JsonNode? node)
{
    if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
    var text = value.ToJsonString();
    if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E')) return null;
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
    var clean = DisplayNumber.Clean(number);
    return clean.ToString("R", CultureInfo.InvariantCulture) == number.ToString("R", CultureInfo.InvariantCulture) ? null : JsonValue.Create(clean);
}

// --compact removes repeated explanatory boilerplate and duplicate catalog cells; warnings, notes, errors and data stay.
static string Output(JsonObject result, bool compact)
{
    GameWatch.CollectAdvisories(result);
    if (GameWatch.Advisories.Count > 0) result["advisories"] = GameWatch.Advisories.DeepClone();
    CleanNumbers(result);
    if (!compact) return result.ToJsonString();
    var copy = result.DeepClone().AsObject();
    Strip(copy);
    // An empty notification block is dropped, except a filtered `notifications` replay (its filter counts are the answer).
    if (copy["notifications"] is JsonObject notices && notices["items"] is JsonArray { Count: 0 } && notices["filter"] == null) copy.Remove("notifications");
    return copy.ToJsonString();
}

static void Strip(JsonNode? node)
{
    var boilerplate = new[] { "streamId", "precision", "catalogScope", "analysisOptions", "periodNote", "creditNote", "productIndicatorSemantics", "marketGuidance", "planning", "draftSideEffects", "columnsSource", "offscreenIncluded", "method", "linesSemantics", "semantics" };
    if (node is JsonArray array) { foreach (var item in array) Strip(item); return; }
    if (node is not JsonObject obj) return;
    foreach (var key in boilerplate) obj.Remove(key);
    // A "scope" sentence is boilerplate; a short window/dialog scope is an identifier needed for window-close/dialog-choose.
    if (obj["scope"] is JsonValue scopeValue && scopeValue.ToString().Length > 60) obj.Remove("scope");
    if (obj["display"] is JsonValue && obj["values"] is JsonObject) obj.Remove("values");
    if (obj["captureAvailable"]?.GetValueKind() == JsonValueKind.True) obj.Remove("captureAvailable");
    if (obj.ContainsKey("captureError") && obj["captureError"] == null) obj.Remove("captureError");
    if (obj["historyTruncated"]?.GetValueKind() == JsonValueKind.False) obj.Remove("historyTruncated");
    foreach (var pair in obj.ToArray()) Strip(pair.Value);
}

static async Task<JsonObject> Send(HttpClient client, JsonObject request)
{
    using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await client.PostAsync("v1/command", content);
    response.EnsureSuccessStatusCode();
    return JsonSafe.ParseObject(await response.Content.ReadAsStringAsync());
}

static async Task<bool> GameProcessStopped()
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        var status = await GameHost.Status();
        if (status["host"]?["processRunning"]?.GetValue<bool>() == false) return true;
        await Task.Delay(200);
    }
    return false;
}

static void MergeNotifications(JsonObject collected, JsonObject response)
{
    if (response["notifications"] is not JsonObject incoming) return;
    var items = collected["items"] as JsonArray ?? new JsonArray();
    var truncated = collected["historyTruncated"]?.GetValue<bool>() == true || incoming["historyTruncated"]?.GetValue<bool>() == true;
    foreach (var item in incoming["items"]?.AsArray() ?? new JsonArray())
    {
        if (item != null && !items.Any(existing => existing?["id"]?.ToJsonString() == item["id"]?.ToJsonString())) items.Add(item.DeepClone());
    }
    while (items.Count > 512) { items.RemoveAt(0); truncated = true; }
    foreach (var field in incoming.Where(field => field.Key != "items")) collected[field.Key] = field.Value?.DeepClone();
    if (collected["items"] == null) collected["items"] = items;
    collected["historyTruncated"] = truncated;
}

static JsonNode? Take(JsonObject? parameters, string key)
{
    if (parameters == null || !parameters.TryGetPropertyValue(key, out var value)) return null;
    parameters.Remove(key);
    return value;
}

// Local, read-only additions to single native command results. Never dispatches gameplay input.
static async Task<JsonObject> Enrich(HttpClient client, string command, string target, JsonObject request, JsonObject result, JsonObject receivedNotifications, int timeout, bool filtersSupplied)
{
    var state = result.At("operation", "state").Str();
    var errorCode = result.At("operation", "error", "code").Str();
    // Read-only Production commands blocked by stale views: close read-only views (never drafts/dialogs) and retry once.
    // (A blocked navigation click can report partial_failure; these commands only read, so one retry is safe.)
    if (command is "game.product-pulse" or "game.production-read" or "game.product-list" && state is "failed" or "partial_failure" && errorCode == "not_interactable")
    {
        var tidy = await Workspace.Tidy(client, CliSession.Aux, timeout, "ProductionWindow");
        if (tidy["closed"]?.AsArray().Count > 0)
        {
            var retry = await GameWatch.Call(client, command, target, request["parameters"]?.DeepClone() as JsonObject, request["session"]!.ToString(), timeout);
            MergeNotifications(receivedNotifications, retry);
            retry.Remove("notifications");
            result = retry;
            if (result["operation"]?["result"]?["data"] is JsonObject retried) { retried["closedStaleWindows"] = tidy["closed"]!.DeepClone(); retried["retriedAfterClosingStaleWindows"] = true; }
            state = result["operation"]?["state"]?.ToString();
        }
        else if (await Dialogs.Blocking(client, CliSession.Aux, timeout, checkPauseMenu: true, failedReply: result) is JsonObject blocking) result["blockingDialogs"] = blocking;
    }
    if (command == "game.cpu-select" && !filtersSupplied && state == "failed" && errorCode == "not_found" && request["value"] != null && (result["operation"]?["error"]?["message"]?.ToString() ?? "").Contains("catalog", StringComparison.Ordinal))
    {
        var hint = await FilterHint(client, target, request["value"]!.ToString(), timeout);
        if (hint != null) { if (result.At("operation", "error") is JsonObject hinted) hinted["hint"] = hint; result["next"] = hint; }
    }
    // Any other command refused because something covers its target: name the windows that stay open and how to close them.
    if (state is "failed" or "partial_failure" && errorCode == "not_interactable" && result["blockingDialogs"] == null && command.StartsWith("game.", StringComparison.Ordinal))
    {
        var kept = Workspace.Others(await Workspace.Visible(client, CliSession.Aux, timeout));
        if (kept.Count > 0)
        {
            result["blockingWindows"] = kept;
            var cpu = kept.OfType<JsonObject>().Any(w => w["scope"]?.ToString() is "CreateCpuWindow" or "PackageSelectionWindow" or "LithographySelectionWindow" or "ArchitectureSelectionWindow" or "MemorySelectionWindow");
            result["next"] = "These windows stay open and may cover the target: " + string.Join(", ", kept.OfType<JsonObject>().Select(w => $"'{w["name"]}' (scope {w["scope"]})")) + ". Close them with game window-close NAME" + (cpu ? " (closing the CPU designer or a picker keeps the draft's settings; cpu-preview reopens it)" : "") + ", or game window-tidy for read-only views, then retry.";
        }
    }
    var data = result.At("operation", "result", "data") as JsonObject;
    if (data == null) return result;
    // A project left the list during time-advance/projects-wait: report current line totals (a finished factory
    // expansion adds unassigned lines). Price follow-ups that are due are reported too.
    if (command is "game.time-advance" or "game.projects-wait" && data["dialogs"] is not JsonArray { Count: > 0 })
    {
        var projectLeft = data["projectChanges"]?.AsArray().Any(p => p?["change"]?.ToString() == "no_longer_listed") == true;
        var followUpsDue = PriceTools.FollowUpsDue(data["stoppedDate"]?.ToString());
        if (projectLeft || followUpsDue)
        {
            var production = await CliReads.Data(client, "game.production-read", null, CliSession.Aux, timeout);
            if (production != null && projectLeft)
            {
                var lines = ProductionRows.Lines(production);
                data["linesAfterProjectChange"] = new JsonObject { ["capacity"] = lines["capacity"]?.DeepClone(), ["unassigned"] = lines["unassigned"]?.DeepClone(), ["pendingExpansion"] = lines["pendingExpansion"]?.DeepClone(), ["exact"] = lines["exact"]?["unassigned"]?.DeepClone(), ["note"] = "A project left the list. If it was a factory expansion, its lines are now part of capacity and start unassigned (assigning them is your decision)." };
            }
            if (production != null && followUpsDue)
            {
                var rows = production["products"]?.AsArray().OfType<JsonObject>().Select(ProductionRows.FromRow).ToArray() ?? Array.Empty<JsonObject>();
                var followUps = await PriceTools.DueFollowUps(client, CliSession.Aux, timeout, data["stoppedDate"]?.ToString(), rows);
                if (followUps.Count > 0) data["priceFollowUps"] = followUps;
            }
            await PriceTools.CloseViews(client, CliSession.Aux, timeout, keep: null);
        }
    }
    if (command is "game.time-advance" or "game.projects-wait" && (data["dialogs"]?.AsArray().Any(Dialogs.IsRelease) == true || data["readyForRelease"]?.GetValue<bool>() == true))
    {
        var form = await Dialogs.ReleaseForm(client, CliSession.Aux, timeout);
        if (form != null) { data["releaseForm"] = form; if (data["readyForRelease"]?.GetValue<bool>() != true) data["next"] = Dialogs.ReleaseNext; }
    }
    // The native advance names the popups that woke it; add their choices and texts so no separate dialog-read is needed.
    if (command is "game.time-advance" or "game.projects-wait" && data["dialogs"] is JsonArray { Count: > 0 } wokenBy)
    {
        var read = await GameWatch.Call(client, "game.dialog-read", "", null, CliSession.Aux, timeout);
        var visibleDialogs = GameWatch.TryData(read, out var dialogData) ? dialogData!["dialogs"]?.AsArray().OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>() : Array.Empty<JsonObject>();
        foreach (var dialog in wokenBy.OfType<JsonObject>())
        {
            var details = dialog["scope"]?.ToString() == "PauseMenu" ? Dialogs.PauseMenu() : visibleDialogs.FirstOrDefault(d => d["scope"]?.ToString() == dialog["scope"]?.ToString()) is JsonObject visible ? Dialogs.Compact(visible) : null;
            if (details != null) foreach (var key in new[] { "name", "choices", "text", "note" }) if (details[key] != null) dialog[key] = details[key]!.DeepClone();
        }
    }
    if (command == "game.dialog-read" && data["dialogs"] is JsonArray dialogs)
        foreach (var dialog in dialogs.OfType<JsonObject>().Where(Dialogs.IsRelease))
        {
            dialog["releaseForm"] = true;
            dialog["note"] = "This 'Project Completed' dialog is the CPU release form. Inspect it with game projects-release-read and commit with game projects-release \"NAME\" --price P [--sell-on-market ...]. dialog-choose Release refuses without --price P (routed to projects-release) or --confirm true (keeps the form's current price).";
        }
    if (command is "game.production-read" or "game.product-list" or "game.product-production" or "game.product-set" or "game.production-settings" or "game.production-automation" && data["products"] is JsonArray && data["summary"] is JsonObject) ProductionRows.EnrichLines(data);
    if (command is "game.production-settings" or "game.production-automation" && data["settings"] == null && await ProductionToggles(client, timeout) is JsonObject settings) data["settings"] = settings;
    return result;
}

// Readback of the Production window toggles for plugins that do not report `settings` natively (< 0.3.9).
static async Task<JsonObject?> ProductionToggles(HttpClient client, int timeout)
{
    JsonObject reply;
    try { reply = await Send(client, new JsonObject { ["command"] = "observe", ["target"] = "", ["scope"] = "ProductionWindow", ["offset"] = 0, ["limit"] = 200, ["session"] = CliSession.Aux }); }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    var controls = reply["data"]?["controls"]?.AsArray().OfType<JsonObject>().Where(c => c["role"]?.ToString() == "toggle").ToArray();
    if (controls == null || controls.Length == 0) return null;
    JsonObject? Toggle(string name) => controls.FirstOrDefault(c => c["name"]?.ToString() == name) is JsonObject c ? new JsonObject { ["value"] = c["value"]?.DeepClone(), ["label"] = c["label"]?.DeepClone(), ["enabled"] = c["blockedReason"] == null } : null;
    return new JsonObject { ["upgradeLines"] = Toggle("Upgrade Wafer"), ["foundryServices"] = Toggle("Foundry Services"), ["automation"] = Toggle("Automate Production"), ["source"] = "visible Production toggles" };
}

// After cpu-select misses a card: check whether the Show Obsolete or Show Licensed filter hides it, then restore the filter.
static async Task<string?> FilterHint(HttpClient client, string kind, string wanted, int timeout)
{
    var current = await GameWatch.Call(client, "game.cpu-options", kind, null, CliSession.Aux, timeout);
    if (!GameWatch.TryData(current, out var catalog)) return null;
    var filters = catalog!["filters"]?.AsArray().OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
    foreach (var (key, label, flag) in new[] { ("showObsolete", "Obsolete", "--show-obsolete true"), ("licensed", "Licensed", "--licensed true") })
    {
        var toggle = filters.FirstOrDefault(f => (f["name"]?.ToString() ?? "").Contains(label, StringComparison.OrdinalIgnoreCase));
        if (toggle == null || toggle["value"]?.GetValue<bool>() == true || toggle["blockedReason"] != null) continue;
        var shown = await GameWatch.Call(client, "game.cpu-options", kind, new JsonObject { [key] = true }, CliSession.Aux, timeout);
        var names = GameWatch.TryData(shown, out var shownCatalog) ? shownCatalog!["options"]?.AsArray().Select(o => o?["name"]?.ToString() ?? "").ToArray() ?? Array.Empty<string>() : Array.Empty<string>();
        await GameWatch.Call(client, "game.cpu-options", kind, new JsonObject { [key] = false }, CliSession.Aux, timeout);
        if (names.Contains(wanted)) return $"'{wanted}' is hidden by the {kind} catalog's Show {label} filter. Retry: game cpu-select {kind} --value \"{wanted}\" {flag}";
        var similar = names.Where(name => name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (similar.Length > 0) return $"'{wanted}' is not an exact card name; with Show {label} the catalog lists: {string.Join(", ", similar)}. Retry with the exact name and {flag}.";
    }
    return null;
}

internal static class CliSession
{
    // Auxiliary reads use their own notification session so they never consume the caller's notification delivery.
    internal const string Aux = "local-cli-aux";
}