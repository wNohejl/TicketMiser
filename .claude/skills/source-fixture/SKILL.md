---
name: source-fixture
description: Save one real API response from a ticket source (Ticketmaster Discovery, Inventory Status, Discovery Feed, SeatGeek) as a committed test fixture and write or update the parse test that reads it. Use when adding or changing a source adapter, when a parse test fails on live data, or to answer a "what does the source actually return" question.
---

# source-fixture

Adapter tests read fixtures, never the network. This skill is how a fixture comes to exist,
and it is how the open questions in the source research get answered: by a saved response,
not by reading documentation.

## Inputs

- `Source`: `ticketmaster`, `inventory-status`, `discovery-feed`, or `seatgeek`.
- `Url`: the exact request, without the key. The script adds the key from the environment.
- `Name`: what the fixture is about, kebab-case: `bridgestone-onsale-t-plus-5`,
  `ryman-stats-with-listings`.
- A key in the environment: `TICKETMISER_TICKETMASTER_KEY`, `TICKETMISER_INVENTORY_KEY`,
  `TICKETMISER_SEATGEEK_CLIENT_ID`. Keys are never in the repository or in a fixture.

## Steps

1. **Fetch and save** with the script:

   ```powershell
   .\.claude\skills\source-fixture\fetch-fixture.ps1 -Source seatgeek -Name ryman-stats `
     -Url "https://api.seatgeek.com/2/events?venue.city=Nashville&taxonomies.name=concert&per_page=5"
   ```

   It writes `tests/TicketMiser.Tests/Fixtures/<source>/<name>.json` (pretty-printed, key
   redacted everywhere it appears) and `<name>.meta.json` (the redacted URL, the fetch
   time, the HTTP status, the byte size). A fixture is never edited by hand after this;
   fetch again instead.
2. **Read the fixture** and decide what is volatile: ids are kept, timestamps are kept,
   anything that is a secret or a session token is removed. Say what was removed in the
   meta file's `redactions` array. If nothing was, say that.
3. **Write or update the parse test** in `tests/TicketMiser.Tests/Adapters/<Source>ParseTests.cs`:
   one test per fixture, reading the file, calling the adapter's parser, asserting the
   canonical observation fields (`Lowest`, `Average`, `Highest`, `ListingCount`, `AllIn`,
   `Currency`, `ObservedAt`, the source's external id). For a Ticketmaster fixture also
   assert `sales.public.startDateTime`, `dates.status.code` and `allInclusivePricing`.
4. **Record the answer.** If the fixture settles one of the open questions in
   `docs/superpowers/specs/2026-09-11-nashville-concert-price-sources-research.md` §5,
   add the answer there with the fixture's name as the citation.
5. **Commit the fixture, its meta file and the test together.**

## Rules

- One fixture, one shape. A second shape from the same endpoint is a second fixture.
- The script refuses to save a body that still contains the key after redaction.
- Never fetch inside a test. If a test needs the network, it is not a test.
- Record a lesson in `lessons.md` beside this file when a source surprises you.
