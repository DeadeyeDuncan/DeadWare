# Fan control: hardware notes

Status: reference material for [the fan-control design](2026-11-25-fan-control-design.md). These are facts and interoperability conventions only. No code from another project is used or copied. Where a detail is unconfirmed, it says so.

Confidence marks: a plain statement was read in source code or an official document; a maintainer's statement is named as such. **(reported)** is a community report that was not checked. **(inferred, untested)** is a conclusion drawn from the sources. All of it is desk research of 2026-11-25 and 2026-11-26: nothing here was tried on hardware.

## Part 1: the board fan chip (Nuvoton NCT6683D, NCT6686D, NCT6687D)

"The library" is LibreHardwareMonitorLib 0.9.6 unless another version is named. "The Linux driver" is the out-of-tree nct6687d driver, "the kernel driver" is the mainline nct6683 driver, and "the module" is PawnIO's LpcIO module. The chip's datasheet is not public. The files, lines and commits behind the register-level statements are listed under "Where this was read" near the end of this part.

### Identification

- The chip is recognised by two Super I/O configuration registers, `0x20` (chip id) and `0x21` (revision). The library matches both bytes exactly. The Linux driver and the kernel driver join them into one 16-bit value, id first, and ignore its low four bits.

| Chip | The library: id, revision | Linux driver and kernel driver: the 16-bit value masked with `0xFFF0` |
|---|---|---|
| NCT6683D | `0xC7`, `0x32` | `0xC730` |
| NCT6686D | `0xD4`, `0x40` or `0x41` | `0xD440` |
| NCT6687D | `0xD5`, `0x92` | `0xD590` |

- The "DR" variant has the same chip id and revision. The library chooses it by board name alone. Its exact rule, applied after `0xD5` and `0x92` have matched: the chip is NCT6687DR when the board manufacturer is MSI and the SMBIOS baseboard product name contains `B840`, `B850`, `X870` or `Z890`, case ignored (0.9.7 prereleases add B860). The manufacturer counts as MSI when the SMBIOS baseboard manufacturer starts with "Micro-Star International" or is exactly "MSI", case ignored. Anything else is plain NCT6687D. The firmware of the DR boards uses other registers for the system fans. Every other board is plain NCT6687D, an MSI B650M included: the Linux driver's README says the same for B650 boards, and the kernel's tested-board list has an MSI B650M with this chip (for monitoring).
- The Linux driver has no substring rule. It switches to its alternative fan map only for a list of exact board names (B840, B850, B860, X870 and Z890 models), and a comment in its source says that B650, B660, X670, Z690 and Z790 boards use the default map and must not be put on that list.
- For a board with no model entry of its own the library shows 8 controls and 8 fans, named by index: 0 CPU Fan, 1 Pump Fan, 2–7 System Fan #1 to #6. Its identifiers carry a chip index, for example `/lpc/nct6687d/0/control/2`. The only MSI B650M with an entry of its own is the B650M GAMING PLUS WIFI.
- A board can have fewer headers than that. MSI's specification for the MAG B650M MORTAR WIFI lists five: CPU fan, pump fan and three system fans (read through a search summary). Controls 5–7 then have no header; writing them would set mode bits 5–7 and registers `0xA2D`–`0xA2F` with unknown effect (inferred, untested).
- On one plain NCT6687D board, an MSI MAG X670E Tomahawk WiFi, the fans could be read but did not respond to control (reported, closed unresolved).

### Reaching the registers

- The registers are in the chip's EC space (4096 of them, at the addresses in the tables below) and are reached through three I/O ports at the chip's base address + 4, + 5 and + 6. The base address is not fixed: it is read from the chip's Super I/O configuration registers.
- **Finding the base address.** The library does this at every open, inside one 100 ms wait for the ISA mutex; the Linux driver does it when it loads. The rows are in the library's order. An empty cell means no difference.

| Step | The library | The Linux driver, where it differs |
|---|---|---|
| Configuration ports | Two slots, tried in turn: register port `0x2E` with value port `0x2F`, then `0x4E` with `0x4F`. A configuration register is reached by writing its number to the register port and then reading or writing the value port | |
| Enter | Write `0x87` twice to the register port | |
| Identify | Read registers `0x20` (chip id) and `0x21` (revision) | |
| Allowed ports | Call the module's `ioctl_find_bars`, which records the ports the module will let through (see "Reaching the chip without the library"). It leaves another logical device selected | Not applicable |
| Select | Write `0x0B`, the logical device that holds the EC space, to register `0x07` | |
| Read | Read registers `0x60` (high byte) and `0x61` (low byte), wait 1 ms and read both again. The two readings must be equal | Waits 1–2 ms. Also rejects an address of 0 |
| Enable | Not done | Reads register `0x30`; if bit 0 is clear it sets the bit and logs that the data may be unusable |
| Exit | Write `0xAA` to the register port | Writes `0xAA` to the register port, then `0x02` to the register port and `0x02` to the value port. It adjusts and validates before it exits |
| Adjust | If the low three bits of the address are 5, clear them (some Fintek chips report the address that way) | |
| Validate | Reject an address below `0x100`, or with any bit of `0xF007` set | |

- The address that passes is the base, with no further offset. The Linux driver's README shows `0x0A20` in both of its examples (its sample sensor output and an ASRock board with an NCT6686D), which puts the three ports at `0x0A24`, `0x0A25` and `0x0A26`. Nothing says what a given board reports, so the address has to be read.
- **The three ports.**

