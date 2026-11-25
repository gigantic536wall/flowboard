# Flowboard: real-time task board for small teams

A Trello-style board written entirely in C#: Blazor for the interface, SignalR for live updates,
PostgreSQL for storage. Changes made by one person appear for everyone on the board immediately.

> 🚧 Work in progress: the data model and board services are done; the Blazor interface comes next.

## Highlights so far

- **Ordering without renumbering:** cards and columns use fractional ranks, so moving a card writes one row.
  When keys grow long after many inserts at the same spot, the column is renumbered automatically.
- **No lost updates:** card edits use optimistic concurrency. If a colleague saved first, you get a clear
  message instead of silently overwriting their work (tested with simultaneous edits).
- **Safe under concurrency:** placing cards locks the target column row, so parallel inserts and moves never
  collide (tested with 12 simultaneous inserts).
- **Teams and roles:** owners, members and viewers; boards of other teams are invisible ("not found", not "forbidden").
- **History:** every change is recorded (who, what, when) and published to live viewers.

## Tech stack

C# 14 · .NET 10 · Blazor (Interactive Server) · SignalR · ASP.NET Core Identity · Entity Framework Core · PostgreSQL · xUnit

## Run locally

Requirements: .NET 10 SDK, PostgreSQL.

```bash
cd src/Flowboard.Web
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=flowboard;Username=postgres;Password=…"
dotnet user-secrets set "ConnectionStrings:Test" "Host=localhost;Database=flowboard_test;Username=postgres;Password=…"
cd ../..
dotnet tool restore
dotnet ef database update -p src/Flowboard.Core -s src/Flowboard.Web
dotnet run --project src/Flowboard.Web
dotnet test
```
