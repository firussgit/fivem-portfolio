# FiveM Portfolio

A set of connected FiveM (GTA V) server resources written in **C#** (plus a little **Lua** and
**JavaScript**): banking, garage, vehicle customisation, fuel, inventory, shops, jobs, field
actions, needs, a phone, a dispatch radio and an admin tool. Each folder is a self-contained
resource you can drop into `resources/`, and together they behave like a small roleplay server.

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
| [`garage-cs`](garage-cs) | C# + MySQL | Persistent vehicle garage, ownership checks, atomic state changes, lock / engine / locate, hotwire theft and insurance claims |
| [`customs-cs`](customs-cs) | C# + MySQL + NUI | Vehicle customisation: live preview, server-priced mods saved on the owned vehicle |
| [`fuel-cs`](fuel-cs) | C# + MySQL | Fuel per vehicle: server-validated drain, paid refuelling, persisted on owned vehicles |
| [`phone-cs`](phone-cs) | C# + MySQL + NUI | Phone: number per player, contacts, text messages, phone banking |
| [`dispatch-cs`](dispatch-cs) | C# + MySQL + NUI | Dispatch radio: 911/912 calls, on-duty police/EMS claims, server-validated scene positions |
| [`needs-cs`](needs-cs) | C# + MySQL + NUI | Hunger and thirst with server-side decay, driven by inventory events |
| [`shops-cs`](shops-cs) | C# + MySQL + NUI | Shops with all-or-nothing purchases across the bank and inventory |
| [`inventory-cs`](inventory-cs) | C# + MySQL + NUI | Slot/weight inventory, atomic moves and trades, drag and drop UI |
| [`jobs-cs`](jobs-cs) | C# + MySQL + NUI | Jobs, grades, duty, paychecks, courier/taxi activities and police/EMS/mechanic field actions |
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
  `needs-cs` exposes `getNeeds`, `addNeeds`; `dispatch-cs` exposes `createCall`, `cancelCall`.
