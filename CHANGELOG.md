# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.1] - 2026-10-17

### Fixed & Hardened
- **BuyKitAsync Hardening (`ShopTransactionService`)**:
  - Wrapped item delivery in try/catch mirroring `ExecuteBuyAsync`.
  - Added reference tracking for delivered items: on any item delivery failure or exception, removes delivered items via `RemoveItemByReferenceAsync` and refunds the kit price in full via `DepositAsync(ignoreMaxBalance: true)`. Guarantees single-refund execution.
  - Safe subscriber invocation for `OnKitPurchased` moved after delivery and outside rollback try/catch.
  - Delivery exceptions logged with player SteamID and kit name.
- **Storage-Backed Kit Cooldowns**:
  - Replaced in-memory kit cooldowns with storage records across LiteDB, SQLite, and MySQL (`steam_id`, lowercase `kit_name`, `last_purchase`).
  - Implemented read-through in-memory cache, write-through only upon successful transaction, and automatic cleanup on rollback.
- **Resolved Command Collision with uEssentials**:
  - Renamed `CommandKit` to `kizkit` with alias `buykit`. Removed `kit` and `kits` aliases to eliminate collision with uEssentials kit commands.
  - Retained `kizshop.kit` permission node and updated help and translations accordingly.
- **Sale Validation & Arbitrage Protection**:
  - Enforced multiplier boundaries: `0.10 <= buyMultiplier <= 1.00` and `1.00 <= sellMultiplier <= 2.00` via config settings `SaleMinBuyMultiplier` and `SaleMaxSellMultiplier` (clamped in `Normalize()`).
  - Restricted sale duration to 1..1440 minutes with translated error `sale_invalid_params`.
  - Enforced strict buy-price clamping when `AllowSellAboveBuy` is false across `PriceCalculator`, `ExecuteSellAsync`, `ExecuteSellHandAsync`, and `ExecuteSellAll` paths.
  - Sanitized custom sale names: joined multi-word arguments, stripped rich text tags (`<...>`), trimmed whitespace, and capped at 32 characters.
- **Giveaway Safeguards**:
  - Added `GiveawayMaxPerPlayer` configuration setting (default 1000.00, clamped >= 0 in `Normalize()`), rejecting amounts above cap.
  - Added mandatory 30-second confirmation prompt for admin callers (`/giveaway <amount>` -> `/giveaway confirm`) displaying total cost calculation (`amount * onlineCount`). Console callers may execute without confirmation.
  - Added audit logging to transaction history and Discord webhook integration for giveaway distributions. Applied identically to `/eco giveaway`.
- **Async Hygiene**:
  - Replaced fire-and-forget Tasks in `KizShopPlugin` (e.g., `HandleEnemyConnected` balance pre-warming, kill rewards, zombie rewards, bounty claims) with `SafeRunAsync` logging caught exceptions.
  - Re-implemented `MigrateTranslationsOnDisk` using `XDocument` to inspect and update only known keys containing `gold`, creating `.bak` backup once and using atomic temp-file replace.
- **CSV Injection Prevention (`DataExportService`)**:
  - Sanitized text cells (`Type`, `Details`) in CSV exports: prefixing cells starting with `=`, `+`, `-`, `@`, tab (`\t`), or CR (`\r`) with a single quote `'`. Quotes cells, doubles internal quotation marks, and replaces newlines with spaces.
- **Permissions & Starter Kit Price Rebalance**:
  - Updated documentation: added `kizshop.history` and `kizshop.kit` to default player permissions; verified `kizshop.giveaway` and `kizshop.admin` are admin-only.
  - Reduced starter kit default price to $100.00 to match default starting balance.

## [0.5.0] - 2026-10-16

### Added
- **Bundle Kits (`/kit`, `/buykit`)**:
  - Added kit shop system with atomic delivery and rollback refund safety.
  - Included 4 default kits: starter ($1,000), hunter ($2,000), rifle ($3,000), sniper ($3,000).
  - Cooldown and combat tag integration.
- **Daily Login Reward Reminders (`/daily`)**:
  - Automated friendly chat notification sent 4 seconds after player connection if daily reward is unclaimed.
  - Storage methods (`CanClaimDailyAsync`) checking claim availability without mutating streak.
- **Admin Money Giveaway (`/giveaway`, `/eco giveaway`)**:
  - Real-time money distribution to all currently connected players with server-wide broadcast.
  - Fully non-blocking concurrency with `RunOnMainThreadAsync` SteamID collection.