| Port | Role |
|---|---|
| base + 4 | Page register. Takes the high byte of the register address. By the library's convention it reads `0xFF` while no access is in progress |
| base + 5 | Index register. Takes the low byte of the register address |
| base + 6 | Data register |

- **One register access** is a short sequence on these ports. The order of page, index and data is the same in every source. What happens before and after is not:

| | The library | The Linux driver and the kernel driver |
|---|---|---|
| Before | Read the page register until it reads `0xFF`, with a 1 ms sleep between reads. If 500 ms pass without `0xFF`, write `0xFF` to the page register and go on | Write `0xFF` to the page register, without reading it first. The kernel driver's comment describes that write as an unlock |
| Access | Write the page to base + 4, then the index to base + 5, then read or write base + 6 | The same |
| After | Write `0xFF` to the page register, which the source describes as freeing it for other instances | Nothing. The page register keeps the last page |

- The library's 500 ms time-out is silent: nothing is returned, thrown or logged, and the access goes ahead after the forced `0xFF`. The deadline is taken from the system clock, and the 1 ms is a requested sleep, so the real poll interval is the system's timer step (inferred, untested).
- A 16-bit value is two separate accesses, high byte first. No source latches the pair, so a value can change between the two reads (inferred, untested).
- Through PawnIO every port read or write is one call to the driver, so one register access costs the library at least five calls: one or more reads of the page register, two writes, the data transfer and the release.
- **Named mutexes** that cooperating Windows programs take, created with full access for everyone: `Global\Access_ISABUS.HTP.Method`, `Global\Access_PCI` and `Global\Access_EC` (the library has more, for Razer and USB devices). **`Global\Access_ISABUS.HTP.Method` is the one that guards this bus** ("the ISA mutex" below); the comments of PawnIO's LpcIO module name the same mutex. The library counts an abandoned mutex, one whose owner died, as acquired. It also counts a mutex that it could neither create nor open as acquired: with no mutex object every wait reports success.
- A program that ignores the mutex can break another program's chip detection (inferred, untested). For intermittently missing NCT6687D sensors FanControl's maintainer suspected another tool's fan feature (reported).

### Registers and the write sequence

| Register | Meaning |
|---|---|
| `0xA00` | Mode, one register for all headers. Bit i = 1: header i is under manual (software) control. Bit i = 0: firmware automatic. Bit 0 CPU, bit 1 pump, bits 2–7 system fans 1–6. |
| `0xA01` | Configuration handshake, shared by all headers: `0x80` = request, `0x40` = done. |
| `0xA28` + i | Duty command for header i, 0–255. |
| `0x160` + i | Duty read-back for header i, 0–255. |
| `0x140` + 2·n | Speed of tachometer input n in RPM, 16 bits: high byte at this address, low byte at the next. Details under "Fan speed and duty readings". |
| `0x852`, `0x853` | A 13-bit fan counter that the library reads as a seventeenth fan. Details in the same place. |
| `0xCF8` | Fan engine status (phase, invalid, check done, lock), named in the Linux driver, which steers its handshake by it. Bits under "How the Linux driver does it". The library never reads it. |
| `0x180` | Monitor configuration. Bit 7 switches monitoring on. The library at every open, and the Linux driver at load, read the register and write it back with bit 7 set if the bit was clear. |
| `0x1BB`–`0x1BF` | Written with `0x61`, `0x62`, `0x63`, `0x64` and `0x65`, in that order and without a read first, by the library at every open and by the Linux driver at load. The source comments say that this enables the chip's own voltage inputs. |
| `0x604`, `0x605`, `0x606` | EC firmware build year, month and day. The Linux driver reads them once for its log line; the library does not. |
| `0x608`, `0x609` | EC firmware version, high and low part, read the same way. |

The library sets the duty of header i in these steps, holding the ISA mutex from the first to the last:

1. Write `0xA01` = `0x80`, then wait 50 ms.
2. Read `0xA00`, set bit i, write it back.
3. Write the duty to `0xA28` + i.
4. Write `0xA01` = `0x40`, then wait 50 ms.

- It makes no status check, read-back or retry. One write holds the bus, and blocks the caller, for at least 100 ms.
- Only bit i and `0xA28` + i are touched, so control is per header. In a Linux test on an MSI board one channel was held in manual mode while the other seven stayed on the BIOS curve (reported). What the firmware does with the other headers while the shared handshake is open is not documented.
- The duty is one byte. The library multiplies a percentage by 2.55 and truncates (100 gives 255, 50 gives 127), and for the read-back divides by 2.55 and rounds to a whole percent. A written 0 stops a fan that supports it (Linux driver README). No firmware minimum is documented.
- **The duty stays in the chip without rewriting.** The library writes only when a mode or value changes; its update pass only reads. The Linux README describes manual mode as fixing the fan at its current speed, and the Linux driver gained a watchdog of its own because the last manual value otherwise stays active without limit (reported). No report was found of the firmware taking a header back at run time while its manual bit is set. Boot and resume are different (below).

### Fan speed and duty readings

- **Tachometer registers.** `0x140` + 2·n holds the speed of input n as a 16-bit RPM figure, high byte first. Neither the library nor the Linux driver applies a divisor or any other conversion, and neither treats any value as "no reading": the number is shown as it is. An input with no fan shows 0 RPM in the Linux README's sample output.
- **Which input is which header.** On a plain NCT6687D board the library's default names and the Linux driver's default map agree, and input n belongs to the same header as control i = n:

