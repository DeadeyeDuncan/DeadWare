# Fan control: hardware notes

Status: reference material for [the fan-control design](2026-11-25-fan-control-design.md). These are facts and interoperability conventions only. No code from another project is used or copied. Where a detail is unconfirmed, it says so.

Confidence marks: a plain statement was read in source code or an official document; a maintainer's statement is named as such. **(reported)** is a community report that was not checked. **(inferred, untested)** is a conclusion drawn from the sources. All of it is desk research of 2026-11-25: nothing here was tried on hardware.

## Part 1: the board fan chip (Nuvoton NCT6683D, NCT6686D, NCT6687D)

"The library" is LibreHardwareMonitorLib 0.9.6 unless another version is named. "The Linux driver" is the out-of-tree nct6687d driver. The chip's datasheet is not public.

### Identification

- NCT6687D reports chip id `0xD5`, revision `0x92`. The ids of NCT6683D and NCT6686D were not recorded.
- The "DR" variant has the same chip id. The library chooses it by board name alone: an MSI board whose product name contains B840, B850, X870 or Z890 (0.9.7 prereleases add B860). The firmware of those boards uses other registers for the system fans. Every other board is plain NCT6687D, an MSI B650M included: the Linux driver's README says the same for B650 boards, and the kernel's tested-board list has an MSI B650M with this chip (for monitoring).
- For a board with no model entry of its own the library shows 8 controls and 8 fans, named by index: 0 CPU Fan, 1 Pump Fan, 2–7 System Fan #1 to #6. Its identifiers carry a chip index, for example `/lpc/nct6687d/0/control/2`. The only MSI B650M with an entry of its own is the B650M GAMING PLUS WIFI.
- A board can have fewer headers than that. MSI's specification for the MAG B650M MORTAR WIFI lists five: CPU fan, pump fan and three system fans (read through a search summary). Controls 5–7 then have no header; writing them would set mode bits 5–7 and registers `0xA2D`–`0xA2F` with unknown effect (inferred, untested).
- On one plain NCT6687D board, an MSI MAG X670E Tomahawk WiFi, the fans could be read but did not respond to control (reported, closed unresolved).

### Reaching the registers

- The registers are in the chip's EC space (4096 of them, at the addresses in the table below) and are reached through three I/O ports at the chip's base address + 4, + 5 and + 6. One of them is a page register. The research does not spell out which port is which: page, index and data in that order, with the high byte of a register address as the page and the low byte as the index, is the natural reading (inferred, untested). How the base address is obtained was not recorded either.
- **Page-register convention** (NCT668x chips): before each register access wait until the page register reads `0xFF`, and after the access write `0xFF` to it to release it. The library forces access after waiting 500 ms.
- **Named mutexes** that cooperating Windows programs take, created with full access for everyone: `Global\Access_ISABUS.HTP.Method`, `Global\Access_PCI` and `Global\Access_EC` (the library has more, for Razer and USB devices). **`Global\Access_ISABUS.HTP.Method` is the one that guards this bus** ("the ISA mutex" below); the comments of PawnIO's LpcIO module name the same mutex. The library counts an abandoned mutex, one whose owner died, as acquired.
- A program that ignores the mutex can break another program's chip detection (inferred, untested). For intermittently missing NCT6687D sensors FanControl's maintainer suspected another tool's fan feature (reported).

### Registers and the write sequence

| Register | Meaning |
|---|---|
| `0xA00` | Mode, one register for all headers. Bit i = 1: header i is under manual (software) control. Bit i = 0: firmware automatic. Bit 0 CPU, bit 1 pump, bits 2–7 system fans 1–6. |
| `0xA01` | Configuration handshake, shared by all headers: `0x80` = request, `0x40` = done. |
| `0xA28` + i | Duty command for header i, 0–255. |
| `0x160` + i | Duty read-back for header i, 0–255. |
| `0xCF8` | Fan engine status (phase, invalid, check done, lock), named in the Linux driver. The library does not use it. |
| `0x180` bit 7, `0x1BB`–`0x1BF` | Monitor enable bits. The library writes them on every open. |

The library sets the duty of header i in these steps, holding the ISA mutex from the first to the last:

1. Write `0xA01` = `0x80`, then wait 50 ms.
2. Read `0xA00`, set bit i, write it back.
3. Write the duty to `0xA28` + i.
4. Write `0xA01` = `0x40`, then wait 50 ms.

- It makes no status check, read-back or retry. One write holds the bus, and blocks the caller, for at least 100 ms.
- Only bit i and `0xA28` + i are touched, so control is per header. In a Linux test on an MSI board one channel was held in manual mode while the other seven stayed on the BIOS curve (reported). What the firmware does with the other headers while the shared handshake is open is not documented.
- The duty is one byte. The library multiplies a percentage by 2.55 and truncates (100 gives 255, 50 gives 127), and for the read-back divides by 2.55 and rounds to a whole percent. A written 0 stops a fan that supports it (Linux driver README). No firmware minimum is documented.
- **The duty stays in the chip without rewriting.** The library writes only when a mode or value changes; its update pass only reads. The Linux README describes manual mode as fixing the fan at its current speed, and the Linux driver gained a watchdog of its own because the last manual value otherwise stays active without limit (reported). No report was found of the firmware taking a header back at run time while its manual bit is set. Boot and resume are different (below).

