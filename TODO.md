# OscarWatch — application TODO

Tracked ideas and deferred work. Not a commitment order; items may be split or dropped.

## General

- [ ] Small dialog for triggering keyer memories on the IC-9700

## Radio / rig

See [building radio drivers](documents/building-radio-drivers.md) for adding rigs.

**Per new driver:** protocol client, `IRigDriver` + `RigType` + Settings list, pass init (SAT/split/VFO/mode/CTCSS), `RigController` hooks, tests + hardware smoke test.


## Larger projects (lower priority)

- [x] In-app satellite FT4 modem (Tools → FT4): duplex decode/TX via ft8_lib, PTT methods, auto-sequence, logbook save
- [x] Native SSTV receive (Tools → OscarWatch SSTV): Robot, Scottie, Martin, Wraase SC2-180 and PD modes, auto mode, slant correction, AFC, decode recordings
- [ ] Built-in packet interface

---