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

### Measured results

**Met, with one scope correction.** Frames are rendered from a committed Tycho-2
subset, written as real FITS with the WCS stripped out, and solved by the
embedded Watney the product will ship — so nothing in the loop is mocked. Blind
solves succeed from 7.4° down to 1.2° diagonal, and focal length comes back well
inside 0.5%.

The correction is the narrow end: 0.6° needs an index pack O5 deliberately does
not bundle, and Tycho-2 is too shallow there anyway (about a dozen stars). So
"the full D13 envelope" is not demonstrable with what ships, by choice rather
than by defect. See D3's tuning section for the measured coverage table.

**The closed loop, end to end.** Injected 14.0′ altitude and −11.0′ azimuth
errors came back as 14.005′ and −10.997′ — an axis error of **0.006′**, some 175
times inside the 1′ criterion — through forward model, inverse astrometry,
render, detect, blind solve, forward astrometry and small-circle fit, with 25′ of
cone error injected and captures two minutes apart so the sky rotated between
them.

**A number Phase 1 needs.** Solved field centres landed 0.23″–1.42″ from truth,
so the fit's per-observation noise is under an arcsecond rather than the 1–2″
Phase 1 assumed. That makes Phase 1's 0.1′ target easier than its own measurements
suggested. Treat it as an optimistic bound, though: these frames have exact
catalogue positions and a clean Gaussian PSF, with none of the differential
refraction, optical distortion or seeing-driven centroid wander a real night
adds. The honest conclusion is that solve accuracy is not the binding constraint
on the observing plan — sweep width is.

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

### Measured results

**Simulated half: met.** From an injected 2° error (119.25′), one measurement
round left **0.003′** — the loop reads what the software reports, turns the
simulated bolts by exactly that, and re-measures, so a wrong sign or scale would
diverge rather than converge. Six captures solved blind in well under a second
each, residual RMS 0.33″, and the equipment profile learned the focal length as
100.02 mm against a true 100.00.

One round rather than the successive refinement the criterion implies, because
the simulator has no unmodelled mount error: no periodic error, backlash or
flexure. Expect real hardware to need several rounds, and treat the single-round
result as evidence the loop is *correct*, not as a prediction of how a night goes.

Manual mode (D10) measures identically, and a device disconnected mid-sequence
produces a reported fault and a session that starts again cleanly.

**Hardware half: not done, and not doable here.** No mount, no ASCOM Platform,
and macOS. The ASCOM provider is written and compiles, but has never executed —
see its doc comments and `docs/DEVICE-COMPATIBILITY.md`, which researches the
iOptron and Sky-Watcher `SideOfPier` behaviour D9 marked VERIFY, tagging every
claim by whether it is documented, user-reported or inferred. Two findings there
matter more than the table: the `SideOfPier` unreliability D9 attributes mainly
to SynScan appears in iOptron drivers too, and both families can flip
autonomously mid-sequence from firmware settings — which no pre-slew check can
catch, so D8's per-capture checking is load-bearing rather than belt-and-braces.

**Three bugs worth recording**, all found by the exit criterion rather than by
inspection, and all invisible in a single capture:

1. Cone error was applied in a basis derived from the current pointing rather
   than the mount frame, so the offset direction drifted as the mount turned and
   the track stopped being a circle. 20′ of cone error varied the radius by 10′
   across a sweep. The test that catches it checks the radius at *several*
   rotations; the earlier one checked the offset magnitude at one.
2. A vacuum was being assumed where a real atmosphere belongs, putting an
   altitude-dependent refraction error into every observation — 26′ of axis
   error. See D15.
3. Commanding a constant J2000 declination made the mount drift 5.75′ in
   *mechanical* declination across a 70° sweep. See D16.

D11's residual check caught all three: it rejected the fits and named
declination movement as the likely cause, which is exactly what two of them
were. The safety net earned its place before any hardware existed.

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

### Measured results

