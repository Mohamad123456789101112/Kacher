# ESC/POS hardware engine

Everything the terminal sends to a printer or drawer is assembled as bytes in
`Services/Hardware/EscPos/EscPosBuilder.cs` and shipped by a transport in
`Services/Hardware/Transports/`. No Windows print driver renders anything.

---

## 1. Why bypass the driver

| Path | Typical latency | Failure modes |
| --- | --- | --- |
| GDI + vendor driver (XPS → spooler) | 800–1500 ms | driver crashes, colour management, font substitution, spooler queue stalls |
| Spooler **RAW** (`WritePrinter`) | 250–500 ms | queue misconfiguration |
| TCP 9100 / USB bulk | **120–250 ms** | network/firmware |

The receipt is a fixed 2–6 KB command stream, so the fastest and most predictable approach is
"build bytes, write bytes". It also makes receipts deterministic: the same inputs always produce
identical paper.

---

## 2. Command vocabulary

Prefixes: `ESC = 0x1B`, `GS = 0x1D`, `FS = 0x1C`, `DLE = 0x10`.

| Purpose | Sequence | Notes |
| --- | --- | --- |
| Reset | `ESC @` | Always the first bytes of a job |
| Code page | `ESC t n` | 16 = WPC1252, 22 = CP864, 25 = WPC1256 |
| Align | `ESC a n` | 0 left, 1 centre, 2 right |
| Bold | `ESC E n` | |
| Underline | `ESC - n` | |
| Reverse video | `GS B n` | used for highlight bars |
| Character size | `GS ! n` | high nibble = width−1, low nibble = height−1 |
| Line spacing | `ESC 3 n` | dots |
| Feed | `ESC d n` | n lines |
| Cut | `GS V 66 n` | partial cut with feed |
| **Drawer kick** | `ESC p m t1 t2` | m = pin (0/1), t1 = on ×2 ms, t2 = off ×2 ms |
| Barcode | `GS h n`, `GS w n`, `GS H n`, `GS k m n d…` | Code128 = m 73 |
| QR code | `GS ( k` family | see below |
| Raster image | `GS v 0 m xL xH yL yH d…` | 1 bpp, MSB first |

### Drawer kick — `ESC p 0 25 250`

```
1B 70 00 19 FA
 │  │  │  │  └─ t2 = 250 → 500 ms OFF
 │  │  │  └──── t1 = 25  → 50 ms ON
 │  │  └─────── m  = 0   → drawer pin 2
 │  └────────── p
 └───────────── ESC
```

Epson recommends 50 ms on / 500 ms off. Shorter pulses may not trip the solenoid; longer pulses can
fire the drawer twice or overheat the coil. `DrawerCommandParser` accepts:

* mnemonic — `ESC p 0 25 250`
* prefixed — `0x1B,0x70,0x00,0x19,0xFA`
* hex blob — `1B 70 00 19 FA` or `1B700019FA`

Pin 5 (`ESC p 1 25 250`) is used by the second drawer on dual-drawer tills.

### QR code — `GS ( k`

```
1D 28 6B 04 00 31 41 32 00        select model 2
1D 28 6B 03 00 31 43 n           module size (1..16)
1D 28 6B 03 00 31 45 n           error correction: 48=L 49=M 50=Q 51=H
1D 28 6B pL pH 31 50 30 <data>   store data; pL/pH = data.Length + 3
1D 28 6B 03 00 31 51 30          print stored symbol
```

`pL/pH` is a little-endian 16-bit length of **everything after the length field**, which is why the
`31 50 30` selector bytes are included in the count (`+3`).

---

## 3. Receipt layout (80 mm / 48 columns)

```
                KACHER MARKET            ← logo raster + double-height
        12 Corniche Road, Alexandria
              Tel: +20 3 555 0100
          Tax Reg: EG-512-884-003
================================================
Invoice                              INV-20261022-0007
Date                                      22/10/2026 14:32
Cashier                                      Yara Hassan
Shift                                    S-20261022-0915
------------------------------------------------
ITEM                       QTY      AMOUNT
 Coca-Cola 330ml
                     2 x 15.00        34.20
 Baladi Bread Pack
                     1 x 10.00        10.00
------------------------------------------------
Subtotal                                   39.00 EGP
Tax 14%                                     4.20 EGP
TOTAL                                      44.20 EGP   ← double height + bold
================================================
Paid by                                          CASH
Tendered                                   50.00 EGP
CHANGE                                      5.80 EGP
                  [QR]  scan for digital receipt
           [||||||| INV-20261022-0007 |||||||]
------------------------------------------------
Thank you for shopping with us!
```

58 mm paper uses 32 columns: long names wrap, the money column narrows, and the QR module size drops
from 6 to 4 so the symbol still fits.

---

## 4. Transports

| `PrinterConnectionType` | Class | When to use |
| --- | --- | --- |
| `WindowsDriver` | `WindowsRawPrinterTransport` | Vendor driver already installed; `pDatatype = "RAW"` bypasses its rendering |
| `Network` | `NetworkEscPosTransport` | Ethernet/Wi-Fi printers, TCP 9100, 3 s connect timeout, one retry |
| `Usb` | `UsbEscPosTransport` | Device bound to `usbprint.sys`; writes to the bulk-OUT pipe |
| `Serial` | `SerialEscPosTransport` | Legacy RS-232 / USB-serial printers, 9600–115200 baud |
| *(diagnostics)* | `FileSpoolTransport` | Writes the exact bytes to `%LOCALAPPDATA%\Kacher\spool\*.bin` |

**USB note.** `CreateFile` on the printer-class interface path
(`\\?\usb#vid_04b8&pid_0e15#…`) only works while Windows binds `usbprint.sys`. If a vendor driver has
claimed the device, switch the profile to `WindowsDriver` — it still sends RAW bytes, it just goes
through the queue.

**Serial note.** `SerialPort` has no true async I/O on Windows, so the write is dispatched to the
thread pool. The UI thread is never blocked.

---

## 5. Job orchestration

`EpsonEscPosPrinterService` serialises jobs with a `SemaphoreSlim`. Thermal printers have a ~4 KB
input buffer; interleaving two jobs corrupts both. The flow is:

1. Build the payload (pure, no I/O).
2. Send with a single `WriteAsync` — one buffer, one syscall.
3. Retry once, only for `Network` transports (Wi-Fi roam / DHCP are the realistic transient failures).
4. Pulse the drawer as a **separate** short job on cash sales, so a receipt retry never delays the kick.

Every job is timed and logged with its byte count.

---

## 6. Troubleshooting

| Symptom | Check |
| --- | --- |
| Garbage characters | Code page mismatch — set `ESC t` index to match the printer's ROM (try 16, 22, 25) |
| Receipt clipped on the right | Wrong paper width — 58 mm must be 32 columns |
| Drawer does not open | Pulse pin (0 vs 1), drawer cable in the RJ-11 **kick** port (not drawer port), 24 V supply |
| Prints but no cut | `AutoCut` disabled, or cutter blocked — run the test page |
| Network printer unreachable | Ping the host, confirm port 9100, disable Wi-Fi isolation on the AP |
| Arabic prints as `?` | Select a code page with Arabic glyphs (22/864 or 25/1256) |
| Logo prints as noise | Image must be 1 bpp; `LogoRasterizer` thresholds at 160 grey and scales to 384 dots |
| Nothing at all | Use **Probe connection**, then check `logs/pos-*.log` for the transport error |