### Returning a header to firmware (BIOS) control

- Clearing bit i of `0xA00` returns header i to the firmware. For an automatic-mode request the Linux driver reads `0xA00`, clears the bit, writes it back and reads it again to verify, all outside the `0xA01` handshake. In a test of that driver on the CPU fan channel of an MSI board the BIOS curve took over at once (reported, one test).
- The library's restore also writes the mode register before it opens the handshake, and then writes the saved duty command inside it. It has no read-back, in 0.9.6 or in the prereleases.

### LibreHardwareMonitor 0.9.6: what a caller must know

- **Saved state is per instance.** At its first write to a header a chip object saves that header's mode bit and the content of `0xA28` + i. It is kept in memory only, and every `Open()` and `Reset()` starts with nothing saved.
- **The hand-back is inverted.** The restore runs on `SetDefault()`, `Close()`, `Reset()` and when the motherboard group is switched off. It ANDs the mode register with the complement of the saved bit, then writes the saved duty command back. What that does depends on the state the instance first saw:
  - Mode bit 0 (firmware control, the normal case after boot): the header **stays in manual mode**, at the duty `0xA28` + i held before takeover.
  - Mode bit 1 (already manual: left by a crash, an earlier faulty hand-back or another tool): the header is switched to firmware control.
- Within one instance the outcome alternates: a second take-and-release cycle hands back correctly (one report agrees). The fault covers NCT6683D, NCT6686D, NCT6687D and the DR variant, and is in every release from the first NCT6687D support in 2020 up to 0.9.6. Headers were left at 40 % on several MSI boards, at 25 %, at about 50 % on an ASRock B650 with NCT6686D and at 100 % on a CPU fan (reported). That value is board firmware data.
- **`SetSoftware` writes 0 % first.** It sets the mode, then the value. The mode change at once writes the control's stored software value, which is 0 for a new control; the value change then writes the requested duty. The header is therefore commanded to 0 % for about 100 ms at first takeover, and stays there if the second write is dropped (inferred, untested). On an MSI B550-A PRO the system fans visibly stopped for about a second, which the maintainer traced to this order (reported). A control reads the `ISettings` keys `<control id>/value` and `<control id>/mode` when it is built, and a stored mode of `Software` makes the library take control inside `Open()` or `Reset()`. A host that passes its own settings object therefore decides what the first write is (inferred, untested); the default settings object stores nothing.
- **Writes and restores are skipped silently.** Each waits 10 ms for the ISA mutex. On a time-out nothing is written, nothing is returned or thrown, and the control object already shows the new mode and value. `Update()` waits 10 ms too and skips its pass. Chip detection in `Open()` and `Reset()` waits 100 ms, and on a time-out that instance has no Super I/O hardware at all. A write can therefore be lost whenever another client holds the bus for more than 10 ms, or when `Update()` and `SetSoftware` run on different threads of one process (inferred, untested).
- **An unchanged value cannot be sent again.** The setters act only on a change, and there is no refresh call. Values are not clamped to 0–100.
- **`Close()`** runs the restore for every control, then closes the PawnIO handles and the mutexes. `Reset()` does the same and builds everything anew. `Computer` is not disposable and has no finaliser or process-exit hook: a host that exits without `Close()` restores nothing. The library does not handle sleep, resume or session end. **A read-only instance** saved nothing, so its `Close()` restores nothing; it still writes the monitor enable bits on every open.
- **Two `Computer` objects in one process** share the same static mutex objects. Closing one closes them for both, after which the other's waits fail and its Super I/O reads and writes are skipped. Two instances that control one header each restore their own saved state, so one can undo or mis-restore the other (inferred, untested). The mode register is read, changed and written inside the mutex, so instances that change different headers keep each other's bits.
- **Reading the mode bit through the public interface:** `GetReport()` on the Super I/O hardware dumps all 4096 EC registers as hex text, and the row `0A00` starts with the mode and handshake registers. It waits 100 ms for the mutex, leaves the dump out on a time-out, and holds the bus for the whole dump. The control sensor's value is the duty read-back, not the mode.

### The 0.9.7 prereleases

| Package | Published | Change that matters here |
|---|---|---|
| 0.9.7-pre742 | 2026-09-18 | Hand-back fix: the restore puts the saved mode bit back |
| 0.9.7-pre747 | 2026-09-29 | `SensorType.SmallData` removed; every Data sensor reports bytes |
| 0.9.7-pre748 | 2026-10-03 | `SetSoftware` sets the value before the mode |
| 0.9.7-pre749 | 2026-10-03 | AMD CPU: new sensors shift voltage and temperature indices (inferred, untested) |
| 0.9.7-pre784 | 2026-11-11 | net11.0 added; net8.0 and net9.0 dropped |