**Freeze-and-track works, and is the part worth the most scrutiny.** A bolt turn
is recovered from a *single* field across turns from 1′ to a full degree, giving
back both the turn itself and the resulting axis to within 0.001′, with 2° of
cone error present and in both hemispheres. Sidereal tracking is not mistaken for
a bolt turn, because the drive's rotation is supplied and removed exactly — the
failure that would otherwise make the readout drift while the user did nothing.
Six successive guided turns converge monotonically.

**Two inherent limits, both now recorded as D17.** It cannot detect a
disturbance that is not a bolt turn — a declination slip fits exactly, with a
near-zero residual — so it refines a trusted measurement rather than policing
one. And its blind spot is due east and due west, not the zenith, because the
altitude bolt's axis runs east–west; conditioning there is twenty thousand
against one to sixty near the meridian. Both were found by testing rather than
by inspection, the second because the first draft of the tests sat a degree from
due west and failed wholesale.

**The UI exists but is only partly verified.** The presentation logic is a pure
reducer over the event stream plus pure formatting, and that *is* tested — in
particular that success is judged on the total error rather than either bolt
figure or their sum, and that a withheld result clears the previous estimate
instead of leaving a stale number on screen. The window itself is not tested and
cannot be here; it compiles, and the app launches and stays running with the
virtual observatory wired up, which demonstrates the dependency graph constructs
and nothing more. The correction reticle (D12) is still a placeholder.

**The exit criterion is not met and cannot be here.** It asks for a real night on
real equipment, which needs both hardware and a sky. What has been established is
that the numbers behind the screen are right and that the screen does not
misrepresent them; whether they can be *acted on* without reading the source is
exactly what a real night would test.

### Phase 4b — Operator control of the loop

Added after the first look at the running window, which made clear how much the
UI assumed and how little it asked. The session had connected its own devices,
inherited a site, and slewed the moment it was started.

- **Device choice, connect and disconnect** for camera and mount, over the same
  `DeviceCatalog` that enumerates the simulator and any installed plugins (D4).
  Discovery failures are listed rather than swallowed, and a missing plugins
  directory is not presented as one.
- **Focal length is entered, then measured.** The typed value is shown as
  entered; the first solve replaces it and says so, because "1000 mm (as
  entered)" and "1000 mm (measured)" mean different things when a solve keeps
  failing.
- **Displayed:** camera and mount names with their drivers, pixel pitch and
  sensor size, focal length with its provenance, plate scale and field radius,
  the mount's reported RA and Dec, tracking state, pier side, and the confirmed
  site to five decimal places.
- **Nothing moves without confirmation (D18).** The first capture is taken where
  the telescope already points; every later point is proposed with editable
  coordinates and waits.
- **The site is reloaded, confirmed, then stored (D19),** and a mount reporting a
  different site is reported rather than obeyed.
- **A full session log** is written to `~/.free-polar-align/logs/`, narrated by
  the same function that writes the on-screen log so the two cannot drift apart,
  flushed per line because the usual way a session ends is not a clean shutdown.

**Tracking has three states, not two.** A driver that does not report it reads as
unknown, never as stopped: someone who believes the drive is stopped when it is
running will misread every number that follows, and the two are
indistinguishable in a boolean.

**A real southern-hemisphere defect surfaced from the new planner tests.**
Mechanical declination is measured towards the pole the mount's axis actually
points at, so it is positive towards the *visible* pole in both hemispheres and
is not sky declination south of the equator. `TargetSelection.Plan` negated it
below the equator, as though it were sky declination. At latitude −33.9° it then
chose a target **105° from the polar axis, six degrees above the horizon**, and
silently settled for a 30° sweep instead of the 70° requested — which, since
uncertainty scales as the inverse square of the sweep, is roughly five times
less certain than the software implied. It reported success throughout. That
silence is what made it worth finding: there is now a regression test asserting
the chosen target lands in the 20°–75° pole-distance band at four latitudes in
both hemispheres.

