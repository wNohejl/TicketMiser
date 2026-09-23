---
name: source-drift
description: Diff a live response's shape against the saved fixture for a ticket source and report added, missing and retyped fields without changing anything. Use when parse errors rise, when a source's success-rate or freshness alert fires, or weekly as a check.
---

# source-drift

Sources change shape without telling anyone. The fixture is the shape we parse; this
skill says how far the live response has moved from it. It reports and stops; changing the
fixture is `source-fixture`'s job, after a person has read the diff.

## Steps

1. **Fetch a live sample** to a temporary file with the same URL as the fixture's
   `meta.json` (never into the fixtures directory):

   ```powershell
   $meta = Get-Content tests/TicketMiser.Tests/Fixtures/seatgeek/ryman-stats.meta.json | ConvertFrom-Json
   .\.claude\skills\source-fixture\fetch-fixture.ps1 -Source $meta.source -Name drift-probe -Url $meta.url
   ```

   then move `drift-probe.json` to the scratchpad and delete `drift-probe.meta.json`.
2. **Diff the shapes:**

   ```powershell
   .\.claude\skills\source-drift\shape-diff.ps1 -Fixture tests/TicketMiser.Tests/Fixtures/seatgeek/ryman-stats.json -Live <scratch>/drift-probe.json
   ```

   The script flattens both documents to dotted paths with a type (`events[].stats.lowest_price: number`),
   array elements merged, and prints three lists: paths only in the live sample, paths only
   in the fixture, and paths whose type changed. Exit code 1 if any list is non-empty.
3. **Judge.** Additions are usually harmless. A missing path or a type change on a field
   the adapter reads (`lowest_price`, `priceRanges[].min`, `dates.status.code`,
   `allInclusivePricing`) is an incident: open one in the Ops window with the diff as the
   note, then run `source-fixture` to capture the new shape and fix the parser.
4. **Report** the three lists and the judgement. Do not modify the fixture.

## Rules

- Read-only. The only files this skill writes are in the scratchpad.
- A drift on a field the adapter does not read is recorded in `lessons.md`, not acted on.
