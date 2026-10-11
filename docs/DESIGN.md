# KizShop Design Document

## Architecture Overview

KizShop combines economy management and item/vehicle commerce for Unturned (RocketModFix) into a modular, thread-safe, decoupled architecture.

```
+-----------------------------------------------------------+
|                       RocketMod                           |
|  Commands: /bal, /pay, /baltop, /eco, /buy, /sell, etc.   |
|  Events: PlayerDeath, ZombieDeath, PlaytimeTimer          |
+-----------------------------+-----------------------------+
                              |
+-----------------------------v-----------------------------+
|                        KizShop                            |
|             (KizShopPlugin, Main Lifecycle)               |
+--------------+------------------------------+-------------+
               |                              |
+--------------v-------------+ +--------------v-------------+
|       EconomyService       | |         ShopService        |
|  - In-memory cache         | |  - Shop entry catalog      |
|  - Balance validation      | |  - Lookup (ID, GUID, Name) |
|  - Kill & playtime income  | |  - Inventory validation    |
|  - Atomic transactions     | |  - Safe give/remove/spawn  |
+--------------+-------------+ +--------------+-------------+
               |                              |
               +--------------+---------------+
                              |
+-----------------------------v-----------------------------+
|                      StorageService                       |
|                 (IKizStorage Interface)                   |
|          +-------------------+-------------------+        |
|          |  SqliteStorage    |   MySqlStorage    |        |
|          +-------------------+-------------------+        |
+-----------------------------------------------------------+
                              |
+-----------------------------v-----------------------------+
|                       KizShopAPI                          |
|         Public static facade for other plugins            |
+-----------------------------------------------------------+
```

## Core Principles
1. **Thread Safety & Marshaling**: Database transactions execute asynchronously off the main thread. All Unturned/Unity API calls (item manipulation, vehicle spawning, chat messaging) marshal onto the Unity main thread via `TaskDispatcher.QueueOnMainThread`.
2. **Financial Atomicity**: All balance operations (pay, withdraw, buy, sell) execute atomically at the database and cache level to eliminate race conditions.
3. **Safe Inventory Operations**:
   - Buy: Verify balance -> Check inventory space -> Add item -> Deduct balance. Roll back item if deduction fails; refund money if give fails.
   - Sell: Verify player items in inventory -> Remove items -> Credit balance. Never credit before items are securely removed.
4. **Original Implementation**: Clean-room implementation with zero third-party plugin code copied.

## Database Schema

### Table: `kiz_accounts`
| Column | Type | Constraints | Description |
|---|---|---|---|
| `steam_id` | BIGINT UNSIGNED / TEXT | PRIMARY KEY | SteamID64 |
| `balance` | DECIMAL(18, 2) | NOT NULL DEFAULT 0.00 | Current balance |
| `last_updated` | DATETIME | NOT NULL | Timestamp of last modification |

### Table: `kiz_transactions`
| Column | Type | Constraints | Description |
|---|---|---|---|
| `id` | INTEGER AUTO_INCREMENT | PRIMARY KEY | Transaction ID |
| `timestamp` | DATETIME | NOT NULL | Transaction timestamp |
| `steam_id` | BIGINT UNSIGNED / TEXT | NOT NULL | SteamID64 of account |
| `type` | VARCHAR(32) | NOT NULL | Transfer, Buy, Sell, Eco, Reward |
| `amount` | DECIMAL(18, 2) | NOT NULL | Signed or absolute amount |
| `details` | VARCHAR(255) | NULL | Reason or counterparty info |

## Storage Interface (`IKizStorage`)
```csharp
public interface IKizStorage : IDisposable
{
    Task InitializeAsync();
    Task<decimal> GetBalanceAsync(ulong steamId, decimal defaultStartingBalance);
    Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason);
    Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason, decimal? maxBalance = null);
    Task<bool> TransferAsync(ulong fromSteamId, ulong toSteamId, decimal amount, string reason, decimal? maxBalance = null);
    Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count);
    Task SetBalanceAsync(ulong steamId, decimal amount, string reason);
    Task LogTransactionAsync(ulong steamId, string type, decimal amount, string details);
    Task PruneLogsAsync(int retentionDays);
}
```

## Shop Catalog
Shop entries stored in `KizShop.shop.json`:
- `Items`: List of item shop entries (Id, Guid, Name, BuyPrice, SellPrice, Enabled, Permission, MaxAmountPerPurchase).
- `Vehicles`: List of vehicle shop entries (Id, Guid, Name, BuyPrice, SellPrice, Enabled, Permission).
- Flexible resolution: search order Numeric ID -> Guid -> Case-insensitive name prefix/contain match.

## Public API (`KizShopAPI`)
- `decimal GetBalance(ulong steamId)`
- `bool TryWithdraw(ulong steamId, decimal amount, string reason)`
- `void Deposit(ulong steamId, decimal amount, string reason)`
- `event Action<ulong, decimal, decimal, string> OnBalanceChanged`
