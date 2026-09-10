# Architecture decisions

Each entry states the decision, why, and what it costs. Where a decision rests on
an assumption that has not been verified against a real product, that is marked
**VERIFY** — do not treat it as settled.

---

## What "10 arcminutes" means

**10 arcminutes is a success indication, not an error budget.** It is the point at
which the UI tells the user their alignment is good enough to stop turning bolts.
It is not a tolerance the software is allowed to accumulate error up to.

The measurement itself must be far better than that, because the actual figure is
always on screen and the user acts on it. The roadmap asks for 0.1′ recovery from
the fit (Phase 1), 1′ end to end through the solver (Phase 2), and better than 2′
on a real night (Phase 4).

So when a decision below weighs an error source, the number to beat is roughly
**0.1 arcminute — 6 arcseconds — on the reported altitude and azimuth error**, not
10 arcminutes.

One asymmetry is worth knowing, because it recurs: errors that amount to a
rotation *about* the celestial pole are largely self-cancelling here, while errors
that *move* the pole are not. A clock or UT1 error rotates the whole sky about the
polar axis, which leaves the pole itself fixed in the local horizon frame and
perturbs the measured misalignment only at second order — the product of the
misalignment ρ and the rotation δθ. Two seconds of clock error against a 2°
misalignment is about 1 arcsecond of error in the result. Polar motion, by
contrast, genuinely moves the pole relative to the ground, but only by a few
tenths of an arcsecond. Both are comfortably inside the 6 arcsecond budget; they
are not the same kind of safe, and only the first stays safe as the numbers grow.

---

## D1 — Platform: Windows ships, macOS builds

Windows x64 is the only supported release target for v1. The codebase must
nonetheless build and run its full test suite on macOS (arm64).

**Why.** Development happens on a Mac. A codebase that cannot be run on the
development machine cannot be developed on it.

**Consequences.** No compile-time dependency on any Windows assembly from any
project that isn't explicitly Windows-targeted. This is enforced by D4. It also
means the eventual Linux/macOS release is a packaging problem rather than a
rewrite, which was a stated stretch goal.

---

## D2 — Stack: .NET 10 + C# + Avalonia

**Why .NET 10 rather than .NET 8.** Originally .NET 8. Retargeted once the D3
verification showed Watney 2.0.x targets `net10.0` only, having dropped
netstandard2.0: staying on `net8.0` would have pinned the solver to an
unmaintained 2023 build (see O4). The build machine already had only the .NET 10
SDK, so `net8.0` was being reached through a `RollForward` workaround that is now
gone. Solve quality is load-bearing for D11's safety story, so tracking a
maintained solver matters more than the original TFM.

**Why Avalonia rather than WPF.** WPF does not run on macOS. Choosing it would
mean writing UI code that cannot be launched on the build machine. Avalonia is
close enough to WPF's model that the knowledge transfers, and it is used by real
astronomy software.

**Consequences.** A smaller ecosystem than WPF and occasional rough edges in
controls. Accepted. Avoid Windows-only UI conveniences (native dialogs, COM-backed
controls) in shared code.

---

## D3 — Solver: Watney embedded, ASTAP optional

`ISolver` abstracts the solver. Two implementations:

- **Watney** — **Apache-2.0** licensed (not MIT, as this entry previously said;
  Apache-2.0 is equally permissive and changes nothing about O1), written in C#,
  usable as a library rather than a subprocess. Embeds directly, ships in the
  installer, runs on macOS. This is the default and the reason "deliverable as a
  whole" is achievable.
- **ASTAP** — GPL, invoked as a subprocess so no linking obligation arises. Fast
  and excellent at unknown-scale solving. Offered as an accelerator for users who
  already have it; never required.

**Rejected: astrometry.net.** Robust and the reference implementation, but
awkward to package on Windows and its blind-solving index sets are large.

**Consequences.** All solver-specific behaviour stays behind the interface,
including scale hints, timeouts, and failure modes. The engine must never assume
which solver ran.

### Verified 2026-09-08 (the roadmap wanted this closed in Phase 0)

Checked against the real package and repository, and by compiling and running
against the assembly on the macOS arm64 build machine:

- **Embedding works.** `WatneyAstrometry.Core` is pure managed IL with **zero
  NuGet dependencies** and no P/Invoke. It builds, loads and runs natively as
  `osx-arm64`, and reports missing databases or files as clean typed exceptions
  rather than crashing — which is what `ISolver`'s failure-reason mapping needs.
  D3's central assumption holds.
- **The CD matrix is available directly.** `SolveResult.Solution.FitsHeaders`
  exposes `CD1_1`/`CD1_2`/`CD2_1`/`CD2_2`, so D12's determinant-based parity
  logic needs no derivation.
- **Two contract mismatches, both adapter-level, neither architectural.** Watney
  expresses a scale hint as min/max *field radius*, so the adapter must convert
  from `ApproximateScaleArcsecPerPixel` and the image dimensions; and it has no
  timeout parameter, only a `CancellationToken`, so `PlateSolveRequest.Timeout`
  becomes a `CancellationTokenSource` in the adapter.
- **Watney accepts more than `ISolver` currently offers**: a file path, an
  in-memory image, or a pre-detected star list. The star-list overload skips
  file I/O entirely and may be worth exposing on the contract in Phase 2.
- **Unresolved: the target framework.** Watney 2.0.x targets `net10.0` only and
  the maintainer has dropped the netstandard2.0 profile, so the last release
  usable from `net8.0` (D2) is 1.2.3 from 2023. See **O4**.
- **Not yet done:** no end-to-end solve has been run, because the smallest quad
  database is 369 MB. The library-embedding mechanism is verified; solve quality
  is not. Do that in Phase 2.

### Tuning Watney (Phase 2) — its defaults do not suit this application

Solve quality turned out to depend far more on how Watney is *configured* than on
anything about the images. With its defaults, rendered fields of 2.3° and 1.2°
diagonal would not solve at all; correctly configured, everything from 7.4° down
to 1.2° solves in under a second. Two settings account for that, and neither is
obvious from the API.

**Widen the density passes.** `MaxNegative/PositiveDensityOffset` default to
**zero** — a single density pass. A pack's passes are built at fixed star
densities (20, 28, 40, 57 … stars/deg²), and a frame whose detected density falls
between two of them matches neither. Setting ±2 is what fixed the 1.2° field, and
it is the single largest cause of otherwise inexplicable failures.

**A scale hint must pin the radius, not bracket it.** Watney searches a
*discrete* sequence of field radii, halving from a start value — its defaults run
22.5° down to 0.703°, and 0.703 is exactly 22.5/2⁵. A fractional scale tolerance
therefore cannot be expressed as a range: handing it a ±25% band collapses the
ladder to a single radius that may sit 25% off the truth, and measurably turned
*succeeding* solves into `NoMatchFound`. So a tolerance tight enough to be a
measurement pins the radius exactly; a looser one is treated as a claim and
ignored in favour of the blind ladder, whose floor also has to be lowered below
Watney's 0.703° or no frame under about 1.4° can match at all.

**Measured coverage with the bundled `00-07` pack**, fully blind, no hints:

| Frame diagonal | Field radius | Result |
|---|---|---|
| 7.4° | 3.73° | solves, 0.14 s |
| 2.3° | 1.15° | solves, 0.12 s |
| 1.6° | 0.76° | solves, 0.38 s |
| 1.2° | 0.57° | solves, 0.91 s |
| 0.64° | 0.32° | no match — needs the optional `12-13` pack |

The 0.64° result is the expected consequence of O5's bundling decision rather
than a defect, and it confirms that decision's stated cost: the narrow corner of
D13's envelope needs a download. Note also that this measured floor of ~1.2°
diagonal is *worse* than the wiki's "field radius ≥ 0.8°" claim for this pack
would imply, so the bundled packs cover less than D13 assumed.

---

## D4 — Device support is a runtime plugin contract

`FreePolarAlign.Devices` defines `ICamera`, `IMount`, `IDeviceProvider`. Providers are
discovered by scanning `plugins/` at startup. The application has no compile-time
reference to any provider.

**Why.** This is the single mechanism that satisfies D1, and it is also how ASCOM
Alpaca, INDI, and each native vendor SDK will be added later without touching
application code. Solving both problems with one abstraction is worth the small
amount of ceremony.

