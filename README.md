# Kacher POS

A production-shaped **Windows point-of-sale terminal** built with **C# 12 / .NET 8 / WPF**, Material Design 3, EF Core + SQLite, and a hand-written **ESC/POS** hardware engine that talks directly to thermal printers and cash drawers.

The project is deliberately opinionated: it behaves like software that runs on a real counter — offline-first, touch-first, fast under load, and resilient when hardware misbehaves.

---

## 1. What is included

| Area | Implementation |
| --- | --- |
| Shell | Borderless window, custom draggable title bar, window controls, collapsible navigation rail, top status bar |
| Sign-in | Touch PIN pad (4–6 digits), PBKDF2-SHA256 PIN hashing, role matrix (Cashier / Supervisor / Admin), physical keyboard support |
| Sales | Split cart + catalogue layout, category chips, product tiles, quantity steppers, line & order discounts, cash/card payment overlay, change calculation, quick-tender notes |
| Barcode | Keyboard-wedge burst detection (timing-based, never steals focus) **and** serial (COM) scanners; auto-increment of duplicate lines |
| Catalogue | Inline create/edit panel, EAN-13 generation with check digit, soft delete, per-product tax rates, weighted items |
| Inventory | Low-stock worklist, receive / adjust / write-off, immutable stock-movement ledger, stock value at cost |
| Printing | Direct ESC/POS byte generation: receipt, Z-report, hardware test page, QR code, Code128 barcode, logo raster, drawer kick |
| Transports | Network (TCP 9100), Windows spooler RAW, USB (usbprint interface), Serial RS-232, plus a file spool for diagnostics |
| Shifts | Open float, paid-in / paid-out, live totals, counted-cash reconciliation, Z-report, shift JSON snapshot |
| Reports | Daily KPIs, hourly revenue chart, top sellers, tax bands, invoice history, CSV + PDF export |
| Hardware UI | Printer/drawer/scanner configuration, device discovery, test print, test pulse, connection probe |
| PDF | Dependency-free PDF writer for 80 mm receipts, Z-reports and A4 daily summaries |

Everything runs **offline**. SQLite lives under `%LOCALAPPDATA%\Kacher`, so no admin rights are needed.

---

## 2. Getting started

### Prerequisites

* Windows 10 (21H2+) or Windows 11
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* Visual Studio 2022 17.8+ (or Rider) with the **.NET desktop development** workload

### Build & run

```powershell
git clone <repo-url>
cd Kacher
dotnet restore
dotnet build Kacher.sln -c Debug
dotnet run --project src\Kacher.Pos\Kacher.Pos.csproj
```

### Build the Windows application (`.exe`)

WPF is a Windows-only target: the XAML markup compiler and the Windows Desktop targeting pack do not
exist for Linux or macOS, so the application has to be produced on Windows. Two supported routes:

**A. One command, on any Windows machine with the .NET 8 SDK**

```powershell
.\build-exe.ps1            # self-contained folder → dist\KacherPos\Kacher.Pos.exe
.\build-exe.ps1 -Zip       # same, plus dist\KacherPos-win-x64.zip
```

Self-contained means the till needs **nothing installed** — no .NET runtime, no SDK. Copy the folder,
run `Kacher.Pos.exe`, done. Add `-FrameworkDependent` for a much smaller folder if the .NET 8 Desktop
Runtime is already on the machine.

**B. Let GitHub build it**

Push a tag (`git tag v1.0.0 && git push --tags`) or run the **build** workflow manually
(*Actions → build → Run workflow*). It compiles the solution, publishes the self-contained app and
attaches `kacher-pos-win-x64` to the run (and to the release, for tags) — download, unzip, run.

The exe is unsigned, so Windows SmartScreen shows "Unknown publisher" on first launch: *More info →
Run anyway*. Sign it for production with an Authenticode certificate
(`signtool sign /fd SHA256 /a Kacher.Pos.exe`).

### Demo operators

Seeded on first launch (change them in **Settings → Operators** before going live):

| PIN | Operator | Role |
| --- | --- | --- |
| `1234` | Omar Khaled | Admin |
| `2468` | Nour El-Sayed | Supervisor |
| `1111` | Yara Hassan | Cashier |
| `2222` | Ali Mansour | Cashier |

### First run

