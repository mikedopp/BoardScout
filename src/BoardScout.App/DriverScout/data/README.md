# data/ — vendored hardware-ID databases

Committed snapshots of the maintained successors to the old **pcidatabase.com**:

| File | What | Source | Snapshot |
|------|------|--------|----------|
| `pci.ids` | PCI vendor/device/subsystem IDs (`VEN`/`DEV`) | [pci-ids.ucw.cz](https://pci-ids.ucw.cz/) | 2026.06.09 |
| `usb.ids` | USB vendor/product IDs (`VID`/`PID`) | [linux-usb.org](http://www.linux-usb.org/usb.ids) | 2025.12.13 |
| `oui.tsv` | Network card maker by MAC prefix (OUI → organization) | [IEEE Registration Authority MA-L listing](https://standards-oui.ieee.org/oui/oui.csv) | 2026-09-27 |

These let DriverScout resolve every device's hardware ID to a human-readable
vendor + product name **fully offline**. Licensing and attribution: see
[`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) (`pci.ids` and
`usb.ids` are GPLv2+/3-clause-BSD dual-licensed). `oui.tsv` is BoardScout's
Connections view's MAC-prefix lookup: the IEEE public MA-L listing reduced to
prefix and organization name (addresses dropped), so devices on your network
can be labeled by maker without going online.

**To refresh `oui.tsv`:** download the CSV above and keep, for each `MA-L` row,
the six-hex-digit assignment and the organization name, tab-separated and sorted.

**Runtime precedence:** the tool prefers a fresh download in `../cache/`
(auto-refreshed every 30 days) and falls back to this committed snapshot when
offline or when upstream is unreachable.

**To refresh this snapshot:**

```powershell
.\Get-DriverRundown.ps1 -RefreshDb        # downloads latest into cache\
Copy-Item cache\pci.ids, cache\usb.ids data\ -Force
```