- At the time of the research 0.9.6 (2026-02-14) was the newest stable release and 0.9.7-pre785 (2026-11-11) the newest prerelease. No date for a stable 0.9.7 was found.
- **Why a prerelease is not a clean swap.** Every package with the hand-back fix also renames storage hardware (0.9.6 uses `/nvme/N`, `/ssd/N` or `/hdd/N`; from pre742 at the latest a transport prefix such as `ata`, `scsi`, `nvme` or `usb`), moves to DiskInfoToolkit 2.x and ships different PawnIO module binaries (module set 0.2.11, where 0.9.6 names 0.1.6). From pre747 the AMD GPU memory sensors move from `/gpu-amd/N/smalldata/…` to `/gpu-amd/N/data/…` (identifier strings inferred, untested) and from MB to bytes, other Data sensors go from GB to bytes, and every `SensorType` member after the removed one moves down by one. The `SetSoftware` fix comes after that removal, so no package has both fixes without it.
- Unchanged through pre785: the NCT6687D identifiers and default names, the control interface, the mutex names, the PawnIO wrapper, the 10 ms mutex wait and the missing read-back.
- The fixed restore puts back the state its instance first saw. A header left in manual mode by a crashed session is saved as manual and restored to manual at the crashed session's duty, so a fixed version cannot force firmware control through its public interface (inferred, untested).

### Reaching the chip without the library

- The library's own I/O classes are not public: its Super I/O interface, chip class, port class and LpcIO wrapper are internal. Its PawnIO class is public and has a public execute call, but the constructor is private and the only factory is internal.
- PawnIO is a driver that is installed separately. A client opens the device `\\?\GLOBALROOT\Device\PawnIO`, loads the signed module `LpcIO.bin` and calls the module's functions `ioctl_select_slot`, `ioctl_find_bars`, `ioctl_pio_inb` and `ioctl_pio_outb`. What each takes and returns was not recorded. The module allows only the Super I/O ports and the base address ranges it finds, and asks its callers to hold the ISA mutex.
- The signed module blob is an embedded resource of the library's assembly and also ships in the PawnIO.Modules releases (LGPL-2.1). The blob in the prereleases differs from the one in 0.9.6.
- The library reads the installed PawnIO version from the registry, and its application asks for an update below 2.0.0. Whether the 0.2.11 modules need a newer driver was not found. When the device cannot be opened the library carries on with an unloaded module whose reads all return zero.

### Failsafes

- **Watchdog.** No description was found of a chip or firmware watchdog that returns a header to its curve when the host stops writing. The Linux driver added one of its own: a lease of 1–300 s that user space arms and keeps renewing, after which the driver restores every channel it changed. It lives in the driver, so it covers a dead control program and not a dead host (inferred, untested).
- **Over-temperature in manual mode: not known.** No source says whether the firmware forces full speed or applies a critical temperature while a header's manual bit is set. Reports show headers held at a low fixed duty with no override mentioned, which proves nothing either way. Independently of this chip the CPU limits itself at its maximum temperature (an AMD product page, read through a search summary); a shutdown threshold was not found.
- **Datasheet.** Available from Nuvoton on request only, per the kernel documentation, which adds that Intel boards run firmware whose specification is under NDA. The mainline kernel driver allows duty writes on Mitac boards only.

### After a crash, a reboot, sleep and resume

- **Controlling process killed:** the header stays in manual mode at the last written duty, and the mutex is left abandoned (inferred from the code and the persistence evidence, untested). If the process dies inside a write, `0xA01` is left at `0x80`, the request value it holds for at least 50 ms of every write. On an MSI PRO X870E-P WIFI the firmware's own fan setup waits for that request to clear and never clears a stale one (reported, another board); the effect on other boards is unknown. The next complete write closes the handshake again (inferred, untested).
- **Reboot:** returns firmware control. After a restart with all controls disabled the fans followed the BIOS curves, and during a controller's start delay after boot they sat at the BIOS level (reported).
- **Boot and S3 resume:** the board firmware runs its own fan configuration (reported for the MSI PRO X870E-P WIFI), so manual mode is probably lost and software has to take control again (inferred, untested). On resume the Linux driver restores only its monitor enable register. Which of warm reboot, cold boot, S3 resume and hibernate clear the mode bits on a B650 board is not known; the evidence is from other boards.
- **Resume in other programs:** the library does nothing. Its own application calls `Reset()` on resume and `Close()` at session end. FanControl re-detects and re-applies every fan speed five seconds after resume, and on one board a shorter delay brought a fault back (reported).

### The FanControl program

