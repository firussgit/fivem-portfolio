using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Inventory.Client
{
    /// <summary>
    /// Thin bridge between the game and the inventory UI. It forwards the player's intent to the
    /// server and plays effects the server has already approved; it owns no inventory data.
    /// </summary>
    public class InventoryClient : BaseScript
    {
        bool _open;
        int _releaseUntil;

        public InventoryClient()
        {
            // Each callback the page calls must be registered, or the request is silently dropped.
            foreach (var name in new[] { "close", "move", "use", "give", "discard" })
                RegisterNuiCallbackType(name);

            RegisterCommand("inventory", new Action<int, List<object>, string>((s, a, r) => Toggle()), false);
            RegisterKeyMapping("inventory", "Open inventory", "keyboard", "I");

            EventHandlers["inv:state"] += new Action<string>(json => SendNuiMessage(json));
            EventHandlers["inv:effect"] += new Action<string>(OnEffect);

            Nui("close", d => Close());
            Nui("move", d => TriggerServerEvent("inv:move", Int(d, "from"), Int(d, "to"), Int(d, "count")));
            Nui("use", d => TriggerServerEvent("inv:use", Int(d, "slot")));
            Nui("give", d => TriggerServerEvent("inv:give", Int(d, "slot"), Int(d, "count")));
            Nui("discard", d => TriggerServerEvent("inv:discard", Int(d, "slot"), Int(d, "count")));

            Tick += OnTick;
        }

        void Nui(string name, Action<IDictionary<string, object>> handler)
        {
            EventHandlers["__cfx_nui:" + name] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                handler(d);
                cb("ok");
            });
        }

        static int Int(IDictionary<string, object> d, string key) => Convert.ToInt32(d[key]);

        void Toggle()
        {
            if (_open) { Close(); SendNuiMessage("{\"type\":\"close\"}"); return; }
            if (IsPauseMenuActive()) return;

            _open = true;
            SetNuiFocus(true, true);
            SendNuiMessage("{\"type\":\"open\"}");
            TriggerServerEvent("inv:request");
        }

        void Close()
        {
            _open = false;
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
            await Delay(250);
        }

        // Effects only. Whether the item is consumed was already decided (and saved) by the server.
        void OnEffect(string item)
        {
            int ped = PlayerPedId();
            switch (item)
            {
                case "bandage":
                    SetEntityHealth(ped, Math.Min(GetEntityMaxHealth(ped), GetEntityHealth(ped) + 25));
                    Notify("You bandage your wounds.");
                    break;
                case "water":
                    Notify("You drink some water.");
                    break;
                case "sandwich":
                    Notify("You eat a sandwich.");
                    break;
                case "repair_kit":
                    int veh = GetVehiclePedIsIn(ped, false);
                    if (veh == 0)
                    {
                        var c = GetEntityCoords(ped, true);
                        veh = GetClosestVehicle(c.X, c.Y, c.Z, 5.0f, 0, 70);
                    }
                    if (veh != 0) { SetVehicleFixed(veh); Notify("Vehicle repaired."); }
                    else Notify("No vehicle nearby to repair.");
                    break;
                case "lockpick":
                    // The lockpick works on an owned car belonging to someone else. The client only
                    // says which vehicle; garage-cs validates ownership, distance and the timer.
                    var carPos = GetEntityCoords(ped, true);
                    int target = GetClosestVehicle(carPos.X, carPos.Y, carPos.Z, 5.0f, 0, 70);
                    if (target == 0) { Notify("No vehicle nearby to hotwire."); break; }
                    TriggerServerEvent("garage:hotwire", NetworkGetNetworkIdFromEntity(target));
                    Notify("You work the lock...");
                    break;
            }
        }

        static void Notify(string msg) =>
            TriggerEvent("chat:addMessage", new { args = new[] { "Inventory", msg } });
    }
}
