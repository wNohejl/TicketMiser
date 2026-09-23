# From a tracker to a product: what TicketMiser can offer a customer

**Date:** 2026-09-12
**Status:** Research and plan, written against `TicketMiser_Development` at `b1655b9`
**Audience:** the developer deciding what to build after Phase 3

The site today is an operator's desk with a good backend and no customer-facing surface:
five sources, four jobs, a partitioned observation store, an on-sale watch that ticks on a
fake clock, and eighteen windows of which one is real. This document asks what a customer
would pay attention to, what the repository can already give them, what stands in the way,
and in what order to build it. It ends with proposed phases for `phase-plan`.

The short version: **the product is the on-sale record, published, per event, for free.**
Everything else — the watchlist, the alerts, the report, the accounts — is either a way to
get people to the record or a way to keep them once they have seen it. The record costs
nothing new: it is `onsale:watch` output rendered on a public page.

---

## 1. Where the repository stands

Verified by `phase-status` on 2026-09-12: build clean; 270 desk tests and 34 unit tests
green; the 9 integration tests fail only because Docker Desktop was not running. The
working tree carries an uncommitted theme, accent and text-size change to the desk.

| Phase | Progress | Open |
|---|---|---|
| 0 Bootstrap | 4/5 | bunit 2.x migration |
| 1 Lift the desk | 4/4 | met 2026-09-11 |
| 2 Fixtures, domain, sources | 7/9 | a source that quotes Live Nation rooms; one real manual run |
| 3 Operations, scheduler, on-sale watch | 4/6 | the four ops windows; one real on-sale window end to end |
| 4 The tracker's own windows | 0/4 | everything a fan would touch |
| 5 Release and first report | 0/3 | release gate, public URL, first report |

Three facts from the research and the fixtures shape everything below:

1. **SeatGeek returns no prices to a fresh client id.** `stats` is empty under every auth
   shape tried. The resale side of the board is blind until the partner program answers.
2. **Ticketmaster does not quote the Live Nation rooms.** Of twenty priced events in the
   fixtures, eleven were TicketWeb clubs. Bridgestone and Ascend show status, not price,
   until a Platinum on-sale proves otherwise on 2026-09-18.
3. **The Ryman, the Opry House and the Basements are on AXS**, which has no public API.
   Five of the fourteen seeded venues, including the two most famous, have no primary
   signal at all.

So today the one thing the site can measure well, for every Ticketmaster room, is
**availability over time**: `TICKETS_AVAILABLE` to `FEW_TICKETS_LEFT` to
`TICKETS_NOT_AVAILABLE` and back, at five-minute marks, from one Inventory Status call per
tick for the whole watchlist. That is the on-sale record's spine, and it works without a
single price.

---

## 2. Who the customer is

Three people, in the order they will arrive.

**The fan deciding whether to buy.** Wants one sentence: *"cheapest resale is $X on
SeatGeek; face value at Ticketmaster was $Y at on-sale and is sold out."* Behind the
sentence, three questions no marketplace answers: was it really sold out, did face-value
tickets come back, and is the resale price falling or rising toward the show. The fan has
no account and will not make one to read a page.

**The watchdog.** A local journalist, a consumer advocate, a Tennessee Attorney General
staffer, an academic. Wants evidence: a timestamped, permanent, per-event ledger of what
the primary site showed in the first two hours, beside how many resale listings existed at
the same minute. The Senate and the New York AG had to subpoena this. A public record built
from official APIs, with every tick linked to its source, is a citation.

**The operator.** The developer, on two machines, who must keep the poller inside budget,
notice when a source's shape drifts, and refresh the snapshot. Already served by the
reliability layer and the ops windows; not the subject of this document.

The fan is the audience; the watchdog is the reason the record is kept forever; the
operator is the constraint on cost.

---

## 3. What is beneficial, ranked by evidence per dollar of quota

