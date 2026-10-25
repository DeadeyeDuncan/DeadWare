DeadWare's first Windows preview combines hardware graphs, per-app network activity, detachable graphs and a desktop widget.

Download the **win-x64 ZIP**, extract the entire folder, and run **DeadWare.exe**. The .NET runtime is included. This preview is unsigned and does not include an installer.

Five theme presets are included. Sensor availability depends on hardware, firmware, drivers and permissions. Advanced monitoring and per-app ETW capture may require the explicit administrator restart option. Missing sensors remain unavailable; the app does not disable Windows protections or install a custom driver.

Hardware polling defaults to CPU and GPU sensors. Extended motherboard, controller, storage and memory-device sensors are opt-in and require a restart; their performance and support vary by machine.

History is held in memory for the running session. Network rates describe observed traffic, not an ISP billing meter. ARM64, comprehensive device coverage, persistent traffic history and firewall controls are not part of this preview.

The adjacent `.sha256` file provides a checksum for the ZIP. Report bugs and hardware compatibility findings through GitHub Issues; redact personal details before uploading diagnostics.
