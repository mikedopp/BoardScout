# Changelog

## 1.9.0 — 2026-10-08

Search, Eject, and drives that don't say what they are.

### Added
- **Search** on the Connections map (Ctrl+F or `/`): finds cards by name, kind (HDD, SSD, NVMe), or any
  detail value (USB ID, firmware, link speed, disk number). Matches stay lit on the map while the rest dim;
  arrow keys move through the list, Enter jumps to one (closing the list and focusing its card), Shift+Enter
  shows them all, Esc clears. A map refresh keeps the search, so a drive plugged in while you search for
  it lights up.
- **Eject** on every external USB drive, in its details and on its Live drives card: the Safely Remove
  request the taskbar sends. One click arms it, a second sends it. The card says "safe to unplug" only when
  Windows confirms; then the button is gone, so a stopped drive can't be ejected again, and the map
  re-reads at once. A refusal says why (a file open, a named program or service, administrator rights) and
  keeps the button for a retry. A drive Windows no longer lists, or can't look up, reports that nothing
  was ejected. A stopped drive shows as "Stopped · safe to unplug" instead of a problem device.
- **USB port problems** as their own warning cards: a port where something is plugged in but failed to
  start, tripped over-current, didn't get enough power or bandwidth, or sits behind too many hubs. These
  come from the hub itself, so they show even when Device Manager has no entry.
- USB controller details explain that a device missing from the map never made a connection.
- Unit tests (`tests\BoardScout.Tests`): drive type from model numbers, eject result messages.

### Fixed
- **USB drives whose bridge doesn't report a drive type** (many ASMedia/JMicron enclosures) were drawn
  with the SSD picture. The model number now decides when it's a known hard-drive or SSD family, and the
  details say it came from the model number; a drive that's still unknown gets a plain outline, never the
  NAND picture.
- Opening a drive's details before the first live reading arrived threw an error and left the panel
  half drawn.
- A USB device's "Hub port" said "(USB 2 port)" for the USB 2 half of a USB 3 socket, which read like a
  USB 2-only socket. It now says which connection the device came in on, and when a USB 3 device made
  contact only through the USB 2 wires. That warning now points at the cable and plug when the port is
  already USB 3.
- `Build-Portable.ps1` deleted the whole output folder, including the `Data` folder (settings and scans)
  of a BoardScout run from `build\portable` or `build\standalone`. It now keeps `Data`, and never puts it in
  the zip.
- BoardScout's own disk reads (refresh, live counters, idle checks) now step aside for an eject instead of
  holding a handle that would make Windows refuse it; ejects run one at a time.

## 1.8.0 — 2026-10-05

FlashStream: SSDs and NVMe drives as light moving through their NAND dies.

### Changed
- **Live drives: FlashStream for SSD/NVMe** (shared with DedupApp 2.4). The flat board of blinking cells is replaced
  by a tilted grid of NAND dies with light streaks moving through it: reads (blue) leave a die, cross the
  controller and arc up to the PC; writes (amber) travel the other way and flash the die they land on. The number
  of streaks follows operations per second, their speed follows MB/s, their length follows the average I/O size
  (4 KB random reads are short sparks, large sequential transfers long ribbons), beads circling the controller
  show queued requests, and the controller glows with busy time. NVMe drives are drawn with 8 channels and SATA
  SSDs with 4. Which die a streak hits is random, since Windows doesn't report it. Hard drives keep the platter.
  The same picture appears in a drive's details on the map. It follows Motion and stops when the panel closes.
- Drives whose media type Windows doesn't report (some USB bridges) are tagged **Disk** instead of SSD.

### Added
- **Read and write history graph** on every Live drives card: reads in blue and writes in amber over the last 60
  live samples, on one shared scale, with the peak. A drive's details panel uses the same colors for disks.
- Per-disk **busy %** in the live samples, from the same zero-access disk counters (idle time over the
  interval). It drives the controller glow.
