# Third-Party Software Notices

BoardScout is MIT-licensed (see [LICENSE](LICENSE)). The release builds are self-contained, so
they also redistribute the components below. Each keeps its own license. Nothing here is
modified from the published version.

Last reviewed for BoardScout 1.6.0 (2026-09-27).

## Libraries compiled into BoardScout.exe

| Component | Version | License | Source |
| --- | --- | --- | --- |
| .NET runtime and Windows Forms | 10.0.12 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/winforms |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| BlackSharp.Core (LibreHardwareMonitorLib dependency) | 1.0.7 | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| DiskInfoToolkit (LibreHardwareMonitorLib dependency) | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD (LibreHardwareMonitorLib dependency) | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp (LibreHardwareMonitorLib dependency) | 2.6.4 | Apache-2.0 | https://software.seekye.com/hidsharp |
| Microsoft.Web.WebView2 (SDK and loader) | 1.0.4191.47 | Microsoft BSD-style license (below) | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| System.Management | 10.0.12 | MIT | https://github.com/dotnet/runtime |
| System.IO.Ports (LibreHardwareMonitorLib dependency) | 10.0.3 | MIT | https://github.com/dotnet/runtime |
| Mono.Posix.NETStandard (LibreHardwareMonitorLib dependency) | 1.0.0 | MIT | https://www.nuget.org/packages/Mono.Posix.NETStandard |

**MPL-2.0 components.** The Source Code Form of each MPL-2.0 component is available at the
repository linked above, under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/).
BoardScout uses the unmodified NuGet packages.

**HidSharp.** Copyright 2010-2025 James F. Bellinger. Licensed under the Apache License,
Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0). Distributed on an "AS IS" basis,
without warranties or conditions of any kind.

## Web libraries bundled in `Assets/vendor`

These run inside the Topology, Connections, and System views. They are loaded from disk, not from
a CDN, so the views work offline. The files are byte-identical to the npm packages; the tarball SHA-1
matched the registry's published `shasum` when they were added.

| File | Package | License | SHA-256 |
| --- | --- | --- | --- |
| `vendor/quick-liquid/index.mjs` | quick-liquid 0.1.2 | MIT | `0eb68672ed9ab3ab089e1387a043592638752968741fc0f1a40b29f9156ca73a` |
| `vendor/quick-liquid/chunk-4TLGP4GF.mjs` | quick-liquid 0.1.2 | MIT | `b0258bfac49133cf48eb6c11275a383e361cdd6c4af803739701f0c88f007e0b` |
| `vendor/d3/d3.v7.min.js` | d3 7.9.0 | ISC | `f2094bbf6141b359722c4fe454eb6c4b0f0e42cc10cc7af921fc158fceb86539` |

### QuickLiquid 0.1.2

Source: https://github.com/amarnath3003/quickLiquid

```
MIT License

Copyright (c) 2026 Amarnath

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### D3.js 7.9.0

Source: https://github.com/d3/d3

```
Copyright 2010-2023 Mike Bostock

Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright notice
and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
THIS SOFTWARE.
```

## Microsoft WebView2 SDK license

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The WebView2 package also ships a `NOTICE.txt` for components Microsoft incorporates; Microsoft
publishes the corresponding source at https://3rdpartysource.microsoft.com.

## Bundled DriverScout engine

The PowerShell scanner in `DriverScout/` is part of this project (MIT). Its own notices, including
the PCI and USB ID databases, are in `src/BoardScout.App/DriverScout/THIRD-PARTY-NOTICES.md`. The
Connections view also reads that same bundled `usb.ids` file (the USB ID Repository, 3-clause BSD)
to name USB devices, and `oui.tsv` — the IEEE Registration Authority's public MA-L listing reduced
to prefix and organization name — to label network devices by maker (details in the DriverScout
notices).

## Online services used on request

The Connections view's **Look up my public IP** button requests `https://1.1.1.1/cdn-cgi/trace`,
and its **Test my Internet speed** button downloads from and uploads to `https://speed.cloudflare.com`
(the endpoints Cloudflare's public speed test page uses), both from Cloudflare, Inc. and only when
clicked. No Cloudflare code or data is bundled; Cloudflare's own terms and privacy policy govern
those requests.

## Not bundled

- **PawnIO** (namazso, https://pawnio.eu/) is the kernel driver LibreHardwareMonitorLib 0.9.6 uses
  for CPU, VRM, and motherboard fan sensors. BoardScout does not ship or install it; the version
  pop-out links to its official site.
- **Microsoft Edge WebView2 Runtime** is part of Windows 10/11 or installed by Microsoft's
  Evergreen installer; BoardScout does not ship it.

---

# Trademark Notices

All product names, logos, and brands mentioned in this software are the property of their
respective owners and are used for identification only.

- **AMD**, **Ryzen**, **Radeon**, and **AMD B550** are trademarks of Advanced Micro Devices, Inc.
- **Intel** and **Intel Core** are trademarks of Intel Corporation.
- **NVIDIA**, **GeForce**, and **RTX** are trademarks of NVIDIA Corporation.
- **ASRock** is a trademark of ASRock Inc.
- **Microsoft**, **Windows**, **Windows 11**, **WebView2**, and **Xbox** are trademarks of Microsoft Corporation.
- **Crucial**, **Ballistix**, and **Micron** are trademarks of Micron Technology, Inc.
- **G.SKILL**, **Kingston**, and **Corsair** belong to G.SKILL International Enterprise Co., Ltd.,
  Kingston Technology Corporation, and Corsair Memory, Inc.; BoardScout reads their memory part
  numbers only to find a kit's rated speed.
- **XMP** (Intel), **EXPO** (AMD), and **DOCP** (ASUSTeK Computer Inc.) are memory-profile names of
  their respective owners.
- **Samsung** is a trademark of Samsung Electronics Co., Ltd.
- **Western Digital**, **WD**, **WD Blue**, **My Passport**, and **My Book** are trademarks of Western Digital Corporation.
- **Lexar** is a trademark of Longsys Electronics.
- **Noctua** is a trademark of Rascom Computerdistribution GmbH.
- **Cloudflare** is a trademark of Cloudflare, Inc.
- **Logitech** and **Logi Bolt** are trademarks of Logitech International S.A.
- **Elgato** is a trademark of Corsair Memory, Inc.
- **Dell** is a trademark of Dell Inc.
- **Realtek** is a trademark of Realtek Semiconductor Corp.
- **Bluetooth** is a trademark of Bluetooth SIG, Inc.
- **Pi-hole** is a trademark of Pi-hole LLC.
- **Deco** is a trademark of TP-Link Systems Inc. Vizio, Ring, Raspberry Pi, Espressif, and the other
  makers named from the IEEE registry are the names or trademarks of their respective owners.
- **Wi-Fi** and **Wi-Fi Protected Setup (WPS)** are trademarks of the Wi-Fi Alliance.
- **Amazon** and **Newegg** are trademarks of their respective owners; the upgrade report links to
  their public search pages without affiliation.

Use of these trademarks does not imply endorsement by the trademark holder. BoardScout is an
independent project and is not affiliated with, endorsed by, or sponsored by any company above.

BoardScout reads hardware identifiers (vendor IDs, product names, model numbers) that the
operating system and firmware report, only to show hardware inventory and diagnostics to the
user. No proprietary firmware, drivers, or copyrighted vendor materials are bundled with or
distributed by this software.
