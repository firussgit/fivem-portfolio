using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Garage.Client
{
    /// <summary>
    /// Spawns vehicles the server says the player owns, reports a vehicle to park, and applies
    /// lock / engine / locate results. The client decides nothing about ownership or state:
    /// it sends *which vehicle* and the server checks everything, then tells us what to apply.
    /// </summary>
    public class GarageClient : BaseScript
    {
        const float NearbyRange = 8f;

        // Networked vehicles whose engine the server said should stay off.
        readonly HashSet<int> _engineOff = new HashSet<int>();

        public GarageClient()
        {
            EventHandlers["garage:spawn"] += new Action<string, string, int, int, string>(OnSpawn);
            EventHandlers["garage:applyLock"] += new Action<int, bool>(OnApplyLock);
            EventHandlers["garage:applyEngine"] += new Action<int, bool>(OnApplyEngine);
            EventHandlers["garage:locate"] += new Action<string>(OnLocate);
            EventHandlers["garage:hotwired"] += new Action<int, string>(OnHotwired);
            EventHandlers["garage:theftCar"] += new Action<string, string>(OnTheftCar);

            RegisterCommand("park", new Action<int, List<object>, string>((s, a, r) => Park()), false);

            RegisterCommand("lock", new Action<int, List<object>, string>((s, a, r) => ToggleLock()), false);
            RegisterKeyMapping("lock", "Lock / unlock nearest vehicle you have keys to", "keyboard", "L");

            RegisterCommand("engine", new Action<int, List<object>, string>((s, a, r) => ToggleEngine()), false);
            RegisterKeyMapping("engine", "Engine on / off", "keyboard", "K");

            Tick += KeepEngineOff;
        }

        // ---------- spawn / park ----------

        async void OnSpawn(string model, string plate, int engine, int body, string mods)
        {
            uint hash = (uint)GetHashKey(model);
            if (!IsModelInCdimage(hash) || !IsModelAVehicle(hash)) return;

            RequestModel(hash);
            for (int i = 0; i < 100 && !HasModelLoaded(hash); i++) await Delay(50);
            if (!HasModelLoaded(hash)) return;

            int ped = PlayerPedId();
            var pos = GetOffsetFromEntityInWorldCoords(ped, 0.0f, 4.0f, 0.5f);
            int veh = CreateVehicle(hash, pos.X, pos.Y, pos.Z, GetEntityHeading(ped), true, false);

            SetVehicleNumberPlateText(veh, plate);
            SetVehicleEngineHealth(veh, (float)engine);
            SetVehicleBodyHealth(veh, (float)body);
            SetEntityAsMissionEntity(veh, true, true); // stop the game from cleaning it up
            SetPedIntoVehicle(ped, veh, -1);
            SetModelAsNoLongerNeeded(hash);

            // Announce the new vehicle to other resources on this client; whoever understands the
            // opaque "mods" string (customs-cs) applies it. The garage doesn't know what it means.
            TriggerEvent("garage:vehicleSpawned", veh, mods ?? "");

            // Give the entity a moment to replicate, then tell the server which one it is.
            // The server only accepts it if the real entity carries the plate it gave us.
            await Delay(1000);
            if (DoesEntityExist(veh)) TriggerServerEvent("garage:spawned", NetworkGetNetworkIdFromEntity(veh), plate);
        }

        void Park()
        {
            int veh = CurrentOrNearbyVehicle();
            if (veh == 0) { Notify("No vehicle nearby."); return; }

            // We only send *which* entity and its condition; the server verifies plate and owner.
            TriggerServerEvent("garage:park",
                NetworkGetNetworkIdFromEntity(veh),
                (int)GetVehicleEngineHealth(veh),
                (int)GetVehicleBodyHealth(veh));
        }

        // Dev helper: spawn a locked car a few metres away, without seating the player.
        async void OnTheftCar(string model, string plate)
        {
            uint hash = (uint)GetHashKey(model);
            if (!IsModelInCdimage(hash) || !IsModelAVehicle(hash)) return;

            RequestModel(hash);
            for (int i = 0; i < 100 && !HasModelLoaded(hash); i++) await Delay(50);
            if (!HasModelLoaded(hash)) return;

            int ped = PlayerPedId();
            var pos = GetOffsetFromEntityInWorldCoords(ped, 0.0f, 7.0f, 0.5f);
            int veh = CreateVehicle(hash, pos.X, pos.Y, pos.Z, GetEntityHeading(ped), true, false);
            SetVehicleNumberPlateText(veh, plate);
            SetVehicleDoorsLocked(veh, 2);
            SetVehicleDoorsLockedForAllPlayers(veh, true);
            SetEntityAsMissionEntity(veh, true, true);
            SetModelAsNoLongerNeeded(hash);

            await Delay(1000);
            if (DoesEntityExist(veh)) TriggerServerEvent("garage:theftSpawned", NetworkGetNetworkIdFromEntity(veh), plate);
        }

        // ---------- lock ----------
        void ToggleLock()
        {
            int veh = CurrentOrNearbyVehicle();
            if (veh == 0) { Notify("No vehicle nearby."); return; }
            TriggerServerEvent("garage:lock", NetworkGetNetworkIdFromEntity(veh));
        }

        void OnApplyLock(int netId, bool locked)
        {
            int veh = NetworkGetEntityFromNetworkId(netId);
            if (veh == 0 || !DoesEntityExist(veh)) return;

            NetworkRequestControlOfEntity(veh);
            SetVehicleDoorsLocked(veh, locked ? 2 : 1);              // 2 = locked, 1 = unlocked
            SetVehicleDoorsLockedForAllPlayers(veh, locked);
            StartVehicleHorn(veh, 120, (uint)GetHashKey("HELDDOWN"), false); // short chirp as feedback
        }

        // ---------- engine ----------

        void ToggleEngine()
        {
            int veh = GetVehiclePedIsIn(PlayerPedId(), false);
            if (veh == 0) { Notify("You need to be inside a vehicle."); return; }
            TriggerServerEvent("garage:engine", NetworkGetNetworkIdFromEntity(veh));
        }

        void OnApplyEngine(int netId, bool on)
        {
            int veh = NetworkGetEntityFromNetworkId(netId);
            if (veh == 0 || !DoesEntityExist(veh)) return;

            NetworkRequestControlOfEntity(veh);
            if (on) _engineOff.Remove(veh); else _engineOff.Add(veh);
            SetVehicleEngineOn(veh, on, false, true);
        }

        // The server already validated and recorded the theft; this only makes the car drivable.
        async void OnHotwired(int netId, string plate)
        {
            int veh = NetworkGetEntityFromNetworkId(netId);
            if (veh == 0 || !DoesEntityExist(veh)) return;

            NetworkRequestControlOfEntity(veh);
            await Delay(150);
            if (!DoesEntityExist(veh)) return;

            _engineOff.Remove(veh);
            SetVehicleDoorsLocked(veh, 1);
            SetVehicleDoorsLockedForAllPlayers(veh, false);
            SetVehicleUndriveable(veh, false);
            SetVehicleEngineOn(veh, true, true, true);
            Notify($"Hotwired [{plate}]. Move before the owner sees you.");
        }

        // The game restarts the engine whenever someone gets in, so keep it off while the server wants it off.
        async Task KeepEngineOff()
        {
            if (_engineOff.Count == 0) { await Delay(500); return; }

            int veh = GetVehiclePedIsIn(PlayerPedId(), false);
            if (veh != 0 && _engineOff.Contains(veh))
            {
                if (GetIsVehicleEngineRunning(veh)) SetVehicleEngineOn(veh, false, true, true);
                SetVehicleUndriveable(veh, true);
            }
            else
            {
                _engineOff.RemoveWhere(v => !DoesEntityExist(v));
                await Delay(250);
            }
        }

        // ---------- locate ----------

        // payload: "plate,x,y,z;plate,x,y,z" (positions read from the real entities on the server)
        async void OnLocate(string payload)
        {
            var me = GetEntityCoords(PlayerPedId(), true);
            var blips = new List<int>();
            float bestDist = float.MaxValue;
            Vector3 best = Vector3.Zero;
            string bestPlate = null;

            foreach (var entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(',');
                float x, y, z;
                if (p.Length != 4
                    || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;

                var pos = new Vector3(x, y, z);
                float dist = Vector3.Distance(me, pos);
                Notify($"[{p[0]}] is {Math.Round(dist)} m away.");

                int blip = AddBlipForCoord(x, y, z);
                SetBlipSprite(blip, 225); // car
                SetBlipColour(blip, 3);
                SetBlipFlashes(blip, true);
                BeginTextCommandSetBlipName("STRING");
                AddTextComponentSubstringPlayerName("My vehicle " + p[0]);
                EndTextCommandSetBlipName(blip);
                blips.Add(blip);

                if (dist < bestDist) { bestDist = dist; best = pos; bestPlate = p[0]; }
            }

            if (bestPlate == null) return;
            SetNewWaypoint(best.X, best.Y); // GPS route to the nearest one

            await Delay(25000); // the blips are temporary
            foreach (var b in blips) { int blip = b; RemoveBlip(ref blip); }
        }

        // ---------- helpers ----------

        // The vehicle the player is in, otherwise the closest one within a few metres.
        static int CurrentOrNearbyVehicle()
        {
            int ped = PlayerPedId();
            int veh = GetVehiclePedIsIn(ped, false);
            if (veh != 0) return veh;

            var c = GetEntityCoords(ped, true);
            return GetClosestVehicle(c.X, c.Y, c.Z, NearbyRange, 0, 70);
        }

        static void Notify(string msg) =>
            TriggerEvent("chat:addMessage", new { args = new[] { "Garage", msg } });
    }
}
