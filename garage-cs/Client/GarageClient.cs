using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Garage.Client
{
    /// <summary>
    /// Spawns vehicles the server says the player owns, and reports a vehicle to park.
    /// The client decides nothing about ownership: the server checks everything.
    /// </summary>
    public class GarageClient : BaseScript
    {
        public GarageClient()
        {
            EventHandlers["garage:spawn"] += new Action<string, string, int, int>(OnSpawn);
            RegisterCommand("park", new Action<int, List<object>, string>((s, a, r) => Park()), false);
        }

        async void OnSpawn(string model, string plate, int engine, int body)
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
        }

        void Park()
        {
            int ped = PlayerPedId();
            int veh = GetVehiclePedIsIn(ped, false);

            // Not inside one: fall back to the closest vehicle within a few metres.
            if (veh == 0)
            {
                var c = GetEntityCoords(ped, true);
                veh = GetClosestVehicle(c.X, c.Y, c.Z, 6.0f, 0, 70);
            }
            if (veh == 0) { Notify("No vehicle nearby."); return; }

            // We only send *which* entity and its condition; the server verifies plate and owner.
            TriggerServerEvent("garage:park",
                NetworkGetNetworkIdFromEntity(veh),
                (int)GetVehicleEngineHealth(veh),
                (int)GetVehicleBodyHealth(veh));
        }

        static void Notify(string msg) =>
            TriggerEvent("chat:addMessage", new { args = new[] { "Garage", msg } });
    }
}
