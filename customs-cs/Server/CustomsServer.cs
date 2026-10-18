using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Customs.Server
{
    /// <summary>
    /// The mods of one vehicle. Serialised as "p=0;s=0;t=0;e=0;b=0;x=0;u=0" and stored in the
    /// owned vehicle's `mods` column, which the garage treats as opaque data.
    /// </summary>
    class Mods
    {
        public int Primary, Secondary, Tint, Engine, Brakes, Transmission, Turbo;

        public string Serialize() =>
            $"p={Primary};s={Secondary};t={Tint};e={Engine};b={Brakes};x={Transmission};u={Turbo}";

        // Strict: unknown keys, missing keys or out-of-range values make the whole string invalid.
        // The server never builds a mods string from anything it hasn't validated this way.
        public static Mods Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return new Mods(); // a vehicle with no saved mods is stock
            var values = new Dictionary<string, int>();
            foreach (var part in s.Split(';'))
            {
                var kv = part.Split('=');
                int v;
                if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return null;
                values[kv[0]] = v;
            }
            string[] keys = { "p", "s", "t", "e", "b", "x", "u" };
            if (values.Count != keys.Length || keys.Any(k => !values.ContainsKey(k))) return null;

            var m = new Mods
            {
                Primary = values["p"], Secondary = values["s"], Tint = values["t"],
                Engine = values["e"], Brakes = values["b"], Transmission = values["x"], Turbo = values["u"],
            };
            return m.IsValid() ? m : null;
        }

        public bool IsValid() =>
            InRange(Primary, 0, 160) && InRange(Secondary, 0, 160) && InRange(Tint, 0, 6) &&
            InRange(Engine, 0, 4) && InRange(Brakes, 0, 3) && InRange(Transmission, 0, 3) && InRange(Turbo, 0, 1);

        static bool InRange(int v, int lo, int hi) => v >= lo && v <= hi;
    }

    class Session
    {
        public string Plate;
        public Mods Current;   // what the database says the vehicle has right now
        public int NetId;
    }

    /// <summary>
    /// Vehicle customisation. The player previews changes locally, but the server decides everything that
    /// matters: it reads the vehicle's saved mods from the database, prices only what actually changed, takes
    /// the money, and saves the result against the owner's row (an UPDATE that only matches if they own it).
    /// </summary>
    public class CustomsServer : BaseScript
    {
        // Prices live in code. Upgrades are charged per level gained; downgrading is free.
        const int ColorPrice = 200;
        const int TintPrice = 150;
        const int EnginePerLevel = 600;
        const int BrakesPerLevel = 350;
        const int TransmissionPerLevel = 450;
        const int TurboPrice = 1800;
        const int MechanicDiscountPercent = 20;

        const float BayRange = 10f;
        const long CooldownMs = 300;
        const string LocationFile = "customs.txt";

        readonly List<Vector3> _bays = new List<Vector3>();
        readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>(); // player -> open customs session
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        public CustomsServer()
        {
            LoadBays();
            RegisterCommand("setcustoms", new Action<int, List<object>, string>(SetCustomsCommand), false);

            try
            {
                // The column is normally created by garage-cs; this just makes start order irrelevant.
                Db("ALTER TABLE player_vehicles ADD COLUMN IF NOT EXISTS mods VARCHAR(512) NULL", new object[0],
                   _ => Debug.WriteLine($"[customs] ready, {_bays.Count} customs bay(s)"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[customs] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Opening a session
        // ==================================================================

        [EventHandler("customs:open")]
        void OnOpen([FromSource] Player p)
        {
            if (!Gate(p)) return;
            string license = License(p);
            if (license == null) return;

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            if (!_bays.Any(b => Vector3.Distance(GetEntityCoords(ped), b) <= BayRange)) { Say(p, "You are not at a customs bay."); return; }

            // They must be driving a real vehicle, and the DB must say it's theirs.
            int veh = GetVehiclePedIsIn(ped, false);
            if (veh == 0 || GetPedInVehicleSeat(veh, -1) != ped) { Say(p, "Drive your vehicle into the bay first."); return; }
            string plate = (GetVehicleNumberPlateText(veh) ?? "").Trim();

            Query("SELECT mods FROM player_vehicles WHERE plate = ? AND owner = ?", new object[] { plate, license }, rows =>
            {
                if (rows.Count != 1) { Say(p, "You don't own this vehicle."); return; }

                var current = Mods.Parse(rows[0]["mods"] == null ? "" : rows[0]["mods"].ToString()) ?? new Mods();
                _sessions[int.Parse(p.Handle)] = new Session { Plate = plate, Current = current, NetId = NetworkGetNetworkIdFromEntity(veh) };
                TriggerClientEvent(p, "customs:show", current.Serialize(), plate);
            });
        }

        [EventHandler("customs:closed")]
        void OnClosed([FromSource] Player p)
        {
            if (p != null) _sessions.Remove(int.Parse(p.Handle));
        }

        // ==================================================================
        //  Quote and purchase
        // ==================================================================

        [EventHandler("customs:quote")]
        void OnQuote([FromSource] Player p, string desiredString)
        {
            if (!Gate(p)) return;
            Session s; Mods desired;
            if (!TryGetSession(p, desiredString, out s, out desired)) return;

            int src = int.Parse(p.Handle);
            bool discount = HasMechanicDiscount(src);
            TriggerClientEvent(p, "customs:quoted", Price(s.Current, desired, discount), discount);
        }

        [EventHandler("customs:buy")]
        void OnBuy([FromSource] Player p, string desiredString)
        {
            if (!Gate(p)) return;
            Session s; Mods desired;
            if (!TryGetSession(p, desiredString, out s, out desired)) return;

            // The vehicle must still be where the session says (they can't open it and drive away to buy).
            int ped = GetPlayerPed(p.Handle);
            int veh = GetVehiclePedIsIn(ped, false);
            if (veh == 0 || (GetVehicleNumberPlateText(veh) ?? "").Trim() != s.Plate
                || !_bays.Any(b => Vector3.Distance(GetEntityCoords(ped), b) <= BayRange)) { Say(p, "You must stay in the bay with your vehicle."); return; }

            string license = License(p);
            int src = int.Parse(p.Handle);
            if (desired.Serialize() == s.Current.Serialize()) { Say(p, "Nothing to change."); return; }

            int cost = Price(s.Current, desired, HasMechanicDiscount(src));
            string plate = s.Plate;
            string note = $"Customs [{plate}]";

            // Take the money first (the bank checks the balance and deducts in one step)...
            if (cost > 0)
            {
                try
                {
                    if (!(bool)Exports["bank-cs"].removeMoney(src, "bank", cost, note)) { Say(p, $"Not enough money in your bank account (${cost} needed)."); return; }
                }
                catch (Exception e)
                {
                    Debug.WriteLine("[customs] purchase aborted, bank-cs unavailable: " + e.Message);
                    Say(p, "Customs is unavailable right now.");
                    return;
                }
            }

            // ...then save against the owner's row. The WHERE clause only matches if they still own it.
            Db("UPDATE player_vehicles SET mods = ? WHERE plate = ? AND owner = ?", new object[] { desired.Serialize(), plate, license }, affected =>
            {
                if (affected != 1)
                {
                    if (cost > 0) Exports["bank-cs"].addMoney(src, "bank", cost, "refund: " + note);
                    Say(p, "Could not save the changes, you were not charged.");
                    return;
                }

                s.Current = desired;
                Debug.WriteLine($"[customs] {p.Name} customised [{plate}] for ${cost}: {desired.Serialize()}");
                Say(p, cost > 0 ? $"Done! Charged ${cost}." : "Saved.");
                TriggerClientEvent(p, "customs:applied", desired.Serialize());
            });
        }

        // Parses and validates what the player wants. Anything malformed or out of range is dropped silently.
        bool TryGetSession(Player p, string desiredString, out Session session, out Mods desired)
        {
            desired = null;
            if (!_sessions.TryGetValue(int.Parse(p.Handle), out session)) return false;
            desired = Mods.Parse(desiredString);
            return desired != null;
        }

        int Price(Mods current, Mods desired, bool discount)
        {
            long total = 0;
            if (desired.Primary != current.Primary) total += ColorPrice;
            if (desired.Secondary != current.Secondary) total += ColorPrice;
            if (desired.Tint != current.Tint) total += TintPrice;
            total += Math.Max(0, desired.Engine - current.Engine) * (long)EnginePerLevel;
            total += Math.Max(0, desired.Brakes - current.Brakes) * (long)BrakesPerLevel;
            total += Math.Max(0, desired.Transmission - current.Transmission) * (long)TransmissionPerLevel;
            if (desired.Turbo > current.Turbo) total += TurboPrice;

            if (discount) total = total * (100 - MechanicDiscountPercent) / 100;
            return (int)total;
        }

        // A mechanic who is on duty gets a discount (jobs-cs). If jobs-cs isn't running, there's simply no discount.
        bool HasMechanicDiscount(int src)
        {
            try { return (bool)Exports["jobs-cs"].isOnDuty(src, "mechanic"); }
            catch (Exception) { return false; }
        }

        // ==================================================================
        //  Bay locations (placed in-game by an admin)
        // ==================================================================

        void SetCustomsCommand(int src, List<object> args, string raw)
        {
            if (src <= 0) return;
            var p = Players[src];
            if (p == null || !Gate(p)) return;
            if (!IsPlayerAceAllowed(p.Handle, "customs.admin")) { Say(p, "You are not allowed to use that."); return; }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            var here = GetEntityCoords(ped);
            if (_bays.Any(b => Vector3.Distance(b, here) < 5f)) { Say(p, "There is already a customs bay here."); return; }

            _bays.Add(here);
            SaveBays();
            TriggerClientEvent("customs:locations", BaysPayload());
            Say(p, $"Customs bay placed ({_bays.Count} total). Drive in and press E.");
        }

        [EventHandler("customs:requestLocations")]
        void OnRequestLocations([FromSource] Player p)
        {
            if (p != null) TriggerClientEvent(p, "customs:locations", BaysPayload());
        }

        string BaysPayload() =>
            string.Join(";", _bays.Select(b => string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", b.X, b.Y, b.Z)));

        void LoadBays()
        {
            string raw = LoadResourceFile(GetCurrentResourceName(), LocationFile);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                float x, y, z;
                if (parts.Length == 3
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    _bays.Add(new Vector3(x, y, z));
            }
        }

        void SaveBays() =>
            SaveResourceFile(GetCurrentResourceName(), LocationFile, string.Join("\n", _bays.Select(b =>
                string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", b.X, b.Y, b.Z))), -1);

        // ==================================================================
        //  Helpers
        // ==================================================================

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            int src = int.Parse(p.Handle);
            _sessions.Remove(src);
            _lastUse.Remove(src);
        }

        bool Gate(Player p)
        {
            if (p == null) return false;
            int src = int.Parse(p.Handle);
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < CooldownMs) return false;
            _lastUse[src] = now;
            return true;
        }

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
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 255, 120, 200 }, args = new[] { "Customs", msg } });
    }
}
