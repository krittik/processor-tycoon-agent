using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProcessorTycoon;
using ProcessorTycoon.Bank;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.CompanySystem.PlayerUI;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.MarketSystem;
using ProcessorTycoon.Production.Math;
using ProcessorTycoon.Production.UI;
using ProcessorTycoon.ResearchSystem;
using ProcessorTycoon.ResearchSystem.Math;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMod.Fast;

// 0.2.16a5 model adapter. Only this directory references the game's simulation types.
// Called on Unity's main thread: a snapshot cannot straddle a simulation tick.
internal static class FastGameApi
{
    private static readonly string ReferenceSession = Guid.NewGuid().ToString("N").Substring(0, 8);
    internal static readonly string[] Commands = new[] {
        "game.desktop-read", "game.finance-read", "game.situation", "game.products-pulse", "game.production-read", "game.product-list", "game.product-read", "game.product-pulse",
        "game.product-set", "game.product-price", "game.product-preview", "game.product-production", "game.production-settings", "game.production-automation",
        "game.research-list", "game.research-read", "game.research-start", "game.research-set", "game.research-cancel", "game.market-catalog", "game.market-share", "game.time-read"
    }.Concat(FastCpuApi.Commands).ToArray();
    internal static bool IsMutation(string command) => command is "game.projects-release" or "game.cpu-develop" or "game.product-set" or "game.product-price" or "game.product-production" or "game.production-settings" or "game.production-automation" or "game.research-start" or "game.research-set" or "game.research-cancel";
    internal static bool IsMutation(Request request) => IsMutation(request.Command) && !(request.Parameters?["dryRun"]?.Type == JTokenType.Boolean && request.Parameters["dryRun"]!.Value<bool>());
    private static ICompany Company => Player.Instance.Company;
    internal static string Date => DateController.Instance.CurrentDate.ToString("yyyy-MM-dd");

    internal static JObject Execute(Request request)
    {
        if (!Commands.Contains(request.Command)) throw new AgentError("headless_fast_not_supported", $"{request.Command} has no direct backend. No UI fallback or action occurred. Read capabilities for supported --headless-fast commands.");
        if (Player.Instance == null || DateController.Instance == null || GameManager.Instance == null || GameManager.Instance.IsLoading || Company?.ResearchSector == null) throw new AgentError("no_campaign_loaded", "A ready campaign is required for --headless-fast.");
        if (request.Hidden || request.ObserveAfter != null) throw new AgentError("invalid_request", "--headless-fast cannot be combined with hidden or observe-after UI behavior.");
        if (IsMutation(request.Command) && (Company.IsBankrupt || MultiplayerInterop.LocalBankrupt)) throw new AgentError("company_bankrupt", "Bankrupt companies cannot start new actions.");
        var clock = Stopwatch.StartNew();
        var result = request.Command switch
        {
            "game.desktop-read" or "game.finance-read" => Desktop(request),
            "game.situation" => Situation(request),
            "game.production-read" or "game.product-list" or "game.products-pulse" => Production(request),
            "game.product-read" or "game.product-pulse" or "game.product-set" or "game.product-price" or "game.product-preview" or "game.product-production" => Product(request),
            "game.production-settings" or "game.production-automation" => ProductionSettings(request),
            "game.research-list" or "game.research-read" or "game.research-start" or "game.research-set" or "game.research-cancel" => Research(request),
            "game.market-catalog" => Catalog(request),
            "game.market-share" => MarketShare(request),
            "game.time-read" => TimeRead(request),
            _ => FastCpuApi.Execute(request)
        };
        result["executionMode"] = "headless-fast";
        result["source"] = "simulation-model";
        result["date"] = Date;
        result["snapshotFrame"] = Time.frameCount;
        result["elapsedMs"] = clock.Elapsed.TotalMilliseconds;
        result["visualFeedback"] = "No native window was opened or clicked; inspect the JSON result. Existing UI may refresh later.";
        return result;
    }