1. Sign in with `1234`.
2. There is no open shift yet, so the app lands on **Shift** — tap *Open shift* and enter a float (e.g. `200.00`).
3. Go to **Sales**, scan any seeded barcode or tap a tile, then use *Cash* / *Card* / *Process & Print*.
4. If no printer is configured, the receipt is written as a PDF under `%LOCALAPPDATA%\Kacher\reports` and the operator is told where it is.

### Migrations

The app calls `Database.MigrateAsync()` on every launch. Generate the initial migration once after cloning (the schema is code-first):

```powershell
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src\Kacher.Pos --startup-project src\Kacher.Pos
```

Until a migration exists, `DatabaseBootstrapper` falls back to `EnsureCreatedAsync()` so the terminal still runs, and logs a warning telling you to add the baseline.

---

## 3. Architecture

```
Kacher.Pos
├── App.xaml / App.xaml.cs          Composition root (DI), startup, global exception policy
├── Models/                         Entities, enums, permission matrix, report DTOs
├── Data/                           DbContext, fluent config + indexes, PRAGMA interceptor,
│                                   connection factory, bootstrapper (migrate + seed)
├── Services/
│   ├── Repositories/               Async data access (products, invoices, users, hardware)
│   ├── SaleService                 Pure money math + transactional commit
│   ├── ShiftService                Drawer sessions, reconciliation, Z-report data
│   ├── ReportService               Daily/range aggregation, CSV export
│   ├── Hardware/
│   │   ├── EscPos/                 Command vocabulary, fluent builder, drawer parser, encodings
│   │   ├── Transports/             Network / spooler-RAW / USB / serial / file spool + factory
│   │   ├── Scanner/                Keyboard-wedge and serial barcode capture
│   │   ├── EpsonEscPosPrinterService  Print orchestration, retry, drawer policy
│   │   └── HardwareMonitorService  Background health polling for the status bar
│   ├── Printing/                   Receipt/Z-report/test templates + logo rasterizer
│   └── Pdf/                        Minimal PDF writer, receipt & report export
├── ViewModels/                     MVVM (CommunityToolkit.Mvvm, source-generated)
├── Views/                          XAML views, dialogs, shell windows
├── Themes/                         Palette + touch-friendly control styles
├── Converters/                     Value converters (no logic in code-behind)
└── Infrastructure/                 Logging, clock, theme, notifications, session, shell bridge
```

### Design decisions worth knowing

**MVVM without a framework tax.** `ObservableObject`, `[ObservableProperty]` and `[RelayCommand]` generate the boilerplate. Views bind to view models; windows own only chrome, keyboard capture and dialog lifetime.

**One context per operation.** `IDbContextFactory<KacherDbContext>` creates a short-lived context per call. There is no long-lived tracked context, so a slow report can never block a sale, and concurrent reads are safe.

**SQLite tuned for a till.** WAL journalling, `synchronous=NORMAL`, a 20 MB page cache, `busy_timeout` and memory-mapped I/O are applied per connection by `SqlitePragmaInterceptor`. Money is stored as `TEXT` (exact decimal, no binary-float drift).

**Indexes that matter.** `UX_Products_Barcode` (unique) makes every scan one index seek; `IX_Products_CategoryId`, `IX_Invoices_CreatedAt`, `IX_Invoices_ShiftId` and `IX_InvoiceItems_InvoiceId` cover the tab switches, reports and receipt reprints. The barcode lookup is also an EF **compiled query**.

**Async everywhere.** No `.Result`/`.Wait()` in application code. Printing, PDF export, seeding and hardware probes are all `await`ed; the hardware monitor and scanner run on background loops and marshal to the dispatcher only to touch bindings.

**Money is computed once.** `SaleService.Calculate()` is pure and is the single source of truth for subtotal, discounts, tax and total — the bottom bar, the payment overlay and the committed invoice all use it.

**Printing never blocks a sale.** The cart is cleared the moment the invoice commits; the print attempt happens afterwards. If the printer is unreachable, a PDF fallback is generated and the sale remains valid.

**Sign-out restarts the process.** Deliberate: it guarantees no cart, cache or session state can leak between cashiers, which is what an audit expects at hand-over.

---

## 4. Hardware (ESC/POS)

Verification status — what was statically checked, and the on-terminal acceptance runbook — is in [`docs/VERIFICATION.md`](docs/VERIFICATION.md).

