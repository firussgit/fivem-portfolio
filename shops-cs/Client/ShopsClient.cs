using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Shops.Client
{
    class ShopSpot
    {
        public string Id;
        public string Label;
        public Vector3 Pos;
        public int Blip;
    }

    /// <summary>
    /// Shows shop locations (blips and markers), the "press E" prompt, and bridges the store UI.
    /// It holds no prices or stock: the server sends those and re-validates every purchase.
    /// </summary>
    public class ShopsClient : BaseScript
    {
        const float PromptRange = 2.5f;
        const float MarkerRange = 25f;
        const int KeyE = 38; // INPUT_PICKUP

        readonly List<ShopSpot> _spots = new List<ShopSpot>();
        bool _open;
        string _openShop;
        int _releaseUntil;

        public ShopsClient()
        {
            // Each callback the page calls must be registered, or the request is silently dropped.
            RegisterNuiCallbackType("close");
            RegisterNuiCallbackType("buy");

            EventHandlers["shops:locations"] += new Action<string>(OnLocations);
            EventHandlers["shops:show"] += new Action<string>(OnShow);
            EventHandlers["shops:state"] += new Action<string>(json => SendNuiMessage(json));

            EventHandlers["__cfx_nui:close"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) => { Close(); cb("ok"); });
            EventHandlers["__cfx_nui:buy"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                if (_openShop != null)
                    TriggerServerEvent("shops:buy", _openShop, Convert.ToString(d["item"]), Convert.ToInt32(d["quantity"]), Convert.ToString(d["account"]));
                cb("ok");
            });

            TriggerServerEvent("shops:requestLocations");
            Tick += OnTick;
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            foreach (var s in _spots) if (s.Blip != 0) { int b = s.Blip; RemoveBlip(ref b); }
            if (_open) Close();
        }

        // "id,label,x,y,z;id,label,x,y,z"
        void OnLocations(string payload)
        {
            foreach (var s in _spots) if (s.Blip != 0) { int b = s.Blip; RemoveBlip(ref b); }
            _spots.Clear();

            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(',');
                float x, y, z;
                if (p.Length != 5
                    || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;

                var spot = new ShopSpot { Id = p[0], Label = p[1], Pos = new Vector3(x, y, z) };
                spot.Blip = AddBlipForCoord(x, y, z);
                SetBlipSprite(spot.Blip, 52); // grocery store
                SetBlipColour(spot.Blip, 2);
                SetBlipAsShortRange(spot.Blip, true);
                BeginTextCommandSetBlipName("STRING");
                AddTextComponentSubstringPlayerName(spot.Label);
                EndTextCommandSetBlipName(spot.Blip);
                _spots.Add(spot);
            }
        }

        void OnShow(string json)
        {
            if (_open) return;
            _open = true;
            SetNuiFocus(true, true);
            SendNuiMessage("{\"type\":\"open\"}");
            SendNuiMessage(json);
        }

        void Close()
        {
            if (_open) TriggerServerEvent("shops:closed");
            _open = false;
            _openShop = null;
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

            var me = GetEntityCoords(PlayerPedId(), true);
            ShopSpot near = null;
            bool anyClose = false;

            foreach (var s in _spots)
            {
                float d = Vector3.Distance(me, s.Pos);
                if (d <= MarkerRange)
                {
                    anyClose = true;
                    // Ped coordinates sit about a metre above the ground, so draw the marker a bit lower.
                    DrawMarker(29, s.Pos.X, s.Pos.Y, s.Pos.Z - 0.7f, 0f, 0f, 0f, 0f, 0f, 0f, 0.7f, 0.7f, 0.7f, 63, 185, 122, 170, false, true, 2, false, null, null, false);
                }
                if (d <= PromptRange) near = s;
            }

            if (near != null)
            {
                BeginTextCommandDisplayHelp("STRING");
                AddTextComponentSubstringPlayerName($"Press ~INPUT_PICKUP~ to browse the {near.Label}");
                EndTextCommandDisplayHelp(0, false, false, -1);

                if (IsControlJustPressed(0, KeyE))
                {
                    _openShop = near.Id;
                    TriggerServerEvent("shops:open", near.Id);
                }
            }

            if (!anyClose) await Delay(500); // nothing to draw: check again shortly
        }
    }
}