    internal static void Parameters(Request r, params string[] keys)
    {
        GameUi.Parameters(r, keys);
        foreach (var p in r.Parameters?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            var valid = p.Name switch
            {
                "price" or "plannedPrice" or "fundingPercent" => p.Value.Type == JTokenType.Integer,
                "retire" or "sellOnMarket" or "availableForContracts" or "dryRun" or "automation" or "enabled" or "upgradeWafer" or "foundryServices" or "innovationEffort" or "showRetired" => p.Value.Type == JTokenType.Boolean,
                "lines" => p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.String && (string?)p.Value == "max",
                _ => p.Value.Type == JTokenType.String
            };
            if (!valid) throw new AgentError("invalid_value", $"Invalid type for {p.Name}; no action occurred.");
        }
    }
    internal static JObject Finances()
    {
        var cash = Company.MoneyAmount;
        var limit = CentralBank.Instance.DebtLimit;
        var balance = MoneyBalance.Instance.GetBalance(); // Native saved daily balance DTO, not TMP or window visibility.
        return new JObject {
            ["cash"] = cash, ["availableCredit"] = limit + Math.Min(0, cash), ["creditLimit"] = limit, ["debt"] = Math.Max(0, -cash),
            ["headroomBeforeDefault"] = limit + cash, ["monthlyBalance"] = balance.TotalBalance * 30,
            ["monthlySales"] = balance.Sales * 30, ["monthlyContracts"] = balance.Contracts * 30, ["monthlyInterest"] = balance.Interest * 30,
            ["monthlyProduction"] = balance.Production * 30, ["monthlyResearch"] = balance.Research * 30, ["monthlyDevelopment"] = balance.Development * 30,
            ["monthlyConstruction"] = balance.Construction * 30, ["monthlyOutsourcing"] = balance.Outsourcing * 30, ["monthlyRoyalties"] = balance.Royalties * 30,
            ["default"] = new JObject { ["active"] = Company.IsInDefault, ["daysElapsed"] = Company.DaysInDefault, ["daysLeft"] = Company.IsInDefault ? Math.Max(0, BankruptcyHandler.Instance.defaultTimeLimit - Company.DaysInDefault) : (int?)null },
            ["bankrupt"] = Company.IsBankrupt || MultiplayerInterop.LocalBankrupt, ["precision"] = "native-number", ["period"] = "last-native-daily-balance-times-30"
        };
    }

    private static JObject Desktop(Request r)
    {
        Parameters(r);
        return new JObject { ["company"] = Company.Name, ["companyId"] = Company.SaveID, ["difficultyLevel"] = Player.Instance.DifficultyLevel, ["finances"] = Finances() };
    }

    private static Cpu[] OwnProducts() => Company.Owner.GetCpus().Where(cpu => cpu.IsReleased && !cpu.IsRetired).ToArray();
    private static Cpu FindProduct(string target)
    {
        var matches = OwnProducts().Where(cpu => cpu.Name == target || Reference(cpu) == target).ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No released own product '{target}'. Use product-list.");
        if (matches.Length > 1) throw new AgentError("ambiguous_target", $"Several own CPUs are named '{target}'; use productRef from product-list.");
        return matches[0];
    }
    internal static string Reference(Cpu cpu) => "cpu:" + ReferenceSession + ":" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle + ":" + cpu.SaveID;
    private static int FreeLines() => Math.Max(0, Company.Factory.ProductionCapacity - OwnProducts().Where(cpu => !cpu.IsOutsourced).Sum(cpu => cpu.CurrentProductionLines + cpu.CurrentContractedProductionLines) - Company.OutsourcedCpus.Sum(cpu => cpu.CurrentProductionLines + cpu.CurrentContractedProductionLines));

