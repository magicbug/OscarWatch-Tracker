# ft4_coder (native)

Cross-platform FT4 codec shared library for OscarWatch. Phase 0 builds the library and runs an encode smoke test in CI on all five publish RIDs.

## Source

Compiles WSJT-X Fortran from [paulh002/wsjtx_lib](https://github.com/paulh002/wsjtx_lib) (GPLv3, K1JT et al.) at configure time via CMake `FetchContent`, then exposes a thin C shim:

- `encode_ft4` — implemented (48 kHz, 241920 samples)
- `decode_ft4` — exported stub in Phase 0 (full decoder wiring follows)

## Local build

```bash
cd native/ft4_coder
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
ctest --test-dir build --output-on-failure
```

### Dependencies

| Platform | Packages |
|----------|----------|
| Linux | `cmake`, `g++`, `gfortran`, `libfftw3-dev` |
| macOS | `brew install gcc fftw` (use `gfortran` from gcc formula) |
| Windows | MSYS2 MinGW64: `mingw-w64-x86_64-{gcc,gfortran,cmake,fftw,make}` |

## CI

`.github/workflows/build-ft4-native.yml` builds and smoke-tests on:

- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Artifacts: `ft4_coder` shared library + `ft4_smoke` per RID.

## Licensing

WSJT-X Fortran is GPLv3. OscarWatch FT4 addon must comply with GPLv3/AGPL obligations when linking this library. See project `THIRD_PARTY_NOTICES` (to be added when the addon ships).
