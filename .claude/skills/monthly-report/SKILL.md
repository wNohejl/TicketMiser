---
name: monthly-report
description: Produce the Nashville monthly on-sale report from the database — minutes to primary sellout against resale listings present at that minute, fee gap by venue, primary re-releases — as a fixed-shape markdown file, and stop short of publishing. Use on the first of the month or when asked for "the Nashville report".
---

# monthly-report

The loudest thing a small tool can do is publish what it saw. This skill renders the
numbers; a person publishes them after reading. Its inputs are the `OnSaleTick` and
`PriceObservation` tables from the source research §4; until they exist the skill says so
and stops.

## Steps

1. **Check the prerequisites.** `src/TicketMiser.Data` exists, Postgres is up
   (`docker compose ps`), and `on_sale_ticks` has rows for the month. If not, stop and say
   which is missing.
2. **Run the queries** in `queries/` against the local database, for the month given or
   the previous calendar month:

   | Query | Produces |
   |---|---|
   | `sellout-vs-resale.sql` | Per event: venue, on-sale time, minutes until primary status left `TICKETS_AVAILABLE`, SeatGeek listing count and lowest price at that minute, and whether primary later reappeared. |
   | `fee-gap-by-venue.sql` | Per venue: median all-in minus face, as a percentage, across the month's primary observations with `all_in = true` and a face value recorded. |
   | `price-vs-onsale.sql` | Per event: final price per source as a percentage of the on-sale price. |

   ```powershell
   $env:PGPASSWORD = (Get-Content .env | Select-String '^POSTGRES_PASSWORD=' | ForEach-Object { $_.Line.Split('=')[1] })
   psql -h localhost -U ticketmiser -d ticketmiser -v month='2026-09' -f .claude/skills/monthly-report/queries/sellout-vs-resale.sql
   ```

3. **Render** `docs/reports/<yyyy-mm>-nashville.md`, opening with front matter that reads
   `---` / `published: false` / `---`, then the fixed shape: a one-paragraph
   summary with no adjectives, the three tables, a "how this was measured" section naming
   the sources, the cadence and the all-in rule, and a "what we could not see" section
   listing AXS venues, which are resale-only in our data.
4. **Stop.** Do not publish, post, or push the report. Say it is ready to read.
   To publish, a person sets `published: true` in the file's front matter (the skill writes `published: false`) and commits; the next deploy serves it at `/reports/<yyyy-mm>`. Nothing mails it on its own.
5. **After publishing: publish, deploy, then send-report.** Once the deployed site serves
   `/reports/<yyyy-mm>`, the operator mails it to the report list, from the machine whose
   database holds the list:

   ```powershell
   dotnet run --project src/TicketMiser.Web -- send-report 2026-10
   ```

   It migrates, sends one email per confirmed report subscriber who has not had that month
   (the report's summary paragraph and a link to the page, with a one-click unsubscribe),
   prints `sent / failed / already sent`, and exits without starting the host. An
   unpublished or missing month is refused and nothing is sent. Running it twice sends
   nothing the second time; a failed send has no delivery row, so running it again retries
   only those. It mails the report list (`ReportSubscriptions`) only — never the event
   alerts' subscribers (legal-guidelines rule 8). Not the skill's to run: a person runs it.

## Rules

- Every number in the report traces to a query in `queries/`. No hand-typed figures.
- Summaries only. No listing, seat, or copied marketplace content appears in the report.
- Say "we could not see" rather than inferring. The AXS rooms are the standing example.
