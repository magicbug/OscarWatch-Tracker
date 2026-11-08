# Testing the Terminal built-in packet modem

The built-in modem (`OscarWatch.Core/Terminal/Modem`, `OscarWatch/Terminal/BuiltInModemTransport.cs`) implements 1200 baud AFSK and 9600 baud G3RUH. It has three layers of checks. The first runs on every build; the other two need files or hardware.

## 1. Unit tests (always run)

```powershell
dotnet test OscarWatch.Tests --filter "FullyQualifiedName~Terminal"
```

These cover the demodulator bank (including optional ±175/±350 Hz offset banks), HDLC and bit fixing, the resampler, the transmit path (frame chaining, PTT sequencing, watchdog, CSMA, half duplex blanking and guard, the FT-847 hold callback), settings clamping and link type classification.

## 2. Offline corpora (run when the files are present)

| Test class | Needs | What it checks |
|----|----|----|
| `Wa8lmfBenchmarkTests` | `OSCARWATCH_TNC_TEST_CD` set to the track folder, or to `1` for the `testdata/wa8lmf` folder that `tools/fetch-tnc-test-cd.ps1` fills. Opt-in locally because the tracks take minutes to decode. A version tag (`v*`) runs this job in the Publish workflow before packages are built. | Decode counts on WA8LMF TNC Test CD tracks 1 to 4 against fixed gates. Put `direwolf-atest.txt` beside the tracks to print the difference from a local Direwolf run. |
| `DirewolfInteropTests` | `OSCARWATCH_DIREWOLF_DIR` pointing at a Direwolf build folder with `atest` and `gen_packets` | At 1200 baud: Direwolf decodes every frame our AFSK modulator produces, and we decode at least 95% as many `gen_packets -n 100` frames as Direwolf (bit fixing off). At 9600 baud: same checks with `-B 9600` and `G3ruhReceiver` (50 frames). |
| `G3ruhChannelSimulationTests` | (always) | Synthetic 9600 loopback, noise, DC drift, polarity inversion, and clock offset. |
| `G3ruhDopplerRobustnessTests` | (always) | Sudden discriminator steps (mid-frame and between frames), constant offset, and linear ramp; documents `G3ruhDopplerTolerance`. |
| `G3ruhHighPassMeasurementTests` | (always) | Checks the 12 Hz DC blocker retains most scrambled 9600 baseband power. |

Direwolf's Windows build is on the [Direwolf releases page](https://github.com/wb2osz/direwolf/releases). With 1.8.1 the interop test gave 20 of 20 frames decoded by Direwolf, and 73 (OscarWatch) against 72 (Direwolf) on the `gen_packets -n 100` set.

Never play the TNC Test CD or any generated WAV over the air.

## 3. Hardware smoke tests (manual)

Run these before a release that changes the modem, PTT keyer or Terminal transport. Record the radio, interface, Windows audio devices and results in the pull request.

### IC-9700 on the ISS (simplex, half duplex)

1. Rig control on, IC-9700, data port audio (USB audio codec), PTT method CAT.
2. Terminal settings: Connection set to the built-in soundmodem, Duplex on Automatic. The status bar should say half duplex for the ISS.
3. Monitor input with the squelch open on 145.825 MHz and set the level to 30 to 70%.
4. Test tone into a dummy load: check about 3 kHz deviation and that PTT drops after 5 seconds.
5. During a pass, send a position and a message. Check:
   - the radio keys once per send, with no stuck PTT;
   - the digipeated copy appears with a Delay value;
   - received frames show a Q percentage.
6. Pull the USB audio cable while idle, then plug it back in. Record what Terminal shows and whether Disconnect then Connect brings the modem back.

### Crossband digipeater (full duplex)

1. Use a satellite whose profile is Crossband (or set the link type in the profile list), with uplink and downlink on different bands.
2. The status bar should say full duplex. Send a frame and check your own digipeated copy is decoded while the transmission is still in progress or straight after it.

### Yaesu FT-847 on a same-band satellite

1. Rig control on, FT-847, single radio, PTT method CAT or a separate COM port.
2. Focus the ISS. Send a frame. With CAT logging on, check the order:
   - the radio moves to the uplink frequency;
   - PTT on;
   - PTT off;
   - the radio returns to the downlink frequency.
3. Disconnect the CAT cable and send again. The status line should say the uplink could not be held and nothing is transmitted.

### Stuck PTT recovery

1. Key a long test tone with PTT method CAT, then end OscarWatch from Task Manager while keyed.
2. Start OscarWatch again. Once rig control connects, the radio should drop to receive and Terminal should show the recovery notice.
3. Set a transmit time-out timer on the radio as well; the software recovery is not a substitute for it.
