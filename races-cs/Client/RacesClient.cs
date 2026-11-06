using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Races.Client
{
    /// <summary>
    /// Client half of the time trials: map blips for the route starts, the provided race car,
    /// the checkpoint marker and the HUD. Every checkpoint and the clock are decided on the
    /// server; this script only renders the state it is told and reports the car network id.
    /// </summary>
    public class RacesClient : BaseScript
    {
        const long HudIntervalMs = 250;

        class Checkpoint
        {
            public string Label;
            public Vector3 Pos;
        }

        readonly List<Checkpoint> _checkpoints = new List<Checkpoint>();
        readonly List<int> _routeBlips = new List<int>();

        int _car;
        int _blip;
        bool _active;
        int _index;
        long _startedAt;
        long _lastHud;
        string _routeLabel = "";

        public RacesClient()
        {
            EventHandlers["races:routes"] += new Action<string>(OnRoutes);
            EventHandlers["races:spawn"] += new Action<string, string, string, string>(OnSpawn);
            EventHandlers["races:started"] += new Action<string, string>(OnStarted);
            EventHandlers["races:checkpoint"] += new Action<int, string>(OnCheckpoint);
            EventHandlers["races:end"] += new Action<string, int, bool, int>(OnEnd);
            EventHandlers["races:board"] += new Action<string, string, string>(OnBoard);

            TriggerServerEvent("races:requestRoutes");
            Tick += OnTick;
        }

        [EventHandler("onResourceStop")]
        void OnStop(string resourceName)
        {
            if (resourceName != GetCurrentResourceName()) return;
            Cleanup();
            ClearRouteBlips();
            SendNuiMessage("{\"type\":\"hide\"}");
        }

        // ---------- route start blips ----------

        // "id|label|x|y|z;id|label|x|y|z"
        void OnRoutes(string payload)
        {
            ClearRouteBlips();

            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                float x, y, z;
                if (parts.Length != 5 || !TryFloat(parts[2], out x) || !TryFloat(parts[3], out y) || !TryFloat(parts[4], out z))
                    continue;

                int blip = AddBlipForCoord(x, y, z);
                SetBlipSprite(blip, 38); // race flag
                SetBlipColour(blip, 5);
                SetBlipScale(blip, 0.9f);
                SetBlipAsShortRange(blip, false);
                BeginTextCommandSetBlipName("STRING");
                AddTextComponentSubstringPlayerName("Race: " + parts[1]);
                EndTextCommandSetBlipName(blip);
                _routeBlips.Add(blip);
            }
        }

        void ClearRouteBlips()
        {
            foreach (int id in _routeBlips)
            {
                int blip = id;
                RemoveBlip(ref blip);
            }
            _routeBlips.Clear();
        }

        // ---------- race lifecycle ----------

        void OnSpawn(string routeId, string model, string label, string checkpoints)
        {
            CleanupCar();
            ClearCheckpointBlip();
            _routeLabel = label ?? "Time trial";
            ParseCheckpoints(checkpoints);
            _ = SpawnCar(model);
        }

        async System.Threading.Tasks.Task SpawnCar(string model)
        {
            uint hash = (uint)GetHashKey(model);
            if (!IsModelInCdimage(hash) || !IsModelAVehicle(hash))
            {
                TriggerServerEvent("races:spawnFailed");
                return;
            }

            RequestModel(hash);
            for (int i = 0; i < 100 && !HasModelLoaded(hash); i++) await Delay(50);
            if (!HasModelLoaded(hash))
            {
                TriggerServerEvent("races:spawnFailed");
                return;
            }

            int ped = PlayerPedId();
            Vector3 pos = GetOffsetFromEntityInWorldCoords(ped, 0f, 4.5f, 0.5f);
            int car = CreateVehicle(hash, pos.X, pos.Y, pos.Z, GetEntityHeading(ped), true, false);

            if (car == 0)
            {
                SetModelAsNoLongerNeeded(hash);
                TriggerServerEvent("races:spawnFailed");
                return;
            }

            SetVehicleOnGroundProperly(car);
            SetVehicleNumberPlateText(car, "RACE" + ((GetGameTimer() / 1000) % 9000 + 1000));
            SetEntityAsMissionEntity(car, true, true);
            SetPedIntoVehicle(ped, car, -1);
            SetVehicleEngineOn(car, true, true, true);
            SetModelAsNoLongerNeeded(hash);
            _car = car;

            await Delay(1000);
            if (_car != 0 && DoesEntityExist(_car))
                TriggerServerEvent("races:carSpawned", NetworkGetNetworkIdFromEntity(_car));
            else
                TriggerServerEvent("races:spawnFailed");
        }

        void OnStarted(string label, string checkpoints)
        {
            _routeLabel = label ?? "Time trial";
            ParseCheckpoints(checkpoints);
            _active = true;
            _index = 0;
            _startedAt = GetGameTimer();
            _lastHud = 0;
            SetCheckpoint(0);
            SendHud();
        }

        void OnCheckpoint(int index, string label)
        {
            _index = index;
            SetCheckpoint(index);
            SendHud();
        }

        void OnEnd(string code, int milliseconds, bool personalBest, int best)
        {
            _active = false;
            CleanupCar();
            ClearCheckpointBlip();

            string json = "{\"type\":\"end\",\"code\":" + Json(code ?? "") +
                ",\"time\":" + Json(FormatTime(milliseconds)) +
                ",\"best\":" + Json(best > 0 ? FormatTime(best) : "") +
                ",\"pb\":" + (personalBest ? "true" : "false") + "}";
            SendNuiMessage(json);
        }

        // ---------- leaderboard ----------

        void OnBoard(string routeId, string routeLabel, string payload)
        {
            var entries = new List<string>();
            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length != 2) continue;

                int milliseconds;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds)) continue;

                entries.Add("{\"name\":" + Json(parts[0]) +
                            ",\"time\":" + Json(FormatTime(milliseconds)) +
                            ",\"ms\":" + milliseconds + "}");
            }

            SendNuiMessage("{\"type\":\"board\",\"route\":" + Json(routeLabel ?? routeId) +
                           ",\"entries\":[" + string.Join(",", entries) + "]}");
        }

        // ---------- per-frame rendering ----------

        async System.Threading.Tasks.Task OnTick()
        {
            if (!_active || _index < 0 || _index >= _checkpoints.Count) return;

            Checkpoint checkpoint = _checkpoints[_index];
            DrawMarker(1, checkpoint.Pos.X, checkpoint.Pos.Y, checkpoint.Pos.Z - 1.0f,
                0f, 0f, 0f, 0f, 0f, 0f, 20f, 20f, 3.0f,
                255, 200, 90, 140, false, false, 2, false, null, null, false);

            long now = GetGameTimer();
            if (now - _lastHud < HudIntervalMs) return;
            _lastHud = now;
            SendHud();

            await System.Threading.Tasks.Task.FromResult(0);
        }

        void SendHud()
        {
            if (!_active || _index < 0 || _index >= _checkpoints.Count) return;

            Checkpoint checkpoint = _checkpoints[_index];
            Vector3 me = GetEntityCoords(PlayerPedId(), true);
            float distance = Vector3.Distance(me, checkpoint.Pos);
            long elapsed = Math.Max(0, GetGameTimer() - _startedAt);

            string json = "{\"type\":\"state\",\"visible\":true" +
                ",\"label\":" + Json(_routeLabel) +
                ",\"index\":" + (_index + 1) +
                ",\"total\":" + _checkpoints.Count +
                ",\"objective\":" + Json(checkpoint.Label ?? "") +
                ",\"elapsedMs\":" + elapsed.ToString(CultureInfo.InvariantCulture) +
                ",\"distance\":" + distance.ToString("F1", CultureInfo.InvariantCulture) + "}";
            SendNuiMessage(json);
        }

        // ---------- checkpoint blip ----------

        void SetCheckpoint(int index)
        {
            ClearCheckpointBlip();
            if (index < 0 || index >= _checkpoints.Count) return;

            Checkpoint checkpoint = _checkpoints[index];
            _blip = AddBlipForCoord(checkpoint.Pos.X, checkpoint.Pos.Y, checkpoint.Pos.Z);
            SetBlipSprite(_blip, 1);
            SetBlipColour(_blip, 5);
            SetBlipScale(_blip, 1.0f);
            SetBlipRoute(_blip, true);
            SetBlipRouteColour(_blip, 5);
            SetBlipAsShortRange(_blip, true);
            BeginTextCommandSetBlipName("STRING");
            AddTextComponentSubstringPlayerName((index == _checkpoints.Count - 1 ? "Finish: " : "Checkpoint: ") + checkpoint.Label);
            EndTextCommandSetBlipName(_blip);
        }

        void ClearCheckpointBlip()
        {
            if (_blip == 0) return;
            int blip = _blip;
            RemoveBlip(ref blip);
            _blip = 0;
        }

        // ---------- cleanup ----------

        void Cleanup()
        {
            _active = false;
            CleanupCar();
            ClearCheckpointBlip();
            _checkpoints.Clear();
            _index = 0;
        }

        void CleanupCar()
        {
            int car = _car;
            _car = 0;
            if (car == 0 || !DoesEntityExist(car)) return;

            SetEntityAsMissionEntity(car, true, true);
            DeleteVehicle(ref car);
        }

        // ---------- parsing / formatting ----------

        void ParseCheckpoints(string payload)
        {
            _checkpoints.Clear();
            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                float x, y, z;
                if (parts.Length != 4 || !TryFloat(parts[1], out x) || !TryFloat(parts[2], out y) || !TryFloat(parts[3], out z))
                    continue;

                _checkpoints.Add(new Checkpoint { Label = parts[0], Pos = new Vector3(x, y, z) });
            }
        }

        static string FormatTime(int milliseconds) =>
            $"{milliseconds / 60000}:{(milliseconds / 1000) % 60:00}.{milliseconds % 1000:000}";

        static bool TryFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        static string Json(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
    }
}
