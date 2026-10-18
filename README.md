# FiveM Portfolio

Server resources for FiveM (GTA V), written in **C#** and **Lua**. Each folder is a
self-contained resource you can drop into `resources/`.

| Resource | Language | What it shows |
|---|---|---|
| [`bank-cs`](bank-cs) | C# + HTML/CSS/JS | Server-authoritative banking, ATM prop detection, NUI interface |
| [`garage-cs`](garage-cs) | C# + MySQL | Persistent vehicle garage, ownership checks, atomic state changes |
| [`customs-cs`](customs-cs) | C# + MySQL + NUI | Vehicle customisation: live preview, server-priced mods saved on the owned vehicle |
| [`needs-cs`](needs-cs) | C# + MySQL + NUI | Hunger and thirst with server-side decay, driven by inventory events |
| [`shops-cs`](shops-cs) | C# + MySQL + NUI | Shops with all-or-nothing purchases across the bank and inventory |
| [`inventory-cs`](inventory-cs) | C# + MySQL + NUI | Slot/weight inventory, atomic moves and trades, drag and drop UI |
| [`jobs-cs`](jobs-cs) | C# + MySQL | Jobs, grades, duty and paychecks paid through a bank export |
| [`admin-cs`](admin-cs) | C# + MySQL | Roles, bans enforced at connect, rank checks, audit log |
| [`spawn-core`](spawn-core) | Lua | Persistent last position across sessions, `/car` dev command |
| [`hello-lua`](hello-lua) | Lua | Client/server events and commands, the basics |
| [`chat-tabcomplete`](chat-tabcomplete) | JavaScript (NUI) | Tab completion for the default chat |

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
- **Vehicle controls.** `L` locks/unlocks, `K` toggles the engine, `/locate` shows where your
  vehicles are (blips plus a GPS waypoint to the nearest). The client sends only *which* vehicle;
  the server resolves the real entity, checks it carries a plate this player took out, checks
  distance (or that they're sitting in it, for the engine), flips its own record of the state and
  tells the client what to apply. Positions for `/locate` are read from the real entities on the server.
- **Clean state.** Cars of a player who disconnects, and every car after a server restart,
  go back to the garage instead of staying marked "out".

Commands: `/givecar <model>` (admin), `/garage`, `/takeout <id>`, `/park`, `/locate`, `/lock` (L), `/engine` (K).

Requires `oxmysql`, a `mysql_connection_string` convar, and OneSync (`+set onesync on`).
Build with `./garage-cs/deploy.ps1`.

## customs-cs

Vehicle customisation with a live preview, where the server prices and saves everything.

- **Preview is local, purchases are not.** Sliders change how the vehicle looks to the player
  instantly; the server then prices the proposal. Nothing is charged or saved until the server
  approves the purchase.
- **The server reads the truth.** It never trusts the client's idea of what the vehicle already
  has: it loads the saved mods from the database, prices only what actually changed (upgrades are
  charged per level gained, downgrades are free) and validates every value against strict ranges.
  A malformed or out-of-range mods string is rejected whole.
- **Ownership enforced twice.** The player must be driving a real vehicle inside a customs bay
  (position read on the server), and the save is `UPDATE ... WHERE plate = ? AND owner = ?`,
  which only matches if they own it. If the save fails after the bank charge, the money is refunded.
- **Loose coupling with the garage.** `garage-cs` stores the mods as an opaque string and announces
  "a vehicle spawned" with it; `customs-cs` is the only part that knows what it means and applies it.
  Customised vehicles keep their look every time they are taken out.
- **Cross-resource discount.** A mechanic on duty (`jobs-cs`) pays 20% less; if `jobs-cs` isn't
  running there is simply no discount.
- Customisable: primary and secondary colour, window tint, engine / brakes / transmission levels, turbo.

Commands: `/setcustoms` (admin) places a bay where you stand; drive in as the driver and press E.

Requires `oxmysql`, `bank-cs` and `garage-cs`. Build with `./customs-cs/deploy.ps1`.
## needs-cs

Hunger and thirst, built to react to other resources without depending on them.

- **Server-owned numbers.** The server decays hunger and thirst on a timer, applies item effects,
  decides when the player is hurt (starving, dehydrated, or both) and persists the values. The
  client only draws the HUD and applies the damage it's told to.
- **Decoupled integration.** When an item is used, `inventory-cs` raises a server event
  (`inventory:itemUsed`). `needs-cs` listens and decides what that item does, so the inventory
  knows nothing about needs and new effects need no inventory change.
- **Safe loading and saving.** One in-memory copy per online player (concurrent loads reuse it),
  values clamped to 0-100, written on a timer, on disconnect and on resource stop.
- **Exports:** `getNeeds`, `addNeeds` for other resources.
- Decay interval is configurable: `set needs_tick_seconds 60`.

Commands: `/setneeds <hunger> <thirst>` (admin, for testing). Water and sandwiches from the shop
restore thirst and hunger.

Requires `oxmysql`. Build with `./needs-cs/deploy.ps1`.
## shops-cs

Shops where a purchase spans two other resources and still can't leave things half done.

- **All-or-nothing purchases.** The server validates the shop, distance, item, quantity and
  stock first, then checks `inventory-cs` can carry it (`canAdd`), takes the money with
  `bank-cs` (`removeMoney` checks the balance and deducts in one step), and finally hands over
  the item (`addItem`). Those steps run back to back in one synchronous handler, so another
  request can't slip in between. If the last step ever fails, the money is refunded.
- **Never trusts the client.** It sends a shop id, an item id, a quantity and "cash" or "bank".
  Prices, stock, locations and balances all come from the server.
- **Server-side proximity.** The buyer's position is read on the server (OneSync) and must be
  within 4 m of the shop. Shop spots are placed in-game with `/setshop <id>` and saved to a file,
  so there are no hand-typed coordinates.
- **Stock that restocks.** Limited stock per item, refilled on a timer
  (`set shops_restock_minutes 5`); open shop windows update live. Stock is intentionally kept
  in memory, while every purchase is written to a `shop_purchases` history table.
- **Cash or bank**, chosen per purchase, with live balances in the UI.
- **Fails safe.** If the bank or inventory resource is down, the purchase is aborted before
  anything changes.

Shops: General Store (water, sandwich, bandage, radio) and Hardware Store (lockpick, repair kit).
Commands: stand where a shop should be and run `/setshop <general|hardware>` (admin); players
walk up and press E.

Requires `oxmysql`, `bank-cs` and `inventory-cs`. Build with `./shops-cs/deploy.ps1`.
## inventory-cs

A slot and weight based inventory where the server owns every number.

- **Intent, not state.** The UI sends "move slot 3 to 7" or "give 2 from slot 5". The server
  validates slot bounds, ownership, counts, stack limits and weight, and only then mutates.
  After every action it sends the new truth back, so the UI can't drift from the server.
- **Dupe-resistant by construction.** Each online player's inventory lives in server memory and
  is changed in single synchronous steps. `Give` removes from one inventory and adds to the
  other in the same step, after checking the receiver has room, so an item can't exist twice
  or nowhere. Moves, merges and swaps that break a rule are rejected without changing anything.
- **Server chooses the receiver.** `Give nearby` picks the closest player within 3 m using
  server-side positions, so a client can't hand items to a far-away or arbitrary player.
- **Coalesced writes.** Mutations mark slots dirty and a 500 ms loop writes the final values
  (and everything is flushed on disconnect and resource stop). That removes redundant writes
  and prevents two quick changes to one slot from reaching MySQL out of order.
- **Safe loading.** Rows are validated on load (unknown item, bad slot, oversized stack are
  ignored and logged), and concurrent loads can never create two copies of one inventory.
- **Item definitions in code**, DB stores only slot / item / amount.
- **Exports for other resources:** `addItem`, `removeItem`, `hasItem`, `getCount`
  (server-side only; all-or-nothing, so a failed add never leaves a partial grant).
- **UI:** drag and drop to move, stack and swap (hold Shift to split the quantity box amount),
  a detail panel with Use / Give nearby / Discard, and a weight bar.

Commands: `I` opens the inventory, `/giveitem <id> <item> [count]` (admin).

Requires `oxmysql`. Build with `./inventory-cs/deploy.ps1`.
## jobs-cs

Jobs with grades, a duty toggle and automatic paychecks, built to cooperate with the bank.

- **Cross-resource integration.** Paychecks are paid with `exports['bank-cs']:addBank(...)`.
  It's a *server* export, so a client can't call it to give itself money, and the bank
  re-validates the amount and the player on every call instead of trusting the caller.
- **Definitions in code, state in the database.** The DB stores only `job` and `grade`;
  labels and salaries come from the server-side definitions, so changing a salary needs no
  migration, and a job removed from the config falls back to unemployed instead of crashing.
- **Pay only while on duty.** `/duty` toggles it; duty resets on disconnect, on server restart
  and whenever a job changes, so nobody is paid for a role they no longer hold.
- **Exports for other resources:** `getJob`, `getGrade`, `isOnDuty(src, job)`
  (synchronous, served from a per-player cache that lives only while they're online).
- Interval is configurable with `set jobs_pay_interval_minutes 15`.

Commands: `/jobs`, `/job`, `/duty`, `/quitjob`, `/setjob <id> <job> [grade]` (admin).

Requires `oxmysql` and `bank-cs`. Build with `./jobs-cs/deploy.ps1`.

## admin-cs

Server-side admin tool where every action is permission-checked, rank-checked and logged.

- **Roles in MySQL:** `mod < admin < superadmin`. Each command declares its minimum role,
  checked on every call (no cached role that can go stale).
- **Rank protection.** You can only act on someone ranking strictly below you, so a mod can't
  kick an admin and an admin can't ban another admin.
- **Bans enforced at connect** with `playerConnecting` deferrals, so a banned player is
  refused before they ever load in. Supports `30m`, `12h`, `7d` and `perm`.
- **Audit log.** Every kick, ban, unban, teleport, announcement and role change is stored
  with who did it, to whom and why (`/adminlog`).
- **Safe by default.** Bans are saved *before* the player is dropped, and a failed insert
  aborts the kick. Reasons are length-limited and commands are rate-limited.
- **Bootstrapping.** The first SuperAdmin comes from the `admin_bootstrap_license` convar
  (kept in `server.cfg`, never committed). After that, roles are managed with `/setrole`.

Commands: `/kick`, `/ban <id> <30m|12h|7d|perm> [reason]`, `/unban <ban id>`, `/bans`,
`/goto`, `/bring`, `/announce`, `/setrole <id> <role>` (superadmin), `/adminlog`.

Requires `oxmysql` and OneSync. Build with `./admin-cs/deploy.ps1`.

## spawn-core

- The client reports its position every 5 s; the server validates the payload and stores
  it per license (flushed every minute and on disconnect).
- On spawn the client asks for it and teleports there, waiting for map collision to load
  first so the player doesn't fall through the world.
- `/car [model]` spawns a validated vehicle and seats you in it (deletes the previous one).

## Notes

Developed and tested against a local FiveM server (artifact 35245). C# resources target
.NET Framework 4.5.2 via the `CitizenFX.Core` NuGet packages.
