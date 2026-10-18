# FiveM Portfolio

A set of connected FiveM (GTA V) server resources written in **C#** (plus a little **Lua** and
**JavaScript**): banking, garage, vehicle customisation, fuel, inventory, shops, jobs, needs,
a phone and an admin tool. Each folder is a self-contained resource you can drop into `resources/`,
and together they behave like a small roleplay server.

The common thread is one rule: **the client is never trusted.** The UI and the game client only
send *intent* ("buy this", "move slot 3 to 7"); the server validates everything, owns the numbers,
and answers with the new truth.

## Contents

- [Resources](#resources)
- [How the resources fit together](#how-the-resources-fit-together)
- [Design principles](#design-principles)
- [Quick start](#quick-start) (one script plus a Docker database)
- [Command reference](#command-reference)
- [Try it: a five-minute tour](#try-it-a-five-minute-tour)
- [Resource details](#resource-details)
- [Notes](#notes)

## Resources

| Resource | Language | What it shows |
|---|---|---|
| [`bank-cs`](bank-cs) | C# + HTML/CSS/JS | Server-authoritative banking, ATM prop detection, NUI interface |
| [`garage-cs`](garage-cs) | C# + MySQL | Persistent vehicle garage, ownership checks, atomic state changes, lock / engine / locate |
| [`customs-cs`](customs-cs) | C# + MySQL + NUI | Vehicle customisation: live preview, server-priced mods saved on the owned vehicle |
| [`fuel-cs`](fuel-cs) | C# + MySQL | Fuel per vehicle: server-validated drain, paid refuelling, persisted on owned vehicles |
| [`phone-cs`](phone-cs) | C# + MySQL + NUI | Phone: number per player, contacts, text messages, phone banking |
| [`needs-cs`](needs-cs) | C# + MySQL + NUI | Hunger and thirst with server-side decay, driven by inventory events |
| [`shops-cs`](shops-cs) | C# + MySQL + NUI | Shops with all-or-nothing purchases across the bank and inventory |
| [`inventory-cs`](inventory-cs) | C# + MySQL + NUI | Slot/weight inventory, atomic moves and trades, drag and drop UI |
| [`jobs-cs`](jobs-cs) | C# + MySQL | Jobs, grades, duty and paychecks paid through a bank export |
| [`admin-cs`](admin-cs) | C# + MySQL | Roles, bans enforced at connect, rank checks, audit log |
| [`spawn-core`](spawn-core) | Lua | Persistent last position across sessions, `/relog`, `/car` dev commands |
| [`hello-lua`](hello-lua) | Lua | Client/server events and commands, the basics |
| [`chat-tabcomplete`](chat-tabcomplete) | JavaScript (NUI) | Tab completion for the default chat |

## How the resources fit together

```
                      ┌────────────┐
                      │  oxmysql   │  MySQL access for every *-cs resource below
                      └─────┬──────┘
                            │
  ┌──────────┐  money   ┌───┴──────┐  items   ┌──────────────┐
  │ jobs-cs  ├─────────►│ bank-cs  │◄─────────┤  shops-cs    │
  └────┬─────┘ paychecks└──┬───▲───┘ purchase └──────┬───────┘
       │ isOnDuty          │   │ charges             │ addItem / canAdd
       │ (mechanic         │   │                     ▼
       │  discount)        │   │              ┌──────────────┐  inventory:itemUsed  ┌──────────┐
       │            ┌──────▼───┴───┐          │ inventory-cs ├─────────────────────►│ needs-cs │
       └───────────►│  customs-cs  │          └──────────────┘                      └──────────┘
                    └──────▲───────┘
        garage:vehicleSpawned (mods)             fuel-cs ── bank-cs (cash)     phone-cs ── bank-cs (bank)
                    ┌──────┴───────┐
                    │  garage-cs   │  stores the vehicle, mods (opaque) and fuel column
                    └──────────────┘
```

- **Exports** (server-side only, so clients can't call them): `bank-cs` exposes `addMoney`,
  `removeMoney`, `addBank`, `getBalances`; `inventory-cs` exposes `addItem`, `removeItem`,
  `hasItem`, `getCount`, `canAdd`; `jobs-cs` exposes `getJob`, `getGrade`, `isOnDuty`;
  `needs-cs` exposes `getNeeds`, `addNeeds`.
- **Events:** `inventory-cs` raises `inventory:itemUsed` (needs-cs reacts to it);
  `garage-cs` raises `garage:vehicleSpawned` on the client (customs-cs reacts to it).
- Resources degrade gracefully: without `jobs-cs` there's just no mechanic discount; without
  `bank-cs` a purchase aborts before anything changes.

## Design principles

| Principle | Where you can see it |
|---|---|
| **Server-authoritative state.** Balances, stock, fuel, inventories and vehicle state live on the server; the client renders what it's told. | every resource |
| **Server-side position checks.** With OneSync the server reads the player's real position, so "must be at an ATM / shop / bay / pump" can't be faked. | bank, shops, customs, fuel, inventory (give nearby) |
| **Atomic operations.** Check-and-change happens in one step (`UPDATE ... WHERE state = 1`, check-and-deduct), so two requests can't both win. | garage, bank, inventory |
| **All-or-nothing across resources, with a refund path.** A purchase that spans bank + inventory can't be left half done. | shops, customs, phone |
| **Strict input validation and rate limiting.** Bounds, lengths, formats, whitelists, per-player cooldowns. | every resource |
| **Coalesced, ordered database writes.** Dirty tracking plus a timer instead of a write per click. | inventory, fuel, needs |
| **Loose coupling.** Resources talk through exports and events and keep working when a dependency is missing. | needs, customs, jobs |
| **NUI holds no authority.** The page renders state and posts intent through callbacks; user text is rendered with `textContent`. | bank, inventory, shops, customs, phone |

## Quick start

You can run the whole thing on your own PC in a few steps. Everything below is Windows / PowerShell.

**You need:** a FiveM server folder, the [.NET SDK](https://dotnet.microsoft.com/download),
[Docker Desktop](https://www.docker.com/products/docker-desktop/) (for the database), a free
[Cfx.re license key](https://keymaster.fivem.net) and GTA V with FiveM installed to join.

**1. Get a FiveM server and the base resources.** Download the Windows server artifact from
[runtime.fivem.net](https://runtime.fivem.net/artifacts/fivem/build_server_windows/master/), and clone
[cfx-server-data](https://github.com/citizenfx/cfx-server-data) as the folder you run it from. Also download
[oxmysql](https://github.com/overextended/oxmysql/releases) (the release `.zip`) into `resources/[standard]/oxmysql`.

**2. Start the database** (MariaDB in Docker, only reachable from your own machine):

```powershell
docker compose up -d
```

**3. Build and install every resource** into your server's `resources` folder with one command.
It builds each C# resource, stops on the first failed build, and copies the results to `resources\[portfolio]\`:

```powershell
./setup.ps1 -ResourcesPath C:\path\to\server-data\resources
esources
esources
```

**4. Configure the server.** Copy [`server.cfg.example`](server.cfg.example) to your server folder as
`server.cfg`, put your license key in it, and (for a local test server only) uncomment the permission
block at the bottom so you can use the setup commands. The connection string already matches the Docker database.

**5. Start the server with OneSync** (it has to be a launch argument; several resources read player
positions on the server):

```powershell
.\FXServer.exe +set onesync on +exec server.cfg
```

Join `localhost:30120` from the FiveM client. Database tables are created automatically on first start.
The server console should print lines like `[bank] server started`, `[garage] ready` and `[phone] ready`.
Then follow [the five-minute tour](#try-it-a-five-minute-tour).

**Rebuilding one resource while developing:** each resource has its own `deploy.ps1`
(for example `./garage-cs/deploy.ps1`). It targets `../../server-data/resources/[dev]/` by default,
so edit the `$dest` line at the top if your layout differs, then `restart garage-cs` in the server console.

### Configuration reference

```cfg
set jobs_pay_interval_minutes 15      # how often paychecks are paid (jobs-cs)
set needs_tick_seconds 60             # how often hunger / thirst decay (needs-cs)
set shops_restock_minutes 5           # how often shop stock refills (shops-cs)
set admin_bootstrap_license "xxxxxxxx"   # first SuperAdmin (admin-cs); keep it out of git
```

Placement commands (`/setshop`, `/setcustoms`, `/setfuel`, `/atmscan`) and the dev commands check
ACE permissions, so you decide who gets them. On a real server, give them to a staff group instead of everyone:

```cfg
add_ace group.admin garage.admin allow
add_ace group.admin jobs.admin allow
add_ace group.admin bank.admin allow
add_ace group.admin inventory.admin allow
add_ace group.admin shops.admin allow
add_ace group.admin needs.admin allow
add_ace group.admin customs.admin allow
add_ace group.admin fuel.admin allow
add_principal identifier.license:xxxxxxxx group.admin
```

Optional: [`chat-tabcomplete`](chat-tabcomplete) adds Tab completion to the stock chat (see its README for the four-step patch).

## Command reference

Chat commands start with `/`. Arguments in `<angle brackets>` are required, `[square brackets]`
are optional. "Admin" means an ACE permission (named in the last column) unless stated otherwise.

### Everyday commands

| Command | Resource | What it does | Example |
|---|---|---|---|
| `/hello` | hello-lua | Round trip client → server → client | `/hello` |
| `/relog` | spawn-core | Saves your position and drops you back to the FiveM menu | `/relog` |
| `/id` | admin-cs | Shows your server id | `/id` |
| `/balance` | bank-cs | Shows your cash and bank balance | `/balance` |
| `/deposit <amount>` | bank-cs | Cash → bank (**at an ATM**) | `/deposit 250` |
| `/withdraw <amount>` | bank-cs | Bank → cash (**at an ATM**) | `/withdraw 100` |
| `/transfer <id> <amount>` | bank-cs | Send bank money to a player (**at an ATM**) | `/transfer 3 500` |
| `/garage` | garage-cs | Lists your stored vehicles with their ids | `/garage` |
| `/takeout <vehicle id>` | garage-cs | Spawns a stored vehicle next to you | `/takeout 4` |
| `/park` | garage-cs | Stores the vehicle you're in or next to | `/park` |
| `/lock` | garage-cs | Locks / unlocks your nearest vehicle (key **L**) | `/lock` |
| `/engine` | garage-cs | Engine on / off for the vehicle you're in (key **K**) | `/engine` |
| `/locate` | garage-cs | Blips and a GPS route to your vehicles that are out | `/locate` |
| `/jobs` | jobs-cs | Lists available jobs | `/jobs` |
| `/job` | jobs-cs | Shows your job, grade, salary and duty state | `/job` |
| `/duty` | jobs-cs | Toggles duty (you only get paychecks on duty) | `/duty` |
| `/quitjob` | jobs-cs | Become unemployed | `/quitjob` |
| `/inventory` | inventory-cs | Opens the inventory (key **I**) | `/inventory` |
| `/phone` | phone-cs | Opens the phone (key **F1**) | `/phone` |
| `/car [model]` | spawn-core | Dev helper: spawns a vehicle and seats you in it | `/car sultan` |
| `/uifix` | bank-cs | Releases the mouse cursor if a UI ever leaves it stuck | `/uifix` |

### Staff commands (admin-cs roles)

Roles rank `none < mod < admin < superadmin`. You can only act on someone ranking strictly below you.

| Command | Min role | What it does | Example |
|---|---|---|---|
| `/players` | mod | Lists online players with ids | `/players` |
| `/kick <id> [reason]` | mod | Kicks a player | `/kick 5 spamming` |
| `/goto <id>` | mod | Teleports you to a player | `/goto 5` |
| `/bring <id>` | mod | Teleports a player to you | `/bring 5` |
| `/bans` | mod | Lists active bans | `/bans` |
| `/ban <id> <30m\|12h\|7d\|perm> [reason]` | admin | Bans a player (saved before the kick) | `/ban 5 7d cheating` |
| `/unban <ban id>` | admin | Lifts a ban (ids from `/bans`) | `/unban 12` |
| `/announce <message>` | admin | Server-wide announcement | `/announce Restart in 5 minutes` |
| `/adminlog` | admin | Shows the audit log | `/adminlog` |
| `/setrole <id> <none\|mod\|admin\|superadmin>` | superadmin | Changes a player's role | `/setrole 2 mod` |

### Setup and test commands (ACE permission)

| Command | Permission | What it does | Example |
|---|---|---|---|
| `/givecar <model>` | `garage.admin` | Adds a vehicle to your garage. Models: adder, sultan, buffalo, comet2, kuruma, faggio, bati, sandking, stanier, baller | `/givecar comet2` |
| `/giveitem <id> <item> [count]` | `inventory.admin` | Gives an item. Items: water, sandwich, bandage, lockpick, repair_kit, radio | `/giveitem 1 sandwich 3` |
| `/setjob <id> <job> [grade]` | `jobs.admin` | Sets a job. Jobs: unemployed, police, ems, mechanic, taxi | `/setjob 1 mechanic 1` |
| `/setneeds <hunger> <thirst>` | `needs.admin` | Sets your own hunger and thirst (0-100) | `/setneeds 20 10` |
| `/setshop <general\|hardware>` | `shops.admin` | Places a shop where you stand | `/setshop general` |
| `/setcustoms` | `customs.admin` | Places a customs bay where you stand | `/setcustoms` |
| `/setfuel` | `fuel.admin` | Places a fuel station where you stand | `/setfuel` |
| `/atmscan` | `bank.admin` | Registers ATM props near you as valid ATM locations | `/atmscan` |
| `/atmdebug` | none | Prints the ATMs the client can currently see | `/atmdebug` |

### Keybinds

| Key | Action |
|---|---|
| **E** | Interact: use an ATM, open a shop, enter a customs bay, refuel (the prompt shows when it applies) |
| **I** | Inventory |
| **F1** | Phone |
| **L** | Lock / unlock vehicle |
| **K** | Engine on / off |
| **Tab** (in chat) | Complete the command you're typing; press again to cycle matches |
| **Esc** | Close any open interface |

Keys can be rebound in the game's keyboard settings (FiveM key mappings).

## Try it: a five-minute tour

1. **Money.** `/balance`, walk to an ATM and press **E**, deposit some cash.
2. **A car.** `/givecar comet2`, `/garage`, `/takeout 1`. Drive a bit, then `/park` and take it out again.
3. **Controls.** Press **L** (the car chirps), **K** (engine off), walk away and use `/locate`.
4. **Fuel.** Stand at a pump and run `/setfuel`. Watch the `FUEL %` gauge drop while driving, then pull up to the pump and press **E**. The fuel level survives `/park` and `/takeout`.
5. **Customs.** Run `/setcustoms` in a garage forecourt, drive in and press **E**. Move the sliders (the car changes live, the price comes from the server), then **Buy & save**. Park and take the car out: the mods are still there.
6. **Jobs.** `/setjob <your id> mechanic 1`, `/duty`, wait for a paycheck, then open customs again: you now get a 20% discount.
7. **Items.** Place a shop with `/setshop general`, buy water and a sandwich, open the inventory with **I** and drag them around. Use them and watch your hunger and thirst bars recover.
8. **Phone.** Press **F1**. Add a contact, send a text to a friend's number, and pay them from the Bank tab.
9. **Staff.** `/setrole <id> mod`, then `/players`, `/goto` and `/adminlog`.

## Resource details

### bank-cs

A banking system built around one rule: **the client is never trusted**.

- **Server-authoritative.** Balances live on the server. The client (and the NUI) can only
  request an action; the server validates the amount, the target and the funds.
- **Server-side ATM proximity.** Deposit, withdraw and transfer only work within a few metres of a
  registered ATM, checked against the player's real position. `/atmscan` registers the ATM props
  around an admin into `atms.txt`.
- **ATM interaction.** The client finds real ATM props in the world (4 models), verifies
  existence, model and distance, and shows a prompt; `E` opens the interface.
- **NUI.** A small dependency-free HTML/CSS/JS UI. It renders state and posts intent back
  through NUI callbacks; it holds no authority.
- **Persistence.** Accounts are stored per Rockstar license with resource KVP, so they
  survive restarts without needing a database.
- **Abuse protection.** Per-player rate limit, amount bounds, no self-transfer, and an
  audit line in the server console for every money movement.
- **Exports** for other resources: `addMoney`, `removeMoney` (check-and-deduct in one step),
  `addBank`, `getBalances`.

Layout (every resource follows the same shape):

```
bank-cs/
  Server/BankServer.cs   accounts, validation, commands, NUI events
  Client/BankClient.cs   ATM detection, prompt, NUI bridge
  resource/              fxmanifest.lua + ui/ (index.html, style.css, script.js)
  deploy.ps1             builds both assemblies and copies them into the server
```

### garage-cs

A persistent vehicle garage. The database is the source of truth and the client can't
claim a car it doesn't own.

- **MySQL via oxmysql.** `player_vehicles` (owner license, model, unique plate, stored/out
  state, engine and body health, plus `mods` and `fuel` columns used by customs and fuel). The
  table is created on start; see `sql/install.sql`.
- **Atomic take-out.** `UPDATE ... WHERE id = ? AND owner = ? AND state = 1` checks ownership
  *and* that the car is stored in one statement, so two simultaneous requests can never both
  spawn the same vehicle.
- **Server-verified parking (OneSync).** `/park` sends only a network id and the condition.
  The server resolves the real entity, reads its plate, checks that this player took that
  plate out, and only then stores and deletes it.
- **Whitelisted models, capped garage size (10), rate limiting, clamped inputs.**
- **Vehicle controls.** `L` locks/unlocks, `K` toggles the engine, `/locate` shows where your
  vehicles are (blips plus a GPS waypoint to the nearest). The client sends only *which* vehicle;
  the server resolves the real entity, checks it carries a plate this player took out, checks
  distance (or that they're sitting in it, for the engine), flips its own record of the state and
  tells the client what to apply. Positions for `/locate` are read from the real entities on the server.
- **Opaque mods.** The garage stores a `mods` string it doesn't understand and announces
  `garage:vehicleSpawned` with it; `customs-cs` interprets it.
- **Clean state.** Cars of a player who disconnects, and every car after a server restart,
  go back to the garage instead of staying marked "out".

Requires `oxmysql`, a `mysql_connection_string` convar, and OneSync.

### customs-cs

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
- **What you can change:** primary and secondary colour, window tint, engine / brakes / transmission
  levels, turbo, and body parts (spoiler, bumpers, side skirts, exhaust, grille, hood, roof). Body
  sliders only appear for parts the vehicle model actually has.

Place a bay with `/setcustoms`; drive in as the driver and press **E**.
Requires `oxmysql`, `bank-cs` and `garage-cs`.

### fuel-cs

Fuel for every vehicle, tracked per licence plate.

- **The client simulates, the server decides.** Drain depends on speed, which the client knows, so the
  client burns fuel and reports the level every 5 seconds. The server only accepts a report that is
  *lower* than what it has and drops by a plausible amount, from the player who is really driving
  that plate. A modified client can never raise its own fuel.
- **The only way up is paying.** At a station the server checks the player's real position and the
  vehicle's, prices the litres (and caps them to the cash the player has), takes the money through
  `bank-cs`, and only then sends the new level.
- **Persisted.** Owned vehicles save their level in `player_vehicles.fuel` (batched writes every 30 s
  and on resource stop), so a vehicle keeps its level through `/park` and `/takeout`. Other
  vehicles start part-full.
- An empty tank stops the engine; refuelling brings it back. Stations are placed in-game with `/setfuel`.

Requires `oxmysql` and `bank-cs`.

### phone-cs

A phone with its own number, contacts, texting and phone banking, backed by MySQL.

- **Numbers belong to the licence.** Each player gets a unique `555-xxxx` number the first time and keeps it.
- **No spoofing.** The "from" of a message is the server's own record of who the sender is; the NUI only
  says who to send to and what to say. Input is cleaned, length-limited and rate-limited.
- **Live and offline delivery.** Messages are stored; an online recipient gets a push and a notification,
  an offline one finds it on their next open.
- **Phone banking.** Pay an online player by number: the amount is taken from the sender with the
  bank's atomic check-and-deduct and credited to the receiver, with a refund if the credit fails.
- Contacts can only be deleted by their owner (`DELETE ... WHERE id = ? AND owner = ?`). User text is
  rendered with `textContent`, never as HTML.

Open with **F1** or `/phone`. Requires `oxmysql` and `bank-cs`.

### needs-cs

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

Water and sandwiches from the shop restore thirst and hunger. Requires `oxmysql`.

### shops-cs

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
- **Stock that restocks.** Limited stock per item, refilled on a timer; open shop windows update
  live. Stock is intentionally kept in memory, while every purchase is written to a
  `shop_purchases` history table.
- **Cash or bank**, chosen per purchase, with live balances in the UI.
- **Fails safe.** If the bank or inventory resource is down, the purchase is aborted before
  anything changes.

Shops: General Store (water, sandwich, bandage, radio) and Hardware Store (lockpick, repair kit).
Requires `oxmysql`, `bank-cs` and `inventory-cs`.

### inventory-cs

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
- **Exports for other resources:** `addItem`, `removeItem`, `hasItem`, `getCount`, `canAdd`
  (server-side only; all-or-nothing, so a failed add never leaves a partial grant).
- **UI:** drag and drop to move, stack and swap (hold Shift to split the quantity box amount),
  a detail panel with Use / Give nearby / Discard, and a weight bar.

Requires `oxmysql`.

### jobs-cs

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

Jobs and grades: police (Cadet, Officer, Sergeant), ems (Trainee, Paramedic, Chief),
mechanic (Apprentice, Mechanic), taxi (Driver), unemployed. Requires `oxmysql` and `bank-cs`.

### admin-cs

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

Requires `oxmysql` and OneSync.

### spawn-core

- The client reports its position every 5 s; the server validates the payload and stores
  it per license (flushed every minute and on disconnect).
- On spawn the client asks for it and teleports there, waiting for map collision to load
  first so the player doesn't fall through the world.
- `/car [model]` spawns a validated vehicle and seats you in it (deletes the previous one).
- `/relog` saves your position and has the server drop you back to the FiveM menu. Scripts
  can't run `connect` or `disconnect` (both are disabled in production mode), so the server does it.

### hello-lua and chat-tabcomplete

`hello-lua` is the smallest possible round trip (command → server event → client event) in Lua.
`chat-tabcomplete` patches the default chat with Tab completion: type `/gi`, press **Tab** for
`/givecar `, press **Tab** again to cycle the other matches.

## Notes

Developed and tested against a local FiveM server (artifact 35245). C# resources target
.NET Framework 4.5.2 via the `CitizenFX.Core` NuGet packages, and each one builds with
`dotnet build` through its `deploy.ps1`. The `*-dev` ACEs in a local `server.cfg` are for testing
only; on a real server, grant them to staff groups as shown in the quick start.

## License

MIT, see [LICENSE](LICENSE).
