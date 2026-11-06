# Test plan: Kenwood TS-990S, TS-890S and TS-590 as dual-radio legs

Hardware and UI checks for [issue #205](https://github.com/magicbug/OscarWatch-Tracker/issues/205). The automated tests cover the CAT strings and a simulated LAN radio. This list covers what only a real radio and the real Settings window can show.

Tick each line, and note the radio, firmware version and connection used. If something fails, save the log (Help → Troubleshoot, or the log folder) and note the time.

## Before you start

- [ ] Close OscarWatch and rebuild, so the new Settings panels are in the build you test.
- [ ] Note the radio firmware versions:
  - TS-990S:
  - TS-890S:
  - TS-590S:
  - TS-590SG:
- [ ] Close ARCP, KNS software, logging software and anything else that uses the radio's COM port or port 60000.
- [ ] On each radio, note the COM baud rate menu setting.
- [ ] For LAN, set an administrator ID and password (and, on the TS-890S, a user ID) in the radio's LAN menu.
- [ ] Have a dummy load or low power ready for any transmit checks.

## 1. Settings window

Open **Settings → Radio** and tick **Dual radio**.

### Radio lists

- [ ] The downlink and uplink lists both show **Kenwood TS-990S**, **Kenwood TS-890S**, **Kenwood TS-590S** and **Kenwood TS-590SG**.
- [ ] None of the four appear in the single-radio list (Dual radio unticked).
- [ ] Picking one of the four sets the baud rate to **115200**.

### Connection choice

- [ ] TS-990S and TS-890S show a **Connection** choice: COM port or Network (LAN).
- [ ] TS-590S and TS-590SG show no Connection choice and no LAN fields.
- [ ] With **COM port** chosen: serial port, baud rate and **Hardware RTS (flow control)** are shown. LAN fields are hidden.
- [ ] With **Network (LAN)** chosen: host, port, account, password and **Test LAN connection** are shown. COM port, baud and RTS are hidden.
- [ ] **Administrator account** appears on the TS-890S only (not the TS-990S).
- [ ] **Region** stays visible on the downlink leg when LAN is chosen.
- [ ] The port defaults to **60000**.
- [ ] The password field shows dots, not the password.
- [ ] The Kenwood hint text appears under the panels when either leg is one of the four radios, and disappears otherwise.

### Saving and loading

- [ ] Set a TS-890S downlink on LAN (host, port, account, password, administrator unticked) and a TS-590SG uplink on COM with RTS unticked. Save, close and reopen Settings. Every value is kept.
- [ ] Restart OscarWatch. Every value is still kept.
- [ ] Switch the downlink to **SDR (rigctl)**. The SDR host and port show the SDR defaults (127.0.0.1 / 4532), not the Kenwood LAN values.
- [ ] Switch back to the TS-890S. The LAN values are still there.

### COM port conflicts

- [ ] Set both legs to COM on the same port. OscarWatch warns about the conflict.
- [ ] Change one leg to LAN, keeping the same (hidden) COM port. The warning goes away.
- [ ] Set a rotator or GPS on the COM port that a LAN leg used to have. No conflict is reported.

### Languages

- [ ] Switch the UI language to at least two others (for example German and Japanese). The new labels, buttons and hint are translated and nothing shows a raw key such as `Settings.Radio.KenwoodLanHost`.

## 2. COM connection (each radio)

Repeat for each radio you have. Use one radio as the downlink leg and a known-good radio (or Dummy uplink) on the other leg.

| Check | TS-990S | TS-890S | TS-590S | TS-590SG |
|----|----|----|----|----|
| Connects at 115200 with RTS on | | | | |
| Connects with RTS off (if your cable allows) | | | | |
| Wrong baud rate gives a clear connect failure, not a hang | | | | |
| Wrong model selected (for example TS-890S chosen, TS-590SG connected) refuses to connect | | | | |
| Unplugging the USB cable mid-pass is reported and reconnects after plugging back in | | | | |

## 3. LAN connection (TS-990S and TS-890S)

### Test button

- [ ] Correct details: shows "Connected to the TS-… at … MHz" with the radio's actual frequency.
- [ ] Wrong password: shows "Login rejected".
- [ ] TS-890S with a user ID and **Administrator account** unticked: connects.
- [ ] TS-890S with a user ID but **Administrator account** ticked: login rejected.
- [ ] ARCP (or KNS) already connected: shows "The radio refused the connection".
- [ ] Wrong IP address: fails within a few seconds with a clear message (no long freeze).
- [ ] Empty account or password: asks you to fill them in.
- [ ] TS-990S with an account or password longer than 8 characters: gives a clear message.
- [ ] TS-990S with a non-ASCII character in the password (if the radio allows one): connects.

### During tracking

- [ ] Start tracking over LAN. The log shows "Kenwood LAN session open".
- [ ] Leave the radio idle for at least 30 seconds (no satellite, or CAT paused). The session stays up (no reconnect messages in the log).
- [ ] Pull the network cable (or turn off Wi-Fi) for 15 seconds, then restore it. OscarWatch reconnects and carries on tuning.
- [ ] Power the radio off and on again during a pass. OscarWatch reconnects once the radio is back.
- [ ] Open ARCP while OscarWatch is connected. Note what happens (the radio should refuse ARCP).
- [ ] Stop tracking and quit. The radio accepts a new ARCP connection straight away.

## 4. Transverter mode

Set this on the radio before the pass. OscarWatch should never change it.

### 10 m (transverter off)

- [ ] Transverter off. A 10 m satellite (or a 29 MHz test frequency via the frequency panel) puts the radio dial on that exact frequency.

### 2 m / 70 cm (transverter on)

Note the offset you set on the radio: ______ (plus / minus)

| Check | TS-990S | TS-890S | TS-590S | TS-590SG |
|----|----|----|----|----|
| Log at pass start shows "transverter on, offset +… Hz" with the right value | | | | |
| OscarWatch shows the satellite frequency (for example 145.900 MHz), not the IF | | | | |
| Radio display shows the expected frequency for its transverter setting | | | | |
| The IF on the radio (or transverter) is satellite frequency minus offset | | | | |
| Doppler moves in the right direction on the radio | | | | |
| Minus offset (if the radio supports it) inverts correctly | | | | |

- [ ] TS-590S: transverter menu **50** set to 1, then to 2. Both are treated as on.
- [ ] TS-590SG: transverter menu **56** set to 1, then to 2. Both are treated as on.
- [ ] Turn transverter mode off on the radio mid-pass, then change the satellite mode. OscarWatch tunes 10 m directly again.
- [ ] Change the offset on the radio mid-pass, then change the mode. The log shows the new offset and tuning follows it.
- [ ] After the pass, the radio's transverter setting and offset are exactly as you left them.

## 5. On-air passes

Record the satellite, date and time for each.

### 10 m pass (a 2 m / 10 m satellite, or a 10 m test frequency)

- [ ] Downlink leg on 10 m tracks Doppler smoothly.
- [ ] Passband trim (turning the dial) is followed, as on other dual-radio legs.

### Linear U/V or V/U with a transverter leg

- [ ] Downlink and uplink both track through the pass.
- [ ] SSB mode is set correctly (USB or LSB per the transponder) on the Kenwood leg.
- [ ] CW works with the CW sideband setting.
- [ ] No unexpected beeps, menu changes or VFO swaps on the radio.

### FM pass

- [ ] FM mode set on the Kenwood leg.
- [ ] Uplink tone: OscarWatch does not set CTCSS on these radios. Confirm you set it on the radio and the satellite opens.

### Combinations

- [ ] Kenwood downlink + another radio uplink (for example FT-818).
- [ ] Another radio or SDR downlink + Kenwood uplink.
- [ ] Two Kenwood radios (one COM, one LAN if you have both).

## 6. Things that should not happen

- [ ] OscarWatch never changes RF power on these radios.
- [ ] OscarWatch never changes split, the sub receiver or VFO B.
- [ ] OscarWatch never changes transverter on/off or the offset.
- [ ] No CAT commands are sent before the radio identifies itself (check the log at connect: `ID` first).
- [ ] CAT pause stops all frequency writes.
- [ ] Quitting OscarWatch leaves the radio usable and the COM port free.

## 7. Regression checks

The dual-radio connection code and COM conflict checks were touched, so quickly confirm existing setups still work.

- [ ] FT-817/818 pair in Dual radio connects and tracks.
- [ ] An ICOM leg (IC-705 or IC-7300) in Dual radio connects and tracks.
- [ ] SDR (rigctl) downlink with a serial uplink connects, and the SDR test button still works.
- [ ] TS-2000 in single-radio mode connects and enters SATL.
- [ ] TH-D74/TH-D75 leg still connects.
- [ ] Connect errors for serial legs still name the COM port. For LAN and SDR legs they name host:port.

## Results

| Area | Pass / fail | Notes |
|----|----|----|
| Settings window | | |
| COM connection | | |
| LAN connection | | |
| Transverter mode | | |
| On-air passes | | |
| Things that should not happen | | |
| Regression checks | | |

Tested by: ____________ Date: ____________
