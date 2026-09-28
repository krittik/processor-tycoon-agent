using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

// A tiny in-process stand-in for the plugin's HTTP bridge with a stateful fake campaign (late game, >1000 lines).
// Responses mimic the plugin's shapes closely enough to drive the CLI compositions end to end without the game.
internal sealed class MockBridge : IDisposable
{
    internal sealed class Product
    {
        public string Name = "", Ref = "", Market = "Desktop";
        public int Manual, Contract = 0;
        public double Demand, Production, Stock, Mips, Price, UnitCost, Popularity;
    }

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public DateTime Date = new(2017, 4, 21);
    public int Capacity = 1150, Pending = 50, ExpansionDay = 3;
    public bool Paused = true;
    public double ResearchFunding = 100, ResearchDays = 200;
    public readonly List<Product> Products = new()
    {
        new Product { Name = "C46", Ref = "product-ui:abcd0001:1:1", Manual = 600, Demand = 20000, Production = 15000, Stock = 0, Mips = 149310, Price = 589, UnitCost = 300, Popularity = 80 },
        new Product { Name = "C45", Ref = "product-ui:abcd0001:1:2", Manual = 500, Demand = 40000, Production = 40000, Stock = 5000, Mips = 96920, Price = 349, UnitCost = 200, Popularity = 90 },
        new Product { Name = "R25", Ref = "product-ui:abcd0001:1:3", Market = "Industries", Manual = 30, Demand = 100, Production = 5000, Stock = 90000, Mips = 4220, Price = 2, UnitCost = 1, Popularity = 20 }
    };
    // Rival table: market -> (company, cpu, mips, price); price changes scripted by day offset.
    public readonly List<(string Market, string Company, string Cpu, double Mips, double Price)> Rivals = new()
    {
        ("Desktop", "Inlet", "Kore i7 7700", 119660, 707), ("Desktop", "Inlet", "Kore i5 7400", 49430, 293),
        ("Mobile", "ARM Co", "M1", 5000, 100), ("Industries", "Texas Increments", "TMX 17000", 4410, 43)
    };
    public readonly List<string> Log = new();
    // Quit simulation: how session-exit answers while the game shuts down, and a fake process state (never the real one).
    public string ExitMode = "ok";
    public volatile bool Exited;
    public bool ExitIgnored;
    public bool DialogOpen;
    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

