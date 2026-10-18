using System;
using System.Collections.Generic;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Phone.Client
{
    /// <summary>
    /// Opens the phone UI and passes requests to the server. The client holds no data of its own:
    /// the server sends the whole state as JSON and pushes new messages and balance changes.
    /// </summary>
    public class PhoneClient : BaseScript
    {
        bool _open;
        int _releaseUntil;

        public PhoneClient()
        {
            // Each callback the page calls must be registered, or the request is silently dropped.
            foreach (var name in new[] { "close", "addContact", "deleteContact", "send", "markRead", "pay" }) RegisterNuiCallbackType(name);

            Nui("close", d => Close());
            Nui("addContact", d => TriggerServerEvent("phone:addContact", Str(d, "name"), Str(d, "number")));
            Nui("deleteContact", d => TriggerServerEvent("phone:deleteContact", Convert.ToInt32(d["id"])));
            Nui("send", d => TriggerServerEvent("phone:send", Str(d, "to"), Str(d, "body")));
            Nui("markRead", d => TriggerServerEvent("phone:markRead", Str(d, "from")));
            Nui("pay", d => TriggerServerEvent("phone:pay", Str(d, "to"), Convert.ToInt32(d["amount"])));

            EventHandlers["phone:state"] += new Action<string>(json => SendNuiMessage(json));
            EventHandlers["phone:push"] += new Action<string, bool>(OnPush);

            RegisterCommand("phone", new Action<int, List<object>, string>((s, a, r) => Toggle()), false);
            RegisterKeyMapping("phone", "Open / close phone", "keyboard", "F1");

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

        static string Str(IDictionary<string, object> d, string key) => Convert.ToString(d[key]);

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name == GetCurrentResourceName() && _open) Close();
        }

        void Toggle()
        {
            if (_open) { Close(); SendNuiMessage("{\"type\":\"close\"}"); return; }
            if (IsPauseMenuActive() || GetGameTimer() < _releaseUntil) return;

            _open = true;
            SetNuiFocus(true, true);
            SendNuiMessage("{\"type\":\"open\"}");
            TriggerServerEvent("phone:open");
        }

        void Close()
        {
            _open = false;
            SetNuiFocus(false, false);
            SetNuiFocusKeepInput(false);
            // Closing with ESC can also open the pause menu in the same moment; see OnTick.
            _releaseUntil = GetGameTimer() + 1500;
        }

        // The server pushes new messages and balance changes; the page decides what to show.
        void OnPush(string json, bool incoming)
        {
            SendNuiMessage(json);
            if (!incoming) return;

            // A text arrived for us: a feed notification and a sound, even with the phone closed.
            BeginTextCommandThefeedPost("STRING");
            AddTextComponentSubstringPlayerName("New message");
            EndTextCommandThefeedPostTicker(false, true);
            PlaySoundFrontend(-1, "Text_Arrive_Tone", "Phone_SoundSet_Default", true);
        }

        async System.Threading.Tasks.Task OnTick()
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
    }
}
