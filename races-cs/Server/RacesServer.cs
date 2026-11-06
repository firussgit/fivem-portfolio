using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using Portfolio.Core;
using static CitizenFX.Core.Native.API;

namespace Races.Server
{
    class Checkpoint
    {
        public string Label;
        public Vector3 Pos;
        public Checkpoint(string label, Vector3 pos) { Label = label; Pos = pos; }
    }

    class RouteDef
    {
        public string Id;
        public string Label;
        public string Model;
        public Vector3 Start;
        public int TimeLimit; // seconds
        public Checkpoint[] Checkpoints;

        public RouteDef(string id, string label, string model, Vector3 start, int timeLimit, params Checkpoint[] checkpoints)
        {
            Id = id; Label = label; Model = model; Start = start; TimeLimit = timeLimit; Checkpoints = checkpoints;
        }
    }

    class RaceState
    {
        public string License;
        public int Src;
        public RouteDef Route;
        public int CarNetId;
        public long StartedAt;
        public long SpawnDeadline;
        public int Index;
        public long LastCheckpointAt;
        public Vector3 LastPos;
    }

    /// <summary>
    /// Server-timed time trials. Routes are fixed data in this file; the server picks the car,
    /// verifies the real vehicle and the player's real position every tick, and owns the clock.
    /// The client only draws the checkpoints and reports which car it spawned.
    /// </summary>
    public class RacesServer : PortfolioScript
    {
        const float StartRange = 25f;      // metres from the route start
        const float ClaimRadius = 25f;     // checkpoint radius (checked on the server)
        const double MaxTravelSpeed = 70.0; // m/s: faster than the route cars, so only teleports fail
        const long SpawnTimeoutMs = 20000;
        const long FinishCooldownMs = 10000;
        const long CancelCooldownMs = 5000;

        // The definitions are the source of truth. Checkpoints use known road positions from the
        // jobs routes (the hubs themselves are approximate); edit this table to tune a race.
        static readonly Dictionary<string, RouteDef> Routes = new Dictionary<string, RouteDef>
        {
            ["downtown"] = new RouteDef("downtown", "Downtown Sprint", "comet2",
                new Vector3(215.7f, -810.2f, 30.7f), 600,
                new Checkpoint("Pillbox Hill", new Vector3(307.7f, -595.3f, 43.3f)),
                new Checkpoint("Vinewood Blvd", new Vector3(293.0f, 180.0f, 104.0f)),
                new Checkpoint("Mirror Park", new Vector3(1050.0f, -700.0f, 57.0f)),
                new Checkpoint("La Mesa", new Vector3(800.0f, -1300.0f, 26.0f)),
                new Checkpoint("Mission Row (finish)", new Vector3(425.1f, -979.6f, 30.7f))),

            ["coast"] = new RouteDef("coast", "Coast Run", "sultan",
                new Vector3(-1850.0f, -1231.0f, 13.0f), 900,
                new Checkpoint("Vespucci Beach", new Vector3(-1200.0f, -1500.0f, 4.0f)),
                new Checkpoint("LS International", new Vector3(-1037.8f, -2737.8f, 20.2f)),
                new Checkpoint("La Puerta", new Vector3(300.0f, -2000.0f, 5.0f)),
                new Checkpoint("Legion Square (finish)", new Vector3(215.7f, -810.2f, 30.7f))),

            ["desert"] = new RouteDef("desert", "Desert Dash", "buffalo",
                new Vector3(1699.0f, 3599.0f, 35.4f), 600,
                new Checkpoint("Sandy Shores town", new Vector3(1960.0f, 3740.0f, 35.0f)),
                new Checkpoint("Desert junction", new Vector3(2200.0f, 3900.0f, 40.0f)),
                new Checkpoint("Desert flats", new Vector3(2000.0f, 4100.0f, 45.0f)),
                new Checkpoint("Sandy Shores south (finish)", new Vector3(1500.0f, 3800.0f, 35.0f))),
        };

        readonly Dictionary<string, RaceState> _races = new Dictionary<string, RaceState>(); // license -> active race
        readonly Dictionary<string, long> _cooldown = new Dictionary<string, long>();        // license -> next allowed

        public RacesServer()
        {
            ChatPrefix = "Races";
            ChatColour = new[] { 255, 200, 90 };

            Cmd("race", RaceCommand);
            Cmd("top", TopCommand);

            Tick += RaceLoop;
            CreateTable();
        }

