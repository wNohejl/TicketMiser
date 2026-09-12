# The skills and the implementation that carry TicketMiser through its lifecycle

**Date:** 2026-09-11
**Status:** Research and recommendation, follows the [source research](2026-09-11-nashville-concert-price-sources-research.md)
**Audience:** the developer, who is one person on two machines

This document answers one question: which agent skills, working rules and implementation
practices pay for themselves across the life of this site, from the first fixture to a
public Nashville on-sale record. It is written against what already exists: the LineOps
working rules, the `apple-mudblazor` skill that lives in the LineOps checkout, the
`design-to-phases` and `phase-status` skills in the OS vault, and the `superpowers`
brainstorm → spec → plan → execute cycle that produced every document in this directory.

The short version: **twelve skills, four of which exist and need copying, five of which
this product must write, and three that come from the plugin.** The implementation
practices are the LineOps ones with two additions the on-sale record forces: a fixture
discipline for every source, and a cadence test that runs against the clock.

---

## 1. What the lifecycle actually is

| Stage | What is happening | What can go wrong | What the skill or rule prevents |
|---|---|---|---|
| **Fixture** (now) | Saving real responses from Ticketmaster, SeatGeek, the Discovery Feed | Building parsers on guessed shapes; not knowing if SeatGeek prices include fees | `source-fixture` |
| **Domain** (Phase 2) | Entities, resolver, ingestion, budget guard | Resolver merges the Ryman and the Opry House; a run overruns the quota | `superpowers:test-driven-development`, the integration bench |
| **Cadence** (Phase 3) | Scheduler, on-sale watch, budget arithmetic | The five-minute watch fires late or never; quota is gone by noon | `cadence-check` |
| **Windows** (Phase 4) | Watchlist, on-sale record, price history, purchases | The desk drifts from the design system; a window is built modal | `apple-mudblazor`, ADR 0016 |
| **Release** | Docker, HTTPS, first public URL | Secrets in the image; the feed download fills the disk | `release-check`, the CI workflow |
| **Operate** | Daily runs, alerts, the monthly report | A source changes shape silently; a quota is exhausted; nobody notices | `source-drift`, the reliability layer, `monthly-report` |
| **Reflect** | Monthly | Skills that never fire; rules that no longer hold | `skill-audit` (exists in the vault) |

---

## 2. The skills

### 2.1 Exist already, copy them in

| Skill | Where it is | What to change |
|---|---|---|
| `apple-mudblazor` | `LineExtractor/.claude/skills/apple-mudblazor` | Nothing in the references. The templates were generalised from LineOps files that now live in `TicketMiser.Desk` under different names; point the workflow at the desk's own primitives rather than copying templates a second time. |
| `design-to-phases` | `OS/.claude/skills/design-to-phases` | It scaffolds a new repo and writes to the vault. For this project only the phase-plan half applies: `PHASES.md` with demonstrable exit criteria per phase, from §6 of the reuse plan. |
| `phase-status` | `OS/.claude/skills/phase-status` | As is. It is the honest answer to "where is TicketMiser" and it keeps checkboxes true. |
| `skill-audit` | `OS/.claude/skills/skill-audit` | As is, monthly. |

**Where they go.** `.claude/` is gitignored in this repository. That was right for a
machine-specific `launch.json`; it is wrong for skills that took a week to write and that
the laptop needs too. Un-ignore `.claude/skills/` and `.claude/launch.json`, keep
`.claude/settings.local.json` ignored, and the skills travel with the code the way the
LineOps rules say everything must.

### 2.2 The product must write these

Each is one job, a description that reads as a routing rule, and a script for the
deterministic part. Written with `superpowers:writing-skills`.

**`source-fixture`** — "Use when adding or changing a source adapter, or when a parse test
fails on live data." Fetches one real response from a named source and Nashville event,
strips volatile fields, saves it under `tests/TicketMiser.Tests/Fixtures/<source>/`, and
writes or updates the parse test that reads it. Its script does the fetch and the
redaction; the model writes the assertion. It is how the four open questions in the
source research get answered, and it is how every future shape change gets caught.

**`cadence-check`** — "Use before merging any change to the scheduler, the budget
calculator or the on-sale watch, and whenever a run is refused for budget." Reads the
watchlist size and each source's limits, prints the day's sweep plan (sweeps, interval,
reserve, on-sale watches scheduled and their cost), and runs the scheduler against a fake
clock through one on-sale window to prove the five-minute ticks land. Deterministic
arithmetic in a script; the model reads the plan back and says whether it is sane.

**`release-check`** — "Use before any deploy or when asked whether the site can ship."
Builds the image, runs the container with the compose file, waits for health, checks that
no secret is in the image or the compose environment, that HTTPS terminates, that the
database initialiser ran, that the feed download has a size cap, and that
`dotnet ef migrations has-pending-model-changes` is clean. The LineOps CI job in code, run
locally before the push.

**`source-drift`** — "Use when a run's parse errors rise, when a source's success rate
alert fires, or weekly." Diffs the newest live response against the saved fixture's shape:
new fields, missing fields, type changes. Reports; does not change the fixture, because
that is `source-fixture`'s job after a human has looked.

**`monthly-report`** — "Use on the first of the month or when asked for the Nashville
report." Runs the report queries from the source research §4.1 (minutes to primary sellout
against resale listings present, by venue; fee gap by venue), renders the numbers as the
report's fixed shape, and stops. It does not publish; a human does, after reading it.

### 2.3 From the plugin