    private static JObject ProductData(Cpu cpu)
    {
        var requested = cpu.CurrentProductionLines + cpu.CurrentContractedProductionLines;
        var effective = Player.Instance.AutomateProduction || cpu.IsOutsourced ? Math.Min(requested, cpu.LinesActuallyAllocated) : requested;
        var output = effective * 50 * ProductionMath.DiesPerWafer(cpu.DieSize, cpu.Manufacturer.CurrentWaferSize, cpu.CurrentNodeYield) * (1 - cpu.FailureRate);
        var demand = (cpu.SoldLastDay + cpu.MissedSalesLastDay + cpu.TodayDemandByContracts + cpu.MissedSalesLastDayByContracts) * 30;
        return new JObject {
            ["name"] = cpu.Name, ["productRef"] = Reference(cpu), ["company"] = cpu.Company.Name, ["price"] = cpu.Price, ["mips"] = cpu.Ips / 1000, ["frequencyMHz"] = cpu.Frequency / 1000,
            ["dieSizeMm2"] = cpu.DieSize, ["powerWatts"] = cpu.PowerConsumption, ["unitCost"] = cpu.RealUnitCost, ["popularity"] = cpu.HighestPopularity * 100,
            ["sellOnMarket"] = cpu.SellOnMarket, ["availableForContracts"] = cpu.SellOnContracts,
            ["retired"] = cpu.IsRetired,
            ["manualLines"] = cpu.CurrentProductionLines, ["contractLines"] = cpu.CurrentContractedProductionLines, ["requestedTotalLines"] = requested,
            ["productionLines"] = effective, ["allocatedLinesLastDay"] = cpu.LinesActuallyAllocated, ["capacityLimited"] = effective < requested,
            ["availableRange"] = new JObject { ["minLines"] = 0, ["maxLines"] = cpu.CurrentProductionLines + FreeLines() },
            ["demandPerMonth"] = demand, ["productionPerMonth"] = output, ["stockUnits"] = cpu.Market.Stock, ["outOfStock"] = cpu.Market.Stock <= 0,
            ["soldLastDay"] = cpu.SoldLastDay, ["producedLastDay"] = cpu.ProducedLastDay, ["demandFreshness"] = "last completed simulation day; a new price takes effect on a future tick"
        };
    }

    private static JObject Production(Request r)
    {
        Parameters(r);
        return new JObject { ["products"] = new JArray(OwnProducts().Select(ProductData)), ["finances"] = Finances(), ["lines"] = new JObject { ["capacity"] = Company.Factory.ProductionCapacity, ["unassigned"] = FreeLines() }, ["settings"] = Settings() };
    }