- The installed copy of FanControl that was examined bundles LibreHardwareMonitorLib 0.9.5 (commit `596a861`), which has the same inverted hand-back. Which FanControl version that copy is was not determined. FanControl V278 (2026-09-19) lists a fix for hand-off when a control is disabled, together with a library update.
- So the headers that copy controlled stay in manual mode after it exits, until the firmware re-initialises them, and a program started in that state finds the mode bit already set (inferred, untested).

### AMD graphics card fans

The library gives every AMD GPU a fan control (`/gpu-amd/<n>/control/0`), but it sets the speed with an ADL Overdrive5 call and ignores the result. For RX 6000 cards that call does not work (reported): the upstream issue for an RX 6800 has been open since 2022, an RX 6900 owner got "not supported" from every function that sets a specific fan speed, and a feature request explains that Overdrive 8 has a fan-curve system that makes the old call inoperable. The code is the same in 0.9.7-pre785. FanControl uses AMD's ADLX interface instead, and its maintainer says that third-party software cannot apply 0 % to a GPU (reported).

## Part 2: the iCUE LINK System Hub

The device, framing, command bytes, read transactions, parsing, device types, the two mode commands and the shared mutex are in [the iCUE LINK protocol notes](2026-11-15-corsair-icue-link-notes.md) and are not repeated here. Reply offsets are counted as in that file, after the report-ID byte. FanControl.CorsairLink ("the plugin") is the Corsair plugin for the FanControl program. LinkPilot (created 2026-11-22) and OpenLinkHubWindows (a port of OpenLinkHub, created 2026-10-17) are very young and not widely used.

### The speed write

One transaction of four frames on handle 1, inside the shared mutex: close endpoint `0x18` (`05 01 01 18`), open it (`0D 01 18`), write (`06 01 <payload>`), close it again. Each reply echoes the opcode at `[2]` and carries the status at `[3]`. OpenLinkHub, FanControl.CorsairLink and LinkPilot all build the same bytes.

| Payload offset (after `06 01`) | Size | Content |
|---|---|---|
| 0 | 2 | length, little-endian = 4·N + 3: the data type and everything after it are counted, the two zero bytes are not |
| 2 | 2 | `00 00` |
| 4 | 2 | data type `07 00` |
| 6 | 1 | N, the number of channel records |
| 7 + 4k | 1 | record k (k from 0): channel number, the same LINK channel that indexes the speed and temperature tables |
| 8 + 4k | 1 | record k: mode byte, `00` = fixed percent |
| 9 + 4k | 2 | record k: value, 16-bit little-endian. In percent mode the low byte is the percent, 0–100, and the high byte is `00` |

- **Known-good vector** (FanControl.CorsairLink's unit test): channel 1 at 50 % and channel 8 at 100 % give the bytes `02 01 00 32 00 08 00 64 00` from offset 6. A whole frame built on it would start `00 00 01 06 01 0B 00 00 00 07 00 02 01 00 32 00 08 00 64 00`, followed by zero padding (inferred, untested).
- **Mode byte.** `00` is the only mode any current tool sends. `01` = fixed RPM, with the RPM as the 16-bit value, comes from a single 2024 sketch by OpenLinkHub's author on firmware 2.4.438; he states that it works for fans and that pumps accept percent only. Nobody has shown it on firmware 3.x or 4.x. No other value is documented.
- **Several channels in one write.** FanControl.CorsairLink and LinkPilot put every channel in each write, and OpenLinkHub's curve loop sends its whole table when any one channel changes. A write may also carry a single channel (OpenLinkHub's manual path). That channels left out keep their value is stated nowhere (inferred, untested). All three tools write the records in ascending channel order; whether the order matters is not stated. OpenLinkHub and FanControl.CorsairLink include a record for every listed device, even one without a motor. 24 channels need 97 data bytes, so a speed write always fits one frame.
- **Reply status.** `00` = accepted, `03` = wrong mode: the hub is in hardware mode, and the open step is expected to answer it first (inferred, untested). No other code is documented, and no tool reads a payload from the write reply. On a status other than `00` OpenLinkHub repeats the whole transaction about 20 times at 100 ms intervals and then gives up silently. FanControl.CorsairLink raises an error on a non-zero status of any of the four frames and drops that refresh; on `03` or a time-out it sends the software-mode command again. LinkPilot on `03` re-enters software mode and sends its full table on the next tick.
- FanControl.CorsairLink drains queued input reports before every output frame and uses 500 ms read and write time-outs. A close of a handle that is not open is answered with `00`, as in the read transaction (inferred, untested for this endpoint).
- **Re-sending.** All three tools read speeds and temperatures every second (LinkPilot: settable 0.5–5 s). OpenLinkHub writes speeds once at start (70 % to every channel) and then only when a channel's whole-number percent changes; it evaluates its curves every 3 s. FanControl.CorsairLink writes once at start (fans 50 %, pumps 100 %) and then only when a requested value differs from the stored one, checked every second. LinkPilot writes on a change and at least every 10 s. iCUE polls continuously and writes only when something changes, per a USB capture by OpenLinkHub's author. OpenRGB never writes speeds.
- **The hub keeps the last value it was given.** OpenLinkHub's author states that with automatic control switched off the speeds are written once at 70 % and stay there, and LinkPilot's documentation says the hub keeps the last speed through communication trouble. This rests on those statements; no measurement was found.
- **Order.** No tool sends a speed write before the software-mode command. OpenLinkHub sends the firmware query, software mode, a 500 ms pause, lighting setup and the device list, then its first speed write. FanControl.CorsairLink sends the firmware query, software mode with no pause, the device list, the speed write, then its reads. After it re-enters software mode later (status `03`, a time-out or a USB reconnect) it does not send its speeds again unless a value happens to change.