    public MockBridge() { listener.Start(); _ = Task.Run(Serve); }
    public void Dispose() { stop.Cancel(); listener.Stop(); }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (tcp)
                {
                    var stream = tcp.GetStream();
                    var buffer = new List<byte>();
                    var chunk = new byte[8192];
                    int headerEnd = -1, length = 0;
                    while (true)
                    {
                        var n = await stream.ReadAsync(chunk);
                        if (n <= 0) return;
                        buffer.AddRange(chunk.Take(n));
                        var text = Encoding.ASCII.GetString(buffer.ToArray());
                        if (headerEnd < 0 && (headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(text[..headerEnd], @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            length = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                        }
                        if (headerEnd >= 0 && buffer.Count >= headerEnd + 4 + length) break;
                    }
                    var body = Encoding.UTF8.GetString(buffer.ToArray(), headerEnd + 4, length);
                    string reply;
                    string? raw;
                    lock (this) raw = Raw(JsonNode.Parse(body)!.AsObject());
                    if (raw == null) return; // dropped connection: close without any response
                    reply = raw;
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(bytes);
                }
            });
        }
    }

    private static JsonObject Ok(JsonObject data) => new() { ["ok"] = true, ["operation"] = new JsonObject { ["state"] = "completed", ["result"] = new JsonObject { ["data"] = data } }, ["notifications"] = new JsonObject { ["items"] = new JsonArray(), ["captureAvailable"] = true } };
    private static JsonObject Fail(string code, string message) => new() { ["ok"] = true, ["operation"] = new JsonObject { ["state"] = "failed", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, ["notifications"] = new JsonObject { ["items"] = new JsonArray(), ["captureAvailable"] = true } };
    private string Iso => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Abbrev(double v)
    {
        var a = Math.Abs(v);
        string s = a >= 1e9 ? (v / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B" : a >= 1e6 ? (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M" : a >= 1e3 ? (v / 1e3).ToString("0.00", CultureInfo.InvariantCulture) + "K" : Math.Round(v).ToString(CultureInfo.InvariantCulture);
        return s;
    }

    private int Used => Products.Sum(p => p.Manual + p.Contract);

    private JsonObject Production()
    {
        var free = Math.Max(0, Capacity - Used);
        var rows = new JsonArray(Products.Select(p => (JsonNode?)new JsonObject
        {
            ["productRef"] = p.Ref, ["name"] = p.Name, ["productionLines"] = p.Manual + p.Contract, ["effectiveLines"] = p.Manual + p.Contract, ["requestedTotalLines"] = p.Manual + p.Contract,
            ["manualLines"] = p.Manual, ["contractLines"] = p.Contract, ["linesBasis"] = "native_slider", ["availableRange"] = new JsonObject { ["minLines"] = 0, ["maxLines"] = p.Manual + free },
            ["display"] = new JsonArray($"{p.Name} | FY: 85.00%, 7nm", $"Balance: | {Abbrev((p.Production - p.Demand) / 30)}/d", $"Demand: | {Abbrev(p.Demand)}/m", $"Production: | {Abbrev(p.Production)}/m", p.Stock <= 0 ? "Stock: | Out of Stock!" : $"Stock: | {Abbrev(p.Stock)}")
        }).ToArray());
        return new JsonObject
        {
            ["method"] = "game", ["summary"] = new JsonObject { ["productionLines"] = $"Production lines: | {Abbrev(Capacity)}" + (Pending > 0 ? $" (+{Pending})" : ""), ["linesUsed"] = $"Lines used: | {Abbrev(Used)}", ["usedByContracts"] = "Used by contracts: | 0", ["usedByClients"] = "Used by clients: | 0" },
            ["automation"] = new JsonObject { ["enabled"] = false }, ["settings"] = new JsonObject { ["upgradeLines"] = new JsonObject { ["value"] = true } },
            ["products"] = rows, ["date"] = Iso, ["finances"] = PulseFinances()
        };
    }

    private JsonObject PulseFinances() => new() { ["available"] = true, ["cash"] = new JsonObject { ["display"] = "$50.00M", ["value"] = 50e6 }, ["balance"] = new JsonObject { ["value"] = 4059999.9999999995 }, ["credit"] = new JsonObject { ["value"] = 1.5e9 }, ["sales"] = new JsonObject { ["value"] = 30e6 }, ["default"] = new JsonObject { ["active"] = false } };

    private JsonArray Catalog(string market, bool marketView)
    {
        var rows = new JsonArray();
        foreach (var p in Products.Where(p => p.Market == market))
            rows.Add(new JsonObject { ["values"] = marketView ? new JsonObject { ["company"] = "Claude Silicon", ["cpu"] = p.Name, ["popularity"] = p.Popularity, ["price"] = new JsonObject { ["value"] = p.Price } } : new JsonObject { ["company"] = "Claude Silicon", ["cpu"] = p.Name, ["mips"] = p.Mips, ["year"] = 2017, ["price"] = new JsonObject { ["value"] = p.Price } } });
        foreach (var r in Rivals.Where(r => r.Market == market))
            rows.Add(new JsonObject { ["values"] = marketView ? new JsonObject { ["company"] = r.Company, ["cpu"] = r.Cpu, ["popularity"] = 50, ["price"] = new JsonObject { ["value"] = r.Price } } : new JsonObject { ["company"] = r.Company, ["cpu"] = r.Cpu, ["mips"] = r.Mips, ["year"] = 2017, ["price"] = new JsonObject { ["value"] = r.Price } } });
        return rows;
    }

    private JsonObject Share(string filter)
    {
        JsonObject Market(string name, double size, double unserved, double mine) => new() { ["displayName"] = name, ["size"] = new JsonObject { ["value"] = size }, ["shares"] = new JsonArray(new JsonObject { ["company"] = "Potential Sales", ["share"] = new JsonObject { ["value"] = unserved } }, new JsonObject { ["company"] = "Claude Silicon", ["share"] = new JsonObject { ["value"] = mine } }, new JsonObject { ["company"] = "Inlet", ["share"] = new JsonObject { ["value"] = 100 - unserved - mine } }) };
        JsonObject Item(string segment, string price) => new() { ["market"] = segment, ["recommendations"] = new JsonObject { ["Recommended Price"] = price } };
        return filter switch
        {
            "All" => new JsonObject { ["markets"] = new JsonObject { ["total"] = Market("Total", 3e6, 3, 40), ["desktop"] = Market("Desktop", 2e6, 2, 45), ["mobile"] = Market("Mobile", 8e5, 5, 0), ["industries"] = Market("Industries", 2e5, 10, 1) }, ["guidance"] = new JsonObject { ["items"] = new JsonArray(Item("Industries", "$40")) } },
            "Desktop" => new JsonObject { ["markets"] = new JsonObject { ["total"] = Market("Total", 2e6, 2, 45), ["highEnd"] = Market("High End", 5e5, 1, 60) }, ["guidance"] = new JsonObject { ["items"] = new JsonArray(Item("High End", "$500"), Item("Mid Range", "$250"), Item("Low End", "$0")) } },
            _ => new JsonObject { ["markets"] = new JsonObject { ["total"] = Market("Total", 8e5, 5, 0) }, ["guidance"] = new JsonObject { ["items"] = new JsonArray(Item("High End", "$300"), Item("Mid Range", "$150"), Item("Low End", "$0")) } }
        };
    }

    private JsonObject Research() => new() { ["active"] = new JsonObject { ["name"] = "7nm", ["fundingPercent"] = ResearchFunding, ["researchSpeed"] = "1.0x", ["timeLeft"] = $"{Math.Round(ResearchDays * 100 / ResearchFunding)} days", ["fundingDisplay"] = "$" + Abbrev(ResearchFunding * 1e4) + "/m", ["innovationEffort"] = false, ["controls"] = new JsonObject { ["funding"] = new JsonObject { ["min"] = 0, ["max"] = 100 } } }, ["technologies"] = new JsonArray(new JsonObject { ["name"] = "L3 16MB", ["timeDisplay"] = Date >= new DateTime(2017, 4, 23) ? "100 days" : "300 days", ["costDisplay"] = "$2.00M/m" }) };

    private void AdvanceDay()
    {
        Date = Date.AddDays(1);
        foreach (var p in Products) { p.Stock = Math.Max(0, p.Stock + (p.Production - p.Demand) / 30); }
        // Day 2: the rival cuts its price by 35%; day 3: the pending expansion completes.
        if (Date == new DateTime(2017, 4, 23)) { var i = Rivals.FindIndex(r => r.Cpu == "Kore i7 7700"); Rivals[i] = Rivals[i] with { Price = 459 }; }
        if (Date == new DateTime(2017, 4, 21).AddDays(ExpansionDay)) { Capacity += Pending; Pending = 0; }
        // Demand of C45 drops 40% on day 4 (tests the demand-change portfolio guard).
        if (Date == new DateTime(2017, 4, 25)) Products[1].Demand *= 0.6;
    }

    private string? Raw(JsonObject request)
    {
        var command = request["command"]?.ToString() ?? "";
        if (command == "game.session-exit")
        {
            Log.Add("game.session-exit " + request["target"]);
            if (!ExitIgnored) Exited = true;
            return ExitMode switch
            {
                "drop" => null, "empty" => "", "nonobject" => "[]", "string_error" => """{"ok":false,"error":"bridge shutting down"}""",
                "running_then_drop" => """{"ok":true,"operation":{"id":"op-exit","state":"running"}}""",
                _ => Ok(new JsonObject { ["outcome"] = "exit_requested", ["expectedDisconnect"] = true }).ToJsonString()
            };
        }
        if (command == "operation" && request["target"]?.ToString() == "op-exit") return null;
        if (command == "game.dialog-read" && DialogOpen) return Ok(new JsonObject { ["dialogs"] = new JsonArray(new JsonObject { ["name"] = "Project Completed", ["scope"] = "ProjectReleaseWindow", ["choices"] = new JsonArray("Analysis", "Release") }) }).ToJsonString();
        if (command == "game.save-create") { Log.Add("game.save-create " + request["target"]); return Ok(new JsonObject { ["saved"] = true, ["name"] = request["target"]?.DeepClone() }).ToJsonString(); }
        if (command == "game.garbage") return "[1,2]";
        return Handle(request).ToJsonString();
    }

    private JsonObject Handle(JsonObject request)
    {
        var command = request["command"]?.ToString() ?? "";
        var target = request["target"]?.ToString() ?? "";
        var p = request["parameters"] as JsonObject ?? new JsonObject();
        Log.Add(command + (target.Length > 0 ? " " + target : ""));
        switch (command)
        {
            case "status": return new JsonObject { ["ok"] = true, ["version"] = "0.4.0" };
            case "notifications": return new JsonObject { ["ok"] = true, ["notifications"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = 1, ["gameDate"] = "2017-04-20", ["type"] = "competitor_cpu_released", ["text"] = "Inlet has released a new CPU!" }), ["cursor"] = 1, ["captureAvailable"] = true } };
            case "game.production-expand":
                Pending += (int)(p["lines"]?.GetValue<long>() ?? 0);
                return Ok(new JsonObject { ["outcome"] = "expansion_started", ["pendingExpansionLines"] = Pending, ["verification"] = new JsonObject { ["verifiedBy"] = "pending_lines" } });
            case "game.cpu-options": return Ok(new JsonObject { ["options"] = new JsonArray() });
            case "game.window-list": return Ok(new JsonObject { ["visibleWindows"] = new JsonArray(), ["coveredWindows"] = new JsonArray() });
            case "game.window-close": return Ok(new JsonObject { ["closed"] = true });
            case "game.window-open": return Ok(new JsonObject { ["opened"] = true });
            case "game.dialog-read": return Ok(new JsonObject { ["dialogs"] = new JsonArray() });
            case "game.time-read": return Ok(new JsonObject { ["date"] = Iso, ["paused"] = Paused });
            case "game.production-read": return Ok(Production());
            case "game.product-pulse":
            {
                var prod = Products.FirstOrDefault(x => x.Name == target);
                if (prod == null) return Fail("not_found", "No production row");
                JsonObject I(double v, string? period) => new() { ["available"] = true, ["value"] = v, ["period"] = period };
                return Ok(new JsonObject { ["date"] = Iso, ["product"] = new JsonObject { ["name"] = prod.Name, ["productRef"] = prod.Ref, ["productionLines"] = prod.Manual, ["manualLines"] = prod.Manual, ["contractLines"] = 0, ["demand"] = I(prod.Demand, "month"), ["production"] = I(prod.Production, "month"), ["stock"] = prod.Stock <= 0 ? new JsonObject { ["available"] = true, ["value"] = 0, ["outOfStock"] = true } : I(prod.Stock, null), ["balance"] = I(0, "day") }, ["finances"] = PulseFinances() });
            }
            case "game.desktop-read": return Ok(new JsonObject { ["status"] = new JsonObject { ["date"] = Iso }, ["finances"] = new JsonObject { ["available"] = true, ["availableCredit"] = "$1.50B", ["cashDisplay"] = "$50.00M", ["balanceDisplay"] = "$4.06M/m", ["default"] = new JsonObject { ["active"] = false }, ["items"] = new JsonArray(new JsonObject { ["key"] = "sales", ["display"] = "$30.00M/m" }, new JsonObject { ["key"] = "interest", ["display"] = "$0/m" }, new JsonObject { ["key"] = "production", ["display"] = "$10.00M/m" }, new JsonObject { ["key"] = "research", ["display"] = "$1.00M/m" }) } });
            case "game.sales-read": return Ok(new JsonObject { ["rows"] = new JsonArray(Products.Select(x => (JsonNode?)new JsonObject { ["cPU"] = x.Name, ["allTime"] = Abbrev(1.2e6), ["sold"] = Abbrev(Math.Min(x.Demand, x.Production)), ["missedSales"] = Abbrev(Math.Max(0, x.Demand - x.Production)), ["stock"] = Abbrev(x.Stock), ["income"] = "$1.00M", ["profit"] = "$500.00K", ["unitCost"] = "$" + x.UnitCost.ToString(CultureInfo.InvariantCulture), ["price"] = "$" + x.Price.ToString(CultureInfo.InvariantCulture), ["popularity"] = x.Popularity.ToString(CultureInfo.InvariantCulture) }).ToArray()) });
            case "game.market-catalog": return Ok(new JsonObject { ["rows"] = Catalog(p["market"]?.ToString() ?? "Desktop", p["view"]?.ToString() == "market") });
            case "game.market-share": return Ok(Share(p["market"]?.ToString() ?? "All"));
            case "game.research-read":
            case "game.research-list": return Ok(Research());
            case "game.research-set": ResearchFunding = p["fundingPercent"]!.GetValue<long>(); return Ok(Research());
            case "game.projects-list": return Ok(new JsonObject { ["projects"] = new JsonArray(new JsonObject { ["name"] = "Production Lines", ["timeLeftDays"] = 12, ["status"] = "In progress" }) });
            case "game.product-price":
            {
                var prod = Products.First(x => x.Name == target);
                prod.Price = p["price"]!.GetValue<long>();
                return Ok(new JsonObject { ["outcome"] = "price_confirmed", ["product"] = target, ["appliedValue"] = p["price"]!.DeepClone(), ["date"] = Iso });
            }
            case "game.product-production":
            {
                var prod = Products.First(x => x.Name == target);
                var lines = (int)p["lines"]!.GetValue<long>();
                if (lines > prod.Manual + Math.Max(0, Capacity - Used)) return Fail("invalid_value", "outside range");
                prod.Manual = lines;
                var data = Production(); data["outcome"] = "production_applied";
                return Ok(data);
            }
            case "game.time-advance":
            {
                var days = (int)(p["days"]?.GetValue<long>() ?? 0);
                for (var i = 0; i < days; i++) AdvanceDay();
                var data = new JsonObject { ["outcome"] = "completed", ["daysAdvanced"] = days, ["wakeReasons"] = new JsonArray("target_reached"), ["paused"] = true, ["stoppedDate"] = Iso, ["dialogs"] = new JsonArray(), ["projectChanges"] = new JsonArray(), ["finances"] = new JsonObject { ["available"] = true, ["cash"] = "$50.00M", ["balance"] = "$4.06M/m", ["availableCredit"] = "$1.50B", ["default"] = new JsonObject { ["active"] = false } } };
                if (p["includeProduction"]?.GetValue<bool>() == true) { var prod = Production(); prod["available"] = true; data["production"] = prod; }
                return Ok(data);
            }
            default: return Fail("unsupported_command", "mock: " + command);
        }
    }
}