| n | Register | Header |
|---|---|---|
| 0 | `0x140` | CPU fan |
| 1 | `0x142` | Pump fan |
| 2–7 | `0x144`–`0x14E` | System fans 1–6 |

- For the B650M GAMING PLUS WIFI the library names inputs 0–4 only: CPU fan, pump fan and system fans 1–3. Its chip class also reads inputs 8–15 (`0x150`–`0x15E`), which only the entries of other boards show. The kernel driver's header gives the family 16 fan inputs as well.
- **The 13-bit counter.** `0x852` (high byte) and `0x853` (low byte) are read by the library only, as input 16, for a seventh system fan on an MSI MEG Z790 GODLIKE MAX. On NCT6687D the pair `0xFF`, `0xF8` means no fan or a stopped fan and gives 0 RPM. Otherwise the count is the high byte × 32 plus the low five bits of the low byte. A count from `0x15` to `0x1FFE` gives 1,350,000 ÷ count RPM, a count of `0x1FFF` gives 0 RPM, and a count below `0x15` gives no reading.
- **Duty read-back.** `0x160` + i is one byte, 0–255. The library shows it as a percentage (above); the Linux driver shows the byte unchanged.
- **Mode read-back.** Bit i of `0xA00`. The Linux driver reads the register with every update and reports each header as manual or automatic. The library has no sensor for it (see `GetReport()` below).
- **How often.** The Linux driver reads the chip at most once a second, however often it is asked. The library reads on every `Update()` call.

### How the Linux driver does it

These are the steps of the nct6687d driver at the commit named under "Where this was read" (2026-11-15). It holds one lock of its own across each operation, sensor reads included. Every wait polls about once a millisecond and gives up after one second.

The fan engine status register `0xCF8`:

| Bit | Mask | Name in the driver | How the driver uses it |
|---|---|---|---|
| 3 | `0x08` | configuration phase | Set while the EC is in a configuration phase |
| 4 | `0x10` | configuration invalid | Set when the EC has rejected the configuration it was just given |
| 5 | `0x20` | check done | Set when the EC has finished checking that configuration |
| 6 | `0x40` | lock | Set while the fan configuration registers are locked. Lock clear together with phase set means they are open |

- The names and meanings are the driver's own; no datasheet backs them. Bits 0–2 and 7 are not named.

Opening the handshake:

1. Read `0xCF8`. If the lock bit is clear and the phase bit is set, the registers count as open already: nothing is written and the driver goes on.
2. Otherwise wait until the phase bit of `0xCF8` and the request bit (`0x80`) of `0xA01` are both clear. On a time-out the operation fails.
3. Write `0xA01` = `0x80`.
4. Wait until `0xCF8` shows the lock bit clear and the phase bit set. On a time-out the operation fails.

Closing the handshake:

1. Read `0xCF8`. If the lock bit is set or the phase bit is clear, the registers count as closed already: the step fails and nothing is written.
2. Write `0xA01` = `0x40`. On NCT6683D the driver writes `0x00` instead, because `0x40` was reported to switch that chip to automatic and to clear `0xA00` and `0xA28`–`0xA2F`; its comment says that nobody has tested this again.
3. Wait until `0xCF8` shows the check-done bit. A time-out counts as a failure.
4. The step also fails when that last reading has the invalid bit set, or the lock bit still clear.

The three operations on header i:

| Operation | Steps, in order |
|---|---|
| Set a duty | Open the handshake; if that fails, stop. On the first change to this header, save its mode bit and the content of `0xA28` + i. Read `0xA00`, set bit i and write it back. Read `0xA28` + i and write the duty only if it differs; after a write, read it back and compare. Close the handshake. Read `0x160` + i and `0xA00` and check that bit i is set. A failed step fails the operation, but the later steps still run |
| Manual mode, no new duty | If nothing is saved for this header yet: open the handshake, save the mode bit and the content of `0xA28` + i, close the handshake. Then, outside the handshake: read `0xA00`, set bit i, write it back, and read `0x160` + i and `0xA00` to check the bit. The duty command is not written; the README describes the result as fixing the fan at its current speed |
| Back to automatic | If a state is saved for this header: open the handshake, write the saved duty command back to `0xA28` + i (only if it differs, with read-back), close the handshake. Then, outside the handshake: read `0xA00`, clear bit i, write it back, read `0x160` + i and `0xA00` to check that the bit is clear, and forget the saved state |

- The driver reads and writes the duty command only inside the handshake, and its comments call the fan registers locked outside it. The library reads `0xA28` + i outside the handshake when it saves a header's state. Both write the mode register inside the handshake for a duty write and outside it for a restore.
- The driver follows `0xCF8` where the library sleeps a fixed 50 ms twice, and it verifies by read-back where the library does not.
- The kernel driver has the shortest form: `0xA01` = `0x80`, a pause of 1–2 ms, the duty to `0xA28` + i, `0xA01` = `0x40`. It sets no mode bit and checks nothing.
- When the driver is unloaded it restores every header that has a saved state, in the same way as its watchdog (under "Failsafes").

### Returning a header to firmware (BIOS) control

