using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Needs.Server
{
    class PlayerNeeds
    {
        public double Hunger = StartValue;
        public double Thirst = StartValue;
        public bool Dirty;
        public const double StartValue = 80;
    }

    /// <summary>
    /// Hunger and thirst. The server owns the numbers: it decays them on a timer, applies item effects,
    /// decides when the player is hurt, and persists everything. The client only displays and obeys.
    /// Items reach this resource through an event, so it never depends on the inventory resource.
    /// </summary>
    public class NeedsServer : BaseScript
    {
        // What each item restores. Anything not listed here has no effect on needs.
        static readonly Dictionary<string, KeyValuePair<double, double>> ItemEffects = new Dictionary<string, KeyValuePair<double, double>>
        {
            ["sandwich"] = new KeyValuePair<double, double>(30, 0),   // hunger, thirst
            ["water"] = new KeyValuePair<double, double>(0, 35),
        };

        const double HungerDecay = 1.0;  // points lost per tick
        const double ThirstDecay = 1.5;
        const double LowThreshold = 20;

        readonly Dictionary<string, PlayerNeeds> _needs = new Dictionary<string, PlayerNeeds>(); // online players only
        readonly int _tickSeconds;

        public NeedsServer()
        {
            _tickSeconds = Math.Max(5, GetConvarInt("needs_tick_seconds", 60));

            RegisterCommand("setneeds", new Action<int, List<object>, string>(SetNeedsCommand), false);

            // For other resources (e.g. a hospital that resets needs on discharge).
            Exports.Add("getNeeds", new Func<int, string>(src =>
            {
                var n = Cached(src);
                return n == null ? null : Round(n.Hunger) + "," + Round(n.Thirst);
            }));
            Exports.Add("addNeeds", new Func<int, double, double, bool>((src, hunger, thirst) =>
            {
                var n = Cached(src);
                if (n == null) return false;
                Apply(n, hunger, thirst);
                SendUpdate(Players[src], n);
                return true;
            }));

            CreateTable();
            foreach (var p in Players) Load(p, null); // players already online when the resource starts
            Tick += DecayLoop;
        }

        void CreateTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS player_needs (license VARCHAR(64) PRIMARY KEY, hunger FLOAT NOT NULL DEFAULT 80, " +
                   "thirst FLOAT NOT NULL DEFAULT 80, updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)",
                   new object[0], _ => Debug.WriteLine($"[needs] ready, decay every {_tickSeconds}s"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[needs] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Decay and damage
        // ==================================================================

        async Task DecayLoop()
        {
            await Delay(_tickSeconds * 1000);

            foreach (var p in Players.ToList())
            {
                var n = Cached(int.Parse(p.Handle));
                if (n == null) continue;

                Apply(n, -HungerDecay, -ThirstDecay);

                // Starving and dehydration hurt; both at once hurts more. The server decides, the client applies it.
                int damage = (n.Hunger <= 0 ? 2 : 0) + (n.Thirst <= 0 ? 3 : 0);
                if (damage > 0) TriggerClientEvent(p, "needs:hurt", damage);

                SendUpdate(p, n);
            }

            FlushAll();
        }

        // ==================================================================
        //  Item effects (via event, no direct dependency on the inventory)
        // ==================================================================

        // Raised by inventory-cs after it has already consumed the item.
        [EventHandler("inventory:itemUsed")]
        void OnItemUsed(int src, string item)
        {
            KeyValuePair<double, double> effect;
            if (item == null || !ItemEffects.TryGetValue(item, out effect)) return;

            var p = Players[src];
            var n = Cached(src);
            if (p == null || n == null) return;

            Apply(n, effect.Key, effect.Value);
            SendUpdate(p, n);
        }

        // ==================================================================
        //  Admin test command
        // ==================================================================

        void SetNeedsCommand(int src, List<object> args, string raw)
        {
            if (src <= 0) return;
            var p = Players[src];
            if (p == null) return;
            if (!IsPlayerAceAllowed(p.Handle, "needs.admin")) { Say(p, "You are not allowed to use that."); return; }

            double hunger, thirst;
            if (args.Count < 2
                || !double.TryParse(args[0]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out hunger)
                || !double.TryParse(args[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out thirst))
            {
                Say(p, "Usage: /setneeds <hunger 0-100> <thirst 0-100>  (sets your own values, for testing)");
                return;
            }

            var n = Cached(src);
            if (n == null) { Say(p, "Your needs aren't loaded yet."); return; }
            n.Hunger = Clamp(hunger);
            n.Thirst = Clamp(thirst);
            n.Dirty = true;
            SendUpdate(p, n);
        }

        // ==================================================================
        //  Loading, saving, helpers
        // ==================================================================

        [EventHandler("playerJoining")]
        void OnJoining([FromSource] Player p, string oldId)
        {
            if (p != null) Load(p, null);
        }

        // The HUD asks for its first values once the player is in the game.
        [EventHandler("needs:request")]
        void OnRequest([FromSource] Player p)
        {
            if (p == null) return;
            Load(p, n => SendUpdate(p, n));
        }

        void Load(Player p, Action<PlayerNeeds> then)
        {
            string lic = License(p);
            if (lic == null) return;

            PlayerNeeds existing;
            if (_needs.TryGetValue(lic, out existing)) { if (then != null) then(existing); return; }

            Query("SELECT hunger, thirst FROM player_needs WHERE license = ?", new object[] { lic }, rows =>
            {
                if (Players[int.Parse(p.Handle)] == null) return; // left while loading

                // A parallel load may have finished first: reuse it, never keep two copies.
                PlayerNeeds n;
                if (!_needs.TryGetValue(lic, out n))
                {
                    n = new PlayerNeeds();
                    if (rows.Count > 0)
                    {
                        n.Hunger = Clamp(Convert.ToDouble(rows[0]["hunger"], CultureInfo.InvariantCulture));
                        n.Thirst = Clamp(Convert.ToDouble(rows[0]["thirst"], CultureInfo.InvariantCulture));
                    }
                    _needs[lic] = n;
                }
                if (then != null) then(n);
            });
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            string lic = License(p);
            if (lic == null) return;

            PlayerNeeds n;
            if (_needs.TryGetValue(lic, out n)) { Save(lic, n); _needs.Remove(lic); }
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name == GetCurrentResourceName()) FlushAll();
        }

        void FlushAll()
        {
            foreach (var kv in _needs.ToList())
                if (kv.Value.Dirty) Save(kv.Key, kv.Value);
        }

        void Save(string lic, PlayerNeeds n)
        {
            n.Dirty = false;
            Db("INSERT INTO player_needs (license, hunger, thirst) VALUES (?, ?, ?) ON DUPLICATE KEY UPDATE hunger = VALUES(hunger), thirst = VALUES(thirst)",
               new object[] { lic, n.Hunger, n.Thirst }, _ => { });
        }

        void Apply(PlayerNeeds n, double hungerDelta, double thirstDelta)
        {
            n.Hunger = Clamp(n.Hunger + hungerDelta);
            n.Thirst = Clamp(n.Thirst + thirstDelta);
            n.Dirty = true;
        }

        void SendUpdate(Player p, PlayerNeeds n)
        {
            if (p == null) return;
            // The client gets rounded whole numbers and a "low" flag; it never needs the raw floats.
            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"needs\",\"hunger\":{0},\"thirst\":{1},\"low\":{2}}}",
                Round(n.Hunger), Round(n.Thirst), (n.Hunger < LowThreshold || n.Thirst < LowThreshold) ? "true" : "false");
            TriggerClientEvent(p, "needs:update", json);
        }

        PlayerNeeds Cached(int src)
        {
            var p = Players[src];
            string lic = p == null ? null : License(p);
            PlayerNeeds n;
            return lic != null && _needs.TryGetValue(lic, out n) ? n : null;
        }

        static double Clamp(double v) => v < 0 ? 0 : v > 100 ? 100 : v;
        static int Round(double v) => (int)Math.Round(v);

        static string License(Player p)
        {
            string id = p.Identifiers["license"];
            return string.IsNullOrEmpty(id) ? null : id;
        }

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

        void Db(string sql, object[] args, Action<int> done) =>
            Exports["oxmysql"].update(sql, args, new Action<dynamic>(n => done(n == null ? 0 : Convert.ToInt32(n))));

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 255, 180, 100 }, args = new[] { "Needs", msg } });
    }
}