    private static JObject Product(Request r)
    {
        Parameters(r, r.Command switch { "game.product-price" => new[] { "price", "dryRun" }, "game.product-production" => new[] { "lines", "dryRun" }, "game.product-set" or "game.product-preview" => new[] { "price", "name", "retire", "sellOnMarket", "availableForContracts", "dryRun" }, _ => Array.Empty<string>() });
        var cpu = FindProduct(GameUi.RequiredTarget(r));
        var p = r.Parameters ?? new JObject();
        var preview = r.Command == "game.product-preview" || (bool?)p["dryRun"] == true;
        var price = (int?)p["price"];
        if (price is < 1 or > 9999) throw new AgentError("invalid_value", "price must be an integer from 1 to 9999.");
        var name = (string?)p["name"];
        if ((bool?)p["retire"] == true && p.Properties().Any(x => x.Name is not ("retire" or "dryRun"))) throw new AgentError("invalid_request", "Retirement cannot be combined with other product edits.");
        if (name != null && (string.IsNullOrWhiteSpace(name) || name.Length > 20)) throw new AgentError("invalid_value", "name must contain 1..20 characters.");
        if (r.Command == "game.product-production")
        {
            if (cpu.IsOutsourced || Player.Instance.AutomateProduction) throw new AgentError("not_interactable", "Manual lines require an own factory and production automation disabled.");
            var max = cpu.CurrentProductionLines + FreeLines();
            var raw = p["lines"] ?? throw new AgentError("invalid_request", "lines is required.");
            var lines = raw.Type == JTokenType.String && (string?)raw == "max" ? max : raw.Type == JTokenType.Integer ? (int)raw : throw new AgentError("invalid_value", "lines must be an integer or max.");
            if (lines < 0 || lines > max) throw new AgentError("invalid_value", $"lines must be 0..{max}; contract lines are separate.");
            if (!preview)
            {
                cpu.CurrentProductionLines = lines;
                foreach (var ui in Resources.FindObjectsOfTypeAll<ProductionUI>().Where(ui => ui != null && ui.cpu == cpu)) { ui.productionLinesSlider.Setup(true, 0, max); ui.productionLinesSlider.Value = lines; }
            }
        }
        else if (r.Command is "game.product-set" or "game.product-price" or "game.product-preview")
        {
            if (cpu.Company != Company) throw new AgentError("not_interactable", "Only the product's owning company can edit it.");
            if (r.Command == "game.product-price" && price == null) throw new AgentError("invalid_request", "price is required.");
            if (!preview)
            {
                if ((bool?)p["retire"] == true) cpu.TriggerRetirement();
                else
                {
                    if (name != null) cpu.ChangeName(name);
                    if (price != null) cpu.Price = price.Value;
                    if (p["sellOnMarket"] != null) cpu.SellOnMarket = (bool)p["sellOnMarket"]!;
                    if (p["availableForContracts"] != null) cpu.SellOnContracts = (bool)p["availableForContracts"]!;
                }
            }
        }
        var result = new JObject { ["outcome"] = preview ? "preview" : IsMutation(r.Command) ? "product_changes_confirmed" : "read", ["product"] = ProductData(cpu), ["requested"] = p.DeepClone(), ["finances"] = Finances() };
        if (preview && price != null) result["pricePreview"] = new JObject { ["price"] = price, ["unitCostAtPrice"] = cpu.RealUnitCostWithNewPrice(price.Value), ["unitMargin"] = price.Value - cpu.RealUnitCostWithNewPrice(price.Value), ["demandForecastAvailable"] = false };
        return result;
    }

    private static JObject Settings() => new() { ["automation"] = Player.Instance.AutomateProduction, ["upgradeLines"] = Company.Factory.UpdateWafer, ["foundryServices"] = Company.Divisions.FoundryServices };
    private static JObject ProductionSettings(Request r)
    {
        Parameters(r, "automation", "enabled", "upgradeWafer", "foundryServices");
        var p = r.Parameters ?? new JObject();
        if (Company.IsFabless || Company.IsFoundry && (p["automation"] != null || p["enabled"] != null)) throw new AgentError("not_interactable", "These settings are disabled for this company type.");
        if (p["foundryServices"] != null && Player.Instance.DifficultyLevel == 5) throw new AgentError("not_interactable", "Foundry services are disabled by Impossible difficulty.");
        if (p["automation"] != null && p["enabled"] != null) throw new AgentError("invalid_request", "Supply automation or enabled, not both.");
        var automation = (bool?)(p["automation"] ?? p["enabled"]);
        if (automation != null)
        {
            Player.Instance.AutomateProduction = automation.Value;
            ProductionWindow.Instance.productionAutomation.AutomateMarket = automation.Value;
            ProductionWindow.Instance.automateProductionToggle.IsOn = automation.Value;
        }
        if (p["upgradeWafer"] != null) { Company.Factory.UpdateWafer = (bool)p["upgradeWafer"]!; ProductionWindow.Instance.waferUpdateToggle.IsOn = Company.Factory.UpdateWafer; }
        if (p["foundryServices"] != null) { Company.Divisions.FoundryServices = (bool)p["foundryServices"]!; ProductionWindow.Instance.SetFoundryServicesToggle(Company.Divisions.FoundryServices); }
        return new JObject { ["outcome"] = "settings_confirmed", ["settings"] = Settings() };
    }

