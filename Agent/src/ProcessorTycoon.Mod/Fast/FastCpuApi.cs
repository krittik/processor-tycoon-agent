using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.CompanySystem.PlayerUI;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.Hardware.Math;
using ProcessorTycoon.Production.Math;
using ProcessorTycoon.ProjectSystem;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMod.Fast;

// Independent model draft: the visible creator is never used as storage or a calculator.
internal static class FastCpuApi
{
    internal static readonly string[] Commands = { "game.cpu-preview", "game.cpu-read", "game.cpu-options", "game.cpu-select", "game.cpu-variants", "game.cpu-review", "game.cpu-develop", "game.projects-list", "game.projects-release-read", "game.projects-release-preview", "game.projects-release" };
    private static ICompany? owner;
    private static object? scheduler;
    private static Draft? draft;
    private static string? reviewId;
    private static string? reviewedState;
    private static string[] reviewedRisks = Array.Empty<string>();
    private static DateTime reviewTime;
    private static ICompany Company => Player.Instance.Company;
    private sealed class Draft
    {
        internal string Name = "CPU";
        internal IPackage Package = null!;
        internal ProcessNode Node = null!;
        internal Memory Memory = null!;
        internal Architecture Architecture = null!;
        internal float Die, Frequency;
        internal int Cores = 1, L1, L2, L3;
        internal bool Overclock, Smt, AutomateCache;
        internal Draft Clone() => (Draft)MemberwiseClone();
    }
    private static Draft Current()
    {
        if (owner != Company || !ReferenceEquals(scheduler, TimerScheduler.Instance))
        {
            owner = Company;
            scheduler = TimerScheduler.Instance;
            draft = null;
            reviewId = null;
        }
        if (draft == null)
        {
            var package = Company.Packages.Last();
            draft = new Draft { Package = package, Node = Company.ProcessNodes.Last(), Memory = Company.Memories.Last(), Architecture = ArchitectureManager.Instance.GetMostRecentArchitectureOfType(IsaType.CISC), Die = (package.MinSize + package.MaxSize) / 2 };
            draft.Frequency = Frequencies(draft).Last();
        }
        return draft;
    }
    private static float MaximumFrequency => Company.MaxFrequency + Company.ResearchModifiers.FrequencyIncrease;
    // Same discrete scale as SliderUI.Setup(5, max / 20, max), without creating a slider.
    private static float Interval(float n) => n >= 1000000 ? 2000 : n >= 100000 ? 1000 : n >= 10000 ? 100 : n >= 1000 ? 10 : 1;
    private static float[] Frequencies(Draft d)
    {
        var max = MaximumFrequency * (d.Overclock ? 1.5f : 1);
        var values = new List<float>();
        for (var n = Mathf.Floor(max / 20 / (5 * Interval(max))) * (5 * Interval(max)); n <= max; n += 5 * Interval(n)) values.Add(n);
        return values.ToArray();
    }
    private static JObject Settings(Draft d) => new() { ["name"] = d.Name, ["package"] = d.Package.Name, ["processNode"] = d.Node.Name, ["memory"] = d.Memory.Name, ["architecture"] = d.Architecture.Name, ["dieSizeMm2"] = d.Die, ["frequencyMHz"] = d.Frequency / 1000, ["coreCount"] = d.Cores, ["l1CacheKB"] = d.L1 * 4, ["l2CacheKB"] = d.L2 * 16, ["l3CacheKB"] = d.L3 * 64, ["overclock"] = d.Overclock, ["smt"] = d.Smt, ["automateCache"] = d.AutomateCache };
    private static int Cache(JObject p, string key, int scale, int existing)
    {
        if (p[key] == null) return existing;
        var kb = p[key]!.Value<float>();
        if (kb % scale != 0) throw new AgentError("invalid_value", $"{key} must be a multiple of {scale} KB per core (L3 is shared).");
        return (int)(kb / scale);
    }
    private static void Validate(Draft d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 20) throw new AgentError("invalid_value", "name must contain 1..20 characters.");
        if (!Company.Packages.Contains(d.Package) || !Company.ProcessNodes.Contains(d.Node) || !Company.Memories.Contains(d.Memory) || !ArchitectureManager.Instance.GetAvailableArchitectures().Contains(d.Architecture)) throw new AgentError("context_changed", "Selected hardware is unavailable to this company/date. Select current hardware again.");
        if (d.Die < d.Package.MinSize || d.Die > d.Package.MaxSize) throw new AgentError("invalid_value", "Die size is outside the selected package range.");
        if (!Frequencies(d).Any(f => Mathf.Abs(f - d.Frequency) < 0.01f)) throw new AgentError("invalid_value", "Choose a frequencyMHz from ranges.frequencyMHzValues; arbitrary frequencies bypass native slider limits.");
        var cores = new[] { 1 }.Concat(d.Package.SupportsMultipleCores ? Company.Multicores.Skip(1).Select(m => m.CoreCount) : Array.Empty<int>());
        if (!cores.Contains(d.Cores) || d.Smt && (!Company.UnlockedSmt || !d.Package.SupportsMultipleCores)) throw new AgentError("not_interactable", "Core count or SMT is not unlocked for this package.");
        var max = Company.GetMaxCache();
        if (d.L1 < 0 || d.L1 > max.Item1 || d.L2 < 0 || d.L2 > max.Item2 || d.L3 < 0 || d.L3 > max.Item3 || d.L3 % 16 != 0) throw new AgentError("invalid_value", "Cache exceeds researched native limits.");
    }
    private static Cpu Calculate(Draft d)
    {
        Validate(d);
        var previous = CpuMath.CurrentModifiers;
        var previousL1 = CpuMath.maxL1CacheSliderValue;
        try
        {
            CpuMath.CurrentModifiers = Company.ResearchModifiers;
            CpuMath.SetMaxL1CacheSliderSteps(Company.GetMaxCache().Item1);
            var transistors = CpuMath.TransistorCount(Company, d.Node, d.Die);
            var power = CpuMath.Consumption(d.Architecture, d.Package, d.Node, d.Die, d.Frequency, MaximumFrequency, d.Cores, d.Smt);
            var temperature = CpuMath.Temperature(power, d.Package);
            var l1 = d.L1 * d.Cores;
            var l2 = d.L2 * d.Cores;
            var maxCache = CpuMath.MaxCacheSize(d.Package, d.Die);
            var efficiency = HardwareMath.TotalCacheKB(l1, l2, d.L3) > maxCache ? CpuMath.CacheEfficiency(l1, l2, d.L3, maxCache, d.Memory) : 1;
            var ipc = CpuMath.MulticoreIpc(CpuMath.Ipc(d.Architecture, d.Memory, d.Frequency, transistors, l1, l2, d.L3, efficiency, d.Cores, d.Smt), l1, l2, d.L3, d.Cores, d.Smt);
            var failure = CpuMath.FailureRate(d.Package, d.Node, temperature, MaximumFrequency, d.Frequency, d.Cores, d.Smt);
            return new Cpu { Name = d.Name, Company = Company, OutsourcedID = -1, DieSize = d.Die, Transistors = transistors, PowerConsumption = power, ThermalEfficiency = d.Package.ThermalEfficiency, Temperature = temperature, Frequency = d.Frequency, FrequencyTechnology = MaximumFrequency, CoreCount = d.Cores, Smt = d.Smt, L1Cache = l1, L2Cache = l2, L3Cache = d.L3, Ipc = ipc, Ips = CpuMath.Ips(d.Frequency, ipc), UnitCost = CpuMath.MaterialCost(d.Package, d.Memory, d.Node, l1, l2, d.L3, failure), FailureRate = failure, Architecture = d.Architecture, Package = d.Package, ProcessNode = d.Node, Memory = d.Memory, Year = DateController.Instance.YearOnRelease(Days(d)), Market = new CpuBase.MarketData() };
        }
        finally { CpuMath.CurrentModifiers = previous; CpuMath.SetMaxL1CacheSliderSteps((int)previousL1); }
    }
    private static float Cost(Draft d) => CpuMath.ProjectCost(d.Package, d.Node, d.Memory, d.Architecture, Company);
    private static int Days(Draft d) => CpuMath.ProjectTime(d.Package, d.Node, d.Memory, d.Architecture);
    private static JObject Preview(Draft d)
    {
        var cpu = Calculate(d);
        var dies = ProductionMath.DiesPerWafer(d.Die, Company.CurrentWaferSize, Company.CurrentNodeYield(d.Node));
        var max = Company.GetMaxCache();
        return new JObject {
            ["settings"] = Settings(d), ["draftStorage"] = "independent headless-fast draft; visible CPU editor is unchanged",
            ["stats"] = new JObject {
                ["mips"] = cpu.Ips / 1000, ["ipc"] = cpu.Ipc, ["temperatureC"] = cpu.Temperature, ["powerWatts"] = cpu.PowerConsumption, ["transistors"] = cpu.Transistors,
                ["failureRate"] = cpu.FailureRate, ["productionViable"] = cpu.FailureRate < 1 && !Company.IsFabless,
                ["unitCost"] = cpu.FailureRate >= 1 ? null : (JToken)CpuMath.RealUnitCost(Company, d.Die, cpu.UnitCost, dies, cpu.FailureRate, false, 1),
                ["monthlyUnitsPerLine"] = 50 * dies * (1 - cpu.FailureRate), ["projectCost"] = Cost(d), ["projectDays"] = Days(d), ["monthlyProjectCost"] = Cost(d) / Days(d) * 30
            },
            ["ranges"] = new JObject {
                ["dieMinMm2"] = d.Package.MinSize, ["dieMaxMm2"] = d.Package.MaxSize, ["frequencyMHzValues"] = new JArray(Frequencies(d).Select(f => f / 1000)),
                ["coreCountOptions"] = new JArray(new[] { "Single Core" }.Concat(d.Package.SupportsMultipleCores ? Company.Multicores.Skip(1).Select(m => m.Name) : Array.Empty<string>())),
                ["smtUnlocked"] = Company.UnlockedSmt && d.Package.SupportsMultipleCores,
                ["l1MaxKBPerCore"] = max.Item1 * 4, ["l2MaxKBPerCore"] = max.Item2 * 16, ["l3MaxKB"] = max.Item3 * 64,
                ["l1StepKBPerCore"] = 4, ["l2StepKBPerCore"] = 16, ["l3StepKB"] = 1024
            }
        };
    }
    private static JObject Options(Request r)
    {
        GameUi.Parameters(r); // Owned hardware only; licensed foundries need a separate verified adapter.
        IEnumerable<IHardware> options = r.Target switch { "package" => Company.Packages.Cast<IHardware>(), "process-node" => Company.ProcessNodes.Cast<IHardware>(), "memory" => Company.Memories.Cast<IHardware>(), "architecture" => ArchitectureManager.Instance.GetAvailableArchitectures().Cast<IHardware>(), _ => throw new AgentError("invalid_value", "kind must be package, process-node, memory or architecture.") };
        return new JObject { ["kind"] = r.Target, ["complete"] = true, ["scope"] = "owned/unlocked hardware, all obsolete entries included; available public/royalty architectures", ["options"] = new JArray(options.Select(o => new JObject { ["name"] = o.Name })) };
    }
    private static JObject Review(Request r, Draft d)
    {
        FastGameApi.Parameters(r, "targetMarket", "plannedPrice");
        if (r.Target != d.Name) throw new AgentError("context_changed", "Review the exact current fast draft name.");
        var marketName = (string?)r.Parameters?["targetMarket"] ?? throw new AgentError("invalid_request", "target-market is required.");
        var market = marketName switch { "Desktop" => ProcessorTycoon.MarketSystem.MarketType.Desktop, "Industries" => ProcessorTycoon.MarketSystem.MarketType.Industrial, "Mobile" => ProcessorTycoon.MarketSystem.MarketType.Mobile, _ => throw new AgentError("invalid_value", "Unknown target market.") };
        var price = (int?)r.Parameters?["plannedPrice"] ?? throw new AgentError("invalid_request", "planned-price is required.");
        if (price < 1 || price > 9999) throw new AgentError("invalid_value", "planned-price must be 1..9999.");
        var cpu = Calculate(d);
        if (!ProcessorTycoon.MarketSystem.Market.Instance.CpuBelongsToMarket(cpu, market)) throw new AgentError("invalid_value", "Draft architecture does not address the requested market.");
        var competitors = DataFinder.FindAllCpus().Where(c => c.Company != Company && c.IsReleased && !c.IsRetired && ProcessorTycoon.MarketSystem.Market.Instance.CpuBelongsToMarket(c, market)).ToArray();
        var risks = new List<string>();
        var finance = FastGameApi.Finances();
        var baseline = Math.Max(0, -MoneyBalance.Instance.GetBalance().TotalBalance * Days(d)) + Cost(d);
        if (baseline > (double)finance["headroomBeforeDefault"]!) risks.Add("baseline_credit_shortfall");
        if (competitors.Length > 0 && cpu.Ips < competitors.Max(c => c.Ips) * 0.5f) risks.Add("far_below_market_performance");
        if (Company.IsInDefault) risks.Add("company_in_default");
        var result = Preview(d);
        result["finance"] = finance;
        result["targetMarket"] = marketName;
        result["plannedPrice"] = price;
        result["bestRivalMips"] = competitors.Length == 0 ? null : (JToken)(competitors.Max(c => c.Ips) / 1000);
        result["baselineCashNeeded"] = baseline;
        result["forecastLimitations"] = "Baseline repeats today's balance plus full draft cost; excludes credit growth, changing interest, new sales, rival future CPUs, contracts and market demand prediction. This is evidence, not an affordability decision.";
        result["riskSignals"] = new JArray(risks);
        reviewId = Guid.NewGuid().ToString("N");
        reviewedState = State(d);
        reviewedRisks = risks.ToArray();
        reviewTime = DateTime.UtcNow;
        result["reviewId"] = reviewId;
        return result;
    }
    private static string State(Draft d) => Settings(d).ToString(Newtonsoft.Json.Formatting.None) + "|" + FastGameApi.Date + "|" + Company.MoneyAmount + "|" + Cost(d) + "|" + Days(d);
    private static JObject Develop(Request r, Draft d)
    {
        CpuGameApi.Validate(r);
        if (r.Target != d.Name || reviewId == null || (string?)r.Parameters?["reviewId"] != reviewId || State(d) != reviewedState || DateTime.UtcNow - reviewTime > TimeSpan.FromMinutes(3)) throw new AgentError("review_required", "Run a fresh --headless-fast cpu-review for this draft before developing.");
        if (string.IsNullOrWhiteSpace((string?)r.Parameters?["decisionReason"])) throw new AgentError("invalid_request", "decision-reason is required.");
        var acknowledged = ((string?)r.Parameters?["acknowledgeRisks"] ?? "").Split(',').Select(x => x.Trim()).ToArray();
        if (reviewedRisks.Except(acknowledged).Any()) throw new AgentError("risk_acknowledgement_required", "Explicitly acknowledge risk kinds from the review: " + string.Join(",", reviewedRisks));
        var cpu = Calculate(d);
        if (Company.IsFabless || cpu.FailureRate >= 1) throw new AgentError("not_interactable", "Draft has no viable own production. Licensed manufacturing is not supported by this fast adapter.");
        var dto = DataConverter.CpuToSaveProject(cpu, Days(d), Cost(d));
        var project = new Project();
        project.SetProjectType(dto);
        project.Initialize(d.Name, Days(d), Cost(d), () => { cpu.SaveID = SaveIDHandler.Instance.NewID(); cpu.OutsourcedID = dto.OutsourcedID; cpu.DevelopmentCost = dto.DevelopmentCost; TimerScheduler.Instance.Unschedule(project); ProjectCompletedHandler.Instance.AddCpu(cpu); }, Company);
        TimerScheduler.Instance.Schedule(project);
        reviewId = null;
        return new JObject { ["outcome"] = "development_started", ["project"] = ProjectData(project), ["decisionReason"] = r.Parameters!["decisionReason"] };
    }
    private static JObject ProjectData(Project p) => new() { ["name"] = p.Name, ["progress"] = p.Progress, ["remainingDays"] = p.EstimatedTime, ["remainingCost"] = p.RemainingCost, ["monthlyCost"] = p.MonthlyCost, ["paused"] = p.IsPaused };
    private static JObject Variants(Request r, Draft d)
    {
        FastGameApi.Parameters(r, "dieSizes", "frequencies", "coreCounts", "plannedPrice");
        var p = r.Parameters ?? new JObject();
        string[] Parts(string key, string fallback) => ((string?)p[key] ?? fallback).Split(',').Select(s => s.Trim()).ToArray();
        float Number(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !float.IsNaN(n) && !float.IsInfinity(n) && n >= 0 ? n : throw new AgentError("invalid_value", "Sweep numbers must be finite and nonnegative.");
        float Frequency(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"^([\d.]+)\s*(GHz|MHz|KHz)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) throw new AgentError("invalid_value", "Frequencies must be MHz or have explicit GHz/MHz/KHz suffixes.");
            return Number(match.Groups[1].Value) * (match.Groups[2].Value.ToLowerInvariant() switch { "ghz" => 1000000, "khz" => 1, _ => 1000 });
        }
        var dies = Parts("dieSizes", d.Die.ToString(CultureInfo.InvariantCulture)).Select(Number).ToArray();
        var frequencies = Parts("frequencies", (d.Frequency / 1000).ToString(CultureInfo.InvariantCulture)).Select(Frequency).ToArray();
        var cores = Parts("coreCounts", d.Cores == 1 ? "Single Core" : Company.Multicores.Single(m => m.CoreCount == d.Cores).Name);
        if ((long)dies.Length * frequencies.Length * cores.Length > 60) throw new AgentError("invalid_value", "At most 60 combinations per sweep.");
        var price = (int?)p["plannedPrice"];
        if (price is < 1 or > 9999) throw new AgentError("invalid_value", "planned-price must be 1..9999.");
        var rows = new JArray();
        foreach (var core in cores)
        foreach (var frequency in frequencies)
        foreach (var die in dies)
        {
            var candidate = d.Clone();
            candidate.Die = die;
            candidate.Frequency = frequency;
            var row = new JObject { ["dieSizeMm2"] = die, ["frequencyMHz"] = frequency / 1000, ["coreCount"] = core };
            try
            {
                candidate.Cores = core == "Single Core" ? 1 : Company.Multicores.SingleOrDefault(m => m.Name == core)?.CoreCount ?? throw new AgentError("invalid_value", "Unknown unlocked core count.");
                if (candidate.AutomateCache) candidate.L3 = Math.Min(Company.GetMaxCache().Item3 / 16, HardwareMath.RecommendedL3CacheSliderValue(candidate.Cores) / 16) * 16;
                var result = Preview(candidate);
                row["stats"] = result["stats"];
                var units = (double)result["stats"]!["monthlyUnitsPerLine"]! * Company.Factory.ProductionCapacity;
                row["estimatedMonthlyUnitsAllLines"] = units;
                if (price != null && result["stats"]!["unitCost"]?.Type != JTokenType.Null) row["grossMarginIfAllSold"] = units * (price.Value - (double)result["stats"]!["unitCost"]!);
            }
            catch (AgentError error) { row["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message }; }
            rows.Add(row);
        }
        return new JObject { ["variants"] = rows, ["draftUnchanged"] = true, ["plannedPrice"] = price, ["note"] = "Native calculations, current yield/wafer/capacity; margin assumes all units sell, not a demand forecast. Bare frequencies are always MHz in fast mode." };
    }
    private static JObject Release(Request r)
    {
        var handler = ProjectCompletedHandler.Instance;
        if (r.Command == "game.projects-release-read") { GameUi.Parameters(r); return new JObject { ["completedCpus"] = new JArray(handler.cpus.Where(c => c.Company == Company).Select(c => new JObject { ["name"] = c.Name, ["cpuId"] = c.SaveID, ["productRef"] = FastGameApi.Reference(c), ["mips"] = c.Ips / 1000, ["unitCost"] = c.RealUnitCost })) }; }
        FastGameApi.Parameters(r, "name", "price", "sellOnMarket", "availableForContracts");
        var matches = handler.cpus.Where(c => c.Company == Company && (c.Name == r.Target || FastGameApi.Reference(c) == r.Target)).ToArray();
        if (matches.Length != 1) throw new AgentError(matches.Length == 0 ? "not_found" : "ambiguous_target", "Choose a unique completed own CPU from projects-release-read.");
        var cpu = matches[0];
        var p = r.Parameters ?? new JObject();
        var price = (int?)p["price"] ?? throw new AgentError("invalid_request", "An explicit price is required for fast release/preview.");
        var name = (string?)p["name"] ?? cpu.Name;
        if (price < 1 || price > 9999 || string.IsNullOrWhiteSpace(name) || name.Length > 20) throw new AgentError("invalid_value", "Price must be 1..9999 and name 1..20 characters.");
        var market = (bool?)p["sellOnMarket"] ?? true;
        var contracts = (bool?)p["availableForContracts"] ?? true;
        var result = new JObject { ["name"] = name, ["price"] = price, ["unitCostAtPrice"] = cpu.RealUnitCostWithNewPrice(price), ["sellOnMarket"] = market, ["availableForContracts"] = contracts, ["outcome"] = "preview" };
        if (r.Command == "game.projects-release")
        {
            cpu.Name = name;
            cpu.Price = price;
            cpu.SellOnMarket = market;
            cpu.SellOnContracts = contracts;
            cpu.Year = DateController.Instance.CurrentDate.Year;
            cpu.Month = DateController.Instance.CurrentDate.Month;
            cpu.IsReleased = true;
            cpu.Company.AddCpu(cpu);
            handler.RemoveCpu(cpu);
            // A release form may already have opened automatically. Clear its binding so Update cannot rename a released CPU.
            if (handler.projectReleaseWindow.currentCpu == cpu) { handler.projectReleaseWindow.currentCpu = null; handler.projectReleaseWindow.gameObject.SetActive(false); }
            result["outcome"] = "cpu_released";
            result["cpuId"] = cpu.SaveID;
        }
        return result;
    }
    internal static JObject Execute(Request r)
    {
        if (r.Command.StartsWith("game.projects-release", StringComparison.Ordinal)) return Release(r);
        if (r.Command == "game.projects-list") { GameUi.Parameters(r); return new JObject { ["projects"] = new JArray(TimerScheduler.Instance.GetTimedObjectsOfType<Project>().Where(p => p.Company == Company).Select(ProjectData)), ["completedCpus"] = new JArray(ProjectCompletedHandler.Instance.cpus.Where(c => c.Company == Company).Select(c => new JObject { ["name"] = c.Name, ["mips"] = c.Ips / 1000, ["cpuId"] = c.SaveID })) }; }
        if (r.Command == "game.cpu-options") return Options(r);
        var d = Current();
        if (r.Command == "game.cpu-variants") return Variants(r, d);
        if (r.Command == "game.cpu-review") return Review(r, d);
        if (r.Command == "game.cpu-develop") return Develop(r, d);
        var next = d.Clone();
        if (r.Command == "game.cpu-select")
        {
            Options(new Request { Target = r.Target });
            var name = (string?)r.Value ?? throw new AgentError("invalid_request", "value must be an exact hardware name.");
            T Choose<T>(IEnumerable<T> list) where T : IHardware { var matches = list.Where(x => x.Name == name).ToArray(); if (matches.Length != 1) throw new AgentError(matches.Length == 0 ? "not_found" : "ambiguous_target", "Choose a unique name from fast cpu-options."); return matches[0]; }
            switch (r.Target) { case "package": next.Package = Choose(Company.Packages); next.Die = Mathf.Clamp(next.Die, next.Package.MinSize, next.Package.MaxSize); break; case "process-node": next.Node = Choose(Company.ProcessNodes); break; case "memory": next.Memory = Choose(Company.Memories); break; case "architecture": next.Architecture = Choose(ArchitectureManager.Instance.GetAvailableArchitectures()); break; }
        }
        else
        {
            CpuGameApi.Validate(r);
            var p = r.Parameters ?? new JObject();
            if (p["memoryController"] != null) throw new AgentError("unsupported_parameter", "Fast draft uses the native default External memory controller; the other native option is not implemented by the game.");
            next.Name = (string?)p["name"] ?? next.Name;
            next.Overclock = (bool?)p["overclock"] ?? next.Overclock;
            next.Smt = (bool?)p["smt"] ?? next.Smt;
            next.AutomateCache = (bool?)p["automateCache"] ?? next.AutomateCache;
            next.Die = (float?)p["dieSizeMm2"] ?? next.Die;
            next.Frequency = p["frequencyMHz"] == null ? next.Frequency : p["frequencyMHz"]!.Value<float>() * 1000;
            if (p["coreCount"] != null)
            {
                var name = (string)p["coreCount"]!;
                next.Cores = name == "Single Core" ? 1 : Company.Multicores.SingleOrDefault(m => m.Name == name)?.CoreCount ?? throw new AgentError("invalid_value", "Unknown unlocked core-count name.");
            }
            next.L1 = Cache(p, "l1CacheKB", 4, next.L1);
            next.L2 = Cache(p, "l2CacheKB", 16, next.L2);
            next.L3 = Cache(p, "l3CacheKB", 64, next.L3);
        }
        if (next.AutomateCache) next.L3 = Math.Min(Company.GetMaxCache().Item3 / 16, HardwareMath.RecommendedL3CacheSliderValue(next.Cores) / 16) * 16;
        var result = Preview(next); // Validate/calculate before replacing the draft; rejected edits leave it intact.
        draft = next;
        return result;
    }
}
