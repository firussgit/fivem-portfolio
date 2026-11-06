using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Jobs.Client
{
    class WorkHub
    {
        public string Id;
        public string Label;
        public Vector3 Pos;
        public int Blip;
    }

    class WorkStop
    {
        public string Label;
        public Vector3 Pos;
    }

    /// <summary>
    /// Client half of the job activities: map blips, the stop marker, the HUD, and the single
    /// "I reached the stop" intent. It never decides whether a stop counts - the server checks
    /// the real position (OneSync), the vehicle and the time it would take to get there.
    /// </summary>
    public class JobsClient : BaseScript
    {
        const float ReachRadius = 7.5f;
        const float HudIntervalMs = 400;

        readonly List<WorkHub> _hubs = new List<WorkHub>();
        readonly List<WorkStop> _stops = new List<WorkStop>();

        bool _active;
        bool _claiming;
        int _index;
        int _payout;
        int _timeLimit;
        int _blip;
        long _startedAt;
        long _claimAgainAt;
        long _lastHud;
        long _lastHubRequest;
        long _overrideUntil;
        string _activityLabel = "";
        string _overrideStatus = "";

        public JobsClient()
        {
            EventHandlers["jobs:hubs"] += new Action<string>(OnHubs);
            EventHandlers["jobs:activity"] += new Action<string>(OnActivity);
            EventHandlers["jobs:activityError"] += new Action<string>(OnError);

            TriggerServerEvent("jobs:requestHubs");
            _lastHubRequest = GetGameTimer();
            Tick += OnTick;
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            Cleanup();
            ClearHubBlips();
            SendNuiMessage("{\"type\":\"hide\"}");
        }

        // ---------- hubs ----------

        // "id|label|x|y|z;id|label|x|y|z"
        void OnHubs(string payload)
        {
            ClearHubBlips();
            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split('|');
                float x, y, z;
                if (p.Length != 5 || !TryFloat(p[2], out x) || !TryFloat(p[3], out y) || !TryFloat(p[4], out z)) continue;

                var hub = new WorkHub { Id = p[0], Label = p[1], Pos = new Vector3(x, y, z) };
                hub.Blip = AddBlipForCoord(x, y, z);
                SetBlipSprite(hub.Blip, hub.Id == "taxi" ? 198 : 477);
                SetBlipColour(hub.Blip, hub.Id == "taxi" ? 5 : 2);
                SetBlipScale(hub.Blip, 0.9f);
                SetBlipAsShortRange(hub.Blip, false);
                BeginTextCommandSetBlipName("STRING");
                AddTextComponentSubstringPlayerName(hub.Label);
                EndTextCommandSetBlipName(hub.Blip);
                _hubs.Add(hub);
            }
        }

        void ClearHubBlips()
        {
            foreach (var h in _hubs)
                if (h.Blip != 0) { int b = h.Blip; RemoveBlip(ref b); }
            _hubs.Clear();
        }

        // ---------- activity state from the server ----------

        void OnActivity(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            var p = payload.Split('|');
            if (p.Length == 0) return;

            if (p[0] == "start")
            {
                // start|label|payout|timeLimit|stopLabel|x|y|z|...
                if (p.Length < 8) return;
                Cleanup();

                _activityLabel = p[1];
                _payout = ParseInt(p[2]);
                _timeLimit = ParseInt(p[3]);
                for (int i = 4; i + 3 < p.Length; i += 4)
                {
                    float x, y, z;
                    if (!TryFloat(p[i + 1], out x) || !TryFloat(p[i + 2], out y) || !TryFloat(p[i + 3], out z)) continue;
                    _stops.Add(new WorkStop { Label = p[i], Pos = new Vector3(x, y, z) });
                }
                if (_stops.Count == 0) return;

                _index = 0;
                _startedAt = GetGameTimer();
                _lastHud = 0;
                _claiming = false;
                _claimAgainAt = 0;
                _overrideUntil = 0;
                _active = true;

                SetCurrentBlip();
                SendHud(true, DistanceToCurrentStop(), "Drive to the marked stop");
                Notify($"Job started: {_activityLabel}");
            }
            else if (p[0] == "next")
            {
                if (!_active || p.Length < 2) return;
                int next = ParseInt(p[1]);
                if (next < 0 || next >= _stops.Count) return;

                _index = next;
                _claiming = false;
                _claimAgainAt = 0;
                SetCurrentBlip();
                SendHud(true, DistanceToCurrentStop(), "Drive to the marked stop");
            }
            else if (p[0] == "end")
            {
                string reason = p.Length > 1 ? p[1] : "ended";
                int payout = p.Length > 2 ? ParseInt(p[2]) : 0;

                Cleanup();
                SendEnd(reason, payout);
                if (reason == "completed") Notify($"Job complete: ${payout}");
                else if (reason == "expired") Notify("Job timed out.");
                else if (reason == "cancelled") Notify("Job cancelled.");
            }
        }

        void OnError(string message)
        {
            _claiming = false;
            _claimAgainAt = GetGameTimer() + 2000;
            _overrideStatus = string.IsNullOrEmpty(message) ? "Move closer to the stop." : message;
            _overrideUntil = GetGameTimer() + 2500;
            Notify(_overrideStatus);
        }

        // ---------- tick ----------

        async Task OnTick()
        {
            if (!_active)
            {
                // The first request can land before the server is listening; ask again until we have hubs.
                long idleNow = GetGameTimer();
                if (_hubs.Count == 0 && idleNow - _lastHubRequest > 10000)
                {
                    _lastHubRequest = idleNow;
                    TriggerServerEvent("jobs:requestHubs");
                }
                await Delay(1000);
                return;
            }

            long now = GetGameTimer();
            if (_claiming && now >= _claimAgainAt) _claiming = false; // no answer: allow another try
            if (_index < 0 || _index >= _stops.Count) { await Delay(500); return; }

            int ped = PlayerPedId();
            Vector3 me = GetEntityCoords(ped, true);
            WorkStop stop = _stops[_index];
            float distance = Vector3.Distance(me, stop.Pos);
            bool inVehicle = GetVehiclePedIsIn(ped, false) != 0;

            // A cylinder the player can see from the road; the blip also draws the GPS route.
            DrawMarker(1, stop.Pos.X, stop.Pos.Y, stop.Pos.Z - 1.0f, 0f, 0f, 0f, 0f, 0f, 0f, 3.0f, 3.0f, 1.5f,
                63, 185, 122, 150, false, true, 2, false, null, null, false);

            if (inVehicle && !_claiming && now >= _claimAgainAt && distance <= ReachRadius)
            {
                _claiming = true;
                _claimAgainAt = now + 4000;
                TriggerServerEvent("jobs:activityCheckpoint");
            }

            if (now - _lastHud > HudIntervalMs)
            {
                _lastHud = now;
                string status = now < _overrideUntil ? _overrideStatus
                    : !inVehicle ? "Get in a vehicle first"
                    : distance <= 12f ? "Arriving at the stop"
                    : "Drive to the marked stop";
                SendHud(true, distance, status);
            }

            await Delay(250);
        }

        // ---------- blips and HUD ----------

        void SetCurrentBlip()
        {
            ClearStopBlip();
            if (!_active || _index < 0 || _index >= _stops.Count) return;

            WorkStop stop = _stops[_index];
            _blip = AddBlipForCoord(stop.Pos.X, stop.Pos.Y, stop.Pos.Z);
            SetBlipSprite(_blip, 1);
            SetBlipColour(_blip, 5);
            SetBlipScale(_blip, 1.0f);
            SetBlipRoute(_blip, true);
            SetBlipRouteColour(_blip, 5);
            SetBlipAsShortRange(_blip, true);
            BeginTextCommandSetBlipName("STRING");
            AddTextComponentSubstringPlayerName(stop.Label);
            EndTextCommandSetBlipName(_blip);
        }

        void ClearStopBlip()
        {
            if (_blip != 0) { int b = _blip; RemoveBlip(ref b); _blip = 0; }
        }

        float DistanceToCurrentStop()
        {
            if (_index < 0 || _index >= _stops.Count) return 0f;
            return Vector3.Distance(GetEntityCoords(PlayerPedId(), true), _stops[_index].Pos);
        }

        void SendHud(bool visible, float distance, string status)
        {
            if (!visible && !_active) return;

            int left = Math.Max(0, _timeLimit - (int)((GetGameTimer() - _startedAt) / 1000));
            string objective = _active && _index >= 0 && _index < _stops.Count ? _stops[_index].Label : "";

            string json = "{\"type\":\"state\",\"visible\":" + (visible ? "true" : "false") +
                ",\"label\":" + Json(_activityLabel) +
                ",\"payout\":" + _payout +
                ",\"timeLeft\":" + left +
                ",\"index\":" + (_index + 1) +
                ",\"total\":" + _stops.Count +
                ",\"objective\":" + Json(objective) +
                ",\"distance\":" + distance.ToString("F1", CultureInfo.InvariantCulture) +
                ",\"status\":" + Json(status ?? "") + "}";
            SendNuiMessage(json);
        }

        void SendEnd(string reason, int payout) =>
            SendNuiMessage("{\"type\":\"end\",\"reason\":" + Json(reason) + ",\"payout\":" + payout + "}");

        void Cleanup()
        {
            ClearStopBlip();
            _active = false;
            _claiming = false;
            _stops.Clear();
            _index = 0;
            _overrideUntil = 0;
        }

        void Notify(string text)
        {
            SetNotificationTextEntry("STRING");
            AddTextComponentString(text);
            DrawNotification(false, true);
        }

        // ---------- parsing helpers ----------

        static bool TryFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        static int ParseInt(string value)
        {
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result);
            return result;
        }

        static string Json(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
    }
}
