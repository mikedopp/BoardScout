# data/ — vendored hardware-ID databases

Committed snapshots of the maintained successors to the old **pcidatabase.com**:

| File | What | Source | Snapshot |
|------|------|--------|----------|
| `pci.ids` | PCI vendor/device/subsystem IDs (`VEN`/`DEV`) | [pci-ids.ucw.cz](https://pci-ids.ucw.cz/) | 2026.06.09 |
| `usb.ids` | USB vendor/product IDs (`VID`/`PID`) | [linux-usb.org](http://www.linux-usb.org/usb.ids) | 2025.12.13 |

These let DriverScout resolve every device's hardware ID to a human-readable
vendor + product name **fully offline**. Licensing and attribution: see
[`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) (both files are
GPLv2+/3-clause-BSD dual-licensed).

**Runtime precedence:** the tool prefers a fresh download in `../cache/`
(auto-refreshed every 30 days) and falls back to this committed snapshot when
offline or when upstream is unreachable.

**To refresh this snapshot:**

```powershell
.\Get-DriverRundown.ps1 -RefreshDb        # downloads latest into cache\
Copy-Item cache\pci.ids, cache\usb.ids data\ -Force
```
