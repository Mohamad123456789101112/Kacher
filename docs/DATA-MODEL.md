# Data model & persistence

Provider: **SQLite** through EF Core 8. Schema is code-first and configured by fluent API in
`Data/Configurations.cs`.

```
┌──────────────┐        ┌──────────────┐        ┌─────────────────┐
│  Categories  │1      *│   Products   │1      *│  InvoiceItems   │
│──────────────│────────│──────────────│────────│─────────────────│
│ Id           │        │ Id           │        │ Id              │
│ Name (U)     │        │ Barcode (U)  │        │ InvoiceId  ─────┼──┐
│ ColorHex     │        │ Name         │        │ ProductId       │  │
│ SortOrder    │        │ SellingPrice │        │ Quantity        │  │
└──────────────┘        │ CostPrice    │        │ UnitPrice       │  │
                        │ StockQuantity│        │ DiscountAmount  │  │
                        │ TaxRate      │        │ TotalPrice      │  │
                        │ ReorderLevel │        │ TaxRate/Amount  │  │
                        └──────────────┘        └─────────────────┘  │
                               │1                                    │
                               │*                                    │
                        ┌──────────────┐                             │
                        │StockMovement │                             │
                        └──────────────┘                             │
                                                                     │*
┌──────────────┐   ┌──────────────┐        ┌──────────────┐          │
│    Users     │1 *│   Shifts     │1      *│   Invoices   │1*────────┘
│──────────────│───│──────────────│────────│──────────────│
│ Id           │   │ Id           │        │ Id           │1
│ DisplayName  │   │ ShiftNumber  │        │ InvoiceNumber│*
│ PinHash/Salt │   │ OpeningFloat │        │ CreatedAtUtc │──────┐
│ Role         │   │ CountedCash  │        │ Subtotal     │      │
│ IsActive     │   │ CashDiff     │        │ DiscountAmt  │  ┌──────────────┐
└──────────────┘   │ Status       │        │ TaxAmount    │  │InvoicePayment│
                   │ ZReportJson  │        │ TotalAmount  │  │ Method/Amount│
                   └──────────────┘        │ PaymentMethod│  └──────────────┘
                          │1               │ Status       │
                          │*               │ CashierId    │
                   ┌──────────────┐        │ ShiftId      │
                   │CashMovements │        │ RefundedInvId│
                   └──────────────┘        └──────────────┘
```

Plus two support tables: `HardwareConfigs` (device profile), `AppSettings` (key/value), `AuditLogs`.

---

## Indexes and why they exist

| Index | Query it serves |
| --- | --- |
| `UX_Products_Barcode` (unique) | every scanner lookup — one seek, no scan |
| `IX_Products_Sku` | hand-typed short codes |
| `IX_Products_CategoryId` | category chip switches |
| `IX_Products_(IsActive, Name)` | catalogue grid ordering |
| `UX_Invoices_InvoiceNumber` | reprint / lookup by receipt number |
| `IX_Invoices_CreatedAt` | daily and range reports |
| `IX_Invoices_(ShiftId, Status)` | shift close / Z-report |
| `IX_Invoices_(CreatedAtUtc, Status)` | report window + status filter |
| `IX_InvoiceItems_InvoiceId` | invoice detail and receipt regeneration |
| `IX_Shifts_(Status, OpenedAtUtc)` | “is a shift open?” on every sale |
| `IX_StockMovements_(ProductId, CreatedAtUtc)` | movement ledger panel |

---

## Connection pragmas

Applied per connection in `SqlitePragmaInterceptor`:

```sql
PRAGMA journal_mode = WAL;      -- readers never block the writer
PRAGMA synchronous  = NORMAL;   -- durable under WAL, far cheaper fsync
PRAGMA busy_timeout = 5000;     -- wait instead of throwing "database is locked"
PRAGMA cache_size   = -20000;   -- 20 MB page cache
PRAGMA temp_store   = MEMORY;   -- reporting sorts stay in RAM
PRAGMA mmap_size    = 134217728;
```

`Pooling=True` in the connection string avoids reconnect churn on a terminal that commits a sale
every few seconds.

---

## Money, dates and concurrency

* **Money** — `decimal` mapped to SQLite `TEXT`. Exact arithmetic; no float drift in totals or tax.
* **Dates** — persisted **UTC**; converted with `ToLocalTime()` at the display edge (receipts,
  reports, UI). Invoice numbers use the **local** date of sale, so the sequence resets at local
  midnight, which is what a shopkeeper expects.
* **Invoice numbering** — allocated inside the write transaction:
  `INV-yyyyMMdd-####`, gap-free, based on the highest existing number for that day.
* **Stock** — decremented in the same transaction as the invoice; each delta is also written to
  `StockMovements` with the operator id, so sales and the ledger always reconcile.
* **Refunds** — recorded as a negative invoice (number suffixed `-R`) linked via `RefundedInvoiceId`;
  the original is marked `Refunded`. Nothing is deleted.

---

## Migrations

```powershell
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src\Kacher.Pos --startup-project src\Kacher.Pos
dotnet ef database update --project src\Kacher.Pos --startup-project src\Kacher.Pos
```

`DatabaseBootstrapper.InitializeAsync()` runs `MigrateAsync()` on every launch and then seeds:

* roles/operators (only when `Users` is empty),
* a default hardware profile,
* application settings,
* 8 categories and ~50 products with checksum-valid EAN-13 barcodes.