- The engine lives in `Assets/vendor/flashstream/flashstream.js`, built from the shared FlashStream source.

## 1.7.1 — 2026-10-04

- Skitter engine 2.1.0: the crawlers on the Connections map are now drawn as real spiders (abdomen and carapace,
  eight eyes, palps, three-part legs, each in its own color). Behavior on the map is unchanged; the engine's new
  reading, link-following and octopus modes aren't used in BoardScout.

## 1.7.0 — 2026-10-01

Skitter: spiders that crawl the Connections map and hunt bottlenecks.

### Added
- **Skitter crawlers** (Settings → *Skitter crawlers on the Connections map*: Off, 2, 4, 8; off by default).
  Procedural spiders with jointed legs walk over the map. Their feet plant on real device cards, summary pills,
  and buttons, and they ride along when the map scrolls.
- They hunt problems first: cards and pills for links running below what the device supports (and other
  warnings) get a pulsing red edge while a spider sits on them. Other cards they visit get a brief colored edge,
  and some get their name torn off and dragged away. Only the overlay draws this; the map itself is unchanged,
  and torn labels use the text already on screen, so privacy mode still applies.
- Move the pointer near a spider to shoo it. Spiders need Motion on and pause while the window is hidden.
- The engine (Assets/vendor/skitter/skitter.js) is shared with OpsConsole.

## 1.6.0 — 2026-09-27

Live drives: every drive animated from its real activity.

### Added
- **Live drives** (Connections → *Live drives*): a card per drive, fastest bus first. Hard drives draw as a
  platter that spins faster with throughput and an arm that seeks at the measured operation rate, then
  parks after a few idle seconds; SSDs and NVMe drives draw as flash cells lit by real reads (blue) and
  writes (amber). Dots carry reads and writes between the drive and the PC, and each card shows read and
  write speed, operations per second, queue, and temperature where Windows reports it.
- The same live picture in the details panel when you click any drive on the map.
- Ported from DedupApp's Live drives screen. It is driven by the disk counters BoardScout already samples
  for the map (no new requests to drives), follows the Motion setting, and stops when the panel closes or
  the tab is hidden; nine drives animating cost about 7% of one CPU core.

## 1.5.1 — 2026-09-27

Data safety: BoardScout now stays off phones and cameras entirely, and never questions a drive while it
is busy.

### Changed
- **Phones and cameras** (anything that copies files over MTP or PTP) are never sent BoardScout's one
  USB request, the power question; their cards say why.
- **Drives** are asked that question only when Windows' own read and write counters show them idle; a
  busy drive is skipped and asked on a later look, so a request never lands in the middle of a copy.
- **Drive temperature** is asked only of internal drives (SATA, NVMe, and similar), not USB drives or
  card readers.
- The plan points out a phone plugged in through a hub: Windows doesn't double-check files copied from
  a phone, so big copies are safest on a direct port with a good cable.
- The README has a **Data safety** section listing exactly what BoardScout reads from drives and
  devices, and what it never does.

### Notes
- A phone backup that came out damaged while BoardScout was running was checked byte by byte: the
  damage sat inside two USB 2 packets of the phone's own file transfer, a link the running version
  never touched. This release makes that boundary explicit.

## 1.5.0 — 2026-09-27

An optimization plan on the Connections map: where to plug things in, how to make every drive faster on
the port or slot it uses, whether memory sits in the right slots, what conflicts or takes the most
interrupt time, and what draws the most power.

