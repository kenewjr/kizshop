# Panduan In-Game Visual UI KizShop

> [!IMPORTANT]
> **Unity AssetBundle + Client-Side Workshop Mod REQUIRED**
> - Antarmuka visual (Visual UI) memerlukan **Unity AssetBundle** dan mod Workshop di sisi client/server.
> - Asset bundle ini **TIDAK dihasilkan** oleh build repository ini (kode repo ini hanya berisi plugin C# server-side).
> - Tanpa bundle mod tersebut, sistem toko chat (`/shop`, `/buy`, `/sell`, `/cost`) adalah **jalur utama yang didukung penuh**.
> - Karena itu, konfigurasi `EnableVisualUI` default diatur ke `false` sampai bundle aset mod disediakan di server.

## 1. Mengapa Visual UI Memerlukan Effect Asset?

Di Unturned (versi 3.x), antarmuka grafis (UI) kustom untuk plugin **bukan merupakan bawaan kode C#**, melainkan menggunakan sistem **EffectAsset** Unturned (`EffectManager.sendUIEffect`).

Untuk menampilkan UI di layar pemain:
1. Server dan client harus memiliki Unity UI Canvas prefab yang diekspor menjadi Unturned AssetBundle.
2. Asset tersebut memiliki file konfigurasi `Effect.dat` dengan ID yang sesuai (default: `32001`).
3. Jika bundle ini belum dimasukkan ke dalam server (folder `Workshop/Content/` atau `Sandbox/Bundles/`), server/klien tidak memiliki prefab untuk ditampilkan.

## 2. Fitur Proteksi Otomatis KizShop
KizShop memvalidasi ketersediaan Effect ID di server:
- Jika Effect ID `32001` **terdeteksi**: Perintah `/shop` dan `/shopui` akan langsung menampilkan Visual UI.
- Jika Effect ID **belum terpasang**:
  - `/shop` otomatis jatuh ke mode katalog chat dengan format teks warna-warni (Rich Text).
  - `/shopui` memberi pesan instruksi bahwa bundle effect belum terpasang di server dan pemain dapat menggunakan perintah chat (`/buy`, `/sell`, `/cost`).
  - Kursor pemain **tidak akan terkunci (no modal freeze)** ketika effect tidak ada.

## 3. Struktur Asset Effect Unturned

Buat folder bundle di server atau workshop Unturned (misal `KizShopUI/`):

### `KizShopUI.dat`
```text
GUID 7a8e3b1c9d2e4f5a8b1c2d3e4f5a6b7c
Type Effect
ID 32001
UI true
```

### Elemen Hierarchy GameObject Prefab Unity:
Canvas harus memiliki GameObject dengan nama-nama persis berikut:

| Nama GameObject | Tipe Komponen | Fungsi |
|---|---|---|
| `KizShop_Balance` | Text / TextMeshPro | Menampilkan saldo pemain saat ini |
| `KizShop_Qty` | Text | Menampilkan jumlah pembelian (`Qty: 1`) |
| `KizShop_Page` | Text | Menampilkan halaman (`Page 1/3`) |
| `KizShop_TabLabel` | Text | Label tab aktif (`Items` / `Vehicles`) |
| `KizShop_Tab_Items` | Button | Tombol beralih ke tab barang |
| `KizShop_Tab_Vehicles` | Button | Tombol beralih ke tab kendaraan |
| `KizShop_Next` | Button | Tombol halaman berikutnya |
| `KizShop_Prev` | Button | Tombol halaman sebelumnya |
| `KizShop_Qty_1` | Button | Pilih kuantitas x1 |
| `KizShop_Qty_5` | Button | Pilih kuantitas x5 |
| `KizShop_Qty_10` | Button | Pilih kuantitas x10 |
| `KizShop_Close` | Button | Tombol menutup UI |
| `KizShop_Slot_0` .. `KizShop_Slot_5` | GameObject (Container) | Wadah item/slot ke-0 sampai 5 |
| `KizShop_Slot_0_Name` .. `_5_Name` | Text | Nama item |
| `KizShop_Slot_0_Buy` .. `_5_Buy` | Text | Harga beli |
| `KizShop_Slot_0_Sell` .. `_5_Sell` | Text | Harga jual |
| `KizShop_Buy_0` .. `KizShop_Buy_5` | Button | Tombol beli item |
| `KizShop_Sell_0` .. `KizShop_Sell_5` | Button | Tombol jual item |
