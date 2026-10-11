# KizShop Design Document

## Architecture Overview

KizShop combines economy management and item/vehicle commerce for Unturned (RocketModFix) into a modular, thread-safe, decoupled architecture.

```
+--------------------------------------------------------------------------+
|                                RocketMod                                 |
|  Commands: /bal, /pay, /baltop, /eco, /buy, /sell, /buyvehicle, etc.     |
|  Events: PlayerDeath, ZombieDeath, PlaytimeTimer                         |
+------------------------------------+-------------------------------------+
                                     |
+------------------------------------v-------------------------------------+
|                              CommandRunner                               |
|        - Catches all exceptions, rate-limits error logging               |
|        - Dispatches generic translated error messages to players         |
+------------------------------------+-------------------------------------+
                                     |
+------------------------------------v-------------------------------------+
|                         ShopTransactionService                           |
|        - Per-player in-flight locks (ConcurrentDictionary)               |
|        - Withdraw-first purchase flow with reference-tracked rollback    |
|        - Instance-based sell flow with gear page isolation               |
|        - Interfaces: IInventoryGateway, IVehicleGateway, IClock          |
+------------------+-----------------------------------+-------------------+
                   |                                   |
+------------------v----------------+ +----------------v-------------------+
|          EconomyService           | |            ShopService             |
|  - Integer cents (Money struct)   | |  - Shop entry catalog              |
|  - Write-through cache (disabled  | |  - Lookup: exact > prefix > contains|
|    for MySQL)                     | |  - Atomic temp file + replace save |
|  - Main-thread isolated events    | |  - Broken JSON timestamped backup  |
+------------------+----------------+ +------------------------------------+
                   |
+------------------v-------------------------------------------------------+
|                              IKizStorage                                 |
|  +--------------------+--------------------+--------------------+         |
|  |   LiteDbStorage    |   SqliteStorage    |    MySqlStorage    |         |
|  |   (Default Pure)   |    (WAL Mode)      |  (Ascending Locks) |         |
|  +--------------------+--------------------+--------------------+         |
+--------------------------------------------------------------------------+
```

## Core Principles

1. **Threading Model**:
   - The game thread is kept completely free of database I/O. All storage calls run via `Task.Run` with `ConfigureAwait(false)`.
   - SQLite uses `PRAGMA journal_mode=WAL;`.
   - LiteDB executes on a dedicated background worker thread to prevent thread-local transaction leaks and lock contention.
   - All player communications and Unity operations marshal to the main thread via `IMainThreadDispatcher`.
   - Every player command executes inside `CommandRunner.Run` which catches unhandled exceptions, rate-limits error logs, and delivers generic translated error messages.

2. **Money Precision**:
   - Currency is represented exclusively as integer minor units (`long` cents) via the immutable `Money` struct.
   - Amounts with more than 2 decimal places or exceeding `MaxTransactionAmount` are strictly rejected without rounding.
   - Database backends store balances and transaction amounts as 64-bit signed integers.
   - All backends maintain a `schema_version` table (version 2).

3. **Transaction Flow & Rollback Rules**:
   - **Buying Workflow**:
     1. Validate item enabled, permissions, quantity, max purchase limits, and valid money representation.
     2. Acquire per-player in-flight lock (`ConcurrentDictionary<ulong, byte>`). If busy, return `transaction_in_flight`.
     3. Pre-check inventory space before withdrawing.
     4. Atomically withdraw total purchase amount at the storage level.
     5. Deliver items one instance at a time using `inventory.tryAddItem(item, true)`, tracking each delivered `Item` reference.
     6. If delivery stops early or fails: remove tracked items by reference (never by item ID), refund the full purchase price with a refund deposit that ignores `MaxBalance`, and log `Refund`.
     7. Any exception after withdrawal triggers full refund, logging, and a player error message.
   - **Selling Workflow**:
     1. Count and remove by item instance (a full magazine is 1 unit; never trim `Item.amount`).
     2. Scan only the player's own gear pages (`BACKPACK` 3, `VEST` 4, `SHIRT` 5, `PANTS` 6). Storage (7), area/ground (8), and equipped items are excluded.
     3. Refuse guns with attachments or loaded magazines ("remove attachments first").
     4. Calculate payout: `SellPrice * (amount / defaultAmount) * (quality / 100 if ScaleSellByQuality)`.
     5. Remove items by reference first. Pay only after removal succeeds. If payout fails, return the removed items to the player.
   - **Vehicle Workflow**:
     1. Check vehicle rate limit cooldown (`VehicleCooldownSeconds`) and `MaxVehiclesPerPlayer`.
     2. Validate spawn point via ground raycast and overlap checks, trying up to 4 alternate offsets.
     3. Atomically withdraw purchase amount -> spawn vehicle -> destroy and refund if spawn fails.

## Database Schemas

### SQLite Backend
```sql
CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS kiz_accounts (
    steam_id TEXT PRIMARY KEY,
    balance INTEGER NOT NULL DEFAULT 0,
    last_updated TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS kiz_transactions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    steam_id TEXT NOT NULL,
    type TEXT NOT NULL,
    amount INTEGER NOT NULL,
    details TEXT
);
```

### MySQL Backend
```sql
CREATE TABLE IF NOT EXISTS schema_version (
    version INT PRIMARY KEY,
    applied_at DATETIME NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS kiz_accounts (
    steam_id BIGINT UNSIGNED PRIMARY KEY,
    balance BIGINT NOT NULL DEFAULT 0,
    last_updated DATETIME NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS kiz_transactions (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    timestamp DATETIME NOT NULL,
    steam_id BIGINT UNSIGNED NOT NULL,
    type VARCHAR(32) NOT NULL,
    amount BIGINT NOT NULL,
    details VARCHAR(255) NULL,
    INDEX idx_steam_id (steam_id),
    INDEX idx_timestamp (timestamp)
) ENGINE=InnoDB;
```
*MySQL Deadlock Elimination*: In transfers, rows are locked in strictly ascending `steam_id` order (`SELECT ... FOR UPDATE`), with automatic retry on error 1213.

### LiteDB Backend
Collections:
- `schema_version`: `{ _id: int Version, AppliedAt: DateTime }`
- `accounts`: `{ _id: long SteamId, BalanceCents: long, LastUpdated: DateTime }`
- `transactions`: `{ _id: int Id, Timestamp: DateTime, SteamId: long, Type: string, AmountCents: long, Details: string }`

## Storage Interface (`IKizStorage`)

```csharp
public interface IKizStorage : IDisposable
{
    Task InitializeAsync();
    Task<bool> AccountExistsAsync(ulong steamId);
    Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance);
    Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance);
    Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false);
    Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null);
    Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count);
    Task SetBalanceAsync(ulong steamId, Money amount, string reason);
    Task LogTransactionAsync(ulong steamId, string type, Money amount, string details);
    Task PruneLogsAsync(int retentionDays);
}
```

## Public API (`KizShopAPI`)

```csharp
// Async methods (Recommended)
public static Task<decimal> GetBalanceAsync(ulong steamId);
public static Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason = "API");
public static Task<bool> DepositAsync(ulong steamId, decimal amount, string reason = "API");

// Sync methods (Deadlock-safe, cache-first)
public static decimal GetBalance(ulong steamId);
public static bool TryWithdraw(ulong steamId, decimal amount, string reason = "API");
public static void Deposit(ulong steamId, decimal amount, string reason = "API");

// Event
public static event Action<ulong, Money, Money, string> OnBalanceChanged;
```