### Added
- **Optimization plan** (Connections → *Optimization plan*), built from what the map just read. Every
  item says what to do and has a *Show on the map* link that highlights the cards it is about.
  - **USB ports:** each USB controller (the CPU's own or the chipset's), its USB 3 and USB 2-only
    ports, how many are free, and what is plugged into each (hubs list what hangs off them). USB 3
    devices stuck at USB 2 speed are called out with what limits them (on the test PC, a phone behind
    a USB 2 hub), and drives that share one hub link are noted.
  - **USB drives:** which already run at full speed (5 or 10 Gbps), which could run faster on a
    10 Gbps port or in a UAS enclosure, and write caching: *Better performance* for drives that stay
    on the desk (their own power supply, or behind a hub) and *Quick removal* for drives you carry.
  - **Inside the PC:** NVMe drives given fewer lanes than they support (on the test PC, the SN570 in
    the B550M Steel Legend's x2 M2_2 socket, with the x4 PCIE3 slot as the fix), whether the fastest
    drive has the CPU's own M.2 socket, the graphics card's link (a card wired for 8 lanes on a Ryzen
    5000G's PCIe 3.0 is expected, not a fault), everything sharing the chipset's uplink, and TRIM.
  - **Memory:** slots from the firmware's own table (SMBIOS): channels in use, speed against the
    modules' rating (XMP/DOCP/EXPO), mismatched kits, free slots, and on two-channel desktop boards
    whether two sticks sit in the slots boards recommend. On the test PC it found both sticks in
    A1/B1, where ASRock's manual recommends A2/B2.
  - **Conflicts and interrupts:** devices Windows reports a problem with (a resource conflict is code
    12), legacy interrupt lines shared by several devices, which devices use message-signaled
    interrupts, and interrupts and DPCs per second on every logical processor.
  - **Interrupt time per driver** (administrator): a 10-second Windows kernel trace of every
    interrupt and DPC, summarized by driver with its devices, its share of processor time, and its
    longest single run; runs over 1 ms, the kind that crackle audio, are flagged. Without admin, the
    button restarts BoardScout as administrator (Windows asks first), reopens the plan, and measures.
  - **Power:** what each USB device asks its port for, hubs without a power supply and what they
    carry, devices Windows has put to sleep, the graphics card's live power draw (and the CPU's, with
    admin and PawnIO), and battery state on laptops.
  - **Compatibility:** what kind of PC BoardScout sees (desktop, laptop, server, or virtual machine,
    from the firmware's chassis type) and what it could read on it.
- **Memory on the map:** a Memory band under the CPU with every slot, filled or empty, and each
  stick's size, speed, and part number.
- More card details: the hub port a USB device uses and whether that port is USB 3, how much current
  it asks for and whether it has its own power supply, whether Windows has put it to sleep, drive type
  (SSD or hard drive) and write caching for every drive, and each PCI device's interrupts.
- A **0.5 s** live refresh option (version button → Settings).
- `--plan-json` prints the plan (`--privacy` masks it; `--profile` adds the driver measurement when
  run as administrator).

### Changed
- Plugging in or removing a device redraws the map in about 0.7 s instead of 1.7 s: a first look
  0.4 s after Windows reports the change, and one more once changes have stopped for 2 s, since a
  drive's disk arrives a moment after its USB device.
- USB devices are asked for their power needs once per session, never on a timer.
- The map's side panels start below the header however tall it grows, and scrollbars are dark.

### Fixed
- The Connections header (summary, legend, Refresh) stayed on screen only for the first screenful of
  scrolling; it now stays however far down the map you go.

### Notes
- The plan is built locally. The interrupt measurement keeps its trace in memory and saves nothing.
- Windows can't say where a USB port is on the case, and many boards don't describe which USB 2 and
  USB 3 ports share a connector; BoardScout pairs them in port order, as common controllers do.

## 1.4.0 — 2026-09-27

Real names and real speeds on the Connections map: what your router, network, and the devices on it
are called, packets and disk operations per second, and an Internet speed test.

### Added
- **Your router's make and model.** Many routers put WPS device info in their Wi-Fi beacon; BoardScout
  reads it from the Wi-Fi card's scan list, even when this PC is on Ethernet (on the test PC:
  "TP-Link Deco BE63"). Without Wi-Fi it falls back to the name in the router's web certificate
  ("tplinkdeco.net" → TP-Link Deco), then to the maker of its network card.
- **Mesh units.** Every access point broadcasting your network is counted, each Deco-style unit is
  matched to its address on your network, and the map says which one is closest to this PC and how
  strong each signal is.
- **Windows' name for your network** ("Network 5", or the Wi-Fi name) and whether Windows treats it
  as Public or Private, on each adapter.
- **Devices on your network**, named where they say who they are (multicast DNS, NetBIOS, UPnP, your
  DNS server) and labeled by maker from their MAC address using the bundled IEEE registry: TVs,
  consoles, cameras, smart plugs, computers, servers, Raspberry Pis, and mesh units. **Find more
  devices** pings every address on your local network (on request) so quiet devices show up too.
- **Packets per second** on every network card, plus packets, discards, and errors since the adapter
  connected; network speeds now read in Mbps like link speeds and Internet plans.
- **IOPS and queue depth** for every drive (reads and writes per second, requests waiting).
- **Test my Internet speed** on the Internet card: latency, jitter, download, and upload against
  Cloudflare's public speed test, with a live readout while it runs and a note on whether your
  Internet or your link to the router is the limit. It runs only when clicked, stops after a few
  seconds or a data cap, and the Scan Log records it.
- The map opens at once and fills in names a few seconds later; toggling privacy mode no longer
  re-reads the hardware.
- `--connections-json --sweep` includes the device search.

### Privacy
- Privacy mode also hides device names, the router's own name for itself, and the Wi-Fi names it
  broadcasts; devices are shown by type and maker instead ("Vizio TV or streamer").

### Notes
- Only the Internet speed test and the public IP lookup leave your network. Naming uses standard
  local discovery (multicast DNS, NetBIOS, UPnP) and a TLS handshake with your router; nothing logs
  in to anything.

## 1.3.0 — 2026-09-27

A Connections map: every device in the PC, where it plugs in, the speed its link negotiated, and the
data moving through it, out through the router to the Internet.

### Added
- **Connections tab**, read live from Windows' device tree without admin rights: the CPU, the
  chipset and its shared uplink, each USB controller, hub, and port, SATA and NVMe drives, monitors,
  Bluetooth devices, and network adapters, each with the link it negotiated (PCIe generation and
  lanes, USB speed, HDMI or DisplayPort mode, Ethernet or Wi-Fi rate).
  - Links running below what the device supports are flagged with the likely reason. On the test
    PC it found a GeForce RTX 3050 at PCIe 3.0 x8 (the card supports 4.0 x16), a Crucial P3 Plus at
    PCIe 3.0 x4 (Ryzen 5000G APUs run their lanes at 3.0), and a WD SN570 given 2 of its 4 lanes.
  - USB 3 devices running at USB 2 speed are flagged. The USB 2 half of a USB 3 hub is labeled as
    such instead of being flagged.
  - Live data movement: dots flow along the real paths as disks read and write and as network
    traffic moves from the Internet through the router, the adapter, and the chipset to the CPU.
    Cards show live rates; the CPU, GPU, and chipset cards show temperatures when the sensors are
    readable.
  - Your network: the router's address, name, MAC, and round trip; DNS servers by name (Pi-hole is
    recognized); Wi-Fi network, signal, band, standard, link rate, and security; and whether
    Windows' own connectivity check sees Internet access.
  - **Look up my public IP** on the Internet card asks Cloudflare, only when clicked, and the Scan
    Log records that it did.
  - A details panel (QuickLiquid glass) with the link now versus its best, live traffic over the
    last minute, firmware and driver versions, capacity, and drive temperature where Windows
    reports it without admin rights.
  - The map redraws itself when a device is plugged in or removed, or the network changes.
  - Privacy mode hides MAC addresses, Wi-Fi network names and access points, IPv6 addresses,
    Bluetooth names that look like someone's ("Sam's AirPods"), and the public IP.
  - `BoardScout.exe --connections-json` prints the map's data; add `--privacy` to mask it.

