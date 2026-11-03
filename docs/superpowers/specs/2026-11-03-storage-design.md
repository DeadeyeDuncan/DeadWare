# Storage (0.11.0)

Status: proposed on 2026-11-03; approach, placement, gating, integrations and all three design sections approved by the user in the same session. Second of three planned releases (per-core CPU shipped as 0.10.0, then Storage, then Processes). Per-application disk I/O is explicitly out of this release and belongs to 0.12 Processes.

## Intent

DeadWare watches CPU, GPU, RAM, network and battery, and nothing about disks. A drive that is overheating, wearing out or filling up gives no sign on the dashboard, and a process hammering the disk shows up only as "the machine feels slow". This release adds the four things the user asked for day to day: drive health and temperature, live read/write throughput, free space per volume, and a drive's temperature in Cooling Health. LibreHardwareMonitor's storage scan (`IsStorageEnabled`) is already plumbed into the collector but is bundled with the Extended sensors switch, which is off by default and warns about responsiveness; this release gives storage its own switch and keeps the fast metrics off LibreHardwareMonitor entirely.

Minor-version milestone 0.11.0: a new saved setting, a new sidebar page, a new Overview card, new persisted graph-history keys and new Cooling Health devices.

## Decisions taken in brainstorming

- Scope: health/temperature, throughput, free space. Per-application disk I/O deferred to 0.12 Processes, which will add a disk column to the per-process table.
- Placement: Overview card plus a Storage page plus an optional widget graph.
- Gating: own switch **Storage sensors**, on by default, restart to apply. Throughput and free space never depend on it.
- Integrations: six-hour live graph cache and Cooling Health. Not Alerts, not Usage history (both can follow later; nothing here blocks them).
- Data layer: hybrid. Windows performance counters and `DriveInfo` every poll; LibreHardwareMonitor storage sensors every 30 seconds on a separate loop; identity joined once through WMI.

## Proposed experience

- **Overview** gains a seventh card, **Disk**, after Network. Subtitle: the busiest drive's model, then `· N drives`. The graph draws total read (solid) and total write (dashed) on a shared bytes-per-second scale like the Network card, with separate colour buttons `diskread` and `diskwrite`. When more than one drive is present, one thin line per drive draws under the bold totals using the per-core line renderer, and the hover chip lists drives by current rate after the totals. The card grid grows from 2×3 to 2×4 rows; the 810 px minimum and the stretch-to-window-height behaviour are unchanged.
- **Storage** joins the sidebar between Network and Sensors with a disk icon. A card strip lists one row per drive: model, bus type, size, temperature, a health badge (`OK`, `Warning`, `Failing`, `Unknown`), wear percent, and a free-space bar spanning the drive's volumes. Selecting a row fills a detail card: that drive's read/write graph, a volume table (letter, label, used, free), a SMART summary (power-on hours, highest temperature, remaining life) and the identity note. A status line repeats the storage-related dashboard status (sensors off, more than eight drives, counters unavailable). **Open in window** pops the selected drive's graph out in the compact chrome other pop-outs use.
- **Widget** gains a **Disk** graph kind (total read/write), off by default in the widget subset so saved layouts do not change.
- **Settings → Monitoring** gains **Storage sensors (temperature and health; restart to apply)** beside Extended sensors. Off: temperature, health and wear read `Unavailable · storage sensors off`; throughput and free space still show.
- Removable drives and drives without a serial number appear on the page and the card, but are live-only: no six-hour cache, no Cooling Health device. The row's identity note says why.
- Up to eight drives are cached and offered to Cooling Health. Further drives are live-only and the hardware status line notes once that drives beyond eight are not recorded (the same pattern as the per-core 64-core cap).

## Data and collection

### Identity

`StorageDrive(Id, Serial, Model, Index, BusType, SizeBytes, Volumes)` and `StorageVolume(Letter, Label, SizeBytes, FreeBytes)` live in `DeadWare.Core`. `Id` is the serial number when WMI reports one, otherwise `model#index` with `IdentityNote = "no serial number"`. Enumeration runs at startup and every 60 seconds through WMI: `Win32_DiskDrive` → `Win32_DiskDriveToDiskPartition` → `Win32_LogicalDiskToPartition` → `Win32_LogicalDisk`. A failed enumeration keeps the previous drive list and sets a status.

### Fast path (every poll, collector thread)