### Pump limits (TITAN, type `0x11`)

| Tool and path | TITAN floor | Other pumps | At start |
|---|---|---|---|
| OpenLinkHub, interpolated curves | clamps to 31 % | other AIO pumps: under 50 % becomes 70 %; XD5 and XD6: under 20 % becomes 30 % | 70 % |
| OpenLinkHub, step tables (the setting in its sample configuration) | no TITAN rule: under 50 % becomes 70 % | the same | 70 % |
| OpenLinkHub, manual single-channel set | 50 % | 50 % for anything with a pump; the profile editor saves a pump value under 20 as 50 | none |
| FanControl.CorsairLink | 50 %, overridable to 0–100 with an environment variable | the same | 100 % |
| LinkPilot | 50 %, settable 20–100; unknown devices are treated as pumps | the same | 70 % |
| OpenLinkHubWindows | 31 % | 50 % for other AIO pumps | 70 % |

- **Why 31 %.** OpenLinkHub treated the TITAN like other AIO pumps (50 %) until February 2026. Its author then found in an iCUE capture that 31 % is the lowest value iCUE sends to a TITAN, that it gives about 1,400–1,500 RPM and that iCUE's Quiet preset uses it. The rule is keyed to type `0x11` alone and exists only in the interpolated-curve path, the one the author still maintains.
- **Why 50 %, and 100 % at start.** FanControl.CorsairLink introduced both in 2023 for the predecessor controller (Commander Core), after a user's pump entered a failure state at 0 % and because of noise and resonance at low pump speed. The LINK hub inherited them; no LINK incident is cited. The override exists because a user ran a pump at 25 % at idle without trouble (reported). When a control is switched off in FanControl the plugin resets that channel to its defaults, pump 100 % and fan 50 %.
- **Below the floor, or at 0 %: not documented for a TITAN on the LINK hub.** No test, capture or statement was found. iCUE itself never sends a TITAN less than 31 %. No report of a stall, noise or damage at low speed was found either; the one user who runs a TITAN at about 1,450 RPM calls it silent and stable (reported). The hub passes the pump's tachometer signal on to the motherboard, so a very slow or stopped pump can also raise a board-level alarm (inferred, untested).
- On the predecessor (Commander Core with an Elite LCD pump) 0 % triggered a pump-failure state at once: red lighting, a warning on the pump screen and fan control no longer working, cleared only by a full shutdown. 1 % ran at about 600 RPM, and grinding noise began at about 44 % (reported). OpenLinkHub's author describes that failsafe as red blinking lighting with the fans at 100 %, triggered by a failed coolant probe, a failed pump tachometer reading or a corrupted stored pump profile, and not by 50 %.
- **Corsair's advice:** pump speed matters little for temperatures compared with fan speed, and Corsair recommends a fixed pump speed of about 70–80 % with the fans following coolant temperature. iCUE's pump presets are Quiet, Balanced and Extreme; Corsair publishes no RPM or percent for them.

| Pump | Condition | RPM | Confidence |
|---|---|---|---|
| TITAN (`0x11`) | 31 % | 1,400–1,500 | OpenLinkHub's author |
| TITAN RX 360, hub firmware 2.11.527 | iCUE Variable Speed at zero load | about 1,450 | reported |
| TITAN RX 360 | iCUE's default, Balanced | about 2,000 | reported, search summary only |
| TITAN 240; TITAN 360 RX RGB | maximum | 2,800; up to about 3,000 | Corsair; reported, search summary only |
| TITAN II 360 RX LCD (`0x1A`) | Quiet / Balanced / Extreme; 100 % | 1,540 / 2,070 / 3,025; about 3,070 | reported |

- A straight line from 31 % at about 1,450 RPM to 100 % at 2,800–3,000 RPM gives about 20–22 RPM per percent: roughly 1,820–1,880 RPM at 50 % and 2,210–2,330 RPM at 70 % (inferred, untested). A scale through zero does not fit, so either the scale has an offset or the pump has a floor of its own near 1,400–1,500 RPM. Which is not known.

### Fan limits (RX RGB, type `0x0F`)