- Clearing bit i of `0xA00` returns header i to the firmware. For an automatic-mode request the Linux driver first puts the duty command it saved back into `0xA28` + i, inside a handshake, and then clears the bit outside the handshake and reads `0xA00` again to verify (the steps are under "How the Linux driver does it"). In a test of that driver on the CPU fan channel of an MSI board the BIOS curve took over at once (reported, one test).
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
- PawnIO is a driver that is installed separately. A client opens its device, loads the signed module `LpcIO.bin` into that handle and then calls the module's functions by name. The module lets through only the Super I/O ports and the base address ranges it finds, and asks its callers to hold the ISA mutex. The details follow.
- **The device.** The driver's device is `\Device\PawnIO`, which a Windows program opens as `\\?\GLOBALROOT\Device\PawnIO`; that is the path the library uses. The older name `\\.\PawnIO` exists in driver 2.0.0, is gone in 2.1.0 and is back in 2.2.0, marked as deprecated; the library used it up to 0.9.5. Only SYSTEM and administrators have access to the device, so a client has to run elevated. The 2.2.0 release notes say that the device can now be opened to others and advise against it.
- **Two ways to talk to it.** PawnIO's own documentation tells a client to load `PawnIOLib.dll` from the install folder and to call its `pawnio_open`, `pawnio_load`, `pawnio_execute` and `pawnio_close`. The install folder is the registry value `InstallLocation` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO`, with `%ProgramFiles%\PawnIO` as the fallback. The library does not use that DLL: it opens the device itself and sends the same two control codes. Both routes come to this:

| Action | Control code | Input buffer | Output buffer |
|---|---|---|---|
| Load a module | `0xA1B22084`: device type 41394, function `0x821`, buffered, any access | The whole module blob, signature included | None |
| Execute a function | `0xA1B22104`: function `0x841` | A 32-byte field with the function name in ASCII, filled up with zero bytes (31 characters at most), then the input values, 8 bytes each, least significant byte first | Room for the output values, 8 bytes each. On success the driver reports the whole buffer as written |
| Ask for the driver's version | `0xA1B22184`: function `0x861`, drivers 2.1.0 and later | None | Exactly 4 bytes: major × 65536 + minor × 256 + patch |

