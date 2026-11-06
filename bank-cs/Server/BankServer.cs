using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
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
        const int DefaultCredit = 650;

        // Known ATM positions, loaded from atms.txt (one "x,y,z" per line). The server can't inspect
        // world props, so it keeps its own list and checks the player's real position against it.
        readonly List<Vector3> _atms = new List<Vector3>();
        const long CooldownMs = 500;

        // Loan rules come from convars so a server owner can tune them without a rebuild.
        int _loanMin;
        int _loanMax;
        int _loanIntervalMinutes;
        int _loanMaxDays;
        int _loanMissLimit;
        long _lastSettleTick;

        readonly Dictionary<string, Account> _accounts = new Dictionary<string, Account>();
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        public BankServer()
        {
            Cmd("balance", (p, a) => SendBalance(p));
            Cmd("deposit", (p, a) => Move(p, a, toBank: true));
            Cmd("withdraw", (p, a) => Move(p, a, toBank: false));
            Cmd("transfer", Transfer);
            Cmd("loan", LoanCommand);

            _loanMin = Math.Max(100, Math.Min(1000000, GetConvarInt("bank_loan_min", 500)));
            _loanMax = Math.Max(_loanMin, Math.Min(1000000, GetConvarInt("bank_loan_max", 25000)));
            _loanIntervalMinutes = Math.Max(1, Math.Min(10080, GetConvarInt("bank_loan_interval_minutes", 1440)));
            _loanMaxDays = Math.Max(1, Math.Min(60, GetConvarInt("bank_loan_max_days", 30)));
            _loanMissLimit = Math.Max(1, Math.Min(10, GetConvarInt("bank_loan_miss_limit", 3)));

            LoadAtms();

            // Server-side export for other resources (e.g. paychecks). Clients can't call server exports.
            Exports.Add("addBank", new Func<int, int, string, bool>(AddBank));
            Exports.Add("addMoney", new Func<int, string, int, string, bool>((src, account, amount, reason) => Change(src, account, amount, reason, +1)));
            Exports.Add("removeMoney", new Func<int, string, int, string, bool>((src, account, amount, reason) => Change(src, account, amount, reason, -1)));
            Exports.Add("getBalances", new Func<int, string>(GetBalances));

            Tick += OnTick;
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

        [EventHandler("bank:loanApply")]
        void OnLoanApply([FromSource] Player p, int amount, int days) => FromUi(p, amount, () => ApplyLoanAmount(p, amount, days));

        [EventHandler("bank:loanPay")]
        void OnLoanPay([FromSource] Player p, int amount) => FromUi(p, amount, () => PayLoanAmount(p, amount));

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

        // ---------- loans ----------

        void LoanCommand(Player p, List<object> args)
        {
            if (args.Count == 0)
            {
                var acc = Get(p);
                if (acc != null) LoanSummary(p, acc);
                return;
            }

            string sub = args[0]?.ToString().ToLowerInvariant();
            if (sub == "apply") { ApplyLoan(p, args); return; }
            if (sub == "pay") { PayLoan(p, args); return; }

            Say(p, "Usage: /loan | /loan apply <amount> [days] | /loan pay <amount>");
        }

        void ApplyLoan(Player p, List<object> args)
        {
            if (args.Count < 2 || !int.TryParse(args[1]?.ToString(), out int amount))
            {
                Say(p, "Usage: /loan apply <amount> [days]");
                return;
            }
            int days = 7;
            if (args.Count >= 3 && !int.TryParse(args[2]?.ToString(), out days))
            {
                Say(p, "Usage: /loan apply <amount> [days]");
                return;
            }
            ApplyLoanAmount(p, amount, days);
        }

        void ApplyLoanAmount(Player p, int amount, int days)
        {
            if (!RequireAtm(p)) return;
            var acc = Get(p);
            if (acc == null) return;

            if (acc.Loan != null)
            {
                Say(p, $"You already have a loan with ${acc.Loan.Balance} outstanding. Repay it first.");
                return;
            }
            if (days < 1 || days > _loanMaxDays)
            {
                Say(p, $"Choose a term between 1 and {_loanMaxDays} days.");
                return;
            }

            int limit = Math.Min(_loanMax, LoanMath.LimitFor(acc.Credit));
            if (amount < _loanMin || amount > limit)
            {
                Say(p, $"Loan amounts are ${_loanMin} to ${limit} at your credit score ({acc.Credit}).");
                return;
            }
            if (acc.Bank > int.MaxValue - amount)
            {
                Say(p, "That would overflow your bank account.");
                return;
            }

            int rate = LoanMath.RateFor(acc.Credit);
            int total = LoanMath.TotalFor(amount, rate);
            int installment = LoanMath.InstallmentFor(total, days);
            long now = NowMs();

            acc.Loan = new Loan
            {
                Id = "L" + now.ToString(CultureInfo.InvariantCulture),
                Principal = amount,
                Balance = total,
                Installment = installment,
                Total = days,
                IntervalMinutes = _loanIntervalMinutes,
                NextDue = now + _loanIntervalMinutes * 60000L,
                Created = now,
                Status = "active"
            };
            acc.Bank += amount;
            Persist(p, acc);

            Log(p, "loan:apply", amount);
            Say(p, $"Loan approved: ${amount} at {rate}% for {days} day(s). Total ${total}, ${installment} per payment. ${amount} deposited.");
            SendBalance(p);
        }

        void PayLoan(Player p, List<object> args)
        {
            if (args.Count < 2 || !int.TryParse(args[1]?.ToString(), out int amount))
            {
                Say(p, "Usage: /loan pay <amount>");
                return;
            }
            PayLoanAmount(p, amount);
        }

        void PayLoanAmount(Player p, int amount)
        {
            if (!RequireAtm(p)) return;
            var acc = Get(p);
            if (acc == null) return;

            var loan = acc.Loan;
            if (loan == null) { Say(p, "You have no loan to repay."); return; }
            if (amount <= 0) { Say(p, "Enter a whole amount greater than 0."); return; }

            if (amount > loan.Balance) amount = loan.Balance; // paying more than is owed just clears it
            if (acc.Bank < amount) { Say(p, $"Insufficient bank funds (need ${amount})."); return; }

            acc.Bank -= amount;
            loan.Balance -= amount;
            Log(p, "loan:repay", amount);

            if (loan.Balance <= 0)
            {
                FinishLoan(p, acc);
            }
            else
            {
                Say(p, $"Repaid ${amount}. ${loan.Balance} left.");
            }

            Persist(p, acc);
            SendBalance(p);
        }

        /// <summary>
        /// Applies every instalment that fell due, including while the player was offline. The clock
        /// is wall-clock based, so logging out never pauses a loan. Each payment is check-and-deduct
        /// in the same synchronous step, so it can never overdraw the account.
        /// </summary>
        void Settle(Player p, Account acc)
        {
            var loan = acc.Loan;
            if (loan == null || loan.Status != "active") return;

            long now = NowMs();
            bool changed = false;
            int guard = 0;

            while (loan.Status == "active" && now >= loan.NextDue && guard++ < 1000)
            {
                int due = Math.Min(loan.Installment, loan.Balance);
                if (due <= 0)
                {
                    FinishLoan(p, acc);
                    changed = true;
                    break;
                }

                if (acc.Bank >= due)
                {
                    acc.Bank -= due;
                    loan.Balance -= due;
                    loan.Paid++;
                    acc.Credit = LoanMath.ClampCredit(acc.Credit + 5);
                    Log(p, "loan:installment", due);

                    if (loan.Balance <= 0)
                    {
                        Say(p, $"Loan payment ${due} taken.");
                        FinishLoan(p, acc);
                        changed = true;
                        break;
                    }
                    Say(p, $"Loan payment ${due} taken. ${loan.Balance} left.");
                }
                else
                {
                    loan.Missed++;
                    acc.Credit = LoanMath.ClampCredit(acc.Credit - 25);
                    Log(p, "loan:missed", due);
                    Say(p, $"Loan payment of ${due} failed: bank balance is ${acc.Bank}. Missed {loan.Missed}/{_loanMissLimit}.");
                    if (loan.Missed >= _loanMissLimit)
                    {
                        DefaultLoan(p, acc);
                        changed = true;
                        break;
                    }
                }

                loan.NextDue += loan.IntervalMinutes * 60000L;
                changed = true;
            }

            if (changed) Persist(p, acc);
        }

        void FinishLoan(Player p, Account acc)
        {
            var loan = acc.Loan;
            if (loan == null) return;

            bool wasDefaulted = loan.Status == "defaulted";
            acc.Credit = LoanMath.ClampCredit(acc.Credit + (wasDefaulted ? 10 : 20));
            acc.Loan = null;
            Say(p, $"Loan closed. Credit score: {acc.Credit}.");
            Log(p, "loan:closed", loan.Principal);
        }

        void DefaultLoan(Player p, Account acc)
        {
            var loan = acc.Loan;
            if (loan == null || loan.Status == "defaulted") return;

            loan.Status = "defaulted";
            acc.Credit = LoanMath.ClampCredit(acc.Credit - 75);
            Say(p, $"LOAN DEFAULTED: ${loan.Balance} outstanding. Credit score: {acc.Credit}. Repay with /loan pay.");
            Log(p, "loan:default", loan.Balance);
        }

        void LoanSummary(Player p, Account acc)
        {
            int limit = Math.Min(_loanMax, LoanMath.LimitFor(acc.Credit));
            int rate = LoanMath.RateFor(acc.Credit);
            Say(p, $"Credit score {acc.Credit}: loans up to ${limit} at {rate}% interest.");

            var loan = acc.Loan;
            if (loan == null)
            {
                Say(p, $"No active loan. At an ATM: /loan apply <amount> [1-{_loanMaxDays} days].");
                return;
            }

            string when = loan.Status == "active"
                ? "next payment in " + FormatDuration(loan.NextDue - NowMs())
                : "DEFAULTED - repay now";
            Say(p, $"{loan.Id}: ${loan.Balance} left of ${loan.Principal} principal, ${loan.Installment} per payment, {loan.Missed} missed, {when}.");
        }

        // ---------- loan helpers ----------

        int LimitFor(Account acc) => Math.Min(_loanMax, LoanMath.LimitFor(acc.Credit));

        void SendLoanState(Player p, Account acc)
        {
            var loan = acc.Loan;
            long left = loan == null ? 0 : Math.Max(0, loan.NextDue - NowMs());
            TriggerClientEvent(p, "bank:loans",
                acc.Credit,
                LimitFor(acc),
                LoanMath.RateFor(acc.Credit),
                loan?.Balance ?? 0,
                loan?.Principal ?? 0,
                loan?.Installment ?? 0,
                loan?.Missed ?? 0,
                loan?.Status ?? "",
                (int)Math.Min(int.MaxValue, left / 1000));
        }

        void Persist(Player p, Account acc)
        {
            Save(p, acc);
            SaveCredit(p, acc);
            SaveLoan(p, acc);
        }

        void SaveCredit(Player p, Account acc) =>
            SetResourceKvp("credit:" + p.Identifiers["license"], acc.Credit.ToString(CultureInfo.InvariantCulture));

        void SaveLoan(Player p, Account acc) =>
            SetResourceKvp("loan:" + p.Identifiers["license"], acc.Loan == null ? "" : acc.Loan.Serialize());

        static long NowMs() =>
            (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        static string FormatDuration(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
            return $"{Math.Max(1, t.Minutes)}m";
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
                acc.Credit = ParseCredit(GetResourceKvpString("credit:" + id));
                acc.Loan = Loan.Parse(GetResourceKvpString("loan:" + id));
                _accounts[id] = acc;
            }

            // Runs on every access, not just the first: the 30-second sweep and any bank action
            // both catch up whatever fell due, and it is a no-op when nothing is owed.
            Settle(p, acc);
            return acc;
        }

        static int ParseCredit(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? LoanMath.ClampCredit(v) : DefaultCredit;

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
            SendLoanState(p, acc);
            Say(p, $"Cash: ${acc.Cash} | Bank: ${acc.Bank}");
        }

        // Check every loaded account ~30 s so an online player sees an instalment land (or a miss)
        // without touching the ATM. Offline accounts are caught up by Get() on their next login.
        async Task OnTick()
        {
            long now = GetGameTimer();
            if (now - _lastSettleTick < 30000) return;
            _lastSettleTick = now;

            foreach (var p in Players)
            {
                if (p != null) Get(p);
            }
        }

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 80, 200, 120 }, args = new[] { "Bank", msg } });

        void Log(Player p, string action, int amount) =>
            Debug.WriteLine($"[bank] {p.Name} ({p.Identifiers["license"]}) {action} ${amount}");
    }
}