**Consequences.** Contracts must be defined early and changed rarely, because they
are the seam two developers work either side of. Version the contract assembly.
Plugin load failures must be surfaced clearly, not swallowed — a missing ASCOM
Platform should produce a readable message, not an empty device list.

---

## D5 — Astrometry via a pure C# IAU 2000B implementation

Coordinate transforms (precession, nutation, aberration, refraction) are
hand-written in managed C#, using the truncated IAU 2000B nutation series
(~77 terms, vs. ~1365 for full IAU 2000A).

**Why.** J2000 to apparent place is roughly 22 arcminutes of general precession
at the current epoch — over 200 times the 0.1 arcminute accuracy the reported
figure needs. Getting precession, nutation, aberration and refraction right is not
optional. IAU 2000B is accurate to well under an arcsecond, comfortably inside
that 6 arcsecond budget, and clears the Phase 0 exit criterion (<1 arcsec
agreement with Astropy) — measured at 0.14 arcsec worst case across 400
randomized epochs, sites and declinations.

**Why not ASCOM's Transform.** It would pull a Windows dependency into the math
core, violating D1.

**Why not liberfa (P/Invoke).** liberfa (BSD-3, the open release of IAU SOFA)
was the initial default and remains more accurate and less code to write. But
that headroom buys nothing against a 6 arcsecond accuracy target IAU 2000B
already beats by more than an order of magnitude, while it costs
prebuilt native binaries and per-RID packaging (`win-x64`, `osx-arm64`, and every
future RID in Phase 6) — a recurring cost for accuracy the project doesn't need.
Rejected on that basis; see O2.

**Consequences.** The team owns and must verify an astrometry algorithm
in-house rather than delegating to the reference implementation. Test hard
against the Phase 0 exit criterion (Astropy reference vectors) before trusting
it. No native packaging, no per-RID concerns — trivially portable to any future
OS in Phase 6.

---

## D6 — In-process UI, message-shaped engine boundary

Single desktop application. The UI does not call the engine directly; it sends
commands and consumes an event stream.

**Why.** A laptop at the mount is the v1 workflow, so a client/server split is
unjustified work now. But the "engine on a mini-PC at the pier, UI on a tablet"
workflow is genuinely valuable when you are crouched at the tripod turning bolts,
and designing the boundary as messages now makes that a transport change later
instead of a rewrite.

**Consequences.** Slight indirection cost. No shared mutable state across the
boundary. All engine state changes must be expressible as serialisable events.

---

## D7 — Three or more points, not two

The session captures a minimum of three positions.

**Why.** Two solved positions define an arc but not where its centre lies along the
perpendicular bisector. Closing that gap requires trusting the mount's reported
slew angle, which imports its gearing error and backlash into the result. Three
points make the measurement self-contained; a fourth provides a residual check.

**Consequences.** Slightly longer sequences. A two-point fast mode may be offered
later, clearly labelled as depending on mount encoder accuracy.

---

## D8 — Never cross the meridian during a sequence

Target selection constrains the whole sweep to one side of the meridian, and
`SideOfPier` is checked between captures.

**Why.** A meridian flip rotates the optical axis within the mount frame and
inverts the sense of cone error. The captured points then lie on two different
circles and the fit is meaningless — but it will still *return an answer*, which is
the dangerous part.

**Consequences.** Usable RA sweep is limited to roughly 60–75° rather than a full
half-revolution. This is not a real constraint: plate solving is precise enough
that 30° gives good conditioning, so the geometry is comfortable. If a requested
sweep would cross, the software reverses direction instead.

Some drivers report `SideOfPier` unreliably (see D9), so hour angle is also
computed independently as a cross-check.

---

## D9 — Mount control via `SlewToCoordinatesAsync`, not `MoveAxis`

RA steps are commanded as absolute slews to the *same declination* with an RA
offset, rather than timed axis motion.

**Why.** Absolute slews are closed-loop against the mount's own encoders. Timed
`MoveAxis` motion accumulates rate and latency error, and would need calibrating
per mount.

**Target mounts for initial testing:** iOptron (CEM/GEM/HAE series, iOptron ASCOM
driver) and Sky-Watcher (via EQMOD/EQASCOM or the SynScan ASCOM driver).

