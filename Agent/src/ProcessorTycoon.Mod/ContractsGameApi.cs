using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

// English 0.2.16a5 mappings. Reads and mutations use only currently visible native UI.
internal sealed class ContractsGameApi : IGameModule
{
    private const string ContractsScope = "ContractWindow";
    private const string ManagerScope = "ContractManagerWindow";
    private const string BusinessScope = "BusinessWindow";
    private const string InspectorScope = "ContractInspectorWindow";
    private const string NegotiationScope = "ContractNegotiationWindow";
    private const string BreakScope = "BreakContractWindow";
    private const string ManagerCpuRow = "Cpu Button Variant";
    private const string ContractRefPrefix = "contract-ui:";
    private const string CpuRefPrefix = "contract-cpu-ui:";
    private static readonly string IdentitySession = Guid.NewGuid().ToString("N").Substring(0, 8);
    private static ConditionalWeakTable<object, IdentityToken> contractTokens = new();
    private static ConditionalWeakTable<object, IdentityToken> cpuTokens = new();
    private static readonly Dictionary<string, string> contractLabels = new(StringComparer.Ordinal);
    private static int identityScene = int.MinValue;
    private static long nextIdentityToken;
    private readonly GameUi ui;
    private string? offerName;
    private string? contractRef;
    private string? cpuName;
    private string? cpuRef;
    private string? companyName;
    private readonly JArray listedContracts = new();
    private string listView = "names";
    private bool listCatalogComplete;
    private bool listEvaluationComplete;
    private string? listIncompleteReason;
    private int listCatalogCount;
    private readonly JArray activeCpuResults = new();
    private string activeView = "names";
    private bool activeCatalogComplete;
    private readonly HashSet<string> businessContractsBefore = new();
    private JObject? expectedBusinessContract;
    private JObject? verifiedBusinessContract;
    private int businessContractsBeforeCount;
    private int businessContractsAfter;
    private bool businessBaselineAvailable;
    private string? businessSignVerificationReason;
    private string? businessContractIdentity;
    private string? businessBreakFine;
    private string? businessBreakConfirmationText;
    private int businessBreakBeforeCount;
    private bool businessBreakConfirmed;
    private bool businessBreakBeforeComplete;
    private string? contractBreakFine;
    private string? contractBreakConfirmationText;
    private int contractBreakBeforeCount;
    private bool contractBreakConfirmed;
    private bool contractBreakBeforeComplete;
    private string? contractBreakRef;
    private string contractOfferState = "not_checked";
    private string? contractStatusCompany;
    private string? contractStatusProduct;
    private JObject? contractStatusAvailable;
    private JObject? contractStatusActive;
    private string? contractStatusReason;
    private bool contractStatusAvailableComplete;
    private bool? contractStatusActiveComplete;

    public ContractsGameApi(GameUi ui) => this.ui = ui;
    public string[] Commands => new[]
    {
        "game.contracts-list", "game.contracts-read", "game.contracts-send", "game.contracts-cancel", "game.contracts-status",
        "game.contracts-active", "game.contracts-manage", "game.contracts-break",
        "game.business-list", "game.business-read", "game.business-contracts", "game.business-negotiate", "game.business-sign", "game.business-break"
    };

    public static object Schema => new
    {
        offers = new[] { "game contracts-list [--view names|summary|full] [--cpu EXACT_CPU] [--match TEXT] [--eligible-only BOOL] [--filter All|Sent Offer|No Offer] [--type All|Calculator|Computer|Console|Phone]", "game contracts-read EXACT_OFFER [--cpu EXACT_CPU]", "game contracts-send EXACT_OFFER --cpu EXACT_CPU", "game contracts-cancel EXACT_OFFER --cpu EXACT_CPU", "game contracts-status EXACT_OFFER --cpu EXACT_CPU" },
        active = new[] { "game contracts-active [--view names|full] [--cpu EXACT_CPU]", "game contracts-manage EXACT_CPU", "game contracts-break EXACT_ACTIVE_CONTRACT --cpu EXACT_CPU [--confirm true]" },
        business = new[] { "game business-list", "game business-read EXACT_COMPANY", "game business-contracts EXACT_COMPANY", "game business-negotiate EXACT_COMPANY [TERMS]", "game business-sign EXACT_COMPANY [TERMS]", "game business-break EXACT_COMPANY --contract EXACT_IDENTITY [--confirm true]" },
        terms = new { playerRole = "provider|client", durationYears = "1|3|5|10", productionPercent = "integer 1..25", markupPercent = "integer 25..125", exclusivity = "boolean", renew = "boolean" },
        visibility = "Contract/company/CPU names use the complete active player-browsable UI catalog. Duplicate Contracts entities return Contracts-local contractRef/cpuRef selectors; Production productRef is not accepted. Summary/full contract views sequentially select native rows to read the real detail pane."
    };