- **Speed range.** Corsair lists 400–2,100 RPM ±10 % for the RX120 RGB sold as a fan and 300–2,100 RPM ±10 % for the same fans on the TITAN 360 RX RGB page. The two figures disagree at the low end. Each fan draws 0.66 A.
- **Control range.** Corsair states 20–100 % and a zero-RPM mode in which the fan stops at low load. The cooler's specification lists the control method as digital, not a PWM wire, so the percent is a request that the hub's firmware turns into a drive level (inferred, untested).
- **Ramping and cut-off.** Hub firmware added a smooth ramp-up (2.9.488), a smoother ramp rate (3.2.571) and a minimum on-time near the zero-RPM cut-off (3.11.646). The hub therefore ramps changes itself and has a cut-off near the bottom of the range (inferred, untested). The cut-off percent and the minimum start duty are not published.
- **What tools allow.** OpenLinkHub floors fans at 20 % unless the profile carries its zero-RPM flag, and then sends lower values, 0 included. FanControl.CorsairLink and LinkPilot allow 0–100 %. OpenLinkHubWindows treats 20 % as the lowest speed that does not stop the fan. RX140 MAX fans on a LINK hub stood still under iCUE's Quiet preset and under an OpenLinkHub zero-RPM profile (reported); nothing specific to type `0x0F` was found.
- **RPM per percent.** The second-generation RX fans of a TITAN II gain about 220 RPM per 10 % and reach 2,130 RPM at 100 % (reported), which implies roughly 370 RPM at 20 % (inferred, untested).

### Default profiles

OpenLinkHub's built-in profiles. All three follow **CPU temperature**, not coolant. Values are percent, fans / pump.

| CPU °C | 0–30 | 30–40 | 40–50 | 50–60 | 60–70 | 70–80 | 80–90 | above 90 |
|---|---|---|---|---|---|---|---|---|
| Quiet | 30 / 50 | 40 / 50 | 40 / 50 | 40 / 50 | 40 / 50 | 70 / 80 | 90 / 90 | 100 / 100 |
| Normal | 30 / 70 | 40 / 70 | 40 / 70 | 45 / 70 | 55 / 70 | 70 / 80 | 90 / 90 | 100 / 100 |
| Performance | 70 / 70 | 70 / 70 | 70 / 70 | 70 / 70 | 70 / 70 | 70 / 80 | 90 / 90 | 100 / 100 |

Curves that follow **coolant temperature**, written as °C: percent.

| Curve and source | Points |
|---|---|
| OpenLinkHub's template for a new coolant profile, fans / pump | 0–32: 30 / 50; 32–36: 40 / 50; 36–40: 40 / 60; 40–42: 50 / 60; 42–44: 60 / 70; 44–46: 70 / 80; 46–48: 80 / 90; 48–50: 90 / 90; 50–60: 100 / 100 (marked critical) |
| Corsair's default for Hydro X on the LINK hub, XD5 pump | below 20: 38; 30: 44; 40: 46; 45: 53; 50: 66; above 55: 100 |
| Corsair's default for Hydro X on the LINK hub, fans | below 34: 0 (fans stopped); 35: 10; 36: 25; 38: 42; 39: 54; 40: 66; 43: 100 |

- **OpenLinkHub's rules around its tables.** A new device, the pump included, starts on Normal (the Windows port starts AIO pumps on the coolant profile). With interpolated curves switched on, each table becomes a point list: one point at the first row's lower bound, then one at each later row's upper bound, straight lines between them, the result rounded to a whole percent. A missing profile falls back to Normal, and a sensor that reads zero is replaced by 50 °C. Coolant above 57 °C forces every device to 100 % until it drops below 52 °C. A fixed profile is 70 / 70.
- **Corsair's curves** are from its help article on Hydro X default curves, which describes them as tuned for silence. They are for a custom loop with a D5 pump, not for the TITAN, and they are the only cooling curve points Corsair publishes for this hub.
- **iCUE's presets for AIO coolers** (Quiet, Balanced, Extreme, Zero RPM; Balanced by default) have followed the coolant sensor since iCUE 5.5.134 and followed the CPU package sensor before. Their points are not published. Since iCUE 5.6.97 a preset copied into a custom curve shows the real preset data for LINK coolers. A custom temperature curve in iCUE has 7 points.

### Hardware mode

