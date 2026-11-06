using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Jobs.Client
{
    /// <summary>
    /// Client half of the field actions. /repair and /impound need the local vehicle entity, so the
    /// client finds the nearest one and sends its network id; the server resolves the real entity,
    /// checks the job, duty and distance, and only then approves the effect.
    /// </summary>
    public partial class JobsClient
    {
        const float RepairReach = 6.0f;
        const float ImpoundReach = 8.0f;

        void SetupActions()
        {
            EventHandlers["jobs:revive"] += new Action<string>(OnRevived);
            EventHandlers["jobs:repairOk"] += new Action<int>(OnRepairOk);
            EventHandlers["jobs:impoundOk"] += new Action<int>(OnImpoundOk);

            RegisterCommand("repair", new Action<int, List<object>, string>(
                (src, args, raw) => SendNearestVehicle("jobs:repair", RepairReach)), false);
            RegisterCommand("impound", new Action<int, List<object>, string>(
                (src, args, raw) => SendNearestVehicle("jobs:impound", ImpoundReach)), false);
        }

        void SendNearestVehicle(string eventName, float reach)
        {
            int ped = PlayerPedId();
            Vector3 me = GetEntityCoords(ped, true);
            int veh = GetClosestVehicle(me.X, me.Y, me.Z, reach, 0, 70);
            if (veh == 0 || !DoesEntityExist(veh)) { Notify("No vehicle nearby."); return; }

            TriggerServerEvent(eventName, NetworkGetNetworkIdFromEntity(veh));
        }

        // ---------- EMS revive: the patient's own client resurrects them ----------

        void OnRevived(string medic)
        {
            int ped = PlayerPedId();
            if (ped == 0 || GetEntityHealth(ped) > 0) return;

            Vector3 pos = GetEntityCoords(ped, true);
            NetworkResurrectLocalPlayer(pos.X, pos.Y, pos.Z, GetEntityHeading(ped), true, false);
            SetEntityHealth(ped, GetEntityMaxHealth(ped));
            ClearPedBloodDamage(ped);
            ClearPedTasksImmediately(ped);

            Notify((string.IsNullOrEmpty(medic) ? "EMS" : medic) + " revived you.");
        }

        // ---------- Mechanic repair ----------

        void OnRepairOk(int netId) => _ = ApplyRepair(netId);

        async Task ApplyRepair(int netId)
        {
            int veh = NetworkGetEntityFromNetworkId(netId);
            if (veh == 0 || !DoesEntityExist(veh)) { Notify("Vehicle repaired."); return; }

            NetworkRequestControlOfEntity(veh);
            await Delay(150);

            if (!DoesEntityExist(veh)) return;
            SetVehicleFixed(veh);
            SetVehicleDeformationFixed(veh);
            SetVehicleUndriveable(veh, false);
            SetVehicleEngineHealth(veh, 1000f);
            SetVehicleBodyHealth(veh, 1000f);
            SetVehicleDirtLevel(veh, 0f);

            Notify("Vehicle repaired.");
        }

        // ---------- Police impound ----------

        void OnImpoundOk(int netId)
        {
            int veh = NetworkGetEntityFromNetworkId(netId);

            // garage-cs normally deletes the real entity on the server; this is the fallback
            // when that resource is not running.
            if (veh != 0 && DoesEntityExist(veh))
            {
                SetEntityAsMissionEntity(veh, true, true);
                int local = veh;
                DeleteVehicle(ref local);
            }

            Notify("Vehicle impounded.");
        }
    }
}
