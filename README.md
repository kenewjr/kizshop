# KizShop

> Unified Economy & Shop Plugin for Unturned ([RocketModFix](https://github.com/RocketModFix/RocketModFix))

**KizShop** combines player economy management and item/vehicle commerce into a single, high-performance, original codebase. Built specifically for modern Unturned dedicated servers with native .NET Standard 2.1 support, thread-safe asynchronous database access, atomic transactions, and protection against money and item duplication exploits.

---

## Features

- **Original Clean-Room Codebase**: Original implementation without third-party plugin dependencies.
- **Precision Economy**:
  - Exact integer minor unit money handling (`Money` struct in cents, `long`) — strict 2-decimal rejection without rounding.
  - Per-player accounts keyed by SteamID64 with schema version tracking.
  - Configurable starting balance, currency name, symbol, max transaction limit, and optional max balance limit.
  - Optional income sources: Player kill rewards (with cooldown, suicide prevention, and group member prevention), zombie kill rewards, mega zombie kill rewards, and periodic playtime income.
- **Exploit-Safe Item & Vehicle Shop (Chat-Only)**:
  - Support lookup by **numeric ID**, **GUID**, and **case-insensitive partial name match** (exact > prefix > contains). Quoted arguments supported for names with spaces.
  - **Transaction ordering & rollback rules**:
    - **Buying**: checks space simulation -> acquires per-player lock -> atomically withdraws total price at storage level -> delivers one instance at a time with `tryAddItem(item, true)` tracking references. On failure or exception: removes tracked items by reference, issues a full refund deposit (ignoring `MaxBalance`), and cancels the entire purchase.
    - **Selling**: counts and removes by item instance (magazines count as 1 unit; no `Item.amount` trimming). Scans only player gear pages (`BACKPACK`, `VEST`, `SHIRT`, `PANTS`), excluding storage, ground, and equipped items. Rejects guns with attachments or loaded magazines. Payout calculated based on durability/quality and ammo ratio. Payout delivered only after item removal; rolls back items if payout fails.
    - **Vehicles**: checks rate limit cooldown and `MaxVehiclesPerPlayer`, validates safe spawn point via raycast and offset alternatives, withdraws funds, and spawns locked vehicle.
  - Resilient `KizShop.shop.json` catalog with atomic save (`File.Replace`), backup preservation on corrupt JSON (`.broken-<timestamp>`), and admin management commands (`/kizshop`).
- **Swappable Thread-Safe Storage**:
  - **LiteDB (Default)**: Embedded, pure-managed zero-config database (`kizshop.litedb`). No native binaries required.
  - **SQLite**: Local SQLite database with `PRAGMA journal_mode=WAL`.
  - **MySQL**: Enterprise backend with ascending row locks and automatic deadlock retry.
  - All DB transactions execute asynchronously off the main thread; game thread remains completely free of database I/O.
- **Developer Public API**:
  - Static `KizShopAPI` providing deadlock-proof sync methods alongside recommended Task-returning async methods: `GetBalanceAsync`, `TryWithdrawAsync`, `DepositAsync`, and `OnBalanceChanged` event.
  - `UconomyCompat` adapter class for legacy plugin migration.

---

## Installation

1. Install [RocketModFix](https://github.com/RocketModFix/RocketModFix) on your Unturned dedicated server.
2. Download `KizShop-Release.zip` from the [Releases](https://github.com/Kenewjr/kizshop/releases) page.
3. Extract the release archive:
   - Place `Plugins/KizShop.dll` into your server's `Rocket/Plugins/` directory.
   - Place all DLLs from `Libraries/` into your server's `Rocket/Libraries/` directory.
4. Start the server. The plugin will generate:
   - `Rocket/Plugins/KizShop/KizShop.configuration.xml`
   - `Rocket/Plugins/KizShop/KizShop.shop.json`
   - `Rocket/Plugins/KizShop/KizShop.en.translation.xml`
   - `Rocket/Plugins/KizShop/kizshop.litedb` (when LiteDB is used)

---

## Commands

| Command | Aliases | Description | Default Permissions |
|---|---|---|---|
| `/balance [player]` | `/bal` | Show own balance or target player's balance | `kizshop.balance`, `kizshop.balance.others` |
| `/pay <player\|steamid> <amount>` | | Transfer money to an online player or existing account | `kizshop.pay` |
| `/baltop` | | Show the top wealthiest players | `kizshop.baltop` |
| `/shop` | `/items` | List available categories with item counts and page hint | `kizshop.shop` |
| `/shop categories` | | List all shop categories | `kizshop.shop` |
| `/shop <page>` | | Browse all items paged | `kizshop.shop` |
| `/shop <category> [page]` | | Browse items within a category (exact or unique prefix match) | `kizshop.shop` |
| `/shop search <text> [page]` | | Search items by name substring or exact ID | `kizshop.shop` |
| `/cost <item\|vehicle>` | `/price` | Check buy and sell prices (displays base price if dynamic pricing active) | `kizshop.cost` |
| `/buy <item> [amount]` | | Purchase items from the shop | `kizshop.buy` |
| `/sell <item> [amount\|all]` | | Sell items from inventory to shop | `kizshop.sell` |
| `/sell hand [amount]` | | Sell the item instance currently held in hands | `kizshop.sell` |
| `/sell all <category> [confirm]` | | Sell all gear items in category (summary preview with 30s confirmation) | `kizshop.sell` |
| `/buyvehicle <name\|id\|guid>` | `/buyv`, `/vehicle` | Buy and safely spawn a vehicle | `kizshop.vehicle` |
| `/bounty set <player\|steamid> <amount>` | | Place a bounty on a target player into escrow | `kizshop.bounty` |
| `/bounty list [page]` | | List top active bounties on the server | `kizshop.bounty` |
| `/bounty me` | | Check active bounties placed on you | `kizshop.bounty` |
| `/daily` | | Claim daily login reward with consecutive streak bonuses | `kizshop.daily` |
| `/eco <give\|take\|set> <player> <amount>` | `/economy` | Admin: adjust player balances | `kizshop.eco`, `kizshop.admin` |
| `/kizshop [version]` | `/ks` | Admin: print build version or admin menu | `kizshop.admin` |
| `/kizshop reload` | | Admin: reload config & shop catalog | `kizshop.admin` |
| `/kizshop additem <id> <name> <buy> [sell] [perm] [category]` | | Admin: add or update an item entry | `kizshop.admin` |
| `/kizshop removeitem <id>` | | Admin: remove an item from shop | `kizshop.admin` |
| `/kizshop setitemprice <id> <buy> [sell]` | | Admin: update prices for an item | `kizshop.admin` |
| `/kizshop setcategory <id> <category>` | | Admin: change the category of an item | `kizshop.admin` |
| `/kizshop resetpricing <id\|all>` | | Admin: reset dynamic pricing pressure for an item or all items | `kizshop.admin` |
| `/kizshop import uconomy <mysql\|csv> [overwrite] [apply]` | | Admin/Console: import balances from Uconomy (dry run by default) | `kizshop.import`, `kizshop.admin` |
| `/kizshop addveh <id> <name> <buy> [sell] [perm] [category]` | | Admin: add or update a vehicle entry | `kizshop.admin` |
| `/kizshop removeveh <id>` | | Admin: remove a vehicle from shop | `kizshop.admin` |
| `/kizshop setvehprice <id> <buy> [sell]` | | Admin: update prices for a vehicle | `kizshop.admin` |

### Permissions Summary

| Permission | Description |
|---|---|
| `kizshop.balance` | Check own balance (`/balance`) |
| `kizshop.balance.others` | Check target player's balance (`/balance <player>`) |
| `kizshop.pay` | Transfer money to other players (`/pay`) |
| `kizshop.baltop` | View top wealthiest players (`/baltop`) |
| `kizshop.shop` | Browse shop catalog, categories, and search (`/shop`) |
| `kizshop.cost` | Check item and vehicle prices (`/cost`) |
| `kizshop.buy` | Purchase items (`/buy`) |
| `kizshop.sell` | Sell items (`/sell`, `/sell hand`, `/sell all <category>`) |
| `kizshop.vehicle` | Purchase and spawn vehicles (`/buyvehicle`) |
| `kizshop.bounty` | Place, view, and check bounties (`/bounty`) |
| `kizshop.daily` | Claim daily rewards and maintain streaks (`/daily`) |
| `kizshop.bypass.combattag` | Bypass combat tag shop restrictions while in combat |
| `kizshop.import` | Execute Uconomy data migrations (`/kizshop import`) |
| `kizshop.eco` | Admin balance manipulation (`/eco`) |
| `kizshop.admin` | Admin shop catalog management and configuration (`/kizshop`) |

### Discord Bot Commands (Gateway v10)

| Command | Description | Permission |
|---|---|---|
| `!ks bal <SteamID64>` | Check balance of specified player | Anyone |
| `!ks baltop` | Display top wealthiest accounts | Anyone |
| `!ks eco <give\|take\|set> <SteamID64> <amount>` | Admin balance adjustment | `DiscordBotAdminUserIds` |
| `!ks help` | List available bot commands | Anyone |

---

## Configuration Reference (`KizShop.configuration.xml`)

```xml
<?xml version="1.0" encoding="utf-8"?>
<KizShopConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <!-- Database backend: "LiteDB" (default), "SQLite", or "MySQL" -->
  <DatabaseType>LiteDB</DatabaseType>
  <!-- Connection string or file path -->
  <ConnectionString>kizshop.litedb</ConnectionString>
  
  <!-- Economy Settings -->
  <CurrencyName>Credits</CurrencyName>
  <CurrencySymbol>$</CurrencySymbol>
  <StartingBalance>100.00</StartingBalance>
  <MaxBalance>0.00</MaxBalance>
  <MaxTransactionAmount>10000000.00</MaxTransactionAmount>
  <BaltopCount>10</BaltopCount>
  
  <!-- Combat Tag Settings -->
  <CombatTagEnabled>true</CombatTagEnabled>
  <CombatTagSeconds>20</CombatTagSeconds>
  <CombatTagBlocksBuy>true</CombatTagBlocksBuy>
  <CombatTagBlocksSell>true</CombatTagBlocksSell>
  <CombatTagBlocksVehicle>true</CombatTagBlocksVehicle>

  <!-- Dynamic Pricing Settings -->
  <DynamicPricingDefaultEnabled>false</DynamicPricingDefaultEnabled>

  <!-- Uconomy Import Settings -->
  <UconomyImportConnectionString></UconomyImportConnectionString>
  <UconomyImportTable>uconomy</UconomyImportTable>
  <UconomyImportSteamIdColumn>steamId</UconomyImportSteamIdColumn>
  <UconomyImportBalanceColumn>balance</UconomyImportBalanceColumn>

  <!-- Boss Reward Settings -->
  <BossRewardTopDamagePercent>30</BossRewardTopDamagePercent>
  <BossRewardLastHitPercent>10</BossRewardLastHitPercent>
  <BossRewardParticipationPercent>60</BossRewardParticipationPercent>
  <BossRewardMinDamageSharePercent>1.0</BossRewardMinDamageSharePercent>

  <!-- Bounty System Settings -->
  <BountyEnabled>true</BountyEnabled>
  <BountyMinAmount>100.00</BountyMinAmount>
  <BountyMaxPerTarget>100000.00</BountyMaxPerTarget>
  <BountyFeePercent>10</BountyFeePercent>
  <BountyExpiryDays>7</BountyExpiryDays>
  <BountyAnnounceInChat>true</BountyAnnounceInChat>

  <!-- Daily Reward System Settings -->
  <DailyRewardAmount>100.00</DailyRewardAmount>
  <DailyStreakBonusPercentPerDay>10</DailyStreakBonusPercentPerDay>
  <DailyMaxStreakDays>7</DailyMaxStreakDays>
  <DailyResetUtcOffsetHours>7</DailyResetUtcOffsetHours>
  
  <!-- Transaction Log Pruning (days to keep, 0 = keep forever) -->
  <LogRetentionDays>30</LogRetentionDays>
  
  <!-- Kill Rewards & Abuse Prevention -->
  <PlayerKillRewardEnabled>true</PlayerKillRewardEnabled>
  <PlayerKillRewardAmount>50.00</PlayerKillRewardAmount>
  <PlayerKillRewardCooldownSeconds>600</PlayerKillRewardCooldownSeconds>
  <ZombieKillRewardEnabled>true</ZombieKillRewardEnabled>
  <ZombieKillRewardAmount>5.00</ZombieKillRewardAmount>
  <MegaZombieKillRewardAmount>50.00</MegaZombieKillRewardAmount>
  
  <!-- Periodic Playtime Rewards -->
  <PlaytimeIncomeEnabled>true</PlaytimeIncomeEnabled>
  <PlaytimeIncomeIntervalSeconds>600</PlaytimeIncomeIntervalSeconds>
  <PlaytimeIncomeAmount>25.00</PlaytimeIncomeAmount>
  
  <!-- Shop Settings -->
  <VehicleSpawnDistance>5.0</VehicleSpawnDistance>
  <VehicleCooldownSeconds>30</VehicleCooldownSeconds>
  <MaxVehiclesPerPlayer>2</MaxVehiclesPerPlayer>
  <ScaleSellByQuality>true</ScaleSellByQuality>
  <AllowSellAboveBuy>false</AllowSellAboveBuy>
  <GunPurchaseIncludesMagazine>false</GunPurchaseIncludesMagazine>
  <AutoEquipPurchasedItems>false</AutoEquipPurchasedItems>
  <ShopItemsPerPage>8</ShopItemsPerPage>

  <!-- Discord Webhook Integration -->
  <DiscordWebhookEnabled>false</DiscordWebhookEnabled>
  <DiscordWebhookUrl></DiscordWebhookUrl>
  <DiscordLogPurchases>true</DiscordLogPurchases>
  <DiscordLogAdminEco>true</DiscordLogAdminEco>
  <DiscordLargeTransactionThreshold>5000.00</DiscordLargeTransactionThreshold>

  <!-- Discord Bot Integration (Gateway v10) -->
  <DiscordBotEnabled>false</DiscordBotEnabled>
  <DiscordBotToken></DiscordBotToken>
  <DiscordBotPrefix>!ks </DiscordBotPrefix>
  <DiscordBotAdminUserIds></DiscordBotAdminUserIds>
</KizShopConfiguration>
```

---

## Developer API

Reference `KizShop.dll` in your RocketMod plugin to interact directly with player balances:

```csharp
using KizShop.API;
using System.Threading.Tasks;

// Async API (Recommended)
decimal balance = await KizShopAPI.GetBalanceAsync(playerSteamId);

if (await KizShopAPI.TryWithdrawAsync(playerSteamId, 50.00m, "EventEntryFee"))
{
    // Withdraw succeeded
}

await KizShopAPI.DepositAsync(playerSteamId, 100.00m, "QuestCompleteReward");

// Sync API (Deadlock-safe, cache-first)
decimal syncBal = KizShopAPI.GetBalance(playerSteamId);
bool withdrew = KizShopAPI.TryWithdraw(playerSteamId, 25.00m, "EntryFee");

// Balance change event
KizShopAPI.OnBalanceChanged += (steamId, oldBal, newBal, reason) =>
{
    // Raised on main thread with isolated subscriber error handling
};
```

---

## Building from Source

```bash
dotnet restore
dotnet build -c Release
dotnet test
dotnet publish -c Release src/KizShop/KizShop.csproj -o publish/
```

---

## License

This project is licensed under the [MIT License](LICENSE). Original clean-room implementation.
