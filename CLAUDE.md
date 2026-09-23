# Working agreements

Instructions to Claude for this repository. They exist because each one was
learned the hard way; the reasons are kept so they can be argued with rather
than only obeyed.

## Do not commit or push unless asked

Finish the work, run the tests, update the documentation, and then **stop and
say it is ready**. Commit only when the user asks for it, or explicitly accepts
an offer to.

"Commit and push" covers that piece of work. It is not standing permission for
the next one.

**Why.** The user reviews changes before they land, and often runs a build
against real hardware first. A commit that arrives before that review takes the
decision away from them, and on a branch already pushed it is awkward to undo.
Leaving it uncommitted costs nothing: the work is done either way, and the
commit is one message away.

**What "ready" means**, and it is worth saying which of these were done:

- `dotnet test FreePolarAlign.sln` passes.
- Documentation updated where the change earned it (see below).
- `dist/` republished if the user needs a build.
- The version bumped (see below).

## Every build handed over gets a new version

`<Version>` lives in `Directory.Build.props`. **Bump the patch before
publishing any build the user will run** — 1.1.0, 1.1.1, 1.1.2 — and bump the
minor instead when the build carries a new capability rather than a fix. Say
the version in the handover message, so the log line can be matched to a
conversation.

The version reaches the user through the session log's second line, written by
`AppVersion`: version, git commit (with `-dirty` when the tree is not clean),
and the build's timestamp.

**Why.** A night was spent re-diagnosing a bug that had already been fixed,
because the published folder the user ran and the build containing the fix
carried the *same* stamp — both `1.0.0+58066392a537-dirty`. The only way to
tell them apart was to recognise the wording of an error message. A frame or a
log from the field is only interpretable if its build is identifiable, and a
distinct version is the cheapest way to make it so.

Two habits follow from the same lesson:

- When a log arrives, **check the version line first** and say plainly if it
  predates the fix being discussed. Diagnosing a stale binary wastes the user's
  observing time, which is the one resource this project cannot replace.
- Prefer committing before publishing, so the stamp names a real commit rather
  than `-dirty`. If the user has not asked for a commit, publish anyway and say
  the stamp is dirty.

## Update documentation when the change earns it

- **`docs/DECISIONS.md`** — a decision that constrains future work, with the
  reasoning and the measurements behind it. New `D<n>` entries continue the
  existing numbering. Revise an existing entry when its facts change: D16 said
  the ASCOM layer refuses non-J2000 drivers long after it had been taught to
  convert, and a stale decision record is worse than none, because it is
  believed.
- **`docs/MOUNT-COMPATIBILITY.md`** — anything learned about a specific driver
  or mount, tagged `[DOC]`, `[USER]`, `[INFER]` or `[UNKNOWN]` as that file's
  own header requires.
- **`docs/ROADMAP.md`** — when a phase's exit criterion is met, or is shown not
  to be.
- **`README.md`** — only for what a newcomer needs: project layout, how to
  build, what the pieces are.

Not every change earns an entry. A bug fix belongs in the commit message; a
*decision* — this approach over that one, and why — belongs in the decisions
file, where the next person looking at the same trade-off will find it.

## House style, briefly

The prose in this repository explains **why**, not what. Comments and commit
messages carry the reasoning, the measurement, and the thing that was tried and
rejected. Match that. A comment restating the code is worse than no comment,
and a number without its measurement is an opinion.

Tests are expected to earn their place: they pin down a decision, a guarantee,
or a defect that actually happened. When fixing a defect, check that the new
test fails against the old behaviour — a regression test that never could have
caught the regression is worse than none.
