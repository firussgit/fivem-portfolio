using System;
using System.Collections.Generic;
using System.Linq;
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

        readonly Random _rng = new Random();
        readonly Dictionary<string, HashSet<string>> _out = new Dictionary<string, HashSet<string>>(); // license -> plates in the world
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        // Live state of vehicles that are out in the world, keyed by plate. The server is the source of truth;
        // clients are told what to apply. All of it is cleared when a vehicle is parked or its owner leaves.
        readonly Dictionary<string, int> _netIds = new Dictionary<string, int>(); // plate -> network id
        readonly HashSet<string> _locked = new HashSet<string>();
        readonly HashSet<string> _engineOff = new HashSet<string>();
        const float InteractRange = 15f;

        public GarageServer()
        {
            Cmd("locate", Locate);
            Cmd("givecar", GiveCar);
            Cmd("garage", ListGarage);
            Cmd("takeout", TakeOut);

            try
            {
                Db("CREATE TABLE IF NOT EXISTS player_vehicles (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, owner VARCHAR(64) NOT NULL, model VARCHAR(32) NOT NULL, " +
                   "plate VARCHAR(8) NOT NULL UNIQUE, state TINYINT NOT NULL DEFAULT 1, " +
                   "engine INT NOT NULL DEFAULT 1000, body INT NOT NULL DEFAULT 1000, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX idx_owner (owner))",
                   new object[0], _ =>
                   {
                       // After a restart nobody's cars are in the world any more: return them all to the garage.
                       Db("UPDATE player_vehicles SET state = 1 WHERE state = 0", new object[0],
                          n => Debug.WriteLine($"[garage] ready, {n} vehicle(s) returned to garage"));
                   });
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

            Query("SELECT id, model, plate, state FROM player_vehicles WHERE owner = ? ORDER BY id", new object[] { license }, rows =>
            {
                if (rows.Count == 0) { Say(p, "Your garage is empty."); return; }
                Say(p, "Your vehicles (use /takeout <id>):");
                foreach (var r in rows)
                {
                    bool stored = Convert.ToInt32(r["state"]) == 1;
                    Say(p, $"#{r["id"]}  {r["model"]}  [{r["plate"]}]  {(stored ? "in garage" : "OUT")}");
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

                Query("SELECT model, plate, engine, body FROM player_vehicles WHERE id = ?", new object[] { id }, rows =>
                {
                    var v = rows[0];
                    string plate = v["plate"].ToString();
                    Track(license).Add(plate);
                    TriggerClientEvent(p, "garage:spawn", v["model"].ToString(), plate, Convert.ToInt32(v["engine"]), Convert.ToInt32(v["body"]));
                    Debug.WriteLine($"[garage] {p.Name} took out #{id} [{plate}]");
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
            Db("UPDATE player_vehicles SET state = 1, engine = ?, body = ? WHERE plate = ? AND owner = ? AND state = 0",
               new object[] { engine, body, plate, license }, affected =>
               {
                   if (affected != 1) { Say(p, "Could not store that vehicle."); return; }
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

            // A player who leaves with cars out must not leave them marked "out" forever.
            if (_out.TryGetValue(license, out var plates))
            {
                foreach (var plate in plates)
                {
                    ForgetVehicle(plate);
                    Db("UPDATE player_vehicles SET state = 1 WHERE plate = ? AND owner = ?", new object[] { plate, license }, _ => { });
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
