# Legal guidelines for the sources and the product

What the terms we have read allow, as rules a change can be checked against. The research
doc (`docs/superpowers/specs/2026-09-11-nashville-concert-price-sources-research.md`, §2
and §3) records where each rule comes from; this file is the checklist. Written
2026-09-12. When a term changes, change the rule here and cite the date.

## The rules every change is checked against

1. **No scraping.** Every number comes from an API whose terms we have read and accepted
   under our own key. No headless browser, no HTML parsing of a marketplace page, no
   residential proxy, no aggregator that does those things on our behalf inside the
   scheduled poll. Ticketmaster and every resale site forbid automated access to their
   pages; affiliate accounts are revoked for it; and a record collected that way is not a
   citation.
2. **Nothing is fetched unasked.** The scheduler polls watches. A request is either in
   response to an operator's action, a watch the operator set, or the on-sale window of a
   watched event. The budget guard refuses a run rather than overrunning a published
   limit, and the 20 percent reserve is never spent.
3. **Attribution on every cell.** Every price and status cell links to its source.
   SeatGeek's name appears wherever its numbers do, linked to seatgeek.com. Ticketmaster
   cells link to the Ticketmaster event page.
4. **Summaries, never content.** We store the numbers a source gives about an event
   (lowest, average, highest, listing count, status, all-in flag) and the event's
   reference data. We never store listings, seat maps, images, descriptions or any
   marketplace content, and raw responses are discarded once parsed. Fixtures committed
   for tests are the one exception and carry a redacted key.
5. **All-in is a column.** The FTC rule and Tennessee § 47-50-121 make the all-in price
   the number a consumer must be shown. Every observation carries `AllIn`; a face-value
   number is never compared to an all-in number; the board never ranks primary against
   resale.
6. **No revenue from the Ticketmaster API outside Ticketmaster's programmes.** The
   developer terms forbid deriving revenue from the API. The only sanctioned path is the
   Ticketmaster affiliate programme on Impact. Until a partner agreement or written
   clarification exists, the product is free and carries no paid tier that depends on
   Ticketmaster data.
7. **Affiliate links are disclosed.** Where a link earns a commission, the page says so
   in plain words near the link, as the FTC endorsement guides require.
8. **The record is public and permanent, the personal data is not.** `OnSaleTick` rows
   are never pruned and are in every snapshot. Subscriber addresses, accounts, and the
   watches and purchases an account owns are stored only for the purpose given, with
   one-click unsubscribe, and are never in a snapshot that leaves the machine. There are
   two lists and they do not mix: an event's "face value is back" alert (`Subscriptions`)
   and the monthly Nashville report (`ReportSubscriptions`), each with its own double
   opt-in and its own unsubscribe. An address given for an event alert is never mailed the
   report, and an address on the report list is mailed nothing but the report; an address
   on both gave its consent twice. Neither list, nor the record of what was sent to it
   (`AlertDeliveries`, `ReportDeliveries`), is in a snapshot.
9. **Complaints are the consumer's.** The export is a dated receipt of what was shown
   when. The product never files a complaint, never claims a violation occurred, and
   never names a seller as having broken a law. It shows the timeline and links the
   Tennessee Attorney General and FTC forms.

## Per-source terms, as read

| Source | Allowed | Not allowed | Note |
|---|---|---|---|
| Ticketmaster Discovery, Inventory Status, Feed | Display with link to the event page; cache "for reasonable periods in order to provide the service"; 5,000 calls a day, 5 a second | Deriving revenue outside TM programmes; calls "not primarily in response to direct user actions" at a rate TM considers abusive | Our scheduled poller is a grey area the reserve and the modest cadence defend; ask TM in writing before any paid tier |
| SeatGeek Platform API | Display with the SeatGeek logo linked to seatgeek.com; summary numbers of our own observations | Copying, storing or caching SeatGeek content beyond intermediate purposes; displaying listings for other sellers | Prices arrive only with the partner client id, applied for 2026-09-12 |
| StubHub | API to affiliates on application | Anything before the application is answered | Apply via affiliates@stubhub.com |
| JamBase Data | Paid feed with on-sale signals and face value on its plans | Republishing its feed as ours | Trial before spending |
| Gametime, TickPick, Vivid Seats, AXS, Etix | Affiliate links where a programme exists | Automated access of any kind | No public API; not in the poll |
| Tickets.dev, TicketsData | Spot checks by hand after a lawyer reads their terms and the marketplaces' | The scheduled poll | They scrape on the customer's behalf |

## What to do when a rule and a feature conflict

Say so in the commit or the spec, name the rule, and do not merge the feature. A rule is
changed only by reading the term again and recording the date and the wording that
changed. If the term is unclear, the question goes to the source in writing and the
answer is filed under `docs/terms/`.
