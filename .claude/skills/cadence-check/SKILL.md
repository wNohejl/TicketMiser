---
name: cadence-check
description: Print the day's polling plan for every ticket source (sweeps, interval, reserve, on-sale watches and their quota cost) and run the scheduler through one on-sale window on a fake clock. Use before merging any change to the scheduler, the budget calculator, or the on-sale watch, and whenever a run is refused for budget.
---

# cadence-check

The arithmetic is deterministic, so a script does it. The judgement, whether the plan is
sane, is the reader's. Both happen before a scheduler change merges.

## Steps

1. **Print the plan:**

   ```powershell
   .\.claude\skills\cadence-check\cadence.ps1 -Watchlist 100 -OnSaleEvents 3
   ```

   Parameters default to the published limits (Ticketmaster 5,000/day and 5/s; SeatGeek
   no published limit, our own ceiling of 600/hour) and a 20 percent reserve. Override
   them with the real watchlist size from the database when it exists:
   `SELECT count(*) FROM watches WHERE enabled`.

   The plan shows, per source: calls available after reserve, on-sale watch cost for the
   day (40 Discovery calls per event plus one Inventory Status call per tick for the
   whole list), what is left for the progression sweep, sweeps per day, the interval, and
   whether the interval is shorter than the source's own price refresh (Ticketmaster: one
   hour), in which case the extra sweeps buy nothing.
2. **Read it back.** The plan is sane when: the on-sale watches fit inside the day with
   the progression sweep still running at least hourly; no source's per-second limit is
   exceeded by a sweep's burst; the reserve is untouched. If any of those fail, the
   change does not merge; say which number is wrong.
3. **Run the on-sale window on a fake clock.** When `src/TicketMiser.Ingestion` exists:

   ```powershell
   dotnet test tests/TicketMiser.Tests --filter "Category=Cadence"
   ```

   The Cadence tests inject a `TimeProvider`, set one event's `sales.public.startDateTime`,
   and advance from T minus 20 minutes to T plus 3 hours in one-minute steps, asserting a
   tick at every five-minute mark inside the window, the switch to hourly after, and the
   budget guard's reservation before the first tick. Until that project exists, say so and
   stop after step 2.
4. **Paste the plan** into the commit body of the scheduler change.

## Rules

- A change that makes the interval shorter must say what it costs in on-sale watches.
- Never raise the reserve below 20 percent; that is the margin for a source that retries.