- **Events:** `inventory-cs` raises `inventory:itemUsed` (needs-cs reacts to it);
  `garage-cs` raises `garage:vehicleSpawned` on the client (customs-cs reacts to it);
  `jobs-cs` raises `jobs:dutyChanged` and `jobs:jobChanged` on the server
  (dispatch-cs releases the unit's calls when they stop being eligible).
- **Dispatch:** `phone-cs` creates 911/912 calls through dispatch-cs' server-only `createCall`
  export. Dispatch asks `jobs-cs` who is on duty, offers the call only to the matching
  department, and pays a resolved call through `bank-cs`.
- Resources degrade gracefully: without `jobs-cs` there's just no mechanic discount; without
  `bank-cs` a purchase aborts before anything changes.

## Design principles

| Principle | Where you can see it |
|---|---|
| **Server-authoritative state.** Balances, stock, fuel, inventories and vehicle state live on the server; the client renders what it's told. | every resource |
| **Server-side position checks.** With OneSync the server reads the player's real position, so "must be at an ATM / shop / bay / pump / job hub / call" can't be faked. | bank, shops, customs, fuel, inventory (give nearby), jobs, dispatch |
| **Anti-teleport checks.** A job stop or a dispatch "on scene" only counts if the server can see the player there *and* enough time has passed for a vehicle to cover the distance. | jobs activities, dispatch |
| **Atomic operations.** Check-and-change happens in one step (`UPDATE ... WHERE state = 1`, check-and-deduct), so two requests can't both win. | garage, bank, inventory, dispatch claim |
| **Verify the effect, not just the request.** A revive pays only after the server re-reads the patient's health; a fine is logged only after the bank confirms the deduction; an impound only moves a vehicle that is still out. | jobs field actions |
| **All-or-nothing across resources, with a refund path.** A purchase that spans bank + inventory can't be left half done. | shops, customs, phone |
| **Strict input validation and rate limiting.** Bounds, lengths, formats, whitelists, per-player cooldowns. | every resource |
| **Coalesced, ordered database writes.** Dirty tracking plus a timer instead of a write per click. | inventory, fuel, needs |
| **Loose coupling.** Resources talk through exports and events and keep working when a dependency is missing. | needs, customs, jobs |
| **Shared code, no runtime coupling.** One server base class provides licence lookup, chat, oxmysql and rate limits; it is compiled into each resource rather than deployed as a shared DLL. | `shared/Server/PortfolioScript.cs` |
| **NUI holds no authority.** The page renders state and posts intent through callbacks; user text is rendered with `textContent`. | bank, inventory, shops, customs, phone, jobs |

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
set jobs_require_vehicle 1            # courier/taxi runs need a vehicle (0 = allow on foot)
set ems_revive_pay 200                # bank bonus for an EMS revive (0 = none)
set ems_revive_cooldown_seconds 60    # minimum gap before the same patient can be revived again
set mechanic_repair_pay 250           # bank fee for a mechanic repair (consumes a repair kit)
set police_fine_max 2500              # largest fine a police officer can issue
set garage_hotwire_seconds 8          # how long a lockpick takes to hotwire a stolen car
set insurance_payout_percent 50       # share of the repair cost paid on an insurance claim
set insurance_payout_max 2500         # hard cap on one claim settlement
set insurance_deductible 250          # taken off each claim settlement
set insurance_claim_cooldown_minutes 30 # minimum gap between claims on the same vehicle
set needs_tick_seconds 60             # how often hunger / thirst decay (needs-cs)
set shops_restock_minutes 5           # how often shop stock refills (shops-cs)
set dispatch_pay_per_call 150         # bank bonus for a resolved dispatch call (0 = none)
set dispatch_scene_radius 40          # metres: how close a unit must be to mark on scene / resolve
set dispatch_call_ttl_minutes 15      # calls left open this long expire
set dispatch_call_cooldown_seconds 20 # minimum gap between a player's 911 calls
set admin_bootstrap_license "xxxxxxxx"   # first SuperAdmin (admin-cs); keep it out of git
```

Placement commands (`/setshop`, `/setcustoms`, `/setfuel`, `/setworkhub`, `/atmscan`) and the dev
commands check ACE permissions, so you decide who gets them. On a real server, give them to a staff
group instead of everyone:

```cfg
add_ace group.admin garage.admin allow
add_ace group.admin jobs.admin allow
add_ace group.admin spawn.admin allow
add_ace group.admin bank.admin allow
add_ace group.admin inventory.admin allow
add_ace group.admin shops.admin allow
add_ace group.admin needs.admin allow
add_ace group.admin customs.admin allow
add_ace group.admin fuel.admin allow
add_ace group.admin dispatch.admin allow
add_ace group.admin dispatch.test allow
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
| *Use* `lockpick` | inventory-cs + garage-cs | Hotwire a registered car that belongs to someone else (8 s, consumes the lockpick) | Use a lockpick from the inventory next to the car |
| `/insurance` | garage-cs | Lists your stored cars with the estimated insurance payout | `/insurance` |
| `/claim <vehicle id>` | garage-cs | Repairs a stored, damaged car and pays the settlement to your bank | `/claim 4` |
| `/jobs` | jobs-cs | Lists available jobs | `/jobs` |
| `/job` | jobs-cs | Shows your job, grade, salary and duty state | `/job` |
| `/duty` | jobs-cs | Toggles duty (you only get paychecks on duty) | `/duty` |
| `/quitjob` | jobs-cs | Become unemployed | `/quitjob` |
| `/work` | jobs-cs | Starts the activity for your job (courier deliveries or taxi fares); must be on duty at the hub | `/work` |
| `/work stop` | jobs-cs | Cancels the run you are on | `/work stop` |
| `/work stats` | jobs-cs | Shows completed runs, earnings and field-action totals | `/work stats` |
| `/fine <id> <amount> <reason>` | jobs-cs | On-duty police: fines a nearby player from their bank | `/fine 4 500 reckless driving` |
| `/revive <id>` | jobs-cs | On-duty EMS: revives a downed player within 4 m | `/revive 4` |
| `/repair` | jobs-cs | On-duty mechanic: repairs the nearest vehicle (uses a repair kit) | `/repair` |
| `/impound` | jobs-cs | On-duty police: impounds the nearest owned vehicle | `/impound` |
| `/inventory` | inventory-cs | Opens the inventory (key **I**) | `/inventory` |
| `/phone` | phone-cs | Opens the phone (key **F1**) | `/phone` |
| `/911 <message>` | dispatch-cs | Calls police dispatch (the phone's Emergency tab does the same) | `/911 Help at Legion Square` |
| `/912 <message>` | dispatch-cs | Calls medical dispatch | `/912 Cyclist down on the beach` |
| `/cancelcall` | dispatch-cs | Cancels your active call | `/cancelcall` |
| `/dispatch` | dispatch-cs | Opens / closes the dispatch console (key **F2**) | `/dispatch` |
| `/car [model]` | spawn-core | Dev helper: spawns a vehicle and seats you in it (requires `spawn.admin`) | `/car sultan` |
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
| `/theftcar <model>` | `garage.admin` | Spawns a locked car owned by `test:theft`, so you can test hotwiring solo | `/theftcar sultan` |
| `/giveitem <id> <item> [count]` | `inventory.admin` | Gives an item. Items: water, sandwich, bandage, lockpick, repair_kit, radio | `/giveitem 1 sandwich 3` |
| `/setjob <id> <job> [grade]` | `jobs.admin` | Sets a job. Jobs: unemployed, courier, police, ems, mechanic, taxi | `/setjob 1 mechanic 1` |
| `/setworkhub <courier\|taxi>` | `jobs.admin` | Moves a job activity start point (hub) to where you stand | `/setworkhub courier` |
| `/setneeds <hunger> <thirst>` | `needs.admin` | Sets your own hunger and thirst (0-100) | `/setneeds 20 10` |
| `/setshop <general\|hardware>` | `shops.admin` | Places a shop where you stand | `/setshop general` |
| `/setcustoms` | `customs.admin` | Places a customs bay where you stand | `/setcustoms` |
| `/setfuel` | `fuel.admin` | Places a fuel station where you stand | `/setfuel` |
| `/atmscan` | `bank.admin` | Registers ATM props near you as valid ATM locations | `/atmscan` |
| `/dispatchtest <police\|medical> [message]` | `dispatch.test` | Creates a test call at your position (useful for a solo dispatch test) | `/dispatchtest police Robbery in progress` |
| `/dispatchclose <call id>` | `dispatch.admin` / `dispatch.test` | Force-closes an active call | `/dispatchclose 12` |
| `/atmdebug` | none | Prints the ATMs the client can currently see | `/atmdebug` |

### Keybinds

| Key | Action |
|---|---|
| **E** | Interact: use an ATM, open a shop, enter a customs bay, refuel (the prompt shows when it applies) |
| **I** | Inventory |
| **F1** | Phone |
| **F2** | Dispatch console |
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
7. **A shift.** `/setjob <your id> courier 0` (or `taxi 0`), `/duty`, drive to the Go Postal / cab hub marked on the map and run `/work`. Follow the GPS stop to stop; the HUD shows the timer, distance and payout. Finish at the last stop to get paid in cash, then try `/work stats`.
8. **Dispatch.** `/setjob <your id> police 0`, `/duty`, then press **F2** to open the dispatch console. Normally a civilian dials 911 or 912 from the phone's Emergency tab; for a solo test run `/dispatchtest police Robbery in progress` to place a call at your feet. Claim it, mark **En route**, mark **On scene**, then close it as **Resolved**: $150 is paid to your bank. `/dispatchtest medical ...` proves the medical queue is separate.
9. **Field actions.** As on-duty police, stand next to a player and `/fine <id> 250 speeding`; with an owned car out, stand next to it and `/impound` (it returns to `/garage`). Switch to `ems 0` and `/duty` to `/revive <id>` a downed player, or `mechanic 1` and `/duty` to `/repair` a nearby vehicle with a repair kit from a shop. Each action is written to `job_actions` and visible in `/work stats`.
10. **Items.** Place a shop with `/setshop general`, buy water, a sandwich and a lockpick, open the inventory with **I** and drag them around. Use them and watch your hunger and thirst bars recover.
11. **Theft.** With someone else's owned car out, stand next to it, open the inventory and use the lockpick. Eight seconds later (`garage_hotwire_seconds`) it is hotwired and `stolen`; the owner is told in chat and `/garage` shows `STOLEN`. As on-duty police, `/impound` that car: it is recovered, the flag is cleared and it is back in the owner's garage. A stolen car can't be `/park`ed.
12. **Insurance.** Crash a car, `/park` it, then `/insurance` to see the quoted settlement and `/claim <id>` to have it repaired and the money paid to your bank. The same car can't be claimed again for 30 minutes, and a stolen car is excluded until the police recover it.
13. **Phone.** Press **F1**. Add a contact, send a text to a friend's number, and pay them from the Bank tab.
14. **Staff.** `/setrole <id> mod`, then `/players`, `/goto` and `/adminlog`.

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
  state, a `stolen` flag, engine and body health, plus `mods` and `fuel` columns used by customs
  and fuel). The table is created on start; see `sql/install.sql`.
- **Atomic take-out.** `UPDATE ... WHERE id = ? AND owner = ? AND state = 1` checks ownership
  *and* that the car is stored in one statement, so two simultaneous requests can never both
  spawn the same vehicle.
- **Server-verified parking (OneSync).** `/park` sends only a network id and the condition.
  The server resolves the real entity, reads its plate, checks that this player took that
  plate out, and only then stores and deletes it. A stolen car can't be parked away.
- **Hotwire theft.** Using a `lockpick` (`inventory-cs`) next to someone else's car sends only
  the vehicle's network id. The server resolves the real entity, checks the plate is registered,
  out, owned by someone else and unoccupied, consumes the lockpick, then runs the
  `garage_hotwire_seconds` timer itself before setting `stolen = 1`. The client can't skip the
  wait or claim a car it isn't standing next to. The owner is told in chat to call 911; police
  `/impound` (jobs-cs) recovers the car, which clears the flag and returns it to the garage.
  `/garage` shows `STOLEN (call the police)` while it is out.
- **Insurance claims.** `/insurance` quotes a settlement for each stored car from the damage saved
  on its row; `/claim <id>` repairs it and pays the settlement to the player's bank. The quote is
  `2 × lost health points × insurance_payout_percent` minus `insurance_deductible`, capped by
  `insurance_payout_max`, with a per-vehicle `insurance_claim_cooldown_minutes` cooldown. Stored,
  non-stolen cars only; the update matches the exact damage the quote used and each claim is
  appended to `insurance_claims` with a `paid` flag.
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

Requires `oxmysql`, a `mysql_connection_string` convar, and OneSync. `inventory-cs` is optional:
without it the garage still works, but lockpicks can't start a hotwire.

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
- **Emergency calls.** The Emergency tab dials 911 (police) or 912 (medical). The phone passes only the
  text to `dispatch-cs`; dispatch reads the caller's position itself, so the call can't point somewhere else.
- Contacts can only be deleted by their owner (`DELETE ... WHERE id = ? AND owner = ?`). User text is
  rendered with `textContent`, never as HTML.

Open with **F1** or `/phone`. Requires `oxmysql` and `bank-cs`.

### dispatch-cs

A dispatch radio that turns a 911 call into a claimable, server-validated job for police and EMS.

- **The call is created by the server.** A civilian dials 911/912 from the phone's Emergency tab (or uses
  `/911` / `/912`). `phone-cs` calls dispatch's server-only `createCall` export; the client never sends
  a location. Dispatch reads the caller's OneSync position, maps it to the nearest named district and
  stores the call in `dispatch_calls`.
