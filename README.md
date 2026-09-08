# free-polar-align

Plate-solving polar alignment for German equatorial mounts. Point the telescope
anywhere reasonable, rotate the RA axis a few times, and the software tells you
which way to turn the altitude and azimuth bolts, updating live as you turn them.

Windows is the shipping target. The codebase builds and its core is testable on
macOS and Linux.

---

## How it works

The mount's RA axis points somewhere. Rotate in RA only, and the telescope's
pointing direction traces a small circle on the sky whose pole *is* the mount's
polar axis. Plate solve three or more positions, fit the circle, compare its pole
to the true refracted pole, and you have the misalignment in altitude and azimuth.

Three properties of this method are worth knowing up front, because they shape
the code:

**Cone error is irrelevant.** The optical axis can be badly misaligned with the
declination axis and the fit still recovers the polar axis correctly, because the
optical axis is simply *fixed* in the rotating mount frame. Nothing needs to
model or calibrate it.

**Sidereal tracking is harmless.** Tracking is rotation about the same RA axis,
so tracked and untracked captures are the same measurement. We leave tracking on
to avoid star trailing, and timestamp exposure midpoints.

**Declination must not move between captures.** If it does, the optical axis is no
longer fixed in the mount frame and the fit is invalid. This is the one operator
error that silently produces a plausible wrong answer, so it is detected and
warned about rather than trusted.

---

## Architecture

Layered, with the valuable part (the math) isolated from the messy part (the
hardware).

```
┌───────────────────────────────────────────────────┐
│  FreePolarAlign.App          Avalonia UI          │
├───────────────────────────────────────────────────┤
│  FreePolarAlign.Session      state machine        │
│                               commands in,         │
│                               event stream out     │
├──────────────┬────────────────┬───────────────────┤
│ .Core        │ .Solving       │ .Devices          │
│ math, no I/O │ ISolver        │ ICamera           │
│              │                │ IMount            │
└──────────────┴────────────────┴───────────────────┘
                                       ▲
                        plugins/ ──────┘
                        loaded at runtime, never
                        referenced at compile time
```

The device layer is a **runtime plugin contract**, not a compile-time reference.
`FreePolarAlign.Devices.Ascom` targets `net8.0-windows`, implements `IDeviceProvider`,
and is copied into `plugins/` by the Windows build only. The application assembly
has no knowledge of it and no `#if WINDOWS` anywhere. On macOS you get the
simulator provider; on Windows, simulator plus ASCOM.

This is what makes the macOS build work, and it is also what will make ASCOM
Alpaca, INDI and the native vendor SDKs drop in later without touching the app.

### Project layout

| Project | TFM | Purpose |
|---|---|---|
| `FreePolarAlign.Core` | `net8.0` | Coordinate transforms, circle fit, error model, correction vectors. Pure functions, no I/O. |
| `FreePolarAlign.Imaging` | `net8.0` | FITS read/write, TAN WCS parsing, star detection. |
| `FreePolarAlign.Solving` | `net8.0` | `ISolver`, Watney adapter, ASTAP subprocess adapter. |
| `FreePolarAlign.Devices` | `net8.0` | `ICamera`, `IMount`, `IDeviceProvider` contracts only. |
| `FreePolarAlign.Devices.Simulated` | `net8.0` | Virtual observatory. See below. |
| `FreePolarAlign.Devices.Ascom` | `net8.0-windows` | ASCOM COM provider. Windows build only. |
| `FreePolarAlign.Session` | `net8.0` | Orchestration state machine. |
| `FreePolarAlign.App` | `net8.0` | Avalonia UI. |
| `FreePolarAlign.Tests.*` | `net8.0` | Unit and end-to-end tests. Must pass on macOS. |

### The virtual observatory

`FreePolarAlign.Devices.Simulated` is not a stub. It is a simulated mount plus a
camera that reads the mount's pointing, applies a **configurable injected polar
misalignment and cone error**, and renders a synthetic star field from a bundled
Tycho-2 subset into a real FITS frame with a known WCS.

This gives a closed loop with exact ground truth that runs on a laptop at noon.
Every accuracy claim in the roadmap is verified against it. Without this, the
project is only debuggable on clear nights, which is not a viable development
loop.

---

## Building

**macOS / Linux** — core development, all math and solver work:

```bash
dotnet build FreePolarAlign.Core.slnf     # solution filter excludes Windows projects
dotnet test  FreePolarAlign.Core.slnf
dotnet run --project src/FreePolarAlign.App
```

The app launches with the simulator provider only. This is enough to develop and
demonstrate the entire alignment loop.

**Windows** — device integration and release builds:

```powershell
dotnet build FreePolarAlign.sln
dotnet publish src/FreePolarAlign.App -r win-x64 -c Release --self-contained
```

Requires the ASCOM Platform installed for the ASCOM provider to load.

Cross-publishing a Windows binary from macOS works, but installer packaging and
code signing need a real Windows machine.

---

## Documentation

- [`docs/DECISIONS.md`](docs/DECISIONS.md) — architectural decisions and the reasoning behind them
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — development phases, exit criteria, work split