### Fixed
- **The NET tile counted network traffic up to four times.** Windows lists packet-filter layers
  (WFP, QoS Packet Scheduler) as extra adapters that repeat the real adapter's byte counters, and
  BoardScout added them all. On the test PC one Ethernet adapter was counted four times. Only
  adapters with an IP address are counted now.
- **Topology kept animating after you left it.** A WebView2 on a hidden tab never learned it was
  hidden, so after one visit to Topology its animation ran on every other tab. Measured on 1.2.0:
  about 1.26 CPU cores (mostly the WebView2 GPU process) with the Overview tab showing. Web views
  on hidden tabs, and all of them while the window is minimized, are now paused.

### Changed
- Sidebar items shrink slightly on shorter windows so all eight fit.
- The version pop-out and README list Connections among the views that need the WebView2 Runtime.

## 1.2.0 — 2026-09-27

Privacy mode, so screenshots and exports can be shared without giving away who or where you are.

### Fixed
- **Exporting a scan as JSON shared the Windows owner's email and product ID.** Export copied the
  raw scan file, which records the Windows registered owner (often an email address) and the
  Windows product ID. JSON exports now always leave both out; nothing in a hardware report needs them.
- **Copy diagnostics could include your user folder path.** Diagnostics are meant for public issue
  reports, so they now always replace your PC name, user folder, serial numbers, email addresses,
  and MAC addresses with placeholders.