- **Opening.** The library passes the value 3 as the access it asks for, which is read-data plus write-data, with read and write sharing. PawnIOLib asks for generic read and write. The control codes are defined for any access.
- **One module per handle.** A second load on a handle that already has a module is refused. The module's state, for LpcIO the selected slot and the list of allowed ports, belongs to the handle and ends when the handle is closed. The library opens one handle for each slot it probes, closes it again where it finds no chip, and keeps the handle of a found chip for as long as the chip object lives. Any other handle, in the same process or in another, starts with no slot and no allowed ports.
- **Load.** A blob is a 4-byte signature length (least significant byte first), the signature and then the module code. The driver checks the signature (RSA with PKCS #1 padding over a SHA-256 hash of the code) against a public key built into it and refuses a blob that fails, so the official driver loads only modules signed with the PawnIO project's key. It then runs the module's start function and returns that function's status. LpcIO's start function refuses every processor architecture but x64.
- **Execute.** The driver refuses a call when the handle has no module, when the input is shorter than the 32-byte name field, when the name has no zero byte inside the field or does not begin with `ioctl_`, and when the module has no public function of that name. Otherwise the function runs with the input values and the output buffer, and its return value, an NTSTATUS, becomes the result of the call: when a module function refuses, the whole call fails. Calls on one handle are serialised by a lock inside the driver. Calls on different handles are not, and that is what the ISA mutex is for.

| Status, by its NTSTATUS name | When |
|---|---|
| `STATUS_ALREADY_INITIALIZED` | Load, on a handle that already has a module |
| `STATUS_INVALID_PARAMETER` | Load: the blob is shorter than its signature length says. Execute: no module on the handle, an input shorter than 32 bytes, a name with no zero byte or without the `ioctl_` prefix. LpcIO: the wrong number of values, or a slot other than 0 or 1 |
| The signature check's own error | Load: the signature does not verify |
| `STATUS_NOT_SUPPORTED` | Load of LpcIO on a processor that is not x64 |
| `STATUS_OBJECT_NAME_NOT_FOUND` | Execute: the module has no function of that name |
| `STATUS_DEVICE_NOT_READY` | LpcIO: no slot has been selected |
| `STATUS_ACCESS_DENIED` | LpcIO: the port is not on the allowed list |
| `STATUS_NOT_FOUND` | `ioctl_find_bars`: the chip id register reads `0x00` or `0xFF` |
| `STATUS_UNSUCCESSFUL` | The module could not be started, or its code failed while it ran |

- A client sees these as the Windows error codes that the system maps them to. **The library's wrapper hides all of them.** If the device cannot be opened or the load fails, it carries on with an unloaded module, and an unloaded module and a failed call both return zeros. A refused port read therefore looks like a port that reads 0.
- **The LpcIO module's functions.** The four named above, and three more that the library uses for the configuration registers:

| Function | Input values | Output values | What it does and what it refuses |
|---|---|---|---|
| `ioctl_select_slot` | 1: the slot | None | Forgets the earlier slot and every allowed port, then selects slot 0 (ports `0x2E` and `0x2F`) or slot 1 (`0x4E` and `0x4F`). Any other number is refused as an invalid parameter and leaves no slot selected |
| `ioctl_find_bars` | None | None | Refused as not ready without a slot. Reads configuration register `0x20` and returns not found when it reads `0x00` or `0xFF`, so the chip must already be in configuration mode. Then it selects each logical device number from `0x00` to `0xFE` in turn and, where the chip accepts the number, reads the 16-bit values at `0x60` and `0x62`; it waits 1 ms and makes the whole pass again. A value becomes an allowed base when both passes agree and it is not 0, not `0xFFFF` and not below `0x100`; a value whose low three bits are 5 is recorded with those bits cleared. It does not put the logical device selection back |
| `ioctl_pio_inb` | 1: the port (low 16 bits) | 1: the byte read | Reads one byte from an I/O port. Refused as not ready without a slot, and as access denied when the port is not allowed |
| `ioctl_pio_outb` | 2: the port, the value | None | Writes one byte, the low 8 bits of the value, to an I/O port, with the same two refusals |
| `ioctl_superio_inb` | 1: the register (low 8 bits) | 1: the byte read | Writes the register number to the slot's register port and reads the value port. Refused only without a slot |
| `ioctl_superio_inw` | 1: the register | 1: a 16-bit value | Reads the register as the high byte and the next register as the low byte. Refused only without a slot |
| `ioctl_superio_outb` | 2: the register, the value | None | Writes the register number to the register port and the value to the value port. Refused only without a slot |

- The number of values is checked: a function refuses a call whose number of input values, or of output values, is not the number in the table. Where the table says none, the number is not checked.
- **Allowed ports.** The two ports of the selected slot are always allowed. After `ioctl_find_bars` a port is also allowed when its address with the low three bits cleared equals a recorded base, which means the eight ports that start at each base. For a base of `0x0A20` that is `0x0A20`–`0x0A27`, which covers base + 4, + 5 and + 6. The list has room for 128 bases.
- The module has no function that performs a whole register access. The page, index and data sequence is always several calls, and only the ISA mutex and the page-register convention protect it.
- **Later module sets change the rules.** The source file was compared at the tags named here:

| Module set | LpcIO's rules |
|---|---|
| 0.1.6, 0.2.0, 0.2.2, 0.2.7 | As above. The source file is identical at these four tags |
| 0.2.8, 0.2.9 | Ports `0x25C` and `0x25D` are allowed as well, for an ASUS EC |
| 0.2.10, 0.2.11, 0.2.12 | I/O ports `0xCF8`–`0xCFF` (PCI configuration, not the EC register `0xCF8`) are never allowed, and a base whose eight ports would touch them is not recorded. The list stops at 128 bases |
| After 0.2.12, not released yet | The low three bits of every base are cleared, not only when they are 5 |

- **The mutex.** The documentation comment of every one of the seven functions says that the caller should first hold `\BaseNamedObjects\Access_ISABUS.HTP.Method`, which is the kernel's name for `Global\Access_ISABUS.HTP.Method`. The module cannot check this, and nothing enforces it.
- **The library's order of calls** for one slot, all inside one hold of the ISA mutex: `ioctl_select_slot`; `0x87` twice to the register port with `ioctl_pio_outb`; registers `0x20` and `0x21` with `ioctl_superio_inb`; `ioctl_find_bars`; `0x0B` to register `0x07` with `ioctl_superio_outb`; register `0x60` twice with `ioctl_superio_inw` (and once the word at `0x23`, which matters for Fintek chips only); `0xAA` to the register port with `ioctl_pio_outb`. After that, every register access uses only `ioctl_pio_inb` and `ioctl_pio_outb` on base + 4, + 5 and + 6, on the same handle.
- **The blob.** It is an embedded resource of the library's assembly, named `LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin`. The name was read from the resource list of the 0.9.6 package's net10.0 and net472 assemblies, without running the library. The blob also ships in the PawnIO.Modules releases (LGPL-2.1-or-later). In 0.9.6 it is 17,172 bytes with SHA-256 `3dcf8b2bc80ff642d97c4608511a818642b5bf315ff53df3df393d043e71d101`, the same bytes as the file in the library's repository at the tag: a 512-byte signature and 16,656 bytes of code. Its name table holds the seven function names above and the names of five functions of the driver: port read, port write, sleep, debug print and the architecture query. The file entered the library's repository on 2025-11-01, when 0.1.6 was the newest module set, and had not changed by 0.9.6; a note beside it names the 0.1.6 release. The blob in the prereleases differs from the one in 0.9.6.
- **The installed version.** The library reads the registry value `DisplayVersion` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO`, in the process's own registry view first and in the 64-bit view second, and treats PawnIO as not installed when neither gives a version. Its application asks for an update below 2.0.0. The library itself checks nothing and loads the module whatever the version is. Drivers from 2.1.0 on also answer the version control code in the table above; 2.0.0 does not have it. PawnIOLib's own version call returns a fixed 2.0.0 in the current source, so it is not the driver's version.
- **The driver version the module needs: no source states one.** What the sources do show: the 0.9.6 blob names only the five driver functions listed above, and all five are in the driver's source at 2.0.0; the module's source checks no version; the library's application accepts 2.0.0; and the 0.1.6 module set dates from 2025-09, when 2.0.x was the only 2.x driver (2.1.0 followed in 2026-02, 2.2.0 in 2026-03). For the 0.2.11 modules nothing was found either. Their include files declare the same driver functions as at 0.1.6 and LpcIO's source calls the same five, but the 0.2.11 blob itself was not inspected.
- **Licences, as the projects state them.** The driver is GPL-2.0-or-later with an added permission for LGPL software and for independent programs that talk to it only through device control calls. PawnIOLib and the modules are LGPL-2.1-or-later. The official signed binaries are proprietary, and their installer may be redistributed. PawnIO's documentation recommends sending users to its website for the driver.

### Failsafes

- **Watchdog.** No description was found of a chip or firmware watchdog that returns a header to its curve when the host stops writing. The Linux driver added one of its own: a lease of 1–300 s that user space arms and keeps renewing, after which the driver restores every channel it changed. It lives in the driver, so it covers a dead control program and not a dead host (inferred, untested). How it works:
  - **Arming.** A program writes a number of seconds, 1 to 300, to the driver's `fan_control_watchdog` file. Every such write sets a new deadline that far ahead. Writing 0 disarms it, and a number above 300 is refused. The driver never arms it by itself, and a duty write does not renew it.
  - **The decision.** The driver restores when a deadline passes without a new write. It looks at nothing else: no temperature, no fan speed, and no check of whether the controlling program is alive. A renewal that arrives while the restore is still waiting for the driver's lock counts, and the restore is dropped.
  - **What the restore does.** For every header with a saved state, which means every header the driver has changed since that header's last automatic-mode request: write the saved duty command back to `0xA28` + i inside a handshake, then write the saved mode bit outside it and verify it by read-back. That is the state from before the driver's first change, not automatic mode as such: a header that was already manual when the driver first touched it goes back to manual with its old duty command.
  - **Afterwards.** When every header is back, the lease is disarmed and the driver logs a warning. A header that could not be restored is tried again every second, and the lease stays armed until all are back.
  - **Sleep.** The lease does not run while the system is suspended, and it continues with its remaining time after resume.
  - The same restore runs for every header with a saved state when the driver is unloaded.
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

### Where this was read

The register-level statements of this part were read in the files below on 2026-11-25 and 2026-11-26, each at a fixed commit. Lines a–b of a file are at `https://github.com/<repository>/blob/<commit>/<file>#La-Lb`.

| Source | Repository | Version read | Commit |
|---|---|---|---|
| The library | `LibreHardwareMonitor/LibreHardwareMonitor` | tag `v0.9.6` | `3d331e3370efb858411f19511373eff65a218701` |
| The module | `namazso/PawnIO.Modules` | tag `0.1.6` | `4aa792beb020a14c8261072e9786d2dfb38489d9` |
| The PawnIO driver, 2.2.0 | `namazso/PawnIO` | branch `master`, whose build file says 2.2.0 | `9d52965895588b0ffa3703b72eec75ba19f4ccc0` |
| The PawnIO driver, 2.1.0 | `namazso/PawnIO` | tag `2.1.0` | `e1043d7750f501b6b0d3ac0a71bd5b9406cd17b6` |
| The PawnIO driver, 2.0.0 | `namazso/PawnIO` | tag `2.0.0` | `92bf4e6287c12e0776c2cf13ee466080b39be95a` |
| The Linux driver | `Fred78290/nct6687d` | branch `main` of 2026-11-15 | `3e4861052c5d7931e66b6cb6a6484e733ff8a419` |
| The kernel driver | `torvalds/linux` | tag `v6.12` | use the tag name |

The driver's tag `2.2.0` (`5cdf470831fdfff3f7f1d06363ca6b230f3bf35a`) was compared with the `master` commit above: the control codes are identical and nothing stated here about loading and executing differs. One detail does: PawnIOLib at that tag copies at most 30 characters of a function name.

The library. Paths are below `LibreHardwareMonitorLib/` except the last one:

| File | Lines | What |
|---|---|---|
| `Hardware/Motherboard/Lpc/LpcIO.cs` | 21–32 | The 100 ms wait for the ISA mutex around detection |
| | 65–82, 778–780 | The two slots and their ports |
| | 100–107, 760–762 | Enter, then chip id and revision from `0x20` and `0x21` |
| | 320–327, 401–405, 409–427 | NCT6683D, NCT6686D, NCT6687D and the DR rule |
| | 466–472, 771 | Allowed ports, logical device `0x0B`, the base address read twice |
| | 481–510 | Exit, comparison of the two readings, adjustment and validation |
| | 540–546 | The address handed to the chip class unchanged |
| `Hardware/Motherboard/Lpc/LpcPort.cs` | 13–19, 61–75, 120 | One module handle per slot; select, enter and exit |
| `Hardware/Motherboard/Lpc/Nct677X.cs` | 57–64 | Duty read-back, duty command, mode and handshake registers |
| | 401–407, 451–475 | Fan and control counts, the tachometer registers, the counter's limits |
| | 481–494 | Monitor configuration and the five voltage-enable writes |
| | 588–658 | The duty write |
| | 841–866, 868–905, 907–919 | The 13-bit counter, the fan reads, the duty read-back |
| | 1097–1140, 1142–1185, 1399–1403 | One register read, one register write, the three port offsets and `0xFF` |
| | 1224–1279 | Saving and restoring a header |
| `Hardware/Motherboard/Identification.cs` | 79–81 | What counts as MSI |
| `Hardware/Motherboard/Motherboard.cs` | 40–41 | Manufacturer and model come from the SMBIOS baseboard strings |
| `Hardware/Motherboard/SuperIOHardware.cs` | 500–530, 583–625 | Names for the B650M GAMING PLUS WIFI and for a board with no entry |
| `Hardware/Mutexes.cs` | 19–55, 119–136 | How the mutexes are made and when a wait counts as acquired |
| `PawnIo/PawnIo.cs` | 16–19, 158–162 | The two control codes |
| | 23–42 | The installed version from the registry |
| | 65–90, 98–121 | Opening and loading; executing, with the buffer layout |
| `PawnIo/LpcIO.cs` | 6, 9–50 | The resource name; the seven functions and their value counts |
| `Resources/PawnIo/README` | all | Names module set 0.1.6 |
| `LibreHardwareMonitor/UI/MainForm.cs` | 169–183 | The application's version prompt |

Also from the library's repository: pull request 2222 (the device path changed from `\\.\PawnIO`), and the history of `LibreHardwareMonitorLib/Resources/PawnIo/LpcIO.bin` up to the tag, which is one commit of 2025-11-01. From the 0.9.6 NuGet package: the resource list and the resource bytes of `LibreHardwareMonitorLib.dll` for net10.0 (names also for net472), read with a reflection-only load.

The module, the PawnIO driver and its documents:

| File | Lines | What |
|---|---|---|
| Module: `LpcIO.p` | 20–39, 41–58 | State kept per handle; configuration register access |
| | 60–82, 84–113 | When a base is recorded; the sweep over the logical devices |
| | 115–139, 141–154 | `ioctl_select_slot`, `ioctl_find_bars` |
| | 156–170 | Which ports are allowed |
| | 172–191, 193–211 | `ioctl_pio_inb`, `ioctl_pio_outb` |
| | 213–264, 266–271 | The three configuration-register functions; the start function |
| | 122, 147, 179, 198, 220, 238, 254 | The mutex that callers should hold |
| Module: `include/core.inc` | 157–162 | The check on the number of values |
| Module: `LpcIO.p` at other tags | whole file | Compared at `0.2.0`, `0.2.2`, `0.2.7`, `0.2.8`, `0.2.9`, `0.2.10`, `0.2.11`, `0.2.12` and on `main` (`34fb98184f3f7df456f1a6310d6374bda4261737`); `include/native.inc` compared at `0.1.6` and `0.2.11` |
| Driver 2.2.0: `PawnIO/include/pawnio_um.h` | 49–58 | Device type, device names, the three control codes |
| Driver 2.2.0: `PawnIO/src/driver.cpp` | 210–303 | One module per handle; load, execute and version requests |
| Driver 2.2.0: `PawnIO/src/vm.cpp` | 503–509, 793–873 | The lock of a loaded module; signature check and load |
| | 884–925, 927–990 | The start function's status; execute, with the name rules and the buffer layout |
| Driver 2.2.0: `PawnIO/src/signature.cpp` | 118–159 | The signature algorithm |
| Driver 2.2.0: `PawnIO/src/natives_impl_windows.cpp` | 433, 437 | The port write and read that the module calls |
| Driver 2.2.0: `PawnIO/PawnIO.inf.in` | 38–39 | Who may open the device |
| Driver 2.2.0: `PawnIOLib/PawnIOLib.cpp` | 133–136, 146–162, 172–184, 212–263 | PawnIOLib's version, open, load and execute |
| Driver 2.2.0: `README.md` | 5–51 | The driver's licence and its added permission |
| Driver 2.1.0: `PawnIO/include/pawnio_um.h`, `PawnIO/src/driver.cpp` | 49–56; 91–108, 188 | One device name only; access rights; the version request |
| Driver 2.0.0: `PawnIO/ioctl.h`, `PawnIO/driver.cpp` | 49–59; 71–114, 116–204 | Both device names, no version request; access rights; the same load and execute rules |
| Driver 2.0.0: `PawnIO/vm.cpp` | 395–467, 609–676 | The driver functions on offer; execute |

Documents without line numbers: the modules wiki page "Using PawnIO Modules" (`https://github.com/namazso/PawnIO.Modules/wiki/Using-PawnIO-Modules`), the release list of `namazso/PawnIO.Modules` (dates of the module sets) and the release notes of `namazso/PawnIO.Setup` (2.0.0 to 2.2.0).

The Linux driver and the kernel driver:

| File | Lines | What |
|---|---|---|
| Linux driver: `nct6687.c` | 1 | The licence line |
| | 118–129, 150–171 | Configuration registers and chip ids; enter and exit |
| | 177–178, 237–240 | The EC ports |
| | 211–235 | Register addresses and the `0xCF8` bits |
| | 340–349, 426–503 | The default fan map; the list of boards with the other map |
| | 646–675 | One register read, one register write |
| | 805–859 | Fan, duty and mode reads; the once-a-second limit |
| | 861–944 | Opening and closing the handshake |
| | 946–1033 | The mode bit, its check, saving and restoring |
| | 1035–1065, 1067–1106 | The duty write; the mode change |
| | 1108–1192 | The watchdog |
| | 1349–1392 | Monitor configuration at load; the restore at unload |
| | 1440–1479 | Suspend and resume |
| | 1505–1598 | Detection and the base address |
| Linux driver: `README.md` | 77–106, 143–157, 340–394, 480–490 | The sample output, the ASRock board, the duty, mode and watchdog files, the B650 note |
| Linux driver: `LICENSE`, `debian/copyright` | all | GPL version 2 text; "GPL-2+" |
| Kernel driver: `drivers/hwmon/nct6683.c` | 1, 13–16 | The licence line; 16 fan inputs for the family |
| | 65–74, 97–119, 126–131 | Configuration registers and chip ids; enter and exit; the EC ports |
| | 141–143, 168–170, 182–187 | Fan, duty and handshake registers; the firmware version registers |
| | 499–521 | One register read, one register write |
| | 923–943, 957–961 | The duty write; writable on Mitac boards only |

### Still not settled on paper

Looked for in this pass and not found in any source:

1. **Which PawnIO driver version the module needs.** No source states one, for the 0.1.6 module set or for any later one. The evidence under "Reaching the chip without the library" points to 2.0.0 being enough for LpcIO; it is evidence and not a statement.
2. **What `0xFF` in the page register means to the chip.** The library waits for it and takes it as a free bus. The Linux driver and the kernel driver write it without looking, and the kernel driver's comment calls that an unlock. Without the datasheet it is not known whether the board firmware ever holds the page register itself, or what the library's forced access after 500 ms does to another program's access in progress.
3. **Whether the duty command can be read or written outside the handshake.** The Linux driver touches `0xA28` + i only inside it. The library reads it outside when it saves a header's state. Nothing says what such a read returns.
4. **The `0xCF8` bits on this board.** Their meaning rests on the Linux driver alone, and no source shows them on a B650 board. Whether the library's two fixed 50 ms waits are always long enough is not stated anywhere either: the library never reads `0xCF8`.
5. **Tachometer details.** Whether the two bytes of a speed can be read torn, and what an empty or stalled header reads on a given board beyond the 0 RPM of one sample output.
6. **The base address on the board in question.** `0x0A20` appears in examples only.
7. **Whether `ioctl_find_bars` is harmless on every chip.** It writes the logical-device select register about 510 times and leaves it changed. The library runs it at every open; no document covers its side effects.
8. **The module release archives were not downloaded.** The 0.9.6 blob was matched byte for byte to the file in the library's repository, and to the 0.1.6 source by its function names and by the file's history, but not to the 0.1.6 release archive itself. The 0.2.11 blob was not inspected at all.
9. **The "done" value on NCT6683D.** The Linux driver writes `0x00` and the library `0x40`, and the Linux source says that its choice has not been tested again.

Every source listed under "Where this was read" loaded; none had to be left out. The datasheet is not public. The PawnIO website and the other pages of the modules wiki say nothing about the client interface.

Marked above and still open, because only a datasheet or a careful hardware check can close them: the effect of writing controls 5–7 on a board with five headers; what the firmware does with the other headers while the shared handshake is open; whether there is a firmware minimum duty; what happens at over-temperature in manual mode; which of boot, resume and hibernate clear the mode bits on a B650 board; and every other statement that keeps its (inferred, untested) mark.

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

- **LibreHardwareMonitor**: library source at tag v0.9.6 (chip detection, the NCT668x chip class, controls, mutexes, PawnIO wrapper, AMD GPU code), the 0.9.6 package assembly (disassembled to confirm the restore; its resource list read for the module blob), the differences up to 0.9.7-pre785, the fix and its pull request, issues, and NuGet package metadata.
- **Fred78290/nct6687d** (out-of-tree Linux driver): driver source, licence file, README and pull requests. **Linux kernel**: the nct6683 driver and its documentation.
- **PawnIO.Modules**: the LpcIO module source at the tags 0.1.6 to 0.2.12, its include files, the release list and the wiki. **PawnIO**: the driver and PawnIOLib source at 2.0.0, 2.1.0 and 2.2.0, and the setup project's release notes. **FanControl**: README, release notes, issues and discussions, and the library version bundled with one installed copy.
- **OpenLinkHub**: the LINK System Hub driver, temperature profiles, device database, sleep monitor, README, two commits and issues; a 2024 sketch by its author.
- **FanControl.CorsairLink**: the LINK hub device code, data writer and its unit test, options, plugin, README, releases and issues.
- **LinkPilot** and **OpenLinkHubWindows**: protocol, hub and controller code, settings and READMEs. **OpenRGB**: the CorsairICueLink controller and two issues. **liquidctl**: the Commander Core protocol notes and driver.
- **Corsair**: hub firmware and iCUE release notes, help articles (among them the Hydro X default curves), guides and product pages; forum threads as search summaries only. **hwbusters**: review of the TITAN II 360 RX LCD. **MSI** and **AMD** product pages, as search summaries.

## Attribution

> Nuvoton NCT668x fan-control details were learned from public source and documentation of LibreHardwareMonitor (MPL-2.0), the nct6687d Linux driver (Fred78290/nct6687d; GPL-2.0-or-later), the Linux kernel's nct6683 driver (GPL-2.0-or-later), PawnIO (the driver GPL-2.0-or-later with an added permission for clients of its device control interface, PawnIOLib LGPL-2.1-or-later) and PawnIO.Modules (LGPL-2.1-or-later), and from the public README and issue tracker of FanControl (closed source). Corsair iCUE LINK speed-control details were learned from public documentation and source of OpenLinkHub (GPL-3.0-or-later), FanControl.CorsairLink (no licence), LinkPilot and OpenLinkHubWindows (GPL-3.0), OpenRGB's CorsairICueLink controller (GPL-2.0-or-later), liquidctl's Commander Core protocol notes (GPL-3.0-or-later) and Corsair's own published documents. No code from these projects is included. Nuvoton, MSI, AMD, Corsair, iCUE and iCUE LINK are trademarks of their owners; DeadWare is not affiliated with or endorsed by any of them.