- **Durability & Quality Price Scaling**:
  - Item sell payouts scale linearly based on item durability (`quality / 100m`).
  - Added `MinItemSellQuality` (default 10%) preventing sale of destroyed or severely damaged gear.
- **In-Game Transaction History (`/history`, `/transaksi`)**:
  - Paginated transaction logs in chat displaying timestamp, action type, amount (+/- green/red), and details.
- **Global Sale Events & Happy Hour (`/kizshop sale`)**:
  - Temporary server-wide buy discount and sell bonus multipliers with auto-expiration.
  - Applies across items, vehicles, and bundle kits with server broadcast announcement.
- **CSV Data Export (`/kizshop export`)**:
  - Standalone admin backup dumping accounts or transaction logs into CSV files in `exports/` folder.
- **Multi-Word Parser Support**:
  - Multi-word queries for items, player names, categories, and vehicle names now supported without strict quotes across all commands.

### Fixed
- Replaced fragile `Task.Delay` in main-thread dispatchers with deterministic `RunOnMainThreadAsync`.
- Differentiated kit space exhaustion returning `kit_no_space` instead of generic `kit_delivery_failed`.

## [0.4.1] - 2026-10-13

### Fixed
- **Buy & Transaction Delivery Safety**:
  - Introduced `delivered` tracking in `ShopTransactionService.ExecuteBuyAsync`: refunds are strictly forbidden once items have been placed into player inventory.
  - Post-delivery errors (pressure updates, balance reads, event notifications) are logged as errors and return a `Success` transaction result falling back to `withdrawRes.NewBalance` (never `Money.Zero`).
  - Isolated event subscriber invocations (`GetInvocationList`) with per-subscriber exception swallowing across item purchases, sales, and vehicle purchases.
  - Applied the same irreversible success rule to `/sell`, `/sell hand`, `/sell all <category> confirm`, and `/buyvehicle`.
- **Atomic Reward-Event Idempotency**:
  - Added atomic `TryRecordRewardEventAsync` across LiteDB, SQLite, and MySQL to prevent duplicate reward distributions during concurrent event execution.
  - Added persistent `RewardPayouts` tracking and automated resumption on plugin load for interrupted boss reward payouts.
- **Dynamic Pricing Elapsed Recovery**:
  - Updated `UpdateItemPricePressureAsync` to compute elapsed recovery before applying transaction pressure deltas (`PriceCalculator.ComputeRecoveredPressure`), preventing artificial accumulation of pressure over time.
- **Bounty Escrow & Claim Safety**:
  - Added per-placer in-flight lock for `/bounty set` with guaranteed escrow refund on placement errors.
  - Added atomic `CreateBountyIfWithinLimitAsync` preventing concurrent bounty placements from exceeding per-target maximums.
  - Added `refund_pending` flag for crash-safe bounty expiration refunds; processed automatically on startup and during sweeps.
  - Restored original placer SteamID and creation date if bounty claim payouts fail.
  - Decoupled PvP kill reward cooldown from bounty claims.
- **Configuration Normalization & Input Validation**:
  - Added `KizShopConfiguration.Normalize()` validating and clamping bounty, combat tag, daily reward, and boss reward settings.
  - Hardened `PriceCalculator` against invalid min/max multipliers.
  - Added regex sanitization (`^[A-Za-z0-9_]+$`) for table and column names in Uconomy import tool.
- **Combat Tag Verification**:
  - Deferred damage evaluation via `QueueOnMainThread` in `HandleDamagePlayerRequested`, tagging only when damage was confirmed and victim health actually decreased.

## [0.4.0] - 2026-10-12

### Added
- **Combat Tag**:
  - Added combat tagging on PvP damage events (`DamageTool.damagePlayerRequested`), tagging attacker and victim for `CombatTagSeconds` (default 20s).
  - Configurable action blockers: `CombatTagBlocksBuy`, `CombatTagBlocksSell`, `CombatTagBlocksVehicle`.
  - Cleaned tags automatically on death and disconnect (`Provider.onEnemyDisconnected`).
  - Added `ICombatTagService` with testable `MockClock` integration.
  - Added `kizshop.bypass.combattag` permission to bypass combat tag restrictions.
- **Shop Categories and Search**:
  - Added `Category` property to `ItemShopEntry` (default `"General"`) and `VehicleShopEntry` (default `"Vehicles"`) with backward-compatible catalog JSON deserialization.
  - Enhanced `/shop` command: `/shop` (category overview with counts & page hint), `/shop categories`, `/shop <page>`, `/shop <category> [page]`, and `/shop search <text> [page]`.
  - Added prefix matching and ambiguity detection for category names; rejected purely numeric or control-character categories.
  - Added admin commands: `/kizshop setcategory <id> <category>`, with trailing category parameter supported in `/kizshop additem` and `/kizshop addveh`.