### Added
- **Privacy mode** (version button → Settings, or Ctrl+Shift+P). While it is on:
  - the header shows a purple **Privacy on** chip, so a screenshot shows it was taken masked;
    clicking the chip turns privacy mode off;
  - the System tab hides the installed-software, patch, and scheduled-task lists and shows their
    counts only;
  - the Scan Log shows `THIS-PC` for the PC name and `%USERPROFILE%` for your user folder;
  - the Storage tab shows `Volume D:` instead of volume labels you named yourself;
  - spec sheet exports say "This PC" and omit the PC name, machine ID, serial numbers, system UUID,
    and volume labels from the embedded scan data; JSON exports omit the same and stay importable.
- Scans on disk are unchanged; privacy mode only changes what is shown and exported.

### Changed
- The Topology view now receives hardware details only (no PC name, owner details, or serials).
- The Storage tab shows `Volume <letter>` when a volume has no disk model or label (it showed a blank).

## 1.1.0 — 2026-09-26

.NET 10, the QuickLiquid glass look, a UI that no longer freezes, and a System tab that tells the
truth about Windows support.

### Fixed
- **The window froze for about 5 seconds after every start.** The System tab asked WMI for the TPM
  on the UI thread; without admin rights that query times out after ~5 s. TPM is now read through
  TPM Base Services (~10 ms, no admin), and the whole System gather (~0.6 s) runs off the UI thread.
- **"TPM: Not detected" on PCs that have one.** Same cause: the WMI query fails without admin. This
  PC's TPM 2.0 now shows correctly.
- **The UI stuttered every second.** Sensor sampling (~80 ms) and the network adapter scan (~30 ms)
  ran on the UI thread each tick, and the sensor driver's first open (~0.6 s) blocked startup. All
  sampling now runs on a background thread. Measured on the release build: UI stalls in the first
  8 s went from 6 (~1.2 s total, recurring) to 2 (~0.2 s, only in the first 0.6 s).
- **Patches listed out of order.** Install dates were sorted as text, so 9/9/2026 came before
  12/1/2025. Dates are parsed and sorted as dates (ISO format in the table).
- **The ".NET SDKs" trait never appeared.** SDKs were counted from `dotnet --list-runtimes`, which
  never lists SDKs. Runtimes and SDKs are now read from the dotnet folders (x64 and x86).
- **Topology was blank without internet.** D3 loaded from d3js.org; it is now bundled.
- **The web views broke in read-only folders.** WebView2 kept its profile beside the exe; it now
  lives in the BoardScout data folder.
- **Topology showed one board's details on every board.** "M2_2 disables SATA 5/6", PCIE2/PCIE3,
  and the Key-E note were hard-wired for the ASRock B550M Steel Legend. They now appear only for
  that board; other boards get generic labels plus the slots their firmware reports.
