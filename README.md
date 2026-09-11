# TicketMiser

A ticket price tracker: compile the price of an event's tickets from several marketplaces on
a schedule, keep the history, and show where the best price is now and how it got there.

.NET 10 · Blazor (Interactive Server) · MudBlazor 9 · PostgreSQL 17 · Docker

## Where it comes from

The desk — the window manager, the strip and its drawers, the primitives, the theme and the
stylesheet — is `src/TicketMiser.Desk`, carried over from
[LineOps](https://github.com/wNohejl/LineExtractor) with its git history. `git log --follow`
on any file in it reaches back to the commit that first wrote it there. The design it
implements is recorded in `docs/adr` (0007, 0013, 0016), and the plan for what this product
builds on it is `docs/superpowers/specs/2026-09-11-ticket-tracker-desk-reuse-plan.md`.

`src/TicketMiser.Web` is the host: a starter window catalogue in which every window is a
placeholder, so the desk can be driven end to end before any window has content. The desk's
render tests are in `tests/TicketMiser.Desk.Tests` and read that catalogue by name.

## Running it

```powershell
dotnet run --project src/TicketMiser.Web --launch-profile http
```

Then open <http://localhost:5270>. No database is needed yet; nothing here reads one.

```powershell
dotnet test
```