- **Uconomy Import Tool**:
  - Added `/kizshop import uconomy <mysql|csv> [overwrite] [apply]` with `kizshop.import` permission.
  - Default dry-run mode reporting anticipated changes without modifying storage.
  - Idempotent migration from CSV (`uconomy_import.csv`) or MySQL tables with 17-digit SteamID validation and limit checks.
- **Boss Reward API**:
  - Added `KizShopAPI.RewardAsync` and `KizShopAPI.DistributeBossRewardAsync` (callable from any thread without deadlocks).
  - Pure function `BossRewardCalculator` ensuring distributed integer cents sum to exactly 100% of the prize pool.
  - Deterministic remainder cent distribution (highest fractional remainder first, broken by lower SteamID64).
  - Storage idempotency via unique `eventKey` recording in `kiz_reward_events` / `reward_events`.
- **Dynamic Pricing**:
  - Added optional dynamic pricing to catalog items (`SellDecayPerUnit`, `MinSellMultiplier`, `RecoveryPerHour`, `BuyIncreasePerUnit`, `MaxBuyMultiplier`).
  - Added `PriceCalculator` pure function computing sequential multi-unit transactions, linear recovery over mocked time, and strict sell <= buy price clamping.
  - Thread-safe storage pressure tracking (`kiz_item_prices` / `item_prices`) updated only after successful transaction delivery and payment.
  - Added admin command `/kizshop resetpricing <id|all>`.
- **/sell hand & /sell all <category>**:
  - Added `/sell hand [amount]` selling held item instance from hands grid, marshalled to main thread, with gun attachment verification.
  - Added `/sell all <category>` providing summary preview with 30-second confirmation window (`/sell all <category> confirm`) and 0-cent delta inventory protection.
- **Bounty & Daily Reward Systems**:
  - Added `/bounty set <target> <amount>` with escrow deduction, configurable fee sink (`BountyFeePercent`), and hourly expiry sweep.
  - Added `/bounty list [page]` and `/bounty me`.
  - Atomic PvP kill bounty claim (`ClaimBountiesAsync`) preventing double-claims and preventing placers from claiming their own bounties.
  - Added `/daily` reward with streak progression, configurable streak bonus cap, gap day reset, and UTC offset support.
- **New Permissions**:
  - Documented `kizshop.bounty`, `kizshop.daily`, `kizshop.bypass.combattag`, `kizshop.import`.

## [0.3.0] - 2026-10-12

### Removed
- **In-Game Visual UI Purged**: The shop is strictly chat-only (`/shop`, `/buy`, `/sell`, `/cost`, `/buyvehicle`). Deleted `ShopUiService`, `UnturnedShopUiGateway`, `IShopUiGateway`, `CommandShopUi` (`/shopui`, `/sui`, `/vshop`), and `docs/VISUAL_UI.md`.
- Removed UI configuration options (`EnableVisualUI`, `UIEffectId`, `UIKey`, `UseVisualUiForShopCommand`, `UIItemsPerPage`). Old configuration files containing these XML elements continue to deserialize seamlessly without errors.
- Removed `shop_ui_unavailable` translation and `kizshop.shopui` permission.

### Fixed
- **Configuration Default Drift**: Synchronized property initializers and `LoadDefaults()` for all public properties with automated reflection validation.
- **Gun Sell & Buy Semantics**:
  - Replaced crude `magazine != 0` check with byte 0..9 comparison against game asset default state, allowing freshly purchased guns to be sold back.
  - Added `GunPurchaseIncludesMagazine` configuration option (default `false`). When `false`, stripped magazine (bytes 8..9) and ammo (byte 10) on delivery and during default state comparison.
- **Off-Thread Database I/O**:
  - Wrapped all public methods in `LiteDbStorage` and `SqliteStorage` with `await Task.Run(...).ConfigureAwait(false)`, ensuring database I/O never executes synchronously on the main thread.
- **Catalog Validation Timing**:
  - Deferred catalog asset validation to `Level.onPostLevelLoaded` (or immediately if already loaded) on the main thread.
  - Validation preserves configured entry names and does not mutate `Enabled`. Added non-serialized `Available` property to `ItemShopEntry`, marking missing game assets unavailable for resolution and listing.
  - Catalog validation re-runs automatically on `ReloadPlugin()` and `/kizshop reload`.
