# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
