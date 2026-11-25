# Flowboard: real-time task board for small teams

[![CI](https://github.com/gigantic536wall/flowboard/actions/workflows/ci.yml/badge.svg)](https://github.com/gigantic536wall/flowboard/actions/workflows/ci.yml)

A Trello-style board written entirely in C#: Blazor for the interface, SignalR for live updates,
PostgreSQL for storage. When a colleague moves a card, it moves on your screen too.

**▶ Live demo:** LIVE_DEMO_LINK: press *Try it without signing up*, then open the invite link from the
box at the top of the board in a second browser (or on your phone) and move a card.

<p align="center"><img src="docs/live-collaboration.gif" width="900" alt="Two browsers on the same board: a new card, a move, an 'is editing' badge and an edit conflict appear live on the other side"></p>
<p align="center"><sub>Recorded by the end-to-end test: two separate browsers, two different guests, no reloads.</sub></p>

## Features

- **Boards, columns and cards** with assignees, due dates (overdue ones in red), colour labels and descriptions;
  drag and drop between and within columns; per-column work-in-progress limits.
- **Live for everyone:** changes appear instantly on every open board, with a short notice of who did what.
- **Presence:** see who is on the board right now, and who is editing which card.
- **No lost work:** if someone saved a card while you were editing it, you are told so and nothing is overwritten.
- **Teams and roles:** workspaces with owners, members and read-only viewers; invite links that owners can revoke.
- **History** of every change: who, what, when.
- **Try without an account:** a guest gets a ready-made team board; its invite link lets a second browser join as
  another guest. Demo data is removed after 24 hours.

## Engineering highlights

- **Ordering without renumbering:** cards and columns use fractional ranks, so a move writes one row. When keys
  grow long after many inserts at the same spot, the column is renumbered automatically (tested).
- **Concurrency handled in the database:** card edits use optimistic concurrency (a version column); placing cards
  locks the target column row (`SELECT … FOR UPDATE`), so parallel inserts never collide. Both are tested with
  simultaneous requests against a real PostgreSQL.
- **Real time in two layers:** an in-process broadcaster updates every open Blazor page (each browser is connected
  to its circuit over SignalR), and a SignalR hub (`/hubs/boards`) exposes the same feed to other clients.
- **Privacy by design:** boards of other teams answer "not found" rather than "forbidden", so their existence is not revealed.
- **Production details:** cookie encryption keys are stored in PostgreSQL (sign-ins survive redeploys), migrations
  run on start, demo creation is rate-limited, and a background job cleans up expired demo data.
- **Tests at three levels:** 43 unit and integration tests (ranks, services, presence, broadcasting, invites,
  demo cleanup) and a Playwright test in which **two separate browsers** check presence, live updates,
  drag and drop, "is editing" badges and edit conflicts. CI runs it against the production Docker image.

## Tech stack

C# 14 · .NET 10 · Blazor (Interactive Server) · SignalR · ASP.NET Core Identity · Entity Framework Core ·
PostgreSQL · xUnit · Playwright · Docker · GitHub Actions · Render + Neon

## Project layout

```
src/Flowboard.Core   data model, EF Core, ordering, board/demo services (no UI)
src/Flowboard.Web    Blazor pages and components, real time (broadcaster, presence, hub), accounts, demo endpoints
tests/Flowboard.Tests  unit and integration tests against PostgreSQL
tests/Flowboard.E2E    two-browser Playwright test against a running app
```

## Run locally

Requirements: .NET 10 SDK, PostgreSQL.

```bash
cd src/Flowboard.Web
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=flowboard;Username=postgres;Password=…"
dotnet user-secrets set "ConnectionStrings:Test" "Host=localhost;Database=flowboard_test;Username=postgres;Password=…"
cd ../..
dotnet tool restore
dotnet ef database update -p src/Flowboard.Core -s src/Flowboard.Web
dotnet run --project src/Flowboard.Web     # http://localhost:5173
dotnet test tests/Flowboard.Tests
```

End-to-end test (with the app running):

```bash
dotnet build tests/Flowboard.E2E
pwsh tests/Flowboard.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
FLOWBOARD_URL=http://localhost:5173 dotnet test tests/Flowboard.E2E
```

## Docker and deployment

```bash
docker build -t flowboard .
docker run -p 8080:8080 -e ConnectionStrings__Default="postgres://user:password@host:5432/flowboard" flowboard
```

[`render.yaml`](render.yaml) deploys the image to Render (Frankfurt); the database is a free Neon PostgreSQL instance.