- **Build Identification**:
  - Logged informational version, DLL build timestamp (UTC), and main thread ID on plugin load.
  - Added `/kizshop version` (and default `/kizshop` output) admin command printing build identification in chat.
- **Auto-Equip Prevention on Delivery**:
  - Added `AutoEquipPurchasedItems` configuration option (default `false`). When `false`, delivers items with `tryAddItem(item, false)`, preventing weapons, useables, and clothing from auto-equipping into player hands.

## [0.2.3] - 2026-10-12

### Fixed
- **Main-Thread Marshalling**:
  - Extended `IMainThreadDispatcher` with awaitable `RunOnMainThreadAsync<T>` and `RunOnMainThreadAsync(Action)` via `TaskCompletionSource` with `RunContinuationsAsynchronously`.
  - Main thread ID captured on plugin load.
  - Marshalled all SDG.Unturned and UnityEngine calls through the dispatcher: `UnturnedInventoryGateway`, `UnturnedVehicleGateway`, `UnturnedShopUiGateway`, and player lookups.
  - Gateways and services updated with asynchronous signatures (`Task` / `Task<T>`).
  - Added debug thread assertions to detect off-main-thread game API calls.
  - Updated `CommandRunner` to initiate execution on caller thread without `Task.Run` wrapping.
- **Inventory Space Check (`CheckSpace`) Rewrite**:
  - Delivery creates `count` distinct Item instances; removed invalid `stacksNeeded = ceil(count / asset.amount)` math.
  - Implemented cell-reservation 2D grid simulation per instance across hands and gear pages (`SLOTS`, `BACKPACK`, `VEST`, `SHIRT`, `PANTS`), honoring dimensions and rotation without mutating live inventory.
  - Introduced `CheckSpaceResult` with explicit reason codes (`PlayerNull`, `InventoryNull`, `AssetNotFound`, `NoSpace`) mapped to specific localization keys (`buy_player_unavailable`, `buy_item_unknown`, `buy_no_space`).
  - Added configurable `VerboseInventoryLogging` debug diagnostics and warning logs on `NoSpace`.
- **Item Creation & Delivery Rollback**:
  - Switched item generation to `new Item(id, true)` so default state, quality, and amounts match game asset definitions.
  - Track delivery failures by index; roll back added items by reference and issue full refunds.
- **Catalog Asset Validation**:
  - On plugin load (main thread), verified all catalog entries with `Assets.find(EAssetType.ITEM, id)`. Missing assets logged and marked unavailable.
  - Corrected default catalog item 81 name to match vanilla Unturned asset name "MRE".
- **Visual UI Hardening**:
  - Removed empty catch blocks in `UnturnedShopUiGateway` and added `KizLog.Error` logging with method name and effect ID.
  - Verified effect asset existence before setting `Modal` plugin widget flag.
  - Added `shop_ui_unavailable` notice in `/shop` before chat fallback catalog.
  - `ShopUiService` slot actions deliver localized chat feedback matching `/buy` and `/sell`.
  - Disconnect and plugin unload clean up UI player states; unsubscribed button event handler on unload.
  - Set default `EnableVisualUI` to `false` until an AssetBundle is installed.
- **Discord & Cleanup**:
  - Removed dead code in `InventoryHelper.cs` and routed legacy checks to `SimulatePlacement`.
  - Replaced blocking `Wait(2000)` in `DiscordBotService.Stop()` with CancellationToken cancellation.
  - Parsed Discord bot numbers with `CultureInfo.InvariantCulture`.

## [0.2.2] - 2026-10-11

### Fixed
- **Inventory Space Check (`CheckSpace`)**:
  - Preliminary attempt to include `PlayerInventory.SLOTS` (Page 2: "Hands" / base player inventory grid) to `StoragePages`. *(Note: fully resolved in 0.2.3 with thread-safe instance reservation simulation).*
  - Included Page 2 in `GetGearItems` so items stored in hands inventory can be sold via `/sell`.

## [0.2.1] - 2026-10-11

