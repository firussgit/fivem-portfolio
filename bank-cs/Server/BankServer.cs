using System;
using System.Collections.Generic;
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
        const long CooldownMs = 500;

        readonly Dictionary<string, Account> _accounts = new Dictionary<string, Account>();
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        public BankServer()
        {
            EventHandlers["bank:requestBalance"] += new Action<Player>(p => SendBalance(p));
            // Events used by the NUI. Same validation and rate limit as the chat commands.
            EventHandlers["bank:deposit"] += new Action<Player, int>((p, amt) => FromUi(p, amt, () => MoveAmount(p, amt, true)));
            EventHandlers["bank:withdraw"] += new Action<Player, int>((p, amt) => FromUi(p, amt, () => MoveAmount(p, amt, false)));
            EventHandlers["bank:transfer"] += new Action<Player, int, int>((p, id, amt) => FromUi(p, amt, () => TransferAmount(p, id, amt)));
            EventHandlers["playerDropped"] +=new Action<Player, string>((p, _) => OnDropped(p));

            Cmd("balance", (p, a) => SendBalance(p));
            Cmd("deposit", (p, a) => Move(p, a, toBank: true));
            Cmd("withdraw", (p, a) => Move(p, a, toBank: false));
            Cmd("transfer", Transfer);

            Debug.WriteLine("[bank] server started");
        }

        // ---------- commands ----------

        void Move(Player p, List<object> args, bool toBank)
        {
            if (!TryAmount(p, args, 0, out int amount)) return;
            MoveAmount(p, amount, toBank);
        }

        void MoveAmount(Player p, int amount, bool toBank)
        {
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
