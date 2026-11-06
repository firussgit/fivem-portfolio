using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Dispatch.Client
{
    /// <summary>
    /// Client half of the dispatch radio: opens the console, forwards requests to the server and
    /// draws the GPS route for the call this unit claimed. It never decides whether an action is
    /// allowed - the server checks duty, position and ownership for every step.
    /// </summary>
    public class DispatchClient : BaseScript
    {
        bool _open;
        int _releaseUntil;
        int _blip;
        int _assignedId;

        public DispatchClient()
        {
            foreach (var name in new[] { "close", "claim", "release", "enroute", "onscene", "closeCall" })
                RegisterNuiCallbackType(name);

            Nui("close", d => Close());
            Nui("claim", d => TriggerServerEvent("dispatch:claim", Convert.ToInt32(d["id"])));
            Nui("release", d => TriggerServerEvent("dispatch:release", Convert.ToInt32(d["id"])));
            Nui("enroute", d => TriggerServerEvent("dispatch:status", Convert.ToInt32(d["id"]), "enroute"));
            Nui("onscene", d => TriggerServerEvent("dispatch:status", Convert.ToInt32(d["id"]), "onscene"));
            Nui("closeCall", d => TriggerServerEvent("dispatch:close", Convert.ToInt32(d["id"]), Convert.ToString(d["outcome"])));

            EventHandlers["dispatch:state"] += new Action<string>(json => SendNuiMessage(json));
            EventHandlers["dispatch:assigned"] += new Action<string>(OnAssigned);
            EventHandlers["dispatch:unassigned"] += new Action<int>(OnUnassigned);
            EventHandlers["dispatch:new"] += new Action<string>(OnNewCall);
            EventHandlers["dispatch:denied"] += new Action(OnDenied);
            EventHandlers["dispatch:jobChanged"] += new Action(OnJobChanged);

            RegisterCommand("dispatch", new Action<int, List<object>, string>((src, args, raw) => Toggle()), false);
            RegisterKeyMapping("dispatch", "Open / close dispatch", "keyboard", "F2");

            Tick += OnTick;
        }

        void Nui(string name, Action<IDictionary<string, object>> handler)
        {
            EventHandlers["__cfx_nui:" + name] += new Action<IDictionary<string, object>, CallbackDelegate>((data, cb) =>
            {
                handler(data);
                cb("ok");
            });
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            ClearBlip();
            if (_open) Close();
            SendNuiMessage("{\"type\":\"close\"}");
        }

        void Toggle()
        {
            if (_open) { Close(); return; }
            if (IsPauseMenuActive() || GetGameTimer() < _releaseUntil) return;

            _open = true;
            SetNuiFocus(true, true);
            SendNuiMessage("{\"type\":\"open\"}");
            TriggerServerEvent("dispatch:request");
        }

        void Close()
        {
            _open = false;
            SetNuiFocus(false, false);
            SetNuiFocusKeepInput(false);
            // Closing with ESC can also open the pause menu in the same moment; see OnTick.
            _releaseUntil = GetGameTimer() + 1500;
            SendNuiMessage("{\"type\":\"close\"}");
        }

        void OnDenied()
        {
            if (_open) Close();
            Notify("Dispatch requires on-duty police or EMS.");
        }

        // Changing job takes you off duty and releases any call you held, but it is not a refusal.
        // Close the console quietly (only if it was open) and tell the player what to do next.
        void OnJobChanged()
        {
            if (!_open) return;
            Close();
            Notify("Job changed - dispatch closed. Use /duty, then F2.");
        }

        // ---------- GPS for the claimed call ----------

        void OnAssigned(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var p = payload.Split('|');
            float x, y, z;
            if (p.Length < 6 || !TryFloat(p[1], out x) || !TryFloat(p[2], out y) || !TryFloat(p[3], out z)) return;

            ClearBlip();
            _assignedId = ParseInt(p[0]);

            _blip = AddBlipForCoord(x, y, z);
            SetBlipSprite(_blip, p[4] == "medical" ? 61 : 60);
            SetBlipColour(_blip, p[4] == "medical" ? 1 : 38);
            SetBlipScale(_blip, 1.0f);
            SetBlipRoute(_blip, true);
            SetBlipRouteColour(_blip, p[4] == "medical" ? 1 : 38);
            SetBlipAsShortRange(_blip, true);

            BeginTextCommandSetBlipName("STRING");
            AddTextComponentSubstringPlayerName("Call #" + p[0] + " - " + p[5]);
            EndTextCommandSetBlipName(_blip);

            Notify("Call #" + p[0] + " assigned: " + p[5]);
        }

        void OnUnassigned(int id)
        {
            if (id == _assignedId) ClearBlip();
        }

        void ClearBlip()
        {
            if (_blip != 0) { int b = _blip; RemoveBlip(ref b); _blip = 0; }
            _assignedId = 0;
        }

        void OnNewCall(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var p = payload.Split('|');
            if (p.Length < 3) return;

            string label = p[1] == "medical" ? "Medical" : "Police";
            Notify($"Dispatch #{p[0]}: {label} - {p[2]}");
            PlaySoundFrontend(-1, "Menu_Accept", "Phone_SoundSet_Default", true);
        }

        void Notify(string text)
        {
            SetNotificationTextEntry("STRING");
            AddTextComponentString(text);
            DrawNotification(false, true);
        }

        async Task OnTick()
        {
            if (GetGameTimer() < _releaseUntil)
            {
                SetNuiFocus(false, false);
                if (IsPauseMenuActive()) SetFrontendActive(false);
                DisableControlAction(0, 200, true); // INPUT_FRONTEND_PAUSE_ALTERNATE
                return;
            }
            await Delay(250);
        }

        static bool TryFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        static int ParseInt(string value)
        {
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result);
            return result;
        }
    }
}
