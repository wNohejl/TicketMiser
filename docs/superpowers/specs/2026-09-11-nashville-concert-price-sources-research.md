# Pulling Nashville concert prices from the moment they go on sale

**Date:** 2026-09-11
**Status:** Research, feeds Phase 2 of the [desk reuse plan](2026-09-11-ticket-tracker-desk-reuse-plan.md)
**Audience:** the developer building the ticket domain

The mission: know a concert's ticket price the moment it goes on sale, keep every move it makes
after that, and tell a fan which site to buy from today. Nashville first, because the venue
mix there is unusually split between ticketing companies, which is exactly the situation a
comparison tool exists for.

The short version: **the on-sale moment is a Ticketmaster and AXS problem, the price
progression is a SeatGeek and Ticketmaster problem, and the honest "buy here" answer needs
all-in prices, which only one free source gives you.** Everything below is what each source
actually returns, verified against its documentation on the date above, and what the
ingestion layer should do with it.

---

## 1. Who tickets Nashville

The plan assumed Nashville was a Ticketmaster town. It is not. The venues split three ways,
and the third way has no API at all.

| Venue | Primary ticketing | Discoverable through |
|---|---|---|
| Bridgestone Arena, Nissan Stadium, Ascend Amphitheater, FirstBank Amphitheater, Brooklyn Bowl Nashville, Marathon Music Works | **Ticketmaster** (Live Nation venues) | Discovery API, directly |
| Ryman Auditorium, Grand Ole Opry House | **AXS** — Opry Entertainment Group's exclusive partner since December 2021; the Ryman was Ticketmaster before that | No public API. SeatGeek/JamBase carry the listing. |
| The Basement, The Basement East, Eastside Bowl | **AXS** | Same |
| Exit/In, 3rd & Lindsley, The Caverns, most clubs | **Etix** and other small platforms | Etix has a partner API behind approval; its Event Discovery Network pushes events to Bandsintown, Songkick, JamBase |

Three consequences:

- **The Ryman is the biggest Nashville room we cannot read at source.** AXS's developer
  tools cover access control and redemption for venue operators, not discovery. Their
  partnerships team takes bespoke requests. For the Ryman and the Opry the on-sale price
  comes from a resale-side or aggregator source, and it should be badged as such.
- **Ticketmaster covers the arenas and amphitheaters**, which is where dynamic pricing and
  Platinum do their damage, so it is where release-moment tracking matters most.
- **A "Nashville" query must be geographic, not by provider.** Every source below supports
  a city, a state or a lat/long with radius. The tracker's discovery job should run one of
  each and union the results through the entity resolver.

---

## 2. The sources, verified

### 2.1 Ticketmaster Discovery API v2 — the release-moment source

Free key from developer.ticketmaster.com. **5,000 calls per day, 5 per second.** Deep paging
stops at item 1,000 (`size × page < 1000`), so a Nashville sweep must be sliced by date or
classification rather than paged to the end.

What matters for this product, by parameter name:

- **Geography:** `city`, `stateCode`, `dmaId`, `geoPoint` + `radius` + `unit`, `postalCode`.
  Use `city=Nashville&stateCode=TN` for the discovery sweep and `geoPoint` with a radius for
  the metro. The DMA id is not listed in the public docs; a venue lookup returns the venue's
  `dmas` array, so read it from Bridgestone Arena once and store it.
- **Classification:** `classificationName=music`, or `segmentId` for the music segment.
- **On-sale filters, the whole point:** `onsaleStartDateTime`, `onsaleEndDateTime`,
  `onsaleOnStartDate`, `onsaleOnAfterStartDate`. Sort by `onSaleStartDate,asc`.
- **Response fields the tracker reads:** `sales.public.startDateTime` and `endDateTime`
  (with `startTBD`), `sales.presales[]` (name, start, end, URL), `dates.status.code`
  (`onsale`, `offsale`, `canceled`, `postponed`, `rescheduled`), `priceRanges[]` (type,
  currency, `min`, `max`), `ticketLimit`, `allInclusivePricing` (boolean, US only).
- **Price ranges are refreshed at most once per hour** on Ticketmaster's side. Polling
  faster than hourly buys nothing for price; it does catch status flips.
- **The `max` is capped** and the plan already records `listingsExtendBeyondMax`.

