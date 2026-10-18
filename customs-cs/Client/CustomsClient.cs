using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Customs.Client
{
    /// <summary>
    /// Applies vehicle mods and bridges the customs UI. Previewing is purely local (it only changes how
    /// the vehicle looks to this player); nothing is bought or saved until the server says so.
    /// </summary>
    public class CustomsClient : BaseScript
    {
        const float PromptRange = 8f;
        const float MarkerRange = 40f;
        const int KeyE = 38; // INPUT_PICKUP

        readonly List<Vector3> _bays = new List<Vector3>();
        readonly List<int> _blips = new List<int>();

        bool _open;
        int _vehicle;
        string _current = "";  // the saved mods the server told us about (what a cancelled preview reverts to)
        int _releaseUntil;

        public CustomsClient()
        {
            // Each callback the page calls must be registered, or the request is silently dropped.
            foreach (var name in new[] { "preview", "buy", "close" }) RegisterNuiCallbackType(name);

            EventHandlers["customs:locations"] += new Action<string>(OnLocations);
            EventHandlers["customs:show"] += new Action<string, string>(OnShow);
            EventHandlers["customs:quoted"] += new Action<int, bool>((cost, discount) =>
                SendNuiMessage($"{{\"type\":\"quote\",\"cost\":{cost},\"discount\":{(discount ? "true" : "false")}}}"));
            EventHandlers["customs:applied"] += new Action<string>(mods =>
            {
                _current = mods;
                if (_vehicle != 0) Apply(_vehicle, mods);
                SendNuiMessage($"{{\"type\":\"applied\",\"mods\":\"{mods}\"}}");
            });

            // The garage announces every vehicle it spawns and passes along the stored (opaque) mods string.
            EventHandlers["garage:vehicleSpawned"] += new Action<int, string>((veh, mods) => { if (!string.IsNullOrEmpty(mods)) Apply(veh, mods); });

            EventHandlers["__cfx_nui:preview"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                string mods = Convert.ToString(d["mods"]);
                if (_open && _vehicle != 0) Apply(_vehicle, mods);
                TriggerServerEvent("customs:quote", mods); // the server prices it; the UI shows what it answers
                cb("ok");
            });
            EventHandlers["__cfx_nui:buy"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                TriggerServerEvent("customs:buy", Convert.ToString(d["mods"]));
                cb("ok");
            });
            EventHandlers["__cfx_nui:close"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) => { Close(); cb("ok"); });

            TriggerServerEvent("customs:requestLocations");
            Tick += OnTick;
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            foreach (var b in _blips) { int blip = b; RemoveBlip(ref blip); }
            if (_open) Close();
        }

        void OnLocations(string payload)
        {
            foreach (var b in _blips) { int blip = b; RemoveBlip(ref blip); }
            _blips.Clear();
            _bays.Clear();

            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(',');
                float x, y, z;
                if (p.Length != 3
                    || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;

                _bays.Add(new Vector3(x, y, z));
                int blip = AddBlipForCoord(x, y, z);
                SetBlipSprite(blip, 72); // Los Santos Customs
                SetBlipColour(blip, 5);
                SetBlipAsShortRange(blip, true);
                BeginTextCommandSetBlipName("STRING");
                AddTextComponentSubstringPlayerName("Customs");
                EndTextCommandSetBlipName(blip);
                _blips.Add(blip);
            }
        }

        void OnShow(string current, string plate)
        {
            if (_open) return;
            _vehicle = GetVehiclePedIsIn(PlayerPedId(), false);
            if (_vehicle == 0) return;

            _open = true;
            _current = current;
            SetNuiFocus(true, true);
            SendNuiMessage("{\"type\":\"open\",\"plate\":\"" + plate + "\",\"mods\":\"" + current + "\"}");
        }

        void Close()
        {
            if (_open)
            {
                // Closing without buying: put the saved look back, then tell the server the session is over.
                if (_vehicle != 0 && DoesEntityExist(_vehicle)) Apply(_vehicle, _current);
                TriggerServerEvent("customs:closed");
            }
            _open = false;
            _vehicle = 0;
            SetNuiFocus(false, false);
            SetNuiFocusKeepInput(false);
            // Closing with ESC can also open the pause menu in the same moment; see OnTick.
            _releaseUntil = GetGameTimer() + 1500;
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
            if (_open) return;
            if (_bays.Count == 0) { await Delay(1000); return; }

            int ped = PlayerPedId();
            var me = GetEntityCoords(ped, true);
            bool near = false, anyClose = false;

            foreach (var bay in _bays)
            {
                float d = Vector3.Distance(me, bay);
                if (d <= MarkerRange)
                {
                    anyClose = true;
                    DrawMarker(27, bay.X, bay.Y, bay.Z - 0.9f, 0f, 0f, 0f, 0f, 0f, 0f, 4f, 4f, 1f, 255, 120, 200, 150, false, false, 2, false, null, null, false);
                }
                if (d <= PromptRange) near = true;
            }

            int veh = GetVehiclePedIsIn(ped, false);
            if (near && veh != 0 && GetPedInVehicleSeat(veh, -1) == ped)
            {
                BeginTextCommandDisplayHelp("STRING");
                AddTextComponentSubstringPlayerName("Press ~INPUT_PICKUP~ to customise your vehicle");
                EndTextCommandDisplayHelp(0, false, false, -1);
                if (IsControlJustPressed(0, KeyE)) TriggerServerEvent("customs:open");
            }

            if (!anyClose) await Delay(500);
        }

        // "p=0;s=0;t=0;e=0;b=0;x=0;u=0": primary, secondary, tint, engine, brakes, transmission, turbo.
        static void Apply(int veh, string mods)
        {
            if (veh == 0 || !DoesEntityExist(veh)) return;
            var v = Parse(mods);

            SetVehicleModKit(veh, 0); // required before any SetVehicleMod
            SetVehicleColours(veh, v["p"], v["s"]);
            SetVehicleWindowTint(veh, v["t"]);
            SetLevel(veh, 11, v["e"]); // engine
            SetLevel(veh, 12, v["b"]); // brakes
            SetLevel(veh, 13, v["x"]); // transmission
            ToggleVehicleMod(veh, 18, v["u"] == 1); // turbo
        }

        // Level 0 is stock (-1). The game exposes only as many levels as the vehicle supports, so clamp.
        static void SetLevel(int veh, int modType, int level)
        {
            int index = Math.Min(level - 1, GetNumVehicleMods(veh, modType) - 1);
            SetVehicleMod(veh, modType, index, false);
        }

        static Dictionary<string, int> Parse(string mods)
        {
            var v = new Dictionary<string, int> { ["p"] = 0, ["s"] = 0, ["t"] = 0, ["e"] = 0, ["b"] = 0, ["x"] = 0, ["u"] = 0 };
            foreach (var part in (mods ?? "").Split(';'))
            {
                var kv = part.Split('=');
                int n;
                if (kv.Length == 2 && v.ContainsKey(kv[0]) && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    v[kv[0]] = n < 0 ? 0 : n;
            }
            return v;
        }
    }
}
