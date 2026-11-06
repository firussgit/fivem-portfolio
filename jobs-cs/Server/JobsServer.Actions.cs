using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Jobs.Server
{
    /// <summary>
    /// On-duty field actions: police fines and impound, EMS revive, mechanic repair.
    ///
    /// The client only sends intent ("this vehicle", "that player"). The server checks the job,
    /// duty, the real OneSync position, ownership and stock before anything happens, then records
    /// the action in job_actions and pays through bank-cs.
    /// </summary>
    public partial class JobsServer
    {
        const float FineRange = 6.0f;
        const float ReviveRange = 4.0f;
        const float RepairRange = 6.0f;
        const float ImpoundRange = 8.0f;
        const int MinFine = 50;
        const long FineCooldownMs = 5000;

        int _revivePay;
        int _repairPay;
        int _fineMax;
        long _reviveCooldownMs;

        readonly Dictionary<string, long> _lastRevive = new Dictionary<string, long>(); // target licence -> next allowed
        readonly Dictionary<string, long> _lastFine = new Dictionary<string, long>();   // target licence -> last fine

        void SetupActions()
        {
            _revivePay = Math.Max(0, Math.Min(100000, GetConvarInt("ems_revive_pay", 200)));
            _repairPay = Math.Max(0, Math.Min(100000, GetConvarInt("mechanic_repair_pay", 250)));
            _fineMax = Math.Max(MinFine, Math.Min(100000, GetConvarInt("police_fine_max", 2500)));
            _reviveCooldownMs = Math.Max(10, GetConvarInt("ems_revive_cooldown_seconds", 60)) * 1000L;

            Cmd("fine", FineCommand);
            Cmd("revive", ReviveCommand);

            // /repair and /impound are client commands: only the client can see the nearby vehicle.
            // They send its network id, and the server resolves the real entity again.
            CreateActionsTable();
        }

        void CreateActionsTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS job_actions (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, " +
                   "actor_license VARCHAR(64) NOT NULL, actor_name VARCHAR(64) NOT NULL, " +
                   "action VARCHAR(16) NOT NULL, target_license VARCHAR(64) NULL, target_name VARCHAR(64) NULL, " +
                   "amount INT NOT NULL DEFAULT 0, detail VARCHAR(160) NOT NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "INDEX idx_actor (actor_license), INDEX idx_action (action))", new object[0],
                   _ => Debug.WriteLine("[jobs] field action history ready"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[jobs] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Police: fine
        // ==================================================================

        void FineCommand(Player p, List<object> args)
        {
            With(p, s =>
            {
                if (s.Job != "police" || !s.OnDuty) { Say(p, "Only on-duty police can issue fines."); return; }

                if (args.Count < 3
                    || !int.TryParse(args[0]?.ToString(), out int id)
                    || !int.TryParse(args[1]?.ToString(), out int amount))
                {
                    Say(p, "Usage: /fine <player id> <amount> <reason>");
                    return;
                }

                if (id <= 0) { Say(p, "That player is not online."); return; }
                var target = Players[id];
                if (target == null) { Say(p, "That player is not online."); return; }
                if (target == p) { Say(p, "You can't fine yourself."); return; }
                if (amount < MinFine || amount > _fineMax)
                {
                    Say(p, $"The fine must be between ${MinFine} and ${_fineMax}.");
                    return;
                }

                string reason = CleanText(string.Join(" ", args.Skip(2).Select(x => x?.ToString())), 80);
                if (reason.Length < 3) { Say(p, "Give a short reason for the fine."); return; }

                int ped = GetPlayerPed(p.Handle);
                int targetPed = GetPlayerPed(target.Handle);
                if (ped == 0 || targetPed == 0) return;

                if (Dist2D(GetEntityCoords(ped), GetEntityCoords(targetPed)) > FineRange)
                {
                    Say(p, "You need to be next to the player to fine them.");
                    return;
                }

                string targetLicense = License(target);
                if (targetLicense == null) return;

                long now = GetGameTimer();
                if (_lastFine.TryGetValue(targetLicense, out long last) && now - last < FineCooldownMs)
                {
                    Say(p, "That player was just fined; wait a moment.");
                    return;
                }
                _lastFine[targetLicense] = now;

                // The bank owns the balance check and the deduction, so a fine can never overdraw.
                bool paid = false;
                try
                {
                    paid = (bool)Exports["bank-cs"].removeMoney(Src(target), "bank", amount, "fine: " + reason);
                }
                catch (Exception e)
                {
                    Debug.WriteLine("[jobs] fine failed, bank-cs unavailable: " + e.Message);
                }

                if (!paid)
                {
                    Say(p, "The fine could not be charged (check their bank balance).");
                    return;
                }

                Say(p, $"Fined {target.Name} ${amount}: {reason}");
                Say(target, $"You were fined ${amount} by {p.Name}: {reason}");
                LogAction("fine", p, targetLicense, CleanText(target.Name, 60), amount, reason);
                Debug.WriteLine($"[jobs] {p.Name} fined {target.Name} ${amount} ({reason})");
            });
        }

        // ==================================================================
        //  EMS: revive
        // ==================================================================

        void ReviveCommand(Player p, List<object> args)
        {
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id))
            {
                Say(p, "Usage: /revive <player id>");
                return;
            }

            With(p, s =>
            {
                if (s.Job != "ems" || !s.OnDuty) { Say(p, "Only on-duty EMS can revive players."); return; }
                TryRevive(p, id);
            });
        }

        void TryRevive(Player medic, int targetSrc)
        {
            if (targetSrc <= 0) { Say(medic, "That player is not online."); return; }
            var target = Players[targetSrc];
            if (target == null) { Say(medic, "That player is not online."); return; }
            if (target == medic) { Say(medic, "You can't revive yourself."); return; }

            int medicPed = GetPlayerPed(medic.Handle);
            int targetPed = GetPlayerPed(target.Handle);
            if (medicPed == 0 || targetPed == 0) return;

            if (Dist2D(GetEntityCoords(medicPed), GetEntityCoords(targetPed)) > ReviveRange)
            {
                Say(medic, "Get closer to the patient.");
                return;
            }

            // The server can read the real ped health with OneSync, so "is down" is not a client claim.
            if (GetEntityHealth(targetPed) > 0)
            {
                Say(medic, "That player is not down.");
                return;
            }

            string targetLicense = License(target);
            if (targetLicense == null) return;

            long now = GetGameTimer();
            if (_lastRevive.TryGetValue(targetLicense, out long last) && now - last < _reviveCooldownMs)
            {
                Say(medic, "That patient was just revived; let them recover first.");
                return;
            }
            _lastRevive[targetLicense] = now;

            // The patient's own client applies the resurrection, then the server checks the result
            // before paying, so a refusal or failure cannot earn a bonus.
            TriggerClientEvent(target, "jobs:revive", CleanText(medic.Name, 40));
            Say(medic, $"Reviving {target.Name}...");

            _ = PayAfterRevive(medic, target, targetLicense);
        }

        async Task PayAfterRevive(Player medic, Player target, string targetLicense)
        {
            try
            {
                await Delay(2000);

                if (Players[Src(medic)] == null || Players[Src(target)] == null) return;

                int targetPed = GetPlayerPed(target.Handle);
                if (targetPed == 0 || GetEntityHealth(targetPed) <= 0)
                {
                    Say(medic, "The revive did not take effect.");
                    return;
                }

                bool paid = false;
                try
                {
                    paid = (bool)Exports["bank-cs"].addBank(Src(medic), _revivePay, "EMS revive");
                }
                catch (Exception e)
                {
                    Debug.WriteLine("[jobs] revive payout failed, bank-cs unavailable: " + e.Message);
                }

                Say(medic, paid
                    ? $"You revived {target.Name}. ${_revivePay} was paid to your bank."
                    : $"You revived {target.Name}, but the bank could not pay the bonus.");

                LogAction("revive", medic, targetLicense, CleanText(target.Name, 60), _revivePay, "revived at scene");
                Debug.WriteLine($"[jobs] {medic.Name} revived {target.Name} (paid={paid})");
            }
            catch (Exception e)
            {
                Debug.WriteLine("[jobs] revive payout error: " + e.Message);
            }
        }

        // ==================================================================
        //  Mechanic: repair (client sends a network id)
        // ==================================================================

        [EventHandler("jobs:repair")]
        void OnRepair([FromSource] Player p, int netId)
        {
            if (p == null || RateLimited(Src(p), 1000)) return;

            With(p, s =>
            {
                if (s.Job != "mechanic" || !s.OnDuty) { Say(p, "Only on-duty mechanics can repair vehicles."); return; }

                int entity = NetworkGetEntityFromNetworkId(netId);
                if (entity == 0 || !DoesEntityExist(entity)) { Say(p, "No vehicle found."); return; }

                int ped = GetPlayerPed(p.Handle);
                if (ped == 0 || Vector3.Distance(GetEntityCoords(ped), GetEntityCoords(entity)) > RepairRange)
                {
                    Say(p, "Get closer to the vehicle.");
                    return;
                }

                bool hasKit = false;
                try { hasKit = (bool)Exports["inventory-cs"].hasItem(Src(p), "repair_kit", 1); }
                catch (Exception e) { Debug.WriteLine("[jobs] inventory-cs unavailable: " + e.Message); }
                if (!hasKit) { Say(p, "You need a repair kit. Buy one at a shop."); return; }

                bool removed = false;
                try { removed = (bool)Exports["inventory-cs"].removeItem(Src(p), "repair_kit", 1); }
                catch (Exception e) { Debug.WriteLine("[jobs] inventory-cs unavailable: " + e.Message); }
                if (!removed) { Say(p, "The repair kit could not be used."); return; }

                bool paid = false;
                try { paid = (bool)Exports["bank-cs"].addBank(Src(p), _repairPay, "mechanic repair"); }
                catch (Exception e) { Debug.WriteLine("[jobs] repair payout failed, bank-cs unavailable: " + e.Message); }

                TriggerClientEvent(p, "jobs:repairOk", netId);
                Say(p, paid
                    ? $"Vehicle repaired. ${_repairPay} was paid to your bank."
                    : "Vehicle repaired, but the bank could not pay the service fee.");

                LogAction("repair", p, null, "", _repairPay, "roadside repair");
                Debug.WriteLine($"[jobs] {p.Name} repaired net {netId} (paid={paid})");
            });
        }

        // ==================================================================
        //  Police: impound (client sends a network id)
        // ==================================================================

        [EventHandler("jobs:impound")]
        void OnImpound([FromSource] Player p, int netId)
        {
            if (p == null || RateLimited(Src(p), 1000)) return;

            With(p, s =>
            {
                if (s.Job != "police" || !s.OnDuty) { Say(p, "Only on-duty police can impound vehicles."); return; }

                int entity = NetworkGetEntityFromNetworkId(netId);
                if (entity == 0 || !DoesEntityExist(entity)) { Say(p, "No vehicle found."); return; }

                int ped = GetPlayerPed(p.Handle);
                if (ped == 0 || Vector3.Distance(GetEntityCoords(ped), GetEntityCoords(entity)) > ImpoundRange)
                {
                    Say(p, "Get closer to the vehicle.");
                    return;
                }

                // Don't yank a car out from under a driver.
                foreach (var other in Players.ToList())
                {
                    if (other == null) continue;
                    int otherPed = GetPlayerPed(other.Handle);
                    if (otherPed != 0 && GetVehiclePedIsIn(otherPed, false) == entity)
                    {
                        Say(p, "The vehicle is occupied; ask them to step out.");
                        return;
                    }
                }

                string plate = (GetVehicleNumberPlateText(entity) ?? "").Trim();
                if (plate.Length == 0) { Say(p, "That vehicle has no plate."); return; }

                Query("SELECT owner, model, state FROM player_vehicles WHERE plate = ?", new object[] { plate }, rows =>
                {
                    if (rows.Count == 0) { Say(p, "That vehicle is not registered (only owned vehicles can be impounded)."); return; }

                    var row = rows[0];
                    if (Convert.ToInt32(row["state"]) == 1) { Say(p, "That vehicle is already stored."); return; }

                    string ownerLicense = row["owner"].ToString();
                    string model = row["model"].ToString();

                    // Same atomic move the garage uses: only one impound can win.
                    Db("UPDATE player_vehicles SET state = 1 WHERE plate = ? AND state = 0", new object[] { plate }, affected =>
                    {
                        if (affected != 1) { Say(p, "That vehicle just changed state; try again."); return; }

                        // garage-cs knows the live network id and can delete the real entity server-side.
                        try { Exports["garage-cs"].impound(plate); }
                        catch (Exception e) { Debug.WriteLine("[jobs] garage-cs impound export unavailable: " + e.Message); }

                        // Failsafe: the officer's client also removes the entity if it still exists.
                        TriggerClientEvent(p, "jobs:impoundOk", netId);

                        Say(p, $"Impounded {model} [{plate}].");
                        var ownerPlayer = FindByLicense(ownerLicense);
                        if (ownerPlayer != null)
                            Say(ownerPlayer, $"Your {model} [{plate}] was impounded by {p.Name}. Use /takeout to get it back.");

                        LogAction("impound", p, ownerLicense,
                            ownerPlayer != null ? CleanText(ownerPlayer.Name, 60) : "", 0, $"{model} [{plate}]");
                        Debug.WriteLine($"[jobs] {p.Name} impounded [{plate}] ({model})");
                    });
                });
            });
        }

        // ==================================================================
        //  History / helpers
        // ==================================================================

        void LogAction(string action, Player actor, string targetLicense, string targetName, int amount, string detail)
        {
            string actorLicense = License(actor);
            if (actorLicense == null) return;

            Db("INSERT INTO job_actions (actor_license, actor_name, action, target_license, target_name, amount, detail) " +
               "VALUES (?, ?, ?, ?, ?, ?, ?)",
               new object[]
               {
                   actorLicense, CleanText(actor.Name, 60), action,
                   targetLicense, targetName ?? "", amount, CleanText(detail, 160),
               }, _ => { });
        }

        void ShowActionStats(Player p, string license)
        {
            Query("SELECT action, COUNT(*) AS n, COALESCE(SUM(amount), 0) AS total FROM job_actions " +
                  "WHERE actor_license = ? GROUP BY action", new object[] { license }, rows =>
            {
                if (rows.Count == 0) return;

                var parts = new List<string>();
                foreach (var row in rows)
                {
                    string action = row["action"].ToString();
                    int count = Convert.ToInt32(row["n"]);
                    long total = Convert.ToInt64(row["total"]);
                    string money = "$" + total.ToString("N0", CultureInfo.InvariantCulture);

                    if (action == "fine") parts.Add($"{count} fine(s) issued ({money})");
                    else if (action == "revive") parts.Add($"{count} revive(s) ({money})");
                    else if (action == "repair") parts.Add($"{count} repair(s) ({money})");
                    else if (action == "impound") parts.Add($"{count} impound(s)");
                }

                if (parts.Count > 0) Say(p, "Field actions: " + string.Join(", ", parts) + ".");
            });
        }

        Player FindByLicense(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            foreach (var p in Players.ToList())
                if (p != null && License(p) == license) return p;
            return null;
        }

        static string CleanText(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c)) sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }
    }
}
