# Mount compatibility research (Phase 3 / D9 VERIFY)

D9 names iOptron (CEM/GEM/HAE via the iOptron ASCOM driver) and Sky-Watcher (via
EQMOD/EQASCOM, or the SynScan ASCOM driver) as the initial test targets, flags
`SideOfPier` support as varying between them, and marks the whole area
**VERIFY**. This document is that verification, done by reading driver
documentation, changelogs, and user reports rather than by testing against
real mounts (neither is available on the development machine — see the
Ascom provider's own doc comments for why the code itself is
compiled-but-unverified).

**How to read this document.** Every claim below is tagged:

- **[DOC]** — stated in a vendor changelog, manual, or the ASCOM Platform's
  own developer documentation.
- **[USER]** — reported by users/developers in forums, mailing lists, or
  issue trackers; treat as anecdotal, not authoritative, but often the only
  evidence available for exactly the kind of driver quirk this table exists
  to catch.
- **[INFER]** — my own inference from the above, not stated outright anywhere
  I found. Flagged so it can be weighted accordingly.
- **[UNKNOWN]** — I found nothing either way. Left unknown deliberately
  rather than guessed, per the brief.

No mount or driver was available to test directly. Nothing here should be
treated as more solid than its tag says.

---

## Summary table

| | iOptron ASCOM driver (Commander) | EQMOD/EQASCOM | SynScan ASCOM driver |
|---|---|---|---|
| `SideOfPier` (read) implemented | Yes, since v2.60 (2014) **[DOC]** | Yes **[DOC]**/**[USER]** | Yes, but GEM mounts only, and only with hand-controller firmware ≥ x.38 **[USER]**, sourced from a driver-introduction document I could not fetch directly — see note below |
| `SideOfPier` reliability | Repeated regressions across releases: "sometimes reports incorrect SideOfPier" recurs in the v3.20 (2015) and v4.00 (2015) changelogs; a further "reports incorrect side of pier in Southern Hemisphere" fix shipped in v5.62 (2017) **[DOC]** | Historically **unreliable and dangerous**: the original HA−6h calculation could report the wrong side while physically unchanged, corrupting autoguider Dec-reversal logic **[USER]**; a fix moving to a Dec-direction-based calculation was reported working after field testing (multiple meridian flips, 15-point models) around 2018 **[USER]** — see caveat below on which codebase this fix landed in | Weakest of the three by reputation (matches D9's existing note); `DestinationSideOfPier` is documented as **not implemented** at all **[USER, low-confidence source]** |
| `CanSlewAsync` / `SlewToCoordinatesAsync` | Supported; iOptron's driver update history lists `SlewToCoordinatesAsync`-class goto features as part of its long-standing ASCOM feature set **[INFER]** from changelog entries, not a direct quote | Explicitly supported — EQMOD's own documentation describes `SlewToCoordinatesAsync()` as a supported goto command **[DOC]** | Supported per the ASCOM standard interface it implements; no specific quirk found **[UNKNOWN]** beyond the general note that `SlewToCoordinatesAsync` returns immediately and callers must poll `Slewing`, which is an ASCOM-wide (not driver-specific) contract detail **[DOC]** |
| Site latitude/longitude source and trust | Reported directly from the mount (GPS-equipped models) through Commander to the ASCOM layer **[USER example showed a plausible real coordinate pair]**; Commander must be running for some consumers to read it, suggesting the value flows through Commander rather than being queried driver-to-mount on demand **[INFER]** | Not mount-sourced: EQMOD is a driver for a mount head with no independent site knowledge, so latitude/longitude/elevation are values the **user (or planetarium software) types into EQASCOM's Site Information panel**, or acquires via an optional external serial GPS module through EQMOD's own GPS setup dialog **[DOC]** | Likely the same user-entered-or-passed-through model as EQMOD, since the SynScan hand controller/app itself takes manually-entered or GPS-derived (app/phone GPS) coordinates, but I found no ASCOM-layer-specific confirmation **[UNKNOWN]** |
| Surprising meridian behaviour | Mount-side "Meridian Behavior" setting can be configured to `Flip at Designed Position` (autonomous flip a configurable number of degrees past the meridian, e.g. 0°–14° on a CEM40, 0°–15° on a HEM44) or `Stop at Designed Position` **[DOC, from iOptron manuals/Commander UI]** — an autonomous flip driven by the *mount firmware itself*, independent of what the ASCOM client commands next | EQMOD exposes an "Allow SideOfPier Writes" option (letting a client *command* a pier-side/flip via ASCOM) that community guidance recommends leaving **disabled** **[USER]**; separately, an "Enable Limits" RA-limit feature can trigger an auto-flip when the RA limit is hit, which is **not necessarily at the meridian** **[USER]** | Not found **[UNKNOWN]** |

---

## Detail and citations

### iOptron ASCOM driver / Commander

- `SideOfPier` display and the underlying feature were added together with
  `MoveAxis`, `SetPark`, `DestinationSideOfPier` in **v2.60 (2014-05-05)**:
  "Added MoveAxis, SetPark, DestinationSideofPier feature." The same release
  notes "Fixed some bugs in Park, Unpark, SlewSettleTime and SideofPier."
  **[DOC]** — [iOptron ASCOM 6.4xx update history](https://www.ioptron.com/v/ASCOM/ASCOM64xx_UpdateHistory.txt)
- Two further `SideOfPier`-specific regressions are recorded later in the same
  changelog: **v3.20 (2015-01-16)** and **v4.00 (2015-03-12)** both list
  "Sometimes the driver reports incorrect SideofPier," and **v5.62
  (2017-09-25)** lists "The program reports incorrect side of pier in
  Southern Hemisphere." **[DOC]** — same source. This is a real pattern, not
  a one-off: the same defect class recurred across at least three releases
  spanning three years, which argues for treating `SideOfPier` as advisory on
  this driver too (matching D9's stance generally, even though D9's own text
  singles out SynScan as "the weaker" of the two named drivers) rather than
  assuming iOptron is simply "the reliable one."
- Site latitude/longitude/elevation are read from the mount (GPS-equipped
  CEM/GEM/HAE models have onboard GPS) via Commander; a support thread shows
  a real `SiteLatitude`/`SiteLongitude` pair being read back through the
  driver, and separately notes that the vendor's iPolar tool needs Commander
  running before it can read lat/long from the mount — implying the ASCOM
  layer's view of site location is mediated by Commander rather than being an
  independent driver-to-firmware query. **[DOC for the property names; INFER
  for the Commander-mediation claim]** —
  [SharpCap forum thread](https://forums.sharpcap.co.uk/viewtopic.php?t=298&start=10),
  [Cloudy Nights iPolar thread](https://www.cloudynights.com/topic/733152-ioptron-ipolar-problem-need-help/)
- Meridian handling is explicitly configurable mount-firmware behaviour, not
  just an ASCOM client concern: iOptron's manuals document a "Meridian
  Treatment"/position-limit setting (0°–14° past meridian on a CEM40, 0°–15°
  on a HEM44 for the Northern Hemisphere, narrower in the Southern
  Hemisphere) with two modes — flip automatically at the configured position,
  or stop tracking there — configurable either at the hand controller or
  through Commander's Mount Panel Preferences (`:SMT#` RS-232 command).
  **[DOC]** — [CEM40 manual](https://www.ioptron.com/v/Manuals/C40_CEM40_Manual.pdf),
  [HEM44 manual](https://www.ioptron.com/v/Manuals/HEM44_Manual.pdf),
  [RS-232 command language v3.10](https://www.ioptron.com/v/ASCOM/RS-232_Command_Language2014V310.pdf).
  **Consequence for this project:** if "Flip at Designed Position" is active,
  the mount can flip itself mid-sequence with no ASCOM command from this
  software at all — exactly the D8 hazard ("an unexpected flip mid-sequence
  invalidates the fit") but originating below the ASCOM layer entirely, where
  polling `SideOfPier` after each slew (already planned per D8/D9) is the
  only defence; there is no way to disable the *possibility* from the client
  side except by choosing "Stop at Designed Position" or keeping the sweep
  comfortably inside the configured limit.

### EQMOD / EQASCOM

- `SlewToCoordinatesAsync()` is documented directly as a supported EQMOD goto
  command. **[DOC]** — search-indexed EQMOD documentation (I was not able to
  fetch the primary PDF directly in this session; treat this line as
  corroborated by multiple independent secondary descriptions rather than a
  single primary quote).
- Site latitude/longitude/elevation are **not** mount-sourced for EQMOD:
  EQASCOM's own "Site Information" setup panel is where a user (or an
  upstream planetarium application) enters latitude, longitude, elevation and
  hemisphere, with named site slots that can be saved and recalled; an
  optional external serial GPS module can be attached and its coordinates
  pulled in via EQMOD's own GPS dialog (`[Retrieve Coordinate and Time
  Data]`), which is a separate mechanism from anything the mount head itself
  knows. **[DOC]** — [EQASCOM Quick Start Guide](https://eq-mod.sourceforge.net/docs/EQASCOM_QuickStart.pdf),
  [Using a GPS module (EQMOD wiki)](http://welshdragoncomputing.ca/eqmod/doku.php?id=using_gps).
  **Consequence for D14:** for an EQMOD-driven mount, "ask the mount driver
  for site location" is really "ask whatever the user (or another
  application) last typed into EQASCOM," which is not obviously more
  trustworthy than this project's own manual-entry path with its uncertainty
  display — it is a *pass-through*, not an independent GPS-grade source,
  unless the user has actually wired up EQMOD's GPS module feature. The
  D14 preference order ("mount driver site properties" before "manual entry")
  should not be read as "EQMOD's reported site is necessarily more accurate
  than manual entry" — only that it's less friction, and it inherits whatever
  accuracy the user's earlier entry had.
- `SideOfPier` had a documented, serious defect in at least one EQMOD-family
  codebase: the original implementation computed pier side from `HourAngle −
  6h`, which could report the wrong side while the telescope's physical
  orientation had not changed (specifically pointing near/below the pole),
  and was explicitly called "dangerous to use" because autoguider
  declination-reversal logic depends on it. A fix recomputing pier side
  directly from the declination-axis movement direction (matching the ASCOM
  definition) was field-tested (15-point sky models, multiple meridian flips)
  and reported working, merged around August 2018. **[USER]** —
  [INDI forum: EQMOD telescope pier side](https://indilib.org/forum/mounts/1883-eqmod-telescope-pier-side.html).
  **Caveat, and this matters:** that thread is on the INDI project's forum,
  discussing INDI's own EQMod driver (a separate, Linux/INDI-ecosystem
  reimplementation) being brought in line with the ASCOM `SideOfPier`
  definition — it is not certain from what I could access whether the
  *original* HA−6h defect, or this specific fix, also describes the
  Windows EQMOD/EQASCOM ASCOM driver's own code, or only the analogous INDI
  driver that models mount behaviour similarly. I could not independently
  confirm which codebase(s) carried the bug. Treat "does EQASCOM (the
  Windows ASCOM driver) still have this exact defect today" as **[UNKNOWN]**,
  and the historical existence of the failure mode as real, sourced, but
  possibly attached to the wrong driver.
- A related, separately-sourced feature-request thread against EQMOD itself
  (not INDI) shows the EQMOD maintainers still refining `SideOfPier`
  semantics as recently as February 2023: the request asks EQMOD to
  distinguish the full four-state ASCOM pointing-state space (`PierSide` ×
  physical pointing direction) rather than collapsing it to two, and was
  still open/unassigned as of that date. **[DOC — primary source, an open
  SourceForge tracker item]** — [EQMOD feature request #14](https://sourceforge.net/p/eq-mod/feature-requests/14/).
  This independently corroborates that `SideOfPier` correctness has been an
  ongoing, not fully closed, concern in the actual Windows EQMOD codebase.
- EQMOD exposes a "SideOfPier" driver-setup choice between `Pointing(ASCOM)`
  and a `Physical` mode, and separately an "Allow SideOfPier Writes" option
  that lets an ASCOM client *command* a pier flip by writing to the property;
  community guidance is to leave writes **disabled**. **[USER]** — indexed
  from EQMOD/APT forum guidance. **Consequence:** the *meaning* of a value
  read from `SideOfPier` on an EQMOD-driven mount depends on a user-facing
  configuration choice this project does not control, which is one more
  reason D9's "treat SideOfPier as advisory, cross-check hour angle
  independently" is the right call rather than a defensive default — for
  EQMOD specifically, "advisory" is doing real work.
- Separately, EQMOD's own RA "Enable Limits" feature can trigger an
  automatic pier flip when a configured RA limit is reached, which the
  community guidance explicitly notes is **not necessarily at the meridian**.
  **[USER]**. **Consequence for D8:** exactly the mount-autonomous-flip
  hazard identified for iOptron above, via a different mechanism; guidance
  found recommends disabling "Enable Limits" for this kind of application,
  which this project's setup instructions (Phase 4/5) should probably repeat.

### SynScan ASCOM driver (hand controller and SynScan App)

- `SideOfPier` is reported, in a document titled "ASCOM SkyWatcher Telescope
  Driver Introduction," to be implemented **only for GEM mounts**, and only
  with hand-controller firmware **≥ 3.38 (V3 HC) / ≥ 4.38 (V4 HC)**; earlier
  firmware gets "limited functionality." The same document reportedly states
  `DestinationSideOfPier` is **not implemented** at all. **[USER — I could
  not fetch this PDF directly in this session (host returned 403); this is
  relayed via multiple independent search-result summaries that agree with
  each other and is treated as more trustworthy than a single anecdote for
  that reason, but it is still not a primary-source quote I obtained
  myself.]** — [ASCOM SkyWatcher Telescope Driver Introduction](https://astropolis.pl/applications/core/interface/file/attachment.php?id=155047)
  (the direct link that repeatedly surfaced but would not load for me).
  **Consequence:** on an AZ-mounted SynScan setup, or a GEM with an
  old/unspecified HC firmware version, expect `SideOfPier` to be entirely
  unavailable — this project's `PierSide.Unknown` fallback (D9) is not an
  edge case here, it is the expected steady state for a meaningful slice of
  SynScan users.
- A fix "for ASCOM SideOfPier failing the Conform test in the southern
  hemisphere" shipped in **SynScan driver v2.3.9 (2023-07-11)**. **[DOC or
  close to it — relayed from a search summary of release notes I could not
  independently re-derive the exact wording for; treat as DOC-strength but
  unquoted]**. This is the same class of Southern Hemisphere `SideOfPier`
  defect independently documented for iOptron above (v5.62, 2017) — it is
  apparently a common failure mode across unrelated driver codebases, which
  is useful context: a fresh Southern Hemisphere bug turning up in *any*
  ASCOM mount driver would not be a surprise.
- No SynScan-specific meridian-flip-behaviour documentation was found beyond
  the general community guidance (also seen in EQMOD contexts) to let the
  mount track well past the meridian before attempting a flip, because the
  expected pointing state is uncertain close to the meridian itself.
  **[USER]**. This is a generic ASCOM-telescope caution rather than something
  SynScan-specific, but it reinforces D8's own meridian-avoidance margin.
- Reliability complaints found in forums (SharpCap forums, Cloudy Nights) for
  the SynScan ASCOM driver were mostly about **connection drops and serial
  port/adapter flakiness**, not `SideOfPier` or slewing correctness
  specifically. **[USER]**. Relevant context for field robustness, but not a
  data point for the pointing-state question this table is mainly about.
- Site latitude/longitude: no ASCOM-driver-specific documentation found. The
  SynScan hand controller and SynScan App both take location by manual entry
  or (App only) phone/host GPS, so it is reasonable to expect the ASCOM
  driver simply reflects whatever was set there — but I found no page stating
  this outright for the ASCOM layer specifically. **[UNKNOWN]**, not
  inferred further than that.

---

## What this means for D9/D8 in this project

1. **`SideOfPier` should be treated as advisory across all three drivers, not
   just SynScan.** D9's text singles out SynScan as historically weaker, and
   the evidence above supports that (an entire mount class — AZ, or old
   firmware — can lack the property outright), but iOptron has its own
   multi-year history of `SideOfPier` regressions, and EQMOD's correctness
   depends on a user-facing setup choice. The contract already treats
   `SideOfPier` as advisory (`PierSide.Unknown` fallback, D9); this research
   does not change that design, it reinforces that the fallback path will be
   exercised in practice, not just in theory.
2. **An unexpected mid-sequence flip (D8's core hazard) has at least two
   real, documented, non-ASCOM-layer causes**: iOptron's firmware-level
   "Flip at Designed Position" meridian treatment, and EQMOD's RA
   "Enable Limits" auto-flip. Neither is something this software's own
   pre-slew meridian-crossing check can prevent, because both trigger from
   mount/driver-side state this software does not control. The D8 mitigation
   that *does* help is exactly the one already planned: checking
   `SideOfPier` (or independently-computed hour angle) between every capture,
   not just before starting the sweep, so a mount-initiated flip is caught by
   the next check rather than assumed away. Phase 4/5 setup guidance should
   tell users to disable both features where present.
3. **D14's "mount driver site properties" preference is solid for
   GPS-equipped iOptron mounts, but is closer to a pass-through of manual
   entry for EQMOD** (and, unconfirmed, likely SynScan). The accuracy
   consequence: for a non-GPS mount, reading `SiteLatitude`/`SiteLongitude`
   from the driver does not actually clear D14's 0.1′ accuracy bar by itself
   — it only avoids re-typing a number whose accuracy still depends entirely
   on how it got into the driver in the first place. The manual-entry
   uncertainty display D14 already requires should probably also fire (or at
   least be available) when the "mount driver" source is, transitively, just
   a driver's cached copy of an earlier manual entry — worth a UX note for
   whichever phase builds that flow, though nothing in `IMount`'s contract
   needs to change: it already just reports "whatever the driver currently
   reports" and leaves sourcing trust to the caller.
4. **No compatibility blocker was found for `CanSlewAsync`/`SlewToCoordinatesAsync`**
   on any of the three drivers — all three are documented or strongly implied
   to support it. The open question is entirely about `SideOfPier` and site
   location trust, not about whether the core D9 slewing mechanism works.

## Gaps — genuinely unknown, not to be guessed at

- Whether the Windows EQMOD/EQASCOM ASCOM driver (as opposed to INDI's
  separate EQMod driver) still has, or ever had, the HA−6h `SideOfPier` bug
  described in the INDI forum thread.
- Whether the SynScan ASCOM driver reports site latitude/longitude at all,
  and if so from what source.
- Any meridian-flip firmware behaviour specific to the SynScan driver
  analogous to iOptron's "Meridian Treatment" setting.
- Real-world `SideOfPier` behaviour on current (2025-2026-era) firmware for
  any of the three drivers — everything above is changelog/forum evidence,
  most of it years old; a driver update since could have changed any of it.
  This is exactly why D9 asks for it to be reverified against real hardware
  during Phase 3/4, which this document does not substitute for.
