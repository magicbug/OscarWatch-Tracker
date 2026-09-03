# Building radio (rig) drivers

OscarWatch controls radios for **satellite doppler tracking** via CAT. Each radio protocol implements `IRigDriver`. Doppler policy, VFO layout, and pass setup live in **`RigController`**; the driver is a thin serial/protocol layer.

## Why not HamLib?

HamLib works well for many HF rigs. Satellite tracking needs more than generic frequency and VFO calls: satellite mode, Main/Sub or SAT RX/TX layout, split or exchange, uplink CTCSS, and doppler policy (TX-fixed vs RX-fixed) with per-rig behaviour. HamLib’s model does not map cleanly to that, and satellite features in the field are often thin or wrong.

OscarWatch keeps protocol details in **`IRigDriver`** implementations and pass behaviour in **`RigController`**, so each supported radio can follow how operators actually run a pass (including quirks such as Kenwood SATL or dual-radio FT-817 setups). A HamLib layer is not planned; new rigs should add a native driver. See also the README section *Why not HamLib?*

## Architecture

```mermaid
flowchart LR
  UI[MainViewModel / Frequency overlay]
  RigC[RigController worker thread]
  Fact[RigDriverFactory]
  Drv[IRigDriver]
  HW[Radio CI-V or CAT]
  UI --> RigC
  RigC --> Fact
  Fact --> Drv
  Drv --> HW
```

- **`RigController`** ([`OscarWatch/Rig/RigController.cs`](../OscarWatch/Rig/RigController.cs)) — background thread (~100 ms loop): satellite mode, Main/Sub VFO selection, doppler frequency writes, CTCSS, split, FM companion leg, dial-change detection. Linear USB/LSB/CW uses **interactive** tuning: pause CAT while Main moves, resume after the receive dial has been still for `InteractiveDialSettleMs` (default 800 ms), defer Sub uplink writes for `InteractiveUplinkResumeMs` (default 2500 ms) after dial activity, and `RestoreOperatorVfo()` to keep receive VFO selected on ICOM-style rigs.
- **`IRigDriver`** — open port, read/set frequency, VFO, mode, satellite mode, tones.
- **`RigSettings`** ([`OscarWatch.Core/Models/RigSettings.cs`](../OscarWatch.Core/Models/RigSettings.cs)) — port, baud rate, CI-V address, doppler thresholds, CAT delay.
- **`IcomCivCodec`** ([`OscarWatch.Core/Radio/IcomCivCodec.cs`](../OscarWatch.Core/Radio/IcomCivCodec.cs)) — encode/decode CI-V frames (Core, no serial I/O).

## `IRigDriver` contract

```csharp
public interface IRigDriver : IDisposable
{
    bool IsConnected { get; }
    RigType RigType { get; }
    void Open();
    long? ReadFrequencyHz(RigVfo vfo);
    bool SetFrequencyHz(long hz);
    void SelectVfo(RigVfo vfo, bool force = false);
    void SetMode(string mode);
    void SetSplitOn(bool on);
    void SetSatelliteMode(bool on);
    void ExchangeVfos();
    void SetToneOn(bool on);
    void SetToneSquelchOn(bool on);
    void SetToneHz(double hz, bool squelchTone);
    bool SupportsTracking { get; }
    bool SupportsVfoExchange => true;
}
```

| Member | Expectations |
|--------|----------------|
| `SupportsTracking` | If `false`, `RigController` will not run doppler updates (use for rigs that only support a subset of features). |
| `Open` | Open serial (or other) link. On failure, leave `IsConnected` false. |
| `ReadFrequencyHz` | Select VFO if needed, return Hz or null. May cache last good value when offline (see Icom base). |
| `SetFrequencyHz` | Set frequency on **currently selected** VFO. Return `true` if accepted. Validate satellite band in driver or rely on codec. |
| `SelectVfo` | `RigVfo`: `VfoA`, `VfoB`, `Main`, `Sub` — Icom satellite stack uses Main/Sub. |
| `SetSatelliteMode` | Rig-specific satellite/SAT menu (required for tracking). |
| `SetMode` | `"FM"`, `"USB"`, etc. — Icom uses CI-V mode bytes. |
| `SetSplitOn` / `ExchangeVfos` | Satellite split operation. |
| Tone methods | Sub uplink CTCSS for FM satellites. |

