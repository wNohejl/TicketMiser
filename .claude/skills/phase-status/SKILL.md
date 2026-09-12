---
name: phase-status
description: Verify PHASES.md checkmarks against the actual repository and report the next startable task. Use for "where are we", "what's next", "status", or before starting a work session.
---

# phase-status

Keeps `PHASES.md` honest. A checkbox is true only if the thing it names exists and its exit
criterion passes.

## Steps

1. Read `PHASES.md`. Find the current phase: the first with an unchecked item.
2. For every **checked** item in the current and previous phase, verify against the
   repository:
   - named files exist (`Glob`), named tests exist (`Grep` for the test name);
   - if the exit criterion is a command that runs in under two minutes, run it and compare
     the output; otherwise verify statically and say so in the report.
3. For each mismatch, uncheck the item in `PHASES.md` and say why in the report. Do not
   check an item that is unchecked, even if it looks done; that is the developer's call.
4. State the **next unchecked task** as one concrete, startable sentence naming the file
   or command it begins with.
5. Report, in this order: a one-line progress bar per phase (`Phase 2 ▮▮▮▯▯ 3/5`), the
   mismatches found, the next task, and which gating skill it needs first.

## Rules

- Read-only apart from unchecking. Never edit code from this skill.
- If `PHASES.md` does not exist, say so and stop; `phase-plan` writes it.
