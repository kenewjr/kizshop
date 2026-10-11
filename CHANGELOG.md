# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-10-11

### Added
- **Unified Architecture**: All-in-one economy and shop plugin for Unturned on RocketModFix (`netstandard2.1`).
- **Original Implementation**: Clean-room, independent design without third-party plugin code dependencies.
- **Thread-Safe Storage**:
  - Modular `IKizStorage` interface.
  - Zero-config default SQLite backend with transaction logging and automatic log pruning.
  - Optional MySQL backend with row-level locks and transaction support.
  - Main thread dispatching via `TaskDispatcher` for all Unity / Unturned game API operations.
- **Core Economy**:
  - SteamID64-keyed persistent balance accounts using `decimal` precision (no float rounding bugs).
  - Configurable starting balance, currency name, and optional maximum balance cap.
  - Commands: `/balance` (alias `/bal`), `/pay`, `/baltop`, `/eco` (give/take/set).
  - Optional player kill, normal zombie, and mega zombie kill rewards.
  - Optional periodic playtime income rewards.
- **Shop System**:
  - Item and vehicle commerce supporting ID, GUID, and partial name lookups.
  - Commands: `/buy`, `/sell`, `/cost`, `/shop`, `/buyvehicle`.
  - Transaction safety: items checked and granted before money deduction (with automatic rollback on failure); items verified and removed before payout.
  - Safe vehicle spawning with distance offset and collision safety.
  - Dynamic `KizShop.shop.json` catalog with admin management commands (`/kizshop`).
- **Developer API**:
  - Static `KizShopAPI` providing `GetBalance`, `TryWithdraw`, `Deposit`, and `OnBalanceChanged` event.
  - `UconomyCompat` adapter class for easy legacy plugin migration.
- **Quality & CI**:
  - Full GitHub Actions CI/CD workflow building and uploading release DLLs.
  - Comprehensive unit test suite covering price math, balance rules, asset resolution, and SQLite storage.