| Offer | What the customer gets | What it costs | Depends on |
|---|---|---|---|
| **A. Public on-sale record page per event** | Two lines over the first two hours: primary availability and resale listings, with every status flip timestamped. Permanent URL. | Nothing new: `OnSaleTick` rows already written | Phase 3 real run; one Razor page; no login |
| **B. Nashville on-sale calendar** | Every upcoming Nashville on-sale and presale, as a page and as an `.ics` feed a fan subscribes to once. | Zero quota: the Discovery Feed carries every on-sale and presale time daily | `events:discover` already records them |
| **C. "Face value is back" alert** | The one alert no marketplace sends: `primary_reappeared`, delivered to the fan, not to a desk they never open. | One email or push per event per reappearance | A delivery channel (§5) |
| **D. Monthly Nashville report** | Minutes to primary sellout versus resale listings at that minute, by venue, every month, free. | `monthly-report` skill exists; a page and a mailing list | One full month of records |
| **E. The watchlist board** | Best all-in per market, never across markets, spread rail, badges. | Discovery quota per sweep | SeatGeek partner prices before it is worth a fan's time |
| **F. Personal watches with target prices** | `target_reached` and `price_drop` for events the fan chose, at a price they set. | Accounts, an owner on `Watch`, delivery | C and a sign-in that is not a password |
| **G. Purchases and savings** | Paid versus final, the receipt as a dated export. | Nothing new in data | F |

A, B and D need no account, no new source and almost no quota. They are also the three
things nobody else offers. E is what every competitor already sells, and TicketMiser cannot
do it well until resale prices exist. Build in the order A, B, C, D, then E when SeatGeek
answers, then F and G.

---

## 4. What competitors offer, and where the gap is

Market research on 2026-09-12; verified where a source is cited in §9, inferred where not.

| Product | What a fan gets | Price | What it does not do |
|---|---|---|---|
| TicketData (2025) | Resale history across StubHub, Vivid, SeatGeek; fee-inclusive get-in price; a price forecast; a Chrome button; alerts with an account | Free to view | Nothing from the primary market: no face value, no availability, no on-sale record |
| SeatHeat | Free resale history for marquee tours; a buy-or-wait verdict; sells nothing | Free | Resale only, national, no primary evidence |
| SeatData.io | Per-transaction resale sales since 2021, an API at $0.005 a request | $49 to $129 a month | Built for brokers, not fans |
| TickPick | Price history on the event page, drop alerts, a 72-hour Price Freeze | Free marketplace | Its own listings only |
| SeatGeek, StubHub, Gametime, TicketIQ | In-app alerts and history for their own inventory; Gametime is all-in by default | Free | One marketplace each; no primary comparison |
| Ticketmaster | Favourites: new-event and low-availability alerts | Free | Nothing retrospective; never says when inventory vanished |

Every one of them is a resale price chart or a resale alert. None publishes a
minute-by-minute record of what the primary site showed beside how many resale listings
existed at the same minute, and none tells a fan that face-value tickets came back. The
evidence that does exist on that question came from subpoenas: the New York Attorney
General's 2016 report found more than half of top shows held back for insiders and
presales; the 2018 CBC and Toronto Star investigation into TradeDesk is the base of the
FTC's 2025 suit. Queue-it and Ticketmaster themselves argue that "sold out in seconds" is
mostly holds and presales. A public availability record, per event, settles that argument
without a subpoena.

What fans complain about, in the 2025 and 2026 coverage, maps onto the record directly:

- **"Sold out after the queue, resale already live."** The record shows the minute
  primary went to `TICKETS_NOT_AVAILABLE` and the listing count at that minute.
- **"Should I buy now or wait?"** Price history plus the reappearance alert. Partially
  addressed until resale prices exist.
- **Platinum and dynamic pricing.** Face-value drift over the first hours, where the
  Senate report found prices raised during active sales. Per-section detail is not in
  the API; the range is.
- **Drip pricing.** Largely closed by the FTC rule; the `AllIn` column becomes an audit of
  compliance rather than a defence against it.
- **Presale codes.** Not addressable from any API. Out of scope.

TicketMiser's weakness against this field is coverage: one city, and today no resale
prices. Its strength is that the one thing it records is the one thing nobody else does.

---

## 5. Gaps in the repository that block a customer

Found by reading the code, not the plan.

- **No delivery channel.** Alerts are rows in `Alerts` read by the desk. `price_drop`,
  `target_reached` and `primary_reappeared` fire into a table a fan never sees. A fan
  product needs email at minimum. Nothing in `src/` speaks SMTP, push or webhooks.
- **No public surface.** The Web host has one route, the desk, and two anonymous
  endpoints, `/health` and `/ready`. There is no read-only page, no per-event URL, no way
  to link to a record.
- **No owner on `Watch` or `Purchase`.** Single-user by design. Fine for A through D; a
  wall for F and G. The seam to add is an `OwnerId` column and a nullable owner on both
  entities, with the scheduler polling the union of all owners' watches.
- **No resale prices.** `SeatGeekAdapter` hardcodes `AllIn = false` and reads an empty
  `stats`. The SeatGeek partner application is unsent. Ticketmaster's marketplace source
  (`ticketmaster-resale`) is the only resale number today, and only for AXS rooms.