`/plugin install superpowers@claude-plugins-official`, then rely on three of its skills and
ignore the rest:

- **`brainstorming` → `writing-plans` → `executing-plans`** is the cycle that produced this
  directory. Keep using it: a spec in `docs/superpowers/specs`, a plan in
  `docs/superpowers/plans`, executed in checkpointed batches.
- **`test-driven-development`** for every adapter, the resolver and the budget guard. The
  LineOps lessons that each carry a test are the proof this works here.
- **`verification-before-completion`** is the "Verification before done" section of the
  working rules, enforced.

Skip `using-git-worktrees` and `dispatching-parallel-agents`. The LineOps rules exist
because twenty-three agent worktrees were left behind once. One developer, one branch,
short-lived feature branches merged and deleted in the same sitting.

---

## 3. Working rules for this repository

Write `CLAUDE.md` from the LineOps one with these changes:

- **Branches:** `main` and `TicketMiser_Development`, the LineOps shape. Work lands on the
  development branch; `main` takes merges that have passed `release-check`. A feature
  branch is merged and deleted in the same sitting. Push after every commit.
- **Authorship:** the LineOps rule stands. Commits carry the developer's identity and no
  `Co-Authored-By` trailer. The four commits that carried one were stripped on
  2026-09-11; it is not to be rewritten again.
- **Scope:** Nashville, concerts, two markets. `Sources` with `Kind` primary or resale;
  `Enabled` follows configuration. Do not add a city without a venue list and a DMA id.
- **Data:** the snapshot workflow (`publish-data.ps1` / `restore-data.ps1`) as in LineOps,
  plus one rule the on-sale record adds: `OnSaleTick` rows are never pruned and are in
  every snapshot.
- **Terms:** every price cell links to its source; SeatGeek's logo where its numbers
  appear; raw responses are not kept past the run that parsed them; no scraping.
- **Verification before done:** `dotnet build` clean, `dotnet test` green, the Web host
  starts, and for scheduler changes `cadence-check` has been run.

---

## 4. Implementation practices that pay across the lifecycle

**Carry from LineOps unchanged.** Central package versions; the seven-project split; the
`ISourceAdapter` shape with a `FetchCost`; the budget guard that refuses rather than
overruns; store-on-change; the reliability layer with freshness, success rate, volume and
budget alerts and incidents that need a root cause; OpenTelemetry; the CI workflow with
`dotnet format --verify-no-changes` and the pending-migration check; Testcontainers for the
integration bench; the snapshot scripts.

**Add for this product.**

1. **A fixture per source per shape, committed.** Adapter tests read fixtures, never the
   network. `source-fixture` writes them; `source-drift` watches them. This is the single
   practice that keeps a scraper-and-history product alive when the sources change, which
   they will.
2. **The on-sale watch is a state machine with a clock injected.** Ticks are scheduled
   from `sales.public.startDateTime`, not from a timer, so the test can run a whole on-sale
   window in a second. `cadence-check` is that test with real numbers.
3. **Two tables, two retention rules.** `PriceObservation` is store-on-change and pruned
   after `FinalPrice`. `OnSaleTick` is every tick and kept forever. Partition both by month;
   the partition function from LineOps takes a table name.
4. **The all-in flag is a column, not a convention.** Every price row carries `AllIn`; every
   comparison filters on it; every cell renders it. A test asserts the board never ranks a
   face-value number against an all-in one.
5. **Quota is a first-class resource with a dashboard number.** The Ops window shows calls
   used, calls reserved for on-sale watches, and the next sweep time, the way LineOps shows
   its poll plan.
6. **The feed is a source too.** The Discovery Feed download is an `ISourceAdapter` with a
   `FetchCost` of zero quota and a real disk cost, capped in configuration.
7. **Exports are files, alerts are rows.** The ledger export is a deterministic render from
   `OnSaleTick`; the `primary_reappeared` alert is a rule in the existing engine. Neither
   needs a new subsystem.

---

## 5. Order of work

1. Un-ignore `.claude/skills` and `launch.json`; copy the four existing skills; write
   `CLAUDE.md`; strip the trailers. One commit. **Done 2026-09-11**, with the five product
   skills written in the same commit as shells whose scripts run today and whose
   database-dependent steps say what they are waiting for. `design-to-phases` came in as
   `phase-plan`, the phase-plan half only.
2. Write `source-fixture` and use it to answer the four open questions. One commit per
   fixture, with its parse test.
3. Phase 2 of the reuse plan, test-first, with the `OnSaleTick` table and the `AllIn`
   column added to the entity list.
4. Write `cadence-check` alongside the scheduler in Phase 3.
5. Write `release-check` before the first deploy. `source-drift` and `monthly-report` after
   the first month of runs, when there is something to drift from and something to report.

---

## Sources

- [Superpowers plugin](https://github.com/obra/superpowers) — skill list and install command.
- [Claude Code skills best practices (DesignRevision)](https://designrevision.com/blog/claude-code-skills-best-practices) — routing-rule descriptions, scripts for deterministic work, eight to twelve skills, monthly audit.
- [Best Claude Code skills 2026 (Firecrawl)](https://www.firecrawl.dev/blog/best-claude-code-skills) — lean SKILL.md with companion files.
- LineOps `CLAUDE.md`, `.github/workflows/ci.yml`, `.claude/skills/apple-mudblazor` — the practices this document carries forward.
- OS vault `.claude/skills/design-to-phases`, `phase-status`, `skill-audit`.
