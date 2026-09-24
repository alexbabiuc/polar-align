# Working agreements

## Do not commit or push unless asked

Finish the work, run the tests, update the documentation, then say it is ready.
Commit only when asked, or when an offer to is accepted. "Commit and push"
covers that piece of work, not the next one.

"Ready" means, and say which were done:

- `dotnet test FreePolarAlign.sln` passes.
- Documentation updated where the change earned it.
- Version bumped.
- `dist/` republished, if the user needs a build.

## Every build handed over gets a new version

`<Version>` in `Directory.Build.props`. Say the version in the handover message.

Until every phase in `docs/ROADMAP.md` is implemented and the user calls the
project ready, this is a plain counter and not SemVer: **0.0.1, 0.0.2, 0.0.3**.
Only the last field moves, however large the change. The project is nowhere near
1.0.0 and a version that implied otherwise would be a claim about maturity.

Switch to SemVer when the user says so, not when the numbers look ready.

- Check a log's version line first, and say plainly if it predates the fix being
  discussed.
- Commit before publishing, so the stamp names a commit rather than `-dirty`.
  If no commit was asked for, publish anyway and say the stamp is dirty.

## Update documentation when the change earns it

- **`docs/DECISIONS.md`** — a decision that constrains future work, with the
  reasoning and the measurements. New `D<n>` entries continue the numbering.
  Revise an entry when its facts change; a stale decision record is worse than
  none, because it is believed.
- **`docs/DEVICE-COMPATIBILITY.md`** — anything learned about a specific driver,
  mount or camera, tagged as that file's header requires.
- **`docs/ROADMAP.md`** — when a phase's exit criterion is met, or shown not to
  be.
- **`README.md`** — only what a newcomer needs: layout, how to build, what the
  pieces are.

A bug fix belongs in the commit message. A *decision* belongs in the decisions
file.

## House style

Prose here explains **why**, not what. A comment restating the code is worse
than no comment, and a number without its measurement is an opinion.

Tests pin down a decision, a guarantee, or a defect that actually happened. When
fixing a defect, check the new test fails against the old behaviour.
