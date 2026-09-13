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

- [x] Real fixtures from both free keys, recorded 2026-09-11; the findings are research §6.
      Two of the four open questions are answered (SeatGeek gives no prices to a fresh key;
      the DMA id is 343). The Inventory Status and feed fixtures are still documentation
      samples and say so in their `.meta.json`.
- [ ] A source that quotes the Live Nation rooms: SeatGeek partner program application first,
      Ticketmaster partner Availability API second.
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
- [x] One live run against the real sources: on 2026-09-12 discovery resolved 847
      Ticketmaster and 519 SeatGeek Nashville events in twelve calls, and the resolver joined
      Trans-Siberian Orchestra across both without the drift window. Six watches seeded: the
      two Friday 2026-09-18 Bridgestone on-sales, Victoria Monét, and three Bluebird rounds
      that open Monday to Wednesday.

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
- [x] Ops, Incidents, Runs, History windows from LineOps with source names changed; the
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

## Phase 6 — The record reaches people

Goal: a fan with no account can read a Nashville event's on-sale record from a shared
link, subscribe to the on-sale calendar, and be told when face value comes back.

Gate: `release-check` before the public URL; `cadence-check` before `watchlist:prices`
is switched from `Manual` to `Scheduled`. Roadmap: `docs/superpowers/specs/2026-09-12-product-roadmap.md` §10.

- [ ] `Components/Pages/EventRecord.razor` at `/e/{slug}`: anonymous, static-rendered,
      output-cached; the "Was it really sold out?" component from Phase 4 without the desk
      chrome; every cell links to its source and shows its `AllIn` flag. Render test
      `EventRecordPageTests` in the same commit.
- [ ] `Components/Pages/OnSales.razor` at `/onsales` and `GET /onsales.ics` from the feed's
      on-sale and presale times; `OnSaleCalendarTests` proves a subscribed calendar shows a
      presale and a public on-sale for one event, in Nashville local time.
- [ ] `INotifier` in `TicketMiser.Reliability` with one transactional email provider
      behind `HttpClient`; `primary_reappeared` is the first rule delivered;
      `AlertDeliveryTests` proves one alert row becomes one send and a second evaluation
      does not resend.
- [ ] `Subscription` entity (email, event, confirmed, unsubscribe token); a per-event
      subscribe form on `/e/{slug}` with double opt-in and a one-click unsubscribe route.
- [ ] Affiliate accounts: Ticketmaster and SeatGeek on Impact; `SourceLink` builds the
      tagged URL and `SourceLinkTests` proves an untagged link never renders on a price cell.
- [ ] Applications sent and their answers recorded in the research doc §7: SeatGeek partner
      (sent 2026-09-12), Ticketmaster partner Availability API, StubHub affiliate API,
      JamBase trial. An answer of "no" is a recorded answer.
- [ ] First `docs/reports/<yyyy-mm>-nashville.md` published at `/reports/<yyyy-mm>` and
      mailed to confirmed subscribers.

Exit: `dotnet test --filter "FullyQualifiedName~EventRecord|OnSaleCalendar|AlertDelivery|SourceLink"`
green; an incognito browser opens `/e/<slug>` for an event that went on sale this month and
reads its ticks in order; `/onsales.ics` imports into a calendar; one real
`primary_reappeared` email arrives for a real event.

## Phase 7 — The product keeps people

Goal: a fan can sign in without a password, keep their own watches with target prices,
and log what they paid.

Gate: `apple-mudblazor` on every window; `cadence-check` after the scheduler polls the
union of owners' watches.

- [ ] Magic-link sign-in: `AccountEndpoints` issues a single-use token by email, no
      password anywhere; `OwnerId` on `Watch` and `Purchase` with a migration; Phase 6
      subscriptions migrate to the owner whose address they carry. `OwnershipTests` proves
      one owner never reads another's watches.
- [ ] `IngestionScheduler` polls the union of enabled watches across owners; the Ops window
      shows the union's quota cost; `cadence-check` prints the plan for it.
- [ ] Watchlist board and Price history from Phase 4, live once one resale source returns
      prices; until then the board shows availability and face value only and says so in
      the empty state.
- [ ] Purchases, Savings and the receipt export from Phase 4, per owner.
- [ ] Written clarification from Ticketmaster on the scheduled poller and on retaining
      tick summaries, filed as `docs/terms/ticketmaster-<yyyy-mm-dd>.md`, before any paid tier.

Exit: a signed-in fan sets a target on a watched event and `target_reached` arrives by
email from an all-in observation; `dotnet test --filter Ownership` green; the Ops window
shows the union watchlist inside budget and `cadence-check` agrees.
