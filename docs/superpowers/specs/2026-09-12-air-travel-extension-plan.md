# Extending the record to air travel: what carries over, what does not, and the plan

**Date:** 2026-09-12
**Status:** Research and plan, written against `TicketMiser_Development` at `7f9618d` with
the working tree's `SourceRegistry` and `DiscoveryService` changes
**Audience:** the developer deciding whether a second vertical is worth the seam it needs
**Descends from:** the [source research](2026-09-11-nashville-concert-price-sources-research.md),
the [reuse plan](2026-09-11-ticket-tracker-desk-reuse-plan.md), the
[product roadmap](2026-09-12-product-roadmap.md) and the
[legal guidelines](../../legal-guidelines.md), whose nine rules every feature below is
checked against

The question asked was: plan the product so that it can extend to air travel. This
document answers it in three parts. First, which of the things the repository already
does are about *watching a price* and which are about *concerts*, because only the first
kind extends. Second, what an air fare actually is as a thing to observe, where one can be
read from without scraping, and what the law says about the number on the cell. Third,
the seam to cut and the phases to build, so that flights arrive as a second vertical on
the same desk, the same scheduler and the same budget guard rather than as a fork.

The short version: **the platform extends; the domain does not; and the source problem is
harder than it was for tickets.** Every free fare API a hobby project could once use is
gone or gated as of this summer. The honest sources are one affiliate-cached price feed,
one government fare survey, and a booking API that only a seller may hold. That is enough
for a fare history and a bag-fee record, which is the flight analogue of the on-sale
record, and it is enough to price a whole trip to a Nashville show. It is not enough for a
live search. Build the seam first, because it costs nothing and protects the ticket work;
build the vertical only when the first source's fixture is saved.

---

## 1. What extends, measured against the code

Read `src/`, not the plan. Each row says whether the piece knows it is about concerts.

| Piece | Where | Knows about concerts? | Verdict |
|---|---|---|---|
| `Source`, `SourceKind`, published limits | `Core/Entities/Reference.cs` | `Kind` is `Primary`/`Resale`/`Feed`, a ticket vocabulary | **Extends** once `Kind` gains the airline vocabulary (§5) |
| `CreditBudgetGuard`, `RateLimitPerDay/Hour`, the 20 percent reserve | `Ingestion/Services/CreditBudgetGuard.cs` | No | **Extends unchanged** |
| `IngestionRun`, `KpiDaily`, `Alert`, `Incident`, the runbook | `Core/Entities/Operations.cs`, `Reliability` | `Alert.EventId` is a ticket event | **Extends** with a subject key instead of an event id |
| `IngestionScheduler`, `IngestionJobs`, `PricePollingMode` | `Ingestion/Services` | Job keys are `events:*`, `watchlist:*`, `onsale:*` | **Extends**: jobs are data, add four more |
| `PricePollPlanner` | `Ingestion/Services/PricePollPlanner.cs` | Counts `db.Watches` on `Event.StartsAt` and `OnSaleAt` | **Needs a seam**: the watch count and the on-sale reserve must come from each vertical |
| `OnSaleWindow`, `OnSaleWatchService`, `OnSaleTick` | `Ingestion/Services`, `Core/Entities/Observations.cs` | Entirely | **Does not extend.** Flights have no on-sale moment worth ticking |
| `PriceObservation`, `FinalPrice`, store-on-change, monthly partitions | `Core/Entities/Observations.cs`, `Data` | `EventId` | **Extends as a pattern**, not as a table (§5) |
| `AllIn`, `FaceMin/FaceMax`, `PriceComparison` | `Core/Analytics/PriceComparison.cs` | `SourceKind` in the rule | **Extends** and matters more (§7) |
| `EventResolver` | `Ingestion/Services/EventResolver.cs` | Performer, venue, six-hour drift | **Does not extend.** A flight is resolved by IATA codes and a date, exactly |
| `Watch`, `Purchase`, `SavingsVsFinal` | `Core/Entities` | `EventId` | **Extends** with a subject |
| `Category`, `Performer`, `Venue`, `Event` | `Core/Entities/Reference.cs` | Entirely | **Does not extend.** Airports and routes are their own reference data |
| Adapters, fixtures, `source-fixture`, `source-drift` | `Ingestion/Adapters`, `.claude/skills` | Per source | **Extend as a discipline**: every fare adapter gets a fixture and a parse test |
| The desk: windows, `PriceCell`, `PullMenu`, follow-ups | `Desk`, `Web/Components` | Placeholders name ticket windows | **Extends**: the catalogue is app-owned by design (reuse plan §1.1) |
| `MarketOptions` (city, state, DMA, category) | `Ingestion/Configuration/IngestionOptions.cs` | One city, one category | **Needs a sibling**: origin airport and horizon |
| `CLAUDE.md` scope: Nashville, concerts, two markets | `CLAUDE.md` | Entirely | **Must be amended**, not silently outgrown (§8) |