- Corsair: Device Memory Mode sets how fans and pump behave while iCUE is not running. It is limited to what the hub can do alone: a fixed percent, a fixed RPM, or a temperature curve that uses a sensor on the hub's own devices.
- Stored settings do run without a host: RX120 fans stored at a fixed 800 RPM ran that way whenever no host program was active (reported). Software mode does not erase them; they are only inactive while a host is in control (OpenLinkHub's author).
- When nothing was ever stored: no official statement was found. Forum users say the hub then runs a default Quiet profile (reported, search summary only). Reports on whether custom curves work in Device Memory Mode conflict (reported, search summaries only).
- **No open implementation reads or writes the LINK hub's stored cooling settings.** OpenLinkHub, FanControl.CorsairLink, LinkPilot and OpenRGB between them use only the endpoints `0x17`, `0x18`, `0x20`, `0x21`, `0x22`, `0x28`, `0x29`, `0x36` and `0x40`. liquidctl has no LINK driver.
- liquidctl does program the stored settings of the predecessor (Commander Core): a speed mode, a fixed percent and a curve of up to 7 points per port. Whether the LINK hub has equivalents, and at which endpoints, is unknown. A corrupted stored pump profile is one of the failsafe triggers named for the predecessor. The only documented way to store a fallback curve in this hub is iCUE's Device Memory Mode (inferred, untested).
- In hardware mode the hub answers endpoint commands with status `03`, so speeds and temperatures cannot be read from it. The pump is then visible only at the motherboard's tachometer header.

### The software-mode watchdog

| Date | Firmware | Evidence | Confidence |
|---|---|---|---|
| 2025-08 | not stated | OpenRGB's controller sends its colour frame again when 5 s pass without a write, and sends hardware mode on exit | source code |
| 2025-08 | 3.x | A plugin start that failed after entering software mode left the lighting dark for about 20 s, after which it returned; the fans kept spinning throughout | reported |
| 2025-02 to 2026-07 | 3.10.636 in one case | LINK lighting stays dark for as long as FanControl runs, while the plugin sends only reads between speed changes | reported |
| 2026-09 | not stated | OpenLinkHub's author: all newer Corsair devices have a heartbeat and return to hardware mode after 20 s without data | his statement |
| 2026-11 | not stated | LinkPilot expects the wrong-mode status when another lighting tool starts or closes; OpenLinkHubWindows assumes hardware mode after sleep | source code |

- The 20 s differs from the 10 s the same author measured on firmware 2.4.438 in 2024 (the figure in the protocol notes), and he names no firmware for it. **Nothing was measured on 4.1.656.** Users run FanControl.CorsairLink and OpenLinkHub on 4.1.656 without keep-alive complaints, which is absence of evidence only (reported).
- **Reads probably count as traffic** (inferred, untested): between speed changes FanControl.CorsairLink sends nothing but two endpoint reads per second, and hubs stay in software mode under it indefinitely. Nobody has tested reads at long intervals or a firmware query alone.
- **Keep-alive in each tool.** OpenLinkHub and FanControl.CorsairLink send nothing dedicated beyond their one-second reads (and OpenLinkHub's colour frames). LinkPilot adds a speed rewrite at least every 10 s. FanControl.CorsairLink never sends hardware mode on exit and leaves the hub to fall back alone.
- **Not falling back.** No report was found of a LINK hub whose only host program died and whose speeds stayed frozen. If any other program keeps talking to the hub, software mode stays alive and the hub holds the last speeds (inferred, untested). Other Corsair devices stayed in their last state for hours after a Linux shutdown despite the heartbeat claim (reported).

### Mode transitions, sleep and resume

- **Entering software mode.** The lighting goes dark until colour data arrives (per the plugin's maintainer and OpenLinkHub's author). What the hub does with speeds before the first speed write is documented nowhere; every cooling tool writes speeds within a second or two, which hides it. On firmware 3.x fans kept spinning through about 20 s of software mode without a speed write (reported, no RPM given). The speeds the protocol notes record for this state match no tool's start values, and no source explains them. Three candidates, none supported by a source: the hub holds what hardware mode was commanding at the switch, it applies a default of its own, or the stored preset keeps running until the first speed write (inferred, untested).
- **Returning to hardware mode.** The lighting returns to the stored or default effect at once (reported). The stored cooling preset applies whenever no host is in control. How fast it takes over is not documented; the firmware ramps speed changes. A tool that sends hardware mode on exit (OpenRGB, OpenLinkHub, LinkPilot by default) takes the hub out of software mode for every other program too.
- **Sleep and resume.** No tool resumes in place; each one initialises from scratch. OpenLinkHub (Linux) stops its devices and sends hardware mode on suspend, and after resume waits 15 s by default and exits so that the service manager restarts it. FanControl closes and re-initialises every plugin on resume. LinkPilot drops its handle after 3 failed exchanges, rescans every 3 s and sends all speeds again. OpenLinkHubWindows detects sleep from a clock jump of 5 s or more, assumes the hub is back in hardware mode and starts over.
- **Failures seen.** After resume FanControl.CorsairLink's device-list read timed out (reported; improved in v1.7.0 and v1.7.3), and its maintainer notes that some devices need their USB connection cycled. At boot its first reads met status `03` because the hub was not yet in software mode (reported). After a USB drop it reopens the handle and sends software mode; OpenLinkHub waits 7 s after a re-plug before it initialises.

### Firmware 4.x

- Corsair's public list goes from 3.12.650 (2026-07-23) straight to 4.1.656 (2026-08-19). The 4.1.656 notes list three fixes: for LINK screen brightness lost after power-off, for device initialisation with more than 13 devices on one channel, and for configured fan settings being overwritten when a further pump or AIO is added. They say nothing about speed control or modes.
- No source reports a change to the `0x18` payload, to limits or to the mode commands on 3.x or 4.x. FanControl.CorsairLink needed only a version-check fix for 3.x, and OpenLinkHub has no firmware-dependent code apart from the channel count below major version 2; channels 13 and up belong to the hub's second port on firmware 2 and later. Fan and pump control work on 3.10.636 for two users, and on 4.1.656 one user controls QX fans with the released plugin (reported).
- **V2 devices (type `0x1A`)**, seen with TITAN II products on 4.1.656: model `00` fans, `01` AIO pump, `08` LCD module. OpenLinkHub identifies them by reading endpoint `0x40` with the channel number in frame byte `[1]`; the speed write is unchanged. Its 31 % rule does not cover them. FanControl.CorsairLink 1.8.0 skips the type as unsupported.

### Unknown, to be settled by a careful hardware check

1. What the hub does with fan and pump speed between entering software mode and the first speed write on 4.1.656: hold, a default, or the stored curve.
2. Whether the heartbeat fallback exists on 4.1.656, its time-out (10 s or 20 s), and which traffic resets it: endpoint reads, a firmware query, colour writes.
3. Whether speeds really stay frozen for as long as any traffic continues, for example when one process keeps streaming colour after its cooling logic has stopped.
4. What a TITAN does with a percent below 31, and with 0. On the predecessor 0 % latched a failure state until power-off.
5. The TITAN's percent-to-RPM map (31, 50, 70, 100) and the RPM at which the motherboard header raises an alarm.
6. For RX RGB fans: the percent at which they stop and restart on 4.1.656, and the RPM at 20 %.
7. Whether mode byte `01` (fixed RPM) works on 4.1.656 for these fans, and what the hub does when it is sent for a pump.
8. Whether a write that lists only some channels leaves the others unchanged, and which status codes other than `00` and `03` a speed write can return.
9. What a given hub's Device Memory holds, and so what hardware mode does there. It can only be observed indirectly, because the hub refuses reads in hardware mode.
10. Across sleep and resume: whether the hub is in hardware mode afterwards, whether handles survive, and how long it takes to answer.
11. The storage endpoints for hardware-mode cooling. Nothing is documented.

## Sources

- **LibreHardwareMonitor**: library source at tag v0.9.6 (chip detection, the NCT668x chip class, controls, mutexes, PawnIO wrapper, AMD GPU code), the 0.9.6 package assembly (disassembled to confirm the restore), the differences up to 0.9.7-pre785, the fix and its pull request, issues, and NuGet package metadata.
- **Fred78290/nct6687d** (out-of-tree Linux driver): driver source, README and pull requests. **Linux kernel**: the nct6683 driver and its documentation.
- **PawnIO.Modules**: the LpcIO module source. **FanControl**: README, release notes, issues and discussions, and the library version bundled with one installed copy.
- **OpenLinkHub**: the LINK System Hub driver, temperature profiles, device database, sleep monitor, README, two commits and issues; a 2024 sketch by its author.
- **FanControl.CorsairLink**: the LINK hub device code, data writer and its unit test, options, plugin, README, releases and issues.
- **LinkPilot** and **OpenLinkHubWindows**: protocol, hub and controller code, settings and READMEs. **OpenRGB**: the CorsairICueLink controller and two issues. **liquidctl**: the Commander Core protocol notes and driver.
- **Corsair**: hub firmware and iCUE release notes, help articles (among them the Hydro X default curves), guides and product pages; forum threads as search summaries only. **hwbusters**: review of the TITAN II 360 RX LCD. **MSI** and **AMD** product pages, as search summaries.

## Attribution

> Nuvoton NCT668x fan-control details were learned from public source and documentation of LibreHardwareMonitor (MPL-2.0), the nct6687d Linux driver (Fred78290/nct6687d; licence not recorded in the research), the Linux kernel's nct6683 driver (GPL-2.0) and PawnIO.Modules (LGPL-2.1), and from the public README and issue tracker of FanControl (closed source). Corsair iCUE LINK speed-control details were learned from public documentation and source of OpenLinkHub (GPL-3.0-or-later), FanControl.CorsairLink (no licence), LinkPilot and OpenLinkHubWindows (GPL-3.0), OpenRGB's CorsairICueLink controller (GPL-2.0-or-later), liquidctl's Commander Core protocol notes (GPL-3.0-or-later) and Corsair's own published documents. No code from these projects is included. Nuvoton, MSI, AMD, Corsair, iCUE and iCUE LINK are trademarks of their owners; DeadWare is not affiliated with or endorsed by any of them.