- **Only the right department sees it.** Every broadcast asks `jobs-cs` `isOnDuty(src, job)`, so a call
  is offered only to on-duty police (police calls) or EMS (medical calls). `dispatch.admin` can see both
  queues. Going off duty, changing job or disconnecting releases that unit's calls.
- **Claiming is atomic.** `UPDATE ... WHERE id = ? AND status = 'open'` means two units racing for the
  same call cannot both win; the loser is told it was taken.
- **Positions are checked on the server.** "On scene" requires the server's position to be within
  `dispatch_scene_radius` (40 m) of the call, and the time since the claim must be enough for a vehicle
  to cover the distance (65 m/s cap, 2 s grace). "Resolved" requires the same presence check, so a
  modified client cannot close calls from across the map. No partial state changes: the database row
  is only moved forward after the check passes.
- **A complete lifecycle.** `open → claimed → enroute → onscene → closed`, with outcomes
  `resolved`, `false_alarm`, `no_response`, `cancelled` (caller) or `expired` (TTL). Active calls live
  in memory; every call is a row in `dispatch_calls`.
- **Pay through the bank.** A resolved call pays `dispatch_pay_per_call` (default $150) to the unit's
  bank through `bank-cs`' server-only `addBank`. If the bank is unavailable the row's payout is set to 0
  and the unit is told, so the database never claims money that was not paid.