- **Topology bus labels overlapped** under the CPU. Labels now sit on each link near its device.
- **FANS showed an amber "Fans idle" warning** when only the GPU fan was readable (it stops at idle
  by design). It now says what is missing ("Needs PawnIO" / "Needs admin").
- **Stale version strings:** spec sheets said "BoardScout v0.8.0", downloads sent a v0.8.0
  User-Agent, and every build zip was named 0.9.0. All now come from the real version.
- **Upgrade report assumed one PC:** it called every GPU "a massive upgrade from RTX 3050" and
  listed an Intel AX210 as your Wi-Fi card. It now uses the detected GPU, NVMe drive, and Wi-Fi card,
  and notes when an APU limits the x16 slot to PCIe 3.0. Spec sheets no longer call DDR5 "DDR4".
- **The version pop-out ignored clicks elsewhere** (it only closed on another version click or
  when the window lost focus). Clicking anywhere else or pressing Escape now closes it.

### Added
- **QuickLiquid glass.** The System tab's personality card and the Topology legend use real SVG
  refraction from QuickLiquid 0.1.2 (bundled, works offline). The System tab's task filter has a
  liquid tab indicator.
- **Liquid native UI.** The sidebar selection glides between destinations and stretches like a
  droplet; buttons, header tiles, the inspector, and cards get a glass sheen; zoom eases instead of
  jumping; topology links show flowing traffic, faster on wider buses.
- **Windows support that matches Microsoft's lifecycle pages** (checked 2026-09-26): Windows 10
  shows Extended Security Updates coverage through October 12, 2027, with the days left; Windows 11
  shows the end date for its version and edition (Home/Pro or Enterprise/Education), including 25H2
  and 26H1.
- **Windows 11 readiness** for Windows 10 PCs: TPM 2.0, UEFI Secure Boot, processor generation,
  memory, and system drive, each with a plain-language result. It is a quick check, not Microsoft's.
- **Sensor troubleshooting.** LibreHardwareMonitorLib 0.9.6 reads CPU, VRM, and motherboard fan
  sensors through the PawnIO driver, which it does not ship. The version pop-out now says whether
  PawnIO is installed and whether BoardScout is elevated, links to pawnio.eu, and can restart
  BoardScout as administrator.
- **Settings** under the version button: glass effects, motion, live refresh (1/2/5 s), and minimize
  to tray. Stored in `Data\settings.json`.
- **Copy diagnostics** (version, runtime, Windows build, WebView2, sensors, settings) for issue reports.
- **Standalone exe.** Releases now include a single `BoardScout-<version>-win-x64.exe` with everything
  inside, next to the portable zip. Both keep scans in a `Data` folder beside the exe.
- `crash.log` in the data folder when something unexpected happens, and a `--system-json` switch that
  prints the System tab's data.

### Changed
- .NET 8 → **.NET 10** (runtime 10.0.12). WebView2 SDK 1.0.2903.40 → 1.0.4191.47, System.Management
  10.0.2 → 10.0.12. The exe is ~20 MB smaller (the unused WebView2 WPF assembly is no longer shipped).
- The cached scan loads off the UI thread with source-generated JSON.
- Dark scrollbars; rounded glass cards replace square 1 px borders.
- Removed dead code (an unused GDI+ topology control and unused models).

### Known limitations
- Without PawnIO and administrator rights only GPU temperature and GPU fans are readable.
- Memory use is ~290 MB (was ~260 MB), mostly the newer WebView2 runtime and the glass engine.
- The Windows 11 readiness check covers common Intel Core and AMD Ryzen names; others show
  "check PC Health Check".

