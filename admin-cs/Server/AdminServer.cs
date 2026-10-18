using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Admin.Server
{
    public enum Role { None = 0, Mod = 1, Admin = 2, SuperAdmin = 3 }

    /// <summary>Who is running a command: an online player, or the server console.</summary>
    class Actor
    {
        public Player Player;      // null = console
        public Role Role;
        public string Name => Player?.Name ?? "console";
        public string License => Player?.Identifiers["license"] ?? "console";
        public bool IsConsole => Player == null;
    }

    /// <summary>
    /// Admin tool. Roles live in MySQL, every action is permission- and rank-checked on the server,
    /// bans are enforced while a player connects, and every action lands in an audit log.
    /// </summary>
    public class AdminServer : BaseScript
    {
        const int MaxReasonLength = 200;
        const long CooldownMs = 500;

        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();
        readonly string _bootstrapLicense; // convar admin_bootstrap_license: always SuperAdmin, used to create the first admins

        public AdminServer()
        {
            _bootstrapLicense = GetConvar("admin_bootstrap_license", "");

            Cmd("kick", Role.Mod, Kick);
            Cmd("ban", Role.Admin, Ban);
            Cmd("unban", Role.Admin, Unban);
            Cmd("bans", Role.Mod, ListBans);
            Cmd("goto", Role.Mod, Goto);
            Cmd("bring", Role.Mod, Bring);
            Cmd("announce", Role.Admin, Announce);
            Cmd("setrole", Role.SuperAdmin, SetRole);
            Cmd("adminlog", Role.Admin, AdminLog);

            CreateTables();
        }

        void CreateTables()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS admins (license VARCHAR(64) PRIMARY KEY, role TINYINT NOT NULL, name VARCHAR(64) NOT NULL, " +
                   "updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)", new object[0], _ =>
                Db("CREATE TABLE IF NOT EXISTS bans (id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, name VARCHAR(64) NOT NULL, " +
                   "reason VARCHAR(255) NOT NULL, banned_by VARCHAR(64) NOT NULL, created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "expires_at TIMESTAMP NULL DEFAULT NULL, active TINYINT NOT NULL DEFAULT 1, INDEX idx_license (license, active))", new object[0], __ =>
                Db("CREATE TABLE IF NOT EXISTS admin_log (id INT AUTO_INCREMENT PRIMARY KEY, admin_license VARCHAR(64) NOT NULL, " +
                   "admin_name VARCHAR(64) NOT NULL, action VARCHAR(32) NOT NULL, target VARCHAR(64) NULL, details VARCHAR(255) NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP)", new object[0], ___ =>
                    Debug.WriteLine("[admin] ready"))));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[admin] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ---------- database wrappers around oxmysql ----------

        void Query(string sql, object[] args, Action<List<IDictionary<string, object>>> done)
        {
            Exports["oxmysql"].query(sql, args, new Action<dynamic>(res =>
            {
                var rows = new List<IDictionary<string, object>>();
                if (res != null)
                    foreach (var r in res) rows.Add((IDictionary<string, object>)r);
                done(rows);
            }));
        }

        void Db(string sql, object[] args, Action<int> done) =>
            Exports["oxmysql"].update(sql, args, new Action<dynamic>(n => done(n == null ? 0 : Convert.ToInt32(n))));

        void Insert(string sql, object[] args, Action<int> done) =>
            Exports["oxmysql"].insert(sql, args, new Action<dynamic>(id => done(id == null ? 0 : Convert.ToInt32(id))));

        // ---------- ban enforcement ----------

        [EventHandler("playerConnecting")]
        async void OnConnecting([FromSource] Player p, string name, dynamic setKickReason, dynamic deferrals)
        {
            deferrals.defer();
            await Delay(0);

            string license = p?.Identifiers["license"];
            if (string.IsNullOrEmpty(license)) { deferrals.done("A Rockstar license is required to join."); return; }

            Query("SELECT id, reason, IF(expires_at IS NULL, NULL, TIMESTAMPDIFF(MINUTE, NOW(), expires_at)) AS mins_left " +
                  "FROM bans WHERE license = ? AND active = 1 AND (expires_at IS NULL OR expires_at > NOW()) ORDER BY id DESC LIMIT 1",
                  new object[] { license }, rows =>
                  {
                      if (rows.Count == 0) { deferrals.done(); return; }
                      var b = rows[0];
                      string until = b["mins_left"] == null ? "permanent" : $"{Convert.ToInt32(b["mins_left"]) + 1} minute(s) remaining";
                      deferrals.done($"You are banned (#{b["id"]}): {b["reason"]} [{until}]");
                  });
        }

        // ---------- commands ----------

        void Kick(Actor a, List<object> args)
        {
            if (args.Count < 1) { Reply(a, "Usage: /kick <id> [reason]"); return; }
            WithTarget(a, args[0], t =>
            {
                string reason = Reason(args, 1, "Kicked by an admin.");
                Log(a, "kick", t.Name, reason);
                DropPlayer(t.Handle, $"Kicked: {reason}");
                Reply(a, $"Kicked {t.Name}.");
            });
        }

        void Ban(Actor a, List<object> args)
        {
            if (args.Count < 2) { Reply(a, "Usage: /ban <id> <30m|12h|7d|perm> [reason]"); return; }
            if (!TryDuration(args[1]?.ToString(), out int? minutes)) { Reply(a, "Duration must look like 30m, 12h, 7d or perm."); return; }

            WithTarget(a, args[0], t =>
            {
                string license = t.Identifiers["license"];
                if (string.IsNullOrEmpty(license)) { Reply(a, "Target has no license identifier."); return; }
                string reason = Reason(args, 2, "Banned by an admin.");

                var sql = minutes.HasValue
                    ? "INSERT INTO bans (license, name, reason, banned_by, expires_at) VALUES (?, ?, ?, ?, DATE_ADD(NOW(), INTERVAL ? MINUTE))"
                    : "INSERT INTO bans (license, name, reason, banned_by) VALUES (?, ?, ?, ?)";
                var prm = minutes.HasValue
                    ? new object[] { license, t.Name, reason, a.Name, minutes.Value }
                    : new object[] { license, t.Name, reason, a.Name };

                string targetName = t.Name;
                Insert(sql, prm, id =>
                {
                    if (id == 0) { Reply(a, "Could not save the ban; player was NOT kicked."); return; }
                    Log(a, "ban", targetName, $"#{id} {(minutes.HasValue ? minutes + "m" : "perm")}: {reason}");
                    DropPlayer(t.Handle, $"Banned (#{id}): {reason}");
                    Reply(a, $"Banned {targetName} (ban #{id}).");
                });
            });
        }

        void Unban(Actor a, List<object> args)
        {
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0) { Reply(a, "Usage: /unban <ban id>  (see /bans)"); return; }
            Db("UPDATE bans SET active = 0 WHERE id = ? AND active = 1", new object[] { id }, affected =>
            {
                if (affected != 1) { Reply(a, "No active ban with that id."); return; }
                Log(a, "unban", null, $"#{id}");
                Reply(a, $"Ban #{id} lifted.");
            });
        }

        void ListBans(Actor a, List<object> args)
        {
            Query("SELECT id, name, reason, banned_by, IF(expires_at IS NULL, NULL, TIMESTAMPDIFF(MINUTE, NOW(), expires_at)) AS mins_left " +
                  "FROM bans WHERE active = 1 AND (expires_at IS NULL OR expires_at > NOW()) ORDER BY id DESC LIMIT 10", new object[0], rows =>
                  {
                      if (rows.Count == 0) { Reply(a, "No active bans."); return; }
                      Reply(a, "Active bans (latest 10):");
                      foreach (var r in rows)
                      {
                          string left = r["mins_left"] == null ? "perm" : $"{r["mins_left"]}m left";
                          Reply(a, $"#{r["id"]} {r["name"]} - {r["reason"]} ({left}, by {r["banned_by"]})");
                      }
                  });
        }

        void Goto(Actor a, List<object> args)
        {
            if (a.IsConsole) { Reply(a, "In-game only."); return; }
            if (args.Count < 1) { Reply(a, "Usage: /goto <id>"); return; }
            WithTarget(a, args[0], t =>
            {
                Teleport(a.Player, GetEntityCoords(GetPlayerPed(t.Handle)));
                Log(a, "goto", t.Name, null);
            }, requireLowerRank: false);
        }

        void Bring(Actor a, List<object> args)
        {
            if (a.IsConsole) { Reply(a, "In-game only."); return; }
            if (args.Count < 1) { Reply(a, "Usage: /bring <id>"); return; }
            WithTarget(a, args[0], t =>
            {
                Teleport(t, GetEntityCoords(GetPlayerPed(a.Player.Handle)));
                Log(a, "bring", t.Name, null);
            });
        }

        void Announce(Actor a, List<object> args)
        {
            string msg = string.Join(" ", args.Select(x => x?.ToString())).Trim();
            if (msg.Length == 0) { Reply(a, "Usage: /announce <message>"); return; }
            if (msg.Length > MaxReasonLength) msg = msg.Substring(0, MaxReasonLength);
            TriggerClientEvent("chat:addMessage", new { color = new[] { 255, 170, 0 }, args = new[] { "ANNOUNCEMENT", msg } });
            Log(a, "announce", null, msg);
        }

        void SetRole(Actor a, List<object> args)
        {
            if (args.Count < 2 || !TryRole(args[1]?.ToString(), out Role role)) { Reply(a, "Usage: /setrole <id> <none|mod|admin|superadmin>"); return; }
            WithTarget(a, args[0], t =>
            {
                string license = t.Identifiers["license"];
                if (string.IsNullOrEmpty(license)) { Reply(a, "Target has no license identifier."); return; }
                if (!a.IsConsole && license == a.License) { Reply(a, "You can't change your own role."); return; }

                string targetName = t.Name;
                var done = new Action<int>(_ =>
                {
                    Log(a, "setrole", targetName, role.ToString());
                    Reply(a, $"{targetName} is now {role}.");
                    Say(t, $"Your admin role is now: {role}");
                });

                if (role == Role.None) Db("DELETE FROM admins WHERE license = ?", new object[] { license }, done);
                else Db("INSERT INTO admins (license, role, name) VALUES (?, ?, ?) ON DUPLICATE KEY UPDATE role = VALUES(role), name = VALUES(name)",
                        new object[] { license, (int)role, targetName }, done);
            }, requireLowerRank: false);
        }

        void AdminLog(Actor a, List<object> args)
        {
            Query("SELECT admin_name, action, target, details FROM admin_log ORDER BY id DESC LIMIT 10", new object[0], rows =>
            {
                if (rows.Count == 0) { Reply(a, "Log is empty."); return; }
                Reply(a, "Latest admin actions:");
                foreach (var r in rows)
                    Reply(a, $"{r["admin_name"]}: {r["action"]} {r["target"]} {r["details"]}".Trim());
            });
        }

        // ---------- permissions & targets ----------

        void Cmd(string name, Role min, Action<Actor, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { handler(new Actor { Role = Role.SuperAdmin }, args); return; } // server console is always trusted

                var p = Players[src];
                if (p == null || RateLimited(src)) return;

                GetRole(p, role =>
                {
                    if (role < min) { Say(p, "You don't have permission to use that."); return; }
                    handler(new Actor { Player = p, Role = role }, args);
                });
            }), false);
        }

        void GetRole(Player p, Action<Role> done)
        {
            string license = p.Identifiers["license"];
            if (string.IsNullOrEmpty(license)) { done(Role.None); return; }
            if (_bootstrapLicense.Length > 0 && license == _bootstrapLicense) { done(Role.SuperAdmin); return; }

            Query("SELECT role FROM admins WHERE license = ?", new object[] { license }, rows =>
                done(rows.Count == 0 ? Role.None : (Role)Convert.ToInt32(rows[0]["role"])));
        }

        // Resolves the target by server id. Unless disabled, the target must rank strictly below the actor,
        // so a mod can't kick an admin and an admin can't ban another admin.
        void WithTarget(Actor a, object idArg, Action<Player> action, bool requireLowerRank = true)
        {
            if (!int.TryParse(idArg?.ToString(), out int id)) { Reply(a, "Target must be a player id."); return; }
            var target = Players[id];
            if (target == null) { Reply(a, "That player is not online."); return; }
            if (!a.IsConsole && target.Handle == a.Player.Handle && requireLowerRank) { Reply(a, "You can't do that to yourself."); return; }

            if (!requireLowerRank || a.IsConsole) { action(target); return; }

            GetRole(target, targetRole =>
            {
                if (targetRole >= a.Role) { Reply(a, "You can't do that to someone of equal or higher rank."); return; }
                if (Players[id] == null) return; // left while we were checking
                action(target);
            });
        }

        // ---------- helpers ----------

        void Log(Actor a, string action, string target, string details)
        {
            Debug.WriteLine($"[admin] {a.Name} {action} {target} {details}".TrimEnd());
            Insert("INSERT INTO admin_log (admin_license, admin_name, action, target, details) VALUES (?, ?, ?, ?, ?)",
                   new object[] { a.License, a.Name, action, target, details }, _ => { });
        }

        static string Reason(List<object> args, int from, string fallback)
        {
            string r = string.Join(" ", args.Skip(from).Select(x => x?.ToString())).Trim();
            if (r.Length == 0) return fallback;
            return r.Length > MaxReasonLength ? r.Substring(0, MaxReasonLength) : r;
        }

        // "30m", "12h", "7d" -> minutes; "perm" -> null (permanent). Capped at one year.
        static bool TryDuration(string s, out int? minutes)
        {
            minutes = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            if (s == "perm" || s == "permanent") return true;

            if (s.Length < 2 || !int.TryParse(s.Substring(0, s.Length - 1), out int n) || n <= 0) return false;
            long m;
            switch (s[s.Length - 1])
            {
                case 'm': m = n; break;
                case 'h': m = n * 60L; break;
                case 'd': m = n * 1440L; break;
                default: return false;
            }
            if (m > 525600) return false;
            minutes = (int)m;
            return true;
        }

        static bool TryRole(string s, out Role role)
        {
            role = Role.None;
            switch ((s ?? "").ToLowerInvariant())
            {
                case "none": role = Role.None; return true;
                case "mod": role = Role.Mod; return true;
                case "admin": role = Role.Admin; return true;
                case "superadmin": role = Role.SuperAdmin; return true;
                default: return false;
            }
        }

        static void Teleport(Player p, Vector3 pos) =>
            SetEntityCoords(GetPlayerPed(p.Handle), pos.X, pos.Y, pos.Z, false, false, false, false);

        bool RateLimited(int src)
        {
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < CooldownMs) return true;
            _lastUse[src] = now;
            return false;
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p != null) _lastUse.Remove(int.Parse(p.Handle));
        }

        void Reply(Actor a, string msg)
        {
            if (a.IsConsole) Debug.WriteLine("[admin] " + msg);
            else Say(a.Player, msg);
        }

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 255, 100, 100 }, args = new[] { "Admin", msg } });
    }
}
