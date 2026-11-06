using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Garage.Server
{
    /// <summary>
    /// Persistent vehicle garage. The database is the source of truth; the client only
    /// asks to spawn or park, and the server checks ownership and state before anything happens.
    /// </summary>
    public class GarageServer : BaseScript
    {
        // Only these models can ever be granted. The client is never asked what a vehicle is.
        static readonly HashSet<string> AllowedModels = new HashSet<string>
        {
            "adder", "sultan", "buffalo", "comet2", "kuruma", "faggio", "bati", "sandking", "stanier", "baller"
        };

        const int MaxVehicles = 10;
        const long CooldownMs = 700;
        const string PlateChars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
        const string TestOwner = "test:theft";
        const float HotwireRange = 5.0f;

        readonly Random _rng = new Random();
        readonly Dictionary<string, HashSet<string>> _out = new Dictionary<string, HashSet<string>>(); // license -> plates in the world
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        // Licenses with a hotwire attempt in progress. The attempt is timed on the server; the
        // client just waits and watches, so a modified client cannot skip the wait.
        readonly HashSet<string> _hotwires = new HashSet<string>();
        int _hotwireSeconds;

        // Insurance: the insurer repairs a stored vehicle and pays a settlement based on the
        // damage recorded when it was parked. Values are bounded convars; all maths is here.
        int _insurancePercent;
        int _insuranceMax;
        int _insuranceDeductible;
        long _insuranceCooldownSeconds;
        const int MinClaim = 25;

        // Live state of vehicles that are out in the world, keyed by plate. The server is the source of truth;
        // clients are told what to apply. All of it is cleared when a vehicle is parked or its owner leaves.
        readonly Dictionary<string, int> _netIds = new Dictionary<string, int>(); // plate -> network id
        readonly HashSet<string> _locked = new HashSet<string>();
        readonly HashSet<string> _engineOff = new HashSet<string>();
        const float InteractRange = 15f;

        // A player can only work one lock at a time.
        public GarageServer()
        {
            Cmd("locate", Locate);
            Cmd("givecar", GiveCar);
            Cmd("garage", ListGarage);
            Cmd("takeout", TakeOut);
            Cmd("theftcar", TheftCar);
            Cmd("insurance", ListInsurance);
            Cmd("claim", Claim);

            // Server-only: jobs-cs validates and records a police impound, then asks the garage to
            // remove the live entity and its cache so the vehicle can be taken out again later.
            Exports.Add("impound", new Func<string, bool>(Impound));

            // Server-only: dealer-cs has already inserted the owned row for a purchase; this
            // takes it out through the normal path so tracking, /park, /lock and /locate work.
            Exports.Add("deliver", new Func<int, string, bool>(Deliver));

            _hotwireSeconds = Math.Max(2, Math.Min(60, GetConvarInt("garage_hotwire_seconds", 8)));
            _insurancePercent = Math.Max(0, Math.Min(100, GetConvarInt("insurance_payout_percent", 50)));
            _insuranceMax = Math.Max(0, Math.Min(100000, GetConvarInt("insurance_payout_max", 2500)));
            _insuranceDeductible = Math.Max(0, Math.Min(10000, GetConvarInt("insurance_deductible", 250)));
            _insuranceCooldownSeconds = Math.Max(0, Math.Min(10080, GetConvarInt("insurance_claim_cooldown_minutes", 30))) * 60L;

            try
            {
                Db("CREATE TABLE IF NOT EXISTS player_vehicles (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, owner VARCHAR(64) NOT NULL, model VARCHAR(32) NOT NULL, " +
                   "plate VARCHAR(8) NOT NULL UNIQUE, state TINYINT NOT NULL DEFAULT 1, " +
                   "engine INT NOT NULL DEFAULT 1000, body INT NOT NULL DEFAULT 1000, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX idx_owner (owner))",
                   new object[0], _ =>
                       // "mods" is opaque extension data written by other resources (e.g. customs-cs);
                       // the garage only stores it and passes it on when the vehicle spawns.
                       Db("ALTER TABLE player_vehicles ADD COLUMN IF NOT EXISTS mods VARCHAR(512) NULL", new object[0], __ =>
                           // A stolen car is still out in the world until the police recover it.
                           Db("ALTER TABLE player_vehicles ADD COLUMN IF NOT EXISTS stolen TINYINT NOT NULL DEFAULT 0", new object[0], ___ =>
                               // After a restart nobody's cars are in the world any more: return them all to the garage.
                               Db("UPDATE player_vehicles SET state = 1, stolen = 0 WHERE state = 0", new object[0],
                                  n => Debug.WriteLine($"[garage] ready, {n} vehicle(s) returned to garage")))));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[garage] database unavailable (is oxmysql started?): " + e.Message);
            }

            try
            {
                Db("CREATE TABLE IF NOT EXISTS insurance_claims (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, plate VARCHAR(8) NOT NULL, " +
                   "model VARCHAR(32) NOT NULL, amount INT NOT NULL, paid TINYINT NOT NULL DEFAULT 0, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "INDEX idx_plate (plate), INDEX idx_license (license))", new object[0],
                   _ => Debug.WriteLine("[garage] insurance ready"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[garage] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ---------- database wrappers around oxmysql ----------

        void Query(string sql, object[] args, Action<List<IDictionary<string, object>>> done)
        {
            Exports["oxmysql"].query(sql, args, new Action<dynamic>(res =>
            {
                var rows = new List<IDictionary<string, object>>();
                if (res != null)
                    foreach (var r in res) rows.Add((IDictionary<string, object>)r);
                done(rows);
            }));
        }

        // Runs a statement; the callback gets the affected row count (or insert id for INSERTs).
        void Db(string sql, object[] args, Action<int> done)
        {
            Exports["oxmysql"].update(sql, args, new Action<dynamic>(n => done(n == null ? 0 : Convert.ToInt32(n))));
        }

        void Insert(string sql, object[] args, Action<int> done)
        {
            Exports["oxmysql"].insert(sql, args, new Action<dynamic>(id => done(id == null ? 0 : Convert.ToInt32(id))));
        }

        // ---------- commands ----------

        void GiveCar(Player p, List<object> args)
        {
            // Admin-only. In production this would be a real permission, not a dev ACE.
            if (!IsPlayerAceAllowed(p.Handle, "garage.admin")) { Say(p, "You are not allowed to use that."); return; }

            string model = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;
            if (model == null || !AllowedModels.Contains(model))
            {
                Say(p, "Usage: /givecar <" + string.Join("|", AllowedModels.OrderBy(m => m)) + ">");
                return;
            }

            string license = License(p);
            if (license == null) { Say(p, "No license identifier found."); return; }

            Query("SELECT COUNT(*) AS c FROM player_vehicles WHERE owner = ?", new object[] { license }, rows =>
            {
                if (Convert.ToInt32(rows[0]["c"]) >= MaxVehicles) { Say(p, $"You already own {MaxVehicles} vehicles."); return; }

                string plate = NewPlate();
                Insert("INSERT INTO player_vehicles (owner, model, plate) VALUES (?, ?, ?)",
                    new object[] { license, model, plate }, id =>
                    {
                        if (id == 0) { Say(p, "Could not create the vehicle, try again."); return; }
                        Debug.WriteLine($"[garage] {p.Name} ({license}) was given {model} [{plate}] (id {id})");
                        Say(p, $"Added {model} [{plate}] to your garage. Use /takeout {id}.");
                    });
            });
        }

        void ListGarage(Player p, List<object> args)
        {
            string license = License(p);
            if (license == null) return;

            Query("SELECT id, model, plate, state, stolen FROM player_vehicles WHERE owner = ? ORDER BY id", new object[] { license }, rows =>
            {
                if (rows.Count == 0) { Say(p, "Your garage is empty."); return; }
                Say(p, "Your vehicles (use /takeout <id>):");
                foreach (var r in rows)
                {
                    bool stored = Convert.ToInt32(r["state"]) == 1;
                    bool stolen = Convert.ToInt32(r["stolen"]) == 1;
                    string state = stored ? "in garage" : (stolen ? "STOLEN (call the police)" : "OUT");
                    Say(p, $"#{r["id"]}  {r["model"]}  [{r["plate"]}]  {state}");
                }
            });
        }

        void TakeOut(Player p, List<object> args)
        {
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0)
            {
                Say(p, "Usage: /takeout <vehicle id>  (see /garage)");
                return;
            }
            string license = License(p);
            if (license == null) return;

            // One atomic statement checks ownership AND that it's stored, then flips it.
            // Two simultaneous requests can never both succeed: only one sees affected == 1.
            Db("UPDATE player_vehicles SET state = 0 WHERE id = ? AND owner = ? AND state = 1", new object[] { id, license }, affected =>
            {
                if (affected != 1) { Say(p, "That vehicle doesn't exist, isn't yours, or is already out."); return; }

                Query("SELECT model, plate, engine, body, mods FROM player_vehicles WHERE id = ?", new object[] { id }, rows =>
                {
                    var v = rows[0];
                    string plate = v["plate"].ToString();
                    Track(license).Add(plate);
                    TriggerClientEvent(p, "garage:spawn", v["model"].ToString(), plate, Convert.ToInt32(v["engine"]), Convert.ToInt32(v["body"]),
                                       v["mods"] == null ? "" : v["mods"].ToString());
                    Debug.WriteLine($"[garage] {p.Name} took out #{id} [{plate}]");
                });
            });
        }

        // Dev helper: a car registered to a fake owner, spawned next to you. It is a normal
        // player_vehicles row, so a hotwire and a police /impound behave exactly like real theft.
        void TheftCar(Player p, List<object> args)
        {
            if (!IsPlayerAceAllowed(p.Handle, "garage.admin")) { Say(p, "You are not allowed to use that."); return; }

            string model = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;
            if (model == null || !AllowedModels.Contains(model))
            {
                Say(p, "Usage: /theftcar <" + string.Join("|", AllowedModels.OrderBy(m => m)) + ">");
                return;
            }

            string plate = NewPlate();
            Insert("INSERT INTO player_vehicles (owner, model, plate, state) VALUES (?, ?, ?, 0)",
                new object[] { TestOwner, model, plate }, id =>
                {
                    if (id == 0) { Say(p, "Could not create the test vehicle."); return; }
                    TriggerClientEvent(p, "garage:theftCar", model, plate);
                    Say(p, $"Spawned {model} [{plate}] owned by '{TestOwner}'. Use a lockpick next to it.");
                    Debug.WriteLine($"[garage] dev theft car [{plate}] ({model}) spawned for {p.Name}");
                });
        }

        // ---------- insurance ----------

        // The settlement is server-calculated from the damage saved when the car was parked:
        // every lost health point counts as $2 of repair, the insurer pays a percentage, a
        // deductible is taken off and the result is capped. A claim repairs the stored car.
        int Quote(int engine, int body)
        {
            int repairCost = Math.Max(0, 1000 - engine) * 2 + Math.Max(0, 1000 - body) * 2;
            long settlement = (long)repairCost * _insurancePercent / 100 - _insuranceDeductible;
            if (settlement < 0) settlement = 0;
            if (settlement > _insuranceMax) settlement = _insuranceMax;
            return (int)settlement;
        }

        void ListInsurance(Player p, List<object> args)
        {
            string license = License(p);
            if (license == null) return;

            Say(p, $"Insurance: {_insurancePercent}% of the repair cost, ${_insuranceDeductible} deductible, " +
                   $"max ${_insuranceMax}; one claim per vehicle every {_insuranceCooldownSeconds / 60} min.");

            Query("SELECT id, model, plate, state, stolen, engine, body FROM player_vehicles WHERE owner = ? ORDER BY id",
                new object[] { license }, rows =>
                {
                    if (rows.Count == 0) { Say(p, "Your garage is empty."); return; }

                    foreach (var r in rows)
                    {
                        int id = Convert.ToInt32(r["id"]);
                        string model = r["model"].ToString(), plate = r["plate"].ToString();

                        if (Convert.ToInt32(r["state"]) != 1)
                        {
                            Say(p, $"#{id} {model} [{plate}] - out (store it before claiming)");
                            continue;
                        }
                        if (Convert.ToInt32(r["stolen"]) == 1)
                        {
                            Say(p, $"#{id} {model} [{plate}] - stolen (the police must recover it)");
                            continue;
                        }

                        int quote = Quote(Convert.ToInt32(r["engine"]), Convert.ToInt32(r["body"]));
                        Say(p, quote >= MinClaim
                            ? $"#{id} {model} [{plate}] - estimated claim ${quote}"
                            : $"#{id} {model} [{plate}] - no damage");
                    }
                });
        }

        void Claim(Player p, List<object> args)
        {
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0)
            {
                Say(p, "Usage: /claim <vehicle id>  (see /insurance)");
                return;
            }

            string license = License(p);
            if (license == null) return;

            Query("SELECT model, plate, state, stolen, engine, body FROM player_vehicles WHERE id = ? AND owner = ?",
                new object[] { id, license }, rows =>
                {
                    if (rows.Count == 0) { Say(p, "That vehicle is not yours."); return; }

                    var row = rows[0];
                    string model = row["model"].ToString(), plate = row["plate"].ToString();

                    if (Convert.ToInt32(row["state"]) != 1) { Say(p, "Store the vehicle before claiming."); return; }
                    if (Convert.ToInt32(row["stolen"]) == 1) { Say(p, "A stolen vehicle can't be claimed; the police must recover it first."); return; }

                    int engine = Convert.ToInt32(row["engine"]), body = Convert.ToInt32(row["body"]);
                    int quote = Quote(engine, body);
                    if (quote < MinClaim)
                    {
                        Say(p, $"There is not enough damage to claim (estimated ${quote}).");
                        return;
                    }

                    Query("SELECT COUNT(*) AS c FROM insurance_claims WHERE plate = ? AND TIMESTAMPDIFF(SECOND, created_at, NOW()) < ?",
                        new object[] { plate, _insuranceCooldownSeconds }, cooldownRows =>
                        {
                            int recent = cooldownRows.Count > 0 ? Convert.ToInt32(cooldownRows[0]["c"]) : 0;
                            if (recent > 0)
                            {
                                Say(p, $"This vehicle was claimed within the last {_insuranceCooldownSeconds / 60} min.");
                                return;
                            }

                            // Repair only if the car is still stored, still not stolen, and still
                            // has the exact damage this quote was calculated from.
                            Db("UPDATE player_vehicles SET engine = 1000, body = 1000 " +
                               "WHERE id = ? AND owner = ? AND state = 1 AND stolen = 0 AND engine = ? AND body = ?",
                               new object[] { id, license, engine, body }, affected =>
                               {
                                   if (affected != 1) { Say(p, "The vehicle changed; check /garage and try again."); return; }

                                   Insert("INSERT INTO insurance_claims (license, plate, model, amount) VALUES (?, ?, ?, ?)",
                                       new object[] { license, plate, model, quote }, rawClaimId =>
                                       {
                                           int claimId = Convert.ToInt32(rawClaimId);
                                           if (claimId <= 0)
                                           {
                                               Say(p, $"{model} [{plate}] was repaired, but the claim could not be recorded.");
                                               return;
                                           }

                                           bool paid = false;
                                           try { paid = (bool)Exports["bank-cs"].addBank(int.Parse(p.Handle), quote, "insurance claim"); }
                                           catch (Exception e) { Debug.WriteLine("[garage] insurance payout failed, bank-cs unavailable: " + e.Message); }

                                           if (paid)
                                               Db("UPDATE insurance_claims SET paid = 1 WHERE id = ?", new object[] { claimId }, _ => { });

                                           Say(p, paid
                                               ? $"Claim #{claimId}: {model} [{plate}] repaired, ${quote} paid to your bank."
                                               : $"Claim #{claimId}: {model} [{plate}] repaired, but the bank could not pay the settlement.");
                                           Debug.WriteLine($"[garage] insurance claim #{claimId} {license} [{plate}] ${quote} paid={paid}");
                                       });
                               });
                        });
                });
        }

        // ---------- events from the client ----------

        // The client reports the network id of the vehicle it just spawned. We only accept it if the real
        // entity carries the plate this player legitimately took out.
        [EventHandler("garage:spawned")]
        void OnSpawned([FromSource] Player p, int netId, string plate)
        {
            string license = p == null ? null : License(p);
            if (license == null || plate == null || !Track(license).Contains(plate)) return;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity)) return;
            if ((GetVehicleNumberPlateText(entity) ?? "").Trim() != plate) return;

            _netIds[plate] = netId;
        }

        // The dev theft car is spawned by the client too; track it so /impound can delete it
        // server-side. Only accepts a plate that really belongs to the test owner.
        [EventHandler("garage:theftSpawned")]
        void OnTheftSpawned([FromSource] Player p, int netId, string plate)
        {
            if (p == null || !IsPlayerAceAllowed(p.Handle, "garage.admin") || string.IsNullOrEmpty(plate)) return;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity)) return;
            if ((GetVehicleNumberPlateText(entity) ?? "").Trim() != plate) return;

            Query("SELECT owner FROM player_vehicles WHERE plate = ?", new object[] { plate }, rows =>
            {
                if (rows.Count == 1 && rows[0]["owner"].ToString() == TestOwner) _netIds[plate] = netId;
            });
        }

        [EventHandler("garage:lock")]
        void OnLock([FromSource] Player p, int netId)
        {
            if (p == null || RateLimited(int.Parse(p.Handle))) return;
            string plate;
            int entity = OwnedVehicle(p, netId, out plate);
            if (entity == 0) { Say(p, "You don't have the keys to that vehicle."); return; }

            // The player must actually be near it (server-side position).
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0 || Vector3.Distance(GetEntityCoords(ped), GetEntityCoords(entity)) > InteractRange) { Say(p, "You are too far from the vehicle."); return; }

            bool nowLocked = !_locked.Contains(plate);
            if (nowLocked) _locked.Add(plate); else _locked.Remove(plate);

            TriggerClientEvent(p, "garage:applyLock", netId, nowLocked);
            Say(p, $"[{plate}] {(nowLocked ? "locked" : "unlocked")}.");
        }

        [EventHandler("garage:engine")]
        void OnEngine([FromSource] Player p, int netId)
        {
            if (p == null || RateLimited(int.Parse(p.Handle))) return;
            string plate;
            int entity = OwnedVehicle(p, netId, out plate);
            if (entity == 0) { Say(p, "You don't have the keys to that vehicle."); return; }

            // Only the person sitting in that vehicle can work its engine.
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0 || GetVehiclePedIsIn(ped, false) != entity) { Say(p, "You need to be inside the vehicle."); return; }

            bool nowOn = _engineOff.Contains(plate);
            if (nowOn) _engineOff.Remove(plate); else _engineOff.Add(plate);

            TriggerClientEvent(p, "garage:applyEngine", netId, nowOn);
            Say(p, $"[{plate}] engine {(nowOn ? "on" : "off")}.");
        }

        // ---------- theft ----------

        // Using a lockpick reports the nearest vehicle. The server checks that it is a registered
        // car owned by someone else, that nobody is inside, and that the player is really next to
        // it; it then consumes the lockpick and runs the timed attempt itself.
        [EventHandler("garage:hotwire")]
        void OnHotwire([FromSource] Player p, int netId)
        {
            if (p == null || RateLimited(int.Parse(p.Handle))) return;
            string license = License(p);
            if (license == null) return;

            if (_hotwires.Contains(license)) { Say(p, "You are already working on a lock."); return; }

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity)) { Say(p, "No vehicle found."); return; }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            if (GetVehiclePedIsIn(ped, false) != 0) { Say(p, "Get out of the vehicle first."); return; }
            if (Vector3.Distance(GetEntityCoords(ped), GetEntityCoords(entity)) > HotwireRange)
            {
                Say(p, "Get closer to the vehicle.");
                return;
            }

            string plate = (GetVehicleNumberPlateText(entity) ?? "").Trim();
            if (plate.Length == 0) { Say(p, "That vehicle has no plate."); return; }

            Query("SELECT owner, model, state, stolen FROM player_vehicles WHERE plate = ?", new object[] { plate }, rows =>
            {
                if (rows.Count == 0) { Say(p, "That vehicle is not registered."); return; }

                var row = rows[0];
                string owner = row["owner"].ToString();
                string model = row["model"].ToString();

                if (Convert.ToInt32(row["state"]) == 1) { Say(p, "That vehicle is stored in a garage."); return; }
                if (owner == license) { Say(p, "That is your own vehicle."); return; }
                if (Convert.ToInt32(row["stolen"]) == 1) { Say(p, "That vehicle is already reported stolen."); return; }

                foreach (var other in Players.ToList())
                {
                    if (other == null) continue;
                    int otherPed = GetPlayerPed(other.Handle);
                    if (otherPed != 0 && GetVehiclePedIsIn(otherPed, false) == entity)
                    {
                        Say(p, "Someone is inside the vehicle.");
                        return;
                    }
                }

                bool hasKit = false;
                try { hasKit = (bool)Exports["inventory-cs"].hasItem(int.Parse(p.Handle), "lockpick", 1); }
                catch (Exception e) { Debug.WriteLine("[garage] inventory-cs unavailable: " + e.Message); }
                if (!hasKit) { Say(p, "You need a lockpick. Buy one at a shop."); return; }

                bool removed = false;
                try { removed = (bool)Exports["inventory-cs"].removeItem(int.Parse(p.Handle), "lockpick", 1); }
                catch (Exception e) { Debug.WriteLine("[garage] inventory-cs unavailable: " + e.Message); }
                if (!removed) { Say(p, "The lockpick could not be used."); return; }

                _hotwires.Add(license);
                Say(p, $"You start working the lock on the {model} [{plate}]...");
                _ = FinishHotwire(int.Parse(p.Handle), license, entity, plate, model, owner);
            });
        }

        async Task FinishHotwire(int src, string license, int entity, string plate, string model, string ownerLicense)
        {
            try
            {
                await Delay(_hotwireSeconds * 1000);
                _hotwires.Remove(license);

                var p = Players[src];
                if (p == null) return;

                int ped = GetPlayerPed(p.Handle);
                if (ped == 0 || !DoesEntityExist(entity)
                    || Vector3.Distance(GetEntityCoords(ped), GetEntityCoords(entity)) > HotwireRange)
                {
                    Say(p, "You moved away; the hotwire failed and the lockpick is spent.");
                    return;
                }

                // Only the first successful attempt wins, and a car can only be reported stolen once.
                Db("UPDATE player_vehicles SET stolen = 1 WHERE plate = ? AND stolen = 0 AND state = 0", new object[] { plate }, affected =>
                {
                    if (affected != 1) { Say(p, "The immobiliser refuses; try another vehicle."); return; }

                    _locked.Remove(plate);
                    _engineOff.Remove(plate);

                    TriggerClientEvent(p, "garage:hotwired", NetworkGetNetworkIdFromEntity(entity), plate);
                    Say(p, $"You hotwired the {model} [{plate}]. Drive it somewhere safe.");

                    var owner = FindByLicense(ownerLicense);
                    if (owner != null)
                        Say(owner, $"Your {model} [{plate}] was just stolen! Call 911.");

                    Debug.WriteLine($"[garage] {p.Name} hotwired [{plate}] ({model})");
                });
            }
            catch (Exception e)
            {
                _hotwires.Remove(license);
                Debug.WriteLine("[garage] hotwire error: " + e.Message);
            }
        }

        Player FindByLicense(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            foreach (var p in Players.ToList())
                if (p != null && License(p) == license) return p;
            return null;
        }

        // Resolves a client-supplied network id to a real vehicle entity owned (taken out) by this player.
        int OwnedVehicle(Player p, int netId, out string plate)
        {
            plate = null;
            string license = License(p);
            if (license == null) return 0;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity)) return 0;

            plate = (GetVehicleNumberPlateText(entity) ?? "").Trim();
            return Track(license).Contains(plate) ? entity : 0;
        }

        // Where are my vehicles? Positions are read from the real entities on the server.
        void Locate(Player p, List<object> args)
        {
            string license = License(p);
            if (license == null) return;

            var entries = new List<string>();
            foreach (var plate in Track(license))
            {
                int netId;
                if (!_netIds.TryGetValue(plate, out netId)) continue;
                int entity = NetworkGetEntityFromNetworkId(netId);
                if (entity == 0 || !DoesEntityExist(entity)) continue;

                var pos = GetEntityCoords(entity);
                entries.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1:F1},{2:F1},{3:F1}", plate, pos.X, pos.Y, pos.Z));
            }

            if (entries.Count == 0) { Say(p, "None of your vehicles are out in the world (see /garage)."); return; }
            TriggerClientEvent(p, "garage:locate", string.Join(";", entries));
        }

        void ForgetVehicle(string plate)
        {
            _netIds.Remove(plate);
            _locked.Remove(plate);
            _engineOff.Remove(plate);
        }

        /// <summary>
        /// Removes an impounded vehicle from the world. Ownership and the player_vehicles row are
        /// handled by the caller (jobs-cs); this clears the live entity and the in-memory tracking
        /// so a later /takeout spawns a fresh vehicle.
        /// </summary>
        bool Impound(string plate)
        {
            plate = (plate ?? "").Trim();
            if (plate.Length == 0) return false;

            if (_netIds.TryGetValue(plate, out int netId))
            {
                int entity = NetworkGetEntityFromNetworkId(netId);
                if (entity != 0 && DoesEntityExist(entity)) DeleteEntity(entity);
                ForgetVehicle(plate);
            }

            foreach (var set in _out.Values) set.Remove(plate);

            Debug.WriteLine($"[garage] impounded [{plate}] removed from the world");
            return true;
        }

        /// <summary>
        /// Hands a newly bought, stored vehicle to its owner. The dealer has already inserted the
        /// owned row; this moves it from stored to out and reuses the normal spawn path, so
        /// ownership tracking, /park, /lock, /locate and theft behave exactly as after /takeout.
        /// A server-only export, so a client can never hand itself a car.
        /// </summary>
        bool Deliver(int src, string plate)
        {
            plate = (plate ?? "").Trim();
            if (plate.Length == 0) return false;

            var p = Players[src];
            if (p == null) return false;
            string license = License(p);
            if (license == null) return false;

            Db("UPDATE player_vehicles SET state = 0 WHERE plate = ? AND owner = ? AND state = 1 AND stolen = 0",
                new object[] { plate, license }, affected =>
                {
                    if (affected != 1)
                    {
                        Debug.WriteLine($"[garage] deliver [{plate}] refused (not stored or not {license})");
                        return;
                    }

                    // If the buyer dropped between the request and this callback, put the car back
                    // in the garage instead of leaving it marked "out" with nobody tracking it.
                    if (Players[src] == null)
                    {
                        Db("UPDATE player_vehicles SET state = 1 WHERE plate = ? AND owner = ? AND state = 0",
                            new object[] { plate, license }, _ => { });
                        return;
                    }

                    Query("SELECT model, plate, engine, body, mods FROM player_vehicles WHERE plate = ? AND owner = ?",
                        new object[] { plate, license }, rows =>
                        {
                            if (rows.Count != 1) return;
                            var v = rows[0];
                            Track(license).Add(plate);
                            TriggerClientEvent(p, "garage:spawn", v["model"].ToString(), plate,
                                Convert.ToInt32(v["engine"]), Convert.ToInt32(v["body"]),
                                v["mods"] == null ? "" : v["mods"].ToString());
                            Debug.WriteLine($"[garage] delivered [{plate}] to {license}");
                        });
                });

            return true;
        }

        [EventHandler("garage:park")]
        void OnPark([FromSource] Player p, int netId, int engine, int body)
        {
            if (p == null || RateLimited(int.Parse(p.Handle))) return;
            string license = License(p);
            if (license == null) return;

            // OneSync lets the server look at the real entity instead of trusting the client's description of it.
            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity)) { Say(p, "No vehicle found."); return; }

            string plate = (GetVehicleNumberPlateText(entity) ?? "").Trim();
            if (!Track(license).Contains(plate)) { Say(p, "That is not one of your vehicles."); return; }

            engine = Clamp(engine); body = Clamp(body);
            Db("UPDATE player_vehicles SET state = 1, engine = ?, body = ? WHERE plate = ? AND owner = ? AND state = 0 AND stolen = 0",
               new object[] { engine, body, plate, license }, affected =>
               {
                   if (affected != 1)
                   {
                       Say(p, "Could not store that vehicle (a stolen vehicle has to be recovered by the police).");
                       return;
                   }
                   Track(license).Remove(plate);
                   ForgetVehicle(plate);
                   if (DoesEntityExist(entity)) DeleteEntity(entity);
                   Say(p, $"[{plate}] stored in your garage.");
                   Debug.WriteLine($"[garage] {p.Name} parked [{plate}] (engine {engine}, body {body})");
               });
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;
            _lastUse.Remove(int.Parse(p.Handle));
            _hotwires.Remove(license);

            // A player who leaves with cars out must not leave them marked "out" forever.
            if (_out.TryGetValue(license, out var plates))
            {
                foreach (var plate in plates)
                {
                    ForgetVehicle(plate);
                    Db("UPDATE player_vehicles SET state = 1, stolen = 0 WHERE plate = ? AND owner = ?", new object[] { plate, license }, _ => { });
                }
                _out.Remove(license);
            }
        }

        // ---------- helpers ----------

        HashSet<string> Track(string license)
        {
            if (!_out.TryGetValue(license, out var set)) _out[license] = set = new HashSet<string>();
            return set;
        }

        static int Clamp(int v) => v < 0 ? 0 : v > 1000 ? 1000 : v;

        string NewPlate()
        {
            var chars = new char[8];
            for (int i = 0; i < chars.Length; i++) chars[i] = PlateChars[_rng.Next(PlateChars.Length)];
            return new string(chars);
        }

        static string License(Player p)
        {
            string id = p.Identifiers["license"];
            return string.IsNullOrEmpty(id) ? null : id;
        }

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[garage] /{name} is in-game only"); return; }
                var p = Players[src];
                if (p == null || RateLimited(src)) return;
                handler(p, args);
            }), false);
        }

        bool RateLimited(int src)
        {
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < CooldownMs) return true;
            _lastUse[src] = now;
            return false;
        }

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 90, 160, 255 }, args = new[] { "Garage", msg } });
    }
}