**Known variation to handle:** `SideOfPier` support differs between these drivers,
and the SynScan driver has historically been the weaker of the two. Treat
`SideOfPier` as advisory: check it when available, but rely on computed hour angle
and on fit residuals as the real safety net. **VERIFY** actual behaviour on both
mounts during Phase 3 and record findings in a compatibility table.

**Consequences.** Requires `CanSlewAsync`. Manual mounts bypass this entirely —
see D10.

---

## D10 — Manual mounts are a first-class mode, not a fallback

The algorithm does not care how the mount reached each position. Manual mode
prompts "rotate RA about 40° west, do not touch declination, press continue" and
proceeds identically.

**Why.** It costs almost nothing given the geometry, and it covers users whose
mounts have no ASCOM driver, or whose driver is misbehaving.

**Consequences.** Declination drift cannot be detected from mount telemetry in this
mode, so residual-based detection (D11) carries the full load.

---

## D11 — Detect declination drift from fit residuals

Every fit reports residuals and a covariance. Residuals inconsistent with the
expected solve noise flag a likely declination change or meridian flip, and the
result is withheld rather than displayed.

**Why.** This is the failure mode that produces a confident wrong answer, and the
user has no way to notice it. Silence is not acceptable here.

**Extended in Phase 1 — residuals are not enough on their own.** Implementing the
fit turned up a second, independent way to get a confident wrong answer, which
residuals do not catch. When the telescope points close to the mount's own polar
axis, the captured arc cannot be told from a straight line, and a very large
circle centred far away fits the points with *lower* residuals than the true
small circle. The likelihood becomes multimodal and the axis is simply not
identifiable. Measured, the fit's median error stayed normal while its tail
reached two degrees, with small residuals and a healthy condition number
throughout — every indicator the software had was reporting success.

Withholding therefore rests on three checks, not one:

1. **Residual consistency** (this decision) — catches declination drift and
   meridian flips, which inflate residuals.
2. **Conditioning** — catches a too-narrow RA sweep, which leaves the axis
   position and circle radius degenerate.
3. **Curvature identifiability** — catches an unresolvable circle, which neither
   of the other two sees.

The lesson generalises beyond this instance: a single health metric was
insufficient here precisely because the failure mode was invisible to it, and
the only reason it was found was testing against known ground truth rather than
for self-consistency. Any future estimator added to this project should be
assumed to have a failure mode its own residuals cannot see.

---

## D12 — Correction direction derived from the WCS matrix

On-screen arrow direction and parity come from the solved CD matrix, whose
determinant encodes handedness.

**Why.** This handles star diagonals, mirror flips and arbitrary camera rotation
automatically. The alternative is a "flip image" checkbox that users get wrong and
then report as a bug.

**Consequences.** Note that an azimuth adjustment rotates the mount about the local
vertical, so apparent field motion scales with cos(altitude). Arcseconds per knob
turn genuinely changes with where the telescope is pointed, and the UI should not
pretend otherwise.

---

## D13 — Index packs: tiered, core bundled

Assumed hardware envelope: 100–400 mm focal length, 2–5 µm pixels, sensor
diagonals roughly 4.5–13 mm. That gives:

| | value |
|---|---|
| Field of view (diagonal) | ~0.6° to ~7.6° |
| Pixel scale | ~1.0 to ~10.3 arcsec/px |

Guide scopes are small and exposures are short, so detected stars are typically
brighter than magnitude 11–12 regardless of what the index contains.

- **Core pack, bundled in the installer** — covers roughly 0.5°–8°, stars to about
  magnitude 12. Sized so that a 0.6° field still yields tens of matchable stars.
- **Optional downloadable packs** — deeper magnitudes for sub-0.5° fields, very
  short exposures, or heavy light pollution.

### Verified 2026-09-08 — this sizing does not hold

The **VERIFY** flag was right to be suspicious. Watney slices its databases by
**field radius and star density**, not by a single bundled magnitude ceiling, so
there is no pack shaped like "0.5°–8° to magnitude 12". Real v3 pack sizes:

| Pack | Field radius | Diagonal covered | Size |
|---|---|---|---|
| `00-07-20-v3` | ≥ 0.8° | ≥ ~1.6° | 369 MB |
| `08-09-20-v3` | 0.6–0.7° | ~1.2–1.4° | 390 MB |
| `10-11-20-v3` | 0.4–0.5° | ~0.8–1.0° | 780 MB |
| `12-13-20-v3` | 0.3° | ~0.6° | 1.56 GB |
| `14-20-v3` | 0.2° | ~0.4° | 1.29 GB |

Covering this decision's own 0.6°–7.6° diagonal envelope means bundling the first
four — about **3.1 GB**, an order of magnitude past "low hundreds of MB". The
cheap packs are the wide fields; it is the 0.6° low end that is expensive, because
narrow fields need far denser star data.

### Revised decision (resolves O5)

The bundled envelope moves, not the installer size.

- **Bundled in the installer** — `00-07-20-v3` + `08-09-20-v3`, about **759 MB**.
  Covers a field radius of 0.6° and wider, i.e. roughly **1.2° diagonal and up**,
  which is most guide-scope setups (a 200 mm focal length with a 4.2 mm or larger
  sensor diagonal, or any shorter focal length).
- **Optional download** — `10-11`, `12-13` and `14-20`, for narrower fields. The
  long-focal-length, small-sensor corner of the envelope (a 400 mm guide scope
  with a 4.5 mm sensor, giving 0.64°) therefore needs a download before first use.

This is over the original "low hundreds of MB" but an order of magnitude under
the ~3.1 GB that covering the whole stated envelope offline would cost. The
consequence to own: Phase 5's "installer, offline in full" now means *offline for
the common hardware*, and the index pack manager must say clearly which fields
the bundled packs cover, so a user with a narrow field learns it before a dark
site rather than during one.

A custom pack built with Watney's open-source `GaiaQuadDatabaseCreator`, tuned to
this specific envelope, could plausibly beat 759 MB. Not attempted; revisit only
if installer size becomes a real complaint.

### Where the packs come from

GitHub releases on `Jusas/WatneyAstrometry`, tag **`watneyqdb3`**. Plain HTTPS,
no authentication, stable URLs — so the Phase 5 pack manager needs no API client:

```
https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/watneyqdb-00-07-20-v3.zip
https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/watneyqdb-08-09-20-v3.zip
```

**Use the v3 generation.** Three exist, and the newest is also the smallest:
`0.9.0-qdb1` (2021, one 1.35 GB pack), `1.0.0-qdb1` (2022-01, five sets,
5.50 GB), `watneyqdb3` (2022-02, five sets, 4.39 GB). v3 is about 20% smaller
than v1 for identical coverage and needs Watney v1.1 or later, which O4's move to
2.0.x satisfies. Nothing newer has been published since February 2022, so this is
a stable target rather than a moving one.

**Naming.** `watneyqdb-<pass range>-<lowest star density>-v<format version>`. The
trailing `20` is not a variant to choose between: every published set starts from
the same 20 stars/deg² floor. "Passes" are density tiers — 20, 28, 40, 57, 80,
113, 160, 226, 320, 453, 640, 905, 1280, 1810, 2560 stars/deg² — and denser
passes serve smaller fields, which is why the narrow-field sets are the large
ones. Field radius is half the frame diagonal.

Internally each set is 406 files on an equal-area band-cell division of the sky,
roughly 10°×10° per cell, so a by-region subset is mechanically simple if a
smaller bundle is ever wanted.

**Unpacked size: measured, and it roughly doubles.** `00-07-20-v3` downloads as
369 MB and extracts to **768 MB** across 409 files — a factor of 2.08, not the
small margin "tightly packed binary" suggested. So O5's bundled pair is about
**759 MB to download and roughly 1.55 GB on disk**, and Phase 5 must quote the
larger figure as the free-space requirement. The installer size and the disk
requirement are genuinely different numbers.

**The `.qdbindex` sidecar is mandatory.** Each pack carries one index file
(`gaia2-00-07-20.qdbindex`, 1.2 MB) alongside its 408 `.qdb` cell files, and
Watney throws `QuadDatabaseVersionException` without it. It is easy to lose by
extracting or copying only `*.qdb`, and the resulting error names a *version*
problem rather than a missing file, which sends you looking in the wrong place.
The pack manager must treat it as part of the pack.

---

## D14 — Site position accuracy is a hard requirement

