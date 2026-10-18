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

        public GarageServer()
        {
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
                    Db("UPDATE player_vehicles SET state = 1 WHERE plate = ? AND owner = ?", new object[] { plate, license }, _ => { });
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