- `PhysicalDisk(<index> <letters>)` counters `Disk Read Bytes/sec`, `Disk Write Bytes/sec` and `% Idle Time`, one `NextValue` sweep per poll. Instances are matched to drives by the leading index.
- `DriveInfo` for each volume's `TotalFreeSpace`.
- Published into the snapshot as `storage/{id}/read`, `storage/{id}/write`, `storage/{id}/active` (100 minus idle, clamped 0–100) and `storage/{id}/volume/{letter}/free`. `Reading` carries `StorageDriveCount`.
- Any counter or `DriveInfo` failure publishes `null` for that metric, never zero, and retries on the next poll; a persistent failure (60 s) sets `Disk counters unavailable` in the hardware status.

### Slow path (every 30 seconds, own loop)

- Gated by `Settings.StorageSensors`. When on, the collector enables `IsStorageEnabled` on its own `Computer` instance dedicated to storage, so the main hardware poll is untouched, and a background loop calls `Update` every 30 seconds.
- For each `HardwareType.Storage` hardware: temperature (°C), `Remaining Life` (percent), `Used Space` (percent), power-on hours where exposed, and LibreHardwareMonitor's SMART attribute table from which the health badge is derived: `Failing` when any attribute is below threshold or a critical warning is set, `Warning` when a reallocated/pending/uncorrectable count is non-zero or temperature exceeds the drive's reported maximum, `OK` otherwise, `Unknown` when SMART is not readable.
- Matched to a `StorageDrive` by serial, falling back to the model string; an unmatched hardware is listed on the page as `Unmatched · <model>` with its readings and no fast metrics.
- Each hardware update is bounded by the existing 5-second provider timeout; a stuck drive marks only that drive `Unavailable · sensor timeout` and the loop continues. The loop joins under the same 5-second hardware budget at quit.
- Published as `storage/{id}/temperature`, `/life`, `/used`, plus a `StorageHealth` record per drive on the reading for the page.

## Graph history

- `GraphHistoryProjection.Mappings` adds `storage/{id}/read` and `storage/{id}/write` for serial-identified drives, up to eight, under a new `storage` identity whose hash is the sorted list of drive ids. Totals are derived on read, not stored. Active percent and free space are not cached.
- Restore uses the same guard as CPU/GPU: no restore until the drive list is available and the identity matches. A changed drive set (new or removed serial) is a new identity; the old history ages out under the existing cap.
- 15-second batch writes, 1-second cadence and the 64 MiB cap are unchanged. Eight drives × two metrics at 0.5-second polling stays inside the cap over six hours.

## Cooling Health

- `ThermalProjection` emits one `ThermalDevice` per serial-identified drive with a temperature reading: `Kind = "Storage"`, `SensorMap = "temperature"`, `ConfigurationId` from the drive id. `Load` is the drive's active percent; fan, power and clock are null.
- The device picker groups devices as CPU, GPU and Storage. Reference comparisons and sustained-change warnings apply unchanged; a drive's thermal windows record only when the slow path is on.
- The thermal store schema does not change; `Kind` is already a string column.

## Errors and limits

- WMI unavailable: no drives, Overview card shows `No drives detected`, page shows the status; retry every 60 seconds.
- Counters unavailable (locale-renamed categories, broken performance-counter registry): fast metrics null, status set, page still lists drives with free space from `DriveInfo`.
- LibreHardwareMonitor storage throws: that hardware's slow metrics unavailable with the exception type in Sensors; other drives continue.
- Standard user: throughput and free space always work; ATA SMART on some controllers needs administrator, and the badge then reads `Unknown · needs administrator` with the existing restart-as-administrator action.
- No new database or file; settings gain one boolean.

## Testing

- Core: projection join by serial, by model fallback, duplicate models, no-serial exclusion; null-not-zero on counter failure; eight-drive cap and status; thermal device emission and picker grouping; health badge derivation table.
- App offscreen: seven-card Overview in every theme and both layouts; Storage page rows, detail card, volume table and status strings; widget Disk graph; pop-out; Settings switch. A fake storage provider stands in for WMI, counters and LibreHardwareMonitor, as the existing fake hardware source does.
- Manual before tagging: the user's machine with NVMe and any SATA drive, standard user and administrator, sensors switch off, USB drive plug and unplug, quit with a drive mid-update.

## Out of scope

Per-application disk I/O (0.12), Alerts rules on storage, Usage history minute rows, SMART raw attribute browser, drive benchmarks, trim/defrag actions, network shares.
