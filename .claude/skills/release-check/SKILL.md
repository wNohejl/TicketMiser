---
name: release-check
description: Run the full pre-deploy gate locally — build, format, tests, secret scan, pending-migration check, container build and health — and report pass or fail per step. Use before any deploy, before merging TicketMiser_Development into main, or when asked whether the site can ship.
---

# release-check

The CI job, run on the developer's machine before the push, plus the checks CI cannot do
because they need the compose file and a running container.

## Steps

1. **Run the gate:**

   ```powershell
   .\.claude\skills\release-check\release-check.ps1
   ```

   It runs, in order, and stops at the first failure:

   | Step | What it proves |
   |---|---|
   | `dotnet build -c Release` | The solution compiles with no warnings promoted to errors. |
   | `dotnet format --verify-no-changes` | Formatting matches the repository's rules. |
   | `dotnet test -c Release --no-build` | Every test is green, including Testcontainers integration tests if Docker is up. |
   | Secret scan | No tracked file contains an API key, a client id, a password or a private key; `.env` is not tracked. |
   | `dotnet ef migrations has-pending-model-changes` | The model matches the newest migration. Skipped with a note until `src/TicketMiser.Data` exists. |
   | `docker compose config`, `build`, `up`, health | The image builds, the container starts, `/health` answers. Skipped with a note until `docker-compose.yml` exists. |
   | Feed cap | `Ingestion:DiscoveryFeed:MaxBytes` is set in configuration. Skipped until the feed adapter exists. |

2. **Read the report.** A skipped step is printed in yellow and is not a pass; say which
   steps were skipped in the release note.
3. **If everything passes**, merge `TicketMiser_Development` into `main` with a merge
   commit whose body is the report, and push both.

## Rules

- Never deploy from a red or partially skipped gate without saying so in writing.
- Never add `--no-verify`, `-p:TreatWarningsAsErrors=false` or a skip flag to make the
  gate pass. Fix the thing.
