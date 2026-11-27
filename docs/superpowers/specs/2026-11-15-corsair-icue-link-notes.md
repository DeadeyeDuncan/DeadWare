# Corsair iCUE LINK System Hub: protocol notes

Status: reference material for [the lighting spec](2026-11-15-lighting-design.md) and, later, fan control. These are facts and interoperability conventions only. No code from another project is used or copied. Where a detail is unconfirmed, it says so.

## Device

- USB VID `0x1B1C`, PID `0x0C3F`. Use the HID interface **MI_00**: vendor usage page `0xFF42`, usage `0x01`, 513-byte output report. What MI_01 is for is unknown; never open it.
- Open the interface **shared**, never exclusive. On Windows every open handle receives its own copy of each input report, so other programs' replies can sit in this handle's queue. Drain stale reports before each command.
- The development PC's hub reported firmware **4.1.656** on 2026-11-15.

## Framing

- **Output report (513 bytes):** `[0]` = `0x00` (report ID), `[1]` = `0x00`, `[2]` = `0x01`, then the command bytes from `[3]`, then any data, zero-padded.
- **Input report**, with offsets counted after the report-ID byte (HidSharp returns that byte at index 0):
  - `[0]` = `0x00`, `[1]` = `0x00`
  - `[2]` = the opcode echoed back
  - `[3]` = status: `0x00` means OK. `0x03` on the open of a read endpoint means "wrong mode", i.e. the hub is in hardware mode. On the open of the colour endpoint (`0D 00 22`) the hub answered `0x03` while it was held (2026-11-27, below), so there it is not a mode signal.
  - endpoint reads only: `[4..5]` = data type, `[6]` = count, payload from `[7]`
- Replies to open and close carry leftover bytes from earlier buffers after the header. Ignore them.

## Commands (bytes after the three-byte header)

| Purpose | Bytes |
|---|---|
| Get firmware (works in either mode) | `02 13`; reply `[4]` major, `[5]` minor, `[6..7]` patch, little-endian |
| Enter software mode | `01 03 00 02` |
| Enter hardware mode | `01 03 00 01` |
| Close endpoint (handle 1) | `05 01 01 <ep>` |
| Open endpoint (handle 1) | `0D 01 <ep>` |
| Open colour endpoint (handle 0) | `0D 00 <ep>` |
| Read (handle 1) | `08 01`; on firmware 2.5 and later a second `08 01` returns a continuation packet |
| Write (handle 1) | `06 01 <payload>` |
| Write colour, first chunk | `06 00 <chunk>` |
| Write colour, following chunks | `07 00 <chunk>` |

Do not send `01 08 01`. Opcode `0x01` is the property-*set* opcode, so it may not be a pure read.

Endpoints and their data types:

| Endpoint | Purpose | Data type | Direction |
|---|---|---|---|
| `0x36` | device list | `21 00` | read |
| `0x21` | temperatures | `10 00` | read |
| `0x17` | speeds | `25 00` | read |
| `0x22` | colours | `12 00` | write |
| `0x18` | fan and pump speeds | `07 00` | write; **fan control only**, never sent by the lighting work |

## Transactions

Every transaction holds the shared named mutex `Global\CorsairLinkReadWriteGuardMutex` from its first frame to its last. The mutex is recursive per thread. Treat an abandoned mutex as acquired. A program that makes the mutex must give everyone full access to it: OpenRGB opens it with `CreateMutex`, which asks for all access, does not check whether that failed, and then loops for ever waiting on the failed handle, so it stops finding devices at the first Corsair device that uses the lock (measured 2026-11-27 with a lock made granting only Synchronize and Modify: access denied, error 5). OpenRGB itself makes it with a NULL DACL.

- **Read:** close(ep), open(ep), read, close(ep). Accept the read only if status is `00` and the data type matches.
- **Colour write:**
  - Build the payload as `[n+2 low, n+2 high, 0x00, 0x00, 0x12, 0x00, R, G, B, …]`, where n is the number of RGB bytes. Order the LEDs by channel number, each device contributing its LED count.
  - Send close(`0x22`), then open the colour endpoint (`0D 00 22`).
  - Send the payload in chunks of up to 508 bytes: the first chunk with `06 00`, the rest with `07 00`.
  - Finish with close(`0x22`).
- **Keep it awake:** in software mode the host must keep talking to the hub. On firmware 2.4.438 the hub was reported to fall back to hardware mode after about 10 s of silence. On firmware 4.1.656 (measured 2026-11-27, below) it was still held after 13 s of silence and had fallen back by itself after somewhere between 13 s and 64 s.

## Parsing

- **Device list** (`0x36`): `[6]` is the last channel index. Records start at `[7]` with channel 1. Each record has an 8-byte header, where `[2]` is the type, `[3]` the model and `[7]` the ID length, followed by the ASCII ID. An ID length of 0 marks an empty channel, which takes 8 bytes.
- **Temperatures and speeds** (`0x21` and `0x17`): entry *i* sits at `7 + 3i`. Each entry is a status byte (`0x00` available, `0x01` unavailable) followed by a signed 16-bit little-endian value. Temperatures are in tenths of a °C and speeds in RPM.
  - **The entry index is the channel number.**
  - Entry 0 is the hub itself; it read 31.3 °C on the development PC.

