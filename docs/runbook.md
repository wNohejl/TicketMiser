# TicketMiser runbook

What each alert means, how urgent it is, and what to do about it.

Every incident closed here needs a root cause and a corrective action; the app enforces it.
If the corrective action is a code change, reference the commit in the RCA so the loop from
incident to engineering change is visible later.

The rules come in two families. The platform rules are about a source and are the same four
LineOps runs on. The watch rules are about an event a person is tracking, and they are the
product: they say what the marketplaces would rather nobody noticed.

---

## `freshness` — Critical

**Means:** no successful ingestion run for this source in over 26 hours (SLO configurable via
`Reliability:FreshnessSlo`).

**Urgency:** real. Every price on the board is stale, and an on-sale window that falls inside
the gap is unrecoverable: the record of the first hour cannot be backfilled once the hour has
passed.

**Triage**
1. **Runs** window, filter to the source. Is it failing, or not running at all?
2. Failing → read the error on the most recent run.
   - `HttpRequestException` / 5xx → provider outage. Usually resolves itself; the circuit
     breaker re-closes on its own.
   - `401` / `403` → the key expired or was revoked, or the User-Agent is missing. Replace the
     key in configuration; never in the repository.
   - `429` → rate limited. Check whether the budget guard should have caught this; if the
     provider's real limit is lower than the configured ceiling, correct the `Source` row.
3. Not running at all → is the scheduler alive? Look for "Ingestion scheduler started" in the
   logs. If the host restarted and the schedule has drifted, run **Prices now** from the pull
   menu to close the gap.
4. If the provider is down for an extended period the platform degrades rather than breaks:
   other sources continue, and this alert auto-resolves on the next success.

---

## `success_rate` — Warn

**Means:** fewer than 95% of this source's runs succeeded over the trailing 7 days, across at
least 3 runs. `Partial` runs count as failures.

**Urgency:** investigate the same day. Data is still arriving, but something is intermittently
wrong and it usually degrades further.

**Triage**
1. Look for a pattern in the failures on the Runs window: every run, or one job? One time of
   day? A single job failing usually means one endpoint changed.
2. A mix of `Partial` runs means the provider is returning empty payloads intermittently; go
   to the `volume_anomaly` steps below.
3. Timeouts under load → consider raising the per-attempt timeout, but check first whether the
   provider is simply slow at a particular hour. On-sale minutes are that hour.

---

## `volume_anomaly` — Warn

**Means:** today's row count is below 50% of the trailing 7-day median while runs are still
succeeding.

**Urgency:** this is the alert that matters most. It is the only signal for a provider that
changed its schema and is now returning `HTTP 200` with data we no longer parse.

**Triage**
1. Run the `source-drift` skill against the source's fixture. It prints the paths that moved.
2. If the shape has changed, record the new payload with `source-fixture` **first**, then fix
   the parser, so the regression is pinned before the fix.
3. Check whether it is genuinely a quiet day. A Nashville Tuesday in January is a real cause,
   and the honest response is to note it in the incident and resolve it as a false positive
   rather than to weaken the threshold.
4. Real drift belongs in the adapter's normalisation layer, not in the calling code.

---

## `budget_pressure` — Info, then Warn

**Means:** a provider is at or above 80% of its configured ceiling
(`Reliability:BudgetWarnThreshold`). The alert escalates in place:

| Utilisation | Severity | What it means |
|---|---|---|
| ≥ 80% | `Info` | Tight, but nothing has stopped. The lever is scheduling. |
| ≥ 100% | `Warn` | Spent. `CreditBudgetGuard` is refusing runs and data has stopped arriving. |

Unmetered providers never raise this.

**Urgency:** at `Info`, none immediately. At `Warn` it has already become a freshness problem
waiting to be noticed, because refused runs write no rows.

**Triage**
1. The alert message names the dimension under pressure and the numbers behind it. Start there.
2. Ticketmaster at 5,000 a day → run `cadence-check` with today's watchlist size and on-sale
   count. The on-sale watches are the expensive part; the progression sweep can slow.
3. Consider narrowing the watchlist. Every watched event is one call per sweep.

---

## `price_drop` — Info

**Means:** a watched event's cheapest all-in price at one source fell since the previous
observation, and the watch asked to be told.

**Urgency:** none for the platform. This is a notice to a person, not a fault.

**Triage**
1. Open the event window. The price history shows the drop against the on-sale price.
2. If the drop is on the primary market, check the on-sale record for a primary re-release;
   a drop and a reappearance together mean held-back inventory was released.
3. Nothing to fix. Resolve when read.

---

## `target_reached` — Info

**Means:** a watched event's cheapest all-in price at some source is at or below the watch's
target price.

**Urgency:** none for the platform. A person set the number; the number arrived.

**Triage**
1. Open the event window and confirm the source and the all-in flag. A target set against
   all-in prices is never satisfied by a face-value number; the rule checks this, and the
   cell shows it.
2. Buy or do not. Log the purchase if you do, so the savings analytic can grade it.
3. Resolve when read.

---

## `primary_reappeared` — Warn

**Means:** primary inventory for a watched event went from not available back to available.
Held-back tickets were released, or a hold expired. This is the one alert no marketplace will
ever send.

**Urgency:** for the person, now: face-value tickets are back and will not stay. For the
platform, none.

**Triage**
1. Open the event's on-sale record. The tick where primary flipped back is marked; the resale
   listing count and lowest price at that minute are beside it.
2. If a purchase was made on the resale market between the sellout and the reappearance,
   note it on the purchase; it is the case the monthly report counts.
3. Resolve when read. The record keeps the evidence whether or not the alert is open.

---

## Running a drill

You do not have to wait for a real outage to practise this.

1. **Ops → On-call drill**, pick a source and a failure mode:
   - **Upstream 503**: hard failure, exercises retry and the breaker.
   - **Request timeout**: slow failure.
   - **Empty payload**: the silent one; returns `HTTP 200` and no rows.
2. **Run and evaluate**.
3. Watch the alert appear, promote it to an incident, work the timeline, then write the RCA
   and resolve it.
4. Set the source back to **Healthy** and confirm the alert auto-resolves on the next
   evaluation.
