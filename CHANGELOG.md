# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
