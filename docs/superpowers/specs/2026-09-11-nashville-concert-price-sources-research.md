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
| `events:discover` | Ticketmaster (`city`+`stateCode`, `classificationName=music`, sort by on-sale) and SeatGeek (`venue.city`, `taxonomies.name=concert`) | Daily | Finds new Nashville events, resolves them, records `sales.public.startDateTime` and presales. |
| `onsale:watch` | Ticketmaster | Every 5 minutes from T minus 15 minutes to T plus 2 hours, then hourly for 24 hours | The release-moment observation. Status flip to `onsale`, first `priceRanges`, Inventory Status every call. This is the number the mission is about. |
| `watchlist:prices` | All sources | From the cadence rule: allowance minus reserve, over sweeps, over hours left | The progression. Store on change only. |
| `prices:finalise` | All sources | At event start | One `FinalPrice` per event per source; prune the stream after. |

**The cadence sum for Nashville.** Ticketmaster at 5,000 per day with a 20 percent reserve
leaves 4,000 calls. A discovery sweep of Nashville music is roughly 10 pages. A 100-event
watchlist at one call per event is 100 per sweep, so 39 sweeps a day, one every 37 minutes,
which is faster than Ticketmaster refreshes prices anyway. On-sale watches are the expensive
part: 40 calls per event per release day. Budget them first and let the guard refuse the
rest, which is the LineOps rule.

**The resolver's fast path is the source's own id**, and the slow path is performer plus
venue plus a start inside six hours. Nashville needs one more rule: the Ryman and the
Opry House both belong to the same performer on the same night sometimes, so venue is
never optional in the slow path.

**Which site to buy from** is a per-event ranking of all-in `Lowest` across sources with the
same `Market`, never across markets, with the primary sale shown beside it as the
reference. The board says "cheapest resale is $X on SeatGeek; face value at Ticketmaster
was $Y at on-sale and is sold out". That sentence is the product.

---

## 5. Open questions to settle by fixture, not by reading

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
