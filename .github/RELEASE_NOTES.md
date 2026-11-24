# OscarWatch Tracker v1.1.9 Release Notes

A big thank you to our newest supporters, **Timothy WT8D**, **Mike DL6UMR**, **Claes SM6LRW**, and **Joseph KF7UAN**. Your support keeps OscarWatch moving, and thank you as well to everyone who has already donated.

Welcome to version 1.1.9. This release brings FT8 on QO-100, a big step forward for the CW desk with automatic uplink alignment and a live keyboard, POTA/SOTA/WWFF support and ADIF import in the logbook, detachable Passes and Roves windows, and Kenwood TS-790 support.

## FT8 and FT4

* **FT8 mode for QO-100.** Selecting the QO-100 FT8 transponder switches the shared FT4 / FT8 window to FT8, updating the slot timing, early decode, waterfall slot lines, logbook mode, and OscarWatch.org and PSK Reporter spots. The **Tools** menu now has separate **OscarWatch FT4** and **OscarWatch FT8** entries that select the matching transponder before opening the window. Both FT8 and FT4 entries have been added to the shipped QO-100 database. (#221)
* **Configurable CQ word.** Send a short directed word between CQ and your callsign: POTA, SOTA, DX, TEST, or a custom word of 1 to 4 letters or 3 digits. The decoder and spot reporters now recognise directed CQs (such as `CQ POTA W1AW FN20`) so incoming stations are decoded and spotted cleanly. Compound callsigns without room for the extra word send a plain CQ and show a note in the window. (#260)
* **Newest decodes at the bottom.** A new option under **FT4 Settings → Appearance** (off by default) appends new lines at the bottom of Band activity and Calling me and follows them, in the style of WSJT-X, unless you have scrolled up to read earlier lines. (#257)
* **Slot dividers in the decode lists.** A thin horizontal rule separates each decode cycle in Band activity and Calling me (on by default under **Appearance**). (#258)
* **Saved uplink trim on the status strip.** Any active echo calibration stays visible as a signed hertz value on the frequency row for the satellite and transponder in use, so a leftover trim does not stay applied unnoticed. (#259)
* **Smoother auto-sequencing.** Calling a station that replies with their call and grid now moves straight to a signal report instead of repeating your grid. Contacts also complete after `RRR` once you send `73` or `RR73`, without waiting for a final courtesy `73`, and clicking an `RR73` decode sends `73`.

## CW

* **Automatic uplink alignment on linear transponders.** Click **Align** to send bounded callsign probes, measure your own echo on the downlink, and apply and save the TX offset automatically. Includes safety checks, undo, and a configurable maximum TX offset change cap in settings. The **Align** button stays in step between the CW decoder and the logbook. (#271)
* **Shared keyer strip in the decoder window.** Send your F1 to F12 macros right beside the live copy during a short pass without switching back to the logbook. Right-click any macro button to jump straight to keyer settings. The decoder window and keyer strip layout (including the WPM speed box) have also been tidied up. (#269)
* **N1MM-style live CW keyboard.** Press **Ctrl+K** (or click **Keyboard**) to open a type-ahead CW keyboard window alongside your macros.
* **`{SAVEQSO}` macro token.** Put `{SAVEQSO}` anywhere in a CW macro (for example after `TU 73`) to log the current entry once the message finishes sending. Stopping early with Escape or another key does not log it.
* **Adjustable noise filter.** Choose **Off**, **Normal**, or **Strict** in CW Settings to control how aggressively uncertain short noise bursts are filtered out.
* **Windows DeepCW startup fix.** Windows builds now ship `msvcp140.dll` and `msvcp140_1.dll` beside `onnxruntime.dll` so DeepCW starts on machines without the Visual C++ Redistributable installed.

## Logbook

* **POTA, SOTA, and WWFF references.** Logbooks and QSOs can record a POTA park, SOTA summit, and WWFF reference, with searchable suggestions from the official directories cached locally for offline use. New QSOs copy their logbook's references, and ADIF export writes `MY_POTA_REF`, `MY_SOTA_REF`, and `MY_WWFF_REF` (plus `MY_SIG` and `MY_SIG_INFO` for WWFF), while LoTW export omits them. The award fields stay hidden in the QSO form until you switch them on in Logbook Settings. (#250)
* **ADIF import.** Use **File → Import ADIF** to import standard `.adi` files (such as WSJT-X `wsjtx_log.adi`) into the current logbook, with a preview of new, duplicate, and unreadable records, an optional satellite-only filter, and duplicate conflict handling. Imported QSOs are not re-queued for Cloudlog, Satellite Link, or WSJT-X UDP. (#270)
* **Selected-QSO export and smarter date defaults.** Highlight multiple rows in the logbook to export only those QSOs to ADIF. When opening the Export ADIF dialogue, the date range now defaults to the UTC day of your latest QSO instead of spanning the entire logbook. (#247)
* **WSJT-X UDP log broadcast.** An off-by-default option on the **WSJT-X** tab of Logbook Settings broadcasts newly logged QSOs (manual, FT4/FT8, and Terminal) as WSJT-X schema 3 UDP datagrams (`QSO Logged` and `Logged ADIF`) with heartbeats for external loggers. (#248)
* **Send all QSOs to Cloudlog.** A new button on the **Cloudlog** tab of Logbook Settings uploads every QSO not yet in Cloudlog, oldest first, with progress reporting and duplicate protection.
* **Cleaner logbook menus.** Actions are now grouped under **File**, **Logbook** (with the active logbook name, New, Edit, Delete, and Switch logbook), and **Edit**, and the active logbook name appears in brackets in the window title.

## Radios, rotators, and APRS

* **Kenwood TS-790 cross-band CAT.** Added full-duplex Doppler control for the Kenwood TS-790.
* **First-pass satellite band setup on IC-9700, IC-9100, and IC-910.** Waits for satellite mode to settle and confirms via CI-V readback so the first **Resume CAT** on UHF-downlink passes such as RS-44 does not invert the Main and Sub bands. (#228)
* **IC-821H dual-radio band swaps.** Swaps Main and Sub cleanly when the IC-821H is paired as a dual-radio leg, without sending the unsupported `1A 07` satellite-mode command. (#245)
* **TX fixed receive retuning.** Tuning the receive dial in TX fixed mode now updates the uplink frequency instead of leaving the radio on the first programmed frequency, while still keeping Doppler off the uplink.
* **Shared separate COM ports.** FT4/FT8, Terminal PTT, and the logbook CW keyer can now share one separate COM port in-process (for example RTS for PTT and DTR for CW on a radio's second USB port).
* **SPID parking below the horizon.** SPID rotators can now park down to −10° elevation, with controller-specific limits and guidance in Settings. (#254)
* **APRS-IS gateway reconnect fix.** Stops reconnect loops by gating packets only after the server verifies the passcode, sending a 20-second idle keepalive, and backing off cleanly on dropped connections.

## Layout and transponder database

* **Detachable Upcoming Passes and Roves windows.** Pop either sidebar list out into its own window for a second monitor. Window size, position, and detached state are remembered, and the floating window stays open even when the sidebar section is collapsed. (#266, #267)
* **Toggle the DX Station Monitor panel.** Click the DX station map button again to hide the panel without clearing your entered grid. (#262)
* **Satellite operating notices.** Satellites can carry scheduled notices in the transponder database (such as the SO-131 Wednesday UTC FSK digital-modes schedule), synced on update and shown as an amber banner in the frequency panel, the Live sidebar, and matching upcoming passes.
* **Microwave frequencies in the transponder editor.** Raised the frequency cap from 1.3 GHz to 300 GHz so QO-100 and other microwave transponder rows can be entered directly.
* **Transponder database updates.** Remote database updates now point to `oscarwatch.org`, and the bundled database updates ISS UHF Packet to 437.825 MHz alongside the new QO-100 FT8/FT4 entries and SO-131 notice.

**Full Changelog**: https://github.com/magicbug/OscarWatch-Tracker/compare/v1.1.8...v1.1.9