**The planner also stopped pretending the sweep is guaranteed.** It shrinks
rather than plan a capture below the altitude floor or across the meridian, and
reports what it achieved so the UI can say how much certainty that costs. Near
the equator a shrunk sweep is honest geometry rather than a defect — measured,
latitude 0.5° yields 50° — and is reported as such.

**993 tests pass.** The new ones worth naming: the coordinate parser (a typed
right ascension read in the wrong unit, or a negative declination inside the
first degree losing its sign, would point a telescope somewhere nobody asked
for), and the confirm-before-slew flow, which runs against the real mount
mechanics with a noiseless solver so that it needs no quad database and any
discrepancy in a commanded coordinate is unambiguous.

**A bug that every other test was blind to.** Running the window found the
camera would not stay connected, which also disabled the focal length and with
it the whole loop. The cause was in the view model's thread marshalling: a
single command routinely publishes several events — connecting a camera reports
the device and then its plate scale — and they arrive synchronously while the UI
queue still holds the earlier ones. The reduction happened *before* the post, so
every event of a command was folded against the same stale state and the last
post won: the plate scale arrived and the connection was overwritten away.

Nothing detected it because a test dispatcher that runs inline makes the two
orderings identical, which is exactly what all the existing presentation tests
used. The fix is to post the event and reduce inside the posted action, so each
action folds from whatever the previous one left; it also confines the state to
one thread, where reducing on the publishing thread was a plain data race.
There is now a `DeferredDispatcherTests` suite whose dispatcher genuinely
queues — five of its tests fail against the old ordering, verified by reverting
the fix.

Two smaller defects from the same session: the status poll queued behind a
capture, so a two-minute solve would leave sixty polls to discharge at once (it
now skips rather than queues), and the mount position read during every capture
was being discarded instead of reported, leaving the coordinate readout showing
the pre-slew position until the next timer tick.

**The loop runs end to end on the simulator**, driven through the view model
with a deferring dispatcher: six points, focal length measured from the first
solve at 100.1 mm against a true 100.0, and the injected misalignment recovered
as +27.0′ ± 0.2′ altitude and −19.0′ ± 0.3′ azimuth against an injected 27.0 and
−19.0. The commanded declination visibly moves 9′ across the sweep while the
mechanical declination is held constant, which is D16 doing its job.

### Phase 4c — Seeing the frame, and choosing the readout

- **The captured frame is displayed**, with an automatic stretch and a brightness
  slider. The stretch is not a nicety: a faithful rendering of a real exposure
  averages under 6/255 — measured — because the sky sits a few hundred ADU above
  bias while the stars reach forty thousand. The transform is the standard
  midtone transfer function placed from the median and the median absolute
  deviation, both robust against the stars, which here are the outliers rather
  than the signal. The midtone is solved in closed form rather than searched.
- **The frame is published before the solve is attempted**, so a frame the
  solver could not make sense of is on screen while the user reads why. "No
  stars detected" is answered by looking at the picture; a lens cap, cloud, wild
  defocus and a tracking runaway are all obvious there and none of them are
  distinguishable from the message.
- **Decimation takes the brightest pixel per block, not the average.** A star two
  pixels across, decimated by four, survives max-pooling, is diluted eightfold
  by averaging, and is missed entirely by subsampling. Since the commonest
  reason to look at the preview is "are there stars in this at all", losing them
  to the resize would defeat it.
- **Readout mode selection**, where the driver offers a choice. The simulated
  camera offers a genuine one: eight-bit really is written as BITPIX 8 with the
  frame quantised to 256 levels, so the cost of choosing it shows up in the data
  rather than only in the menu. Verified end to end — both depths solve, and
  both recover the injected misalignment (16-bit: 26.85′/−18.79′; 8-bit:
  26.96′/−18.94′; injected 27.0/−19.0).