The engine writes raw bytes — no GDI rendering, no spooler translation. The full command reference, transport matrix and troubleshooting guide is in [`docs/ESC-POS.md`](docs/ESC-POS.md). Highlights:

* **Receipt template** — logo, store identity, tax registration, itemised lines with wrapped names, per-band VAT breakdown, emphasised total, tender/change, QR for the digital receipt, Code128 invoice barcode, footer, cut.
* **Paper widths** — 80 mm (48 columns) and 58 mm (32 columns) reflow automatically from `HardwareConfig.ReceiptColumns`.
* **Drawer kick** — `ESC p m t1 t2`, default `ESC p 0 25 250` (pin 2, 50 ms on, 500 ms off). The command is operator-editable and parsed from three notations (mnemonic, `0x..`, hex blob).
* **QR** — `GS ( k` model-2 symbol store: select model → module size → error correction → store data → print.
* **Transports** — Network TCP 9100, Windows spooler `RAW`, USB via the `usbprint` device interface, serial RS-232, and a file spool that writes the exact byte stream for diagnosis.

Open **Settings → Hardware** to pick devices, run a test print, pulse the drawer, or probe connectivity. The status bar polls printer/scanner/drawer health every 20 seconds in the background.

---

## 5. Roles

| Permission | Cashier | Supervisor | Admin |
| --- | :-: | :-: | :-: |
| Process sale | ✓ | ✓ | ✓ |
| Open drawer | ✓ | ✓ | ✓ |
| Refund | | ✓ | ✓ |
| Apply discounts | | ✓ | ✓ |
| Adjust stock / edit products | | ✓ | ✓ |
| View reports | | ✓ | ✓ |
| Close shift | | ✓ | ✓ |
| Manage operators / hardware | | | ✓ |

The matrix lives in `Models/User.cs` (`Permissions.Matrix`) and is enforced in both the UI and the services.

---

## 6. Data & backups

```
%LOCALAPPDATA%\Kacher\
├── pos.db            SQLite database (WAL: also pos.db-wal / pos.db-shm)
├── images\           Product images
├── reports\          Exported PDFs and CSVs
├── spool\            Raw ESC/POS captures (diagnostics)
└── logs\             pos-YYYYMMDD.log
```

Back up while the app is closed (or copy `pos.db`, `-wal` and `-shm` together). **Settings → General → Compact database** runs `VACUUM` + `ANALYZE`.

---

## 7. Verification checklist

Because the receipt layout is byte-exact, verify on real hardware before a go-live:

1. **Test print** at both paper widths, checking the cut, the QR and the footer wrapping.
2. **Drawer** pulse on a cash sale and from the manual button (status bar / `F1`).
3. **Scanner**: scan into the field; scan while a tile has focus (wedge detection); confirm duplicate scans increment quantity.
4. **Sale flow**: cash with change, exact cash, card, line discount, order discount, clear cart.
5. **Shift close** with a deliberate 10 EGP variance — the Z-report must show expected vs counted vs difference.
6. **Printer unplugged**: complete a sale, confirm the sale is saved and the PDF fallback is produced.
7. **Theme**: toggle dark/light from the title bar; restart and confirm the choice persisted.

---

## 8. Known limitations / next steps

* **Single terminal.** The schema is multi-terminal-friendly (invoice numbers are per-day sequences), but there is no sync server. A central API + conflict-free invoice-number ranges would be the next step for multi-lane stores.
* **Payments are recorded, not processed.** Card tenders assume an external terminal; integrate a PSP SDK before claiming EMV.
* **Refunds** are implemented in the repository (`InvoiceRepository.RefundAsync`) but not yet surfaced in the UI — the permission and audit trail are ready.
* **Automated tests** are not included yet. The pure components (`SaleService.Calculate`, `CashRounding`, `EscPosBuilder`, `DrawerCommandParser`, `PinHasher`, `SimplePdfWriter`) were written to be testable without a UI or database.
* **Migrations** must be generated once (`dotnet ef migrations add InitialCreate`); until then the app uses `EnsureCreated`.

---

## 9. Tech stack

`net8.0-windows` · WPF · CommunityToolkit.Mvvm 8.2 · MaterialDesignThemes 5.1 · EF Core 8 (SQLite) · Microsoft.Extensions.DependencyInjection · System.IO.Ports · System.Text.Encoding.CodePages

Licensed for internal/commercial use by the repository owner.