        void CreateTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS race_times (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, name VARCHAR(64) NOT NULL, " +
                   "route VARCHAR(32) NOT NULL, milliseconds INT NOT NULL, model VARCHAR(32) NOT NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "INDEX idx_route (route, milliseconds), INDEX idx_license (license))", new object[0],
                   _ => Debug.WriteLine("[races] leaderboard ready"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[races] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Commands
        // ==================================================================

        void RaceCommand(Player p, List<object> args)
        {
            string sub = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;

            if (sub == "stop") { StopRace(p); return; }
            if (sub == null) { ListRoutes(p); return; }
            StartRace(p, sub);
        }

        void ListRoutes(Player p)
        {
            Say(p, "Time trials (stand at the start, then /race <id>):");
            foreach (var route in Routes.Values.OrderBy(r => r.Id))
                Say(p, $"{route.Id} - {route.Label} ({route.Model}, {route.Checkpoints.Length} checkpoints, {route.TimeLimit / 60} min)");
            Say(p, "/top <id> shows the fastest times; /race stop ends a run.");
        }

        void StartRace(Player p, string routeId)
        {
            string license = License(p);
            if (license == null) return;

            if (_races.ContainsKey(license)) { Say(p, "You are already in a race. Use /race stop."); return; }

            long now = GetGameTimer();
            if (_cooldown.TryGetValue(license, out long until) && now < until)
            {
                Say(p, $"Wait {(until - now) / 1000 + 1}s before starting another race.");
                return;
            }

            RouteDef route;
            if (!Routes.TryGetValue(routeId, out route))
            {
                Say(p, "Unknown route. /race lists them.");
                return;
            }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;

            if (GetVehiclePedIsIn(ped, false) != 0) { Say(p, "Get out of your vehicle first; the race car is provided."); return; }

            Vector3 pos = GetEntityCoords(ped);
            if (Vector3.Distance(pos, route.Start) > StartRange)
            {
                Say(p, $"Go to the {route.Label} start point (marked on your map) first.");
                return;
            }

            var race = new RaceState
            {
                License = license,
                Src = int.Parse(p.Handle),
                Route = route,
                SpawnDeadline = now + SpawnTimeoutMs,
                LastCheckpointAt = now,
                LastPos = route.Start,
            };
            _races[license] = race;

            TriggerClientEvent(p, "races:spawn", route.Id, route.Model, route.Label, PackCheckpoints(route.Checkpoints));
            Say(p, $"{route.Label}: get in the provided {route.Model}...");
            Debug.WriteLine($"[races] {p.Name} started {route.Id}");
        }

        void StopRace(Player p)
        {
            string license = License(p);
            if (license == null) return;

            RaceState race;
            if (!_races.TryGetValue(license, out race))
            {
                Say(p, "You are not in a race.");
                return;
            }

            EndRace(race, "stopped", "You stopped the race.", 0, false, 0, CancelCooldownMs);
        }

        void TopCommand(Player p, List<object> args)
        {
            string routeId = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;
            if (routeId == null)
            {
                Say(p, "Usage: /top <route>. Routes: " + string.Join(", ", Routes.Keys.OrderBy(k => k)));
                return;
            }

            RouteDef route;
            if (!Routes.TryGetValue(routeId, out route))
            {
                Say(p, "Unknown route. Routes: " + string.Join(", ", Routes.Keys.OrderBy(k => k)));
                return;
            }

            Query("SELECT name, MIN(milliseconds) AS best FROM race_times WHERE route = ? " +
                  "GROUP BY license, name ORDER BY best ASC LIMIT 10", new object[] { routeId }, rows =>
            {
                if (rows.Count == 0)
                {
                    Say(p, $"No times for {route.Label} yet. Be the first with /race {routeId}.");
                    return;
                }

                TriggerClientEvent(p, "races:board", route.Id, route.Label, PackBoard(rows));

                Say(p, $"{route.Label} - fastest times:");
                for (int i = 0; i < Math.Min(3, rows.Count); i++)
                    Say(p, $"{i + 1}. {Clean(rows[i]["name"].ToString(), 60)}  {FormatTime(Convert.ToInt32(rows[i]["best"]))}");
                if (rows.Count > 3) Say(p, $"See the board for the top {rows.Count}.");
            });
        }

        // ==================================================================
        //  Race lifecycle
        // ==================================================================

        // The client spawned the provided car and is sitting in it.
        [EventHandler("races:carSpawned")]
        void OnCarSpawned([FromSource] Player p, int netId)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;

            RaceState race;
            if (!_races.TryGetValue(license, out race) || race.CarNetId != 0) return;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity))
            {
                EndRace(race, "failed", "The race vehicle could not be spawned.", 0, false, 0, CancelCooldownMs);
                return;
            }