- Bit depth is reported only when the driver actually reveals it. ASCOM exposes
  readout modes as opaque names, so the depth is derived from `MaxADU` where
  that is an exact power of two and left **unknown** otherwise. Matching on the
  mode's name was the obvious alternative and is rejected: a substring match on
  "8" reads "8-bit" correctly and "ADC 8x binned" wrongly, with no way to tell
  which happened, and a wrong depth is worse than an unknown one because the
  depth is what decides how far a centroid can be trusted.

**A bug the eight-bit mode exposed immediately.** The stretch normalised from
the darkest pixel present. At eight bits the noise quantises away entirely —
measured, a background of 600 ADU and 9 ADU of noise become level 2 with a
median absolute deviation of exactly **zero** — so the background *is* the
darkest value in the frame, which normalisation put at zero, and the transfer
function fixes zero at zero. The frame rendered black however far the slider was
dragged. It now normalises from the origin rather than the observed minimum,
which keeps the background strictly positive whenever it is.

### Known fragility: the end-to-end tests depend on the wall clock

Found while chasing the above. `DeviceLostMidSequence_LeavesARecoverableSession`
began failing on a *solve*, and failed identically at the previous commit — so
it is not a regression but a standing flake.

The mechanism: a sequence's target is derived from the current sidereal time, so
which patch of sky it walks across depends on the hour the suite is run. The
committed sample catalogue is a magnitude-limited subset, so the rendered frame
is an incomplete version of what the real quad database expects, and matching
succeeds in some parts of that band and fails in others. The frames were fine —
246 stars detected — and Watney simply found no matching quad.

The recovery test has been narrowed to assert what it is actually about: that a
reconnected session starts and captures again rather than finding the engine
wedged. Requiring a full six-point solution imported the solver's reliability
into a test named after a pulled cable. The convergence tests remain exposed,
and the proper fix is a clock the session can be given, which would also make
D16's just-in-time resolve testable at chosen instants. Not done yet.

**Still outstanding:** the correction reticle (D12) remains a placeholder, and
the window's own rendering is still only verified by having been looked at.

### Phase 4d — Live capture, and a mount that is optional

*Planned and implemented 2026-09-24, in 0.0.8; the simulated half of the exit
criterion is met.* A capture per click made every exposure or
gain adjustment a round trip, and "manual mode" still demanded a connected
mount. See D10, D18, D22, D23 and D25 as revised, and D26 and D27.

- **The camera runs continuously while connected**, with exposure, gain and
  readout mode changeable at any time, taking effect from the next frame.
  Nothing is solved until **Start sequence**; **Stop sequence** returns to the
  plain live view.
- **Samples are triggered by events** (D26). Connected: a slew ending, whether
  the engine's or the hand controller's, and 2 s of settling. Unconnected: a
  blind solve 5 s after the last result, accepted when two consecutive solves
  agree. At most one solve at a time. **Record sample** forces one.
- **A sample must add sweep** (D27): at least the planned spacing from every
  existing one, and no plan under 30°.
- **No mount is an ordinary mode** (D10). The user is told the declination and
  starting hour angle, then to leave declination alone.
- **Proposals start east of the meridian and sweep west** (D18). A telescope
  west of the meridian is proposed a move east first.
- **Declination movement on a connected mount restarts the sequence** and says
  why (D18).
- **Frames are deleted once used**, except the last 20 failed solves; the frame
  on screen can be saved (D26).

**Exit criterion.** On the virtual observatory, with no intervention beyond what
a user at the mount would do:

1. Connected: a sequence driven entirely from outside the engine (slews made
   directly on the simulated mount, as a hand controller would) samples itself
   and recovers the injected misalignment.
2. Unconnected: the same, with the engine never told the mount exists.
3. A declination nudge on the connected mount restarts the sequence rather than
   producing an answer.
4. An exposure and a gain change mid-sequence appear in the next frame's header
   and do not disturb the result.

In both 1 and 2 the injected misalignment must come back within 1′, Phase 2's
end-to-end standard. The hardware half (real hand-controller slews, and whether the
driver reports them as slewing) is recorded as VERIFY until a night allows it.

### Measured results

