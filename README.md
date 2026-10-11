# KizShop

> Unified Economy & Shop Plugin for Unturned ([RocketModFix](https://github.com/RocketModFix/RocketModFix))

**KizShop** combines player economy management and item/vehicle commerce into a single, high-performance, original codebase. Built specifically for modern Unturned dedicated servers with native .NET Standard 2.1 support, thread-safe asynchronous database access, and atomic transactions.

---

## Features

- **Original Clean-Room Codebase**: Zero code copied from legacy plugins.
- **Precision Economy**:
  - `decimal` precision money handling — zero floating-point rounding errors.
  - Per-player accounts keyed by SteamID64.
  - Configurable starting balance, currency name, symbol, and optional max balance limit.
  - Optional income sources: Player kill rewards, normal zombie kill rewards, mega zombie kill rewards, and periodic playtime income.
- **Item & Vehicle Shop**:
  - Support lookup by **numeric ID**, **GUID**, and **case-insensitive partial name match**.
  - **Fail-safe transaction ordering**:
    - Buying: checks inventory space & balance -> gives items -> deducts funds. Automatically rolls back items if deduction fails.
    - Selling: checks & removes inventory items first -> then deposits money.
    - Vehicles: spawns locked vehicle safely in front of player -> then deducts funds.
  - Granular control per item/vehicle: buy price, sell price, enabled status, max purchase quantity, and permission node.
  - Human-readable `KizShop.shop.json` catalog with built-in admin commands.
- **Swappable Thread-Safe Storage**:
  - **SQLite (Default)**: Embedded, zero-configuration local database (`kizshop.db`).
  - **MySQL**: Optional enterprise backend for multi-server networks.
  - All DB transactions execute asynchronously off the main thread; game operations marshal back to Unity thread via `TaskDispatcher`.
  - Transaction audit log table with automatic age retention pruning.
- **Developer Public API**:
  - Static `KizShopAPI` class for external plugins (boss events, quests, custom rewards).
  - `UconomyCompat` adapter class for easy legacy migration.

---

## Installation

1. Install [RocketModFix](https://github.com/RocketModFix/RocketModFix) on your Unturned dedicated server.
2. Download `KizShop.dll` from the [Releases](https://github.com/Kenewjr/kizshop/releases) page (or build locally).
3. Place `KizShop.dll` into your server's `Rocket/Plugins/` directory.
4. Start the server. The plugin will generate:
   - `Rocket/Plugins/KizShop/KizShop.configuration.xml`
   - `Rocket/Plugins/KizShop/KizShop.shop.json`
   - `Rocket/Plugins/KizShop/KizShop.en.translation.xml`
   - `Rocket/Plugins/KizShop/kizshop.db` (when SQLite is used)

---

## Commands

| Command | Aliases | Description | Default Permissions |
|---|---|---|---|
| `/balance [player]` | `/bal` | Show own balance or target player's balance | `kizshop.balance`, `kizshop.balance.others` |
| `/pay <player\|steamid> <amount>` | | Transfer money to another player | `kizshop.pay` |
| `/baltop` | | Show the top wealthiest players | `kizshop.baltop` |
| `/buy <item> [amount]` | | Purchase items from the shop | `kizshop.buy` |
| `/sell <item> [amount\|all]` | | Sell items from inventory to shop | `kizshop.sell` |
| `/cost <item\|vehicle>` | `/price` | Check buy and sell prices | `kizshop.cost` |
| `/shop [page]` | `/items` | View paginated catalog of available items | `kizshop.shop` |
| `/buyvehicle <name\|id\|guid>` | `/buyv`, `/vehicle` | Buy and safely spawn a vehicle | `kizshop.vehicle` |
| `/eco <give\|take\|set> <player> <amount>` | `/economy` | Admin: adjust player balances | `kizshop.eco`, `kizshop.admin` |
| `/kizshop reload` | `/ks` | Admin: reload config & shop catalog | `kizshop.admin` |
| `/kizshop additem <id> <name> <buy> [sell] [perm]` | | Admin: add or update an item entry | `kizshop.admin` |
| `/kizshop removeitem <id>` | | Admin: remove an item from shop | `kizshop.admin` |
| `/kizshop setitemprice <id> <buy> [sell]` | | Admin: update prices for an item | `kizshop.admin` |
| `/kizshop addveh <id> <name> <buy> [sell] [perm]` | | Admin: add or update a vehicle entry | `kizshop.admin` |
| `/kizshop removeveh <id>` | | Admin: remove a vehicle from shop | `kizshop.admin` |
| `/kizshop setvehprice <id> <buy> [sell]` | | Admin: update prices for a vehicle | `kizshop.admin` |

---

## Permissions

| Permission Node | Description |
|---|---|
| `kizshop.balance` | Allows checking own balance via `/balance`. |
| `kizshop.balance.others` | Allows checking another player's balance. |
| `kizshop.pay` | Allows sending payments via `/pay`. |
| `kizshop.baltop` | Allows viewing `/baltop`. |
| `kizshop.buy` | Allows purchasing items via `/buy`. |
| `kizshop.sell` | Allows selling items via `/sell`. |
| `kizshop.cost` | Allows viewing item and vehicle prices via `/cost`. |
| `kizshop.shop` | Allows browsing the shop via `/shop`. |
| `kizshop.vehicle` | Allows purchasing vehicles via `/buyvehicle`. |
| `kizshop.eco` | Allows modifying balances via `/eco`. |
| `kizshop.admin` | Grants full administration access (`/kizshop`, `/eco`). |

### Example `Permissions.config.xml` Snippet

```xml
<Group>
  <Id>default</Id>
  <DisplayName>Default</DisplayName>
  <Prefix />
  <Suffix />
  <Color>white</Color>
  <Members />
  <Permissions>
    <Permission Cooldown="0">kizshop.balance</Permission>
    <Permission Cooldown="0">kizshop.pay</Permission>
    <Permission Cooldown="0">kizshop.baltop</Permission>
    <Permission Cooldown="0">kizshop.buy</Permission>
    <Permission Cooldown="0">kizshop.sell</Permission>
    <Permission Cooldown="0">kizshop.cost</Permission>
    <Permission Cooldown="0">kizshop.shop</Permission>
    <Permission Cooldown="0">kizshop.vehicle</Permission>
  </Permissions>
</Group>

<Group>
  <Id>admin</Id>
  <DisplayName>Admin</DisplayName>
  <Prefix>[Admin] </Prefix>
  <Suffix />
  <Color>red</Color>
  <Members />
  <Permissions>
    <Permission Cooldown="0">kizshop.admin</Permission>
    <Permission Cooldown="0">kizshop.eco</Permission>
    <Permission Cooldown="0">kizshop.balance.others</Permission>
  </Permissions>
</Group>
```

---

## Configuration Reference (`KizShop.configuration.xml`)

```xml
<?xml version="1.0" encoding="utf-8"?>
<KizShopConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <!-- Database backend: "SQLite" (default) or "MySQL" -->
  <DatabaseType>SQLite</DatabaseType>
  <!-- Path to SQLite database or MySQL connection string -->
  <ConnectionString>kizshop.db</ConnectionString>
  
  <!-- Economy Display -->
  <CurrencyName>Credits</CurrencyName>
  <CurrencySymbol>$</CurrencySymbol>
  <StartingBalance>100.00</StartingBalance>
  <!-- Max balance cap: 0 or negative for unlimited -->
  <MaxBalance>0.00</MaxBalance>
  <BaltopCount>10</BaltopCount>
  
  <!-- Transaction Log Pruning (days to keep, 0 = keep forever) -->
  <LogRetentionDays>30</LogRetentionDays>
  
  <!-- Kill Rewards -->
  <PlayerKillRewardEnabled>true</PlayerKillRewardEnabled>
  <PlayerKillRewardAmount>50.00</PlayerKillRewardAmount>
  <ZombieKillRewardEnabled>true</ZombieKillRewardEnabled>
  <ZombieKillRewardAmount>5.00</ZombieKillRewardAmount>
  <MegaZombieKillRewardAmount>50.00</MegaZombieKillRewardAmount>
  
  <!-- Periodic Playtime Rewards -->
  <PlaytimeIncomeEnabled>true</PlaytimeIncomeEnabled>
  <PlaytimeIncomeIntervalSeconds>600</PlaytimeIncomeIntervalSeconds>
  <PlaytimeIncomeAmount>25.00</PlaytimeIncomeAmount>
  
  <!-- Shop Settings -->
  <VehicleSpawnDistance>5.0</VehicleSpawnDistance>
  <ShopItemsPerPage>8</ShopItemsPerPage>
</KizShopConfiguration>
```

### Shop Catalog Format (`KizShop.shop.json`)

```json
{
  "Items": [
    {
      "Id": 15,
      "Guid": "",
      "Name": "Medkit",
      "BuyPrice": 40.0,
      "SellPrice": 15.0,
      "Enabled": true,
      "Permission": "",
      "MaxAmountPerPurchase": 10
    },
    {
      "Id": 18,
      "Guid": "",
      "Name": "Timberwolf",
      "BuyPrice": 500.0,
      "SellPrice": 150.0,
      "Enabled": true,
      "Permission": "vip.snipers",
      "MaxAmountPerPurchase": 1
    }
  ],
  "Vehicles": [
    {
      "Id": 1,
      "Guid": "",
      "Name": "Black Offroader",
      "BuyPrice": 300.0,
      "SellPrice": 100.0,
      "Enabled": true,
      "Permission": ""
    }
  ]
}
```

---

## Developer API

Reference `KizShop.dll` in your RocketMod plugin to interact directly with player balances:

```csharp
using KizShop.API;

// Check balance
decimal balance = KizShopAPI.GetBalance(playerSteamId);

// Deduct balance atomically (e.g. entry fee or custom shop)
if (KizShopAPI.TryWithdraw(playerSteamId, 50.00m, "EventEntryFee"))
{
    // Withdraw succeeded
}

// Reward player (e.g. quest or boss reward)
KizShopAPI.Deposit(playerSteamId, 100.00m, "QuestCompleteReward");

// Listen for balance updates
KizShopAPI.OnBalanceChanged += (steamId, oldBal, newBal, reason) =>
{
    // React to balance changes
};
```

### Uconomy Compatibility Layer

If migrating plugins originally written against Uconomy:

```csharp
using KizShop.API;

decimal bal = UconomyCompat.GetBalance(steamIdString);
bool success = UconomyCompat.TryWithdraw(steamIdString, 25.00m);
UconomyCompat.Deposit(steamIdString, 10.00m);
```

---

## Building from Source

```bash
dotnet restore
dotnet build -c Release
dotnet test
```

Compiled assembly will be at `src/KizShop/bin/Release/netstandard2.1/KizShop.dll`.

---

## License

This project is licensed under the [MIT License](LICENSE).
Original clean-room implementation. No code copied from third-party plugins.
