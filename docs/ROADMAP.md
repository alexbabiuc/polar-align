# Roadmap

Each phase has an exit criterion that is objectively checkable. Phases 0–2 are
entirely testable on macOS with no hardware and no night sky.

CI is deliberately deferred to Phase 5. Until then, tests run locally.

---

## Working in parallel

The natural seam for two developers is hardware. It falls almost exactly along the
plugin boundary from D4.

**Track A — no hardware required, macOS**
Core math, imaging, solver integration, virtual observatory. This is the part that
determines whether the software is *correct*.

**Track B — Windows and telescopes**
Device providers, ASCOM, session orchestration, UI, packaging. This is the part
that determines whether the software is *usable*.

The tracks meet at three interfaces — `ISolver`, `IDeviceProvider`,
`IAlignmentEngine` — which are defined and frozen in Phase 0. **Define these
before splitting the work.** They are cheap to design together for an afternoon
and expensive to renegotiate later.

Phase 0 is joint. Phases 1–2 are Track A while Track B does Phase 3 device work
against the simulator. Phases 4 onward converge.

---

## Phase 0 — Skeleton and contracts *(joint)*

- Solution layout per README, `.slnf` filter excluding Windows projects
- The three interfaces, agreed and frozen
- FITS reader/writer and TAN WCS parser (write these; the needed subset is small
  and a dependency would obscure the CD matrix you need for D12)
- ERFA binding, or the pure C# alternative — resolves open decision O2
- Licence and project name — resolves O1 and O3

**Exit criterion.** A J2000 → topocentric alt/az transform agrees with Astropy to
better than one arcsecond across a spread of epochs, sites and declinations,
verified on macOS. Commit the Astropy-generated reference vectors so the check is
reproducible without Python.

---

## Phase 1 — Math core *(Track A)*

- Forward model: mount frame, injected misalignment, cone error, refraction
- Small-circle fit on the sphere with covariance
- Misalignment expressed as altitude and azimuth error, with correction vectors
- Conditioning analysis as a function of RA sweep and target declination

**Exit criterion.** A parameter sweep over injected misalignments (0.1′ to 5°),
latitudes including both hemispheres and the equator, and target declinations,
recovers the injected value to better than 0.1′ under realistic solve noise.
Conditioning degrades smoothly and *predictably* as sweep narrows or the target
approaches the pole, and the reported covariance reflects it.

That last clause matters more than the accuracy number. The software must know
when it does not know.

### Measured results, and what they constrain

**Met.** Recovery is better than 0.1′ across the full stated parameter space
(misalignments 0.1′–5°, latitudes ±70° and the equator, declinations 0°–85°), and
the reported covariance matches the observed scatter to within 3% — checked per
component and on the total, over thousands of noise realisations.

**Sweep width matters far more than capture count.** Axis uncertainty falls as
the square root of the capture count but as the **square** of the RA sweep, and
the condition number rises as the **fourth power** of a narrowing sweep. Both
laws are asymptotically exact, not approximate. The practical consequence is a
constraint on Phase 3's session design: at 1″ solve accuracy, 20 captures over
60° *misses* 0.1′ (0.111′), while the same 20 captures over 70° meets it with
margin (0.080′). Thirty captures over 60° also passes, at half again the
exposure time. **Prefer sweep width to extra captures**, up to D8's meridian
limit. At 2″ solve accuracy, 0.1′ is not reachable within that limit at all —
so plate solve accuracy is worth optimising in Phase 2, not just solve success.

**Two corrections to this phase's assumptions.** Conditioning does *not* degrade
as the target approaches the pole: the circle's radius does not enter the fit's
conditioning at all, only the spread of rotation angles does, and recovery is
flat from the celestial equator to within 6′ of the pole. What happens instead is
an identifiability cliff — see D11, where this is recorded, since it is a
withholding concern rather than a conditioning one. And a small-circle fit needs
step control: an undamped Gauss-Newton step on poorly resolved geometry runs away
by degrees while leaving small residuals.

---

## Phase 2 — Solving and the virtual observatory *(Track A)*