            // The server checks the real model, not the one the client claims it made.
            // Both natives return a signed int hash. Comparing int to (uint) promotes both to long,
            // which never matches a model whose hash has the high bit set (comet2, buffalo, ...).
            // Keep the comparison signed so every model verifies.
            if (GetEntityModel(entity) != GetHashKey(race.Route.Model))
            {
                EndRace(race, "failed", "That is not the race vehicle.", 0, false, 0, CancelCooldownMs);
                return;
            }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0 || GetVehiclePedIsIn(ped, false) != entity)
            {
                EndRace(race, "failed", "Get in the race vehicle to start.", 0, false, 0, CancelCooldownMs);
                return;
            }

            long now = GetGameTimer();
            race.CarNetId = netId;
            race.StartedAt = now;
            race.LastCheckpointAt = now;
            race.LastPos = race.Route.Start;

            TriggerClientEvent(p, "races:started", race.Route.Label, PackCheckpoints(race.Route.Checkpoints));
            Say(p, $"GO! {race.Route.Checkpoints.Length} checkpoints. Reach the marked checkpoint first.");
            Debug.WriteLine($"[races] {p.Name} racing {race.Route.Id} (net {netId})");
        }

        // The client could not create or enter the provided car, so cancel before the timeout.
        [EventHandler("races:spawnFailed")]
        void OnSpawnFailed([FromSource] Player p)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;

            RaceState race;
            if (!_races.TryGetValue(license, out race) || race.CarNetId != 0) return;

            EndRace(race, "failed", "The race vehicle could not be spawned.", 0, false, 0, CancelCooldownMs);
        }

        async Task RaceLoop()
        {
            while (true)
            {
                await Delay(200);
                try { ScanRaces(); }
                catch (Exception e) { Debug.WriteLine("[races] tick: " + e.Message); }
            }
        }

        void ScanRaces()
        {
            long now = GetGameTimer();

            foreach (var race in _races.Values.ToList())
            {
                Player p = Players[race.Src];
                if (p == null)
                {
                    DeleteCar(race);
                    _races.Remove(race.License);
                    continue;
                }

                // Waiting for the client to spawn the car.
                if (race.CarNetId == 0)
                {
                    if (now > race.SpawnDeadline)
                        EndRace(race, "failed", "The race vehicle did not spawn in time.", 0, false, 0, CancelCooldownMs);
                    continue;
                }

                if (now - race.StartedAt > race.Route.TimeLimit * 1000L)
                {
                    EndRace(race, "expired", "You ran out of time.", 0, false, 0, CancelCooldownMs);
                    continue;
                }

                int ped = GetPlayerPed(p.Handle);
                if (ped == 0 || GetEntityHealth(ped) <= 0) continue;

                int car = NetworkGetEntityFromNetworkId(race.CarNetId);
                if (car == 0 || !DoesEntityExist(car))
                {
                    EndRace(race, "failed", "Your race vehicle is gone.", 0, false, 0, CancelCooldownMs);
                    continue;
                }

                if (GetVehiclePedIsIn(ped, false) != car)
                {
                    EndRace(race, "failed", "You left the race vehicle.", 0, false, 0, CancelCooldownMs);
                    continue;
                }

                if (race.Index < 0 || race.Index >= race.Route.Checkpoints.Length) { continue; }
                Checkpoint checkpoint = race.Route.Checkpoints[race.Index];

                Vector3 pos = GetEntityCoords(ped);
                if (Vector3.Distance(pos, checkpoint.Pos) > ClaimRadius) continue;

                // A checkpoint that was reached faster than a car could drive there is a teleport.
                double elapsed = (now - race.LastCheckpointAt) / 1000.0;
                double minSeconds = Vector3.Distance(race.LastPos, checkpoint.Pos) / MaxTravelSpeed;
                if (elapsed + 2.0 < minSeconds)
                {
                    EndRace(race, "failed", "That checkpoint was reached too fast; drive the route.", 0, false, 0, CancelCooldownMs);
                    continue;
                }

                race.Index++;
                race.LastPos = checkpoint.Pos;
                race.LastCheckpointAt = now;

                if (race.Index >= race.Route.Checkpoints.Length)
                {
                    FinishRace(race, now);
                    continue;
                }

                TriggerClientEvent(p, "races:checkpoint", race.Index, race.Route.Checkpoints[race.Index].Label);
                Debug.WriteLine($"[races] {p.Name} checkpoint {race.Index}/{race.Route.Checkpoints.Length} on {race.Route.Id}");
            }
        }

        void FinishRace(RaceState race, long now)
        {
            int milliseconds = (int)Math.Max(0, Math.Min(int.MaxValue, now - race.StartedAt));

            // Stop ticking immediately; the leaderboard work happens below.
            _races.Remove(race.License);
            _cooldown[race.License] = now + FinishCooldownMs;
            DeleteCar(race);

            Player p = Players[race.Src];
            string name = Clean(p != null ? p.Name : race.License, 60);

            Query("SELECT MIN(milliseconds) AS best FROM race_times WHERE license = ? AND route = ?",
                new object[] { race.License, race.Route.Id }, rows =>
                {
                    int best = 0;
                    if (rows.Count > 0 && rows[0]["best"] != null && !(rows[0]["best"] is DBNull))
                        best = Convert.ToInt32(rows[0]["best"]);

                    bool personalBest = best == 0 || milliseconds < best;

                    Insert("INSERT INTO race_times (license, name, route, milliseconds, model) VALUES (?, ?, ?, ?, ?)",
                        new object[] { race.License, name, race.Route.Id, milliseconds, race.Route.Model },
                        id => Debug.WriteLine($"[races] time #{id} {name} {race.Route.Id} {FormatTime(milliseconds)} pb={personalBest}"));

                    if (p != null)
                    {
                        TriggerClientEvent(p, "races:end", "finished", milliseconds, personalBest, best);
                        Say(p, personalBest
                            ? $"Finished {race.Route.Label} in {FormatTime(milliseconds)} - new personal best!"
                            : $"Finished {race.Route.Label} in {FormatTime(milliseconds)} (best {FormatTime(best)}).");
                    }
                });
        }

        void EndRace(RaceState race, string code, string message, int milliseconds, bool personalBest, int best, long cooldownMs)
        {
            _races.Remove(race.License);
            _cooldown[race.License] = GetGameTimer() + cooldownMs;
            DeleteCar(race);

            Player p = Players[race.Src];
            if (p == null) return;

            TriggerClientEvent(p, "races:end", code, milliseconds, personalBest, best);
            if (!string.IsNullOrEmpty(message)) Say(p, message);
        }

        void DeleteCar(RaceState race)
        {
            if (race.CarNetId == 0) return;
            int entity = NetworkGetEntityFromNetworkId(race.CarNetId);
            if (entity != 0 && DoesEntityExist(entity)) DeleteEntity(entity);
            race.CarNetId = 0;
        }

        // ==================================================================
        //  Routes / cleanup
        // ==================================================================

        [EventHandler("races:requestRoutes")]
        void OnRequestRoutes([FromSource] Player p)
        {
            if (p == null) return;
            TriggerClientEvent(p, "races:routes", PackRoutes());
        }

        [EventHandler("playerDropped")]
        void OnDropped([FromSource] Player p)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;

            RaceState race;
            if (_races.TryGetValue(license, out race))
            {
                DeleteCar(race);
                _races.Remove(license);
                _cooldown.Remove(license);
            }
            Forget(Src(p));
        }

        [EventHandler("onResourceStop")]
        void OnResourceStop(string resourceName)
        {
            if (resourceName != GetCurrentResourceName()) return;
            foreach (var race in _races.Values.ToList()) DeleteCar(race);
            _races.Clear();
        }

        // ==================================================================
        //  Helpers
        // ==================================================================

        static string PackRoutes() =>
            string.Join(";", Routes.Values.OrderBy(r => r.Id).Select(r =>
                r.Id + "|" + Clean(r.Label, 40) + "|" +
                F(r.Start.X) + "|" + F(r.Start.Y) + "|" + F(r.Start.Z)));

        static string PackCheckpoints(Checkpoint[] checkpoints) =>
            string.Join(";", checkpoints.Select(c =>
                Clean(c.Label, 40) + "|" + F(c.Pos.X) + "|" + F(c.Pos.Y) + "|" + F(c.Pos.Z)));

        static string PackBoard(List<IDictionary<string, object>> rows) =>
            string.Join(";", rows.Select(r =>
                Clean(r["name"].ToString(), 60).Replace("|", " ").Replace(";", " ") + "|" +
                Convert.ToInt32(r["best"])));

        static string F(float value) => value.ToString("F1", CultureInfo.InvariantCulture);

        static string FormatTime(int milliseconds) =>
            $"{milliseconds / 60000}:{(milliseconds / 1000) % 60:00}.{milliseconds % 1000:000}";

        static string Clean(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder();
            foreach (char c in value) if (!char.IsControl(c)) sb.Append(c);
            string text = sb.ToString().Trim();
            return text.Length > max ? text.Substring(0, max) : text;
        }

        void Insert(string sql, object[] args, Action<int> done) =>
            Exports["oxmysql"].insert(sql, args, new Action<dynamic>(id => done(id == null ? 0 : Convert.ToInt32(id))));

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[races] /{name} is in-game only"); return; }
                Player p = Players[src];
                if (p == null || RateLimited(src)) return;
                handler(p, args);
            }), false);
        }
    }
}
