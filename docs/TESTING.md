# In-Game Testing Checklist for KizShop

Manual verification checklist for a local Windows Unturned dedicated server running RocketModFix.

---

## Prerequisites
- Clean Unturned dedicated server with RocketModFix installed.
- Release zip (`KizShop-Release.zip` or built `KizShop-v0.1.1.zip`).
- Admin permissions on the test server (`/admin <YourSteamID>`).

---

## 1. Clean Server Installation from Release Zip
- [ ] Extract `Plugins/KizShop.dll` into `Servers/<ServerID>/Rocket/Plugins/`.
- [ ] Extract all DLLs from `Libraries/` into `Servers/<ServerID>/Rocket/Libraries/`.
- [ ] Start the server and observe console output:
  - Verify KizShop loads without assembly binding or type load errors.
  - Verify `kizshop.litedb` is created in `Rocket/Plugins/KizShop/`.
  - Verify default `KizShop.configuration.xml`, `KizShop.en.translation.xml`, and `KizShop.shop.json` are generated.

---

## 2. Selling Mechanics & Magazine Exploit Verification
- [ ] Spawn 1 Timberwolf magazine with full 30 rounds (`/give 20/1/100`).
- [ ] Run `/sell 20 30` or `/sell 20 all`.
  - **Expected**: Exactly 1 magazine unit is sold. The command does NOT treat the 30 rounds as 30 separate magazine items.
- [ ] Spawn 1 Timberwolf magazine with 15 rounds (50% ammo capacity) and quality 80%.
- [ ] Run `/sell 20 1`.
  - **Expected**: Payout equals `SellPrice * (15/30) * (80/100)`. Payout is scaled accurately by quality and remaining ammo.
- [ ] Equip a weapon with attachments (sight, barrel, grip, magazine).
- [ ] Try `/sell <WeaponID>`.
  - **Expected**: Refused with message: *"remove attachments first"*.
- [ ] Put items inside an external storage crate or ground area.
- [ ] Try `/sell` for that item ID.
  - **Expected**: Items in storage or on ground are NOT counted or removed; only items in backpack/vest/shirt/pants are eligible.

---

## 3. Inventory Space & Purchase Rollback
- [ ] Fill all inventory slots completely with junk items so 0 slots remain in backpack/vest/shirt/pants.
- [ ] Run `/buy 15 1` (Medkit).
  - **Expected**: Purchase is refused immediately with an insufficient space error. No funds are deducted from balance.
- [ ] Clear space for exactly 1 item, then run `/buy 15 3`.
  - **Expected**: Space check rejects delivery before deduction, or deliveries fail halfway and trigger a full refund deposit with tracked items removed by reference.

---

## 4. Concurrent / In-Flight Purchase Spam
- [ ] Set your balance to exactly the price of one item (e.g. 40 Credits for Medkit) via `/eco set <YourName> 40`.
- [ ] Spam `/buy 15 1` repeatedly in rapid succession using chat or key binds.
  - **Expected**: Exactly ONE purchase succeeds. The other requests are either rejected with *"previous transaction still processing"* (in-flight lock) or *"insufficient funds"*. No negative balance, no duplicate item delivery.

---

## 5. Fractional Cent Precision & Money Validation
- [ ] Try `/pay <PlayerName> 0.005`.
  - **Expected**: Rejected with error: *"Amount cannot have more than 2 decimal places."*
- [ ] Try `/pay <PlayerName> 0.0000001`.
  - **Expected**: Rejected with error: *"Amount cannot have more than 2 decimal places."*
- [ ] Try `/pay <PlayerName> 0.00` or negative amount.
  - **Expected**: Rejected with error: *"Amount must be at least 0.01."*
- [ ] Try `/pay 76561198999999999 10` (random offline non-existent SteamID).
  - **Expected**: Rejected; plugin never creates accounts for arbitrary non-existent SteamIDs.

---

## 6. Catalog Resilience on Corrupt JSON
- [ ] Open `Rocket/Plugins/KizShop/KizShop.shop.json` in a text editor.
- [ ] Introduce a syntax error (e.g., delete a closing brace `}` or insert trailing junk text). Save file.
- [ ] In game, run `/kizshop reload`.
  - **Expected**:
    - Console logs an error showing line and column position.
    - Broken file is backed up as `KizShop.shop.json.broken-<timestamp>`.
    - Existing in-memory shop catalog remains active; `/buy` and `/sell` continue functioning with the previous catalog.
    - The corrupt file is NOT overwritten or destroyed.

---

## 7. Vehicle Purchase Rate Limit & Spawning
- [ ] Purchase a vehicle via `/buyvehicle 1`.
  - **Expected**: Vehicle spawns safely on the ground in front of the player, locked to the buyer.
- [ ] Immediately try purchasing another vehicle within 30 seconds (`VehicleCooldownSeconds`).
  - **Expected**: Refused due to active cooldown timer.
- [ ] Wait for cooldown, purchase a second vehicle. Then try purchasing a third vehicle.
  - **Expected**: Refused when player reaches `MaxVehiclesPerPlayer` (default 2).
- [ ] Stand facing a wall or obstacle where raycast fails to find clear ground.
  - **Expected**: Vehicle spawn checks 4 alternative offsets; if still obstructed, purchase is cancelled and full price is refunded.

---

## 8. Persistence Across Server Restarts
- [ ] Check balance with `/balance`.
- [ ] Earn rewards or adjust balance via `/eco give <YourName> 250`.
- [ ] Stop server cleanly with `shutdown`.
- [ ] Restart server and join. Run `/balance`.
  - **Expected**: Balance is retained exactly as before restart from the `kizshop.litedb` database.
