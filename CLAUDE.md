# TicketMiser — working rules

Carried from LineOps, whose repository once held twenty-three agent worktrees, thirty-three
branches and sixty Claude trailers. The rules are short because the history behind them was
long.

## Branches: two, and only two

- `main` — stable. Nothing lands here except a merge from the development branch.
- `TicketMiser_Development` — where all work lands. Work here directly, or on a short-lived
  branch that is merged into `TicketMiser_Development` **and deleted** in the same sitting.
- Nothing else persists: no agent worktrees (`git worktree list` shows one entry), no
  `worktree-agent-*` branches, no parked stashes, no bookmark tags.
- Push after every commit. Two machines clone from origin; an unpushed branch exists on one.

## Two machines

- Start with `git pull` on `TicketMiser_Development`. If `data/snapshots/ticketmiser.dump.json`
  changed, the data did: `scripts/restore-data.ps1 -Force` loads it, replacing and not merging.
- `.env`, user-secrets and `src/TicketMiser.Web/appsettings.Local.json` are per machine and
  never committed. A missing source key shows as an unconfigured source; ask for it rather
  than looking for it elsewhere.
- Setting up a fresh machine, and which half travels: README, "Continuing on another machine".

## Authorship

- Commits carry the developer's own git identity. **Never** add a `Co-Authored-By: Claude …`
  trailer and never commit as a Claude identity. The history was stripped of these once; it
  will not be rewritten again.
- Subject: `type(scope): what changed, as a sentence`. Body: why. Read `git log` for the
  register.

## Scope

- **Nashville, concerts, two markets.** A `Source` is `Primary` or `Resale`; the board never
  ranks one market against the other and never compares a face-value number to an all-in
  one. `AllIn` is a column on every price row, not a convention.
- Do not add a city without a venue list and a DMA id in seed data.
- Nothing is fetched unasked: the scheduler polls watches, and the budget guard refuses a
  run rather than overrunning a source's published limit.
- No scraping. Every source is an API with terms we have read. Every price cell links to
  its source; SeatGeek's logo appears where its numbers do; raw responses are not kept past
  the run that parsed them.
- `OnSaleTick` rows are never pruned and are in every data snapshot.
- The terms behind these rules, per source, and the checks a change must pass are in
  `docs/legal-guidelines.md`. A feature that conflicts with a rule there does not merge.

## Skills

`.claude/skills/` travels with the code. `source-fixture` before touching an adapter,
`cadence-check` before merging a scheduler change, `release-check` before a deploy,
`phase-status` for "where are we". `apple-mudblazor` governs every window.

## Verification before "done"

- `dotnet build` clean; `dotnet test` green; the Web host starts
  (`dotnet run --project src/TicketMiser.Web --launch-profile http`, <http://localhost:5270>).
- For an adapter change: its fixture and parse test are updated in the same commit.
- For a scheduler or budget change: `cadence-check` has been run and its plan read.
- For a data change: the snapshot has been refreshed if the other machine should see it.
