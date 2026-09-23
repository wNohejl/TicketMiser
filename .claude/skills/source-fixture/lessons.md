# source-fixture — lessons

At most ten bullets. `skill-audit` reads these monthly.

- **SeatGeek puts the town in the room's name (2026-09-22).** `venue.name` is "The Truth - Nashville",
  "City Winery - Nashville", "Brooklyn Bowl - Nashville" where Ticketmaster says "The Truth" or
  "Brooklyn Bowl Nashville"; Ticketmaster in turn sometimes appends the state ("The Pinnacle - TN").
  `venue.city`/`venue.state` are clean, and SeatGeek gives no Ticketmaster venue id (only the
  Ticketmaster *event* id, top-level `ticketmaster`). A plain letters-and-digits fold therefore
  made a second Venue, and since the event slow path never crosses rooms every show there existed
  twice. Venue names are compared with `VenueName.Key(name, city, state)`, which sets aside a
  trailing locality only when it is the room's own town or state; `DatabaseInitializer
  .MergeDuplicateVenuesAsync` folds rooms split before the fix. When saving a SeatGeek fixture,
  check the venue names against the Ticketmaster fixture for the same night.
- **SeatGeek's `ticketmaster` field is the legacy host id, not the Discovery id (2026-09-22).**
  It is sixteen upper-case hex digits (`1B00650D13C6A27C`), the last segment of the Ticketmaster
  event page URL; the Discovery API's ids look like `G5viZ_A30im87` and cannot be derived from it.
  Filed under the `ticketmaster` key it was fetched as a Discovery id and it kept Ticketmaster's
  own listing from resolving onto the SeatGeek-created event. Both adapters now write it under
  `ExternalIdKeys.TicketmasterLegacy` (Ticketmaster's from its `url`), which joins the two
  listings exactly, and `DatabaseInitializer.RefileLegacyTicketmasterIdsAsync` moves old rows.