## Device types

Identify devices by **type code**, never by channel position or name.

| Type | Model | Device | LEDs | Temperature |
|---|---|---|---|---|
| `0x0F` | `0x00` | iCUE LINK RX RGB fan | 8 | no |
| `0x11` | `0x00`/`0x04` 240, `0x01` 280, `0x02`/`0x05` 360, `0x03` 420 | iCUE LINK TITAN AIO pump | 20 | coolant |
| `0x07` | `0x00`–`0x05` | iCUE LINK H100i–H170i RGB pump | 20 | coolant |
| `0x1A` | `0x01` | AIO pump (V2) | unknown | coolant |
| `0x01` | `0x00` | iCUE LINK QX RGB fan | 34 | no |
| `0x13` | `0x00` | iCUE LINK RX fan | 0 | no |
| `0x0C`, `0x19` | — | XD5 / XD6 pump-reservoir | 22 | yes |

Treat an unknown type, model or reply layout as a reason to stop, not to guess.

## Modes and ownership

- The hub has **one** mode, covering both lighting and cooling.
  - **Hardware mode** is the hub's default. It runs the lighting effect and cooling curves stored in its memory.
  - **Software mode** means the host drives both.
- Entering hardware mode resets the lighting to the stored effect.
- With no speed commands in software mode, the development PC's hub ran the pump at about 2,716 RPM and the fans at about 1,330 RPM on 2026-11-15, and at other speeds on other days (see the table under "2026-11-27" below). Whether the speeds change within one hold isn't known yet. This behaviour isn't documented anywhere.
- Cooperating tools take the shared mutex. iCUE reportedly doesn't honour it and expects exclusive access, so never drive the hub while iCUE runs.
- Polling temperatures and speeds every 2–3 s is plenty: coolant temperature moves over minutes.

## Observed on the development PC (2026-11-15, read-only)

| | Reading |
|---|---|
| Firmware | 4.1.656 |
| Channels | 1–3 are RX RGB fans (`0x0F`); 13 is a TITAN 360 pump (`0x11`, model `0x02`). The device list reports 13 as its last channel. |
| Temperatures | coolant 38.4 °C (channel 13); hub 31.3 °C (channel 0) |
| Speeds | fans 1,337 / 1,326 / 1,337 RPM; pump 2,716 RPM |

The motherboard's PUMP_FAN header reads the same pump speed, so the hub feeds the pump tachometer to the board.

### 2026-11-27, firmware 4.1.656, hub held by OpenRGB

One colour transaction was sent while OpenRGB held the hub, with an open of the temperature endpoint answering `0x00` before and after it as the control. The replies' status bytes:

| Exchange | Status |
|---|---|
| close colours (`05 01 01 22`) | `0x00` |
| open colours (`0D 00 22`) | `0x03` |
| colour write (`06 00 …`, 44 LEDs) | `0x00` |
| close colours (`05 01 01 22`) | `0x00` |

The hub was still held afterwards. So `0x03` on the open of the colour endpoint is not a mode signal.

Fan speed against coolant temperature, each read while OpenRGB held the hub and sent no speed command:

| Date | Coolant | Fans |
|---|---|---|
| 2026-11-27 | 36.5 °C | about 1,155 RPM |
| 2026-11-15 | 38.4 °C | about 1,330 RPM |
| 2026-11-25 | 43.7 °C | about 1,660 RPM |

Each reading was on a different day, after OpenRGB had taken the hub afresh. They fit two explanations that they cannot tell apart: the hub keeps following its own stored curve while held, or it keeps the speed it had at the moment it was taken. Two readings within one hold at clearly different coolant temperatures would settle it; the Sensors history gives those once DeadWare drives the hub.

### 2026-11-27 afternoon, firmware 4.1.656: how long the hub stays held without traffic

From the lighting helper's log of that day: the hub was still held 13 s after its holder was stopped (OpenRGB of DeadWare 0.22.0 stopped at 20:41:45 UTC; a reading at 20:41:58 worked), and it was in its own mode at 20:43:02 with no program having sent hardware mode in between (the OpenRGB started at 20:42:04 was stuck on the shared lock before its first command to the hub). So the hub went back to its own mode by itself after somewhere between 13 s and 64 s without traffic (64 s counted from the last read, 77 s from the last colour frame).

## Attribution

> Corsair iCUE LINK protocol details were learned from public documentation and source of OpenLinkHub (Nikola Jurkovic, GPL-3.0), OpenRGB's CorsairICueLink controller (Aiden Vigue, Adam Honse, Nikola Jurkovic; GPL-2.0-or-later), FanControl.CorsairLink (Evan Mulawski) and liquidctl's Commander Core protocol notes (GPL-3.0). No code from these projects is included. Corsair, iCUE and iCUE LINK are trademarks of Corsair; DeadWare is not affiliated with or endorsed by Corsair.
