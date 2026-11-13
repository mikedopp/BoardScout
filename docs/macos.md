# BoardScout for macOS (untested)

The Linux edition also runs on a Mac. It's the same program and the same browser dashboard. On a Mac it reads
the hardware with Apple's own tools instead of `/sys` and `/proc`.

**This build has not run on a Mac yet.** It was written from Apple's documented output formats and from sample
output that Mac owners have published, and every parser is checked against made-up output in that shape. Expect
rough edges until someone runs it on a real Mac. Issue reports with the Diagnostics text are welcome.

## Download and run

| File | For |
| --- | --- |
| `BoardScout-<version>-osx-arm64` | Macs with Apple silicon (M1 and later) |
| `BoardScout-<version>-osx-x64` | Intel Macs |
| `…-osx-arm64.tar.gz`, `…-osx-x64.tar.gz` | The portable folder: `boardscout` with its web views beside it |
| `BoardScout-<version>-macos-SHA256SUMS.txt` | Checksums |

In Terminal:

    chmod +x BoardScout-<version>-osx-arm64
    xattr -d com.apple.quarantine BoardScout-<version>-osx-arm64
    ./BoardScout-<version>-osx-arm64

The build carries an ad-hoc signature, which Apple silicon needs before it starts any program. It isn't signed
with an Apple Developer ID or notarized, so without the `xattr` line macOS refuses to open a program downloaded
from an unidentified developer. If macOS still says the program is damaged, sign it again on the Mac with
`codesign --force -s - <file>`.

BoardScout prints a link and opens it in your browser. Settings live in
`~/Library/Application Support/BoardScout`. `--install` adds nothing on a Mac: there is no app menu to add to.

## What it reads

| Area | From |
| --- | --- |
| Model, chip, boot ROM | `system_profiler SPHardwareDataType`, `sysctl hw machdep` |
| Memory | `system_profiler SPMemoryDataType`: unified memory on Apple silicon, each slot on Intel Macs |
| Drives | `diskutil list -plist` and `diskutil info -plist` (APFS containers are left out, since they sit on a drive already listed), firmware from `SPNVMeDataType`/`SPSerialATADataType` |
| Volumes | `df -kP` and `mount`: the startup disk, its Data volume, and drives under `/Volumes` |
| USB | `system_profiler SPUSBHostDataType` (newer macOS) or `SPUSBDataType` (older): only the ports something is plugged into |
| Screens | `system_profiler SPDisplaysDataType` |
| Network | `ifconfig -a`, `netstat -ibn` / `-rn`, `arp -an`, `scutil --dns`, `ipconfig getoption`, Wi-Fi from `SPAirPortDataType` |
| Bluetooth | `system_profiler SPBluetoothDataType` |
| Live load | Processor time from the Mach host, memory from `vm_stat`, disk traffic from `ioreg` (IOBlockStorageDriver statistics) |
| macOS version | `sw_vers` |
| What fills a drive | the storage scanner's Mac rules: `/Applications` and Homebrew are Apps, `~/Library/Caches` is Caches, `~/.Trash` is Trash, Xcode's `~/Library/Developer` is Developer |

## What it doesn't do on a Mac

- **No temperatures, fans or processor power.** macOS shares them only through private interfaces or
  `powermetrics`, which needs sudo.
- **No PCI devices.** Apple silicon has no PCI list a program can read, so Topology shows USB, drives, screens
  and the network without a PCIe tree.
- **No Wi-Fi network name** unless macOS gives location access (it has shown the name as `<redacted>` since
  macOS 14).
- **Folders macOS keeps private** (Mail, Messages, Safari, other apps' containers) count as unread space in
  What fills a drive unless Terminal has Full Disk Access. Reading the Desktop, Documents, Downloads or a plugged-in
  drive may ask for permission the first time.
- **Machines tab:** another Mac can't be opened over SSH yet.
- **Drivers, firmware updates and the optimization plan** are written for Linux and Windows. On a Mac they show
  little.