    public void Validate(Request request)
    {
        if (request.Hidden) throw new AgentError("hidden_not_supported", "Contract and business commands may open visible native windows; hidden execution is not supported.");
        switch (request.Command)
        {
            case "game.contracts-list":
                NoTarget(request); GameUi.Parameters(request, "filter", "type", "view", "cpu", "match", "eligibleOnly");
                OptionalChoice(request, "filter", "All", "Sent Offer", "No Offer"); OptionalChoice(request, "type", "All", "Calculator", "Computer", "Console", "Phone"); OptionalChoice(request, "view", "names", "summary", "full");
                OptionalRequiredString(request, "cpu"); OptionalString(request, "match"); OptionalBoolean(request, "eligibleOnly");
                var requestedView = request.Parameters?["view"]?.Value<string>();
                if (requestedView == "names" && request.Parameters?["cpu"] != null) throw new AgentError("invalid_request", "CPU evaluation requires --view summary or --view full; names is the cheap catalog mode.");
                if (request.Parameters?["eligibleOnly"]?.Value<bool>() == true && request.Parameters?["cpu"] == null) throw new AgentError("invalid_request", "eligibleOnly requires --cpu EXACT_CPU.");
                break;
            case "game.contracts-read":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "cpu"); OptionalRequiredString(request, "cpu"); break;
            case "game.contracts-send": case "game.contracts-cancel":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "cpu"); RequiredString(request, "cpu"); break;
            case "game.contracts-status":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "cpu"); RequiredString(request, "cpu"); break;
            case "game.contracts-active":
                NoTarget(request); GameUi.Parameters(request, "view", "cpu"); OptionalChoice(request, "view", "names", "full"); OptionalRequiredString(request, "cpu"); break;
            case "game.contracts-manage":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); break;
            case "game.contracts-break":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "cpu", "confirm"); RequiredString(request, "cpu"); OptionalBoolean(request, "confirm");
                break;
            case "game.business-list":
                NoTarget(request); GameUi.Parameters(request); break;
            case "game.business-read": case "game.business-contracts":
                GameUi.RequiredTarget(request); GameUi.Parameters(request); break;
            case "game.business-negotiate": case "game.business-sign":
                GameUi.RequiredTarget(request); ValidateTerms(request); break;
            case "game.business-break":
                GameUi.RequiredTarget(request); GameUi.Parameters(request, "contract", "confirm"); RequiredString(request, "contract"); OptionalBoolean(request, "confirm"); break;
            default: throw new AgentError("unsupported_command", $"Unsupported contracts/business command '{request.Command}'.");
        }
        if (request.Value != null) throw new AgentError("invalid_request", $"{request.Command} uses named parameters, not --value.");
    }

    public IEnumerable<Request> Prepare(Request request)
    {
        if (request.Command.StartsWith("game.contracts-", StringComparison.Ordinal))
        {
            foreach (var step in PrepareContracts(request)) yield return step;
            yield break;
        }
        foreach (var step in PrepareBusiness(request)) yield return step;
    }

    private IEnumerable<Request> PrepareContracts(Request request)
    {
        if (GameUi.Controls(ui.Read(BreakScope)).Length > 0) throw new AgentError("not_interactable", "A native Break Contract confirmation is already open. It was not reused or closed.");
        if (GameUi.Controls(ui.Read(NegotiationScope)).Length > 0) throw new AgentError("not_interactable", "A native business negotiation draft is open. It was left intact; finish or close it before using a contracts command.");
        foreach (var step in OpenContracts()) yield return step;
        if (request.Command == "game.contracts-list")
        {
            foreach (var pair in new[] { ("filter", "Contract Filter Dropdown"), ("type", "Contract Type Dropdown") })
            {
                var wanted = request.Parameters?[pair.Item1]?.Value<string>();
                if (wanted == null) continue;
                var select = Named(ui.Read(ContractsScope), pair.Item2);
                EnsureOption(select, wanted, pair.Item1);
                if (Selected(select) != wanted) yield return GameUi.Select(select, wanted);
            }
            listedContracts.Clear(); listIncompleteReason = null;
            var requestedCpuSelector = request.Parameters?["cpu"]?.Value<string>();
            cpuName = null; cpuRef = null;
            listView = request.Parameters?["view"]?.Value<string>() ?? (requestedCpuSelector == null ? "names" : "summary");
            var catalog = ui.Catalog(ContractsScope);
            listCatalogComplete = !((bool?)catalog["more"] ?? false);
            var rows = AvailableContractRows(catalog).Where(r => !r.Name.Contains('…')).ToArray();
            var match = request.Parameters?["match"]?.Value<string>();
            if (!string.IsNullOrEmpty(match)) rows = rows.Where(r => r.Name.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            listCatalogCount = rows.Length;
            if (listView == "names")
            {
                foreach (var row in rows) listedContracts.Add(new JObject { ["contractRef"] = row.Ref, ["name"] = row.Name, ["nativeAvailable"] = row.Node["blockedReason"] == null, ["blockedReason"] = row.Node["blockedReason"]?.DeepClone() });
                listEvaluationComplete = true;
                if (!listCatalogComplete) listIncompleteReason = "The active UI catalog exceeded its bounded read limit.";
                yield break;
            }
            listEvaluationComplete = requestedCpuSelector != null;
            if (requestedCpuSelector == null) listIncompleteReason = "No CPU was supplied; native CPU-dependent requirements, acceptance chance, required production lines, fines and eligibility were not evaluated. Offer prices alone do not show whether an offer is reachable.";
            CpuRow? evaluationCpu = null;
            foreach (var contract in rows)
            {
                var selection = VerifiedAvailableClick(contract);
                selection.SettleFrames = 1;
                yield return selection;
                if (requestedCpuSelector != null)
                {
                    evaluationCpu ??= ResolveOfferCpu(ui.Catalog(ContractsScope), requestedCpuSelector);
                    cpuName = evaluationCpu.Name; cpuRef = evaluationCpu.Ref;
                    if (!ContractWindowCurrentCpuIs(evaluationCpu))
                    {
                        evaluationCpu = ResolveOfferCpu(ui.Catalog(ContractsScope), evaluationCpu.Ref);
                        yield return VerifiedOfferCpuClick(evaluationCpu);
                    }
                }
                var entry = ContractEntry(contract, listView == "full", requestedCpuSelector != null);
                if (request.Parameters?["eligibleOnly"]?.Value<bool>() != true || (bool?)entry["requirementsMatch"] == true) listedContracts.Add(entry);
            }
            if (!listCatalogComplete && listIncompleteReason == null) listIncompleteReason = "The active UI catalog exceeded its bounded read limit.";
            yield break;
        }
        if (request.Command == "game.contracts-status")
        {
            foreach (var step in PrepareContractStatus(request)) yield return step;
            yield break;
        }
        if (request.Command is "game.contracts-active" or "game.contracts-manage" or "game.contracts-break")
        {
            if (GameUi.Controls(ui.Read(ManagerScope)).Length == 0) yield return GameUi.Click(Named(ui.Read(ContractsScope), "Signed Contracts"));
            if (request.Command == "game.contracts-active")
            {
                foreach (var step in PrepareActiveList(request)) yield return step;
                yield break;
            }
            var requestedCpu = request.Command == "game.contracts-manage" ? request.Target : request.Parameters!["cpu"]!.Value<string>();
            var cpu = ResolveManagerCpu(ui.Catalog(ManagerScope), requestedCpu!);
            cpuName = cpu.Name; cpuRef = cpu.Ref;
            yield return VerifiedManagerCpuClick(cpu);
            if (request.Command == "game.contracts-manage") yield break;
            var managerCatalog = ui.Catalog(ManagerScope);
            contractBreakConfirmed = request.Parameters?["confirm"]?.Value<bool>() == true;
            var activeRows = ActiveContractRows(managerCatalog);
            var row = ResolveActiveContract(activeRows, request.Target);
            if (row.CpuRef != cpuRef) throw new AgentError("context_changed", "The rendered active contract is bound to a different CPU than the selected manager row.");
            offerName = row.Name; contractRef = row.Ref; contractBreakRef = row.Ref;
            contractBreakBeforeCount = activeRows.Length;
            contractBreakBeforeComplete = !((bool?)managerCatalog["more"] ?? false);
            yield return VerifiedActiveClick(GameUi.One(GameUi.Controls(managerCatalog).Where(c => (string?)c["name"] == "Break Contract" && (string?)c["group"] == row.Group), $"Break Contract for '{offerName}'"), row);
            var dialog = ui.Read(BreakScope);
            if (GameUi.Controls(dialog).Length == 0) throw new AgentError("confirmation_missing", "The expected native Break Contract confirmation did not open; nothing was confirmed.");
            contractBreakConfirmationText = KnownBreakConfirmation(dialog);
            contractBreakFine = BreakFine(contractBreakConfirmationText);
            yield return GameUi.Click(Named(dialog, contractBreakConfirmed ? "Break Contract" : "Cancel"));
            yield break;
        }

        var selectedContract = ResolveAvailableContract(ui.Catalog(ContractsScope), request.Target);
        offerName = selectedContract.Name; contractRef = selectedContract.Ref;
        yield return VerifiedAvailableClick(selectedContract);
        var requestedOfferCpu = request.Parameters?["cpu"]?.Value<string>();
        if (requestedOfferCpu != null)
        {
            var selectedCpu = ResolveOfferCpu(ui.Catalog(ContractsScope), requestedOfferCpu);
            cpuName = selectedCpu.Name; cpuRef = selectedCpu.Ref;
            yield return VerifiedOfferCpuClick(selectedCpu);
        }
        if (request.Command == "game.contracts-read") yield break;
        var action = request.Command == "game.contracts-send" ? "Send Offer" : "Cancel Offer";
        var button = Named(ui.Read(ContractsScope), action);
        yield return GameUi.Click(button);
    }

    private IEnumerable<Request> PrepareContractStatus(Request request)
    {
        var exactContractRequested = request.Target.StartsWith(ContractRefPrefix, StringComparison.Ordinal);
        offerName = request.Target; contractRef = null;
        cpuName = request.Parameters!["cpu"]!.Value<string>(); cpuRef = null;
        contractOfferState = "not_observable"; contractStatusCompany = null; contractStatusProduct = null; contractStatusAvailable = null; contractStatusActive = null; contractStatusReason = null; contractStatusActiveComplete = null;
        foreach (var name in new[] { "Contract Filter Dropdown", "Contract Type Dropdown" })
        {
            var filter = Named(ui.Read(ContractsScope), name);
            if (Selected(filter) != "All") yield return GameUi.Select(filter, "All");
        }
        var availableCatalog = ui.Catalog(ContractsScope);
        contractStatusAvailableComplete = !((bool?)availableCatalog["more"] ?? false);
        var availableRows = AvailableContractRows(availableCatalog).Where(row => !row.Name.Contains('…')).ToArray();
        var available = TryResolveAvailableContract(availableRows, request.Target);
        if (available != null)
        {
            offerName = available.Name; contractRef = available.Ref;
            yield return VerifiedAvailableClick(available);
            var availableCpu = ResolveOfferCpu(ui.Catalog(ContractsScope), cpuName!);
            cpuName = availableCpu.Name; cpuRef = availableCpu.Ref;
            yield return VerifiedOfferCpuClick(availableCpu);
            contractStatusAvailable = ContractRead();
            contractStatusCompany = (string?)contractStatusAvailable["company"];
            contractStatusProduct = (string?)contractStatusAvailable["type"];
            contractOfferState = (bool?)contractStatusAvailable["offerSent"] == true ? "pending" : "available_not_sent";
            contractStatusReason = contractOfferState == "pending" ? "The exact current available contract shows Cancel Offer for this CPU; its decision has not yet removed it from the available UI." : "The exact current available contract does not show an offer from this CPU.";
            yield break;
        }
        if (!contractStatusAvailableComplete) { contractOfferState = "catalog_incomplete"; contractStatusReason = "The bounded available-contract catalog was incomplete, so absence cannot be established."; yield break; }
        if (request.Target.StartsWith(ContractRefPrefix, StringComparison.Ordinal))
        {
            ValidateReference(request.Target, ContractRefPrefix, "contractRef");
            contractRef = request.Target;
            if (!contractLabels.TryGetValue(contractRef, out offerName)) throw new AgentError("stale_reference", $"Contract reference '{contractRef}' is not present in the current scene. Re-run contracts-list or contracts-active; no name fallback was attempted.");
        }
        (contractStatusCompany, contractStatusProduct) = OfferNameParts(offerName!);
        if (GameUi.Controls(ui.Read(ManagerScope)).Length == 0) yield return GameUi.Click(Named(ui.Read(ContractsScope), "Signed Contracts"));
        var managerCatalog = ui.Catalog(ManagerScope);
        contractStatusActiveComplete = !((bool?)managerCatalog["more"] ?? false);
        var managerCpu = TryResolveManagerCpu(ManagerCpuRows(managerCatalog), cpuName!);
        if (managerCpu != null)
        {
            cpuName = managerCpu.Name; cpuRef = managerCpu.Ref;
            yield return VerifiedManagerCpuClick(managerCpu);
            var selectedManagerCatalog = ui.Catalog(ManagerScope);
            contractStatusActiveComplete = !((bool?)selectedManagerCatalog["more"] ?? false);
            var renderedActive = ActiveContractRows(selectedManagerCatalog);
            if (contractRef != null)
            {
                var exact = renderedActive.Where(row => row.Ref == contractRef && row.CpuRef == cpuRef).ToArray();
                if (exact.Length > 1) throw new AgentError("game_ui_mismatch", $"Contract reference '{contractRef}' matched several rendered active rows.");
                if (exact.Length == 1) { contractOfferState = "current_exact_active"; contractStatusActive = ActiveContractEntry(selectedManagerCatalog, exact[0]); contractStatusReason = "The same UI-bound contract object is currently rendered as active for the exact CPU. This is current UI evidence, not retained history after the row disappears."; yield break; }
                if (exactContractRequested)
                {
                    contractOfferState = contractStatusActiveComplete == false ? "catalog_incomplete" : "not_observable";
                    contractStatusReason = contractStatusActiveComplete == false ? "The exact contract reference is absent from the available UI, but the bounded active-contract catalog is incomplete." : "The exact contract reference is rendered in neither the available nor active UI for this CPU. No same-name fallback or historical outcome was inferred.";
                    yield break;
                }
            }
            var active = ActiveContracts(selectedManagerCatalog).OfType<JObject>().Where(row => (string?)row["company"] == contractStatusCompany && (string?)row["product"] == contractStatusProduct).ToArray();
            if (active.Length > 1) { contractOfferState = "ambiguous_active_match"; contractStatusReason = "Several current active rows match the offer's visible company/product identity."; yield break; }
            if (active.Length == 1) { contractOfferState = "current_matching_active"; contractStatusActive = active[0]; contractStatusReason = "A current active contract for this CPU matches the offer's visible company/product identity. The UI exposes no durable offer ID proving it is the same historical listing."; yield break; }
        }
        if (contractStatusActiveComplete == false) { contractOfferState = "catalog_incomplete"; contractStatusReason = "The available offer is absent, but the bounded active-contract catalog was incomplete; no outcome can be established."; }
        else contractStatusReason = "The offer is absent from the current available catalog and no matching active row is visible for this CPU. The native UI retains no rejected/expired offer history, so the historical outcome is unknown.";
    }

    private IEnumerable<Request> PrepareActiveList(Request request)
    {
        activeCpuResults.Clear();
        var catalog = ui.Catalog(ManagerScope);
        activeCatalogComplete = !((bool?)catalog["more"] ?? false);
        var rows = ManagerCpuRows(catalog);
        var requestedCpu = request.Parameters?["cpu"]?.Value<string>();
        if (requestedCpu != null) rows = new[] { ResolveManagerCpu(rows, requestedCpu) };
        activeView = request.Parameters?["view"]?.Value<string>() ?? (requestedCpu == null ? "names" : "full");
        if (activeView == "names")
        {
            foreach (var row in rows) activeCpuResults.Add(ActiveCpuEntry(row, null));
            yield break;
        }
        foreach (var row in rows)
        {
            var current = ResolveManagerCpu(ui.Catalog(ManagerScope), row.Ref);
            var selectCpu = VerifiedManagerCpuClick(current); selectCpu.SettleFrames = 1; yield return selectCpu;
            activeCpuResults.Add(ActiveCpuEntry(row, ActiveContracts(ui.Catalog(ManagerScope))));
        }
    }

    private IEnumerable<Request> PrepareBusiness(Request request)
    {
        companyName = request.Target;
        var negotiationAlreadyOpen = GameUi.Controls(ui.Read(NegotiationScope)).Length > 0;
        if (negotiationAlreadyOpen && request.Command is "game.business-negotiate" or "game.business-sign")
        {
            EnsureNegotiationCompany(ui.Read(NegotiationScope));
            if (request.Command == "game.business-sign") ResetBusinessSign(false);
            foreach (var step in ConfigureNegotiation(request)) yield return step;
            if (request.Command == "game.business-sign") foreach (var step in SignBusinessContract()) yield return step;
            yield break;
        }
        if (negotiationAlreadyOpen) throw new AgentError("not_interactable", "A native business negotiation draft is open. It was left intact; finish or close it before using another business command.");
        if (GameUi.Controls(ui.Read(BreakScope)).Length > 0) throw new AgentError("not_interactable", "A native Break Contract confirmation is already open. It was not reused or closed.");
        var inspector = ui.Read(InspectorScope);
        if (GameUi.Controls(inspector).Length > 0)
        {
            if (request.Command is "game.business-contracts" or "game.business-break")
            {
                var companies = Named(inspector, "Company Dropdown");
                EnsureOption(companies, companyName!, "company");
                if (Selected(companies) != companyName) yield return GameUi.Select(companies, companyName!);
                if (request.Command == "game.business-break") foreach (var step in BreakBusinessContract(request)) yield return step;
                yield break;
            }
            yield return GameUi.Click(Named(inspector, "Close"));
        }
        foreach (var step in OpenBusiness()) yield return step;
        if (request.Command == "game.business-list") yield break;
        yield return GameUi.Click(Exact(NamedRows(ui.Catalog(BusinessScope), "Company UI"), companyName!, "catalog company"));
        if (request.Command == "game.business-read") yield break;
        if (request.Command is "game.business-contracts" or "game.business-break")
        {
            yield return GameUi.Click(Named(ui.Read(BusinessScope), "Inspect Contracts"));
            if (request.Command == "game.business-break") foreach (var step in BreakBusinessContract(request)) yield return step;
            yield break;
        }
        if (request.Command == "game.business-sign")
        {
            ResetBusinessSign(true);
            yield return GameUi.Click(Named(ui.Read(BusinessScope), "Inspect Contracts"));
            var before = ui.Catalog(InspectorScope);
            if (Selected(Named(before, "Company Dropdown")) != companyName) throw new AgentError("target_mismatch", $"Contract inspector opened for a different company, not '{companyName}'. Nothing was signed.");
            var beforeRows = InspectorContracts(before).OfType<JObject>().ToArray(); businessContractsBeforeCount = beforeRows.Length;
            foreach (var row in beforeRows) businessContractsBefore.Add(ContractSignature(row));
            yield return GameUi.Click(Named(ui.Read(InspectorScope), "Close"));
        }
        yield return GameUi.Click(Named(ui.Read(BusinessScope), "New Contract"));
        foreach (var step in ConfigureNegotiation(request)) yield return step;
        if (request.Command == "game.business-sign") foreach (var step in SignBusinessContract()) yield return step;
    }

    private IEnumerable<Request> SignBusinessContract()
    {
        var negotiation = ui.Read(NegotiationScope);
        var acceptance = TextAt(negotiation, "/Background/Contract");
        if (!AcceptanceWillSign(acceptance)) throw new AgentError("not_interactable", $"Native negotiation says '{acceptance}'. Contract was not signed.");
        expectedBusinessContract = ExpectedBusinessContract(negotiation);
        yield return GameUi.Click(Named(negotiation, "Sign Contract"));
        if (GameUi.Controls(ui.Read(NegotiationScope)).Length > 0) { businessSignVerificationReason = "Negotiation remained open after native Sign Contract."; yield break; }
        var inspect = GameUi.Controls(ui.Read(BusinessScope)).SingleOrDefault(c => (string?)c["name"] == "Inspect Contracts");
        if (inspect == null || inspect["blockedReason"] != null) { businessSignVerificationReason = "Company Contracts could not be reopened after Sign Contract."; yield break; }
        yield return GameUi.Click(inspect);
        VerifyBusinessSign();
    }

    private IEnumerable<Request> BreakBusinessContract(Request request)
    {
        businessContractIdentity = request.Parameters!["contract"]!.Value<string>();
        businessBreakConfirmed = request.Parameters?["confirm"]?.Value<bool>() == true;
        var inspector = ui.Catalog(InspectorScope);
        var titles = BusinessContractTitleRows(inspector);
        businessBreakBeforeCount = titles.Length;
        businessBreakBeforeComplete = !((bool?)inspector["more"] ?? false);
        var row = GameUi.One(titles.Where(t => BusinessContractIdentity((string?)t["text"] ?? "", BusinessContractFields(inspector, (string?)t["group"])) == businessContractIdentity), $"business contract '{businessContractIdentity}'");
        var breaker = GameUi.One(GameUi.Controls(inspector).Where(c => (string?)c["group"] == (string?)row["group"] && (string?)c["name"] == "Break Contract"), $"Break Contract for '{businessContractIdentity}'");
        yield return GameUi.Click(breaker);
        var dialog = ui.Read(BreakScope);
        if (GameUi.Controls(dialog).Length == 0) throw new AgentError("confirmation_missing", "The expected native Break Contract confirmation did not open; nothing was confirmed.");
        businessBreakConfirmationText = KnownBreakConfirmation(dialog);
        businessBreakFine = BreakFine(businessBreakConfirmationText);
        yield return GameUi.Click(Named(dialog, businessBreakConfirmed ? "Break Contract" : "Cancel"));
    }

    private IEnumerable<Request> ConfigureNegotiation(Request request)
    {
        var view = ui.Read(NegotiationScope);
        if (GameUi.Controls(view).Length == 0) throw new AgentError("game_ui_mismatch", "The expected Contract Negotiation window did not open.");
        var playerRole = request.Parameters?["playerRole"]?.Value<string>();
        var provider = TextAt(view, "/ProviderCompany/ProviderName");
        var client = TextAt(view, "/ClientCompany/ClientName");
        EnsureNegotiationCompany(view);
        var playerIsProvider = PartyName(client) == companyName;
        if (playerRole != null && (playerRole == "provider") != playerIsProvider)
        {
            yield return GameUi.Click(Named(view, "Swap"));
            view = ui.Read(NegotiationScope);
            provider = TextAt(view, "/ProviderCompany/ProviderName"); client = TextAt(view, "/ClientCompany/ClientName");
        }
        if (playerRole != null)
        {
            var counterpart = playerRole == "provider" ? client : provider;
            if (PartyName(counterpart) != companyName) throw new AgentError("target_mismatch", $"Negotiation is not with '{companyName}' in the requested role. No terms were changed.");
        }
        if (request.Parameters?["durationYears"] is JToken duration)
        {
            var option = duration.Value<int>() == 1 ? "1 Year" : $"{duration.Value<int>()} Years";
            var control = Named(ui.Read(NegotiationScope), "Duration Dropdown");
            if (Selected(control) != option) yield return GameUi.Select(control, option);
        }
        foreach (var pair in new[] { ("exclusivity", "Exclusivity Deal"), ("renew", "Renew Contract") })
        {
            var value = request.Parameters?[pair.Item1]; if (value == null) continue;
            var control = Named(ui.Read(NegotiationScope), pair.Item2);
            if (!JToken.DeepEquals(control["value"], value)) yield return GameUi.Set(control, value);
        }
        foreach (var pair in new[] { ("productionPercent", "ProductionCapacitySlider"), ("markupPercent", "MarkupPriceSlider") })
        {
            var value = request.Parameters?[pair.Item1]; if (value == null) continue;
            var control = GameUi.One(GameUi.Controls(ui.Read(NegotiationScope)).Where(c => ((string?)c["context"] ?? "").EndsWith("/" + pair.Item2, StringComparison.Ordinal)), pair.Item1);
            if (!NumericEquals(control["value"], value)) yield return GameUi.Set(control, value);
        }
        view = ui.Read(NegotiationScope);
        if (request.Parameters?["durationYears"] is JToken expectedDuration)
        {
            var expected = expectedDuration.Value<int>() == 1 ? "1 Year" : $"{expectedDuration.Value<int>()} Years";
            if (Selected(Named(view, "Duration Dropdown")) != expected) throw new AgentError("value_not_applied", $"Native validation did not retain durationYears={expectedDuration}.");
        }
        foreach (var pair in new[] { ("exclusivity", "Exclusivity Deal"), ("renew", "Renew Contract") }) if (request.Parameters?[pair.Item1] is JToken expected && !JToken.DeepEquals(Named(view, pair.Item2)["value"], expected)) throw new AgentError("value_not_applied", $"Native validation did not retain {pair.Item1}={expected}.");
        foreach (var pair in new[] { ("productionPercent", "ProductionCapacitySlider"), ("markupPercent", "MarkupPriceSlider") }) if (request.Parameters?[pair.Item1] is JToken expected && !NumericEquals(SliderNode(view, pair.Item2)["value"], expected)) throw new AgentError("value_not_applied", $"Native validation did not retain {pair.Item1}={expected}.");
    }

    public JObject Result(Request request)
    {
        return request.Command switch
        {
            "game.contracts-list" => ContractList(),
            "game.contracts-read" => ContractRead(),
            "game.contracts-send" => OfferMutation(true),
            "game.contracts-cancel" => OfferMutation(false),
            "game.contracts-status" => ContractStatus(),
            "game.contracts-active" or "game.contracts-manage" => ActiveList(request),
            "game.contracts-break" => BreakResult(),
            "game.business-list" or "game.business-read" => BusinessList(request.Command == "game.business-read" ? companyName : null),
            "game.business-contracts" => BusinessContracts(),
            "game.business-negotiate" => Negotiation(false),
            "game.business-sign" => BusinessSignResult(),
            "game.business-break" => BusinessBreakResult(),
            _ => throw new AgentError("unsupported_command", request.Command)
        };
    }

    private JObject ContractList()
    {
        return new JObject
        {
            ["method"] = "game", ["view"] = listView, ["contracts"] = listedContracts.DeepClone(), ["count"] = listedContracts.Count, ["catalogCount"] = listCatalogCount,
            ["evaluatedCpu"] = cpuName, ["evaluatedCpuRef"] = cpuRef, ["catalogComplete"] = listCatalogComplete, ["evaluationComplete"] = listEvaluationComplete, ["complete"] = listCatalogComplete && listEvaluationComplete,
            ["incompleteReason"] = listIncompleteReason, ["next"] = listView == "names" ? "Use --view summary or full, optionally with --cpu EXACT_CPU, to inspect every matching contract in one call." : "Use contracts-send with an exact returned contract and CPU; this list did not mutate offers."
        };
    }

    private JObject ContractEntry(ContractRow contract, bool full, bool evaluated)
    {
        var view = ui.Read(ContractsScope);
        var summary = TextAt(view, "/BackgroundRight/Contract").Split('|').Select(s => s.Trim()).ToArray();
        var send = GameUi.Controls(view).SingleOrDefault(c => (string?)c["name"] == "Send Offer");
        var cancel = GameUi.Controls(view).SingleOrDefault(c => (string?)c["name"] == "Cancel Offer");
        var requirements = evaluated ? Fields(view, "/ContractData/") : new JObject();
        var cpuDetails = evaluated ? Fields(view, "/CPUData/") : new JObject();
        var entry = new JObject
        {
            ["contractRef"] = contract.Ref, ["name"] = contract.Name, ["lifecycleStage"] = "available", ["company"] = summary.ElementAtOrDefault(0), ["type"] = summary.ElementAtOrDefault(1), ["evaluatedCpu"] = evaluated ? new JValue(cpuName) : JValue.CreateNull(), ["evaluatedCpuRef"] = evaluated ? new JValue(cpuRef) : JValue.CreateNull(),
            ["status"] = evaluated ? cancel != null ? "offer_sent" : "not_sent" : "not_evaluated", ["offerSent"] = evaluated ? new JValue(cancel != null) : JValue.CreateNull(),
            ["offerState"] = evaluated ? cancel != null ? "pending" : "not_sent" : "not_evaluated", ["decisionDate"] = evaluated ? requirements["acceptanceDate"]?.DeepClone() : null,
            ["requirementsMatch"] = evaluated ? new JValue(RequirementsMatch(cpuDetails)) : JValue.CreateNull(),
            ["sendAllowed"] = evaluated ? new JValue(send != null && send["blockedReason"] == null || cancel != null) : JValue.CreateNull(),
            ["sendEnabled"] = evaluated ? new JValue(send != null && send["blockedReason"] == null) : JValue.CreateNull(), ["sendBlockedReason"] = evaluated ? send?["blockedReason"]?.DeepClone() : null,
            ["chance"] = requirements["probability"]?.DeepClone()
        };
        if (full)
        {
            entry["description"] = summary.ElementAtOrDefault(2);
            entry["requirements"] = requirements;
            entry["cpuDetails"] = cpuDetails;
        }
        return entry;
    }

    private JObject ContractRead()
    {
        var view = ui.Read(ContractsScope);
        var send = GameUi.Controls(view).SingleOrDefault(c => (string?)c["name"] == "Send Offer");
        var cancel = GameUi.Controls(view).SingleOrDefault(c => (string?)c["name"] == "Cancel Offer");
        var summary = TextAt(view, "/BackgroundRight/Contract").Split('|').Select(s => s.Trim()).ToArray();
        var cpuDetails = Fields(view, "/CPUData/");
        var requirements = Fields(view, "/ContractData/");
        var cpuRows = OfferCpuRows(ui.Catalog(ContractsScope));
        return new JObject
        {
            ["method"] = "game", ["contractRef"] = contractRef, ["offer"] = offerName, ["company"] = summary.ElementAtOrDefault(0), ["type"] = summary.ElementAtOrDefault(1), ["description"] = summary.ElementAtOrDefault(2),
            ["lifecycleStage"] = "available", ["cpu"] = cpuName, ["cpuRef"] = cpuRef, ["cpuChoices"] = RowNames(cpuRows.Select(row => row.Node)), ["cpus"] = new JArray(cpuRows.Select(row => new JObject { ["cpuRef"] = row.Ref, ["name"] = row.Name })), ["cpuDetails"] = cpuDetails, ["requirements"] = requirements,
            ["requirementsMatch"] = cpuName == null ? JValue.CreateNull() : new JValue(RequirementsMatch(cpuDetails)), ["sendAllowed"] = cpuName == null ? JValue.CreateNull() : new JValue(send != null && send["blockedReason"] == null || cancel != null), ["sendEnabled"] = cpuName == null ? JValue.CreateNull() : new JValue(send != null && send["blockedReason"] == null), ["offerSent"] = cancel != null,
            ["offerState"] = cpuName == null ? "not_evaluated" : cancel != null ? "pending" : "not_sent", ["decisionDate"] = requirements["acceptanceDate"]?.DeepClone(), ["outcomeKnown"] = false,
            ["sendBlockedReason"] = send?["blockedReason"]?.DeepClone(), ["next"] = cpuName == null ? "Choose an exact released CPU with --cpu to evaluate eligibility. Without one, acceptance probability, required production lines and fines are unknown: the price and volume alone do not show whether this offer is reachable. Compare the units per month with your production capacity (cpu-review capacityEstimate) before planning around it." : cancel != null ? "This offer is pending until the displayed acceptance date. Use contracts-status later; Cancel only withdraws while it remains available." : send?["blockedReason"] == null ? "Use game contracts-send with the same offer and CPU." : "This CPU is rejected by the native requirements; choose another CPU."
        };
    }

    private JObject OfferMutation(bool sent)
    {
        var read = ContractRead();
        var observed = (bool?)read["offerSent"] == sent;
        read["outcome"] = observed ? "completed" : "input_dispatched";
        read["changed"] = observed;
        read["offerMutationVerified"] = observed;
        read["contractOutcomeKnown"] = false;
        read["next"] = observed ? sent ? "Offer submission is verified and currently pending, not accepted. Recheck with contracts-status at or after decisionDate." : "Offer withdrawal is verified; this does not describe any earlier selection outcome." : "The native input was dispatched but its postcondition is not visible; inspect before retrying.";
        return read;
    }

    private JObject ContractStatus()
    {
        var pending = contractOfferState == "pending";
        var activeMatch = contractOfferState is "current_matching_active" or "current_exact_active";
        var exactActive = contractOfferState == "current_exact_active";
        return new JObject
        {
            ["method"] = "game", ["contractRef"] = contractRef, ["offer"] = offerName, ["cpu"] = cpuName, ["cpuRef"] = cpuRef, ["status"] = contractOfferState, ["statusScope"] = "current_native_ui",
            ["pending"] = pending, ["matchingActive"] = activeMatch, ["outcomeKnown"] = exactActive, ["historicalOutcome"] = null,
            ["company"] = contractStatusCompany, ["product"] = contractStatusProduct,
            ["available"] = contractStatusAvailable == null ? null : new JObject { ["decisionDate"] = contractStatusAvailable["decisionDate"]?.DeepClone(), ["requirementsMatch"] = contractStatusAvailable["requirementsMatch"]?.DeepClone(), ["sendAllowed"] = contractStatusAvailable["sendAllowed"]?.DeepClone(), ["offerSent"] = contractStatusAvailable["offerSent"]?.DeepClone() }, ["activeContract"] = contractStatusActive?.DeepClone(),
            ["reason"] = contractStatusReason, ["historyAvailable"] = false, ["availableCatalogComplete"] = contractStatusAvailableComplete, ["activeCatalogComplete"] = contractStatusActiveComplete == null ? null : new JValue(contractStatusActiveComplete.Value), ["durableOfferIdAvailable"] = false,
            ["matchBasis"] = exactActive ? "The available and active rendered UI components bind the same contract object by reference identity." : activeMatch ? "ContractButton renders '{Company} {ContractType}'; ActiveContractUI separately renders the same Company and ContractType for the exact CPU." : null,
            ["next"] = pending ? "The offer is still pending. Recheck at or after its displayed acceptance date." : exactActive ? "The exact UI-bound contract is currently active; use contracts-manage for live details. This reference is not retained history after the row disappears." : activeMatch ? "A text-matching contract is currently active; use contracts-manage for live details, but do not treat this as proof about a same-named older listing." : contractOfferState == "available_not_sent" ? "Use contracts-read for full requirements or contracts-send to submit an offer; no offer is currently pending for this CPU." : "Do not infer rejected versus expired from absence. The game exposes no durable offer history; this command reports only current available/active UI evidence."
        };
    }

    private JObject ActiveList(Request request)
    {
        if (request.Command == "game.contracts-active") return new JObject { ["method"] = "game", ["view"] = activeView, ["catalogComplete"] = activeCatalogComplete, ["cpus"] = activeCpuResults.DeepClone(), ["cpuCount"] = activeCpuResults.Count, ["contractCount"] = activeCpuResults.OfType<JObject>().Sum(cpu => cpu["contracts"] is JArray contracts ? contracts.Count : cpu["contractCount"]?.Value<int>() ?? 0), ["next"] = activeView == "names" ? "Use contracts-active --view full to aggregate active contracts for every CPU, or add --cpu EXACT_CPU." : "Break an active contract with its exact identity, CPU and --confirm true." };
        var view = ui.Catalog(ManagerScope);
        var cpuRows = ManagerCpuRows(view); var contracts = ActiveContracts(view);
        return new JObject { ["method"] = "game", ["catalogComplete"] = !((bool?)view["more"] ?? false), ["cpus"] = new JArray(cpuRows.Select(row => new JObject { ["cpuRef"] = row.Ref, ["name"] = row.Name })), ["cpuCount"] = cpuRows.Length, ["selectedCpu"] = cpuName, ["selectedCpuRef"] = cpuRef, ["contracts"] = contracts, ["contractCount"] = contracts.Count, ["next"] = "Use game contracts-manage EXACT_CPU; breaking requires an exact returned contract identity and --confirm true." };
    }

    private static JObject ActiveCpuEntry(CpuRow row, JArray? contracts)
    {
        var parts = ((string?)row.Node["label"] ?? "").Split('|').Select(s => s.Trim()).ToArray();
        var entry = new JObject { ["cpuRef"] = row.Ref, ["name"] = row.Name, ["contractCount"] = ParseLeadingInt(parts.ElementAtOrDefault(1)) };
        if (contracts != null) entry["contracts"] = contracts;
        return entry;
    }

    private JObject BreakResult()
    {
        var manager = ui.Catalog(ManagerScope);
        var rows = ActiveContractRows(manager);
        var afterCount = rows.Length;
        var remaining = rows.Count(r => r.Ref == contractBreakRef && r.CpuRef == cpuRef);
        var modalClosed = GameUi.Controls(ui.Read(BreakScope)).Length == 0;
        var managerReadable = GameUi.Controls(manager).Length > 0;
        var catalogComplete = contractBreakBeforeComplete && !((bool?)manager["more"] ?? false);
        var verified = contractBreakConfirmed && modalClosed && managerReadable && catalogComplete && remaining == 0 && afterCount == contractBreakBeforeCount - 1;
        var previewVerified = !contractBreakConfirmed && modalClosed && managerReadable && catalogComplete && remaining == 1 && afterCount == contractBreakBeforeCount;
        return new JObject
        {
            ["method"] = "game", ["outcome"] = contractBreakConfirmed ? verified ? "completed" : "input_dispatched" : previewVerified ? "preview" : "partial_failure", ["contractRef"] = contractBreakRef, ["contract"] = offerName, ["cpuRef"] = cpuRef, ["cpu"] = cpuName,
            ["confirmationText"] = contractBreakConfirmationText, ["fineDisplay"] = contractBreakFine, ["confirmed"] = contractBreakConfirmed, ["broken"] = verified, ["modalClosed"] = modalClosed, ["managerReadable"] = managerReadable, ["catalogComplete"] = catalogComplete,
            ["beforeCount"] = contractBreakBeforeCount, ["afterCount"] = afterCount, ["matchingRowsRemaining"] = remaining,
            ["next"] = contractBreakConfirmed ? verified ? "The exact active contract row disappeared and the selected CPU's row count decreased by one." : "Native confirmation was dispatched, but exact row disappearance was not verified. Inspect before retrying." : previewVerified ? "The native fine was previewed, cancellation closed the modal, and the exact row remained. Repeat with --confirm true to commit." : "The preview cancellation postcondition was not fully verified. Inspect before retrying."
        };
    }

    private JObject BusinessList(string? selected)
    {
        var view = ui.Catalog(BusinessScope); var rows = NamedRows(view, "Company UI"); var companies = new JArray();
        foreach (var row in rows.Where(r => Identity(r) != "Company UI"))
        {
            var group = (string?)row["group"]; var texts = GameUi.Texts(view).Where(t => (string?)t["group"] == group).ToArray();
            companies.Add(new JObject { ["name"] = Identity(row), ["netWorthDisplay"] = ValueFrom(texts, "/NetWorth"), ["contracts"] = ParseLeadingInt(ValueFrom(texts, "/Contracts")), ["selected"] = Identity(row) == selected });
        }
        return new JObject { ["method"] = "game", ["catalogComplete"] = !((bool?)view["more"] ?? false), ["companies"] = companies, ["next"] = "Use business-read, business-contracts or business-negotiate with an exact company name." };
    }

    private JObject BusinessContracts()
    {
        var view = ui.Catalog(InspectorScope); var select = Named(view, "Company Dropdown");
        return new JObject { ["method"] = "game", ["company"] = Selected(select), ["catalogComplete"] = !((bool?)view["more"] ?? false), ["contracts"] = InspectorContracts(view), ["next"] = "Use business-break with this company and an exact returned identity. Contracts not involving the player remain natively disabled." };
    }

    private JObject BusinessBreakResult()
    {
        var view = ui.Catalog(InspectorScope);
        var identities = InspectorContracts(view).OfType<JObject>().Select(row => (string?)row["identity"]).ToArray();
        var afterCount = identities.Length;
        var remaining = identities.Count(identity => identity == businessContractIdentity);
        var modalClosed = GameUi.Controls(ui.Read(BreakScope)).Length == 0;
        var inspectorReady = GameUi.Controls(view).Length > 0 && Selected(Named(view, "Company Dropdown")) == companyName;
        var catalogComplete = businessBreakBeforeComplete && !((bool?)view["more"] ?? false);
        var verified = businessBreakConfirmed && modalClosed && inspectorReady && catalogComplete && remaining == 0 && afterCount == businessBreakBeforeCount - 1;
        var previewVerified = !businessBreakConfirmed && modalClosed && inspectorReady && catalogComplete && remaining == 1 && afterCount == businessBreakBeforeCount;
        return new JObject
        {
            ["method"] = "game", ["outcome"] = businessBreakConfirmed ? verified ? "completed" : "input_dispatched" : previewVerified ? "preview" : "partial_failure", ["company"] = companyName, ["contract"] = businessContractIdentity,
            ["confirmationText"] = businessBreakConfirmationText, ["fineDisplay"] = businessBreakFine, ["confirmed"] = businessBreakConfirmed, ["broken"] = verified, ["modalClosed"] = modalClosed, ["catalogComplete"] = catalogComplete,
            ["beforeCount"] = businessBreakBeforeCount, ["afterCount"] = afterCount, ["matchingRowsRemaining"] = remaining,
            ["next"] = businessBreakConfirmed ? verified ? "The exact business contract row disappeared and the inspector count decreased by one." : "Native confirmation was dispatched, but exact row disappearance was not verified. Inspect before retrying." : previewVerified ? "The native fine was previewed, cancellation closed the modal, and the exact row remained. Repeat with --confirm true to commit." : "The preview cancellation postcondition was not fully verified. Inspect before retrying."
        };
    }

    private JObject Negotiation(bool signed)
    {
        var view = ui.Read(NegotiationScope);
        if (signed && GameUi.Controls(view).Length == 0) return new JObject { ["method"] = "game", ["outcome"] = "input_dispatched", ["company"] = companyName, ["signed"] = null, ["next"] = "The negotiation closed after Sign Contract. Inspect company contracts to verify the new agreement." };
        var sign = Named(view, "Sign Contract");
        var acceptance = TextAt(view, "/Background/Contract");
        var canSign = sign["blockedReason"] == null && AcceptanceWillSign(acceptance);
        return new JObject
        {
            ["method"] = "game", ["outcome"] = signed ? "input_dispatched" : "preview", ["company"] = companyName,
            ["provider"] = TextAt(view, "/ProviderCompany/ProviderName"), ["client"] = TextAt(view, "/ClientCompany/ClientName"),
            ["duration"] = Selected(Named(view, "Duration Dropdown")), ["productionCapacity"] = Slider(view, "ProductionCapacitySlider"), ["markup"] = Slider(view, "MarkupPriceSlider"),
            ["exclusivity"] = Named(view, "Exclusivity Deal")["value"]!.DeepClone(), ["renew"] = Named(view, "Renew Contract")["value"]!.DeepClone(),
            ["acceptance"] = acceptance, ["acceptanceScore"] = AcceptanceScore(acceptance), ["acceptanceWillSign"] = AcceptanceWillSign(acceptance), ["scoreReasons"] = TextAt(view, "/ScoreTexts"), ["providerObligations"] = TextAt(view, "/ProviderCompany/TextContainer"), ["clientObligations"] = TextAt(view, "/ClientCompany/TextContainer"),
            ["nativeAvailable"] = sign["blockedReason"] == null, ["canSign"] = canSign, ["blockedReason"] = sign["blockedReason"]?.DeepClone(), ["signed"] = false,
            ["next"] = canSign ? "Review the displayed obligations, then use game business-sign with the intended terms." : "The native game will not accept these terms; adjust them or choose another company."
        };
    }

    private JObject BusinessSignResult()
    {
        var verified = verifiedBusinessContract != null;
        var unverifiedTerms = new JArray("renew");
        if (expectedBusinessContract?["royalties"]?.Type != JTokenType.String) unverifiedTerms.Add("royalties");
        return new JObject
        {
            ["method"] = "game", ["outcome"] = verified ? "completed" : "input_dispatched", ["company"] = companyName, ["signed"] = verified, ["verified"] = verified,
            ["baselineAvailable"] = businessBaselineAvailable, ["verificationMode"] = businessBaselineAvailable ? "new_row_against_baseline" : "unique_exact_postcondition", ["newRowVerified"] = verified && businessBaselineAvailable,
            ["beforeCount"] = businessBaselineAvailable ? new JValue(businessContractsBeforeCount) : JValue.CreateNull(), ["afterCount"] = businessContractsAfter, ["expected"] = expectedBusinessContract?.DeepClone(), ["contract"] = verifiedBusinessContract?.DeepClone(), ["unverifiedTerms"] = unverifiedTerms,
            ["verificationReason"] = businessSignVerificationReason, ["next"] = verified ? businessBaselineAvailable ? "The new native Company Contracts row matches the negotiated parties and displayed terms." : "A unique current Company Contracts row matches the signed draft; no pre-sign baseline was available because the draft was already open." : "Sign was dispatched, but no unique matching Company Contracts row was verified. Inspect before retrying; do not sign blindly."
        };
    }

    private JObject ExpectedBusinessContract(JObject negotiation)
    {
        var provider = PartyName(TextAt(negotiation, "/ProviderCompany/ProviderName"));
        var client = PartyName(TextAt(negotiation, "/ClientCompany/ClientName"));
        var otherIsProvider = provider != companyName;
        var clientObligations = TextAt(negotiation, "/ClientCompany/TextContainer");
        var termination = Regex.Match(clientObligations, @"lasts until\s+([A-Za-z]+\s+\d{4})", RegexOptions.IgnoreCase);
        var royalties = Regex.Match(clientObligations, @"royalties of\s+([\d.]+%)", RegexOptions.IgnoreCase);
        return new JObject
        {
            ["counterparty"] = otherIsProvider ? provider : client, ["counterpartyRole"] = otherIsProvider ? "Provider" : "Client", ["type"] = "Foundry Services",
            ["productionCapacity"] = Slider(negotiation, "ProductionCapacitySlider")["display"]!.DeepClone(), ["markup"] = Slider(negotiation, "MarkupPriceSlider")["display"]!.DeepClone(),
            ["exclusive"] = Named(negotiation, "Exclusivity Deal")["value"]!.Value<bool>() ? "Yes" : "No", ["royalties"] = royalties.Success ? royalties.Groups[1].Value : null, ["terminationDate"] = termination.Success ? termination.Groups[1].Value : null
        };
    }

    private void VerifyBusinessSign()
    {
        var after = ui.Catalog(InspectorScope);
        if (GameUi.Controls(after).Length == 0) { businessSignVerificationReason = "Company Contracts did not open after Sign Contract."; return; }
        if (Selected(Named(after, "Company Dropdown")) != companyName) { businessSignVerificationReason = "Company Contracts reopened for a different company."; return; }
        var rows = InspectorContracts(after).OfType<JObject>().ToArray(); businessContractsAfter = rows.Length;
        if (!businessBaselineAvailable)
        {
            var matching = rows.Where(row => BusinessContractMatches(row, expectedBusinessContract!)).ToArray();
            if (matching.Length != 1) { businessSignVerificationReason = $"Expected one current contract row matching the signed draft, found {matching.Length}; no pre-sign baseline was available."; return; }
            verifiedBusinessContract = matching[0]; businessSignVerificationReason = null; return;
        }
        var added = rows.Where(row => !businessContractsBefore.Contains(ContractSignature(row))).ToArray();
        if (added.Length != 1) { businessSignVerificationReason = $"Expected one new contract row, found {added.Length}."; return; }
        var candidate = added[0];
        if (!BusinessContractMatches(candidate, expectedBusinessContract!)) { businessSignVerificationReason = "A new contract row appeared, but its parties or displayed terms do not match the negotiation."; return; }
        verifiedBusinessContract = candidate; businessSignVerificationReason = null;
    }

    private void ResetBusinessSign(bool baselineAvailable)
    {
        businessContractsBefore.Clear(); verifiedBusinessContract = null; expectedBusinessContract = null; businessSignVerificationReason = null;
        businessContractsBeforeCount = 0; businessContractsAfter = 0; businessBaselineAvailable = baselineAvailable;
    }

    private void EnsureNegotiationCompany(JObject view)
    {
        var provider = PartyName(TextAt(view, "/ProviderCompany/ProviderName"));
        var client = PartyName(TextAt(view, "/ClientCompany/ClientName"));
        if (provider != companyName && client != companyName) throw new AgentError("target_mismatch", $"The open negotiation is not with '{companyName}'. No terms were changed and the open draft was left intact.");
    }

    private IEnumerable<Request> OpenContracts()
    {
        if (GameUi.Controls(ui.Read(ContractsScope)).Length == 0) yield return GameUi.Click(Named(ui.Read("DesktopButtons"), "Contracts Desktop"));
    }
    private IEnumerable<Request> OpenBusiness()
    {
        if (GameUi.Controls(ui.Read(BusinessScope)).Length == 0) yield return GameUi.Click(Named(ui.Read("DesktopButtons"), "Business Desktop"));
    }
    private static JObject Named(JObject view, string name) => GameUi.One(GameUi.Controls(view).Where(c => (string?)c["name"] == name), name);
    private static JObject[] NamedRows(JObject view, string name) => GameUi.Controls(view).Where(c => (string?)c["name"] == name && c["group"] != null).ToArray();
    private static JObject Exact(IEnumerable<JObject> rows, string name, string description) => GameUi.One(rows.Where(r => Identity(r) == name && !name.Contains('…')), $"{description} '{name}'");
    private static string Identity(JObject row) => ((string?)row["label"] ?? (string?)row["text"] ?? "").Split('|')[0].Trim();
    private static string ActiveIdentity(JObject row)
    {
        var parts = ((string?)row["text"] ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).Take(2).ToArray();
        return string.Join(" / ", parts);
    }
    private static (string? Company, string? Product) OfferNameParts(string offer)
    {
        foreach (var product in new[] { "Calculator", "Computer", "Console", "Phone" })
        {
            var suffix = " " + product;
            if (offer.EndsWith(suffix, StringComparison.Ordinal) && offer.Length > suffix.Length) return (offer.Substring(0, offer.Length - suffix.Length), product);
        }
        return (null, null);
    }
    private static JArray RowNames(IEnumerable<JObject> rows) => new JArray(rows.Select(r => (JToken)Identity(r)).Distinct());
    private sealed class IdentityToken { public string Value { get; } public IdentityToken(string value) => Value = value; }
    private sealed class ContractRow { public JObject Node = null!; public MonoBehaviour Component = null!; public object Contract = null!; public int Scene; public string Name = ""; public string Ref = ""; }
    private sealed class CpuRow { public JObject Node = null!; public MonoBehaviour Component = null!; public object Cpu = null!; public int Scene; public string Name = ""; public string Ref = ""; }
    private sealed class ActiveContractRow { public JObject Node = null!; public MonoBehaviour Component = null!; public object Contract = null!; public object Cpu = null!; public int Scene; public string Group = ""; public string Name = ""; public string Ref = ""; public string CpuRef = ""; }

    private static ContractRow[] AvailableContractRows(JObject view)
    {
        var components = UiComponents("ProcessorTycoon.ContractSystem.UI.ContractButton", "ProcessorTycoon.ContractSystem.UI.ContractWindow").ToDictionary(ComponentGroup, StringComparer.Ordinal);
        return NamedRows(view, "Contract").Select(node =>
        {
            var group = (string)node["group"]!;
            if (!components.TryGetValue(group, out var component)) throw new AgentError("game_ui_mismatch", $"Rendered contract group '{group}' has no ContractButton binding.");
            var contract = BoundProperty(component, "Contract"); var scene = component.gameObject.scene.handle;
            var name = Identity(node); var reference = IdentityRef(contract, ContractRefPrefix, name, scene);
            contractLabels[reference] = name;
            return new ContractRow { Node = node, Component = component, Contract = contract, Scene = scene, Name = name, Ref = reference };
        }).ToArray();
    }

    private static CpuRow[] OfferCpuRows(JObject view) => CpuRows(view, "Offer", "ProcessorTycoon.ContractSystem.UI.OfferButton", "ProcessorTycoon.ContractSystem.UI.ContractWindow");
    private static CpuRow[] ManagerCpuRows(JObject view) => CpuRows(view, ManagerCpuRow, "ProcessorTycoon.ContractSystem.UI.CpuButton", "ProcessorTycoon.ContractSystem.UI.ContractManagementWindow");

    private static CpuRow[] CpuRows(JObject view, string rowName, string componentType, string windowType)
    {
        var components = UiComponents(componentType, windowType).ToDictionary(ComponentGroup, StringComparer.Ordinal);
        return NamedRows(view, rowName).Select(node =>
        {
            var group = (string)node["group"]!;
            if (!components.TryGetValue(group, out var component)) throw new AgentError("game_ui_mismatch", $"Rendered CPU group '{group}' has no {componentType.Split('.').Last()} binding.");
            var cpu = BoundProperty(component, "Cpu"); var scene = component.gameObject.scene.handle;
            return new CpuRow { Node = node, Component = component, Cpu = cpu, Scene = scene, Name = Identity(node), Ref = IdentityRef(cpu, CpuRefPrefix, null, scene) };
        }).ToArray();
    }

    private static ActiveContractRow[] ActiveContractRows(JObject view)
    {
        var components = UiComponents("ProcessorTycoon.ContractSystem.UI.ActiveContractUI", "ProcessorTycoon.ContractSystem.UI.ContractManagementWindow").ToDictionary(ComponentGroup, StringComparer.Ordinal);
        return GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains("/ActiveContractsScroll/") && ((string?)t["context"] ?? "").EndsWith("ActiveContractUI(Clone)", StringComparison.Ordinal)).Select(node =>
        {
            var group = (string)node["group"]!;
            if (!components.TryGetValue(group, out var component)) throw new AgentError("game_ui_mismatch", $"Rendered active-contract group '{group}' has no ActiveContractUI binding.");
            var contract = BoundField(component, "contract"); var cpu = BoundField(component, "cpu"); var scene = component.gameObject.scene.handle;
            var name = ActiveIdentity(node); var reference = IdentityRef(contract, ContractRefPrefix, name, scene);
            contractLabels[reference] = name.Replace(" / ", " ");
            return new ActiveContractRow { Node = node, Component = component, Contract = contract, Cpu = cpu, Scene = scene, Group = group, Name = name, Ref = reference, CpuRef = IdentityRef(cpu, CpuRefPrefix, null, scene) };
        }).ToArray();
    }

    private static ContractRow ResolveAvailableContract(JObject view, string target) => ResolveAvailableContract(AvailableContractRows(view), target);
    private static ContractRow ResolveAvailableContract(ContractRow[] rows, string target) => TryResolveAvailableContract(rows, target) ?? throw MissingTarget(target, ContractRefPrefix, "available contract", "contracts-list");
    private static ContractRow? TryResolveAvailableContract(ContractRow[] rows, string target)
    {
        if (target.StartsWith(ContractRefPrefix, StringComparison.Ordinal)) { ValidateReference(target, ContractRefPrefix, "contractRef"); return UniqueRef(rows.Where(row => row.Ref == target).ToArray(), target, "contract"); }
        var matches = rows.Where(row => row.Name == target && !target.Contains('…')).ToArray();
        if (matches.Length > 1) throw new AgentError("ambiguous_target", $"Several available contracts are named '{target}'. Use one exact contractRef: {string.Join(", ", matches.Select(row => row.Ref))}.");
        return matches.SingleOrDefault();
    }

    private static CpuRow ResolveOfferCpu(JObject view, string target) => ResolveCpu(OfferCpuRows(view), target, "offered CPU", "contracts-read");
    private static CpuRow ResolveManagerCpu(JObject view, string target) => ResolveManagerCpu(ManagerCpuRows(view), target);
    private static CpuRow ResolveManagerCpu(CpuRow[] rows, string target) => TryResolveManagerCpu(rows, target) ?? throw MissingTarget(target, CpuRefPrefix, "active-contract CPU", "contracts-active");
    private static CpuRow? TryResolveManagerCpu(CpuRow[] rows, string target) => TryResolveCpu(rows, target, "active-contract CPU");
    private static CpuRow ResolveCpu(CpuRow[] rows, string target, string description, string refreshCommand) => TryResolveCpu(rows, target, description) ?? throw MissingTarget(target, CpuRefPrefix, description, refreshCommand);
    private static CpuRow? TryResolveCpu(CpuRow[] rows, string target, string description)
    {
        if (target.StartsWith("product-ui:", StringComparison.Ordinal)) throw new AgentError("invalid_target", "Production productRef values are not Contracts CPU identities. Use a cpuRef returned by contracts-read or contracts-active.");
        if (target.StartsWith(CpuRefPrefix, StringComparison.Ordinal)) { ValidateReference(target, CpuRefPrefix, "cpuRef"); return UniqueRef(rows.Where(row => row.Ref == target).ToArray(), target, "CPU"); }
        var matches = rows.Where(row => row.Name == target && !target.Contains('…')).ToArray();
        if (matches.Length > 1) throw new AgentError("ambiguous_target", $"Several {description} rows are named '{target}'. Use one exact cpuRef: {string.Join(", ", matches.Select(row => row.Ref))}.");
        return matches.SingleOrDefault();
    }

    private static ActiveContractRow ResolveActiveContract(ActiveContractRow[] rows, string target)
    {
        if (target.StartsWith(ContractRefPrefix, StringComparison.Ordinal))
        {
            ValidateReference(target, ContractRefPrefix, "contractRef");
            var referenced = rows.Where(row => row.Ref == target).ToArray();
            if (referenced.Length == 0) throw new AgentError("stale_reference", $"Contract reference '{target}' is not rendered for the selected CPU. Re-run contracts-manage; no name fallback was attempted.");
            if (referenced.Length != 1) throw new AgentError("game_ui_mismatch", $"Contract reference '{target}' matched several active rows.");
            return referenced[0];
        }
        var matches = rows.Where(row => row.Name == target).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No active contract has exact identity '{target}' for the selected CPU. Use contracts-manage.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several active contracts share '{target}'. Use one exact contractRef: {string.Join(", ", matches.Select(row => row.Ref))}.");
        return matches[0];
    }

    private Request VerifiedAvailableClick(ContractRow row) => VerifiedIdentityClick(row.Node, () =>
    {
        ValidateRenderedBinding(row.Component, row.Scene, "ProcessorTycoon.ContractSystem.UI.ContractWindow", "Contract", row.Contract, row.Ref);
    });

    private Request VerifiedOfferCpuClick(CpuRow row) => VerifiedIdentityClick(row.Node, () =>
    {
        ValidateRenderedBinding(row.Component, row.Scene, "ProcessorTycoon.ContractSystem.UI.ContractWindow", "Cpu", row.Cpu, row.Ref);
    });

    private Request VerifiedManagerCpuClick(CpuRow row) => VerifiedIdentityClick(row.Node, () =>
    {
        ValidateRenderedBinding(row.Component, row.Scene, "ProcessorTycoon.ContractSystem.UI.ContractManagementWindow", "Cpu", row.Cpu, row.Ref);
    });

    private Request VerifiedActiveClick(JObject button, ActiveContractRow row) => VerifiedIdentityClick(button, () =>
    {
        ValidateRenderedBinding(row.Component, row.Scene, "ProcessorTycoon.ContractSystem.UI.ContractManagementWindow", "contract", row.Contract, row.Ref, field: true);
        if (!ReferenceEquals(BoundField(row.Component, "cpu"), row.Cpu)) throw new AgentError("context_changed", $"Contract {row.Ref} is no longer bound to CPU {row.CpuRef}; the native click was not invoked.");
    });

    private static bool ContractWindowCurrentCpuIs(CpuRow row)
    {
        if (row.Component == null) return false;
        var window = AncestorComponent(row.Component.transform, "ProcessorTycoon.ContractSystem.UI.ContractWindow");
        return window != null && window.gameObject.scene.IsValid() && window.gameObject.scene.handle == row.Scene && ReferenceEquals(BoundFieldValue(window, "currentCpu"), row.Cpu);
    }

    private static void ValidateRenderedBinding(MonoBehaviour component, int scene, string windowType, string member, object expected, string reference, bool field = false)
    {
        if (component == null || !component.gameObject.scene.IsValid() || component.gameObject.scene.handle != scene || !component.gameObject.activeInHierarchy || !UnderWindow(component.transform, windowType)) throw new AgentError("context_changed", $"Rendered binding for {reference} is no longer active in its original scene; the native click was not invoked.");
        var actual = field ? BoundField(component, member) : BoundProperty(component, member);
        if (!ReferenceEquals(actual, expected)) throw new AgentError("context_changed", $"Rendered binding for {reference} changed before dispatch; the native click was not invoked.");
    }

    private static Request VerifiedIdentityClick(JObject node, Action validate)
    {
        var step = GameUi.Click(node);
        var dispatch = step.NativeAction ?? throw new AgentError("game_ui_mismatch", "Identity-bound catalog action did not preserve its native catalog callback.");
        step.NativeAction = () => { validate(); dispatch(); };
        return step;
    }

    private static T? UniqueRef<T>(T[] rows, string reference, string description) where T : class
    {
        if (rows.Length > 1) throw new AgentError("game_ui_mismatch", $"{description} reference '{reference}' matched several rendered rows.");
        return rows.SingleOrDefault();
    }

    private static AgentError MissingTarget(string target, string prefix, string description, string refreshCommand) => target.StartsWith(prefix, StringComparison.Ordinal)
        ? new AgentError("stale_reference", $"{description} reference '{target}' is not present in the current rendered UI. Re-run {refreshCommand}; no name fallback was attempted.")
        : new AgentError("not_found", $"No {description} has exact name '{target}'. Re-run {refreshCommand}.");

    private static string IdentityRef(object value, string prefix, string? label, int scene)
    {
        EnsureIdentityScene(scene);
        var tokens = prefix == ContractRefPrefix ? contractTokens : cpuTokens;
        var token = tokens.GetValue(value, _ => new IdentityToken((++nextIdentityToken).ToString("x", CultureInfo.InvariantCulture)));
        var reference = $"{prefix}{IdentitySession}:{scene}:{token.Value}";
        if (label != null && prefix == ContractRefPrefix) contractLabels[reference] = label;
        return reference;
    }

    private static void EnsureIdentityScene(int scene)
    {
        if (identityScene == scene) return;
        identityScene = scene; contractTokens = new ConditionalWeakTable<object, IdentityToken>(); cpuTokens = new ConditionalWeakTable<object, IdentityToken>(); contractLabels.Clear(); nextIdentityToken = 0;
    }

    private static void ValidateReference(string reference, string prefix, string name)
    {
        if (!Regex.IsMatch(reference, $@"^{Regex.Escape(prefix)}[0-9a-f]{{8}}:-?\d+:[0-9a-f]+$", RegexOptions.CultureInvariant)) throw new AgentError("invalid_target", $"Malformed {name} '{reference}'. Copy an exact current reference from the Contracts API.");
    }

    private static MonoBehaviour[] UiComponents(string componentType, string windowType)
    {
        var components = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.gameObject.scene.IsValid()).ToArray();
        var scenes = components.Where(component => component.GetType().FullName == windowType).Select(component => component.gameObject.scene.handle).Distinct().ToArray();
        if (scenes.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one rendered {windowType.Split('.').Last()} scene binding.");
        EnsureIdentityScene(scenes[0]);
        return components.Where(component => component.GetType().FullName == componentType && UnderWindow(component.transform, windowType)).ToArray();
    }
    private static string ComponentGroup(MonoBehaviour component) => "g" + component.transform.GetInstanceID();
    private static bool UnderWindow(Transform transform, string windowType)
    {
        for (var current = transform.parent; current != null; current = current.parent) if (current.GetComponents<MonoBehaviour>().Any(component => component != null && component.GetType().FullName == windowType)) return true;
        return false;
    }
    private static MonoBehaviour? AncestorComponent(Transform transform, string componentType)
    {
        for (var current = transform.parent; current != null; current = current.parent)
        {
            var matches = current.GetComponents<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == componentType).ToArray();
            if (matches.Length > 1) throw new AgentError("game_ui_mismatch", $"Rendered row has several {componentType.Split('.').Last()} ancestors.");
            if (matches.Length == 1) return matches[0];
        }
        return null;
    }
    private static object BoundProperty(MonoBehaviour component, string name) => component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(component) ?? throw new AgentError("game_ui_mismatch", $"Rendered {component.GetType().Name} has no '{name}' UI binding.");
    private static object BoundField(MonoBehaviour component, string name) => component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(component) ?? throw new AgentError("game_ui_mismatch", $"Rendered {component.GetType().Name} has no '{name}' UI binding.");
    private static object? BoundFieldValue(MonoBehaviour component, string name)
    {
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new AgentError("game_ui_mismatch", $"Rendered {component.GetType().Name} has no '{name}' UI binding.");
        return field.GetValue(component);
    }
    private static string Selected(JObject select) => select["options"]![select["value"]!.Value<int>()]!.Value<string>()!;
    private static void EnsureOption(JObject select, string wanted, string parameter)
    {
        if (!select["options"]!.Values<string>().Contains(wanted)) throw new AgentError("invalid_value", $"Unknown {parameter} '{wanted}'. Available native choices: {string.Join(", ", select["options"]!.Values<string>())}.");
    }
    private static JObject Fields(JObject view, string path)
    {
        var result = new JObject();
        foreach (var text in GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").Contains(path) && !((string?)t["context"] ?? "").EndsWith("/Title", StringComparison.Ordinal)))
        {
            var parts = ((string?)text["text"] ?? "").Split('|').Select(s => s.Trim()).ToArray();
            if (parts.Length < 2) continue;
            var key = parts[0].TrimEnd(':').Replace(" ", ""); key = char.ToLowerInvariant(key[0]) + key.Substring(1);
            result[key] = string.Join(" | ", parts.Skip(1));
        }
        return result;
    }
    private static bool RequirementsMatch(JObject cpuDetails)
    {
        var visible = string.Join(" | ", cpuDetails.Properties().Select(p => p.Value.Value<string>() ?? ""));
        return visible.IndexOf("(Too Low)", StringComparison.Ordinal) < 0 && visible.IndexOf("(Too High)", StringComparison.Ordinal) < 0 && visible.IndexOf("(Wrong Type)", StringComparison.Ordinal) < 0;
    }
    private static bool NumericEquals(JToken? actual, JToken expected) => actual != null && (actual.Type is JTokenType.Integer or JTokenType.Float) && (expected.Type is JTokenType.Integer or JTokenType.Float) && Math.Abs(actual.Value<double>() - expected.Value<double>()) < 1e-9;
    // Multiplayer mod: with another player as counterparty, signing sends them a proposal to accept or decline.
    private static bool AcceptanceWillSign(string display) => display.StartsWith("They will accept", StringComparison.Ordinal) || display.Contains(" is a player: ");
    private static JToken AcceptanceScore(string display)
    {
        var match = Regex.Match(display, @"\(([+-]?\d+)\)");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) ? new JValue(score) : JValue.CreateNull();
    }
    private static string TextAt(JObject view, string contextSuffix) => (string?)GameUi.Texts(view).SingleOrDefault(t => ((string?)t["context"] ?? "").EndsWith(contextSuffix, StringComparison.Ordinal))?["text"] ?? "";
    private static string KnownBreakConfirmation(JObject dialog)
    {
        var body = TextAt(dialog, "/Background");
        if (!body.StartsWith("Are you sure you want to break this contract?", StringComparison.Ordinal) || GameUi.Controls(dialog).Count(c => (string?)c["name"] == "Break Contract") != 1 || GameUi.Controls(dialog).Count(c => (string?)c["name"] == "Cancel") != 1) throw new AgentError("confirmation_mismatch", "An unfamiliar confirmation opened. It was left open and no confirmation or cancellation was dispatched.");
        return body;
    }
    private static string? BreakFine(string body)
    {
        var match = Regex.Match(body, @"Fines:\s*([^|]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    private static string? ValueFrom(IEnumerable<JObject> texts, string suffix)
    {
        var text = (string?)texts.SingleOrDefault(t => ((string?)t["context"] ?? "").EndsWith(suffix, StringComparison.Ordinal))?["text"];
        if (text == null) return null; var parts = text.Split('|').Select(s => s.Trim()).ToArray(); return parts.FirstOrDefault(s => !s.EndsWith(":"));
    }
    private static int? ParseLeadingInt(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static JObject Slider(JObject view, string suffix)
    {
        var node = SliderNode(view, suffix);
        return new JObject { ["percent"] = node["value"]!.DeepClone(), ["display"] = node["displayValue"]!.DeepClone() };
    }
    private static JObject SliderNode(JObject view, string suffix) => GameUi.One(GameUi.Controls(view).Where(c => ((string?)c["context"] ?? "").EndsWith("/" + suffix, StringComparison.Ordinal)), suffix);
    private static JArray ActiveContracts(JObject view)
    {
        var result = new JArray();
        foreach (var row in ActiveContractRows(view)) result.Add(ActiveContractEntry(view, row));
        return result;
    }

    private static JObject ActiveContractEntry(JObject view, ActiveContractRow row)
    {
        var texts = GameUi.Texts(view).Where(t => (string?)t["group"] == row.Group).ToArray();
        var title = ((string?)row.Node["text"] ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        return new JObject { ["contractRef"] = row.Ref, ["cpuRef"] = row.CpuRef, ["identity"] = row.Name, ["lifecycleStage"] = "active", ["company"] = title.ElementAtOrDefault(0), ["product"] = title.ElementAtOrDefault(1), ["details"] = LabeledFields(texts.Where(t => t != row.Node)), ["canBreak"] = GameUi.Controls(view).Any(c => (string?)c["group"] == row.Group && (string?)c["name"] == "Break Contract" && c["blockedReason"] == null) };
    }
    private static JObject LabeledFields(IEnumerable<JObject> texts)
    {
        var result = new JObject();
        foreach (var text in texts)
        {
            var parts = ((string?)text["text"] ?? "").Split('|').Select(s => s.Trim()).ToArray();
            if (parts.Length < 2) continue;
            var key = parts[0].TrimEnd(':').Replace(" ", ""); key = char.ToLowerInvariant(key[0]) + key.Substring(1);
            result[key] = string.Join(" | ", parts.Skip(1));
        }
        return result;
    }
    private static JArray InspectorContracts(JObject view)
    {
        var result = new JArray();
        foreach (var title in BusinessContractTitleRows(view))
        {
            var group = (string?)title["group"];
            var fields = BusinessContractFields(view, group);
            var breaker = GameUi.Controls(view).SingleOrDefault(c => (string?)c["group"] == group && (string?)c["name"] == "Break Contract");
            result.Add(new JObject { ["identity"] = BusinessContractIdentity((string?)title["text"] ?? "", fields), ["counterpartyAndRole"] = title["text"]?.DeepClone(), ["terms"] = fields, ["canBreak"] = breaker != null && breaker["blockedReason"] == null, ["breakBlockedReason"] = breaker?["blockedReason"]?.DeepClone() });
        }
        return result;
    }
    private static JObject[] BusinessContractTitleRows(JObject view) => GameUi.Texts(view).Where(t => ((string?)t["context"] ?? "").EndsWith("BusinessContractUI(Clone)", StringComparison.Ordinal) && t["group"] != null).ToArray();
    private static JObject BusinessContractFields(JObject view, string? group)
    {
        var fields = new JObject();
        foreach (var text in GameUi.Texts(view).Where(t => (string?)t["group"] == group && !((string?)t["context"] ?? "").EndsWith("BusinessContractUI(Clone)", StringComparison.Ordinal)))
        {
            var parts = ((string?)text["text"] ?? "").Split('|').Select(s => s.Trim()).ToArray();
            if (parts.Length > 1) fields[parts[0].TrimEnd(':').Replace(" ", "")] = parts[1];
        }
        return fields;
    }
    private static string BusinessContractIdentity(string title, JObject fields)
    {
        var parts = title.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).Take(2);
        var terms = new[] { "Type", "TerminationDate", "ProductionCapacity", "Markup", "Royalties", "Exclusive" }.Select(key => (string?)fields[key]).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!);
        return string.Join(" / ", parts.Concat(terms));
    }
    private static string ContractSignature(JObject contract) => contract.ToString(Newtonsoft.Json.Formatting.None);
    private static string PartyName(string display) => display.Split('|').Select(s => s.Trim()).LastOrDefault() ?? "";
    private static bool BusinessContractMatches(JObject actual, JObject expected)
    {
        var title = ((string?)actual["counterpartyAndRole"] ?? "").Split('|').Select(s => s.Trim()).ToArray();
        var terms = actual["terms"] as JObject;
        if (title.ElementAtOrDefault(0) != (string?)expected["counterparty"] || title.ElementAtOrDefault(1) != (string?)expected["counterpartyRole"] || terms == null) return false;
        if ((string?)terms["Type"] != (string?)expected["type"] || (string?)terms["ProductionCapacity"] != (string?)expected["productionCapacity"] || (string?)terms["Markup"] != (string?)expected["markup"] || (string?)terms["Exclusive"] != (string?)expected["exclusive"]) return false;
        if (expected["royalties"]?.Type == JTokenType.String && (string?)terms["Royalties"] != (string?)expected["royalties"]) return false;
        return expected["terminationDate"]?.Type == JTokenType.String && (string?)terms["TerminationDate"] == (string?)expected["terminationDate"];
    }
    private static void ValidateTerms(Request request)
    {
        GameUi.Parameters(request, "playerRole", "durationYears", "productionPercent", "markupPercent", "exclusivity", "renew");
        OptionalRequiredString(request, "playerRole"); var role = request.Parameters?["playerRole"]?.Value<string>(); if (role != null && role is not ("provider" or "client")) throw new AgentError("invalid_value", "playerRole must be provider or client.");
        IntegerChoice(request, "durationYears", 1, 3, 5, 10); IntegerRange(request, "productionPercent", 1, 25); IntegerRange(request, "markupPercent", 25, 125);
        foreach (var key in new[] { "exclusivity", "renew" }) if (request.Parameters?[key] is JToken value && value.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"{key} must be boolean.");
    }
    private static void NoTarget(Request request) { if (!string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", $"{request.Command} does not take a target."); }
    private static void OptionalString(Request request, string key) { if (request.Parameters?[key] is JToken value && value.Type != JTokenType.String) throw new AgentError("invalid_value", $"{key} must be a string."); }
    private static void OptionalBoolean(Request request, string key) { if (request.Parameters?[key] is JToken value && value.Type != JTokenType.Boolean) throw new AgentError("invalid_value", $"{key} must be boolean."); }
    private static void OptionalChoice(Request request, string key, params string[] choices) { OptionalString(request, key); var value = request.Parameters?[key]?.Value<string>(); if (value != null && !choices.Contains(value)) throw new AgentError("invalid_value", $"{key} must be one of: {string.Join(", ", choices)}."); }
    private static void OptionalRequiredString(Request request, string key) { OptionalString(request, key); if (request.Parameters?[key]?.Type == JTokenType.String && string.IsNullOrWhiteSpace(request.Parameters[key]!.Value<string>())) throw new AgentError("invalid_value", $"{key} cannot be empty."); }
    private static void RequiredString(Request request, string key) { OptionalRequiredString(request, key); if (request.Parameters?[key] == null) throw new AgentError("invalid_request", $"{request.Command} requires --{key} EXACT_NAME."); }
    private static void IntegerChoice(Request request, string key, params int[] choices) { if (request.Parameters?[key] is JToken value && (value.Type != JTokenType.Integer || !choices.Contains(value.Value<int>()))) throw new AgentError("invalid_value", $"{key} must be one of: {string.Join(", ", choices)}."); }
    private static void IntegerRange(Request request, string key, int min, int max) { if (request.Parameters?[key] is JToken value && (value.Type != JTokenType.Integer || value.Value<int>() < min || value.Value<int>() > max)) throw new AgentError("invalid_value", $"{key} must be an integer from {min} to {max}."); }
}
