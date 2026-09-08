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

## D2 — Stack: .NET 8 + C# + Avalonia

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

So the installer-size target and the bundled envelope are now in direct conflict
and one of them has to move. See **O5**.

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

**O4 — .NET 8 or .NET 10.** Surfaced by the D3 verification. Watney 2.0.x targets
`net10.0` only and has dropped netstandard2.0, so D2's `net8.0` can only consume
Watney 1.2.3 (2023), which is unmaintained and misses a documented ~61%
blind-solve speedup. Either retarget the stack to `net10.0` — amending D2, and
noting the build machine already has only the .NET 10 SDK, so `net8.0` is
currently reached via a `RollForward` workaround — or pin the stale 1.2.3 and
accept no upstream fixes. Cheap to change now, while the tree is scaffolding;
expensive once Phase 2 adapters and Phase 3 device code exist. Decide before
Phase 2.

**O5 — Bundled index pack scope vs. installer size.** Surfaced by the D13
verification. Covering the stated 0.6°–7.6° diagonal envelope offline costs about
3.1 GB, against a "low hundreds of MB" target and Phase 5's "installer, offline
in full". Options: bundle ~759 MB (`00-07` + `08-09`) and support ~1.2° diagonal
and wider out of the box, pushing narrower fields to optional download; bundle the
full ~3.1 GB; or build a custom pack with Watney's open-source
`GaiaQuadDatabaseCreator` tuned to the guide-scope envelope — smaller in
principle, unverified, and a side project. This decides which hardware works
without a download, so it is a product call, not a packaging detail.
