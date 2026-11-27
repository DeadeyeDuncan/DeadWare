# Third-party notices

DeadWare's MIT license applies to original DeadWare code. Included dependencies retain their own licenses and notices. Release archives include package manifests, supplied license/notice files and a dependency inventory in `licenses/`.

## Hardware monitoring

- **LibreHardwareMonitorLib 0.9.6** — LibreHardwareMonitor contributors; Mozilla Public License 2.0. Distributed without source modifications. Corresponding source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6. Package: https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6.
- **BlackSharp.Core 1.0.7**, **DiskInfoToolkit 1.1.2**, **RAMSPDToolkit-NDD 1.4.2** — Florian K.; Mozilla Public License 2.0. These are unmodified upstream dependencies; their source repositories and exact revisions are recorded in the bundled `.nuspec` manifests and linked from their NuGet package pages. The full MPL 2.0 text is included. Recipients can obtain the corresponding source from those upstream repositories.
- **HidSharp 2.6.4** — Copyright 2010–2025 James Bellinger. See the package's bundled `LICENSE.txt` for its license and attribution.

## Windows diagnostics and runtime

- **Windows App SDK 2.6.5** (WinUI 3, `Microsoft.WindowsAppSDK` and its component packages) — Microsoft Corporation; Microsoft Software License Terms. Redistributed unmodified as the app's self-contained interface runtime. The package's `license.txt` and `NOTICE.txt` are included in the release inventory.
- **Win2D 1.4.0** (`Microsoft.Graphics.Win2D`) — Microsoft Corporation. Redistributed unmodified for drawing the graphs and the map; its license terms are at the address recorded in the bundled package manifest.
- **Microsoft.Data.Sqlite 10.0.14** — Microsoft and contributors, MIT. The MIT license text for it and `Microsoft.Data.Sqlite.Core`, whose packages carry no license file, is in `licenses/MICROSOFT-PACKAGES-MIT-LICENSE.txt` and release `licenses/texts/`. **SQLitePCLRaw.bundle_e_sqlite3 3.0.5** and its managed dependencies `SQLitePCLRaw.core`, `SQLitePCLRaw.config.e_sqlite3` and `SQLitePCLRaw.provider.e_sqlite3` 3.0.5 — SourceGear, LLC; Apache License 2.0. Their packages record that license but carry no license file, so the Apache 2.0 text, with each package's copyright line, is in `licenses/SQLITEPCLRAW-LICENSE.txt` and release `licenses/texts/`; exact versions, manifests and supplied notices are included in the release inventory. SQLite is public-domain software; see the native package's attribution and bundled license material.

- **Microsoft.Diagnostics.Tracing.TraceEvent**, **Microsoft.Diagnostics.NETCore.Client**, Microsoft.Extensions and System packages — Microsoft Corporation, MIT license. Individual package versions, copyright statements, repositories and notices are preserved in the release inventory and package manifests. The MIT license text for every MIT-licensed Microsoft package in the inventory that carries no license file of its own, among them TraceEvent, NETCore.Client and most System packages, is in `licenses/MICROSOFT-PACKAGES-MIT-LICENSE.txt` and release `licenses/texts/`, which lists them.
- **.NET runtime** — Microsoft and contributors; MIT and third-party licenses. See `licenses/texts/DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` in a release archive.
- **Mono.Posix.NETStandard** is a transitive dependency resolved by NuGet. Its manifest and available notices are preserved even where its platform-specific assets are not used on Windows. Its license text, the Mono license (MIT, with some third-party code under the 3-clause BSD license), is in `licenses/MONO-LICENSE.txt` and release `licenses/texts/`.

## Connections and offline geography

- **DB-IP City Lite** — IP Geolocation by [DB-IP.com](https://db-ip.com/), licensed under [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/). The monthly database is an optional user-initiated download from the [official DB-IP download page](https://db-ip.com/db/download/ip-to-city-lite); no DB-IP database is bundled in source or app packages. DeadWare reads the downloaded MMDB locally and displays approximate results; no upstream endorsement is implied.
- **MaxMind.Db 5.2.0** — MaxMind and contributors, Apache License 2.0. Unmodified local database reader. Source revision `575c4b8517c91d99cf8071bd7a3f3327a64696d8`: https://github.com/maxmind/MaxMind-DB-Reader-dotnet. The upstream Apache license is preserved in `licenses/MAXMIND-DB-LICENSE.txt` and release `licenses/texts/`. No commercial MaxMind database is bundled.
- **Natural Earth v5.1.2** — public-domain 1:110m land and country boundaries, 1:50m state/province boundaries, and 1:10m North American state/province boundaries and populated places, maintained by Nathaniel Vaughn Kelso, Tom Patterson and contributors. Embedded derivative coordinate/city JSON comes from pinned revision `f1890d9f152c896d250a77557a5751a93d494776`. Source URLs, SHA-256 hashes and reproducible transformations are in `assets/maps/README.md`. Terms: https://www.naturalearthdata.com/about/terms-of-use/. No upstream endorsement is implied.

## Lighting

- **OpenRGB.NET 3.1.1** — Diogo Trindade (diogotr7); MIT License. Copyright (c) 2022 Diogo Trindade, as the project's LICENSE file reads at the source revision below. Unmodified client library for OpenRGB's network SDK, built into the lighting helper, `DeadWare.Devices.exe`. Source revision `442b448c2ee76c554923918723d47121b6256552`: https://github.com/diogotr7/OpenRGB.NET. Package: https://www.nuget.org/packages/OpenRGB.NET/3.1.1. The package carries no license file, so that LICENSE file's MIT text, with its copyright line, is in `licenses/OPENRGB-NET-LICENSE.txt` and release `licenses/texts/`. Its manifest is included in the release inventory.

Corsair iCUE LINK protocol details were learned from public documentation and source of OpenLinkHub (Nikola Jurkovic, GPL-3.0), OpenRGB's CorsairICueLink controller (Aiden Vigue, Adam Honse, Nikola Jurkovic; GPL-2.0-or-later), FanControl.CorsairLink (Evan Mulawski) and liquidctl's Commander Core protocol notes (GPL-3.0). No code from these projects is included. Corsair, iCUE and iCUE LINK are trademarks of Corsair; DeadWare is not affiliated with or endorsed by Corsair.

`DeadWare.Devices.exe` uses HidSharp (listed above) directly, to talk to the hub.

OpenRGB itself isn't distributed with DeadWare. DeadWare talks to a copy of OpenRGB that you install, over OpenRGB's network SDK.

The restore dependency inventory may contain packages for non-Windows runtimes. This does not indicate DeadWare supports those operating systems or that all those assets are included in the Windows executable distribution.

No upstream component is claimed to be authored by DeadWare. Library and vendor names are used only to identify dependencies and compatibility.
