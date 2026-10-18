using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Fuel.Server
{
    /// <summary>
    /// Tracks fuel per licence plate. The client simulates the drain (it knows speed and RPM) and reports
    /// it, but the server is the source of truth: a report can lower the level by a plausible amount and
    /// never raise it. The only way up is paying at a station, which the server prices and verifies.
    /// </summary>
    public class FuelServer : BaseScript
    {
        const float Capacity = 100f;
        const float PricePerLiter = 3f;
        const float StationRange = 12f;
        const float VehicleRange = 15f;
        const float MaxDrainPerReport = 4f;  // litres a 5 s report may plausibly burn; anything larger is ignored
        const long CooldownMs = 400;
        const long FlushMs = 30000;
        const string StationFile = "stations.txt";

        readonly Random _rng = new Random();
        readonly List<Vector3> _stations = new List<Vector3>();
        readonly Dictionary<string, float> _fuel = new Dictionary<string, float>();
        readonly HashSet<string> _dirty = new HashSet<string>();
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();
        long _lastFlush;

        public FuelServer()
        {
            LoadStations();
            RegisterCommand("setfuel", new Action<int, List<object>, string>(SetFuelCommand), false);

            try
            {
                Exports["oxmysql"].update("ALTER TABLE player_vehicles ADD COLUMN IF NOT EXISTS fuel FLOAT NULL", new object[0],
                    new Action<dynamic>(_ => Debug.WriteLine($"[fuel] ready, {_stations.Count} station(s)")));
            }
            catch (Exception e) { Debug.WriteLine("[fuel] database unavailable (is oxmysql started?): " + e.Message); }

            Tick += async () =>
            {
                await Delay(5000);
                if (_dirty.Count > 0 && GetGameTimer() - _lastFlush >= FlushMs) Flush();
            };
        }

        // ==================================================================
        //  Level lookup and reports
        // ==================================================================

        [EventHandler("fuel:get")]
        void OnGet([FromSource] Player p, string plate)
        {
            if (!Gate(p) || !ValidPlate(plate)) return;
            plate = plate.Trim();

            float known;
            if (_fuel.TryGetValue(plate, out known)) { Send(p, plate, known); return; }

            // Owned vehicles remember their level in the database; anything else starts part-full.
            Exports["oxmysql"].query("SELECT fuel FROM player_vehicles WHERE plate = ?", new object[] { plate }, new Action<dynamic>(res =>
            {
                float level = 40f + (float)_rng.NextDouble() * 40f;
                if (res != null)
                    foreach (var r in res)
                    {
                        var row = (IDictionary<string, object>)r;
                        if (row["fuel"] != null) level = Clamp(Convert.ToSingle(row["fuel"], CultureInfo.InvariantCulture));
                        break;
                    }

                // Someone else may have asked while we were waiting; the first answer wins.
                float existing;
                if (!_fuel.TryGetValue(plate, out existing)) { _fuel[plate] = level; existing = level; }
                Send(p, plate, existing);
            }));
        }

        [EventHandler("fuel:report")]
        void OnReport([FromSource] Player p, string plate, float level)
        {
            if (!Gate(p) || !ValidPlate(plate) || float.IsNaN(level)) return;
            plate = plate.Trim();

            // They must really be driving that vehicle.
            int ped = GetPlayerPed(p.Handle);
            int veh = ped == 0 ? 0 : GetVehiclePedIsIn(ped, false);
            if (veh == 0 || GetPedInVehicleSeat(veh, -1) != ped || (GetVehicleNumberPlateText(veh) ?? "").Trim() != plate) return;

            float known;
            if (!_fuel.TryGetValue(plate, out known)) return;                 // they must have asked for it first
            if (level > known || known - level > MaxDrainPerReport) return;   // can't rise, can't drop implausibly fast

            _fuel[plate] = Clamp(level);
            _dirty.Add(plate);
        }

        // ==================================================================
        //  Refuelling
        // ==================================================================

        [EventHandler("fuel:refuel")]
        void OnRefuel([FromSource] Player p, int netId)
        {
            if (!Gate(p)) return;
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            var me = GetEntityCoords(ped);
            if (!_stations.Any(s => Vector3.Distance(me, s) <= StationRange)) { Say(p, "You are not at a fuel station."); return; }

            int veh = NetworkGetEntityFromNetworkId(netId);
            if (veh == 0 || !DoesEntityExist(veh) || Vector3.Distance(GetEntityCoords(veh), me) > VehicleRange) { Say(p, "Bring your vehicle next to the pump."); return; }
            string plate = (GetVehicleNumberPlateText(veh) ?? "").Trim();
            float level;
            if (!ValidPlate(plate) || !_fuel.TryGetValue(plate, out level)) { Say(p, "Drive the vehicle a little first."); return; }

            float missing = Capacity - level;
            if (missing < 1f) { Say(p, "The tank is already full."); return; }

            int src = int.Parse(p.Handle);
            int cash = 0;
            try
            {
                string balances = Exports["bank-cs"].getBalances(src);
                if (balances != null) int.TryParse(balances.Split(',')[0], out cash);
            }
            catch (Exception) { Say(p, "Payments are unavailable right now."); return; }

            // Fill up as far as the cash in their pocket allows.
            int liters = (int)Math.Min(Math.Floor(missing), Math.Floor(cash / PricePerLiter));
            if (liters < 1) { Say(p, $"You need at least ${(int)PricePerLiter} in cash."); return; }
            int cost = (int)Math.Ceiling(liters * PricePerLiter);

            if (!(bool)Exports["bank-cs"].removeMoney(src, "cash", cost, $"Fuel [{plate}]")) { Say(p, "Not enough cash."); return; }

            _fuel[plate] = Clamp(level + liters);
            _dirty.Add(plate);
            Send(p, plate, _fuel[plate]);
            Say(p, $"Refuelled {liters} L for ${cost}.");
        }

        // ==================================================================
        //  Persistence
        // ==================================================================

        void Flush()
        {
            _lastFlush = GetGameTimer();
            foreach (var plate in _dirty.ToList())
                Exports["oxmysql"].update("UPDATE player_vehicles SET fuel = ? WHERE plate = ?",
                    new object[] { _fuel[plate], plate }, new Action<dynamic>(_ => { }));
            _dirty.Clear();
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name == GetCurrentResourceName()) Flush();
        }

        // ==================================================================
        //  Stations (placed in-game by an admin)
        // ==================================================================

        void SetFuelCommand(int src, List<object> args, string raw)
        {
            if (src <= 0) return;
            var p = Players[src];
            if (p == null || !Gate(p)) return;
            if (!IsPlayerAceAllowed(p.Handle, "fuel.admin")) { Say(p, "You are not allowed to use that."); return; }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            var here = GetEntityCoords(ped);
            if (_stations.Any(s => Vector3.Distance(s, here) < 5f)) { Say(p, "There is already a station here."); return; }

            _stations.Add(here);
            SaveStations();
            TriggerClientEvent("fuel:stations", StationsPayload());
            Say(p, $"Fuel station placed ({_stations.Count} total). Park next to it and press E.");
        }

        [EventHandler("fuel:requestStations")]
        void OnRequestStations([FromSource] Player p)
        {
            if (p != null) TriggerClientEvent(p, "fuel:stations", StationsPayload());
        }

        string StationsPayload() =>
            string.Join(";", _stations.Select(b => string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", b.X, b.Y, b.Z)));

        void LoadStations()
        {
            string raw = LoadResourceFile(GetCurrentResourceName(), StationFile);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                float x, y, z;
                if (parts.Length == 3
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    _stations.Add(new Vector3(x, y, z));
            }
        }

        void SaveStations() =>
            SaveResourceFile(GetCurrentResourceName(), StationFile, string.Join("\n", _stations.Select(b =>
                string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", b.X, b.Y, b.Z))), -1);

        // ==================================================================
        //  Helpers
        // ==================================================================

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p != null) _lastUse.Remove(int.Parse(p.Handle));
        }

        static bool ValidPlate(string plate) => !string.IsNullOrWhiteSpace(plate) && plate.Trim().Length <= 8;
        static float Clamp(float v) => Math.Max(0f, Math.Min(Capacity, v));

        bool Gate(Player p)
        {
            if (p == null) return false;
            int src = int.Parse(p.Handle);
            long now = GetGameTimer();
            long last;
            if (_lastUse.TryGetValue(src, out last) && now - last < CooldownMs) return false;
            _lastUse[src] = now;
            return true;
        }

        void Send(Player p, string plate, float level) =>
            TriggerClientEvent(p, "fuel:level", plate, level);

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 255, 200, 60 }, args = new[] { "Fuel", msg } });
    }
}
