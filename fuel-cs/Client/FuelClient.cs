using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Fuel.Client
{
    /// <summary>
    /// Simulates the fuel drain for the vehicle the player is driving and shows the gauge. The level lives
    /// on the server (per plate): the client asks for it on entering a vehicle, reports the drain every few
    /// seconds, and receives a new level after paying at a station.
    /// </summary>
    public class FuelClient : BaseScript
    {
        const float PromptRange = 12f;
        const float MarkerRange = 40f;
        const int KeyE = 38; // INPUT_PICKUP
        const int ReportEveryMs = 5000;

        // Litres per second: a little at idle, more with speed (metres per second).
        const float IdleBurn = 0.015f;
        const float SpeedBurn = 0.0035f;

        readonly List<Vector3> _stations = new List<Vector3>();
        readonly List<int> _blips = new List<int>();

        string _plate = "";
        float _level = -1f;         // -1 until the server has told us
        int _lastReport;
        int _lastAsk;
        bool _weDisabled;           // we made the vehicle undriveable (empty tank)

        public FuelClient()
        {
            EventHandlers["fuel:stations"] += new Action<string>(OnStations);
            EventHandlers["fuel:level"] += new Action<string, float>((plate, level) =>
            {
                if (plate == _plate) _level = level;
            });

            TriggerServerEvent("fuel:requestStations");
            Tick += OnTick;
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            foreach (var b in _blips) { int blip = b; RemoveBlip(ref blip); }
        }

        void OnStations(string payload)
        {
            foreach (var b in _blips) { int blip = b; RemoveBlip(ref blip); }
            _blips.Clear();
            _stations.Clear();

            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(',');
                float x, y, z;
                if (p.Length != 3
                    || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;

                _stations.Add(new Vector3(x, y, z));
                int blip = AddBlipForCoord(x, y, z);
                SetBlipSprite(blip, 361); // fuel pump
                SetBlipColour(blip, 1);
                SetBlipAsShortRange(blip, true);
                BeginTextCommandSetBlipName("STRING");
                AddTextComponentSubstringPlayerName("Fuel station");
                EndTextCommandSetBlipName(blip);
                _blips.Add(blip);
            }
        }

        async Task OnTick()
        {
            int ped = PlayerPedId();
            int veh = GetVehiclePedIsIn(ped, false);
            bool driving = veh != 0 && GetPedInVehicleSeat(veh, -1) == ped;

            if (driving) Drive(veh);
            else { _plate = ""; _level = -1f; _weDisabled = false; }

            DrawStations(ped, veh);

            if (!driving && _stations.Count == 0) await Delay(500);
        }

        void Drive(int veh)
        {
            string plate = (GetVehicleNumberPlateText(veh) ?? "").Trim();
            if (plate != _plate)
            {
                _plate = plate;
                _level = -1f;
                _weDisabled = false;
            }

            // Ask the server for the level until it answers (retrying gently in case the first ask was dropped).
            if (_level < 0f)
            {
                if (GetGameTimer() - _lastAsk > 2000) { _lastAsk = GetGameTimer(); TriggerServerEvent("fuel:get", plate); }
                return;
            }

            // Burn fuel only while the engine runs.
            if (GetIsVehicleEngineRunning(veh) && _level > 0f)
            {
                float burn = (IdleBurn + GetEntitySpeed(veh) * SpeedBurn) * GetFrameTime();
                _level = Math.Max(0f, _level - burn);
            }

            if (_level <= 0f)
            {
                if (GetIsVehicleEngineRunning(veh)) SetVehicleEngineOn(veh, false, true, true);
                SetVehicleUndriveable(veh, true);
                _weDisabled = true;
            }
            else if (_weDisabled)
            {
                SetVehicleUndriveable(veh, false);   // refuelled
                _weDisabled = false;
            }

            if (GetGameTimer() - _lastReport > ReportEveryMs)
            {
                _lastReport = GetGameTimer();
                TriggerServerEvent("fuel:report", plate, _level);
            }

            DrawGauge(_level);
        }

        void DrawStations(int ped, int veh)
        {
            if (_stations.Count == 0) return;
            var me = GetEntityCoords(ped, true);
            foreach (var s in _stations)
            {
                float d = Vector3.Distance(me, s);
                if (d > MarkerRange) continue;
                DrawMarker(1, s.X, s.Y, s.Z - 1f, 0f, 0f, 0f, 0f, 0f, 0f, 3f, 3f, 0.6f, 255, 60, 60, 120, false, false, 2, false, null, null, false);
                if (d > PromptRange) continue;

                int target = veh != 0 ? veh : GetClosestVehicle(me.X, me.Y, me.Z, 8f, 0, 70);
                if (target == 0) continue;

                BeginTextCommandDisplayHelp("STRING");
                AddTextComponentSubstringPlayerName("Press ~INPUT_PICKUP~ to refuel (~g~$3~s~ per litre)");
                EndTextCommandDisplayHelp(0, false, false, -1);
                if (IsControlJustPressed(0, KeyE)) TriggerServerEvent("fuel:refuel", NetworkGetNetworkIdFromEntity(target));
                break;
            }
        }

        static void DrawGauge(float level)
        {
            int r = 255, g = 255, b = 255;
            if (level < 10f) { g = 60; b = 60; }
            else if (level < 25f) { g = 190; b = 40; }

            SetTextFont(4);
            SetTextScale(0.0f, 0.45f);
            SetTextColour(r, g, b, 230);
            SetTextOutline();
            SetTextRightJustify(true);
            SetTextWrap(0f, 0.985f);
            BeginTextCommandDisplayText("STRING");
            AddTextComponentSubstringPlayerName($"FUEL {Math.Round(level)}%");
            EndTextCommandDisplayText(0.985f, 0.70f);
        }
    }
}
