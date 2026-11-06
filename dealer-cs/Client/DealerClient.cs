using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Dealer.Client
{
    /// <summary>
    /// Client half of the dealership: the showroom blip, the marker and the "press E" prompt,
    /// the NUI bridge, and the local test-drive car. Prices, ownership, the trade-in offer and
    /// the test-drive clock all live on the server; this script only renders and reports what
    /// the server asks for.
    /// </summary>
    public class DealerClient : BaseScript
    {
        const int KeyE = 38; // INPUT_PICKUP

        Vector3 _showroom;
        float _range = 8f;
        string _name = "Dealership";
        int _blip;
        bool _open;
        int _releaseUntil;
        int _testCar;

        public DealerClient()
        {
            // Every callback the page uses must be registered, or the request is dropped.
            RegisterNuiCallbackType("close");
            RegisterNuiCallbackType("buy");
            RegisterNuiCallbackType("sell");
            RegisterNuiCallbackType("testdrive");

            EventHandlers["dealer:showroom"] += new Action<float, float, float, float, string>(OnShowroom);
            EventHandlers["dealer:show"] += new Action<string>(OnShow);
            EventHandlers["dealer:state"] += new Action<string>(json => SendNuiMessage(json));
            EventHandlers["dealer:toast"] += new Action<string, bool>(OnToast);
            EventHandlers["dealer:testdrive"] += new Action<string, string>(OnTestDrive);
            EventHandlers["dealer:testdriveTick"] += new Action<string, string, int>(OnTestDriveTick);
            EventHandlers["dealer:testdriveEnd"] += new Action<string>(OnTestDriveEnd);

            EventHandlers["__cfx_nui:close"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                Close();
                cb("ok");
            });

            EventHandlers["__cfx_nui:buy"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                string model = Convert.ToString(d["model"]);
                string account = Convert.ToString(d["account"]);

                // Close first: the server may spawn the bought car through garage-cs, and the
                // player must not still be holding the cursor when that happens.
                Close();
                TriggerServerEvent("dealer:buy", model, account);
                cb("ok");
            });

            EventHandlers["__cfx_nui:sell"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                // The page stays open for a sale; the server sends the refreshed state afterwards.
                TriggerServerEvent("dealer:sell", Convert.ToInt32(d["id"]));
                cb("ok");
            });

            EventHandlers["__cfx_nui:testdrive"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                string model = Convert.ToString(d["model"]);
                Close();
                TriggerServerEvent("dealer:testdrive", model);
                cb("ok");
            });

            TriggerServerEvent("dealer:requestShowroom");
            Tick += OnTick;
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            CleanupTestCar();
            RemoveShowroomBlip();
            if (_open) SetNuiFocus(false, false);
            SendNuiMessage("{\"type\":\"close\"}");
            SendNuiMessage("{\"type\":\"testdriveEnd\"}");
        }

        // ---------- showroom ----------

        void OnShowroom(float x, float y, float z, float range, string name)
        {
            _showroom = new Vector3(x, y, z);
            _range = range > 0f ? range : 8f;
            _name = string.IsNullOrEmpty(name) ? "Dealership" : name;

            RemoveShowroomBlip();
            _blip = AddBlipForCoord(x, y, z);
            SetBlipSprite(_blip, 225); // car
            SetBlipColour(_blip, 5);   // yellow
            SetBlipAsShortRange(_blip, false);
            BeginTextCommandSetBlipName("STRING");
            AddTextComponentSubstringPlayerName(_name);
            EndTextCommandSetBlipName(_blip);
        }

        void RemoveShowroomBlip()
        {
            if (_blip == 0) return;
            int blip = _blip;
            RemoveBlip(ref blip);
            _blip = 0;
        }

        // ---------- NUI ----------

        void OnShow(string json)
        {
            if (!_open)
            {
                _open = true;
                SetNuiFocus(true, true);
                SendNuiMessage("{\"type\":\"open\"}");
            }
            SendNuiMessage(json);
        }

        void OnToast(string message, bool ok)
        {
            SendNuiMessage("{\"type\":\"toast\",\"text\":" + Json(message) + ",\"ok\":" + (ok ? "true" : "false") + "}");
        }

        void Close()
        {
            _open = false;
            SetNuiFocus(false, false);
            SetNuiFocusKeepInput(false);
            // The page is hidden explicitly: releasing the cursor alone leaves the panel drawn.
            SendNuiMessage("{\"type\":\"close\"}");
            // Closing with ESC can open the pause menu in the same moment; see OnTick.
            _releaseUntil = GetGameTimer() + 1500;
        }

        async Task OnTick()
        {
            if (GetGameTimer() < _releaseUntil)
            {
                SetNuiFocus(false, false);
                if (IsPauseMenuActive()) SetFrontendActive(false);
                DisableControlAction(0, 200, true); // INPUT_FRONTEND_PAUSE_ALTERNATE
                await Delay(0);
                return;
            }

            if (_open || (_showroom.X == 0f && _showroom.Y == 0f))
            {
                await Delay(500);
                return;
            }

            int ped = PlayerPedId();
            Vector3 me = GetEntityCoords(ped, true);
            float distance = Vector3.Distance(me, _showroom);

            if (distance > _range + 25f)
            {
                await Delay(500);
                return;
            }

            // A cylinder on the pavement, visible from the road.
            DrawMarker(1, _showroom.X, _showroom.Y, _showroom.Z - 1f,
                0f, 0f, 0f, 0f, 0f, 0f, 3f, 3f, 1.2f,
                255, 190, 70, 120, false, false, 2, false, null, null, false);

            if (distance <= _range)
            {
                bool onFoot = GetVehiclePedIsIn(ped, false) == 0;
                BeginTextCommandDisplayHelp("STRING");
                AddTextComponentSubstringPlayerName(onFoot
                    ? $"Press ~INPUT_PICKUP~ to browse {_name}"
                    : "Get out of the vehicle to browse the showroom");
                EndTextCommandDisplayHelp(0, false, false, -1);

                if (onFoot)
                {
                    DisableControlAction(0, KeyE, true);
                    if (IsControlJustPressed(0, KeyE)) TriggerServerEvent("dealer:requestOpen");
                }
            }

            await Delay(0);
        }

        // ---------- test drive ----------

        async void OnTestDrive(string model, string plate)
        {
            CleanupTestCar();

            uint hash = (uint)GetHashKey(model);
            if (!IsModelInCdimage(hash) || !IsModelAVehicle(hash))
            {
                TriggerServerEvent("dealer:testdriveFailed");
                return;
            }

            RequestModel(hash);
            for (int i = 0; i < 100 && !HasModelLoaded(hash); i++) await Delay(50);
            if (!HasModelLoaded(hash))
            {
                TriggerServerEvent("dealer:testdriveFailed");
                return;
            }

            int ped = PlayerPedId();
            Vector3 pos = GetOffsetFromEntityInWorldCoords(ped, 0f, 4f, 0.5f);
            int veh = CreateVehicle(hash, pos.X, pos.Y, pos.Z, GetEntityHeading(ped), true, false);

            SetVehicleNumberPlateText(veh, plate);
            SetVehicleEngineOn(veh, true, true, false);
            SetVehicleDoorsLocked(veh, 1);
            SetEntityAsMissionEntity(veh, true, true);
            SetPedIntoVehicle(ped, veh, -1);
            SetModelAsNoLongerNeeded(hash);

            _testCar = veh;

            // Give the entity a moment to replicate, then let the server verify what was spawned.
            await Delay(1000);
            if (DoesEntityExist(veh)) TriggerServerEvent("dealer:testdriveStarted", NetworkGetNetworkIdFromEntity(veh), plate);
        }

        void OnTestDriveTick(string model, string label, int remaining)
        {
            SendNuiMessage("{\"type\":\"testdrive\",\"model\":" + Json(model) +
                           ",\"label\":" + Json(label) + ",\"remaining\":" + remaining + "}");
        }

        void OnTestDriveEnd(string reason)
        {
            CleanupTestCar();
            SendNuiMessage("{\"type\":\"testdriveEnd\"}");
        }

        void CleanupTestCar()
        {
            int car = _testCar;
            if (car != 0 && DoesEntityExist(car)) DeleteEntity(ref car);
            _testCar = 0;
        }

        static string Json(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        }
    }
}
