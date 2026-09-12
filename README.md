# TicketMiser

A ticket price tracker for Nashville concerts: know a ticket's price the moment it goes on
sale, keep every move it makes after that, and say which site to buy from today. The part
nobody else offers is the **on-sale record**: a per-event ledger of the sale's first hours,
what the primary market said and when it said "sold out", and what the resale market held at
the same minute.

.NET 10 · Blazor (Interactive Server) · MudBlazor 9 · PostgreSQL 17 · Docker

## What is where

| Project | What it is |
|---|---|
| `src/TicketMiser.Core` | Entities, the adapter contracts, the one comparison rule (`PriceComparison`), telemetry names. |
| `src/TicketMiser.Data` | EF Core context, migrations, the partitioned observation tables, the initialiser that seeds sources and Nashville venues. |
| `src/TicketMiser.Ingestion` | Adapters (Ticketmaster Discovery, Inventory Status, Discovery Feed, SeatGeek), the resolver, the on-sale watch, the scheduler, the jobs. |
| `src/TicketMiser.Reliability` | Freshness, success rate, volume, budget and the three watch rules; incidents; the runbook. Carried from LineOps. |
| `src/TicketMiser.Observability` | OpenTelemetry wiring and health checks. Carried from LineOps. |
| `src/TicketMiser.Desk` | The window manager, primitives and design system. Carried from LineOps with its history. |
| `src/TicketMiser.Web` | The host: catalogue of windows, single-process scheduler. |
| `src/TicketMiser.Worker` | The same scheduler as its own process. |
| `tests/` | Render tests on the desk; parse tests on fixtures; integration tests on Testcontainers Postgres. |

The design is in `docs/adr`, the plan in `PHASES.md`, the research and decisions in
`docs/superpowers/specs`, the working rules in `CLAUDE.md`, and the skills that gate the work
in `.claude/skills`.

## Running it

```powershell
.\scripts\setup.ps1                                                    # .env and the dev certificate
docker compose -f docker-compose.yml -f compose.dev.yml up -d postgres # Postgres on 127.0.0.1:5434
dotnet run --project src/TicketMiser.Web --launch-profile http
```

Then open <http://localhost:5270>. The host migrates the database and seeds the sources and
venues on start. With no keys configured no source is registered, which the pull menu
reports as unconfigured rather than broken. Keys go in `.env` for compose or
`appsettings.Local.json` for host runs, never in the repository.

```powershell
dotnet test                            # render, parse and integration tests; Docker needed for the last
.\.claude\skills\release-check\release-check.ps1   # the gate, before a deploy
```

## Where it comes from

The desk and the reliability layer are LineOps's, carried over per
`docs/superpowers/specs/2026-09-11-ticket-tracker-desk-reuse-plan.md`. `git log --follow` on
any desk file reaches back to the commit that first wrote it there.
