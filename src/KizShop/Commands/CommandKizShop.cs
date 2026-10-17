using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Shop.Models;

namespace KizShop.Commands
{
    public class CommandKizShop : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "kizshop";
        public string Help => "Admin command to manage KizShop.";
        public string Syntax => "<version|reload|status|backup|kitperm|kitdiscount|additem|removeitem|setitemprice|addveh|removeveh|setvehprice|setcategory|resetpricing|import|giveaway|sale|export>";
        public List<string> Aliases => new List<string> { "ks" };
        public List<string> Permissions => new List<string> { "kizshop.admin" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                int mainThreadId = (plugin.Dispatcher as RocketMainThreadDispatcher)?.MainThreadId ?? System.Threading.Thread.CurrentThread.ManagedThreadId;
                string buildInfo = KizShopPlugin.GetBuildIdentification(mainThreadId);

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                    {
                        KizChat.Say(caller, buildInfo, UnityEngine.Color.cyan);
                        KizChat.Say(caller, plugin.Translate("kizshop_admin_usage"), UnityEngine.Color.yellow);
                    });
                    return;
                }

                var args = ParseQuotedArgs(command);
                string action = args[0].ToLowerInvariant();

                switch (action)
                {
                    case "version":
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, buildInfo, UnityEngine.Color.cyan));
                        break;

                    case "reload":
                        plugin.ReloadPlugin();
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("kizshop_reloaded"), UnityEngine.Color.green));
                        break;

                    case "status":
                        {
                            int accCount = await plugin.Storage.GetAccountCountAsync().ConfigureAwait(false);
                            int txCount = await plugin.Storage.GetTransactionCountAsync().ConfigureAwait(false);
                            int pendingPayouts = await plugin.Storage.GetPendingPayoutCountAsync().ConfigureAwait(false);
                            int pendingBounties = await plugin.Storage.GetPendingBountyRefundCountAsync().ConfigureAwait(false);
                            string backendName = plugin.StorageBackendName;
                            var sale = plugin.Shop?.ActiveSale;
                            string saleStr = (sale != null && sale.IsActive(DateTime.UtcNow))
                                ? $"{sale.Name} (Buy x{sale.BuyMultiplier:0.##}, Sell x{sale.SellMultiplier:0.##}, {(int)Math.Max(0, (sale.ExpiresUtc - DateTime.UtcNow).TotalMinutes)}m left)"
                                : "none";
                            var bHits = plugin.BalTopCache?.Hits ?? 0;
                            var bMiss = plugin.BalTopCache?.Misses ?? 0;
                            var hHits = plugin.HistoryCache?.Hits ?? 0;
                            var hMiss = plugin.HistoryCache?.Misses ?? 0;
                            string cacheStr = $"baltop {bHits}h/{bMiss}m, history {hHits}h/{hMiss}m";
                            string lastBackupStr = plugin.LastBackupStatus ?? "never";
                            string lastPurgeStr = plugin.LastPurgeStatus ?? "never";
                            var stats = plugin.StorageMetrics?.GetStats(TimeSpan.FromMinutes(5)) ?? (0.0, 0.0, 0);
                            string latencyStr = $"avg {stats.AvgMs:F1}ms, max {stats.MaxMs:F1}ms (last 5m)";

                            plugin.Dispatcher.QueueOnMainThread(() =>
                            {
                                KizChat.Say(caller, $"--- KizShop Status (v{KizShopPlugin.VersionString}) ---", UnityEngine.Color.cyan);
                                KizChat.Say(caller, $"Storage: {backendName} | Accounts: {accCount} | Transactions: {txCount}", UnityEngine.Color.white);
                                KizChat.Say(caller, $"Pending: {pendingPayouts} payouts, {pendingBounties} bounty refunds", UnityEngine.Color.white);
                                KizChat.Say(caller, $"Active Sale: {saleStr}", UnityEngine.Color.yellow);
                                KizChat.Say(caller, $"Caches: {cacheStr}", UnityEngine.Color.gray);
                                KizChat.Say(caller, $"Latency: {latencyStr}", UnityEngine.Color.gray);
                                KizChat.Say(caller, $"Last Backup: {lastBackupStr} | Last Purge: {lastPurgeStr}", UnityEngine.Color.gray);
                            });
                        }
                        break;

                    case "backup":
                        {
                            var backupRes = await plugin.BackupManager.CreateBackupAsync(isManual: true).ConfigureAwait(false);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                            {
                                if (backupRes.Success)
                                {
                                    string formattedSize = KizShop.Storage.DatabaseBackupManager.FormatFileSize(backupRes.FileSize);
                                    KizChat.Say(caller, plugin.Translate("kizshop_backup_success", backupRes.FileName, formattedSize), UnityEngine.Color.green);
                                }
                                else
                                {
                                    KizChat.Say(caller, plugin.Translate("kizshop_backup_failed", backupRes.Message), UnityEngine.Color.red);
                                }
                            });
                        }
                        break;

                    case "kitperm":
                        {
                            if (args.Count < 3)
                            {
                                plugin.Dispatcher.QueueOnMainThread(() =>
                                    KizChat.Say(caller, plugin.Translate("kizshop_kitperm_usage"), UnityEngine.Color.yellow));
                                return;
                            }
                            string kpKit = args[1];
                            string kpPerm = args[2].Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : args[2];
                            bool kpSuccess = plugin.Shop.SetKitPermission(kpKit, kpPerm);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                            {
                                if (kpSuccess)
                                {
                                    KizChat.Say(caller, plugin.Translate("kizshop_kitperm_success", kpKit, string.IsNullOrEmpty(kpPerm) ? "none" : kpPerm), UnityEngine.Color.green);
                                }
                                else
                                {
                                    KizChat.Say(caller, plugin.Translate("kit_not_found", kpKit), UnityEngine.Color.red);
                                }
                            });
                        }
                        break;

                    case "kitdiscount":
                        {
                            if (args.Count < 4)
                            {
                                plugin.Dispatcher.QueueOnMainThread(() =>
                                    KizChat.Say(caller, plugin.Translate("kizshop_kitdiscount_usage"), UnityEngine.Color.yellow));
                                return;
                            }
                            string kdKit = args[1];
                            string kdGroup = args[2];
                            decimal? kdMult = null;
                            if (!args[3].Equals("none", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var multVal) || multVal < 0.10m || multVal > 1.00m)
                                {
                                    plugin.Dispatcher.QueueOnMainThread(() =>
                                        KizChat.Say(caller, plugin.Translate("kizshop_kitdiscount_invalid"), UnityEngine.Color.red));
                                    return;
                                }
                                kdMult = multVal;
                            }
                            bool kdSuccess = plugin.Shop.SetKitDiscount(kdKit, kdGroup, kdMult);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                            {
                                if (kdSuccess)
                                {
                                    string desc = kdMult.HasValue ? kdMult.Value.ToString("F2", CultureInfo.InvariantCulture) : "none";
                                    KizChat.Say(caller, plugin.Translate("kizshop_kitdiscount_success", kdKit, kdGroup, desc), UnityEngine.Color.green);
                                }
                                else
                                {
                                    KizChat.Say(caller, plugin.Translate("kit_not_found", kdKit), UnityEngine.Color.red);
                                }
                            });
                        }
                        break;

                    case "additem":
                        // /kizshop additem <id> "<name>" <buyPrice> [sellPrice] [permission] [category]
                        if (args.Count < 4 || !ushort.TryParse(args[1], out var itemId) ||
                            !decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var buyPrice))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_additem_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? sellPrice = null;
                        if (args.Count >= 5 && decimal.TryParse(args[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedSell))
                        {
                            sellPrice = parsedSell;
                        }

                        string itemPerm = args.Count >= 6 ? args[5] : string.Empty;
                        string itemCategory = args.Count >= 7 ? args[6] : "General";

                        string itemValError = plugin.Shop.AddOrUpdateItem(new ItemShopEntry
                        {
                            Id = itemId,
                            Name = args[2],
                            BuyPrice = buyPrice,
                            SellPrice = sellPrice,
                            Permission = itemPerm,
                            Category = itemCategory,
                            Enabled = true
                        });

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (itemValError == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_item_added", args[2], itemId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", itemValError), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "removeitem":
                        if (args.Count < 2 || !ushort.TryParse(args[1], out var remId))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_removeitem_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        bool remSuccess = plugin.Shop.RemoveItem(remId);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (remSuccess)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_item_removed", remId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("cost_not_found", remId), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "setitemprice":
                        if (args.Count < 3 || !ushort.TryParse(args[1], out var priceItemId) ||
                            !decimal.TryParse(args[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var newBuy))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_setitemprice_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? newSell = null;
                        if (args.Count >= 4 && decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNewSell))
                        {
                            newSell = parsedNewSell;
                        }

                        string priceErr = plugin.Shop.SetItemPrice(priceItemId, newBuy, newSell);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (priceErr == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_item_price_set", priceItemId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", priceErr), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "addveh":
                        // /kizshop addveh <id> "<name>" <buyPrice> [sellPrice] [permission] [category]
                        if (args.Count < 4 || !ushort.TryParse(args[1], out var vehId) ||
                            !decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var vehBuyPrice))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_addveh_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? vehSellPrice = null;
                        if (args.Count >= 5 && decimal.TryParse(args[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedVehSell))
                        {
                            vehSellPrice = parsedVehSell;
                        }

                        string vehPerm = args.Count >= 6 ? args[5] : string.Empty;
                        string vehCategory = args.Count >= 7 ? args[6] : "Vehicles";

                        string vehValErr = plugin.Shop.AddOrUpdateVehicle(new VehicleShopEntry
                        {
                            Id = vehId,
                            Name = args[2],
                            BuyPrice = vehBuyPrice,
                            SellPrice = vehSellPrice,
                            Permission = vehPerm,
                            Category = vehCategory,
                            Enabled = true
                        });

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (vehValErr == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_veh_added", args[2], vehId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", vehValErr), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "removeveh":
                        if (args.Count < 2 || !ushort.TryParse(args[1], out var remVehId))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_removeveh_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        bool remVehSuccess = plugin.Shop.RemoveVehicle(remVehId);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (remVehSuccess)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_veh_removed", remVehId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("cost_not_found", remVehId), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "setvehprice":
                        if (args.Count < 3 || !ushort.TryParse(args[1], out var priceVehId) ||
                            !decimal.TryParse(args[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var newVehBuy))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_setvehprice_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        decimal? newVehSell = null;
                        if (args.Count >= 4 && decimal.TryParse(args[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNewVehSell))
                        {
                            newVehSell = parsedNewVehSell;
                        }

                        string vehPriceErr = plugin.Shop.SetVehiclePrice(priceVehId, newVehBuy, newVehSell);
                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            if (vehPriceErr == null)
                            {
                                KizChat.Say(caller, plugin.Translate("kizshop_veh_price_set", priceVehId), UnityEngine.Color.green);
                            }
                            else
                            {
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", vehPriceErr), UnityEngine.Color.red);
                            }
                        });
                        break;

                    case "setcategory":
                        // /kizshop setcategory <id> <category>
                        if (args.Count < 3 || !ushort.TryParse(args[1], out var setCatId))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_setcategory_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        string targetCategory = args[2];
                        if (plugin.Shop.SetItemCategory(setCatId, targetCategory, out string setCatErr))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_category_set", setCatId, targetCategory), UnityEngine.Color.green));
                        }
                        else
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("invalid_catalog_entry", setCatErr), UnityEngine.Color.red));
                        }
                        break;

                    case "resetpricing":
                        // /kizshop resetpricing <id|all>
                        if (args.Count < 2)
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_resetpricing_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        string targetReset = args[1];
                        if (targetReset.Equals("all", StringComparison.OrdinalIgnoreCase))
                        {
                            await plugin.Storage.ResetItemPriceStateAsync(null).ConfigureAwait(false);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_resetpricing_all_success"), UnityEngine.Color.green));
                        }
                        else if (ushort.TryParse(targetReset, out ushort resetItemId))
                        {
                            await plugin.Storage.ResetItemPriceStateAsync(resetItemId).ConfigureAwait(false);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_resetpricing_item_success", resetItemId), UnityEngine.Color.green));
                        }
                        else
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_resetpricing_usage"), UnityEngine.Color.yellow));
                        }
                        break;

                    case "import":
                        // /kizshop import uconomy <mysql|csv> [overwrite] [apply]
                        bool canImport = caller is ConsolePlayer || caller.IsAdmin || caller.HasPermission("kizshop.import");
                        if (!canImport)
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("no_permission"), UnityEngine.Color.red));
                            return;
                        }

                        if (args.Count < 3 || !args[1].Equals("uconomy", StringComparison.OrdinalIgnoreCase))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_import_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        string source = args[2].ToLowerInvariant();
                        bool overwrite = args.Any(a => a.Equals("overwrite", StringComparison.OrdinalIgnoreCase));
                        bool apply = args.Any(a => a.Equals("apply", StringComparison.OrdinalIgnoreCase));

                        var importService = new UconomyImportService(plugin.Storage, () => plugin.Configuration.Instance, plugin.Directory);
                        UconomyImportReport report;

                        if (source == "csv")
                        {
                            report = await importService.ImportFromCsvAsync(overwrite, apply).ConfigureAwait(false);
                        }
                        else if (source == "mysql")
                        {
                            report = await importService.ImportFromMySqlAsync(overwrite, apply).ConfigureAwait(false);
                        }
                        else
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_import_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            KizChat.Say(caller, report.SummaryText, report.ErrorCount > 0 ? UnityEngine.Color.yellow : UnityEngine.Color.green);
                        });
                        KizLog.Info(report.SummaryText);
                        break;

                    case "giveaway":
                        if (args.Count < 2)
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("giveaway_usage"), UnityEngine.Color.yellow));
                            return;
                        }
                        var giveawayArgs = args.Skip(1).ToArray();
                        await CommandGiveaway.ExecuteGiveawayAsync(caller, giveawayArgs).ConfigureAwait(false);
                        break;

                    case "sale":
                        if (args.Count < 2)
                        {
                            var curSale = plugin.Shop.ActiveSale;
                            if (curSale != null && curSale.IsActive(DateTime.UtcNow))
                            {
                                int remMins = (int)Math.Max(1, Math.Ceiling((curSale.ExpiresUtc - DateTime.UtcNow).TotalMinutes));
                                plugin.Dispatcher.QueueOnMainThread(() =>
                                    KizChat.Say(caller, plugin.Translate("sale_status", curSale.Name, curSale.BuyMultiplier, curSale.SellMultiplier, remMins), UnityEngine.Color.cyan));
                            }
                            else
                            {
                                plugin.Dispatcher.QueueOnMainThread(() =>
                                    KizChat.Say(caller, plugin.Translate("sale_usage"), UnityEngine.Color.yellow));
                            }
                            return;
                        }

                        string saleSub = args[1].ToLowerInvariant();
                        if (saleSub == "clear" || saleSub == "off" || saleSub == "stop")
                        {
                            var oldSale = plugin.Shop.ActiveSale;
                            plugin.Shop.ClearSale();
                            plugin.Dispatcher.QueueOnMainThread(() =>
                            {
                                KizChat.Say(caller, plugin.Translate("sale_cleared"), UnityEngine.Color.green);
                                if (oldSale != null)
                                {
                                    KizChat.Broadcast(plugin.Translate("sale_broadcast_ended", oldSale.Name), UnityEngine.Color.yellow);
                                }
                            });
                            return;
                        }

                        if (args.Count < 4 ||
                            !decimal.TryParse(args[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var bMult) ||
                            !decimal.TryParse(args[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sMult) ||
                            !int.TryParse(args[3], out var saleMins))
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("sale_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        var cfg = plugin.Configuration.Instance;
                        if (bMult < cfg.SaleMinBuyMultiplier || bMult > 1.00m ||
                            sMult < 1.00m || sMult > cfg.SaleMaxSellMultiplier ||
                            saleMins < 1 || saleMins > 1440)
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("sale_invalid_params", cfg.SaleMinBuyMultiplier, 1.00m, 1.00m, cfg.SaleMaxSellMultiplier), UnityEngine.Color.red));
                            return;
                        }

                        string saleName = "Happy Hour";
                        if (args.Count >= 5)
                        {
                            saleName = string.Join(" ", args.Skip(4)).Trim();
                        }
                        saleName = Regex.Replace(saleName, @"<[^>]*>", string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(saleName))
                        {
                            saleName = "Happy Hour";
                        }
                        else if (saleName.Length > 32)
                        {
                            saleName = saleName.Substring(0, 32);
                        }

                        plugin.Shop.StartSale(bMult, sMult, saleMins, saleName);

                        int discountPercent = (int)Math.Round((1.0m - bMult) * 100m);
                        int bonusPercent = (int)Math.Round((sMult - 1.0m) * 100m);

                        plugin.Dispatcher.QueueOnMainThread(() =>
                        {
                            KizChat.Say(caller, plugin.Translate("sale_started", saleName, bMult, sMult, saleMins), UnityEngine.Color.green);
                            KizChat.Broadcast(plugin.Translate("sale_broadcast_start", saleName, discountPercent, bonusPercent, saleMins), UnityEngine.Color.yellow);
                        });
                        break;

                    case "export":
                        if (args.Count < 2)
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_export_usage"), UnityEngine.Color.yellow));
                            return;
                        }

                        string exportType = args[1].ToLowerInvariant();
                        var exporter = new DataExportService(plugin.Storage);
                        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                        string exportFolder = System.IO.Path.Combine(plugin.Directory, "exports");

                        if (exportType == "accounts" || exportType == "acc")
                        {
                            string outPath = System.IO.Path.Combine(exportFolder, $"accounts_{timestamp}.csv");
                            int exportedCount = await exporter.ExportAccountsToCsvAsync(outPath).ConfigureAwait(false);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_export_success", exportedCount, outPath), UnityEngine.Color.green));
                        }
                        else if (exportType == "transactions" || exportType == "tx")
                        {
                            string outPath = System.IO.Path.Combine(exportFolder, $"transactions_{timestamp}.csv");
                            int exportedCount = await exporter.ExportTransactionsToCsvAsync(outPath).ConfigureAwait(false);
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_export_success", exportedCount, outPath), UnityEngine.Color.green));
                        }
                        else
                        {
                            plugin.Dispatcher.QueueOnMainThread(() =>
                                KizChat.Say(caller, plugin.Translate("kizshop_export_usage"), UnityEngine.Color.yellow));
                        }
                        break;

                    default:
                        plugin.Dispatcher.QueueOnMainThread(() =>
                            KizChat.Say(caller, plugin.Translate("kizshop_admin_usage"), UnityEngine.Color.yellow));
                        break;
                }
            });
        }

        private static readonly Regex QuotedArgsRegex = new Regex(@"[\""].+?[\""]|[^ ]+", RegexOptions.Compiled);

        private static List<string> ParseQuotedArgs(string[] rawArgs)
        {
            var joined = string.Join(" ", rawArgs);
            var matches = QuotedArgsRegex.Matches(joined);
            var result = new List<string>(matches.Count);
            foreach (Match m in matches)
            {
                result.Add(m.Value.Trim('"'));
            }
            return result;
        }
    }
}
