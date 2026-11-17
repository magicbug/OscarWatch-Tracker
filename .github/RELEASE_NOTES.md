# OscarWatch Tracker v1.1.8 Release Notes

A big thank you to our newest supporters, **Steve N9IP**, **Lars SM0TGU**, and **Jordi EB3JT**. Your support keeps OscarWatch moving, and thank you as well to everyone who has already donated.

Welcome to version 1.1.8. This one is for the dual-radio desk, the CW operator, and anyone who woke up to find SO-131 on the map with no modes: satellite radios as Dual radio legs, live CW decode and a logbook keyer, SSTV that follows a timing step instead of tearing the picture, and a transponder-database merge that keeps up when a bird gets its Oscar number.

Thanks also to **Dave G4DPZ** for the SSTV timing and adaptive smoothing work that landed in this release.

## Dual radio

* **Satellite radios as Dual radio legs.** The IC-910, IC-9100, IC-9700, IC-821H, FT-847, and TS-2000 can pair with another radio. OscarWatch turns satellite mode off and tunes one VFO only, so you can run an IC-9700 with a TS-990S (or similar) without fighting the radio's own sat mode. (#234, #235)
* **Icom LAN on a Dual radio leg.** Saving Settings no longer fails with a COM-port hint when the downlink is an Icom (or Kenwood) network radio. The conflict check now sees the same LAN fields as Save.

## CW

* **Live CW decoding.** Decode soundcard audio locally with DeepCW: a tunable waterfall, Doppler correction, note following, and noise squelch. Automatic pass-based listening, transcript export, and logbook hooks for decoded callsigns and locators. Capture is shared with SSTV.
* **CW keyer in the logbook.** Twelve named macros (F1 to F12) sit between the QSO entry and the history when the mode is CW. Kenwood uses KY text, Icom uses CI-V 0x17, and other radios key DTR or RTS on the CAT port or a separate COM port. Speed is set with the WPM box. (#154)

## SSTV

* **Timing steps without a torn picture.** A gap or repeat in the recording (or a pause in the sender's audio) used to pull every later sync off the grid. The decoder now starts a new stretch when a confirmed step lands, so the lower half of the picture stays locked. On a real ISS PD180 with timing steps, lock rose from about 92% to 98%.
* **Adaptive smoothing for weak pictures.** Speckle is blended out where the neighbourhood is noisy, and left alone on a strong pass. On by default; ignored when Smooth across lines is on. Fine horizontal detail is no longer mistaken for noise.
* **Noise looks like noise.** Pure noise no longer paints as bright random colour. Unsteady envelope fades towards dim grey, closer to what MMSSTV shows, while a usable picture keeps its colour.
* **Shared pictures keep the NORAD ID.** Uploads include the catalogue number when it is known, so a renamed bird still lands in the right gallery on OscarWatch.org.

## Radios and passes

* **FT-817 DATA jack for Digirig FT4.** Catalogue DATA-USB and DATA-LSB select DIG (and PKT for DATA-FM) so Digirig and similar interfaces can TX. Sideband stays on the USER-U / USER-L menu to avoid EEPROM writes. (#241)
* **Icom serial port not found.** A missing or busy CI-V COM port now surfaces the port-not-found guidance instead of a generic "CI-V not responding" message.
* **Stale TLE warning in the pass list.** A pass row is marked when the satellite's element set is older than a period you set on the TLE tab (1 to 365 days, default 14). The satellite picker uses the same period. Thank you, Dave G4DPZ.
* **UHM while the bird is up.** Terminal UHM posts and match polling wait until the focused satellite is above the reportable elevation, and show Below horizon when it is not.

## Satellites and the transponder database

* **Oscar designations.** UNNE-1B is now **SO-131**, HADES-L is **SO-132**, Erminaz-2U / 2V are **AO-129** / **AO-130**, and Luca is **LUCA-1**, with former names kept as alternatives. Modes and aliases were refreshed for those birds; RADFXSAT (FOX-1B) was removed from the shipped database.
* **Official renames on Update transponder database.** After a TLE update to the new Oscar name, merge used to match the old local entry and offer nothing, so SO-131 showed "No transponder data". The merge now offers the official preferred-name rename when the published entry lists your old name under alternative names, and it replaces a stale NORAD ID when the catalogue number changed. Run **Satellites → Update transponder database…** (or the startup check) and apply the rename.

**Full Changelog**: https://github.com/magicbug/OscarWatch-Tracker/compare/v1.1.7...v1.1.8