Latitude error maps 1:1 into the reported polar altitude error, so latitude must
be good to about 0.1 arcminute — roughly 200 m — to match the accuracy of the
number the user is shown and acts on. This is not about clearing the 10 arcminute
success indication; it is about the figure itself being right.

The system clock is far more forgiving: within a second or two is ample, because a
clock error rotates the sky about the polar axis rather than moving the pole, and
so cancels to second order (see "What 10 arcminutes means" above). Latitude and
clock are not symmetric requirements, and it is latitude that carries the risk.

**Sources**, in order of preference: mount driver site properties, serial NMEA GPS,
manual entry. Manual entry must show the derived uncertainty so a user typing a
rounded city coordinate sees the consequence.

---

## D15 — Fit in the horizon frame, on physical pointing directions

The circle is fitted to the telescope's *physical pointing directions* in the
local horizon frame, obtained by putting each plate solve through the full
apparent-place transform (D5) with refraction included. The fitted axis is then
compared directly against the true celestial pole, whose altitude is the site
latitude and whose azimuth is due north.

**Why.** A plate solve reports the catalogue position of whichever star appears
at the field centre. Refraction changes *which* star that is, not where the tube
is pointing — so applying the full transform, refraction included, recovers the
mechanical pointing direction. Since the mount is a rigid body in the ground
frame, those directions lie on an **exact** circle about its polar axis, whatever
the refraction, whatever the cone error.

This supersedes the README's original phrasing about comparing the fitted pole
"to the true refracted pole". That approach — fitting catalogue coordinates and
comparing against where the pole appears to be — is the more common one in
existing tools, but it is an approximation: refraction is a compression toward
the zenith, not a rotation, so it does not map a circle to a circle and the
fitted axis is biased. Doing it in the horizon frame costs nothing extra, since
D5's transform already exists, and removes the approximation entirely.

**Consequences.** The fit depends on site latitude and on the atmospheric model,
so D14's site-accuracy requirement is load-bearing for the *result*, not merely
for pointing. Latitude error maps 1:1 into reported altitude error.

It also means a vacuum is the wrong default anywhere describing a real
observation. Refraction is tens of arcseconds at usable altitudes and *varies*
across a sequence — roughly 25 arcseconds at 66° altitude rising past 50 at 49° —
so assuming it away puts an altitude-dependent distortion into every observation
rather than a harmless constant offset. Measured, that alone moved a recovered
polar axis by 26 arcminutes. Both the session and the simulated camera default to
a standard atmosphere for this reason, and callers with real weather data should
supply it.

---

## D16 — Sequences are commanded in mechanical coordinates, resolved just in time

A capture sequence is planned as mount *mechanical* angles — one fixed
declination and a set of hour angles — and each capture's sky coordinates are
computed from those angles immediately before the slew, not up front.

**Why.** D11 requires the optical axis to stay fixed in the rotating mount
frame, which means the *mechanical* declination must not change between
captures. That is not the same as a fixed catalogue declination, and the
difference is large enough to matter: a constant mechanical declination is a
constant declination about the pole **of date**, whose J2000 equivalent drifts
with right ascension because the two poles differ by the accumulated precession.
Measured, commanding a fixed J2000 declination across a 70° sweep made the
mechanical declination wander **5.75 arcminutes** — the mount faithfully moving
in declination to obey coordinates that meant something slightly different at
each hour angle. Resolving each capture at the moment of its slew makes the round
trip exact instead, because the mount decomposes exactly what it was handed.

**Consequences.** Two, both sharp.

First, the resolution must be the exact inverse of whatever the mount does to the
coordinates — geometric, with no refraction, because a mount's pointing model is
geometric. Using the most physically complete transform available here would be
*wrong*, which is a rare enough situation to be worth stating plainly.

Second, it makes the coordinate epoch a correctness issue rather than a
convention. `IMount.SlewToCoordinatesAsync` is documented as J2000, but ASCOM
drivers interpret coordinates according to their own `EquatorialSystem`, which is
frequently JNow on EQMOD and SynScan setups. A driver silently treating our J2000
coordinates as JNow would shift the commanded position by the full precession
offset — around 22 arcminutes at the current epoch — and, worse, by an amount
that varies across the sweep, reintroducing exactly the declination drift this
decision exists to prevent. The ASCOM provider therefore refuses to run against a
driver whose `EquatorialSystem` is not J2000, rather than guessing. A future
integration needs either J2000-configured drivers or an explicit conversion
layer; see `docs/MOUNT-COMPATIBILITY.md`.