### Fixed
- **Chat Rich Text Rendering**: Replaced default plain text chat with `KizChat.Say` using `rich: true` across all commands and game events, properly rendering colored text instead of printing raw `<color=...>` tags in game.
- **UI Effect Detection & Fallback**: Validated server-side `EffectAsset` existence before calling `sendUIEffect`. If the UI bundle is not loaded, `/shop` cleanly falls back to the formatted chat catalog and `/shopui` warns the player, preventing modal cursor freezes without a visible UI.
- **Documentation**: Added [`docs/VISUAL_UI.md`](file:///e:/project/kizshop/docs/VISUAL_UI.md) detailing Unturned UI effect asset structure, `.dat` specification, and hierarchy naming requirements.

## [0.2.0] - 2026-10-11

### Added
- **In-Game Visual UI**:
  - Modal UI Effect for Unturned (`EffectManager.sendUIEffect`, `enablePluginWidgetFlag(Modal)`).
  - Configurable `UIEffectId`, `UIKey`, `UIItemsPerPage`.
  - Item and Vehicle tabs (`KizShop_Tab_Items`, `KizShop_Tab_Vehicles`).
  - Pagination navigation (`KizShop_Next`, `KizShop_Prev`).
  - Quantity multipliers (`KizShop_Qty_1`, `KizShop_Qty_5`, `KizShop_Qty_10`).
  - Slot action buttons for buying and selling directly from UI.
  - New command `/shopui` (aliases: `/sui`, `/vshop`) with permission `kizshop.shopui`.
  - Config option `UseVisualUiForShopCommand` to open UI automatically on `/shop` when no args are provided.
  - Decoupled `IShopUiGateway` for comprehensive unit testability without Unity/Unturned runtime.
- **Discord Webhooks**:
  - Pure managed `HttpClient` webhook integration with zero external NuGet packages.
  - Rich embed alerts for purchases, sales, admin eco actions (`/eco`), and errors.
  - Configurable `DiscordLargeTransactionThreshold` for security alerting on suspicious or large economy movements.
- **Discord Bot (Gateway v10)**:
  - Pure managed `ClientWebSocket` Discord Gateway connection with automated heartbeat loop and reconnect logic.
  - Commands: `!ks bal <steamid>`, `!ks baltop`, `!ks eco <give|take|set> <steamid> <amount>`, `!ks help`.
  - Admin access validation for bot eco actions against `DiscordBotAdminUserIds`.

## [0.1.1] - 2026-10-11

### Security & Correctness Fixes (Addendum 2)
- **Money Precision**: Replaced floating-point representations with integer minor units (`Money` struct in cents, `long`). Strict 2-decimal rejection without rounding. Added `schema_version` table to database backends.
- **Selling Mechanics**: Fixed exploit by counting and removing items by instance instead of `Item.amount`. Scanning restricted strictly to player gear pages (`BACKPACK`, `VEST`, `SHIRT`, `PANTS`), excluding storage, ground, and equipped items. Gun selling refuses weapons with attachments or loaded magazines. Selling price scales proportionally by default amount and quality. Payout delivered only after item removal; items refunded by reference if payout fails.
- **Buying Workflow**: Implemented withdraw-first pattern with per-player in-flight locks (`ConcurrentDictionary<ulong, byte>`). Simulated space check prior to withdrawal. One-by-one instance delivery with `tryAddItem(item, true)`, tracking exact item references. Automatic full refund (ignoring `MaxBalance`) and reference-based item removal on failure or exception.
- **Catalog Safety**: Corrupt JSON preserves existing in-memory catalog, creates a timestamped `.broken-<timestamp>` backup, and saves atomically via temp file replace. Reject sell prices higher than buy prices unless configured. Support quoted command names with spaces.
- **Threading Model**: Replaced all `ContinueWith` and async lambdas in `TaskDispatcher` with `IMainThreadDispatcher` and `CommandRunner.Run`. All database I/O runs through `Task.Run` with `ConfigureAwait(false)`. SQLite uses WAL journal mode.
- **Deadlock-Proof API**: `KizShopAPI` provides cache-first sync wrappers avoiding deadlocks on single-threaded `SynchronizationContext`, alongside recommended Task-returning async methods (`GetBalanceAsync`, `TryWithdrawAsync`, `DepositAsync`).
- **Storage & Packaging**: Added pure managed `LiteDbStorage` as the default zero-config backend. MySQL row locking in ascending order with deadlock retry. Published zip includes `Plugins/` and `Libraries/` separation.
- **Abuse Prevention**: Kill reward cooldowns per victim, same-group reward prevention, suicide check. `/pay` target validation for valid SteamID64 and existing accounts. Vehicle purchase rate limit, max vehicles per player, ground raycast and overlap validation.

## [0.1.0] - 2026-10-11

### Added
- Initial clean-room implementation of KizShop economy and shop plugin.