**Simulated half: met.** Through the real Watney solver on rendered frames:

- **Unconnected (2).** A five-point sweep turned by hand as instructed came back
  as +40.06′ ± 0.37′ altitude and −29.89′ ± 0.59′ azimuth, against 40′ and −30′
  injected, with residual RMS 0.14″. The engine was never told the mount
  existed. Each sample took two blind solves agreeing, as D26 requires.
- **Connected, driven through the engine.** The Phase 3 convergence tests pass
  unchanged in substance. They now confirm proposals and wait for the engine to
  sample, instead of commanding each capture.

Criteria 1, 3 and 4 are pinned by fast tests against the real mount mechanics
with a noiseless solver. A sweep slewed entirely from outside the engine
samples itself. A 3′ declination nudge restarts the sequence with the reason,
and 0.5′ does not. Exposure, gain and readout changes during a sequence land
on the next frame. There are also tests for the single-solve rule and for a
forced sample never using a frame exposed before the press.

**Three defects found by the tests, none visible by inspection:**

1. **Confirmed slews were refused.** D27's spacing rule refused a slew the user
   had confirmed, when it landed closer than the spacing. The rule is for motion
   the engine did not command; D27 now says so.
2. **The engine refused its own planned points.** The next proposal is exactly
   one spacing on, and the mount's report of arriving there read a hair short.
   Hence D27's nine-tenths allowance.
3. **An unconnected sequence ended a sample short.** It stepped each
   instruction on from rotations measured about the nominal pole, which the
   misalignment itself biases. It stopped at four samples when the plan's fifth
   was reachable. Found only through the real solver; the fast test that now
   pins it follows the instructions to the letter. See D27.

**Not verified:** the window. The app launches and stays running with the
simulator; its reducer, commands, preview queue and save path are tested (147
tests). The hardware half stays VERIFY, in particular whether a driver reports
hand-controller motion as `Slewing` (`docs/DEVICE-COMPATIBILITY.md`). The engine
does not depend on it, because it also treats a changing position as motion.

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

**Pulled forward: ZWO and ToupTek (D25).** Both exist as plugins ahead of this
phase, because through ASCOM the one setting that ruined a night — gain — could
not be reached. Built and tested as far as a machine without the cameras or the
vendor libraries allows: struct layouts checked against the real headers for
both ABIs, all data handling tested, no native call ever executed. *Not yet
verified against real hardware*, which is this phase's actual bar.

The line above turned out not to be quite true. The plugins themselves needed
nothing from the application, but offering their gain did: a gain member on
`ICamera`, capabilities on `DeviceDescriptor` so the UI can offer the right
control before connecting, and settings remembered per camera. Contract changes,
made once; the next vendor should need none of them.

---

## Risks

**Silent wrong answers.** The highest-severity risk in the project. A fit that
returns a confident number after an undetected declination nudge is worse than a
crash, because the user acts on it. Mitigated by D11, and by treating residual
checks as a correctness feature rather than a nicety.

**Vendor SDK redistribution terms.** Check each licence before Phase 6, not during.
Checked for the two pulled forward (D25): ZWO's SDK licence permits
redistribution with its notice. The ToupTek library is LGPL-2.1 as INDI
redistributes it, but that covers its Linux and macOS builds; no terms were found
for the Windows `toupcam.dll`. ZWO's library is now shipped from `resources/`
with its notice; ToupTek's is supplied by the user.

**Watney's library API.** D3 assumes it is usable as an embedded library with
manageable database tiers. If that assumption fails, the fallback is subprocessing
ASTAP with a bundled star database, which is workable but complicates the "whole
package" goal. Verify in Phase 0, not Phase 2.

**Driver inconsistency.** iOptron and Sky-Watcher will not behave identically.
Budget real time for this in Phase 3 and write down what you find.

**Weather.** Any plan that requires clear nights to make progress will slip
unpredictably. This is precisely what the virtual observatory exists to prevent,
which is why it is Phase 2 and not Phase 5.
