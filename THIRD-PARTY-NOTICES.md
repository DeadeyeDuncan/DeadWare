# Third-party notices

DeadWare's MIT license applies to original DeadWare code. Included dependencies retain their own licenses and notices. Release archives include package manifests, supplied license/notice files and a dependency inventory in `licenses/`.

## Hardware monitoring

- **LibreHardwareMonitorLib 0.9.6** — LibreHardwareMonitor contributors; Mozilla Public License 2.0. Distributed without source modifications. Corresponding source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6. Package: https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6.
- **BlackSharp.Core 1.0.7**, **DiskInfoToolkit 1.1.2**, **RAMSPDToolkit-NDD 1.4.2** — Florian K.; Mozilla Public License 2.0. These are unmodified upstream dependencies; their source repositories and exact revisions are recorded in the bundled `.nuspec` manifests and linked from their NuGet package pages. The full MPL 2.0 text is included. Recipients can obtain the corresponding source from those upstream repositories.
- **HidSharp 2.6.4** — Copyright 2010–2025 James Bellinger. See the package's bundled `LICENSE.txt` for its license and attribution.

## Windows diagnostics and runtime

- **Microsoft.Diagnostics.Tracing.TraceEvent**, **Microsoft.Diagnostics.NETCore.Client**, Microsoft.Extensions and System packages — Microsoft Corporation, MIT license. Individual package versions, copyright statements, repositories and notices are preserved in the release inventory and package manifests.
- **.NET runtime and Windows Desktop runtime** — Microsoft and contributors; MIT and third-party licenses. See `licenses/texts/DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` in a release archive.
- **Mono.Posix.NETStandard** is a transitive dependency resolved by NuGet. Its manifest and available notices are preserved even where its platform-specific assets are not used on Windows.

The restore dependency inventory may contain packages for non-Windows runtimes. This does not indicate DeadWare supports those operating systems or that all those assets are included in the Windows executable distribution.

No upstream component is claimed to be authored by DeadWare. Library and vendor names are used only to identify dependencies and compatibility.
