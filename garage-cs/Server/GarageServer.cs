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

        // Vehicle keys. A grant belongs to the stable player_vehicles.id, never a plate, so a
        // re-registered plate can't inherit it. The rows in vehicle_keys are the truth; these
        // caches mirror them so lock/engine can be answered without a query, and every grant,
        // revoke and startup load keeps them in step.
        class VehicleRef
        {
            public int Id;
            public string Owner;
            public string Model;
        }

        readonly Dictionary<string, VehicleRef> _vehicles = new Dictionary<string, VehicleRef>();     // plate -> row
        readonly Dictionary<int, VehicleRef> _vehiclesById = new Dictionary<int, VehicleRef>();       // id -> row
        readonly Dictionary<int, HashSet<string>> _grants = new Dictionary<int, HashSet<string>>();   // vehicle id -> grantees
        readonly Dictionary<string, string> _heldBy = new Dictionary<string, string>();               // plate -> taker licence
        int _maxKeysPerVehicle;

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
            Cmd("keys", ListKeys);
            Cmd("givekey", GiveKey);
            Cmd("takekey", TakeKey);
            Cmd("shared", ListShared);

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
            _maxKeysPerVehicle = Math.Max(1, Math.Min(20, GetConvarInt("garage_max_keys", 5)));

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
                                  n =>
                                  {
                                      Debug.WriteLine($"[garage] ready, {n} vehicle(s) returned to garage");
                                      // Only now is player_vehicles certainly present, so the key table can
                                      // reference it and the cache can be loaded in one ordered step.
                                      SetupKeys();
                                  }))));
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

        // ---------- keys: schema and cache ----------

        // Ran once the vehicle table is definitely there. The foreign key makes a sale (which
        // deletes the player_vehicles row) take its grants with it, so a later car can never
        // inherit somebody else's keys by reusing an id.
        void SetupKeys()
        {
            Db("CREATE TABLE IF NOT EXISTS vehicle_keys (" +
               "id INT AUTO_INCREMENT PRIMARY KEY, " +
               "vehicle_id INT NOT NULL, owner VARCHAR(64) NOT NULL, grantee VARCHAR(64) NOT NULL, " +
               "owner_name VARCHAR(64) NULL, grantee_name VARCHAR(64) NULL, " +
               "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
               "UNIQUE KEY uq_vehicle_grantee (vehicle_id, grantee), " +
               "INDEX idx_grantee (grantee), INDEX idx_owner (owner), " +
               "CONSTRAINT fk_keys_vehicle FOREIGN KEY (vehicle_id) REFERENCES player_vehicles(id) ON DELETE CASCADE)",
               new object[0], _ =>
               {
                   Debug.WriteLine("[garage] keys table ready");
                   LoadKeyCache();
               });
        }

        // Read every owned row and every grant once, so a key action can be answered from memory
        // and a revocation is effective on the very next action, not just the next respawn.
        void LoadKeyCache()
        {
            Query("SELECT id, owner, model, plate FROM player_vehicles", new object[0], rows =>
            {
                foreach (var row in rows)
                {
                    var v = new VehicleRef
                    {
                        Id = Convert.ToInt32(row["id"]),
                        Owner = row["owner"].ToString(),
                        Model = row["model"].ToString()
                    };
                    _vehicles[row["plate"].ToString()] = v;
                    _vehiclesById[v.Id] = v;
                }

                Query("SELECT vehicle_id, grantee FROM vehicle_keys", new object[0], grants =>
                {
                    foreach (var row in grants)
                    {
                        int id = Convert.ToInt32(row["vehicle_id"]);
                        if (!_grants.TryGetValue(id, out var set)) _grants[id] = set = new HashSet<string>();
                        set.Add(row["grantee"].ToString());
                    }

                    int total = _grants.Values.Sum(set => set.Count);
                    Debug.WriteLine($"[garage] key cache: {_vehicles.Count} vehicle(s), {total} grant(s)");
                });
            });
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

        // ---------- keys: sharing ----------

        // Owners see who holds a key to each car. A single id (or none) is enough: /keys and
        // /givekey use the same vehicle id the garage already prints.
        void ListKeys(Player p, List<object> args)
        {
            string license = License(p);
            if (license == null) return;

            int only = 0;
            if (args.Count > 0 && (!int.TryParse(args[0]?.ToString(), out only) || only <= 0))
            {
                Say(p, "Usage: /keys [vehicle id]");
                return;
            }

            Query("SELECT v.id, v.model, v.plate, k.grantee_name, k.grantee " +
                  "FROM player_vehicles v LEFT JOIN vehicle_keys k ON k.vehicle_id = v.id " +
                  "WHERE v.owner = ?" + (only > 0 ? " AND v.id = ?" : "") + " ORDER BY v.id, k.id",
                only > 0 ? new object[] { license, only } : new object[] { license }, rows =>
                {
                    if (rows.Count == 0) { Say(p, only > 0 ? "That vehicle is not yours." : "Your garage is empty."); return; }

                    int current = 0;
                    foreach (var r in rows)
                    {
                        int id = Convert.ToInt32(r["id"]);
                        if (id != current)
                        {
                            current = id;
                            Say(p, $"#{id}  {r["model"]}  [{r["plate"]}]");
                        }

                        if (string.IsNullOrEmpty(r["grantee"]?.ToString())) continue; // the LEFT JOIN produced no grant
                        string name = string.IsNullOrEmpty(r["grantee_name"]?.ToString())
                            ? "a player" : r["grantee_name"].ToString();
                        Say(p, $"    key: {name}");
                    }

                    Say(p, "Give: /givekey <vehicle id> <player id or name>. Take back: /takekey <vehicle id> <player>.");
                });
        }

        void GiveKey(Player p, List<object> args)
        {
            if (args.Count < 2 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0)
            {
                Say(p, "Usage: /givekey <vehicle id> <player id or name>  (see /keys)");
                return;
            }

            string license = License(p);
            if (license == null) return;

            if (!ResolveTarget(p, string.Join(" ", args.Skip(1)), out Player target, out string error))
            {
                Say(p, error);
                return;
            }

            string targetLicense = License(target);
            if (targetLicense == null) { Say(p, "That player has no license identifier."); return; }
            if (targetLicense == license) { Say(p, "You already have the keys to your own vehicle."); return; }

            Query("SELECT id, model, plate FROM player_vehicles WHERE id = ? AND owner = ?",
                new object[] { id, license }, rows =>
                {
                    if (rows.Count == 0) { Say(p, "That vehicle is not yours."); return; }

                    var row = rows[0];
                    string model = row["model"].ToString(), plate = row["plate"].ToString();

                    if (HasKey(id, targetLicense))
                    {
                        Say(p, $"{target.Name} already has a key to the {model} [{plate}].");
                        return;
                    }

                    int count = _grants.TryGetValue(id, out var set) ? set.Count : 0;
                    if (count >= _maxKeysPerVehicle)
                    {
                        Say(p, $"That vehicle already has the maximum of {_maxKeysPerVehicle} keys.");
                        return;
                    }

                    // INSERT IGNORE plus the unique key makes a double-click idempotent. The row's
                    // owner is taken from the checked row, never from the client.
                    Db("INSERT IGNORE INTO vehicle_keys (vehicle_id, owner, grantee, owner_name, grantee_name) " +
                       "VALUES (?, ?, ?, ?, ?)",
                        new object[] { id, license, targetLicense, Name(p), Name(target) }, affected =>
                        {
                            if (affected != 1) { Say(p, "Could not give that key; try again."); return; }

                            Grant(id, targetLicense);
                            Say(p, $"{target.Name} now has a key to the {model} [{plate}].");
                            Say(target, $"{p.Name} gave you a key to their {model} [{plate}]. " +
                                        $"Use /shared to see it and /takeout {id} to take it out.");
                            Debug.WriteLine($"[garage] {license} granted a key to {targetLicense} for #{id} [{plate}]");
                        });
                });
        }

        void TakeKey(Player p, List<object> args)
        {
            if (args.Count < 2 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0)
            {
                Say(p, "Usage: /takekey <vehicle id> <player id or name|all>  (see /keys)");
                return;
            }

            string license = License(p);
            if (license == null) return;

            string who = string.Join(" ", args.Skip(1)).Trim();

            Query("SELECT model, plate FROM player_vehicles WHERE id = ? AND owner = ?",
                new object[] { id, license }, rows =>
                {
                    if (rows.Count == 0) { Say(p, "That vehicle is not yours."); return; }

                    string model = rows[0]["model"].ToString(), plate = rows[0]["plate"].ToString();

                    if (who.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        Db("DELETE FROM vehicle_keys WHERE vehicle_id = ? AND owner = ?",
                            new object[] { id, license }, affected =>
                            {
                                if (affected <= 0) { Say(p, $"No one had a key to the {model} [{plate}]."); return; }

                                // Check every revoked grantee, not just whoever the car is registered
                                // as being out with: a passenger can have taken the wheel over.
                                var revoked = _grants.TryGetValue(id, out var set) ? set.ToList() : new List<string>();
                                if (_grants.TryGetValue(id, out var all)) all.Clear();
                                foreach (string grantee in revoked) DisableIfDriving(FindByLicense(grantee), plate);

                                Say(p, $"All keys to the {model} [{plate}] were revoked.");
                                Debug.WriteLine($"[garage] {license} revoked all {affected} key(s) for #{id} [{plate}]");
                            });
                        return;
                    }

                    if (!ResolveTarget(p, who, out Player target, out string error)) { Say(p, error); return; }
                    string targetLicense = License(target);
                    if (targetLicense == null) return;

                    Db("DELETE FROM vehicle_keys WHERE vehicle_id = ? AND owner = ? AND grantee = ?",
                        new object[] { id, license, targetLicense }, affected =>
                        {
                            if (affected != 1)
                            {
                                Say(p, $"{target.Name} does not have a key to the {model} [{plate}].");
                                return;
                            }

                            Revoke(id, targetLicense);
                            Say(p, $"Revoked {target.Name}'s key to the {model} [{plate}].");
                            Say(target, $"{p.Name} took back your key to their {model} [{plate}].");
                            // A revocation must also stop an already-moving car, not just the next action.
                            DisableIfDriving(target, plate);
                            Debug.WriteLine($"[garage] {license} revoked {targetLicense}'s key for #{id} [{plate}]");
                        });
                });
        }

        // The other side of sharing: the cars someone else has given this player a key to.
        void ListShared(Player p, List<object> args)
        {
            string license = License(p);
            if (license == null) return;

            Query("SELECT v.id, v.model, v.plate, v.state, k.owner_name " +
                  "FROM vehicle_keys k JOIN player_vehicles v ON v.id = k.vehicle_id " +
                  "WHERE k.grantee = ? ORDER BY v.id", new object[] { license }, rows =>
            {
                if (rows.Count == 0) { Say(p, "No one has shared a vehicle with you."); return; }

                Say(p, "Shared with you (use /takeout <id>):");
                foreach (var r in rows)
                {
                    bool stored = Convert.ToInt32(r["state"]) == 1;
                    string by = string.IsNullOrEmpty(r["owner_name"]?.ToString())
                        ? "its owner" : r["owner_name"].ToString();
                    Say(p, $"#{r["id"]}  {r["model"]}  [{r["plate"]}]  {(stored ? "in garage" : "OUT")}  - from {by}");
                }
            });
        }

        // Server id first, then a unique name fragment. An ambiguous name is refused rather than
        // guessed, so a key always goes to the player the owner meant.
        bool ResolveTarget(Player p, string query, out Player target, out string error)
        {
            target = null;
            error = null;
            query = (query ?? "").Trim();
            if (query.Length == 0) { error = "Say which player: a server id or name."; return false; }

            if (int.TryParse(query, out int src))
            {
                if (src <= 0) { error = "No player with that server id."; return false; }
                target = Players[src];
                if (target == null) { error = "No player with that server id."; return false; }
                return true;
            }

            var matches = Players.ToList()
                .Where(x => x != null && x.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (matches.Count == 0) { error = "No player matches that name."; return false; }
            if (matches.Count > 1) { error = "More than one player matches; use their server id (/players)."; return false; }

            target = matches[0];
            return true;
        }

        bool HasKey(int vehicleId, string license) =>
            license != null && _grants.TryGetValue(vehicleId, out var set) && set.Contains(license);

        void Grant(int vehicleId, string license)
        {
            if (!_grants.TryGetValue(vehicleId, out var set)) _grants[vehicleId] = set = new HashSet<string>();
            set.Add(license);
        }

        void Revoke(int vehicleId, string license)
        {
            if (_grants.TryGetValue(vehicleId, out var set)) set.Remove(license);
        }

        // If the player is sitting in that car when a key is taken away, ask their client to keep
        // the engine off, so revocation means "no longer driving", not only "no longer locking".
        void DisableIfDriving(Player target, string plate)
        {
            if (target == null) return;
            int ped = GetPlayerPed(target.Handle);
            if (ped == 0) return;

            int vehicle = GetVehiclePedIsIn(ped, false);
            if (vehicle == 0 || !DoesEntityExist(vehicle)) return;
            if ((GetVehicleNumberPlateText(vehicle) ?? "").Trim() != plate) return;

            TriggerClientEvent(target, "garage:applyEngine", NetworkGetNetworkIdFromEntity(vehicle), false);
        }

        static string Name(Player p)
        {
            string name = (p?.Name ?? "player").Trim();
            return name.Length > 64 ? name.Substring(0, 64) : name;
        }

        void TakeOut(Player p, List<object> args)
        {
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0)
            {
                Say(p, "Usage: /takeout <vehicle id>  (see /garage or /shared)");
                return;
            }
            string license = License(p);
            if (license == null) return;

            // One atomic statement checks that the row exists, is stored, and that this player may
            // use it (owner, or a live grant), then flips it. Two simultaneous requests can never
            // both succeed: only one sees affected == 1. The key join is on the stable row id.
            Db("UPDATE player_vehicles v " +
               "LEFT JOIN vehicle_keys k ON k.vehicle_id = v.id AND k.grantee = ? " +
               "SET v.state = 0 " +
               "WHERE v.id = ? AND v.state = 1 AND v.stolen = 0 AND (v.owner = ? OR k.grantee IS NOT NULL)",
                new object[] { license, id, license }, affected =>
                {
                    if (affected != 1)
                    {
                        Say(p, "That vehicle doesn't exist, isn't yours or shared with you, or is already out.");
                        return;
                    }

                    Query("SELECT owner, model, plate, engine, body, mods FROM player_vehicles WHERE id = ?",
                        new object[] { id }, rows =>
                        {
                            if (rows.Count != 1) return;

                            var v = rows[0];
                            string owner = v["owner"].ToString();
                            string plate = v["plate"].ToString();
                            string model = v["model"].ToString();

                            var reference = new VehicleRef { Id = id, Owner = owner, Model = model };
                            _vehicles[plate] = reference;
                            _vehiclesById[id] = reference;

                            // The car belongs to the owner's garage even when a key holder takes it
                            // out, so parking, theft recovery and a disconnect all return it there.
                            Track(owner).Add(plate);
                            _heldBy[plate] = license;

                            TriggerClientEvent(p, "garage:spawn", model, plate, Convert.ToInt32(v["engine"]), Convert.ToInt32(v["body"]),
                                               v["mods"] == null ? "" : v["mods"].ToString());
                            if (owner != license) Say(p, $"You took out {model} [{plate}] with the owner's key.");
                            Debug.WriteLine($"[garage] {p.Name} ({license}) took out #{id} [{plate}] owned by {owner}");
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

        // The client reports the network id of the vehicle it just spawned. We only accept it if
        // the real entity carries the plate, and the sender is the player who took that plate out
        // (owner or key holder) -- the owner's tracking set alone is no longer proof of that.
        [EventHandler("garage:spawned")]
        void OnSpawned([FromSource] Player p, int netId, string plate)
        {
            string license = p == null ? null : License(p);
            if (license == null || string.IsNullOrEmpty(plate)) return;
            if (!_heldBy.TryGetValue(plate, out string holder) || holder != license) return;

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
            VehicleRef vehicle;
            int entity = UsableVehicle(p, netId, out plate, out vehicle);
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
            VehicleRef vehicle;
            int entity = UsableVehicle(p, netId, out plate, out vehicle);
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

            Query("SELECT id, owner, model, state, stolen FROM player_vehicles WHERE plate = ?", new object[] { plate }, rows =>
            {
                if (rows.Count == 0) { Say(p, "That vehicle is not registered."); return; }

                var row = rows[0];
                int vehicleId = Convert.ToInt32(row["id"]);
                string owner = row["owner"].ToString();
                string model = row["model"].ToString();

                if (Convert.ToInt32(row["state"]) == 1) { Say(p, "That vehicle is stored in a garage."); return; }
                if (owner == license) { Say(p, "That is your own vehicle."); return; }
                // A key holder is not a thief: hotwiring a car you were given access to would
                // flag it stolen for its owner and burn a lockpick for nothing.
                if (HasKey(vehicleId, license)) { Say(p, "You already have the keys to that vehicle."); return; }
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

        // Resolves a client-supplied network id to a real vehicle and applies the key ACL: the
        // owner always has access; a grantee has it only while the grant is in the cache. Because
        // the cache is updated on revoke, a key stops working on the very next lock/engine action,
        // even if the car is already out and moving.
        int UsableVehicle(Player p, int netId, out string plate, out VehicleRef vehicle)
        {
            plate = null;
            vehicle = null;
            string license = License(p);
            if (license == null) return 0;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity)) return 0;

            plate = (GetVehicleNumberPlateText(entity) ?? "").Trim();
            if (!_vehicles.TryGetValue(plate, out vehicle)) return 0;
            if (vehicle.Owner != license && !HasKey(vehicle.Id, license)) return 0;
            return entity;
        }

        // Where are my vehicles (and the ones shared with me)? Positions are read from the real
        // entities on the server; a shared car only appears while it is actually out.
        void Locate(Player p, List<object> args)
        {
            string license = License(p);
            if (license == null) return;

            var plates = new HashSet<string>();
            if (_out.TryGetValue(license, out var own)) plates.UnionWith(own);
            foreach (var pair in _vehicles)
            {
                if (!_grants.TryGetValue(pair.Value.Id, out var set) || !set.Contains(license)) continue;
                if (_out.TryGetValue(pair.Value.Owner, out var ownerPlates) && ownerPlates.Contains(pair.Key))
                    plates.Add(pair.Key);
            }

            var entries = new List<string>();
            foreach (var plate in plates)
            {
                int netId;
                if (!_netIds.TryGetValue(plate, out netId)) continue;
                int entity = NetworkGetEntityFromNetworkId(netId);
                if (entity == 0 || !DoesEntityExist(entity)) continue;

                var pos = GetEntityCoords(entity);
                entries.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1:F1},{2:F1},{3:F1}", plate, pos.X, pos.Y, pos.Z));
            }

            if (entries.Count == 0) { Say(p, "None of your vehicles (or shared ones) are out in the world."); return; }
            TriggerClientEvent(p, "garage:locate", string.Join(";", entries));
        }

        void ForgetVehicle(string plate)
        {
            _netIds.Remove(plate);
            _locked.Remove(plate);
            _engineOff.Remove(plate);
            _heldBy.Remove(plate);
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
            }

            ForgetVehicle(plate);
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

                    Query("SELECT id, model, plate, engine, body, mods FROM player_vehicles WHERE plate = ? AND owner = ?",
                        new object[] { plate, license }, rows =>
                        {
                            if (rows.Count != 1) return;
                            var v = rows[0];
                            int id = Convert.ToInt32(v["id"]);
                            string model = v["model"].ToString();

                            var reference = new VehicleRef { Id = id, Owner = license, Model = model };
                            _vehicles[plate] = reference;
                            _vehiclesById[id] = reference;

                            Track(license).Add(plate);
                            _heldBy[plate] = license;
                            TriggerClientEvent(p, "garage:spawn", model, plate,
                                Convert.ToInt32(v["engine"]), Convert.ToInt32(v["body"]),
                                v["mods"] == null ? "" : v["mods"].ToString());
                            Debug.WriteLine($"[garage] delivered #{id} [{plate}] to {license}");
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
            if (!_vehicles.TryGetValue(plate, out var vehicle))
            {
                Say(p, "That is not one of your vehicles.");
                return;
            }

            bool owner = vehicle.Owner == license;
            if (!owner && !HasKey(vehicle.Id, license))
            {
                Say(p, "That is not one of your vehicles.");
                return;
            }

            engine = Clamp(engine); body = Clamp(body);
            Db("UPDATE player_vehicles SET state = 1, engine = ?, body = ? WHERE id = ? AND state = 0 AND stolen = 0",
               new object[] { engine, body, vehicle.Id }, affected =>
               {
                   if (affected != 1)
                   {
                       Say(p, "Could not store that vehicle (a stolen vehicle has to be recovered by the police).");
                       return;
                   }

                   // Parking always returns the car to its owner's garage, whichever key was used.
                   Track(vehicle.Owner).Remove(plate);
                   ForgetVehicle(plate);
                   if (DoesEntityExist(entity)) DeleteEntity(entity);
                   Say(p, owner ? $"[{plate}] stored in your garage." : $"[{plate}] returned to its owner's garage.");
                   Debug.WriteLine($"[garage] {p.Name} ({license}) parked {vehicle.Owner}'s [{plate}] (engine {engine}, body {body})");
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

            // Return only the cars this player actually has out. A shared car being used by
            // someone else stays in the world if the owner disconnects, and vice versa: a key
            // holder who leaves with the car puts it back in the owner's garage.
            var returning = new List<string>();
            foreach (var plate in _out.Values.SelectMany(set => set).Distinct().ToList())
            {
                if (_heldBy.TryGetValue(plate, out string holder) && holder != license) continue;
                returning.Add(plate);
            }

            foreach (string plate in returning)
            {
                string owner = _vehicles.TryGetValue(plate, out var vehicle) ? vehicle.Owner : license;
                Db("UPDATE player_vehicles SET state = 1, stolen = 0 WHERE plate = ? AND owner = ?",
                    new object[] { plate, owner }, _ => { });
                Track(owner).Remove(plate);
                ForgetVehicle(plate);
            }

            // Keep the owner's set if a key holder is still out with one of their cars.
            foreach (string key in _out.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList())
                _out.Remove(key);
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