`RigController` passes **`RigTrackingContext`** (from the frequency overlay) with uplink/downlink offsets and database mode; the driver does not compute doppler. Use **`EffectiveUplinkMode`** / **`EffectiveDownlinkMode`** for `SetMode` — they apply the panel **Voice/CW** choice and **`RigSettings.CwKeepSidebandDownlink`** via **`TransponderOperatingModes`** in Core (drivers should not reimplement that logic).

## ICOM CI-V stack (recommended pattern)

Most satellite logic is shared in **`IcomCivDriverBase`** ([`OscarWatch/Rig/IcomCivDriverBase.cs`](../OscarWatch/Rig/IcomCivDriverBase.cs)):

- Owns **`IcomSerialTransport`** ([`OscarWatch/Rig/IcomSerialTransport.cs`](../OscarWatch/Rig/IcomSerialTransport.cs)) — framing, retries, read timeout
- Implements frequency, VFO, mode, split, tone commands via **`IcomCivCodec`**
- Caches per-VFO frequencies when disconnected so UI can still show values

Per-radio subclasses only override what differs, usually **`SetSatelliteMode`**:

| Class | `RigType` | Satellite mode CI-V |
|-------|-----------|---------------------|
| [`IcomIc910Driver`](../OscarWatch/Rig/IcomIc910Driver.cs) | `IcomIc910` | `1A 07 01` / `00` |
| [`IcomIc9100Driver`](../OscarWatch/Rig/IcomIc9100Driver.cs) | `IcomIc9100` | `16 5A 01` / `00` (same as IC-9700) |
| [`IcomIc9700Driver`](../OscarWatch/Rig/IcomIc9700Driver.cs) | `IcomIc9700` | `16 5A 01` / `00` |
| [`IcomIc821hDriver`](../OscarWatch/Rig/IcomIc821hDriver.cs) | `IcomIc821h` | `1A 07 01` / `00`; inverted `07 D0`/`D1` in SAT; split no-op |
| [`IcomIc705Driver`](../OscarWatch/Rig/IcomIc705Driver.cs) | `IcomIc705` | no-op (dual-radio VFO A only) |
| [`IcomIc7300Driver`](../OscarWatch/Rig/IcomIc7300Driver.cs) | `IcomIc7300` | no-op (dual-radio VFO A only) |
| [`IcomIc905Driver`](../OscarWatch/Rig/IcomIc905Driver.cs) | `IcomIc905` | no-op (dual-radio VFO A only) |
| [`IcomIc706SeriesDriver`](../OscarWatch/Rig/IcomIc706SeriesDriver.cs) | `IcomIc706`, `IcomIc706Mkii`, `IcomIc706MkiiG` | no-op (dual-radio VFO A only) |

**IC-9700 digital modes:** database `DATA-USB` / `DATA-LSB` send base SSB (`06 01` / `06 00`) then DATA on with FIL1 (`1A 06 01 01`) — USB-D / LSB-D. Command `26` is unavailable in SAT mode; IC-910/9100 keep voice SSB only for `DATA-*` strings.

**IC-910 FM narrow:** database `FMN` sends `06 05 02` (FM + filter 2). Plain `FM` sends `06 05 01`. Generic ICOM mode encoding still maps both to `06 05` (wide); only the IC-910 driver uses the filter byte (Hamlib / SatPC32).

Example new Icom model:

```csharp
public sealed class IcomIc7600Driver : IcomCivDriverBase
{
    public IcomIc7600Driver(string port, int baudRate, string civAddressHex)
        : base(RigType.IcomIc7600, port, baudRate, civAddressHex) { }

    public override bool SupportsTracking => true; // or false until validated

    public override void SetSatelliteMode(bool on) =>
        WriteWithRetry(on ? [/* model-specific bytes */] : [/* off */]);
}
```

Confirm bytes against the radio’s CI-V reference manual. Add codec helpers in Core only if multiple rigs share the same frame format.

### CI-V testing without hardware

- **`IcomCivCodecTests`** — encode/decode frequency and address parsing
- **`RecordingRigDriver`** ([`OscarWatch.Tests/RecordingRigDriver.cs`](../OscarWatch.Tests/RecordingRigDriver.cs)) — records `SetFrequencyHz`, VFO, tones
- **`RigController` tests** — inject `(_ => recordingDriver)` via `RigController` constructor factory parameter
- **`DummyRigDriver`** ([`OscarWatch/Rig/DummyRigDriver.cs`](../OscarWatch/Rig/DummyRigDriver.cs)) — in-memory rig for UI/policy tests

## Non-Icom radios

For Yaesu, Kenwood, Elecraft, etc.:

1. Implement **`IRigDriver`** directly in `OscarWatch/Rig/` (or a subfolder).
2. Use the manufacturer’s CAT document for serial parameters and commands.
3. Map OscarWatch’s `RigVfo` to the radio’s VFO/receiver/transmitter semantics.
4. Set **`SupportsTracking`** accurately; implement `SetSatelliteMode` if the radio has a satellite or split layout equivalent.

Keep **protocol parsing in the app project**; put only reusable math (frequency validation, doppler) in **OscarWatch.Core/Radio/**.

## Reference: Yaesu FT-847 (shipped)

| Piece | Path |
|-------|------|
| CAT codec | [`OscarWatch.Core/Radio/YaesuFt847CatCodec.cs`](../OscarWatch.Core/Radio/YaesuFt847CatCodec.cs) |
| Serial transport | [`OscarWatch/Rig/YaesuCatTransport.cs`](../OscarWatch/Rig/YaesuCatTransport.cs) — **8N2**, five-byte frames |
| Driver | [`OscarWatch/Rig/YaesuFt847Driver.cs`](../OscarWatch/Rig/YaesuFt847Driver.cs) |

- `SetSatelliteMode` → CAT `0x4e` / `0x8e`; `Main`/`Sub` map to **SAT RX** / **SAT TX** opcodes (`0x11` / `0x21`).
- `SupportsVfoExchange` is **false** — band swaps need the front-panel A/B switch.
- CAT frequency resolution is **10 Hz**; CTCSS uses Hamlib’s 0.1 Hz tone table.
- Cross-check commands against [Hamlib `ft847.c`](https://github.com/Hamlib/Hamlib/blob/master/rigs/yaesu/ft847.c).

### Hardware checklist (FT-847)

- Radio menu **#37**: CAT baud matches Settings (often **57600**).
- **CT-62** (or equivalent) on the **CAT/LINEAR** jack.
- Two-way CAT firmware (serial **8G05xxxx+**).
- On a real pass: SAT mode engages, RX/TX doppler tracks, uplink CTCSS on SAT TX (encode-only, like TS-2000; tone decode mutes receive on satellite downlinks).

## Reference: Yaesu FT-817 / FT-818 (shipped)

| Piece | Path |
|-------|------|
| CAT codec | [`OscarWatch.Core/Radio/YaesuFt817CatCodec.cs`](../OscarWatch.Core/Radio/YaesuFt817CatCodec.cs) |
| Serial transport | [`OscarWatch/Rig/YaesuCatTransport.cs`](../OscarWatch/Rig/YaesuCatTransport.cs) — **8N2**, five-byte frames |
| Driver | [`OscarWatch/Rig/YaesuFt817Driver.cs`](../OscarWatch/Rig/YaesuFt817Driver.cs), [`YaesuFt818Driver.cs`](../OscarWatch/Rig/YaesuFt818Driver.cs) |

- **Dual radio only** (`RigSettings.DualRadioEnabled`): FT-817/FT-818 are not offered in the single-radio driver list. Each endpoint is one physical radio and one VFO (RX on downlink, TX + CTCSS on uplink). No split CAT is used in this layout.
- **Band coverage:** HF/6 m, 2 m, and 70 cm on both models. There is no software band gate — any satellite frequency the radio accepts is sent over CAT (including **AO-07 Mode A** on 10 m downlink).
- `SupportsVfoExchange` is **false** — VFO B is selected with CAT opcode `0x81` before TX commands on a **single** split radio; in **dual** mode uplink CTCSS stays on **Main** (VFO A) to match the TX frequency leg.
- Cross-check against [Hamlib `ft817.c`](https://github.com/Hamlib/Hamlib/blob/master/rigs/yaesu/ft817.c).
- CTCSS tone frequency (opcode `0x0B`) uses BCD in bytes 1–2 (Hamlib / KA7OEI), not the FT-847 single-byte tone table.
- CAT opcode **0x00** is **dial lock on**, **0x80** is lock off (not “CAT session on/off”). `Open()` unlocks the panel; `SetMode` locks on **FM/FMN** only so linear USB/LSB/CW can still be spun for passband trim.

### Hardware checklist (FT-817 / FT-818)

- Enable **Settings → Radio → Dual radio**; configure downlink COM + uplink COM (and rotator on a third port if used).
- Menu **#14** CAT rate on **each** radio must match Settings for that leg (OscarWatch suggests **4800**; **38400** also works).
- OscarWatch uses **8N2** Yaesu CAT. One main VFO per radio — downlink for RX, uplink for TX + CTCSS.
- **FM:** dial lock on via CAT while tracking. **USB/LSB/CW:** dial unlocked on downlink so you can scan the transponder; uplink doppler continues on the other radio.
- On a real pass: both legs get doppler; CTCSS on uplink only (USA: TSQL for ICOM and most rigs; TS-2000 always encode-only because CT mutes receive).

## Reference: ICOM IC-905 (shipped, dual radio only)

| Piece | Path |
|-------|------|
| Driver | [`OscarWatch/Rig/IcomIc905Driver.cs`](../OscarWatch/Rig/IcomIc905Driver.cs) |

- **Dual radio only** (`RigSettings.DualRadioEnabled`): IC-905 is not offered in the single-radio driver list. Each endpoint is one physical radio on VFO A (RigController uses `Main`, mapped to VFO A in the driver).
- No dedicated satellite mode — `SetSatelliteMode` is a no-op; dual pass init sets mode and frequency directly.
- Default CI-V address **AC**; default baud **115200** (must match radio Set mode).
- Frequency validation includes VHF/UHF/23 cm plus SHF (13 cm / 6 cm / 3 cm) for IC-905 microwave bands.
- Mixed pairs (e.g. IC-905 uplink + SDR downlink) need no special controller logic.

### Hardware checklist (IC-905 dual)

- Enable **Settings → Radio → Dual radio**; configure each leg (type, COM, baud, CI-V address for IC-905 legs).
- Match **CI-V address** and **baud** in the radio Set mode (defaults AC / 115200).
- One COM port per leg — use the USB CI-V serial port.
- On a real pass: both legs get doppler; CTCSS on uplink only.

## Reference: ICOM IC-705 (shipped, dual radio only)

| Piece | Path |
|-------|------|
| Driver | [`OscarWatch/Rig/IcomIc705Driver.cs`](../OscarWatch/Rig/IcomIc705Driver.cs) |

- **Dual radio only** (`RigSettings.DualRadioEnabled`): IC-705 is not offered in the single-radio driver list. Each endpoint is one physical radio on VFO A (RigController uses `Main`, mapped to VFO A in the driver).
- No dedicated satellite mode — `SetSatelliteMode` is a no-op; dual pass init sets mode and frequency directly.
- Default CI-V address **A4**; default baud **115200** (must match radio menu).
- Mixed pairs (e.g. IC-705 downlink + FT-818 uplink) need no special controller logic.

### Hardware checklist (IC-705 dual)

- Enable **Settings → Radio → Dual radio**; configure each leg (type, COM, baud, CI-V address for IC-705 legs).
- **Connectors → CI-V → CI-V USB Port** = **Link to [CI-V]** on each radio (not REMOTE).
- One COM port per leg — use the CI-V-labeled port when Windows shows two.
- On a real pass: both legs get doppler; CTCSS on uplink only.

## Reference: ICOM IC-7300 (shipped, dual radio only)

| Piece | Path |
|-------|------|
| Driver | [`OscarWatch/Rig/IcomIc7300Driver.cs`](../OscarWatch/Rig/IcomIc7300Driver.cs) |

- **Dual radio only** (`RigSettings.DualRadioEnabled`): IC-7300 is not offered in the single-radio driver list. Each endpoint is one physical radio on VFO A (RigController uses `Main`, mapped to VFO A in the driver).
- No dedicated satellite mode — `SetSatelliteMode` is a no-op; dual pass init sets mode and frequency directly.
- Default CI-V address **94**; default baud **115200** (must match radio menu).
- HF and 6 m coverage (1.8–54 MHz). Typical use: **downlink** for AO-07 Mode A (10 m) paired with a 2 m-capable uplink radio (FT-817/818, IC-706, IC-705, etc.).
- Mixed pairs need no special controller logic.

### Hardware checklist (IC-7300 dual)

- Enable **Settings → Radio → Dual radio**; configure each leg (type, COM, baud, CI-V address for IC-7300 legs).
- Match **CI-V address** and **baud** in the radio CI-V menu (defaults 94H / 115200).
- One COM port per leg — use the USB CI-V serial port.
- On a real pass: both legs get doppler; CTCSS on uplink only.

## Reference: ICOM IC-706 series (shipped, dual radio only)

| Piece | Path |
|-------|------|
| Driver | [`OscarWatch/Rig/IcomIc706SeriesDriver.cs`](../OscarWatch/Rig/IcomIc706SeriesDriver.cs) |

One CI-V driver covers **IC-706**, **IC-706MKII**, and **IC-706MKIIG** as separate dual-radio leg types. OscarWatch uses the same VFO-A command set for all three; only the default CI-V address and band coverage differ. Out-of-band frequency writes are rejected in software (706/MKII: HF/6 m and 2 m; MKIIG adds 70 cm).

| Model | Default CI-V | Bands (satellite-relevant) |
|-------|--------------|----------------------------|
| IC-706 | `48H` | HF/6 m and 2m |
| IC-706MKII | `4CH` | HF/6 m and 2m |
| IC-706MKIIG | `58H` | HF/6 m, 2m, and 70cm (AO-07 Mode A downlink on 10 m) |

- **Dual radio only** — not in the single-radio driver list. Each endpoint is one physical radio on VFO A.
- No dedicated satellite mode — `SetSatelliteMode` is a no-op.
- Default baud **19200** (must match radio CI-V menu).
- **23cm** satellites are outside all three models' hardware.

### Hardware checklist (IC-706 series dual)

- Enable **Settings → Radio → Dual radio**; pick the correct leg type so the default CI-V address matches your radio.
- CI-V via the **REMOTE** jack (or CT-17). **IC-706 / MKII:** Initial Set Mode (LOCK at power-on). **MKIIG:** menus 34–36 (ADDRES / BAUD / TRN On).
- One COM port per leg.
- On a real pass: both legs get doppler; CTCSS on uplink only.

## Reference: Yaesu FT-991 / FT-991A (shipped, dual radio only)

| Piece | Path |
|-------|------|
| CAT codec | [`OscarWatch.Core/Radio/YaesuFt991CatCodec.cs`](../OscarWatch.Core/Radio/YaesuFt991CatCodec.cs) |
| Serial transport | [`OscarWatch/Rig/YaesuNewCatTransport.cs`](../OscarWatch/Rig/YaesuNewCatTransport.cs) — **8N2**, hardware RTS, semicolon ASCII |
| Driver | [`OscarWatch/Rig/YaesuFt991Driver.cs`](../OscarWatch/Rig/YaesuFt991Driver.cs), [`YaesuFt991aDriver.cs`](../OscarWatch/Rig/YaesuFt991aDriver.cs) |

- **Dual radio only**: FT-991/991A are not in the single-radio driver list. Downlink legs use **VFO-A** (`FA`, `MD0`, `LK0/1`, `CT0`/`CN0`). **Uplink** legs use split (`FT3;`) and Doppler on **VFO-B** (`FB`) so TX frequency can update during keydown.
- ASCII newcat commands (Hamlib-compatible subset); default baud **38400** (menu 031).
- FM tracking locks VFO-A dial via `LK1`; linear modes use `LK0` so passband trim works on the downlink leg.
- Cross-check against [Hamlib `ft991.c`](https://github.com/Hamlib/Hamlib/blob/master/rigs/yaesu/ft991.c).

### Hardware checklist (FT-991 / FT-991A dual)

- Enable **Settings → Radio → Dual radio**; configure each leg (type, COM, baud).
- Menu **031 CAT RATE** must match Settings on each radio.
- Use the USB **CAT** virtual COM port; hardware RTS is required.
- On a real pass: both legs get doppler; CTCSS on uplink only.

## Reference: Yaesu FTX-1 series (shipped, dual radio only)

| Piece | Path |
|-------|------|
| CAT codec | [`OscarWatch.Core/Radio/YaesuFt991CatCodec.cs`](../OscarWatch.Core/Radio/YaesuFt991CatCodec.cs) — shared newcat subset |
| Serial transport | [`OscarWatch/Rig/YaesuNewCatTransport.cs`](../OscarWatch/Rig/YaesuNewCatTransport.cs) — **8N2**, hardware RTS, semicolon ASCII |
| Driver | [`OscarWatch/Rig/YaesuFtx1Driver.cs`](../OscarWatch/Rig/YaesuFtx1Driver.cs) |

Covers **FTX-1 Field** and **FTX-1optima** (same field head). Downlink uses VFO-A (`FA`, `MD0`, `LK`, `CN`/`CT`); uplink uses split and **VFO-B** (`FB`) for Doppler during transmit.

- **Dual radio only** — not in the single-radio driver list.
- Default baud **38400** on **CAT-1** (menu CAT-1 RATE; 4800–115200 supported).
- Use the **Enhanced COM port (CAT-1)** for frequency/mode — not CAT-2 (PTT/CW/digital).
- Cross-check against [Hamlib `ftx1.c`](https://github.com/Hamlib/Hamlib/blob/master/rigs/yaesu/ftx1.c) and the FTX-1 CAT manual.

### Hardware checklist (FTX-1 dual)

- Enable **Settings → Radio → Dual radio**; configure each leg (type, COM, baud).
- Match **CAT-1 RATE** in the radio menu to Settings on each field head.
- One **CAT-1** COM port per leg (and a third port for rotator if used).
- On a real pass: both legs get doppler; CTCSS on uplink only; FM locks the MAIN dial via `LK1`.

## Reference: Kenwood TS-2000 (shipped, beta)

| Piece | Path |
|-------|------|
| CAT codec | [`OscarWatch.Core/Radio/KenwoodCatCodec.cs`](../OscarWatch.Core/Radio/KenwoodCatCodec.cs) |
| Serial transport | [`OscarWatch/Rig/KenwoodCatTransport.cs`](../OscarWatch/Rig/KenwoodCatTransport.cs) — **8N1**, hardware RTS by default (Settings toggle), semicolon-terminated ASCII |
| Driver | [`OscarWatch/Rig/KenwoodTs2000Driver.cs`](../OscarWatch/Rig/KenwoodTs2000Driver.cs) |

- Cross-band **SATL** for the TS-2000 ([`KenwoodTs2000_SatCatReference_A07.txt`](../OscarWatch.Tests/Fixtures/KenwoodTs2000_SatCatReference_A07.txt) field CAT capture): `SA1010110;` / `SA1011110;` for CTRL with TRACE on (default), or `SA1010000;` / `SA1011000;` when **TRACE** is off in Settings (no `DC` in SAT), 2× `TO0;`, `FA;` read, `TS1;`, `AI2;`, then `AI0;` after init; pass programming and SATL doppler steps (`FA`/`FB`/`SM` cluster). While tracking, one `FA;` link-hold poll about every second (SatPC32-style), not a burst per doppler step. **Beacon / receive-only** keeps SATL and updates `FA` only. Exit on quit/disconnect: `RX;` `TO0;` `SA0010000;` — also sent on driver `Dispose` when tracking was active. Does **not** set RF power (`PC`). Silent set commands do not require a CAT echo; `FA;` reads wait up to ~450 ms.
- `Main`/`Sub` → **`FA`/`FB`**; **no `FR`/`FT` or `DC`** in SATL (Hamlib/Gpredict disable `FR` in SAT for the same reason).
- `SupportsVfoExchange` is **true** — swaps `FA`/`FB` frequencies in SATL when Main is on the wrong band (same logic as ICOM `TryBandSwap`).
- CTCSS encode: `TN` + `TO`; TSQL squelch: `CN` + `CT` (Hamlib `ts2000_ctcss_list`, 1-based index). In SATL, **`SA1011110;`** selects Sub CTRL before uplink `MD`/tone (not `DC01;`). After entry/pass setup, best-effort **`DC10;`** pins TX/PTT to SUB (CTRL MAIN); ignored if the radio rejects `DC` in SATL.
- If `SA;` does not confirm SATL, OscarWatch **still tracks on `FA`/`FB`** (no split/FR fallback).
- Consecutive failed `FA`/`FB` writes briefly suspend further Doppler CAT to avoid rejection-beep storms.
- Cross-check against [Hamlib `kenwood.c`](https://github.com/Hamlib/Hamlib/blob/master/rigs/kenwood/kenwood.c) and [`ts2000.txt`](https://github.com/Hamlib/Hamlib/blob/master/rigs/kenwood/ts2000.txt).

### Hardware checklist (TS-2000)

- On the radio: select **SAT** mode and turn **memory mode off** before OscarWatch tracking (manual steps; CAT alone is not enough).
- PC CAT port **57600 8N1** with **hardware RTS** by default (matches Settings; RTS must be asserted on full cables or the radio will not reply). Operators with cables that lack RTS/CTS can turn off **Hardware RTS** in Settings → Radio.
- **TRACE / TRACE REV** in SATL SA commands is on by default; turn off **TRACE / TRACE REV in SATL** in Settings when OscarWatch alone should manage Doppler.
- Close any front-panel menu before tracking; press **SAT** on the front panel and turn memory mode off (CAT `SA` alone is not enough). CAT delay ~20–30 ms helps on the TS-2000.
- On a real pass: RX/TX doppler on `FA`/`FB`, uplink CTCSS on Sub.

## Reference: Kenwood TH-D74 / TH-D75 (shipped, dual radio only)

| Piece | Path |
|-------|------|
| CAT codec | [`OscarWatch.Core/Radio/KenwoodThD7xCatCodec.cs`](../OscarWatch.Core/Radio/KenwoodThD7xCatCodec.cs) |
| Serial transport | [`OscarWatch/Rig/KenwoodHtTransport.cs`](../OscarWatch/Rig/KenwoodHtTransport.cs) — **8N1**, CR-terminated ASCII, no RTS/CTS handshake; DTR/RTS modem lines asserted for USB CDC |
| Driver | [`OscarWatch/Rig/KenwoodThD7xDriver.cs`](../OscarWatch/Rig/KenwoodThD7xDriver.cs) |

- **Dual radio only** (`RigSettings.DualRadioEnabled`): TH-D74/TH-D75 are not offered in the single-radio driver list. Each endpoint is one physical HT on **Band B** (all-mode receiver).
- Different dialect from the TS-2000: CR framing, `FQ`/`FO`/`MD`/`VM`/`BC`/`FT`/`FS` (not semicolon `FA`/`FB` SATL).
- On open: assert DTR/RTS, settle briefly, then `VM 1,0`, `BC 1`, and a verified `FO 1` read before reporting connected. FM/data-FM uses NFM (`MD` code 6) on a **5 kHz** grid; USB/LSB/CW/AM use fine tune + **20 Hz** step. Cross-band `FQ` re-applies `FT`/`FS` because the HT stores step per band.
- `SupportsVfoExchange` is **false**. CTCSS CAT is intentionally a no-op until hardware-validated; set uplink tone on the radio manually for FM satellites.
- Protocol behaviour follows CardSat’s bench-tested TH-D7x subset (`LEGF_KWHT`).

### Hardware checklist (TH-D74 / TH-D75 dual)

- Enable **Settings → Radio → Dual radio**; choose TH-D74 or TH-D75 on each leg with the USB/PC COM port at **9600** 8N1 (user-selectable).
- Use normal PC-command mode (not KISS/TNC). Disable unsolicited GPS PC output if enabled.
- On macOS select the radio’s `/dev/cu.*` device (not `/dev/tty.*`); OscarWatch treats a silent FO reply as a failed connection.
- On a real pass: Doppler on Band B; for FM uplinks needing CTCSS, set the tone on the HT yourself for now.

## Reference: FlexRadio SmartSDR (shipped)

| Piece | Path |
|-------|------|
| Discovery parse | [`OscarWatch.Core/Radio/FlexDiscoveryCodec.cs`](../OscarWatch.Core/Radio/FlexDiscoveryCodec.cs) |
| Command framing | [`OscarWatch.Core/Radio/FlexSmartSdrCodec.cs`](../OscarWatch.Core/Radio/FlexSmartSdrCodec.cs) |
| TCP client | [`OscarWatch/Rig/FlexSmartSdrClient.cs`](../OscarWatch/Rig/FlexSmartSdrClient.cs) |
| Discovery service | [`OscarWatch/Rig/FlexDiscoveryService.cs`](../OscarWatch/Rig/FlexDiscoveryService.cs) |
| Driver | [`OscarWatch/Rig/FlexRadioDriver.cs`](../OscarWatch/Rig/FlexRadioDriver.cs) |

- **Single-radio only** — not a dual-radio endpoint. Settings lists discovered radios (UDP **4992**) or accepts a manual host/port (TCP **4992**).
- `SetSatelliteMode(true)` → `radio set full_duplex_enabled=1`, ensure two slices, mark uplink `tx=1`.
- `Main`/`Sub` → RX / TX slice tune + mode; CTCSS via `fm_tone_mode` / `fm_tone_value` on the TX slice.
- `SupportsVfoExchange` is **false**.
- Hardware-less tests use [`FlexSmartSdrStubServer`](../OscarWatch.Tests/FlexSmartSdrStubServer.cs) (same idea as rigctl TCP stubs).
- Do **not** take a FlexLib dependency — protocol subset only (AGPL-friendly, easy to stub).

### Hardware checklist (Flex)

- Dual-SCU radio on the LAN (e.g. **8600** / **8600M**, or dual-SCU 6000-series); optional band→antenna port map in Settings (VHF/UHF RX and TX); otherwise radio ports stay as configured.
- OscarWatch uses the SmartSDR TCP/IP API to the radio (not automation of SmartSDR for Windows).
- Discovery or manual IP; **Test SmartSDR connection** succeeds.
- On a real pass: FDX on, RX/TX Doppler both move, FM uplink CTCSS correct.

## Step-by-step: add a new rig type

### 1. Add `RigType` enum value

[`OscarWatch.Core/Models/RigType.cs`](../OscarWatch.Core/Models/RigType.cs)

### 2. Implement `IRigDriver`

Either extend `IcomCivDriverBase` or create a new class.

### 3. Register in the factory

[`OscarWatch/Rig/RigDriverFactory.cs`](../OscarWatch/Rig/RigDriverFactory.cs):

```csharp
public static IRigDriver Create(RigSettings settings) => settings.Type switch
{
    RigType.IcomIc910 => new IcomIc910Driver(settings.Port, settings.BaudRate, settings.CivAddress),
    RigType.MyRadio => new MyRadioDriver(settings.Port, settings.BaudRate, /* ... */),
    _ => new DummyRigDriver()
};
```

### 4. Default CI-V address (Icom only)

In `RigSettings.DefaultCivAddressFor` if the rig has a non-`60` factory address.

### 5. Settings UI

[`SettingsViewModel.cs`](../OscarWatch/ViewModels/SettingsViewModel.cs) — `RigTypeChoices`:

```csharp
new(RigType.MyRadio, "My Radio Label")
```

Radio tab in [`SettingsWindow.axaml`](../OscarWatch/Views/SettingsWindow.axaml) binds type, port, baud, CI-V address.

### 6. Tests

- Driver unit tests with a fake transport or recording driver
- Golden tests for any new codec bytes in `OscarWatch.Tests`
- `RigController` integration tests for doppler threshold and VFO selection (see `RigPolicyTests.cs`, rig controller tests)

### 7. Manual validation

- Open Settings → Radio, correct COM port and baud
- Enable rig, select satellite, confirm Main/Sub frequencies move with pass
- Toggle CAT pause and standby
- Confirm no COM conflict with rotator on the same port

## How `RigController` uses the driver

You rarely call the driver from the UI. Typical sequence on the worker thread:

1. `EnsureConnected` → `RigDriverFactory.Create` → `Open`
2. New pass (`RunPassInit`) — layout depends on mode (see **`RigSatModeHelper.UseMainSubLayout`** and **`SatelliteTransponderMode.IsBeaconOnly`**):
   - **Cross-band** (`downlink` and `uplink` both &gt; 0, &gt;10 MHz apart) → `SetSatelliteMode(true)`, `SetSplitOn(false)`, Main=RX / Sub=TX, optional `ExchangeVfos`, CTCSS on Sub
   - **Beacon / receive-only** (`uplink` ≤ 0) → **ICOM**: `SetSatelliteMode(false)`; on **IC-910 / IC-9100 / IC-9700** also clear tones on Main+Sub, ensure downlink band on **Main** (`ExchangeVfos` if needed), tune and doppler on Main only. **Kenwood TS-2000**: keep SATL, Doppler on `FA` only.
   - **Same-band** (both freqs, ≤10 MHz apart) → **IC-910/9100/9700**: satellite mode off, split on, VFO A/B; **IC-821H**: satellite mode on, Main/Sub (no split CAT); **Kenwood TS-2000**: satellite mode off, split on
3. Each context update → `SelectVfo` + `SetFrequencyHz` when doppler delta exceeds threshold (`_receiveVfo` may be Main, Sub, VfoA, or VfoB)
4. CTCSS changes → `SetToneHz` / squelch on uplink VFO (skipped when `IsBeaconOnly`)
5. Disconnect / disable → dispose driver

Respect **`RigSettings.CatDelayMs`** and thresholds in the controller; the driver should not sleep for doppler pacing unless the protocol requires it (Icom uses short delays inside `ReadFrequencyHz`).

## Checklist

- [ ] `IRigDriver` with correct `RigType` and `SupportsTracking`
- [ ] `RigDriverFactory` case
- [ ] Settings label (+ default CI-V address if Icom)
- [ ] Thread-safe serial access (one command at a time)
- [ ] `Open` / `Dispose` idempotent and safe
- [ ] Frequency read/write on Main/Sub or A/B as used by `RigController`
- [ ] `RecordingRigDriver` or protocol tests
- [ ] Manual pass test with real hardware

## Related files

| File | Role |
|------|------|
| `OscarWatch.Core/Services/IRigController.cs` | UI-facing rig API |
| `OscarWatch.Core/Models/RigTrackingContext.cs` | Uplink/downlink offsets for doppler |
| `OscarWatch.Core/Radio/RigSatModeHelper.cs` | Main/Sub vs A/B layout |
| `OscarWatch.Core/Radio/DopplerFrequencyCalculator.cs` | Hz math (not serial) |
| `tools/generate_radio_fixtures.py` | Optional golden CAT fixtures |
