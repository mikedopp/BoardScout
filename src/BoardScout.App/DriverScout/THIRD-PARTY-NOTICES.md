# Third-Party Notices

DriverScout itself is licensed under the MIT License (see `LICENSE`). It also
**redistributes** the following third-party data files, which remain under their
own licenses. These notices satisfy the attribution requirement of those
licenses.

---

## pci.ids — The PCI ID Repository

- **File:** `data/pci.ids`
- **Source:** https://pci-ids.ucw.cz/ (GitHub mirror: https://github.com/pciutils/pciids)
- **Maintained by:** Albert Pool, Martin Mareš, and volunteers of the PCI ID Project
- **Snapshot version:** 2026.06.09
- **License:** Distributable under **either** the GNU General Public License
  (version 2 or later) **or** the 3-clause BSD License. DriverScout redistributes
  it under the 3-clause BSD License.

> The database is a compilation of factual data; the copyright covers only the
> aggregation and formatting, and is held by Martin Mareš and Albert Pool.

The original license/copyright header is preserved verbatim at the top of
`data/pci.ids`.

---

## usb.ids — The USB ID Repository

- **File:** `data/usb.ids`
- **Source:** http://www.linux-usb.org/usb.ids (also https://usb-ids.gowdy.us/)
- **Maintained by:** Stephen J. Gowdy <linux.usb.ids@gmail.com>
- **Snapshot version:** 2025.12.13
- **License:** Distributable under **either** the GNU General Public License
  (version 2 or later) **or** the 3-clause BSD License. DriverScout redistributes
  it under the 3-clause BSD License.

The original maintainer header is preserved verbatim at the top of `data/usb.ids`.

---

### Why these files are vendored

The pcidatabase.com site that historically tracked PCI vendor/device IDs is
defunct. The PCI ID Repository and USB ID Repository are its actively-maintained
successors. DriverScout keeps a committed snapshot in `data/` so hardware-ID
resolution works fully offline and is reproducible even if upstream is
unreachable. At runtime the tool prefers a fresh copy in `cache/` (auto-updated)
and falls back to the vendored `data/` snapshot. To refresh the snapshot, run
`Get-DriverRundown.ps1 -RefreshDb` and copy the updated files from `cache/` into
`data/`.
