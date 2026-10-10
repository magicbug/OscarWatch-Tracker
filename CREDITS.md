# Credits

## Earth imagery

The default `OscarWatch/Assets/Maps/world_map.jpg` is an equirectangular Blue Marble–style texture suitable for map overlay rendering. Replace with NASA Marble `world_map.jpg` from KDE Marble if you prefer that asset locally.

- NASA Blue Marble / Visible Earth imagery — public domain ([NASA Earth Observatory](https://earthobservatory.nasa.gov/))
- KDE Marble project — map tiling and `world_map.jpg` convention ([Marble](https://marble.kde.org/))

## Audio

- [PortAudio](https://www.portaudio.com/) via PortAudioSharp2: cross-platform capture for pass recording, OscarWatch FT4 and OscarWatch SSTV
- Optional [ffmpeg](https://ffmpeg.org/) on PATH: converts finished pass recordings to MP3 with libmp3lame when File format is MP3 (not bundled)
- [ft8_lib](https://github.com/kgoba/ft8_lib) by Kārlis Goba (MIT): FT4/FT8 encode and decode, vendored under `native/ft8_lib` and shipped as `oscarwatch_ft8`

## Orbit propagation

- [OrbitTools](http://www.zeptomoby.com/satellites/) by Michael F. Henry — NORAD SGP4/SDP4 (Public Edition via NuGet for non-commercial use)

## TLE data

- Amateur satellite TLEs from [tle.oscarwatch.org](https://tle.oscarwatch.org/)

## UI framework

- [Avalonia UI](https://avaloniaui.net/)

## Localisation

- **Igor Monteiro (PU4ELT)** — Brazilian Portuguese (`pt-BR`) user interface localisation
- **Carlos (EA3HAH)** — Spanish (`es`) user interface localisation (newer strings completed with AI assistance)
- Thai (`th`) user interface localisation (AI-assisted; pending native-speaker review)
- Indonesian (`id`) user interface localisation (AI-assisted; pending native-speaker review)
- Russian (`ru`) user interface localisation (AI-assisted; pending native-speaker review)
- German (`de`) user interface localisation (AI-assisted; pending native-speaker review)

## Hardware testing

- **Abdel (M0NPT)** and **Joe (KE9AJ)** — Yaesu FT-847 CAT driver
