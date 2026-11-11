# OscarWatch Tracker v1.1.7 Release Notes

A big thank you to our newest supporters, **William WT0DX** and **Mark ZL3AB**. Your support keeps OscarWatch moving, and thank you as well to everyone who has already donated.

Welcome to version 1.1.7. This one is for the operators who sit with a pass from AOS to LOS: SSTV that holds together through the fades, an FT4 waterfall you can actually read on a busy downlink, network control for several Icom and Kenwood radios, and a troubleshooter for the morning OscarWatch will not start.

Thanks also to **Dave G4DPZ**, who found the SSTV decoder holding on to the memory of a long picture and sent the fix.

## SSTV

Satellite pictures were being cut short or split when the pass faded into noise. The defences that deal with that are now on by default, and an existing settings file is updated once so they switch on for you.

* **Tone sync, hold through fades, and mode-matched filtering** stay with the picture when the signal drops.
* **Doppler from the satellite:** the line prior is fed from the focused satellite's range rate.
* **Headers in the noise:** a VIS leader buried in the noise is read from the tone on each bit. On a simulated FM link that is about 2 dB deeper, and on recorded passes several pictures land on the right rows.
* **Sync when the pulses are missing:** if a fade takes out too many syncs for a normal run, the search stacks the last few lines at that mode's line period.
* **FM clicks count for less:** pixels are weighted by subcarrier power, so an envelope dip does not drag the line.
* A fade hold lasts up to 45 seconds, then trims back to the last lock if the signal never returns.

**Optional speckle smoothing** (SSTV Settings, off by default). When a picture finishes, each pixel is replaced by the middle value of its neighbours on the lines above and below. That lifts lone FM noise dots on a weak picture. It softens fine detail and small text, so leave it off when the signal is strong.

**Soundcard overruns.** SSTV no longer asks for the lowest input latency. On raw ALSA that was silently dropping about a quarter of the audio and cutting every picture. Capture now asks for a safer buffer, and the log warns when the soundcard overflows or decoding falls behind.

**A steadier waterfall.** Every spectrum row is kept and drawn, timed from the audio, and each row is an average across its audio instead of one short FFT taken wherever a chunk happened to end.

**Memory after a long picture.** A long PD picture no longer leaves the decoder sitting on hundreds of megabytes once the picture is finished. Idle listening settles back to the search buffers. Thank you, Dave G4DPZ.

**Settings tabs.** SSTV Settings is now split like FT4: General, Decoding, Fades and noise, and Saving. The settings are the same, and easier to find.

**Keep the computer awake** (off by default). Overnight listening could lose USB radio audio when the screen saver or idle sleep started (#214). Tick this and a live SSTV session holds the system and the display awake on Windows, macOS, and Linux.

## FT4

* **Waterfall speed.** Slow and Medium keep about 48 and 24 seconds of the pass on screen. Fast is the original 12-second scroll, and it stays the default.
* **Weak traces next to a strong one.** Radio AGC used to push the noise, and the weak stations, below the waterfall floor, so they vanished even while they kept decoding. The floor now follows the level within about ten lines. Each line averages several FFTs so the noise is steadier. **Flatten waterfall** (on by default, under FT4 Settings, Appearance) evens out the radio's SSB filter shape, in the same spirit as WSJT-X Flatten.
* **Slot lines, hover, and double-click.** A line marks the start of each 7.5-second slot, with the slot's UTC time. Hover shows the audio frequency a click would tune, in 10 Hz steps. Double-click a trace to tune it and decode that slot again with the deep decoder, which can pick up a weak station the early decode missed. **Slot lines with UTC times** can be turned off under Appearance.
* **Advanced decode layout.** Basic keeps the single list. Advanced (FT4 Settings, Appearance) shows band activity on the left and, on the right, stations calling you plus your own transmissions. Your own echoes appear in that calling-me column. The first echo line stays unless a later copy is nearer the TX marker.
* **Lists that fit.** Hz sits with the other numbers, as in WSJT-X, so a narrow window only shortens the message. Column titles are centred, and times are not cut off as you raise the font size. Drag the column divider where you want it. The FT4 window remembers its size and position.

## Radios over the network

* **Icom CI-V over LAN or Wi-Fi, with no audio.** The IC-9700, and the IC-705, IC-905, and IC-7300MK2 when they are a dual-radio leg, can be driven without a USB CI-V lead. Settings gain a Serial or Network choice, a login, and a connection test. Network passwords are left out of the diagnostics bundle. Turn on Network Control on the radio, and close RS-BA1 or wfview first: the radio accepts one remote client. (#163)
* **Kenwood HF as a dual-radio leg.** The TS-990S, TS-890S, TS-590S, and TS-590SG can be the downlink or the uplink, on 10 m directly or on 2 m and 70 cm through the radio's own transverter. OscarWatch reads the transverter offset and leaves it under your control. Each radio is a dual-radio leg (downlink or uplink). The TS-990S and TS-890S can connect over LAN (TCP port 60000) as well as COM. The TS-590S and TS-590SG are COM. (#205)

## Rotators

* **AntRunner over GRBL.** Drive a MuseLab AntRunner on its serial port (X azimuth, Y elevation, 360 degrees is 40 mm), without Hamlib rotctld. (#177)
* **Azimuth only for GS-232.** Controllers with no elevation, such as an RTC-59 with an Emoto 1300MSA, use the azimuth commands. With the setting on, elevation is ignored for tracking, parking, and keyhole avoidance, and it is hidden in Settings, the sidebar, and the manual Rotator window. (#190)
* Settings hints now show only the note for the protocol you have selected.

## OscarWatch Troubleshooter

A small separate application, shipped beside OscarWatch on Windows, macOS, and Linux, for the morning the tracker will not start. It shows process, settings, device, and log status, opens the data and log folders, copies or saves redacted diagnostics, closes or starts a stuck OscarWatch, turns off the radio, rotator, and GPS, and can put settings aside (with their backups, so they are not put straight back).

Also in this version:

* **Clear all FT4 echo calibrations** without OscarWatch running, when a bad trim has left you transmitting off frequency and you cannot reach FT4 settings. It takes a dated backup first.
* **GS-232 probe:** read position, stop or nudge a serial GS-232 rotator from the saved settings, and export a byte-level log for support.
* Localised findings, freeze reports, restoring a settings backup, and optional rotator traffic recording.
* Bug reports now point at the troubleshooter. On Linux, logs live under `~/.config/OscarWatch/logs/`.

## Passes and names

* Closing the pass planner no longer writes its minimum elevation, duration, and prediction window into the filters the sidebar and timeline use. Planner filters stay on the planner. Scheduled passes are still matched.
* A short Meteor name no longer selects the rest of that family. ISS and SO-50 still follow their catalogue names.

**Full Changelog**: https://github.com/magicbug/OscarWatch-Tracker/compare/v1.1.6...v1.1.7