    private static JObject ResearchData(Technology technology)
    {
        var s = Company.ResearchSector;
        var research = s.TechnologyAsResearch(technology);
        return new JObject { ["name"] = technology.Name, ["technologyId"] = technology.ID, ["category"] = technology.Category.ToString(), ["type"] = technology.Type.ToString(), ["year"] = technology.Year,
            ["status"] = research.IsResearched ? "researched" : research.IsLocked ? "locked" : "available", ["progress"] = research.Progress,
            ["timeLeftDays"] = research.IsResearched ? 0 : s.TimeLeftOf(technology), ["monthlyCost"] = technology.Cost * s.Funding, ["monthlyCostAfterModifiers"] = technology.Cost * s.Funding * Modifiers.GetModifier(ModifierType.ResearchCost, Company),
            ["dependencies"] = new JArray(technology.Dependencies.Select(t => t.Name)) };
    }
    private static JObject ActiveResearch()
    {
        var s = Company.ResearchSector;
        var technology = s.CurrentResearch();
        return new JObject { ["active"] = technology != null, ["research"] = technology == null ? null : ResearchData(technology), ["fundingPercent"] = s.Funding * 100,
            ["innovationEffort"] = s.InnovationEffort, ["maxFundingPercent"] = s.InnovationEffort ? 500 : 100, ["speedPercent"] = ResearchMath.ResearchSpeed(s.Funding, Company) * 100 };
    }
    private static JObject Research(Request r)
    {
        Parameters(r, r.Command switch { "game.research-list" => new[] { "status", "query" }, "game.research-set" => new[] { "fundingPercent", "innovationEffort" }, _ => Array.Empty<string>() });
        var s = Company.ResearchSector;
        var p = r.Parameters ?? new JObject();
        if (r.Command == "game.research-start")
        {
            var matches = ResearchDataProvider.Instance.GetAllTechnologies().Where(t => t.Name == GameUi.RequiredTarget(r)).ToArray();
            if (matches.Length != 1) throw new AgentError(matches.Length == 0 ? "not_found" : "ambiguous_target", "Choose an exact unique technology from research-list.");
            if (!s.TechnologyAsResearch(matches[0]).IsAvailable) throw new AgentError("not_interactable", "Technology is locked or already researched.");
            s.SetCurrentResearch(matches[0]);
            foreach (var ui in Resources.FindObjectsOfTypeAll<CurrentResearchUI>().Where(ui => ui != null && ui.gameObject.scene.IsValid())) ui.SetTechnology(matches[0]);
        }
        if (r.Command == "game.research-cancel") { s.StopResearching(); foreach (var ui in Resources.FindObjectsOfTypeAll<CurrentResearchUI>().Where(ui => ui != null && ui.gameObject.scene.IsValid())) ui.targetTechnology = null; }
        if (r.Command == "game.research-set")
        {
            var innovation = (bool?)p["innovationEffort"] ?? s.InnovationEffort;
            var funding = (int?)p["fundingPercent"] ?? Mathf.RoundToInt(s.Funding * 100);
            if (funding < 1 || funding > (innovation ? 500 : 100)) throw new AgentError("invalid_value", $"funding-percent must be 1..{(innovation ? 500 : 100)}.");
            s.InnovationEffort = innovation;
            s.Funding = funding / 100f;
            foreach (var ui in Resources.FindObjectsOfTypeAll<CurrentResearchUI>().Where(ui => ui != null && ui.gameObject.scene.IsValid()))
            {
                ui.innovationEffortToggle.IsOn = innovation;
                ui.fundingSlider.Value = funding;
            }
        }
        var result = ActiveResearch();
        if (r.Command == "game.research-list")
        {
            var status = (string?)p["status"] ?? "all";
            if (status is not ("all" or "available" or "locked" or "researched")) throw new AgentError("invalid_value", "status must be all, available, locked or researched.");
            var query = (string?)p["query"] ?? "";
            result["technologies"] = new JArray(ResearchDataProvider.Instance.GetAllTechnologies().Where(t => t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Select(ResearchData).Where(t => status == "all" || (string?)t["status"] == status));
            result["complete"] = true;
        }
        return result;
    }

    private static bool InMarket(Cpu cpu, string market) => market is "All" or "All Markets" || Market.Instance.CpuBelongsToMarket(cpu, market switch { "Desktop" => MarketType.Desktop, "Industries" => MarketType.Industrial, "Mobile" => MarketType.Mobile, _ => throw new AgentError("invalid_value", "market must be All Markets, Desktop, Industries or Mobile.") });
    private static JObject Catalog(Request r)
    {
        Parameters(r, "market", "showRetired", "search");
        var p = r.Parameters ?? new JObject();
        var market = (string?)p["market"] ?? "All Markets";
        var query = (string?)p["search"] ?? "";
        var cpus = DataFinder.FindAllCpus().Where(cpu => cpu.IsReleased && ((bool?)p["showRetired"] == true || !cpu.IsRetired) && InMarket(cpu, market) && cpu.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        return new JObject { ["complete"] = true, ["market"] = market, ["cpus"] = new JArray(cpus.Select(cpu => new JObject { ["name"] = cpu.Name, ["company"] = cpu.Company.Name, ["cpuRef"] = Reference(cpu), ["mips"] = cpu.Ips / 1000, ["price"] = cpu.Price, ["powerWatts"] = cpu.PowerConsumption, ["popularity"] = cpu.HighestPopularity * 100, ["retired"] = cpu.IsRetired })) };
    }

    private static JObject MarketShare(Request r)
    {
        Parameters(r, "market");
        var requestedMarket = (string?)r.Parameters?["market"] ?? "All Markets";
        if (requestedMarket is not ("All" or "All Markets" or "Desktop" or "Industries" or "Mobile")) throw new AgentError("invalid_value", "Unknown market.");
        var market = Market.Instance;
        var cpus = DataFinder.FindAllCpus().Where(cpu => cpu.IsReleased && !cpu.IsRetired).ToArray();
        var markets = new JArray();
        foreach (var name in new[] { "Desktop", "Industries", "Mobile" })
        {
            if (requestedMarket is not ("All" or "All Markets") && requestedMarket != name) continue;
            var size = name == "Desktop" ? market.DesktopSize : name == "Industries" ? market.IndustiresSize : market.MobileSize;
            int Sales(Cpu cpu) => (name == "Desktop" ? cpu.SoldLastDayToDesktop : name == "Industries" ? cpu.SoldLastDayToIndustries : cpu.SoldLastDayToMobile) * 30;
            markets.Add(new JObject { ["market"] = name, ["unitsPerMonth"] = size, ["salesBasis"] = "last completed simulation day times30", ["unservedPercent"] = size == 0 ? 0 : Math.Max(0, 100d * (size - cpus.Sum(Sales)) / size),
                ["companies"] = new JArray(cpus.GroupBy(cpu => cpu.Company).Select(g => new JObject { ["company"] = g.Key.Name, ["companyId"] = g.Key.SaveID, ["sharePercent"] = size == 0 ? 0 : 100d * g.Sum(Sales) / size })) });
        }
        return new JObject { ["markets"] = markets, ["segmentRecommendedPrices"] = new JObject { ["DesktopHigh"] = market.DesktopHighBudget, ["DesktopMid"] = market.DesktopMidBudget, ["DesktopLow"] = market.DesktopLowBudget, ["Industries"] = market.IndustriesBudget, ["MobileHigh"] = market.MobileHighBudget, ["MobileMid"] = market.MobileMidBudget, ["MobileLow"] = market.MobileLowBudget } };
    }

    private static JObject Situation(Request r)
    {
        Parameters(r);
        return new JObject { ["company"] = Company.Name, ["finance"] = Finances(), ["research"] = ActiveResearch(), ["production"] = Production(new Request()), ["markets"] = MarketShare(new Request()), ["competitors"] = Catalog(new Request()) };
    }
    private static JObject TimeRead(Request r)
    {
        Parameters(r);
        return new JObject { ["date"] = Date, ["nativeTimeSpeed"] = DateController.Instance.currentTimeSpeed, ["nativePaused"] = DateController.Instance.currentTimeSpeed == 0, ["canControlTime"] = MultiplayerInterop.CanControlTime, ["multiplayerSession"] = MultiplayerInterop.Status() };
    }
}