**How this was found.** Not by inspection. The residual check from D11 rejected
the fit — residuals ten times the expected solve noise — and the reported reason
was, correctly, that declination had probably moved between captures. The safety
net named its own cause.

---

## D17 — Freeze-and-track refines a measurement; it cannot police one

Live tracking updates the axis estimate from a single field while the user turns
the bolts, by inferring the rigid rotation the bolts applied. Two limits are
inherent to that and are treated as part of the design rather than as defects.

**It is blind to anything that is not a bolt turn.** The altitude and azimuth
bolts give two degrees of freedom, and a single field's motion has exactly two
observable components, so *any* observed motion is explained exactly by some pair
of bolt angles. A declination clutch slipping, or a nudged tripod, produces a
clean fit with a near-zero residual and a wrong answer. Even a gross displacement
fits, because the two bolt angles reach a two-parameter family of directions
covering most of the sphere.

That is the exact opposite of the swept measurement, where six points on a circle
are heavily over-determined and D11's residual check catches this readily. So a
residual test here would pass always while *looking* like a safety net, and
deliberately does not exist — an always-passing guard is worse than none, because
it invites the confidence it cannot justify.

The consequences are that live tracking must be re-anchored by a fresh sweep
rather than trusted indefinitely, and that the two inferred bolt figures are
worth surfacing in their own right: a user who touched only the altitude bolt and
is told the azimuth bolt moved has learned that something else moved.

**Its blind spot is due east and due west, not the zenith.** The altitude bolt
turns the mount about a horizontal axis running east–west, perpendicular to the
polar axis' vertical plane. A telescope pointing due east or west therefore lies
in that axis' own vertical plane, where both bolts move the field along the same
line and cannot be told apart. Measured conditioning:

| Pointing | Altitude 20° | Altitude 80° |
|---|---|---|
| On the meridian | 1.1 | 33 |
| 45° off it | 2.3 | 67 |
| **Due east or west** | **21 000** | **627 000** |

Pointing near the zenith degrades it too, but by a factor of thirty rather than
twenty thousand, and the advice differs. So the tracking target should sit near
the meridian, and `AxisTracker.IsUsableTrackingGeometry` exists so a caller can
check *before* the user starts turning bolts rather than discovering it after.

This was found by accident: the first version of the tests happened to pick a
geometry a degree from due west, and every one of them failed.

---

## Open decisions

**O1 — Licence.** ~~MIT is the natural fit and imposes nothing on Watney. GPL
only becomes relevant if ASTAP is ever linked rather than subprocessed, which
D3 avoids. Needs a call before the repo goes public.~~
**Resolved:** MIT — already in place in the repo's `LICENSE` file.

**O2 — liberfa native vs. pure C#.** ~~See D5. Native is more accurate and less
code; pure C# removes per-RID native packaging entirely. Both are defensible.
Decide in Phase 0, because it affects the build and the collaborator's setup.~~
**Resolved:** pure C# IAU 2000B. See D5.

**O3 — Project name.** ~~Affects namespaces, so worth settling before the scaffold.~~
**Resolved:** `free-polar-align` (repo, product name). C# namespace/project prefix
is `FreePolarAlign`, since hyphens are not valid in .NET identifiers.

**O4 — .NET 8 or .NET 10.** ~~Surfaced by the D3 verification. Watney 2.0.x
targets `net10.0` only and has dropped netstandard2.0, so D2's `net8.0` can only
consume Watney 1.2.3 (2023), which is unmaintained and misses a documented ~61%
blind-solve speedup.~~
**Resolved:** retarget the stack to `net10.0`. See D2.

**O5 — Bundled index pack scope vs. installer size.** ~~Surfaced by the D13
verification. Covering the stated 0.6°–7.6° diagonal envelope offline costs about
3.1 GB, against a "low hundreds of MB" target and Phase 5's "installer, offline
in full".~~
**Resolved:** bundle `00-07` + `08-09`, about 759 MB. See D13.
