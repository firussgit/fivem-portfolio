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
        int _lastRecover;
        int _testCar;
        int _rentalCar;

        public DealerClient()
        {
            // Every callback the page uses must be registered, or the request is dropped.
            RegisterNuiCallbackType("close");
            RegisterNuiCallbackType("buy");
            RegisterNuiCallbackType("sell");
            RegisterNuiCallbackType("testdrive");
            RegisterNuiCallbackType("rent");
            RegisterNuiCallbackType("returncar");

            EventHandlers["dealer:showroom"] += new Action<float, float, float, float, string>(OnShowroom);
            EventHandlers["dealer:show"] += new Action<string>(OnShow);
            EventHandlers["dealer:state"] += new Action<string>(json => SendNuiMessage(json));
            EventHandlers["dealer:toast"] += new Action<string, bool>(OnToast);
            EventHandlers["dealer:testdrive"] += new Action<string, string>(OnTestDrive);
            EventHandlers["dealer:testdriveTick"] += new Action<string, string, int>(OnTestDriveTick);
            EventHandlers["dealer:testdriveEnd"] += new Action<string>(OnTestDriveEnd);
            EventHandlers["dealer:rentalSpawn"] += new Action<string, string>(OnRentalSpawn);
            EventHandlers["dealer:rentalTick"] += new Action<string, string, int>(OnRentalTick);
            EventHandlers["dealer:rentalEnd"] += new Action<string>(OnRentalEnd);

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

            EventHandlers["__cfx_nui:rent"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                string model = Convert.ToString(d["model"]);
                string account = Convert.ToString(d["account"]);

                // Close first: the server is about to spawn a car next to the player.
                Close();
                TriggerServerEvent("dealer:rent", model, account);
                cb("ok");
            });

            EventHandlers["__cfx_nui:returncar"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                // The page stays open; the server sends the refreshed state when the lease ends.
                TriggerServerEvent("dealer:returncar");
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
            CleanupRentalCar();
            RemoveShowroomBlip();
            if (_open) SetNuiFocus(false, false);
            SendNuiMessage("{\"type\":\"close\"}");
            SendNuiMessage("{\"type\":\"testdriveEnd\"}");
            SendNuiMessage("{\"type\":\"rentalEnd\"}");
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

        // The vanilla PDM interior floor is not streamed on every client. A player who walks
        // through the showroom glass falls into the void; put them back on the forecourt
        // instead of leaving them under the map (and letting spawn-core save that position).
        void RecoverFromVoid(int ped)
        {
            Vector3 target = new Vector3(_showroom.X, _showroom.Y, _showroom.Z + 0.5f);
            RequestCollisionAtCoord(target.X, target.Y, target.Z);

            int veh = GetVehiclePedIsIn(ped, false);
            if (veh != 0) SetPedCoordsKeepVehicle(ped, target.X, target.Y, target.Z);
            else SetEntityCoordsNoOffset(ped, target.X, target.Y, target.Z, false, false, false);

            SetEntityVelocity(ped, 0f, 0f, 0f);
            if (veh != 0) SetEntityVelocity(veh, 0f, 0f, 0f);

            _lastRecover = GetGameTimer() + 4000;
            TriggerEvent("chat:addMessage", new
            {
                args = new[] { "Dealer", "The showroom interior is not loaded here, so you were moved back to the forecourt." }
            });
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

            // Fall guard: below the forecourt and inside the building block, while falling.
            // The horizontal test is used because the void is far away in 3D by then.
            if (GetGameTimer() >= _lastRecover && me.Z < 12f)
            {
                float dx = me.X - _showroom.X;
                float dy = me.Y - _showroom.Y;
                int veh = GetVehiclePedIsIn(ped, false);
                bool inAir = IsPedFalling(ped) || (veh != 0 && IsEntityInAir(veh));

                if (dx * dx + dy * dy < 3600f && inAir) // within 60 m of the marker
                {
                    RecoverFromVoid(ped);
                    await Delay(1000);
                    return;
                }
            }

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

        // ---------- rental ----------

        async void OnRentalSpawn(string model, string plate)
        {
            CleanupRentalCar();

            uint hash = (uint)GetHashKey(model);
            if (!IsModelInCdimage(hash) || !IsModelAVehicle(hash))
            {
                TriggerServerEvent("dealer:rentalFailed");
                return;
            }

            RequestModel(hash);
            for (int i = 0; i < 100 && !HasModelLoaded(hash); i++) await Delay(50);
            if (!HasModelLoaded(hash))
            {
                TriggerServerEvent("dealer:rentalFailed");
                return;
            }

            // A resume can arrive while the client is still loading; wait for a real ped first.
            for (int i = 0; i < 150 && (PlayerPedId() == 0 || !IsPlayerPlaying(PlayerId())); i++) await Delay(100);
            if (PlayerPedId() == 0)
            {
                TriggerServerEvent("dealer:rentalFailed");
                return;
            }

            int ped = PlayerPedId();
            Vector3 pos = GetOffsetFromEntityInWorldCoords(ped, 0f, 4f, 0.5f);
            int veh = CreateVehicle(hash, pos.X, pos.Y, pos.Z, GetEntityHeading(ped), true, false);

            SetVehicleNumberPlateText(veh, plate);
            SetVehicleEngineOn(veh, true, true, false);
            SetEntityAsMissionEntity(veh, true, true);
            SetModelAsNoLongerNeeded(hash);

            _rentalCar = veh;

            // A resumed lease should not pull the player out of whatever they are driving;
            // a fresh rental always starts on foot, so there they are seated straight away.
            if (GetVehiclePedIsIn(ped, false) == 0) SetPedIntoVehicle(ped, veh, -1);

            // Give the entity a moment to replicate, then let the server verify what was spawned.
            await Delay(1000);
            if (DoesEntityExist(veh)) TriggerServerEvent("dealer:rentalStarted", NetworkGetNetworkIdFromEntity(veh), plate);
        }

        void OnRentalTick(string model, string label, int remaining)
        {
            SendNuiMessage("{\"type\":\"rental\",\"model\":" + Json(model) +
                           ",\"label\":" + Json(label) + ",\"remaining\":" + remaining + "}");
        }

        void OnRentalEnd(string reason)
        {
            CleanupRentalCar();
            SendNuiMessage("{\"type\":\"rentalEnd\"}");
        }

        void CleanupRentalCar()
        {
            int car = _rentalCar;
            if (car != 0 && DoesEntityExist(car)) DeleteEntity(ref car);
            _rentalCar = 0;
        }

        static string Json(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        }
    }
}
