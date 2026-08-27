# Satellite transponder database

OscarWatch keeps **orbits** (TLEs) and **frequencies/modes** (transponder database) separate. This document describes where that data lives, how updates are published, and how in-app sync works.

## Data sources

| Layer | Location | Role |
|-------|----------|------|
| **Remote** | [tle.oscarwatch.org/satellite_database.json](https://tle.oscarwatch.org/satellite_database.json) | Canonical published updates — new satellites and new/edited transponder modes |
| **Bundled** | `OscarWatch/Assets/satellite_database.json` | Shipped with each app release; fallback when no user file exists |
| **User** | `%AppData%/OscarWatch/satellite_database.json` | Your working copy after first edit or after a merge from the remote URL |

TLEs are fetched from the same host: [tle.oscarwatch.org](https://tle.oscarwatch.org/).

## Current app behaviour

The app loads **user → bundled** (see `SatelliteDatabaseService`).

- **Satellites → Update transponder database…** (or **Check for updates** in the database editor) downloads the remote file and opens a merge dialog.
- **Settings → Tracking → Check for transponder database updates on startup** (on by default) runs the same check when the app opens; the dialog appears only when updates exist.
- New satellites and modes from the server are **selected by default**; you can uncheck any before applying.
- **Conflicts** (same satellite name and mode type, different fields): tick **Use local version** and apply to confirm your edit (acknowledged in `settings.json`; the prompt returns only if local or published data changes). Tick **Use published version** to replace your copy with the server fields.
- **Restore defaults** deletes the user file and reloads the **bundled** database from the installation — not the remote URL.
- **Import JSON…** / **Export JSON…** in the editor (file picker): export the current editor contents; import merges a chosen file into the editor (same merge UI as remote sync). Press **Save** in the editor after import to persist to `%AppData%`.
- **Add satellite** opens a searchable list of TLE catalogue names not already in the database, or you can enter a custom name.

## JSON schema (per mode)

```json
{
  "name": "UmKA-1 (RS40-S)",
  "norad_id": "57172",
  "alternative_names": ["UmKA-1", "RS40-S"],
  "modes": [
    {
      "type": "FM VOICE",
      "downlink": 436795,
      "uplink": 145850,
      "downlink_mode": "FMN",
      "uplink_mode": "FMN",
      "doppler": "NOR",
      "ctcss": 67.0,
      "ctcss_arm": 74.4
    }
  ]
}
```

| Field | Notes |
|-------|--------|
| `name` | Preferred operator-facing label (map, passes, editor). Does not have to match the TLE catalogue name when `norad_id` or `alternative_names` link the entry. |
| `norad_id` | Optional. NORAD catalogue number as a string (e.g. `"27607"`), matching [tle.oscarwatch.org](https://tle.oscarwatch.org/). Prefer this as the stable identity when the display name differs from the TLE name (e.g. `RADFXSAT (FOX-1B)` → `43017` for AO-91). |
| `alternative_names` | Optional. Extra names for TLE / published-database matching (nicknames, previous names after a rename). Case-insensitive; must not duplicate `name` or another satellite’s name/alias. |
| `type` | Label shown in the frequency panel mode list; unique per satellite |
| `downlink` / `uplink` | kHz; use **`0` uplink** for beacon-only / receive-only (SSTV, telemetry, CW beacon). OscarWatch treats these as receive-only for CAT (no uplink doppler; ICOM rigs exit satellite mode and tune Main on the downlink band). |
| `downlink_mode` / `uplink_mode` | Rig mode strings: `FM`, `FMN`, `USB`, `LSB`, `CW`, `DATA-USB`, `DATA-LSB`, `DATA-FM` (`FM-DATA` is accepted and stored as `DATA-FM`) |
| `doppler` | `NOR` or `REV` |
| `ctcss` / `ctcss_arm` | Optional Hz; FM access and arm tones |

**Publishing rules:**

- Frequencies must be JSON **numbers**, not strings (`437350` not `"437350"`).
- Validate with the in-app editor or `SatelliteDatabaseFile.ValidateEntries` before publishing.
- Prefer stable `type` names so merges do not create duplicate modes after renames.

## Name matching and TLEs

Lookup and remote merge prefer **NORAD catalogue ID** when both sides have one, then match by **preferred name** or any **`alternative_names`** entry. That lets you rename a satellite for the map without the next published update treating it as a new spacecraft.

For frequency lookup, OscarWatch also registers common static aliases (e.g. `AO-7` → `AO-07`, `ISS (ZARYA)` → `ISS`, `FOX-1B` → `RADFXSAT (FOX-1B)`, `SAUDISAT 1C` → `SO-50`) and parenthetical prefixes. Optional `norad_id` remains the most reliable cross-catalog link (for example Celestrak `SAUDISAT 1C` → database `SO-50` via `27607`).

When a database entry is found, the map and pass list show the preferred `name` rather than the raw TLE catalogue name. If a spacecraft has TLEs but no database entry, the frequency panel stays empty until an entry exists locally or via sync, and the map keeps the TLE name.

## Contributing updates

1. **GitHub** — pull requests updating `satellite_database.json` / `OscarWatch/Assets/satellite_database.json` (and the repo root copy if kept in sync).
2. **Remote publish** — maintained copies on [tle.oscarwatch.org](https://tle.oscarwatch.org/) for operators between app releases.

Include a short note in the PR: source (official satellite page, operator reports, coordination with AMSAT/ARISS, etc.). Frequency changes on active spacecraft should reflect confirmed operating status, not rumour.

## Privacy and network

Remote sync is a **read-only HTTPS GET**. No station data, callsign, or settings are uploaded. Merge prompts are the consent gate for writing anything to `%AppData%`.

## Related code

| Piece | Path |
|-------|------|
| Load / lookup | `OscarWatch.Core/Services/SatelliteDatabaseService.cs` |
| JSON I/O | `OscarWatch.Core/Services/SatelliteDatabaseFile.cs` |
| Editor | `OscarWatch.Core/Services/SatelliteDatabaseEditor.cs` |
| UI | `OscarWatch/Views/SatelliteDatabaseWindow.axaml` |
| Remote URL constant | `SatelliteDatabasePaths.RemoteDatabaseUrl` |