### Dependencies
- Added (bundled web assets): QuickLiquid 0.1.2 (MIT), D3.js 7.9.0 (ISC, previously loaded from a CDN).
- THIRD-PARTY-NOTICES now also lists LibreHardwareMonitorLib's own dependencies that ship inside the
  exe: BlackSharp.Core, DiskInfoToolkit, RAMSPDToolkit-NDD (MPL-2.0), HidSharp (Apache-2.0),
  System.IO.Ports and Mono.Posix.NETStandard (MIT).

## 1.0.0 — 2026-08-24

### Added
- **System tab**: new sidebar destination with WebView2-rendered dashboard showing OS personality analysis ("What Your OS Says About You"), build summary, .NET versions, installed patches, installed software, and scheduled tasks
- **OS personality verdicts**: automatic profiling based on OS version, patch discipline, installed software, and task scheduler — assigns a personality title (The Power Developer, The Reliable Holdout, The Battle Station, etc.) with emoji, traits, patch health, and actionable recommendations
- **Software inventory**: enumerates installed programs from registry (HKLM + WOW6432 + HKCU) with name, version, publisher, install date, and estimated size
- **Patch inventory**: lists all installed hotfixes from WMI with KB ID, description, install date, and installer
- **Scheduled task inventory**: parses `schtasks` output with category filtering (All / User / Microsoft / Windows)
- **.NET runtime detection**: lists CLR version, all installed runtimes via `dotnet --list-runtimes`, and .NET Framework version from registry
- **TPM and Secure Boot detection**: reads TPM spec version from WMI and Secure Boot state from registry
- **Search and sort**: all System tab tables support live search filtering and column-click sorting

### Changed
- **Board map spacing**: redistributed M2_3 WiFi (reduced height, moved up), M2_2 NVMe (moved up closer to chipset), PCIE3 x4, internal headers, and board identity positions for cleaner vertical spacing in the lower section; updated all circuit trace endpoints to match
- Sidebar navigation now has 7 entries (System added before Scan Log)

## 0.9.0 — 2026-08-23

### Changed
- **PCB-style circuit traces**: 9 Manhattan-routed traces replace plain lines on the board map — color-coded by bus type (blue=CPU-direct, teal=uplink, purple=chipset, orange=SATA) with 3-pass glow rendering and speed labels (DDR4-3200, PCIe×16, etc.)
- **Dot pattern background**: replaces grid lines with subtle dot matrix for a cleaner PCB aesthetic
- **Anti-aliased rounded toolbar buttons**: custom-painted `RoundedButton` control with GDI+ anti-aliased rendering replaces jagged Region-clipped buttons; hover and press states from theme colors
- **Larger board map typography**: label/small/title font minimums bumped by 1pt for readability at all zoom levels
- **Larger corner radii**: component boxes, dashed outlines, and port boxes all use softer rounding (7→12, 5→8, 6→10)
- **BoardScout brand mark** in Pimp My Build report footer

### Fixed
- DDR4-3200 trace label no longer overlaps fan header area (rerouted above at y=65)

## 0.8.0 — 2026-08-22

### Added
- **Pimp My Build button**: green-accent header button opens the upgrade planner report directly in the browser with Amazon and Newegg search links for each recommended component
- **Minimize to tray**: minimizing the window sends BoardScout to the system tray with a version-labeled icon; double-click or right-click "Open" to restore; right-click "Exit" to close
- **Version button animation rewrite**: rotating `LinearGradientBrush` (blue→red→yellow→green sweep, ~8 second revolution) matching SnipDeck's GlowEffect approach
- **Feedback/report links**: "Report issue" link in status bar and version popout opens GitHub Issues

### Changed
- Header toolbar decluttered from 9 buttons to 6 with separator grouping: primary (Scan now + Pimp My Build), driver workflow (Check drivers + Download), data (Import + Export)
- "Data folder" and "Report issue" moved to status bar links
- Pimp My Build uses a distinctive green accent (`StyleFeatureButton`) to stand out from standard buttons

### Dependencies
- No new dependencies

## 0.7.0 — 2026-08-22