- **Console.** Press **F2** or `/dispatch`: a focused NUI panel lists the calls the player may see, with
  age, distance, caller, status, and only the actions that are valid for that call. Claiming sets a GPS
  route and a blip that is cleared on release or close. The page renders with `textContent` only.
- **Test hook.** `/dispatchtest <police|medical> [message]` (ACE `dispatch.test`) places a synthetic
  call at your feet, so one client can exercise the whole flow; `/dispatchclose <id>` force-closes one.

Requires `oxmysql`, `jobs-cs`, OneSync and `bank-cs` for payouts. `phone-cs` is optional: without it
calls still work through `/911` and `/912`. Convars: `dispatch_pay_per_call`, `dispatch_scene_radius`,
`dispatch_call_ttl_minutes`, `dispatch_call_cooldown_seconds`.

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

Jobs with grades, a duty toggle, automatic paychecks, repeatable activities and on-duty field actions, built to cooperate with the bank.

- **Cross-resource integration.** Paychecks are paid with `exports['bank-cs']:addBank(...)`.
  It's a *server* export, so a client can't call it to give itself money, and the bank
  re-validates the amount and the player on every call instead of trusting the caller.
- **Definitions in code, state in the database.** The DB stores only `job` and `grade`;
  labels, salaries and activity routes come from the server-side definitions, so changing a
  salary needs no migration, and a job removed from the config falls back to unemployed instead of crashing.
