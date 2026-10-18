using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Bank.Client
{
    /// <summary>
    /// Detects nearby ATM props and lets the player press E to request their balance.
    /// The client never holds authority: it only asks the server and displays the answer.
    /// </summary>
    public class BankClient : BaseScript
    {
        static readonly string[] AtmModels = { "prop_atm_01", "prop_atm_02", "prop_atm_03", "prop_fleeca_atm" };
        const float Range = 2.2f;        // distance from the ATM at which the prompt shows
        const float SearchRadius = 6.0f; // how far we look for ATM props
        const int KeyE = 38;             // INPUT_PICKUP

        int[] _hashes;
        int _lastCheck;
        bool _nearAtm;
        bool _open;
        int _releaseUntil;

        public BankClient()
        {
            _hashes = new int[AtmModels.Length];
            for (int i = 0; i < AtmModels.Length; i++) _hashes[i] = GetHashKey(AtmModels[i]);

            EventHandlers["bank:balance"] += new Action<int, int>(OnBalance);
            RegisterCommand("atmdebug", new Action<int, System.Collections.Generic.List<object>, string>((s, a, r) => DebugAtm()), false);
            RegisterNui();
            // Escape hatches for the cursor.
            // Admin tool: report the ATM props in the loaded area so the server can build its location list.
            RegisterCommand("atmscan", new Action<int, List<object>, string>((s, a, r) => ScanAtms()), false);

            // /uifix always releases the cursor.
            RegisterCommand("uifix", new Action<int, List<object>, string>((s, a, r) => ReleaseCursor()), false);
            Tick += OnTick;
        }

        // Safety net: never leave the cursor stuck if the resource stops while the UI is open.
        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name == GetCurrentResourceName() && _open) CloseUi();
        }

        async Task OnTick()
        {
            // Scanning for props is relatively costly: do it ~4x/second, not every frame.
            if (GetGameTimer() - _lastCheck > 250)
            {
                _lastCheck = GetGameTimer();
                _nearAtm = IsNearAtm();
            }

            // Right after closing: keep forcing focus off and kill the pause menu ESC may have opened.
            bool releasing = GetGameTimer() < _releaseUntil;
            if (releasing)
            {
                SetNuiFocus(false, false);
                if (IsPauseMenuActive()) SetFrontendActive(false);
                DisableControlAction(0, 200, true); // INPUT_FRONTEND_PAUSE_ALTERNATE
            }

            if (_open) return; // UI has focus; nothing to draw or poll
            if (!_nearAtm) { if (!releasing) await Delay(100); return; }

            BeginTextCommandDisplayHelp("STRING");
            AddTextComponentSubstringPlayerName("Press ~INPUT_PICKUP~ to check your bank balance");
            EndTextCommandDisplayHelp(0, false, false, -1);

            if (IsControlJustPressed(0, KeyE)) OpenUi();
        }

        void OpenUi()
        {
            _open = true;
            SetNuiFocus(true, true);
            SendNuiMessage("{\"type\":\"open\"}");
            TriggerServerEvent("bank:requestBalance");
        }

        void ReleaseCursor()
        {
            SendNuiMessage("{\"type\":\"close\"}");
            CloseUi();
        }

        void CloseUi()
        {
            _open = false;
            SetNuiFocus(false, false);
            SetNuiFocusKeepInput(false);
            // ESC closes our UI *and* may open the pause menu at the same moment; see OnTick.
            _releaseUntil = GetGameTimer() + 1500;
        }

        // NUI callbacks arrive as "__cfx_nui:<name>" events. Amounts are forwarded as-is:
        // the server is the one that validates them.
        void RegisterNui()
        {
            // Without this, the page's fetch() to https://<resource>/<name> is never delivered to
            // the "__cfx_nui:<name>" event below (this was the stuck-cursor bug: "close" got dropped).
            foreach (var name in new[] { "close", "deposit", "withdraw", "transfer" })
                RegisterNuiCallbackType(name);

            EventHandlers["__cfx_nui:close"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) => { CloseUi(); cb("ok"); });
            EventHandlers["__cfx_nui:deposit"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                TriggerServerEvent("bank:deposit", Convert.ToInt32(d["amount"])); cb("ok");
            });
            EventHandlers["__cfx_nui:withdraw"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                TriggerServerEvent("bank:withdraw", Convert.ToInt32(d["amount"])); cb("ok");
            });
            EventHandlers["__cfx_nui:transfer"] += new Action<IDictionary<string, object>, CallbackDelegate>((d, cb) =>
            {
                TriggerServerEvent("bank:transfer", Convert.ToInt32(d["target"]), Convert.ToInt32(d["amount"])); cb("ok");
            });
        }

        /// <summary>Nearest ATM within Range, verified by existence, model and real distance.</summary>
        bool IsNearAtm() => NearestAtm(out _, out float d) && d <= Range;

        bool NearestAtm(out string model, out float distance)
        {
            model = null;
            distance = float.MaxValue;
            var pos = Game.PlayerPed.Position;

            for (int i = 0; i < _hashes.Length; i++)
            {
                int obj = GetClosestObjectOfType(pos.X, pos.Y, pos.Z, SearchRadius, (uint)_hashes[i], false, false, false);
                if (obj == 0 || !DoesEntityExist(obj)) continue;

                float d = Vector3.Distance(pos, GetEntityCoords(obj, true));
                if (d < distance) { distance = d; model = AtmModels[i]; }
            }
            return model != null;
        }

        void ScanAtms()
        {
            var me = Game.PlayerPed.Position;
            var found = new List<string>();

            foreach (var handle in GetGamePool("CObject"))
            {
                int obj = (int)handle;
                if (Array.IndexOf(_hashes, unchecked((int)GetEntityModel(obj))) < 0) continue;

                var c = GetEntityCoords(obj, true);
                if (Vector3.Distance(me, c) > 350f) continue;
                found.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", c.X, c.Y, c.Z));
            }

            TriggerEvent("chat:addMessage", new { args = new[] { "ATM", $"Found {found.Count} ATM prop(s) nearby, sending to server..." } });
            if (found.Count > 0) TriggerServerEvent("bank:atmScan", string.Join(";", found));
        }

        void DebugAtm()
        {
            string msg = NearestAtm(out string m, out float d)
                ? $"Nearest ATM: {m} at {d:F2}m (prompt range {Range}m)"
                : $"No ATM within {SearchRadius}m";
            TriggerEvent("chat:addMessage", new { args = new[] { "ATM", msg } });
        }

        void OnBalance(int cash, int bank)
        {
            SendNuiMessage($"{{\"type\":\"balance\",\"cash\":{cash},\"bank\":{bank}}}");
            if (_open) return; // the UI already shows it

            BeginTextCommandThefeedPost("STRING");
            AddTextComponentSubstringPlayerName($"Cash ~g~${cash}~s~  Bank ~b~${bank}");
            EndTextCommandThefeedPostTicker(false, false);
        }
    }
}
