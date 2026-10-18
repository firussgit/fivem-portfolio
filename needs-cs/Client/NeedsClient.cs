using System;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Needs.Client
{
    /// <summary>
    /// Displays the HUD values the server sends and applies damage the server decides on.
    /// It stores no needs data and never changes a value itself.
    /// </summary>
    public class NeedsClient : BaseScript
    {
        public NeedsClient()
        {
            EventHandlers["needs:update"] += new Action<string>(json => SendNuiMessage(json));
            EventHandlers["needs:hurt"] += new Action<int>(OnHurt);

            // Ask for the first values once this script is running (the HUD starts hidden until then).
            TriggerServerEvent("needs:request");
        }

        // Called when the server says the player is starving or dehydrated.
        void OnHurt(int damage)
        {
            int ped = PlayerPedId();
            if (IsEntityDead(ped)) return;

            SetEntityHealth(ped, Math.Max(0, GetEntityHealth(ped) - damage));
            if (damage >= 3) TriggerEvent("chat:addMessage", new { args = new[] { "Needs", "You are dehydrated and losing health." } });
        }
    }
}
