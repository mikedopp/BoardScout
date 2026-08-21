# BoardScout

BoardScout is a portable native C# desktop application for Windows hardware,
motherboard, storage, and driver health. It bundles the DriverScout engine and
does not require an installer, administrator rights, or a machine-wide .NET
runtime.

## Run

Double-click **BoardScout.cmd**. The launcher uses the matching portable build
for x64 or ARM64 Windows and builds it first when necessary.

You can also run the published executable directly:

    .\build\portable\win-x64\BoardScout.exe

## Efficient workflow

BoardScout deliberately separates work by cost:

1. **Startup uses the cached scan** and opens immediately.
2. **Scan Hardware** performs local inventory only. Run it after hardware or
   firmware changes, not every time the app starts.
3. **Check Drivers** is a separate, cancellable online operation. It uses
   DriverScout resolvers for vendor, OEM, and Microsoft catalog comparisons.
4. BoardScout presents links for review and **never installs or flashes
   anything automatically**.

The Efficiency screen looks for:

- memory running below its advertised XMP/DOCP/EXPO rate;
- critically full or nearly full volumes;
- Device Manager error codes;
- older BIOS firmware that merits a manual review;
- old driver dates, excluding Windows inbox disk.inf dates;
- update results verified by DriverScout sources.

## Portable build

Build the self-contained application and distribution zip:

    .\Build-Portable.ps1 -Runtime win-x64

Outputs:

- build\portable\win-x64\BoardScout.exe
- build\BoardScout-0.2.0-win-x64.zip

The portable folder includes the bundled DriverScout scripts and offline PCI/USB
ID databases. Zip that folder, move it to another Windows 10/11 PC, extract it,
and run **BoardScout.exe**.

If the application folder is writable, scans and reports stay in its **Data**
directory. If it is read-only, BoardScout falls back to
%LOCALAPPDATA%\BoardScout. Set BOARDSCOUT_DATA to override this location.

## Source layout

- src\BoardScout.App — .NET 8 WinForms application
- src\BoardScout.App\DriverScout — bundled DriverScout engine and notices
- src\BoardScout.App\Services — scanning, driver checks, caching, suggestions
- src\BoardScout.App\UI — native motherboard view and application screens
- Build-Portable.ps1 — self-contained publisher and zip packager

DriverScout is bundled under its own MIT license. See
src\BoardScout.App\DriverScout\LICENSE and THIRD-PARTY-NOTICES.md.
