# Kacher POS — product guide

Everything a reseller needs to position, deploy and support the terminal. Written for the person
selling it, not for the developer.

---

## 1. One-line pitch

> A fast, offline-first Windows POS for single-counter retailers: scans, prints, reconciles the
> drawer and protects your data — with no monthly cloud dependency and no internet requirement.

## 2. Who it is for

| Fits well | Does not fit (yet) |
| --- | --- |
| Convenience stores, mini-markets, cafés, bakeries, pharmacies | Multi-branch chains needing central stock |
| One to a few tills per shop, each with its own database | Franchise reporting across locations |
| Shops with a thermal printer (58 mm or 80 mm) and a cash drawer | Restaurants needing table/order-course flows |
| Markets where 14 % VAT and EGP pricing apply | Markets needing fiscal-device certification |

## 3. What the customer gets

- **Till screen** — scan or tap, quantity steppers, line + order discounts, cash/card/split with
  change, drawer kick on cash sales, receipt printing in under a second.
- **Dashboard** — today's sales, average basket, tax collected, cash vs card, what needs attention,
  best sellers, sales by hour.
- **Invoices** — search any past sale, reprint it, save a PDF, or refund it (stock returns
  automatically and the refund appears as a negative document in the Z-report).
- **Catalogue & inventory** — inline product editing, EAN-13 generation, per-product VAT, weighted
  items, stock ledger, low-stock worklist.
- **Shift & end of day** — opening float, paid-in/paid-out, counted cash, variance, Z-report.
- **Reports** — daily KPIs, hourly chart, top products, tax bands, CSV + PDF export.
- **Hardware setup** — printer discovery, test page, drawer test, connection probe, 58/80 mm layouts.
- **Data safety** — automatic daily backup, manual backup, 30-copy rotation.

## 4. Deployment in 20 minutes

1. Copy `KacherPOS-<version>-setup.exe` to the till and run it (per-user install, no admin needed).
2. Launch **Kacher POS**. First run creates `%LOCALAPPDATA%\Kacher\pos.db`, seeds a demo catalogue
   and shows the PIN screen.
3. Sign in as admin (`1234` on a fresh install) → **Settings → Operators** → change the PINs, remove
   the demo operators.
4. **Hardware** → pick the printer queue, paper width, code page → **Test print** → **Test drawer**.
5. **Scan test**: open **Sales**, scan any barcode; the line must appear without clicking the field.
6. **Shift** → open a shift with the float in the drawer.
7. Point a cloud-sync folder (OneDrive/Dropbox) at `%LOCALAPPDATA%\Kacher\backups` for off-site
   copies, or copy that folder to a USB stick at closing.

Full acceptance checklist: [`VERIFICATION.md`](VERIFICATION.md).

## 5. What makes it feel premium

- Material Design 3 dark/light themes, 16 px rounded surfaces, no default Windows chrome.
- A hand-built vector icon set on a 24 px grid — identical at 100 %, 125 % and 150 % scaling, no
  icon font to go missing.
- Money is `decimal` end to end, formatted with an invariant dot separator on receipts so a locale
  change can never print `1.234,50`.
- Printing is byte-level ESC/POS: no driver rendering lag, receipts start printing immediately.
- Every I/O path is `async`; the UI never blocks on the database, the printer or the disk.

## 6. Licensing model that works for this product

| Edition | What is included | Suggested list |
| --- | --- | --- |
| **Single till** | One terminal, unlimited products/operators, 12 months of updates | one-off licence |
| **Shop pack** | Up to 3 terminals at the same address, priority support | 2.5 × single |
| **Support plan** | Remote setup, receipt layout changes, hardware troubleshooting | annual, optional |

Terminal count is enforced socially (per-install licence key, if added) rather than technically,
because a shop with two tills and one licence is a support conversation, not a piracy case.

## 7. Honest limitations to disclose

Say these out loud before signing a deal — they prevent almost every support dispute:

1. **One database per terminal.** Two tills do not share live stock or sales.
2. **No fiscal-device / e-invoice integration.** Receipts are commercial documents, not
   tax-authority submissions.
3. **No purchasing module.** Stock enters through *Inventory → Receive*.
4. **Arabic UI is not localised yet.** Arabic text prints correctly on ESC/POS (`CP1256`), but the
   screens are English with left-to-right layout.
5. **No automatic updates.** Ship a new installer; the database and settings are preserved.
6. **Windows 10 21H2+ / 11, x64.** No ARM build, no Linux, no web client.

## 8. Support playbook

| Symptom | First move |
| --- | --- |
| Nothing prints | **Hardware → Probe printer**. If unreachable, check the queue name / IP:9100. `%LOCALAPPDATA%\Kacher\spool` holds the exact bytes of the last job. |
| Receipt prints garbage | Wrong code page — set it to `WPC1256` (Arabic) or `WPC1252` (Latin) and re-test. |
| Drawer does not open | `ESC p 0 25 250` on pin 2 is the default; some drawers use pin 5. Test both from **Hardware**. |
| Scanner double-adds items | Increase *inter-key timeout* to 80–100 ms. |
| "Database is locked" | Never run two copies of the app; the single-instance guard blocks it. Check the backups folder first, then `Settings → Compact database`. |
| Wrong totals after a refund | The refund is a separate negative invoice `<number>-R`; the Z-report nets it out. |

Logs: `%LOCALAPPDATA%\Kacher\logs`. Backups: `%LOCALAPPDATA%\Kacher\backups`.

## 9. Roadmap order (if the customer base asks)

1. Arabic UI + full RTL.
2. Purchasing & supplier invoices.
3. Multi-terminal sync (one local server or a shared network SQLite/LiteDB path).
4. Licence activation + automatic updates.
5. E-invoice / fiscal integration for the target market.
