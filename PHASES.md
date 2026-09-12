# TicketMiser — phases

The reuse plan's §6 with the source research's additions, kept honest by `phase-status`.
A box is checked when its exit criterion has been run and matched, not when the code exists.

## Phase 0 — Bootstrap

Goal: the repository has the operations shell and working rules.

- [x] Solution, central package versions, `.gitattributes`, `.gitignore`, README.
- [x] `CLAUDE.md` working rules; `.claude/skills` and `launch.json` travel with the code.
- [x] `docker-compose.yml`, `compose.dev.yml`, `.env.example`, `scripts/setup.ps1`,
      `publish-data.ps1`, `restore-data.ps1` carried from LineOps with names changed.
- [x] `.github/workflows/ci.yml` carried from LineOps (build, format, test, pending-migration).
- [ ] bunit 1.40 → 2.x, so AngleSharp moves off the advisory line and the
      `NuGetAuditSuppress` in `Directory.Packages.props` can go. Eleven test files, about
      seventy call sites (`TestContext` → `BunitContext`, `RenderComponent` → `Render`).

Exit: `.\scripts\setup.ps1` writes `.env`; `docker compose up -d` starts Postgres; the CI
workflow runs green on a push to `TicketMiser_Development`.

## Phase 1 — Lift the desk

Goal: the desk runs under this product's name with every window a placeholder.

- [x] `src/TicketMiser.Desk` with `DeskBrand`, `IWindowCatalog`, `AddDesk`.
- [x] `src/TicketMiser.Web` host with the §5 catalogue of placeholders.
- [x] `tests/TicketMiser.Desk.Tests` on `DeskTestContext`, 226 passing.
- [x] ADRs 0007, 0013, 0016 adopted.

Exit: `dotnet test` green; `dotnet run --project src/TicketMiser.Web --launch-profile http`
serves the desk at <http://localhost:5270> with the strip, drawers and follow-ups working.
**Met on 2026-09-11.**

## Phase 2 — Fixtures, domain, and the first two sources

Goal: real Nashville responses are parsed into canonical observations and stored.

Gate: `source-fixture` for every adapter change.

- [ ] Fixtures answering the four open questions in the source research §6
      (SeatGeek fees, Ticketmaster on-sale sequence, JamBase AXS coverage, Nashville DMA id).
      The committed fixtures are documentation samples until a key exists; each says so in
      its `.meta.json`.
- [x] Entities per research §4 plus `OnSaleTick`, `AllIn`, `FaceMin`/`FaceMax`, `Source.Kind`.
- [x] `TicketMiser.Data` with partitioned `PriceObservations` and `OnSaleTicks`, the
      monthly partition function, `DatabaseInitializer` seeding categories, sources with
      published limits, and Nashville venues with their ticketing provider.
- [x] `IPriceSource` contract with `FetchCost`; adapters for Ticketmaster Discovery,
      Inventory Status, Discovery Feed (zero quota, disk cap), SeatGeek.
- [x] `EventResolver`: fast path on external id, slow path on performer or name + venue +
      start within six hours; venue never optional; only a primary or feed source moves a
      start or an on-sale time.
- [x] `PriceIngestionService`: reserve budget, fetch, resolve, store on change.
- [x] Integration tests on the Postgres fixture: store-on-change, resolver drift, budget
      refusal, the all-in comparison guard.
- [ ] One manual run against the real sources with a key.

Exit: `dotnet test --filter Adapters` green on committed fixtures only;
`dotnet test --filter Integration` green on Testcontainers; one manual run writes Nashville
observations for both sources and refuses a run when the budget is exhausted.

## Phase 3 — Operations, the scheduler, and the on-sale watch

Goal: the four jobs run on their own, within budget, and the on-sale record is written.

Gate: `cadence-check` before any scheduler merge.

- [x] `Reliability` and `Observability` carried over; `price_drop`, `target_reached` and
      `primary_reappeared` rules added to the alert engine, each with a runbook section.
- [x] `IngestionScheduler` with an injected `TimeProvider`; `PricePollPlanner` from the
      cadence rule; `RetentionService` promoting `FinalPrice`, grading purchases and
      pruning the stream, never `OnSaleTicks`.
- [x] Jobs: `events:discover` (feed + SeatGeek), `onsale:watch`, `watchlist:prices`,
      `prices:finalise`.
- [x] Cadence tests: one on-sale window on a fake clock, ticks at every five-minute mark,
      for every source, and the reappearance alert read out of the record.
- [ ] Ops, Incidents, Runs, History windows from LineOps with source names changed; the
      Ops window shows quota used, reserved and next sweep.
- [ ] The Worker runs one real on-sale window end to end.

Exit: `cadence-check` prints a sane plan for the seeded watchlist; `dotnet test --filter
Category=Cadence` green; the Worker runs one real on-sale window end to end and the
`on_sale_ticks` rows for it read back in order.

## Phase 4 — The tracker's own windows

Goal: a fan can use it.

Gate: `apple-mudblazor` on every window; render tests in the same commit.

- [ ] Watchlist board: best all-in per market per event, never across markets, spread rail
      with one tick per source, primary and resale badged.
- [ ] Event window, first tab "Was it really sold out?": primary availability and resale
      listings over the first two hours, on-sale price pinned.
- [ ] Price history chart; Performers; Event / Performer / Venue destinations; Purchases
      and Savings; the three follow-ups.
- [ ] Ledger export as a dated receipt.

Exit: open a watched Nashville event that went on sale this week and read its on-sale
record, its history and the cheapest resale beside the primary reference; every price cell
links to its source and shows its all-in flag.

## Phase 5 — Release and the first report

Gate: `release-check` before the deploy; `monthly-report` after the first full month.

- [ ] `release-check` passes with no skipped steps.
- [ ] First public URL over HTTPS.
- [ ] `docs/reports/<yyyy-mm>-nashville.md` rendered from the queries, read, then published.

Exit: the report exists and every number in it traces to a query.