- **Five venues with no primary signal.** AXS and Etix rooms. JamBase's trial would say
  whether their on-sale dates are reachable; the trial has not been started.
- **`watchlist:prices` is manual by default.** Correct for an operator's desk, wrong for a
  product: a fan's watch must poll without the operator pressing a button. Flip to
  `Scheduled` only after `cadence-check` has read the plan for a real watchlist.
- **Phase 4 is untouched.** Sixteen of eighteen windows are `Placeholder.razor`. The
  Event window's first tab is the product; it does not exist yet.

---

## 6. The plan, in horizons

### Horizon 1 — the record exists and can be seen (next four weeks)

1. **Run the real on-sale window** on 2026-09-18 15:00Z, the first Platinum-enabled
   Bridgestone on-sale. Docker up, keys in `.env`, `onsale:watch` live, `on_sale_ticks`
   read back in order. Closes Phase 2's manual run and Phase 3's real window, and answers
   research §7 question 2.
2. **Event window, first tab.** "Was it really sold out?" over `OnSaleTick`: primary
   availability and resale listing count on one axis, on-sale price pinned, every cell
   linked to its source, `AllIn` shown. `apple-mudblazor` governs it; render test in the
   same commit. This is Phase 4's first box and the product's first screen.
3. **Public read-only route** `/e/{slug}` rendering the same component without the desk
   chrome. Anonymous, cacheable, no JS interop needed. A permanent URL per event is what
   a journalist links to and a fan shares.
4. **Ops window** showing quota used, reserved and next sweep, so the operator can see the
   budget while the first records are written. Closes Phase 3.

### Horizon 2 — the record reaches people (weeks four to twelve)

5. **On-sale calendar.** A page listing every Nashville on-sale and presale from the feed,
   and an `.ics` endpoint. Zero quota, subscribe once. The cheapest acquisition channel
   the product will ever have.
6. **Delivery.** One transactional email provider behind an `INotifier`, with
   `primary_reappeared` as the first rule delivered. A per-event "tell me if face value
   comes back" form on the public page, email only, no account: the address is the owner.
7. **First monthly report**, rendered by `monthly-report`, published as a page and mailed
   to the addresses from step 6. Closes Phase 5's third box.
8. **Send the applications**: SeatGeek partner program, Ticketmaster partner Availability
   API, JamBase trial. Each is an email; each unblocks a column. Record the answers in the
   research doc.
9. **Release gate and public URL.** `release-check` clean, HTTPS, the compose file's
   hardening as written. Closes Phase 5.

### Horizon 3 — the product keeps people (months three to six)

10. **Accounts without passwords.** Magic-link sign-in, `OwnerId` on `Watch` and
    `Purchase`, the scheduler polling the union. Watches from step 6 migrate to the owner
    whose address they carry.
11. **Watchlist board and price history**, once at least one resale source returns prices.
    Until then the board shows availability and face value only and says so.
12. **Purchases, savings, receipt export.** Phase 4's last boxes.
13. **A second city**, only with a venue list, provider per venue and DMA id in seed data,
    and only after the first Nashville report has been read by someone who is not the
    developer.

---

## 7. Money, and the terms that bound it

The constraint first. Ticketmaster's developer terms say an app may not derive revenue
from the API except through Ticketmaster's own programmes, may cache event content only
for reasonable periods, and may be rate-limited when calls are not primarily in response
to direct user actions. The sanctioned revenue path is the **Ticketmaster affiliate
programme**, which requires a Discovery key and an Impact publisher account and pays
around one percent with a thirty-day cookie. SeatGeek's partner programme runs on Impact
too, at about one percent with a forty-five-day cookie, and grants the Platform API with
attribution. StubHub grants API access to affiliates on Partnerize at roughly three
percent; TickPick and Gametime pay four to seven percent on Impact and have no API.

What the comparable small products earn from:

| Product | Model |
|---|---|
| camelcamelcamel | Free; Amazon affiliate clickouts and ads; four people |
| Keepa | Free tier, Pro at about €29 a month, data API from €49 a month |
| Hopper | Booking commission plus fintech: price freeze, cancel-for-any-reason |
| SeatData | Broker subscriptions and per-request API |
| Paid newsletters, 2026 | $10 to $15 a month by default; analyst titles $100 to $200 a year |

For a Nashville-only product the arithmetic is plain: affiliate commission at one to three
percent on clickouts from one city will not pay for hosting, let alone time. Its value is
legitimacy: it is the route under which showing Ticketmaster and SeatGeek prices is
expressly allowed. The defensible asset is the record, so the plausible stack is:

