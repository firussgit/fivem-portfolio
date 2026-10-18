# FiveM Portfolio

Server resources for FiveM (GTA V), written in **C#** and **Lua**. Each folder is a
self-contained resource you can drop into `resources/`.

| Resource | Language | What it shows |
|---|---|---|
| [`bank-cs`](bank-cs) | C# + HTML/CSS/JS | Server-authoritative banking, ATM prop detection, NUI interface |
| [`garage-cs`](garage-cs) | C# + MySQL | Persistent vehicle garage, ownership checks, atomic state changes |
| [`spawn-core`](spawn-core) | Lua | Persistent last position across sessions, `/car` dev command |
| [`hello-lua`](hello-lua) | Lua | Client/server events and commands, the basics |

## bank-cs

A banking system built around one rule: **the client is never trusted**.

- **Server-authoritative.** Balances live on the server. The client (and the NUI) can only
  request an action; the server validates the amount, the target and the funds.
- **Commands:** `/balance`, `/deposit <n>`, `/withdraw <n>`, `/transfer <id> <n>`.
- **ATM interaction.** The client finds real ATM props in the world (4 models), verifies
  existence, model and distance, and shows a prompt; `E` opens the interface.
- **NUI.** A small dependency-free HTML/CSS/JS UI. It renders state and posts intent back
  through NUI callbacks; it holds no authority.
- **Persistence.** Accounts are stored per Rockstar license with resource KVP, so they
  survive restarts without needing a database.
- **Abuse protection.** Per-player rate limit, amount bounds, no self-transfer, and an
  audit line in the server console for every money movement.

Layout:

```
bank-cs/
  Server/BankServer.cs   accounts, validation, commands, NUI events
  Client/BankClient.cs   ATM detection, prompt, NUI bridge
  resource/              fxmanifest.lua + ui/ (index.html, style.css, script.js)
  deploy.ps1             builds both assemblies and copies them into the server
```

Build and deploy (needs the .NET SDK):

```powershell
./bank-cs/deploy.ps1
```

Add `ensure bank-cs` to `server.cfg`.

## garage-cs

A persistent vehicle garage. The database is the source of truth and the client can't
claim a car it doesn't own.

- **MySQL via oxmysql.** `player_vehicles` (owner license, model, unique plate, stored/out
  state, engine and body health). The table is created on start; see `sql/install.sql`.
- **Atomic take-out.** `UPDATE ... WHERE id = ? AND owner = ? AND state = 1` checks ownership
  *and* that the car is stored in one statement, so two simultaneous requests can never both
  spawn the same vehicle.
- **Server-verified parking (OneSync).** `/park` sends only a network id and the condition.
  The server resolves the real entity, reads its plate, checks that this player took that
  plate out, and only then stores and deletes it.
- **Whitelisted models, capped garage size, rate limiting, clamped inputs.**
- **Clean state.** Cars of a player who disconnects, and every car after a server restart,
  go back to the garage instead of staying marked "out".

Commands: `/givecar <model>` (admin), `/garage`, `/takeout <id>`, `/park`.

Requires `oxmysql`, a `mysql_connection_string` convar, and OneSync (`+set onesync on`).
Build with `./garage-cs/deploy.ps1`.

## spawn-core

- The client reports its position every 5 s; the server validates the payload and stores
  it per license (flushed every minute and on disconnect).
- On spawn the client asks for it and teleports there, waiting for map collision to load
  first so the player doesn't fall through the world.
- `/car [model]` spawns a validated vehicle and seats you in it (deletes the previous one).

## Notes

Developed and tested against a local FiveM server (artifact 35245). C# resources target
.NET Framework 4.5.2 via the `CitizenFX.Core` NuGet packages.