- **Pay only while on duty.** `/duty` toggles it; duty resets on disconnect, on server restart
  and whenever a job changes, so nobody is paid for a role they no longer hold. Going off duty
  also cancels an active run.
- **Activities that cannot be faked.** `/work` starts the run for your job (courier deliveries
  or taxi fares) when you are on duty and standing at that job's hub. The client only draws the
  blips, marker and HUD and says "I reached this stop"; the server re-reads the real position
  (OneSync), requires a vehicle, rejects stops that are out of range, and rejects a claim that
  covered the distance faster than a vehicle can (65 m/s), so teleporting onto a marker does not
  complete the run. A server-side timer expires a run that takes too long.
- **Server-owned payout.** Completion pay is calculated from the route distance, a per-activity
  rate and the player's grade, then paid in cash through bank-cs' server-only `addMoney`.
  `/work stop` cancels the run and `/work stats` reads the player's completed-run totals.
- **Field actions while on duty.** Police `/fine` a nearby player (the bank's atomic
  check-and-deduct makes the charge all-or-nothing) and `/impound` an owned vehicle (the row is
  moved to stored only if it was still out, and `garage-cs` deletes the live entity). EMS
  `/revive` reads the patient's real ped health and pays only after the patient's client
  resurrects them and the server re-checks the result. Mechanics `/repair` consume a
  `repair_kit` through inventory-cs and pay through bank-cs. Every action re-checks job, duty
  and the server-side distance; `/impound` and `/repair` resolve the client's network id back
  to the real vehicle before doing anything. Impounding a car flagged `stolen` by the garage
  clears that flag, returns it to the owner's garage and tells both players it was recovered.
  Each action is appended to `job_actions`.
- **Hubs placed in-game.** `/setworkhub <courier|taxi>` moves a start point to where an admin is
  standing and saves it to `workhubs.txt`, so no coordinates have to be typed by hand.
- **History, not live state.** Active runs live in memory because they belong to a session; every
  completed, cancelled, expired or abandoned run is appended to `job_runs` with its route, seconds,
  payout and status.
- **Exports for other resources:** `getJob`, `getGrade`, `isOnDuty(src, job)`
  (synchronous, served from a per-player cache that lives only while they're online).

Jobs and grades: courier (Courier, Senior Courier), police (Cadet, Officer, Sergeant),
ems (Trainee, Paramedic, Chief), mechanic (Apprentice, Mechanic), taxi (Driver), unemployed.
Requires `oxmysql`, `bank-cs`, `inventory-cs` and OneSync. `garage-cs` is needed for `/impound`
to remove the live vehicle (the database row is still updated without it). Set
`jobs_require_vehicle 0` to allow activities on foot.

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

- The client reports its position every 5 s; the server rejects malformed values and anything
  outside the map, and compares the report with the player's real position (OneSync) — if they
  disagree by more than a few metres, the server's position is the one it keeps. Positions are
  stored per license and flushed every minute and on disconnect.
- On spawn the client asks for the saved position and teleports there, waiting for map collision
  to load first so the player doesn't fall through the world.
- `/car [model]` is ACE-gated (`spawn.admin`). The client asks the server; only an authorised
  player gets the event back that spawns a validated vehicle and seats them in it.
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

The server-side helpers used by `jobs-cs` and `dispatch-cs` live in [`shared/Server/PortfolioScript.cs`](shared/Server/PortfolioScript.cs)
and are linked into the resource at compile time, so adopting them needs no extra assembly or
runtime dependency. A GitHub Actions workflow builds every resource with `setup.ps1` on each push.

## License

MIT, see [LICENSE](LICENSE).