1. **Free, always:** the public on-sale record page, the calendar, the reappearance
   alert. These are what a watchdog cites and a fan shares; charging for them destroys
   the reason they exist.
2. **Affiliate links on every price cell**, Ticketmaster and SeatGeek via Impact, StubHub
   via Partnerize once its API is granted. Join before the public URL goes up so the
   links are compliant from the first visitor.
3. **The monthly Nashville report as a newsletter**, free to fans, with a sponsored or
   paid tier for venue and artist teams deciding on face-value resale, and for the press
   that covers this beat weekly. Beehiiv or Substack, not a hand-rolled mailer.
4. **The record given away to the Tennessee Attorney General and to journalists**, on
   request and without a fee, because Tennessee is a plaintiff in both live cases and a
   cited record is worth more than a licence fee at this size.
5. **Data licensing or a second city** only after the first three have run for a season.
   Adding a market before monetising matters more than any pricing choice.

Before any paid tier: written clarification from Ticketmaster on the scheduled poller and
on retaining tick summaries indefinitely. The raw responses are already discarded, which
helps; the summaries are the product, so the question must be asked, not assumed.

---

## 8. Decisions and risks

- **Ticketmaster's terms forbid deriving revenue from the API.** Every monetised path
  runs through a partner agreement first. Until one exists the product is free, and that
  is fine: the record's value to the watchdog does not depend on a fee.
- **A scheduled poller is "not primarily in response to direct user actions".** The
  budget guard and the 20 percent reserve are the defence; the public page and the
  calendar add read load, not API load, because they render stored rows.
- **SeatGeek's caching clause.** Storing summary numbers, attributing every cell, keeping
  raw responses out of long-term storage: the grey area the research already names.
  Publishing the record makes the storage more visible, not more extensive.
- **A record with no prices is still a record.** Availability alone tells the sellout
  story. Do not wait for SeatGeek to publish page A.
- **Accounts are the largest single change** and the least valuable until delivery works.
  They are last on purpose.

---

## 9. Sources

Repository documents this plan builds on:

- `docs/superpowers/specs/2026-09-11-nashville-concert-price-sources-research.md`, §2 terms, §3 law, §4.1 the on-sale record, §6 fixture findings, §7 open questions.
- `docs/superpowers/specs/2026-09-11-ticket-tracker-desk-reuse-plan.md`, §5 window catalogue, §6 phases.
- `PHASES.md` as verified on 2026-09-12.

Market research, 2026-09-12:

- Competitors: [TicketData](https://www.ticketdata.com/) and [coverage](https://www.ticketnews.com/2025/11/ticketdata-aims-to-bring-radical-visibility-to-ticket-prices-for-consumers/); [SeatData pricing](https://seatdata.io/pricing/); [SeatHeat](https://seatheat.app/); [TickPick Price Freeze](https://www.tickpick.com/blog/tickpicks-price-freeze-feature-explained/); [TicketIQ](https://www.ticketiq.com/); [Gametime pricing](https://gametime.co/blog/the-ins-and-outs-of-pricing-on-gametime/); [Ticketmaster favourites alerts](https://blog.ticketmaster.com/add-favorite-artists-get-ticket-alerts-recommendations/); [Event Spy](https://www.event-spy.com/).
- Law and enforcement: [FTC junk fees rule](https://www.ftc.gov/news-events/news/press-releases/2024/12/federal-trade-commission-announces-bipartisan-rule-banning-junk-ticket-hotel-fees); [StubHub $10M settlement, April 2026](https://www.regulatoryoversight.com/2026/04/stubhub-to-refund-10m-in-junk-fees-as-part-of-latest-ftc-settlement/); [state AG junk-fee enforcement, 2026](https://www.regulatoryoversight.com/2026/04/state-attorneys-general-and-continued-enforcement-against-junk-fees-in-2026/); [Paul, Weiss on the April 2026 verdict](https://www.paulweiss.com/insights/client-memos/live-nationticketmaster-antitrust-verdict-key-takeaways-from-the-states-jury-trial-win); [Tennessee AG on a breakup](https://www.wsmv.com/2026/04/16/tennessee-ag-says-ticketmaster-live-nation-breakup-is-absolutely-table-after-companies-found-guilty-violating-federal-state-antitrust-laws/); [FTC BOTS Act suit, September 2025](https://www.ftc.gov/news-events/news/press-releases/2025/09/ftc-sues-live-nation-ticketmaster-engaging-illegal-ticket-resale-tactics-deceiving-artists-consumers) and [Tennessee's joinder](https://www.tn.gov/attorneygeneral/news/2025/9/18/pr25-48.html); [motion to dismiss status, May 2026](https://www.digitalmusicnews.com/2026/05/01/ftc-live-nation-bots-act-lawsuit/); [Tenn. Code § 47-50-121](https://law.justia.com/codes/tennessee/title-47/chapter-50/section-47-50-121/); [Tennessee AG complaint form](https://www.tn.gov/attorneygeneral/working-for-tennessee/consumer/file-a-complaint.html); [NY AG "Obstructed View"](https://ag.ny.gov/sites/default/files/reports/Ticket_Sales_Report.pdf); [CBC TradeDesk investigation](https://www.cbc.ca/news/business/ticketmaster-resellers-las-vegas-1.4828535); [Queue-it on the sellout myth](https://queue-it.com/blog/tickets-sold-out-debunking-instant-sellout-myth/).
- Access and money: [Ticketmaster API terms](https://developer.ticketmaster.com/support/terms-of-use/); [Ticketmaster distribution partners](https://developer.ticketmaster.com/partners/distribution-partners/); [Inventory Status API](https://developer.ticketmaster.com/products-and-docs/apis/inventory-status/); [SeatGeek partner programme](https://seatgeek.com/blog/seatgeek-partner-program-instructions-info); [SeatGeek API terms](https://seatgeek.com/api-terms); [StubHub affiliate programme](https://support.stubhub.co.uk/en/support/solutions/articles/80001038076-what-is-the-stubhub-affiliate-programme-); [TickPick affiliates](https://www.tickpick.com/affiliates/); [camelcamelcamel and Keepa](https://goaura.com/blog/camelcamelcamel-vs-keepa); [Hopper revenue mix](https://www.businessofapps.com/data/hopper-statistics/); [paid newsletter pricing, 2026](https://pressgazette.co.uk/newsletters/newsletters-2026-prices-retention-churn/).
- Fan sentiment: [UK survey on dynamic pricing and fees](https://clusters.uk.com/articles/brands-fans-concert-ticket-sales/); [Red Rocks sellout complaints](https://www.yahoo.com/news/axs-red-rocks-answer-questions-221238595.html); [Ticketmaster caps Olivia Dean resale](https://www.aol.com/lifestyle/ticketmaster-caps-olivia-dean-resale-210000248.html); [face-value resale expansion, April 2026](https://www.ticketnews.com/2026/04/ticketmaster-expands-face-value-resale-cashortrade/).

Caveats: TicketData's FAQ, SeatData's pricing page, SeatGeek's API terms and StubHub's
developer site refused automated fetches, so those rows rest on search snippets and
secondary coverage. "Historica", named in the research doc, could not be confirmed as a
product distinct from TicketData.

## 10. Proposed phases for `phase-plan`

Phase 4 and 5 stay as written, reordered so the Event window's first tab and the public
route come before the board. Two phases follow.

**Phase 6 — The record reaches people.** Gate: `release-check` before the public URL;
`cadence-check` before `watchlist:prices` goes `Scheduled`.

- [ ] Public read-only `/e/{slug}` for every event with an on-sale record; anonymous,
      cached, every cell linked to its source.
- [ ] On-sale calendar page and `.ics` endpoint from the feed's on-sale and presale times.
- [ ] `INotifier` with one transactional email provider; `primary_reappeared` delivered;
      per-event subscribe form, address as owner, one-click unsubscribe.
- [ ] Affiliate accounts: Ticketmaster and SeatGeek on Impact; links on every price cell.
- [ ] Applications sent and answers recorded: SeatGeek partner, Ticketmaster partner
      Availability API, StubHub affiliate API, JamBase trial.
- [ ] First monthly report published as a page and mailed.

Exit: a fan with no account opens a Nashville event's record from a shared link,
subscribes to the calendar, and receives one reappearance email for a real event.

**Phase 7 — The product keeps people.** Gate: `apple-mudblazor` on every window.

- [ ] Magic-link sign-in; `OwnerId` on `Watch` and `Purchase`; scheduler polls the union;
      Phase 6 subscriptions migrate to their owner.
- [ ] Watchlist board and price history, live once one resale source returns prices;
      availability-only until then, labelled as such.
- [ ] Purchases, savings, receipt export.
- [ ] Written clarification from Ticketmaster on the scheduled poller and tick retention,
      filed in `docs/`, before any paid tier.

Exit: a signed-in fan sets a target on a watched event and receives `target_reached` from
an all-in observation; the operator's quota dashboard shows the union watchlist inside
budget.
