---
name: skill-audit
description: Monthly review of .claude/skills against what the repository actually did — prune skills that never fire, capture tasks done by hand twice, apply lessons the skills recorded. Use monthly or when the work feels manual.
---

# skill-audit

The vault's `skill-audit` scoped to this repository. Its evidence is the git log and the
skills' own `lessons.md` files, not the vault's run logs.

## Steps

1. **Mine the log.** `git log --since='30 days ago' --format='%s' TicketMiser_Development`.
   Group subjects by scope. A scope touched three or more times by hand with no skill
   behind it is a candidate.
2. **Mine the lessons.** Each skill may keep `lessons.md` beside its `SKILL.md`, at most ten
   bullets. Read every one. A lesson that has sat there for two audits without being
   folded into the `SKILL.md` gets folded in now.
3. **Check the gates were used.** For each commit touching `src/*/Adapters`, `Scheduler`,
   `Budget` or `docker-compose.yml`, was the gating skill's output in the session or the
   commit body? If not, the gate is a rule nobody follows; say so.
4. **Table:** candidate task | occurrences | proposed skill or deletion | expected output |
   priority.
5. For approved additions, write the skill with `superpowers:writing-skills` if the plugin
   is installed, else follow the shape of `source-fixture`: a routing-rule description, a
   script for the deterministic part, a lean `SKILL.md`.
6. Delete any skill not triggered in sixty days unless it is a gate named in `CLAUDE.md`.

## Rules

- Eight to twelve skills. Above that the context tax exceeds the benefit.
- A skill does one job. If the table proposes a skill with "and" in its description,
  split it.
