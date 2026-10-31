# Third-party notices

DeadWare's MIT license applies to original DeadWare code. Included dependencies retain their own licenses and notices. Release archives include package manifests, supplied license/notice files and a dependency inventory in `licenses/`.

## Theme typography

- **Caesar Dressing** — Copyright 2011 Open Window; SIL Open Font License 1.1. Unmodified, embedded for readable Nordic-style lettering.
- **Noto Sans Runic** — Copyright 2022 The Noto Project Authors; SIL Open Font License 1.1. Unmodified, embedded for decorative rune glyphs.
- Font sources and checksums are recorded in `assets/fonts/README.md`; complete notices are in `licenses/CAESAR-DRESSING-OFL.txt` and `licenses/NOTO-SANS-RUNIC-OFL.txt`.

## Hardware monitoring

- **LibreHardwareMonitorLib 0.9.6** — LibreHardwareMonitor contributors; Mozilla Public License 2.0. Distributed without source modifications. Corresponding source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6. Package: https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6.
- **BlackSharp.Core 1.0.7**, **DiskInfoToolkit 1.1.2**, **RAMSPDToolkit-NDD 1.4.2** — Florian K.; Mozilla Public License 2.0. These are unmodified upstream dependencies; their source repositories and exact revisions are recorded in the bundled `.nuspec` manifests and linked from their NuGet package pages. The full MPL 2.0 text is included. Recipients can obtain the corresponding source from those upstream repositories.
- **HidSharp 2.6.4** — Copyright 2010–2025 James Bellinger. See the package's bundled `LICENSE.txt` for its license and attribution.

## Windows diagnostics and runtime

- **Microsoft.Data.Sqlite 10.0.10** — Microsoft and contributors, MIT. **SQLitePCLRaw.bundle_e_sqlite3 3.0.5** and its managed/native dependencies retain their upstream license notices; exact versions, manifests and supplied notices are included in the release inventory. SQLite is public-domain software; see the native package's attribution and bundled license material.

- **Microsoft.Diagnostics.Tracing.TraceEvent**, **Microsoft.Diagnostics.NETCore.Client**, Microsoft.Extensions and System packages — Microsoft Corporation, MIT license. Individual package versions, copyright statements, repositories and notices are preserved in the release inventory and package manifests.
- **.NET runtime and Windows Desktop runtime** — Microsoft and contributors; MIT and third-party licenses. See `licenses/texts/DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` in a release archive.
- **Mono.Posix.NETStandard** is a transitive dependency resolved by NuGet. Its manifest and available notices are preserved even where its platform-specific assets are not used on Windows.

## Connections and offline geography

- **DB-IP City Lite** — IP Geolocation by [DB-IP.com](https://db-ip.com/), licensed under [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/). The monthly database is an optional user-initiated download from the [official DB-IP download page](https://db-ip.com/db/download/ip-to-city-lite); no DB-IP database is bundled in source or app packages. DeadWare reads the downloaded MMDB locally and displays approximate results; no upstream endorsement is implied.
- **MaxMind.Db 5.2.0** — MaxMind and contributors, Apache License 2.0. Unmodified local database reader. Source revision `575c4b8517c91d99cf8071bd7a3f3327a64696d8`: https://github.com/maxmind/MaxMind-DB-Reader-dotnet. The upstream Apache license is preserved in `licenses/MAXMIND-DB-LICENSE.txt` and release `licenses/texts/`. No commercial MaxMind database is bundled.
- **Natural Earth v5.1.2** — public-domain 1:110m land and country boundaries, 1:50m state/province boundaries, and 1:10m North American state/province boundaries and populated places, maintained by Nathaniel Vaughn Kelso, Tom Patterson and contributors. Embedded derivative coordinate/city JSON comes from pinned revision `f1890d9f152c896d250a77557a5751a93d494776`. Source URLs, SHA-256 hashes and reproducible transformations are in `assets/maps/README.md`. Terms: https://www.naturalearthdata.com/about/terms-of-use/. No upstream endorsement is implied.

The restore dependency inventory may contain packages for non-Windows runtimes. This does not indicate DeadWare supports those operating systems or that all those assets are included in the Windows executable distribution.

No upstream component is claimed to be authored by DeadWare. Library and vendor names are used only to identify dependencies and compatibility.