**The all-in problem.** `allInclusivePricing` tells you whether the event's displayed price
includes the fees Ticketmaster programs on it. When it is true the total excludes the order
processing fee; when it is false, `priceRanges` is face value and the checkout price is
24 to 44 percent higher (the FTC's own figure from its September 2025 complaint). The
tracker must store the flag beside every observation and must never compare a face-value
range to another site's all-in number.

**The companion: Inventory Status API.** Separate key, `inventory-status/v1/availability`,
around 350 event ids per call, near real time. Returns `TICKETS_AVAILABLE`,
`FEW_TICKETS_LEFT`, `TICKETS_NOT_AVAILABLE`, `UNKNOWN` plus a `resaleStatus`. This is the
cheap way to watch a sale sell through without spending Discovery quota: one call covers
the whole Nashville watchlist.

**The other companion: the Discovery Feed.** A daily bulk file per country, gzipped CSV,
JSON or XML, open to the public on the same developer key. It carries event ids, names,
venues, attractions, classifications, on-sale dates, presale windows and seller details.
The price columns are always null since March 2025, and nothing in the documentation says
the download counts against the 5,000-call quota. That changes the budget: discovery and
on-sale scheduling come from the feed, and the daily quota is spent only on price
observations and on-sale watches. Filter the file to Tennessee venues on the way in and
store only Nashville; the feed is national.

**Terms.** Cache only "for reasonable periods in order to provide the service"; do not
"derive revenues from the use or provision of the Ticketmaster API"; Ticketmaster may
rate-limit apps whose calls are "not primarily in response to direct user actions". A
scheduled poller is exactly that. Keep the cadence modest, keep every price cell linked to
the Ticketmaster event page, and expect to move to a partner agreement if the product is
ever monetised.

### 2.2 SeatGeek Platform API — the progression source

Free `client_id`. No published rate limit. `/events` filters on `venue.city`, `venue.state`,
`lat`/`lon`/`range`, `datetime_utc.gte/lte`, `performers.slug`, `taxonomies.name`
(`concert`), `listing_count.gt=0`; `per_page` and `page`; `sort`. Every event carries a
`stats` block: `lowest_price`, `average_price`, `highest_price`, `listing_count`. Also
`announce_date` and `visible_until`.

What SeatGeek is and is not:

- It is a **resale aggregate plus primary where SeatGeek is the primary**. For an AXS or
  Etix show it is the only free number we can get, and it is a resale number.
- It gives **no on-sale date**. It cannot tell you a sale is about to start. Pair it with
  Ticketmaster's `sales.public` or with JamBase's on-sale signal.
- **Whether `stats` prices include fees is not documented.** Treat them as not all-in until
  a saved fixture proves otherwise, and say so on the cell.
- Terms: display the SeatGeek logo where its data appears, link it to seatgeek.com; do not
  "copy, store or cache SeatGeek Content, other than for the intermediate purposes allowed";
  do not display listings on behalf of other sellers. A history table of our own
  observations is the grey area every price tracker lives in. Store the summary numbers, not
  their content; attribute every cell; keep the raw responses out of long-term storage.

### 2.3 JamBase Data API — the missing on-sale signal for non-Ticketmaster rooms

Paid, with a 14-day trial. Covers 25+ ticketing sources including Ticketmaster, AXS,
Eventbrite, StubHub, SeatGeek and Vivid Seats. Returns direct ticket URLs from the source
platform, face-value pricing where the source publishes it (Startup plan and above),
on-tour and on-sale signals, event status, and **historical pricing on Pro+**. Fed by Etix's
Event Discovery Network, so the clubs are in it.

This is the one source that sees AXS on-sale dates. Its cost is the question; the trial
answers whether the Nashville coverage is real before a dollar is spent.

### 2.4 Bandsintown Public API v3 — artist-only

Requires an `app_id` granted by Bandsintown after you tell them what you are building.
Endpoints are **by artist only**; there is no city or location search in v3. Events carry
ticket links and an on-sale datetime. Useful for a Performer watch, useless for a Nashville
sweep. Songkick's metro-area API would have been the right shape and is closed to new keys.

### 2.5 The rest

- **StubHub:** by application to affiliates@stubhub.com, as the plan says. Phase 2.
- **AXS, Etix, Vivid Seats, TickPick, Gametime:** no public discovery API. Etix has a
  partner API behind approval and partner terms; worth an email for the clubs.
- **Gametime specifically:** an affiliate program with a 30-day cookie and nothing for
  developers. Its one virtue for us is that it shows all-in prices by default, which makes
  it the honesty check for the other sources' fee handling. Join the affiliate program for
  the purchase link; buy its data through an aggregator only for spot checks; keep it out
  of the scheduled poll.
- **Aggregators that scrape:** TicketsData ($499/month, 10 marketplaces, 5 req/s, section
  level, no history) and Tickets.dev ($0.05 per capture, 9 marketplaces, all-in prices with
  fee broken out, uses "real browsers on residential IPs"). Both are the only way to see
  Vivid Seats and TickPick programmatically, and both are scraping other people's sites on
  your behalf. Tickets.dev's all-in breakdown is the most honest number on the market and
  the cheapest way to spot-check the free sources' fee handling. Neither belongs in the
  scheduled poll until a lawyer has read their terms and the marketplaces'.

---

## 3. What the law changed this year, and why it helps

- **FTC junk-fee rule, in force 12 May 2025:** the all-in price must be shown from the first
  listing. Tennessee's own statute (§ 47-50-121) requires the same disclosure from resellers
  and resale sites, and since 1 July 2025 bans speculative tickets at $5,000 per violation.
- **FTC and seven states, Tennessee among them, sued Live Nation and Ticketmaster on 17
  September 2025** over deceptive pricing and BOTS Act failures. The DOJ settled mid-trial
  on 9 March 2026 with a 15 percent service-fee cap and an eight-year consent decree. The
  states carried on and **on 15 April 2026 a jury found Live Nation and Ticketmaster an
  illegal monopoly**, with damages of $1.72 per primary concert ticket.
- **The Senate PSI report of 16 March 2026** put numbers on dynamic pricing: dynamically
  priced North American concert tickets up more than 700 percent from 2019 to 2022, every
  one of the top 30 tours on Pricemaster by June 2022, and internal encouragement to raise
  prices during active sales.
- **Fees moved rather than fell.** Contracts at 26 public venues show Ticketmaster raising
  per-ticket service charges after dropping the order-processing fee.

For the product this means three things. The all-in price is now the legally required
number, so it is the number the board compares. A price rising inside the first hours of a
sale is a documented practice, not an anomaly, so the release-moment observation is the
reference every later observation is judged against. And the fee itself is a signal worth
tracking: store face and all-in separately, and the gap is a column.

---

## 4. Design consequences for Phase 2

**The unit of observation stays the per-source summary**, as the plan says, with three
columns added: `AllIn` (bool), `FaceMin`/`FaceMax` when the source gives face value
separately, and `Market` (`Primary` or `Resale`) on the source. Ticketmaster observations
carry `allInclusivePricing`; SeatGeek observations carry `AllIn = false` until proven.

**Four jobs, not three:**

| Job | Source | Cadence | What it does |
|---|---|---|---|
| `events:discover` | Ticketmaster Discovery Feed (daily file, filtered to Tennessee) and SeatGeek (`venue.city`, `taxonomies.name=concert`) | Daily | Finds new Nashville events, resolves them, records `sales.public.startDateTime` and presales. Costs no Ticketmaster quota. |
| `onsale:watch` | Ticketmaster Discovery + Inventory Status, SeatGeek | Every 5 minutes from T minus 15 minutes to T plus 2 hours, then hourly for 24 hours | The on-sale record (§4.1). Status flip to `onsale`, first `priceRanges`, Inventory Status and SeatGeek `listing_count` every tick. This is what the mission is about. |
| `watchlist:prices` | All sources | From the cadence rule: allowance minus reserve, over sweeps, over hours left | The progression. Store on change only. |
| `prices:finalise` | All sources | At event start | One `FinalPrice` per event per source; prune the stream after. |

**The cadence sum for Nashville.** Ticketmaster at 5,000 per day with a 20 percent reserve
leaves 4,000 calls, and with discovery on the feed all of them go to prices. A 100-event
watchlist at one call per event is 100 per sweep, so 40 sweeps a day, one every 36 minutes,
which is faster than Ticketmaster refreshes prices anyway. On-sale watches are the expensive
part: 40 Discovery calls per event per release day, plus one Inventory Status call per tick
for the whole list. Budget them first and let the guard refuse the rest, which is the
LineOps rule.

### 4.1 The on-sale record — the thing nobody else offers

Resale price history is a crowded shelf: TicketData, Event Spy, SeatData, SeatHeat and
Historica all chart resale prices after the fact and alert on drops. None of them records
what happened on the primary market in the first hours of a sale, and that is the evidence
the Senate and the New York Attorney General had to subpoena. The Senate report found 20 of
29 Bad Bunny shows taking resale orders before the local on-sale had started; the New York
report says half the house is commonly held back and reappears after the "sellout". A fan
watching one show cannot see either. The HOLDBACKS Act introduced in July 2026 exists
because this data is not public.

The on-sale record is, per event, a timestamped ledger of the sale's first hours, kept
permanently and shown publicly:

- The face and all-in price the second the sale opened, and every change in the next two
  hours. The price at T is the number nobody keeps.
- The minute primary inventory went to `FEW_TICKETS_LEFT` and to `TICKETS_NOT_AVAILABLE`.
- SeatGeek `listing_count` and `lowest_price` in those same minutes, on the same axis.
- Every later primary re-release: `TICKETS_NOT_AVAILABLE` back to `TICKETS_AVAILABLE`.

It is built entirely from the `onsale:watch` job above: no new source, no scraping, no new
terms. What it becomes on the desk:

| Surface | What it shows |
|---|---|
| Event window, first tab: "Was it really sold out?" | Two lines over the first two hours, primary availability and resale listings, with the on-sale price pinned. |
| Alert `primary_reappeared` | The one alert no tracker sends: face-value tickets are back on the primary site. Joins `price_drop` and `target_reached` in the alert engine. |
| Export | The ledger as a dated receipt a fan can attach to a Tennessee Attorney General or FTC complaint. All-in disclosure is Tennessee law; the ledger is proof of what was shown when. |
| Monthly Nashville report | For every sale that month: minutes to primary sellout versus resale listings present at that minute, by venue. Built from summaries, not copied content. |

**Data it needs beyond the plan:** an `OnSaleTick` row per event per tick (time, source,
status, resale status, all-in flag, min, max, listing count) in its own partitioned table,
because it is written at a five-minute cadence and read as a time series, and because it
is never pruned. `PriceObservation` stays the store-on-change stream for everything after
the first day.

The price history chart the plan already calls for stays, but it is the second screen. The
on-sale record is the first.

**The resolver's fast path is the source's own id**, and the slow path is performer plus
venue plus a start inside six hours. Nashville needs one more rule: the Ryman and the
Opry House both belong to the same performer on the same night sometimes, so venue is
never optional in the slow path.

**Which site to buy from** is a per-event ranking of all-in `Lowest` across sources with the
same `Market`, never across markets, with the primary sale shown beside it as the
reference. The board says "cheapest resale is $X on SeatGeek; face value at Ticketmaster
was $Y at on-sale and is sold out". That sentence is the product.

---

## 5. Catching the on-sale moment, continuously

The mission's hard part is not the price fetch, it is knowing *when*. Researched
2026-09-11 against the feed and API documentation, and built into Phase 2 and 3 as follows.

### 5.1 Where the on-sale time comes from

| Source | What it gives | When |
|---|---|---|
| **Discovery Feed** | `onsaleStartDateTime`, `onsaleEndDateTime`, `apiOnsaleStartDateTime`, `presales[]` with start and end, `eventStatus`, `venueTimezone`, for every US event | Regenerated daily; one gzipped file per country; no quota cost |
| **Discovery API** | `sales.public.startDateTime`, `startTBD`, `sales.presales[]`, `dates.status.code`; filters `onsaleStartDateTime` / `onsaleOnAfterStartDate`; sort `onSaleStartDate,asc` | Live; costs quota |
| **SeatGeek** | `announce_date` only. No on-sale time. | Live |
| **AXS, Etix** | Nothing programmatic. JamBase's paid feed carries their on-sale signals. | |

So the calendar of on-sales is the feed's, refreshed daily, and the Discovery API is used
only to confirm a specific event's time inside the last day before it opens, when a
correction would still matter. A time the feed does not have yet (`startTBD`) is polled
again by discovery daily until it appears.

### 5.2 The conventions, and why they matter to the cadence

Public on-sales in the US overwhelmingly open at **10:00 local time**, most often on a
**Friday**, after a presale run that starts **Tuesday** and closes **Thursday night**.
Nashville is Central Time, so a Friday 10:00 CT on-sale is 15:00 UTC in the feed. Two
consequences:

- **On-sale days cluster.** A Friday may hold several Nashville on-sales at the same
  minute. The on-sale watch is therefore driven by the event's own window, not by a
  per-event timer, and the quota arithmetic in `cadence-check` takes the count of events
  on sale *today*, because that is the day the budget is under pressure.
- **Presales are where the price is set.** Platinum and dynamic pricing are live from the
  first presale, and the Senate report found resale activated before the public on-sale on
  20 of 29 shows. The public on-sale is the reference the record is anchored on, but the
  record starts at the first presale for events where the feed lists one: the tick job
  treats a presale start as a second anchor with the same five-minute window.

### 5.3 The mechanism, as built

1. **Discovery, daily.** The feed adapter streams the US file, keeps Tennessee, and the
   resolver records `OnSaleAt`, `OnSaleTbd` and the presale windows on each event. The
   SeatGeek sweep follows and resolves onto the same rows, so the resale id is known before
   the sale opens. Only a primary or feed source may set or move an on-sale time; a resale
   source fills a gap and never moves a value.
2. **The window is arithmetic over T.** `OnSaleWindow` computes marks at T minus 15 minutes,
   every five minutes to T plus two hours, then hourly for a day. A tick is owed when the
   current mark has no row yet. A host that restarts inside the window owes the current
   mark, not the missed ones, so a five-minute outage costs one tick and never a burst.
3. **The scheduler ticks every minute and asks.** `OnSaleWatchService` finds watched events
   whose window contains now and whose current mark is unrecorded, fetches every priced
   source for exactly those events, fetches Inventory Status for the whole batch in one
   call, and writes one `OnSaleTick` per event per source, with the primary availability on
   the primary row. Every mark is written whether or not anything changed.
4. **A watch is the ask.** Nothing is fetched for an event nobody watches. The progression
   sweep is manual by default; the on-sale watch runs unattended because the watch itself
   is the operator's instruction, and its cost is quoted in the pull menu.
5. **Clock.** Every service takes a `TimeProvider`. The cadence test walks a whole window in
   one-minute steps on a fake clock and asserts that exactly the planned marks land, for
   every source, and that the alert engine then reads the scripted sellout-and-reappearance
   out of the record.

### 5.4 What is still unknown, and where it is caught

- Whether the feed's field names match the documentation. The adapter reads them leniently
  and the first `source-fixture` against the real file is what settles it.
- Whether `priceRanges` appears before the public on-sale for a presale-only event. The
  first on-sale window recorded will show it; until then the tick stores a null price and
  the status code, which is itself the answer.
- Whether the Inventory Status API needs a partner-level key. Its documentation says a
  dedicated key on the same portal; if that is refused, the on-sale record still holds
  price and status from Discovery, and `PrimaryStatus` stays null and says so.

## 6. Open questions to settle by fixture, not by reading

1. Do SeatGeek `stats` prices include fees? Save one Nashville event's response and one
   checkout screen on the same minute.
2. What does Ticketmaster's `priceRanges` say at T minus 1 minute, T, and T plus 5 minutes
   for a Bridgestone on-sale with Platinum enabled? One saved sequence tells us whether the
   5-minute cadence is enough.
3. Does JamBase's trial expose AXS on-sale dates for the Ryman? If yes, price the plan.
4. What is Nashville's `dmaId`? Read it from a venue lookup and record it in seed data.

---

## Sources

- [Ticketmaster Discovery API v2](https://developer.ticketmaster.com/products-and-docs/apis/discovery-api/v2/) — parameters, response fields, quota, paging cap.
- [Ticketmaster Inventory Status API](https://developer.ticketmaster.com/products-and-docs/apis/inventory-status/) — availability statuses, hourly price refresh.
- [Ticketmaster Discovery Feed](https://developer.ticketmaster.com/products-and-docs/apis/discovery-feed/) — daily bulk file, on-sale and presale fields, prices null.
- [Gametime affiliate program](https://affi.io/m/gametime).
- Existing resale trackers: [TicketData](https://www.ticketdata.com/), [Event Spy](https://www.event-spy.com/), [SeatData](https://seatdata.io/), [SeatHeat](https://seatheat.app/concerts), [Historica](https://www.digitalmusicnews.com/2025/10/21/historica-live-ticket-pricing-tracking/).
- [NY Senate report on holdbacks](https://www.nysenate.gov/sites/default/files/article/attachment/nys_senate_igo_committee_report_-_live_event_ticketing_practices.pdf); [HOLDBACKS Act](https://www.ticketnews.com/2026/07/pous-holdbacks-act-targets-deceptive-hidden-ticket-inventory-manufactured-scarcity/); [Senate report on early resale activation](https://www.ticketnews.com/2026/03/senate-report-says-ticketmasters-own-records-undercut-its-blame-of-bad-actors-for-high-prices-onsale-chaos/).
- [Ticketmaster Partner / Availability API](https://developer.ticketmaster.com/products-and-docs/apis/partner/availability/) — `allInclusivePricing`, partner-only access.
- [Ticketmaster API terms of use](https://developer.ticketmaster.com/support/terms-of-use/) — caching, revenue, rate-limit clauses.
- [SeatGeek Platform API](https://seatgeek.github.io/) — `/events` filters and `stats`.
- [SeatGeek Platform terms](https://seatgeek.com/api-terms) — attribution, caching, listing restrictions.
- [JamBase Data](https://data.jambase.com/data/) — coverage, face-value and historical pricing tiers.
- [Bandsintown API documentation](http://help.artists.bandsintown.com/en/articles/9186477-api-documentation) — artist-only endpoints.
- [Songkick developer](https://www.songkick.com/developer) — closed to new keys.
- [Etix Event Discovery Network](https://hello.etix.com/etix-event-discovery-network/) and [Etix API partner terms](https://www.etix.com/ticket/online3/apiPartnerTerms.jsp).
- [AXS to ticket all Opry Entertainment Group venues](https://www.billboard.com/pro/axs-ticketing-opry-entertainment-group-venues/); [Basement East on AXS](https://www.axs.com/venues/126344/the-basement-east-nashville-tickets); [Eastside Bowl on AXS](https://www.axs.com/venues/128318/eastside-bowl-madison-tickets); [Brooklyn Bowl Nashville FAQ](https://www.brooklynbowl.com/nashville/nashville-faqs); [Marathon Music Works on Ticketmaster](https://www.ticketmaster.com/marathon-music-works-tickets-nashville/venue/222501).
- [TicketsData](https://ticketsdata.com/) and [Tickets.dev](https://tickets.dev/apis) — scraping aggregators, pricing and sourcing.
- [FTC press release, 17 September 2025](https://www.ftc.gov/news-events/news/press-releases/2025/09/ftc-sues-live-nation-ticketmaster-engaging-illegal-ticket-resale-tactics-deceiving-artists-consumers).
- [Jury verdict, 15 April 2026 (CNN)](https://www.cnn.com/2026/04/15/politics/ticketmaster-live-nation-monopoly-verdict); [Paul, Weiss verdict memo](https://www.paulweiss.com/insights/client-memos/live-nationticketmaster-antitrust-verdict-key-takeaways-from-the-states-jury-trial-win).
- [Senate PSI minority staff report, 16 March 2026](https://www.hsgac.senate.gov/subcommittees/investigations/library/files/2026-03-16-minority-staff-report-so-casually-cruel-how-ticketmasters-monopoly-supercharges-prices-and-fees/); [TicketNews summary](https://www.ticketnews.com/2026/03/platinum-dynamic-and-pricemaster-senate-report-shines-a-light-on-ticketmasters-price-surging-playbook/).
- [Fees raised at 26 venues (Artvoice)](https://artvoice.com/2026/04/02/ticketmaster-got-caught-raising-fees-at-26-venues-after-the-government-banned-hidden-charges/).
- [Tennessee Code § 47-50-121](https://law.justia.com/codes/tennessee/title-47/chapter-50/section-47-50-121/); [TN SB0917 speculative tickets](https://www.billtrack50.com/billdetail/1819697).
- [FTC junk fees rule and ticketing (JCA)](https://www.jcainc.com/understanding-the-ftcs-junk-fees-rule-implications-for-live-event-ticketing/).
