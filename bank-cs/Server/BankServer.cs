using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Bank.Server
{
    /// <summary>
    /// Server-authoritative banking. The client can only *ask*; every amount,
    /// target and balance is validated and stored here.
    /// </summary>
    public class BankServer : BaseScript
    {
        const int StartingCash = 500;
        const int MaxAmount = 1000000;
        const int MaxCredit = 100000; // per call limit for the addBank export
        const float AtmRange = 3.5f;  // how close (metres) a player must be to a registered ATM
        const string AtmFile = "atms.txt";

        // Known ATM positions, loaded from atms.txt (one "x,y,z" per line). The server can't inspect
        // world props, so it keeps its own list and checks the player's real position against it.
        readonly List<Vector3> _atms = new List<Vector3>();
        const long CooldownMs = 500;

        readonly Dictionary<string, Account> _accounts = new Dictionary<string, Account>();
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        public BankServer()
        {
            Cmd("balance", (p, a) => SendBalance(p));
            Cmd("deposit", (p, a) => Move(p, a, toBank: true));
            Cmd("withdraw", (p, a) => Move(p, a, toBank: false));
            Cmd("transfer", Transfer);

            LoadAtms();

            // Server-side export for other resources (e.g. paychecks). Clients can't call server exports.
            Exports.Add("addBank", new Func<int, int, string, bool>(AddBank));
            Exports.Add("addMoney", new Func<int, string, int, string, bool>((src, account, amount, reason) => Change(src, account, amount, reason, +1)));
            Exports.Add("removeMoney", new Func<int, string, int, string, bool>((src, account, amount, reason) => Change(src, account, amount, reason, -1)));
            Exports.Add("getBalances", new Func<int, string>(GetBalances));

            Debug.WriteLine("[bank] server started");
        }

        /// <summary>
        /// Adds (+1) or removes (-1) money from "cash" or "bank". The check and the change happen in one
        /// synchronous step, so a purchase can never overdraw. Returns false and changes nothing on any problem.
        /// </summary>
        bool Change(int src, string account, int amount, string reason, int sign)
        {
            if (amount <= 0 || amount > MaxAmount) return false;
            if (account != "cash" && account != "bank") return false;
            var p = Players[src];
            if (p == null) return false;
            var acc = Get(p);
            if (acc == null) return false;

            int current = account == "cash" ? acc.Cash : acc.Bank;
            if (sign < 0 && current < amount) return false;
            if (sign > 0 && current > int.MaxValue - amount) return false;

            int updated = current + sign * amount;
            if (account == "cash") acc.Cash = updated; else acc.Bank = updated;
            Save(p, acc);
            Log(p, (sign > 0 ? "credit:" : "debit:") + account + ":" + (reason ?? "unknown"), amount);
            Say(p, $"{(sign > 0 ? "+" : "-")}${amount} {account} ({reason}). Cash: ${acc.Cash} | Bank: ${acc.Bank}");
            return true;
        }

        string GetBalances(int src)
        {
            var p = Players[src];
            var acc = p == null ? null : Get(p);
            return acc == null ? null : acc.Cash + "," + acc.Bank;
        }

        /// <summary>Credits a player's bank balance. Returns false (and changes nothing) on any invalid input.</summary>
        bool AddBank(int src, int amount, string reason)
        {
            if (amount <= 0 || amount > MaxCredit) return false;
            var p = Players[src];
            if (p == null) return false;
            var acc = Get(p);
            if (acc == null || acc.Bank > int.MaxValue - amount) return false;

            acc.Bank += amount;
            Save(p, acc);
            Log(p, "credit:" + (reason ?? "unknown"), amount);
            Say(p, $"+${amount} deposited ({reason}). Bank: ${acc.Bank}");
            return true;
        }

        // ---------- events from the client / NUI ----------
        // [FromSource] is how this runtime hands us the sender. A plain Action<Player> lambda
        // receives null for network events. Same validation and rate limit as the chat commands.

        [EventHandler("bank:requestBalance")]
        void OnRequestBalance([FromSource] Player p) => SendBalance(p);

        [EventHandler("bank:deposit")]
        void OnDeposit([FromSource] Player p, int amount) => FromUi(p, amount, () => MoveAmount(p, amount, true));

        [EventHandler("bank:withdraw")]
        void OnWithdraw([FromSource] Player p, int amount) => FromUi(p, amount, () => MoveAmount(p, amount, false));

        [EventHandler("bank:transfer")]
        void OnTransfer([FromSource] Player p, int targetId, int amount) => FromUi(p, amount, () => TransferAmount(p, targetId, amount));

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason) => OnDropped(p);

        // ---------- commands ----------

        void Move(Player p, List<object> args, bool toBank)
        {
            if (!TryAmount(p, args, 0, out int amount)) return;
            MoveAmount(p, amount, toBank);
        }

        void MoveAmount(Player p, int amount, bool toBank)
        {
            if (!RequireAtm(p)) return;
            var acc = Get(p);
            if (acc == null) return;

            if (toBank)
            {
                if (acc.Cash < amount) { Say(p, "You don't have that much cash."); return; }
                acc.Cash -= amount; acc.Bank += amount;
            }
            else
            {
                if (acc.Bank < amount) { Say(p, "Insufficient bank funds."); return; }
                acc.Bank -= amount; acc.Cash += amount;
            }
            Save(p, acc);
            Log(p, toBank ? "deposit" : "withdraw", amount);
            SendBalance(p);
        }

        void Transfer(Player p, List<object> args)
        {
            if (args.Count < 2 || !int.TryParse(args[0]?.ToString(), out int targetId))
            {
                Say(p, "Usage: /transfer <player id> <amount>");
                return;
            }
            if (!TryAmount(p, args, 1, out int amount)) return;
            TransferAmount(p, targetId, amount);
        }

        void TransferAmount(Player p, int targetId, int amount)
        {
            if (!RequireAtm(p)) return;
            var target = Players[targetId];
            if (target == null) { Say(p, "That player is not online."); return; }
            if (target.Handle == p.Handle) { Say(p, "You can't transfer to yourself."); return; }

            var from = Get(p);
            var to = Get(target);
            if (from == null || to == null) return;
            if (from.Bank < amount) { Say(p, "Insufficient bank funds."); return; }

            from.Bank -= amount;
            to.Bank += amount;
            Save(p, from);
            Save(target, to);

            Log(p, $"transfer->{target.Name}", amount);
            Say(p, $"Sent ${amount} to {target.Name}.");
            Say(target, $"{p.Name} sent you ${amount}.");
        }

        // ---------- ATM locations ----------

        // The player's position comes from the server (OneSync), never from the client.
        bool AtAtm(Player p)
        {
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return false;
            var pos = GetEntityCoords(ped);
            return _atms.Any(a => Vector3.Distance(pos, a) <= AtmRange);
        }

        bool RequireAtm(Player p)
        {
            if (AtAtm(p)) return true;
            Say(p, _atms.Count == 0
                ? "No ATM locations are registered yet. An admin must run /atmscan next to ATMs."
                : "You need to be at an ATM to do that.");
            return false;
        }

        // Admin-only: the client reports ATM props it can see; we keep the ones near the admin.
        [EventHandler("bank:atmScan")]
        void OnAtmScan([FromSource] Player p, string payload)
        {
            if (p == null) return;
            if (!IsPlayerAceAllowed(p.Handle, "bank.admin")) { Say(p, "You are not allowed to use that."); return; }

            int ped = GetPlayerPed(p.Handle);
            var origin = GetEntityCoords(ped);
            int added = 0;

            foreach (var item in (payload ?? "").Split(';').Take(300))
            {
                var v = ParseVec(item);
                if (v == null || Vector3.Distance(origin, v.Value) > 400f) continue; // only what's near the admin
                if (_atms.Any(a => Vector3.Distance(a, v.Value) < 1.5f)) continue;   // already known
                _atms.Add(v.Value);
                added++;
            }

            if (added > 0) SaveAtms();
            Say(p, $"ATM scan: {added} new, {_atms.Count} registered in total.");
        }

        void LoadAtms()
        {
            _atms.Clear();
            string raw = LoadResourceFile(GetCurrentResourceName(), AtmFile);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = ParseVec(line);
                if (v != null) _atms.Add(v.Value);
            }
            Debug.WriteLine($"[bank] {_atms.Count} ATM location(s) loaded");
        }

        void SaveAtms()
        {
            var lines = _atms.Select(a => string.Join(",", new[] { a.X, a.Y, a.Z }.Select(f => f.ToString("F2", CultureInfo.InvariantCulture))));
            SaveResourceFile(GetCurrentResourceName(), AtmFile, string.Join("\n", lines), -1);
        }

        static Vector3? ParseVec(string s)
        {
            var parts = (s ?? "").Trim().Split(',');
            if (parts.Length != 3) return null;
            float x, y, z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return null;
            return new Vector3(x, y, z);
        }

        // ---------- helpers ----------

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[bank] /{name} is in-game only"); return; }
                var p = Players[src];
                if (p == null || RateLimited(src)) return;
                handler(p, args);
            }), false);
        }

        void FromUi(Player p, int amount, Action action)
        {
            if (p == null || RateLimited(int.Parse(p.Handle))) return;
            if (amount <= 0 || amount > MaxAmount) { Say(p, $"Enter a whole amount between 1 and {MaxAmount}."); return; }
            action();
        }

        bool RateLimited(int src)
        {
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < CooldownMs) return true;
            _lastUse[src] = now;
            return false;
        }

        bool TryAmount(Player p, List<object> args, int index, out int amount)
        {
            amount = 0;
            if (args.Count <= index || !int.TryParse(args[index]?.ToString(), out amount)
                || amount <= 0 || amount > MaxAmount)
            {
                Say(p, $"Enter a whole amount between 1 and {MaxAmount}.");
                return false;
            }
            return true;
        }

        Account Get(Player p)
        {
            string id = p.Identifiers["license"];
            if (string.IsNullOrEmpty(id)) { Say(p, "No license identifier found."); return null; }

            if (!_accounts.TryGetValue(id, out var acc))
            {
                acc = Account.Parse(GetResourceKvpString("acc:" + id)) ?? new Account { Cash = StartingCash };
                _accounts[id] = acc;
            }
            return acc;
        }

        void Save(Player p, Account acc) =>
            SetResourceKvp("acc:" + p.Identifiers["license"], acc.Serialize());

        void OnDropped(Player p)
        {
            if (p == null) return;
            string id = p.Identifiers["license"];
            if (id != null) _accounts.Remove(id);
            _lastUse.Remove(int.Parse(p.Handle));
        }

        void SendBalance(Player p)
        {
            var acc = Get(p);
            if (acc == null) return;
            TriggerClientEvent(p, "bank:balance", acc.Cash, acc.Bank);
            Say(p, $"Cash: ${acc.Cash} | Bank: ${acc.Bank}");
        }

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 80, 200, 120 }, args = new[] { "Bank", msg } });

        void Log(Player p, string action, int amount) =>
            Debug.WriteLine($"[bank] {p.Name} ({p.Identifiers["license"]}) {action} ${amount}");
    }

    class Account
    {
        public int Cash;
        public int Bank;

        public string Serialize() => Cash + "," + Bank;

        public static Account Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int c) || !int.TryParse(parts[1], out int b)) return null;
            return new Account { Cash = c, Bank = b };
        }
    }
}