- `ISolver`, Watney adapter, ASTAP subprocess adapter
- Star detection and centroiding
- Synthetic sky renderer: Tycho-2 subset, configurable seeing, noise, star trails,
  cloud dropouts, focal length and pixel scale
- Focal length recovery: `FL_mm = 206.265 × pixel_µm / scale_arcsec_per_px`
- Blind solve with fallback to a user-supplied approximate focal length, persisted
  per equipment profile after first success

**Exit criterion.** Three rendered fields with known WCS solve blind across the
full D13 envelope (0.6°–7.6°), recover focal length to within 0.5%, and feed
Phase 1 to recover the injected misalignment to within 1′. Degraded inputs —
clouds, trailing, poor focus, a field with few stars — fail cleanly with a useful
message rather than returning a wrong solve.

---

## Phase 3 — Devices and session *(Track B)*

- Plugin loader and simulator provider
- ASCOM provider: camera and telescope, on Windows
- Session state machine: target selection, slew, expose, solve, fit, iterate, with
  cancellation and recovery at every step
- Target selection honouring D8 — one side of the meridian, altitude above ~30°,
  declination away from the pole
- Manual mount mode (D10)
- Meridian and declination safety checks (D8, D11)
- Compatibility notes for iOptron and Sky-Watcher, especially `SideOfPier`
  behaviour (D9)

**Exit criterion.** The virtual observatory converges from an injected 2° error to
below 10′, then below 2′, without operator intervention. Then the same sequence
runs end to end on a real iOptron and a real Sky-Watcher mount. Pulling the USB
cable mid-sequence produces a recoverable state, not a crash.

---

## Phase 4 — UI and the live adjustment loop *(joint)*

- Correction reticle with parity from the CD matrix (D12)
- Numeric altitude and azimuth deltas in arcminutes, with confidence
- Hemisphere handling
- **Freeze-and-track mode:** once the axis is known, stop slewing and keep solving
  the same field. Any pointing change is a rigid rotation of the mount, so apply it
  to the axis estimate and update the error reading continuously while the user
  turns the bolts. This is the feature people will judge the software by.
- Success indication below 10′, with the actual figure always visible

**Exit criterion.** A real night, on your own equipment, aligning to better than
2′ without consulting the source code to interpret what the screen is saying.

---

## Phase 5 — Shipping

- Index pack manager: bundled core pack, optional downloads (D13)
- Installer, offline in full
- CI: Windows and macOS build and test jobs, release artifact publishing
- Logging, and a one-click diagnostic bundle containing failed frames, solve logs,
  mount telemetry and the fit state

Build the diagnostic bundle properly. Debugging someone else's telescope, at
night, remotely, without it is close to impossible.

**Exit criterion.** A clean Windows machine with no ASCOM Platform, no internet
and no prior setup installs the package and reaches a working simulated alignment.

---

## Phase 6 — Breadth

Native camera SDKs (ZWO, the ToupTek family which also covers Altair, Omegon,
RisingCam and Bresser rebrands, QHY, Atik), ASCOM Alpaca, INDI. Then Linux and
macOS packaging, which by this point is packaging work rather than porting work.

Each lands as a plugin. None require touching the application.

---

## Risks

**Silent wrong answers.** The highest-severity risk in the project. A fit that
returns a confident number after an undetected declination nudge is worse than a
crash, because the user acts on it. Mitigated by D11, and by treating residual
checks as a correctness feature rather than a nicety.

**Vendor SDK redistribution terms.** Check each licence before Phase 6, not during.

**Watney's library API.** D3 assumes it is usable as an embedded library with
manageable database tiers. If that assumption fails, the fallback is subprocessing
ASTAP with a bundled star database, which is workable but complicates the "whole
package" goal. Verify in Phase 0, not Phase 2.

**Driver inconsistency.** iOptron and Sky-Watcher will not behave identically.
Budget real time for this in Phase 3 and write down what you find.

**Weather.** Any plan that requires clear nights to make progress will slip
unpredictably. This is precisely what the virtual observatory exists to prevent,
which is why it is Phase 2 and not Phase 5.