Two-thirds of the ingestion layer and all of the operations and desk layers are about
watching a number within a budget. That is the platform. The entities, the resolver and
the on-sale record are the concert product. The seam runs between them, and today it is
implicit: `PricePollPlanner` reaches into `db.Watches` and `Event.OnSaleAt` directly, and
`Alert` carries an `EventId`. Those three touches are the whole cost of making the seam
explicit.

---

## 2. What an air fare is, mapped onto the ticket domain

| Ticket domain | Air travel | Where it differs |
|---|---|---|
| `Venue` (a room, with a ticketing provider) | `Airport` (IATA code, city, timezone) | No provider: every airline sells at every airport it serves |
| `Performer` | `Airline` (IATA carrier code) | Optional on a watch: "any carrier" is the common case |
| `Event` (one show, one night) | `Trip` (origin, destination, depart date, return date or null, cabin, passengers, checked bags) | A trip is a query the fan defines, not a thing a feed discovers; two fans watching BNA to LAX on the same date share one `Trip` row |
| `Event.OnSaleAt` | none | Schedules open about 330 days out and fares are loaded quietly; there is no announced moment |
| `Event.StartsAt` | `Trip.DepartsAt` | The clock the fare progression runs down to |
| `SourceKind.Primary` (box office) | Airline direct | The DOT 24-hour rule applies here and not to agents |
| `SourceKind.Resale` (marketplace asks) | none | Tickets cannot be resold; no second market |
| — | `SourceKind.Agency` (an OTA or metasearch: Aviasales, Kayak, Duffel's content) | The same seat at a different total; comparable to direct only all-in and bag-for-bag |
| `SourceKind.Feed` | Government survey (BTS OD40) | Historical, quarterly then monthly, no live price: the baseline, never a cell |
| `PriceObservation` (lowest, average, highest, listing count) | `FareObservation` (lowest, average, highest, offer count, carrier of the lowest, stops) | Carrier and stops are what a fan asks first |
| `AllIn` | `AllIn` plus `BagsIncluded` | A $59 fare with a $35 bag is not below an $89 fare with the bag in |
| `FaceMin/FaceMax` (face versus fee) | `BaseFare` versus total (taxes, carrier-imposed charges, first bag) | The gap is the product of §3.1 |
| `OnSaleTick` | none, or the bag-fee record (§3) | Different cadence, different shape |
| `FinalPrice` at event start | `FinalFare` at departure (the walk-up fare) | Same promotion rule, same pruning |
| `Purchase` and `SavingsVsFinal` | `Booking` and paid versus walk-up | Identical arithmetic, same all-in guard |
| `Watch` (event, target, notify on drop) | `FareWatch` (trip, target, notify on drop, max stops) | One more filter |
| Event resolver (name, venue, six hours) | Exact: `(origin, destination, date, cabin)` | No slow path, no drift window |
| `events:discover` | none, or a route list from the survey | Nothing to discover; a trip is entered or derived |

The one structural difference that matters: **a concert is discovered, a trip is declared.**
The ticket product finds Nashville events from a feed and asks the fan which to watch. The
flight product has no feed of "trips" and no on-sale time; the fan, or a watched concert
(§3.2), declares the route and the dates, and the poller starts from there. That removes
the discovery job and the resolver and adds nothing in their place.

---

## 3. The thing nobody else offers, for flights

The roadmap's rule was that the product is the record nobody keeps, published free, and
that the watchlist is what everyone already sells. The same test applied to flights:

Every competitor already does fare history and drop alerts. Google Flights tracks a route
by email, and in 2026 took its Price Guarantee out of pilot: book a flagged itinerary
through Google and the difference is refunded if the fare drops before departure, up to
$500 a year. Hopper sells predictions and a Price Freeze; Kayak sells a forecast and a
verdict; Going watches a home airport for deals. A Nashville fare chart is not a product.

Three things are not on that shelf.

### 3.1 The bag-fee record: what a seat actually cost, all-in, by route

The number on every fare comparison site is the fare with taxes. The number the fan pays
is the fare with taxes and a bag, and since Southwest ended free bags in May 2025 that is
true of every carrier at BNA, where Southwest holds about half the seats. The Department of
Transportation's 2024 rule that would have required bag and change fees to be shown beside
the fare was vacated by the Fifth Circuit on 2026-02-03; DOT restored the 2011 text on
2026-07-02; and on 2026-07-01 DOT proposed relaxing the full-fare rule itself, with full
repeal named as an alternative and comments closed on 2026-08-21. If the alternative is
taken, a fare advertised without its taxes becomes lawful again for the first time since
2012.

A per-route, per-day record of the lowest all-in fare **with one checked bag priced in**,
by carrier, kept permanently and linked to its source, is the flight analogue of the
on-sale record: evidence of what was shown, when, and what it left out. It is the column
the vacated rule wanted on the screen. `AllIn` and `BagsIncluded` on every row is the
whole mechanism; the record is the observation stream not pruned for those routes.

### 3.2 The trip all-in: the concert and the flight on one line

This is the one only TicketMiser can build, because it already holds the other half.
Nashville's marquee rooms sell to out-of-town fans; a Bridgestone on-sale is a national
event. For a watched concert, derive the `Trip` from the fan's home airport, the venue's
city and the event date, and put the ticket's best all-in beside the fare's best all-in:

> Cheapest resale is $X on SeatGeek; face value was $Y at on-sale and is sold out; the
> flight from DEN is $Z all-in with a bag, down from $W when the show went on sale.

Two markets never ranked against each other; two verticals summed on one row, each cell
linked to its source and flagged all-in. It needs one join and no new source beyond the
fare feed. It is also the only version of a flight feature that stays inside the
Nashville scope rule rather than diluting it.

### 3.3 The post-booking drop record

The DOT 24-hour rule refunds a direct booking in full for a day; after that, a fare drop
is the airline's gain unless the fan holds a Google-guaranteed itinerary. A `Booking` row
graded against the fare stream, the way `Purchase` is graded against `FinalPrice`, tells
a fan what they would have saved and on which day, and over a season tells the monthly
report how often BNA fares fall after the typical booking window. This is `Purchase` and
`SavingsVsFinal` with a different subject, and it is the reason to keep the stream rather
than only its final.

Build 3.1 and 3.2. Fold 3.3 into the booking entity because it is free.

---

## 4. The sources, verified 2026-09-12

The verification was against each provider's own pages where they could be read and
against trade coverage where they refused automated reads (noted in §11). The picture is
worse than it was for tickets: there is no free, terms-clean, live fare search for a small
product in September 2026.

| Source | Access | What you get | Cost | Terms and fit |
|---|---|---|---|---|
| **Amadeus Self-Service** | **Gone.** New registrations paused spring 2026; portal and keys decommissioned 2026-07-17. Enterprise portal unaffected, sales-led. | Was: Flight Offers Search, Price Analysis, Cheapest Date, on a free monthly quota then cents per call | — | The source every tutorial names. Do not plan on it. |
| **Travelpayouts / Aviasales Data API** | Free token on signing up as a Travelpayouts affiliate; the real-time Search API needs 50,000 monthly users | Cached prices from Aviasales users' searches: `prices/latest` (48 hours), `cheap`, `direct`, `calendar`, `month-matrix`, `week-matrix`, `nearest-places-matrix`; currency selectable; `expires_at` per offer | Free; commission 1.1 to 1.5 percent on bookings | The docs say "transferred from the cache, recommended for static pages". Whether prices include taxes is not stated; bags are not. Rate limit and attribution pages refused a fetch. **The first adapter, and the one whose fixture answers the open questions.** |
| **Duffel** | Self-serve, but live mode is for travel sellers; test mode is sandbox data | Real offers per search with base and total, ancillaries priced | $3 per order, then $0.005 per search beyond 1,500 searches per order | A tracker books nothing, so every search is a paid one. Only in play if the product ever becomes a seller. |
| **Kayak Affiliate API** (Flights Search, Price Insights) | Application, manual approval, account manager; sandbox while applying; small developers typically declined | Live search; "cheapest prices actually seen" for insights | Affiliate | Apply and record the answer. Their Price Insights is close to what §3.1 needs. |
| **Skyscanner Travel API** | Application for "established travel businesses with a large audience", two-week review | Live search, price grids | Affiliate | Apply, expect no. |
| **Kiwi.com Tequila** | Invitation-only B2B since May 2024 | Live search, virtual interlining | Partnership | Not reachable. |
| **Google Flights** | No API since QPX Express closed 2018-04-10; the "APIs" on offer are scrapers | — | — | **Out**, by the no-scraping rule. Google's own Price Guarantee is a competitor feature, not a source. |
| **BTS Origin and Destination Survey** (DB1B, now OD40) | Public download, no key | Ticket-level sample of fares by market and carrier: 10 percent quarterly through 2025, 40 percent monthly from August 2025 | Free | Historical only, months behind. The baseline that says what a BNA route usually costs, and the only fare source with no terms at all. **The `Feed` of the flight vertical.** |
| Sabre, Travelport | Enterprise contracts | Everything | Five figures | Not for this. |

What this means for the plan:

- **One live-ish source, and it is cached.** Aviasales's cache is other people's searches
  from the last two to seven days, so a quiet route from BNA may have no price at all on a
  given day. The observation model already handles this: no row is written when there is
  nothing new, and the cell says when it was last seen. The `month-matrix` call returns a
  month of dates for one route in one request, which makes the cadence cheap: a watchlist
  of fifty routes is fifty calls an hour, not fifty per date.
- **Bags are not in any source's number.** `BagsIncluded` will be computed, not observed:
  a carrier fee table in seed data, dated, with its own source row so a changed fee is a
  changed fixture. That is a small, honest table; it is also the one that makes §3.1 true.
- **The survey is the record's spine the way the Inventory Status API was.** It costs
  nothing, has no terms, and answers "is this fare high for this route" from four decades
  of tickets. Load BNA markets only, the way the Discovery Feed is filtered to Tennessee.
- **Send the applications the day the seam is merged**: Kayak, Skyscanner, Duffel's
  commercial team. Each is an email; each answer, including no, goes in §10.

---

## 5. Design: the seam

The rule from §1 is that the platform extends and the domain does not. Make that a
project boundary rather than a habit.

### 5.1 Two verticals in one Core, not two Cores

Keep one `TicketMiser.Core` and add a `Vertical` enum with two values, `Tickets` and
`Flights`, carried on `Category` and on `Source`. A second assembly would force every
shared type through a third one and buy nothing until a third vertical exists. What
changes:

- **`SourceKind`** gains `Agency` for an OTA or metasearch that sells the same seat the
  airline does. `PriceComparison.Comparable` stays exactly as written: same kind, same
  all-in, never feed. For flights a second guard is added beside it, `BagsIncluded` equal,
  because two all-in fares with different bag assumptions are the airline version of a
  face-value range against a resale ask.
- **`Subject`**: the thing a watch, an observation, an alert and a purchase are about.
  Not a base class and not a polymorphic table. `Alert` and the desk's follow-ups get a
  `SubjectKind` (`Event` or `Trip`) beside a `SubjectId`, and each vertical keeps its own
  typed tables. EF and Postgres partitioning both prefer it, and the ticket tables are not
  migrated at all.
- **`Trip`, `Airport`, `Airline`, `FareObservation`, `FinalFare`, `FareWatch`, `Booking`**
  as new entities under `Core/Entities/Flights/`. `FareObservation` and `FinalFare` follow
  `PriceObservation` and `FinalPrice` column for column plus `Carrier`, `Stops`,
  `BaseFare`, `BagsIncluded`. `FareObservation` is partitioned by month on `ObservedAt`
  with the same function `DatabaseInitializer` already runs, and is never pruned for a
  trip whose route is on the record list (§3.1), which is a flag on `Trip`.
- **`BagFee`**: carrier, cabin, bag number, amount, currency, effective from, source id.
  The seed carries the BNA carriers' published fees with the date read; the Ops window
  shows the date so a stale table is visible.

### 5.2 The contracts

`IFareSource` mirrors `IPriceSource` with the discovery method removed and a trip in
place of an event ref:

```csharp
Task<FareFetchResult> FetchFaresAsync(IReadOnlyList<TripRef> trips, CancellationToken ct);
```

`FareFetchResult` carries `CanonicalFareObservation` rows and a `FetchCost`, and the
adapter states `AllIn` and `BagsIncluded` for its own numbers, never inferred downstream,
which is the existing rule restated. `SourceRegistry` lists both contracts; the
reconciler's `RegisteredKeys` already unions whatever the registry says.

### 5.3 The planner's seam

`PricePollPlanner` today counts `db.Watches` and reserves for `OnSaleAt` inline. Replace
those two reads with an `IWatchLoad` per vertical:

```csharp
record WatchLoad(int CallsPerSweep, int ReservedCalls);
Task<WatchLoad> LoadAsync(Source source, DateTimeOffset now, CancellationToken ct);
```

The ticket implementation returns what the two queries return today; the flight one
returns route count (one `month-matrix` call per route per sweep) and zero reserve. The
planner sums loads per source and the tightest provider still governs. `cadence-check`
prints one plan with a line per vertical. This is the only change inside the scheduler
and it is the reason the phase gate is `cadence-check`.

### 5.4 Jobs

| Job | Source | Cadence | What it does |
|---|---|---|---|
| `fares:poll` | Aviasales Data API | From the planner, one call per watched route | Month matrix per route, store on change, carrier and stops of the lowest |
| `fares:baseline` | BTS OD40 | Monthly, when a new file appears | BNA markets only: median and quartile fare per route per month, into `FareBaseline` |
| `fares:finalise` | — | At departure plus ten minutes | Promote `FinalFare`, grade `Booking`, prune the stream except record routes |
| `fares:bags` | Seed | On start, like categories | Reconcile `BagFee` from configuration and log any change |

No discovery job. A `Trip` is created by the fan from the desk, or by the trip all-in
feature from a watched event and a configured home airport.

### 5.5 Configuration

`IngestionOptions` gains `Flights` beside `Market`: `HomeAirport` ("BNA"), `Horizon`
(how far ahead a derived trip may be), `RecordRoutes` (the routes kept forever), and
the `Aviasales` source options with token and user agent. `PricePollingMode` applies to
both verticals; `Manual` stays the default.

### 5.6 The desk

Three windows, on the catalogue's existing pattern, gated by `apple-mudblazor`:

| Group | Window | What it shows |
|---|---|---|
| Data | **Trips** | Watched routes: best all-in with a bag per source kind, direct and agency badged, never merged; last seen; carrier of the lowest |
| Data | **Fare history** | The stream for one trip, departure marked, bookings marked, the survey median as a band |
| Destinations | **Trip** | Opened by following a route; first tab is the bag-fee record when the route is one |

And one addition to an existing window: the Event window gains a **Getting there** tab
when a home airport is configured, which is §3.2. `PriceCell` already renders a source
link and an all-in flag; it gains a bag glyph.

---

## 6. Cost and cadence

Aviasales publishes no limit; seed the source row with a ceiling of our own, as SeatGeek
was, at 600 an hour. Fifty watched routes at one `month-matrix` call each is fifty calls a
sweep; the planner's arithmetic then allows a sweep every six minutes against that
ceiling, which is far faster than a two-day cache warrants. Clamp the flight vertical's
`MinimumInterval` at one hour. Fifty routes hourly is 1,200 calls a day for the whole
vertical, on a free token, with a 20 percent reserve untouched.

The survey is a few hundred megabytes a quarter, filtered on the way in to markets that
touch BNA, under the same disk cap the Discovery Feed uses. Nothing else costs money until
an application is granted, and a granted Kayak or Skyscanner key is an affiliate account,
not a bill.

---

## 7. Law, and what it does to the cell

- **The full-fare rule (14 CFR 399.84) is in force today**: the first price shown must
  include taxes and mandatory carrier charges. So every airline and agent number is all-in
  in the ticket sense, and `AllIn` will be true on almost every fare row. Keep the column
  anyway: the 2026-07-01 proposal would let components be shown as large as the total,
  and the repeal alternative would let the base fare stand alone. The column is the audit.
- **Ancillary fees are not in that number.** The 2024 rule that would have put bag and
  change fees beside the fare was vacated on 2026-02-03. `BagsIncluded` is the product's
  answer, and the seed's fee table must carry its source and date.
- **The 24-hour rule** applies to bookings made directly with the airline at least seven
  days out, not to agents. `Booking` records the source kind so the grading can say
  whether a drop inside the first day was refundable.
- **Terms**: Travelpayouts is an affiliate programme, so the cell links must carry its
  marker and the page must say so (legal guidelines rules 3 and 7), as the Impact links
  will for tickets. The survey has no terms. No scraping (rule 1), which rules out every
  "Google Flights API" on offer; the Aviasales Data API passes that rule because its
  cache is Aviasales's own users' searches on Aviasales's own site, not a page read on
  our behalf. Rule 4, summaries never content, is the open question in §10: our history
  table holds the lowest fare per route per day, never an offer, and whether that is
  within Travelpayouts's terms is read from the agreement, not assumed. Each fare source
  gets a row in the guidelines' per-source table before its adapter merges.

---

## 8. Decisions and risks

- **Do the seam before the vertical.** The three touches in §5.3 and the subject key on
  `Alert` are a day's work with the existing tests as the net, and they make the ticket
  code honest about its own boundary whether or not flights ever ship.
- **A cached source is a weaker signal than the ticket sources.** Say so on the cell:
  "last seen 14 h ago on Aviasales". The observation model already tolerates gaps; the
  desk must not hide them.
- **Bags are computed, not observed.** That is a claim the product makes on its own
  authority. Show the fee table's date, keep the base fare beside the total, and let a
  fixture from a live source (Duffel's sandbox prices ancillaries) check the table.
- **Scope drift is the largest risk.** The roadmap held that a second city comes only
  after a report has been read by someone else. A second vertical is a larger step than
  a second city. The trip all-in (§3.2) is the justification because it serves the
  Nashville fan; a general fare tracker does not. Amend `CLAUDE.md` when the phase is
  approved, with these lines:
  - "Two verticals, tickets and flights; one city. A `Trip` exists only from or to BNA
    until a second airport has its own routes, fee table and survey filter in seed data."
  - "`AllIn` and `BagsIncluded` are columns on every fare row."
- **Amadeus is gone and the others are gated.** If Travelpayouts's fixture shows prices
  without taxes, or its terms forbid a history table, the vertical waits for an
  application to be granted. That is a recorded answer, not a failure.

---

## 9. Proposed phases for `phase-plan`

Phases 4 to 7 stay as written. Two are added after them, so that the customer-facing
ticket work is not displaced.

**Phase 8 — The seam.** Goal: the platform no longer knows it is about concerts.
Gate: `cadence-check` before merge; `dotnet test` green with no ticket test changed
except for a rename.

- [ ] `Vertical` on `Category` and `Source`; `SourceKind.Agency`; a migration that sets
      every existing row to `Tickets`. `PriceComparisonTests` gains the bag guard.
- [ ] `SubjectKind` and `SubjectId` on `Alert`, with `EventId` kept as a computed
      convenience; the three watch rules and the runbook read the subject.
- [ ] `PricePollPlannerTests` written first, on the fake clock the cadence tests already
      use, pinning today's plan for the seeded watchlist (interval, calls per sweep,
      on-sale reserve, binding source). The planner has no unit test today; the seam is
      not cut until it has one.
- [ ] `IWatchLoad` with the ticket implementation extracted from `PricePollPlanner`;
      `PricePollPlannerTests` pass unchanged against it.
- [ ] `IngestionJobs.DescribeAsync` lists jobs from every registered vertical; the
      `PullMenu` renders them grouped.
- [ ] Applications sent to Kayak, Skyscanner and Duffel; answers recorded in §10 here.

Exit: `cadence-check` prints the same plan for the ticket watchlist it printed before the
change, with a `Vertical` column; `dotnet test` green.

**Phase 9 — The flight vertical.** Goal: a watched route from BNA has a fare history, a
bag-inclusive all-in, and a survey baseline, and a watched concert shows the trip all-in.
Gates: `source-fixture` for the Aviasales and OD40 adapters; `cadence-check` for the
combined plan; `apple-mudblazor` on the three windows.

- [ ] Fixtures: one real `month-matrix` response for BNA to LAX and one OD40 monthly
      extract filtered to BNA, committed with `.meta.json` that records whether prices
      include taxes and the cache age seen. Parse tests in the same commit.
- [ ] Entities and migration: `Airport`, `Airline`, `Trip`, `FareWatch`,
      `FareObservation` (partitioned), `FinalFare`, `Booking`, `BagFee`, `FareBaseline`;
      seed BNA, its carriers and their dated bag fees.
- [ ] `IFareSource`; `AviasalesAdapter` and `BtsSurveyAdapter`; `FareIngestionService`
      with store-on-change and the bag computation; `fares:poll`, `fares:baseline`,
      `fares:finalise`, `fares:bags` on the scheduler under `Manual`.
- [ ] `FareWatchLoad` and the one-hour floor; `cadence-check` prints both verticals.
- [ ] Windows: Trips, Fare history, Trip destination; the Event window's Getting there
      tab; render tests in the same commit.
- [ ] Alerts: `fare_drop` and `fare_target_reached` reuse the ticket rules through the
      subject key; `INotifier` from Phase 6 delivers them.
- [ ] `docs/legal-guidelines.md` gains a row per fare source (Aviasales, BTS OD40, and
      each applied-to API with its answer) before the adapter merges; question 3 in §10
      is answered there with the date and the wording.
- [ ] `CLAUDE.md` scope amended as §8 says, in the same commit as the seed.

Exit: `dotnet test --filter "Adapters|Flights"` green on committed fixtures; open a
watched Bridgestone event with `HomeAirport` set and read the ticket's best all-in beside
the fare's best all-in with a bag, each cell linked to its source; the Trips window shows
"last seen" on every row; `cadence-check` shows both verticals inside budget.

---

## 10. Open questions to settle by fixture, not by reading

1. Do Aviasales Data API prices include taxes and carrier charges? The docs do not say.
   The fixture's `price` against the same itinerary on the airline's site on the same
   day answers it, and `AllIn` on the adapter is false until it does.
2. What is the cache age on a BNA route on a weekday? `expires_at` and the `found_at`
   field in the fixture answer it, and decide whether hourly polling is worth anything.
3. Does Travelpayouts's agreement permit a history table of our own summaries? The
   requirements page refused an automated fetch; read it by hand and file the answer.
4. Does the OD40 monthly file carry enough BNA tickets per route to give a quartile, or
   only a median? The first extract answers it.
5. Kayak, Skyscanner, Duffel: sent on ______, answered on ______ with ______.

---

## 11. Sources

Repository: `src/TicketMiser.Core`, `src/TicketMiser.Ingestion/Services/PricePollPlanner.cs`,
`src/TicketMiser.Ingestion/Configuration/IngestionOptions.cs`,
`src/TicketMiser.Data/DatabaseInitializer.cs`, `PHASES.md`, and the three specs named at
the top.

External, read 2026-09-12; pages marked † refused an automated fetch and rest on trade
coverage or a documentation mirror:

- Amadeus shutdown: [PhocusWire](https://www.phocuswire.com/amadeus-shut-down-self-service-apis-portal-developers); [Amadeus for Developers](https://developers.amadeus.com/self-service) †; [a migration note naming the 2026-07-17 date](https://ignav.com/docs/amadeus-self-service-shutdown).
- Travelpayouts / Aviasales: [Data API reference mirror](https://travelpayouts-data-api.readthedocs.io/); [help centre article](https://support.travelpayouts.com/hc/en-us/articles/203956163-Aviasales-Data-API) †; [access requirements](https://support.travelpayouts.com/hc/en-us/articles/203956083-Requirements-for-Aviasales-data-API-access) †.
- Duffel: [pricing summary](https://www.xpay.sh/saas-pricing/duffel/); [comparison with a data API](https://flightpowers.com/compare/duffel).
- Kayak: [Flights Search API](https://affiliates.kayak.com/apis/flights); [travel data](https://affiliates.kayak.com/apis/travel-data); [KAYAK for Developers](https://developers.kayak.com/).
- Skyscanner: [apply for the Flights API](https://www.partners.skyscanner.net/product/travel-api); [affiliate programme](https://www.partners.skyscanner.net/product/affiliates).
- Kiwi.com: [Tequila access in 2026](https://phptravels.com/blog/comprehensive-guide-to-flights-api-integration); [Kiwi.com on its partnership approach](https://media.kiwi.com/articles-and-interviews/better-for-business-kiwi-com-takes-a-new-approach-to-partnerships/).
- Google Flights: [no official API, QPX retired 2018](https://softwaretimes.blog/google-flights-developer-api-guide/); [Price guarantee help page](https://support.google.com/travel/answer/9430556); [coverage of the 2026 rollout](https://thriftytraveler.com/guides/google-flights/price-guarantee/).
- Competitors: [price alerts compared, 2026](https://truescho.com/en/blog/flight-price-alerts-2026-skyscanner-google-flights-kayak); [Hopper versus Google Flights](https://globalskyzone.com/google-flights-vs-hopper/); [Going](https://upgradedpoints.com/travel/best-websites-for-flight-deal-alerts/).
- Survey data: [BTS Origin and Destination Survey](https://www.bts.gov/topics/airlines-and-airports/origin-and-destination-survey-data); [OD40 launch](https://www.bts.gov/newsroom/first-quarter-od40-data-live); [OD40 user meeting, August 2025](https://www.bts.gov/sites/bts.dot.gov/files/2026-03/BTS_OD40-User-Meeting-2025-08_Presentation.pdf).
- Law: [Fifth Circuit vacates the ancillary fee rule](https://news.bloomberglaw.com/litigation/biden-era-airline-fee-disclosure-rule-nixed-by-fifth-circuit); [DOT restores the 2011 text, effective 2026-07-02](https://www.federalregister.gov/documents/2026/07/02/2026-13450/increasing-flexibility-on-disclosure-of-airline-ancillary-fees); [NPRM on air fare price advertising, 2026-07-01](https://www.federalregister.gov/documents/2026/07/01/2026-13294/enhancing-flexibility-of-air-fare-price-advertising) †; [Hunton on the proposal and the repeal alternative](https://www.hunton.com/the-nickel-report/dot-proposes-to-revamp-its-air-fare-advertising-rules); [comment deadline extended to 2026-08-21](https://deeparrival.com/news/dot-air-fare-price-advertising-nprm-comment-august-21-2026/); [DOT on refunds and the 24-hour rule](https://www.transportation.gov/individuals/aviation-consumer-protection/refunds).
- Nashville: [Southwest's share at BNA](https://www.nashvillepost.com/business/bna-now-ranks-among-10-largest-for-southwest-operations/article_6a30edfe-75da-41c7-bd41-21e6ca16d905.html); [Southwest bag fees](https://www.yahoo.com/news/articles/southwest-airlines-moving-forward-more-180538845.html).