### Added
- **Version button**: animated Google chasing-colors button in the header showing the current version; click to see runtime info, dependencies, and requirements
- **Component ages**: System details panel now shows release date and age for CPU, motherboard, and GPU with color-coded severity (green < 3yr, amber 3-5yr, red 5yr+)
- **Upgrade planner export**: Export → Upgrade planner generates a self-contained HTML report showing component ages, best compatible upgrades for the detected motherboard/socket, estimated pricing, and a "pimped out" dream build summary
- **Legal files**: MIT LICENSE, THIRD-PARTY-NOTICES.md with dependency licenses and trademark attributions

### Dependencies
- No new dependencies

## 0.6.0 — 2026-08-22

### Added
- **Spec sheet HTML export**: Export → Spec sheet (.html) generates a self-contained shareable hardware summary with stats bar, component cards, storage table with color-coded usage, USB device chips, collapsible raw JSON, and print-friendly light theme
- Export dialog now defaults to spec sheet HTML (also still offers JSON)
- Spec sheet auto-opens in default browser after export
- **Real sensor readings via LibreHardwareMonitorLib**: CPU package temp, GPU temp, VRM temp, and per-fan RPMs from the Super I/O chip (Nuvoton NCT6779D on ASRock B550M) — replaces empty WMI MSAcpi/Win32_Fan queries
- Header bar TEMP now shows multi-zone: "CPU 52° · GPU 41° · VRM 38°"
- Header bar FANS shows named fans with RPM: "CPU Fan 1120 · Chassis Fan #1 780"

### Changed
- System.Management bumped from 8.0.0 to 10.0.2 (LibreHardwareMonitorLib dependency)

### Dependencies
- LibreHardwareMonitorLib 0.9.6 (MPL-2.0; originally listed here as MIT in error) — direct Super I/O, SMBus, GPU sensor access

## 0.5.0 — 2026-08-22

### Added
- D3.js v7 bandwidth topology (WebView2): hierarchical tree layout replaces GDI+ diagram, with bandwidth badges, color-coded nodes, and hover tooltips explaining each bus
- Live thermal monitoring via MSAcpi_ThermalZoneTemperature (root\WMI)
- Live fan speed reading via Win32_Fan
- Live network throughput (up/down) via NetworkInterface.GetIPStatistics()
- Live disk I/O via GetProcessIoCounters P/Invoke
- TEMP, FANS, NET live metrics in the header bar with color-coded thresholds

### Changed
- Color palette swapped to match the DriverScout HTML dashboard (deep navy base, blue/green/amber/red semantic colors)
- Scanner engine bumped to v1.0.0 (schema 2.0) with full board-layout collectors
- Build archive updated for 0.5.0

### Dependencies
- Microsoft.Web.WebView2 1.0.2903.40 (topology visualization)
- System.Management 8.0.0 (WMI thermal/fan queries)

### Verified
- Memory: XMP/DOCP rated-speed detection from DIMM part numbers
- PCIe topology: 36 devices mapped via Win32_PnPSignedDriver Location
- Expansion slots: Win32_SystemSlot enumeration (6 slots)
- Volumes: 8 drives with disk model/bus-type correlation
- Dead devices: ConfigManagerErrorCode scan
- Displays: WmiMonitorID EDID extraction
- USB devices: VID/PID parsing (22 devices)
- Form factor: micro-atx inferred from baseboard product name
- D3.js topology: renders real scan data with CPU-direct and chipset paths

## 0.4.0

- Interactive board map with zoom, pan, hover inspector
- Bandwidth topology diagram (CPU-direct vs chipset paths)
- Sidebar pill navigation (Overview, Topology, Drivers, Storage, Efficiency, Scan Log)
- Driver grid with official vendor update links
- Storage grid with usage percentage color coding
- Suggestion engine: memory XMP, low-space volumes, dead devices, BIOS age, stale drivers
- Live CPU and memory telemetry (1-second polling)
- Dark/light theme with DWM title bar integration
- Import/export scan JSON
- Headless CLI modes: --scan, --check-drivers
