---
name: phase-plan
description: Turn a spec in docs/superpowers/specs into a phased PHASES.md with demonstrable exit criteria, or add a phase to the existing one. Use to "plan phase N", "phase this spec", or when a spec is approved and no phase covers it.
---

# phase-plan

The phase-plan half of the vault's `design-to-phases`, without the repo scaffold and the
vault bookkeeping, which this project does not need. The output is one file, `PHASES.md`
at the repository root, kept honest by `phase-status`.

## Inputs

- The spec to phase: an argument naming a file in `docs/superpowers/specs/`, or the newest
  spec there if none is given.
- The current `PHASES.md`, if it exists. Phases are added and amended, never renumbered.

## Steps

1. **Read the spec** and the plan it descends from. Where the spec has a numbered plan
   (the reuse plan's §6, the source research's §4), the phases follow it; do not invent a
   different decomposition.
2. **Write or extend `PHASES.md`.** Each phase has:
   - a one-line goal;
   - a checklist of concrete tasks, each naming the file, test or command it produces;
   - **exit criteria that are demonstrable**: a command to run and what it prints, or a
     window to open and what it shows. "Adapters work" is not a criterion;
     "`dotnet test --filter Adapters` is green on the saved fixtures" is;
   - the skills that gate it (`source-fixture`, `cadence-check`, `release-check`).
3. **Phase 0 is the walking skeleton** if the project has no running end-to-end slice.
   Here it already exists: the desk runs with placeholders, so Phase 1 is marked done and
   the plan starts at the domain.
4. **Cost posture:** stay on free keys and free tiers unless the spec says otherwise, and
   name the paid item when it does (JamBase is the one so far).
5. **Report** the phases added or changed and the first unchecked task, as a startable
   sentence.

## Rules

- A phase is not done because its code exists. It is done when its exit criteria have
  been run and the output matches. `phase-status` enforces this.
- Never put a date estimate in a phase. Put the exit criterion instead.
- Never write a task that cannot fail. "Consider caching" is not a task.
