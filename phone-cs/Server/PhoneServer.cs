using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Phone.Server
{
    /// <summary>
    /// A phone for every player: a number tied to their licence, contacts, text messages and phone banking.
    /// Everything is stored in MySQL and every action is validated here; the NUI only sends requests.
    /// Messages are always attributed to the sender's real number (the server knows it), never to a number
    /// the client claims, so they cannot be spoofed.
    /// </summary>
    public class PhoneServer : BaseScript
    {
        const int MaxContacts = 100;
        const int MaxNameLength = 24;
        const int MaxBodyLength = 280;
        const int HistoryLimit = 120;
        const int MaxPayment = 50000;
        const long CooldownMs = 300;
        const long SendCooldownMs = 1000;

        static readonly Regex NumberFormat = new Regex(@"^\d{3}-\d{4}$", RegexOptions.Compiled);

        readonly Random _rng = new Random();
        readonly Dictionary<int, string> _numbers = new Dictionary<int, string>();   // player -> own number
        readonly Dictionary<string, int> _online = new Dictionary<string, int>();    // number -> player (for live delivery)
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();
        readonly Dictionary<int, long> _lastSend = new Dictionary<int, long>();

        public PhoneServer()
        {
            try
            {
                // Create the tables in order, then pick up players who were already connected.
                Db("CREATE TABLE IF NOT EXISTS phone_numbers (license VARCHAR(64) PRIMARY KEY, number CHAR(8) NOT NULL UNIQUE)", new object[0], _ =>
                Db("CREATE TABLE IF NOT EXISTS phone_contacts (id INT AUTO_INCREMENT PRIMARY KEY, owner VARCHAR(64) NOT NULL, name VARCHAR(24) NOT NULL, number CHAR(8) NOT NULL, UNIQUE KEY owner_number (owner, number))", new object[0], __ =>
                Db("CREATE TABLE IF NOT EXISTS phone_messages (id INT AUTO_INCREMENT PRIMARY KEY, from_number CHAR(8) NOT NULL, to_number CHAR(8) NOT NULL, body VARCHAR(280) NOT NULL, sent_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP, seen TINYINT NOT NULL DEFAULT 0, KEY to_idx (to_number), KEY from_idx (from_number))", new object[0], ___ =>
                {
                    foreach (var p in Players) EnsureNumber(p, n => { });
                    Debug.WriteLine("[phone] ready");
                })));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[phone] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Phone numbers
        // ==================================================================

        [EventHandler("playerJoining")]
        void OnJoining([FromSource] Player p, string oldId)
        {
            if (p != null) EnsureNumber(p, n => { });
        }

        [EventHandler("playerDropped")]
        void OnDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            int src = int.Parse(p.Handle);
            string number;
            if (_numbers.TryGetValue(src, out number)) _online.Remove(number);
            _numbers.Remove(src);
            _lastUse.Remove(src);
            _lastSend.Remove(src);
        }

        // Every licence gets one number, assigned the first time and kept forever.
        void EnsureNumber(Player p, Action<string> done, int attempt = 0)
        {
            string known;
            int src = int.Parse(p.Handle);
            if (_numbers.TryGetValue(src, out known)) { done(known); return; }
            string license = License(p);
            if (license == null) return;

            Query("SELECT number FROM phone_numbers WHERE license = ?", new object[] { license }, rows =>
            {
                if (rows.Count == 1) { Assign(src, rows[0]["number"].ToString().Trim(), done); return; }
                if (attempt >= 8) { Debug.WriteLine("[phone] could not allocate a number for " + p.Name); return; }

                string candidate = "555-" + _rng.Next(0, 10000).ToString("D4", CultureInfo.InvariantCulture);
                // INSERT IGNORE: a taken number (or a concurrent insert for the same licence) inserts nothing, so we retry/re-read.
                Db("INSERT IGNORE INTO phone_numbers (license, number) VALUES (?, ?)", new object[] { license, candidate }, affected =>
                {
                    if (affected == 1) Assign(src, candidate, done);
                    else EnsureNumber(p, done, attempt + 1);
                });
            });
        }

        void Assign(int src, string number, Action<string> done)
        {
            if (Players[src] == null) return; // they left while we were asking the database
            _numbers[src] = number;
            _online[number] = src;
            done(number);
        }

        // ==================================================================
        //  Opening the phone
        // ==================================================================

        [EventHandler("phone:open")]
        void OnOpen([FromSource] Player p)
        {
            if (!Gate(p)) return;
            EnsureNumber(p, number => SendState(p, number));
        }

        void SendState(Player p, string number)
        {
            string license = License(p);
            int src = int.Parse(p.Handle);

            Query("SELECT id, name, number FROM phone_contacts WHERE owner = ? ORDER BY name", new object[] { license }, contacts =>
            Query("SELECT id, from_number, to_number, body, UNIX_TIMESTAMP(sent_at) * 1000 AS at, seen FROM phone_messages " +
                  "WHERE from_number = ? OR to_number = ? ORDER BY id DESC LIMIT " + HistoryLimit, new object[] { number, number }, messages =>
            {
                var sb = new StringBuilder();
                sb.Append("{\"type\":\"state\",\"number\":").Append(J(number)).Append(",\"bank\":").Append(Bank(src)).Append(",\"contacts\":[");
                sb.Append(string.Join(",", contacts.Select(c =>
                    "{\"id\":" + Convert.ToInt64(c["id"]) + ",\"name\":" + J(c["name"].ToString()) + ",\"number\":" + J(c["number"].ToString().Trim()) + "}")));
                sb.Append("],\"messages\":[");
                sb.Append(string.Join(",", messages.AsEnumerable().Reverse().Select(MessageJson)));
                sb.Append("]}");
                TriggerClientEvent(p, "phone:state", sb.ToString());
            }));
        }

        // ==================================================================
        //  Contacts
        // ==================================================================

        [EventHandler("phone:addContact")]
        void OnAddContact([FromSource] Player p, string name, string number)
        {
            if (!Gate(p)) return;
            name = Clean(name, MaxNameLength);
            number = (number ?? "").Trim();
            if (name.Length == 0 || !NumberFormat.IsMatch(number)) { Say(p, "Enter a name and a number like 555-0123."); return; }

            EnsureNumber(p, own =>
            {
                string license = License(p);
                Query("SELECT COUNT(*) AS n FROM phone_contacts WHERE owner = ?", new object[] { license }, rows =>
                {
                    if (Convert.ToInt32(rows[0]["n"]) >= MaxContacts) { Say(p, "Your contact list is full."); return; }
                    // Saving a number you already have just renames that contact.
                    Db("INSERT INTO phone_contacts (owner, name, number) VALUES (?, ?, ?) ON DUPLICATE KEY UPDATE name = VALUES(name)",
                       new object[] { license, name, number }, _ => SendState(p, own));
                });
            });
        }

        [EventHandler("phone:deleteContact")]
        void OnDeleteContact([FromSource] Player p, int id)
        {
            if (!Gate(p)) return;
            EnsureNumber(p, own =>
                // The owner is part of the WHERE clause, so you can only ever delete your own contacts.
                Db("DELETE FROM phone_contacts WHERE id = ? AND owner = ?", new object[] { id, License(p) }, _ => SendState(p, own)));
        }

        // ==================================================================
        //  Messages
        // ==================================================================

        [EventHandler("phone:send")]
        void OnSend([FromSource] Player p, string to, string body)
        {
            if (p == null) return;
            int src = int.Parse(p.Handle);
            long now = GetGameTimer(), last;
            if (_lastSend.TryGetValue(src, out last) && now - last < SendCooldownMs) { Say(p, "You are sending messages too fast."); return; }

            to = (to ?? "").Trim();
            body = Clean(body, MaxBodyLength);
            if (!NumberFormat.IsMatch(to) || body.Length == 0) return;
            _lastSend[src] = now;

            EnsureNumber(p, own =>
                Query("SELECT number FROM phone_numbers WHERE number = ?", new object[] { to }, rows =>
                {
                    if (rows.Count != 1) { Say(p, "That number is not in service."); return; }

                    // "From" is our own record of who they are; the client has no say in it.
                    Exports["oxmysql"].insert("INSERT INTO phone_messages (from_number, to_number, body) VALUES (?, ?, ?)",
                        new object[] { own, to, body }, new Action<dynamic>(id =>
                        {
                            string json = "{\"type\":\"message\",\"msg\":" + MessageJson(own, to, body, id == null ? 0 : Convert.ToInt64(id), (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds, false) + "}";
                            TriggerClientEvent(p, "phone:push", json, false);

                            int target;
                            if (to != own && _online.TryGetValue(to, out target) && Players[target] != null)
                                TriggerClientEvent(Players[target], "phone:push", json, true);
                        }));
                }));
        }

        [EventHandler("phone:markRead")]
        void OnMarkRead([FromSource] Player p, string from)
        {
            if (!Gate(p)) return;
            from = (from ?? "").Trim();
            if (!NumberFormat.IsMatch(from)) return;
            EnsureNumber(p, own =>
                Db("UPDATE phone_messages SET seen = 1 WHERE to_number = ? AND from_number = ? AND seen = 0", new object[] { own, from }, _ => { }));
        }

        // ==================================================================
        //  Emergency calls (handled by dispatch-cs when it is running)
        // ==================================================================

        [EventHandler("phone:emergency")]
        void OnEmergency([FromSource] Player p, string category, string body)
        {
            if (!Gate(p)) return;

            category = (category ?? "").Trim().ToLowerInvariant();
            if (category != "police" && category != "medical") return;

            body = Clean(body, 160);
            if (body.Length < 4) { Say(p, "Describe the emergency first."); return; }

            bool ok = false;
            try
            {
                // Server-only export. Dispatch reads the caller's real position itself.
                ok = (bool)Exports["dispatch-cs"].createCall(int.Parse(p.Handle), category, body);
            }
            catch (Exception e)
            {
                Debug.WriteLine("[phone] dispatch-cs unavailable: " + e.Message);
                Say(p, "Dispatch is unavailable right now.");
            }

            TriggerClientEvent(p, "phone:push",
                "{\"type\":\"emergency\",\"ok\":" + (ok ? "true" : "false") + ",\"state\":\"placed\"}", false);
        }

        [EventHandler("phone:emergencyCancel")]
        void OnEmergencyCancel([FromSource] Player p)
        {
            if (!Gate(p)) return;

            bool ok = false;
            try
            {
                ok = (bool)Exports["dispatch-cs"].cancelCall(int.Parse(p.Handle));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[phone] dispatch-cs unavailable: " + e.Message);
                Say(p, "Dispatch is unavailable right now.");
            }

            TriggerClientEvent(p, "phone:push",
                "{\"type\":\"emergency\",\"ok\":" + (ok ? "true" : "false") + ",\"state\":\"cancelled\"}", false);
        }

        // ==================================================================
        //  Phone banking
        // ==================================================================

        [EventHandler("phone:pay")]
        void OnPay([FromSource] Player p, string to, int amount)
        {
            if (!Gate(p)) return;
            to = (to ?? "").Trim();
            if (!NumberFormat.IsMatch(to) || amount < 1 || amount > MaxPayment) { Say(p, $"Enter a number and an amount between $1 and ${MaxPayment}."); return; }

            EnsureNumber(p, own =>
            {
                int target;
                if (to == own) { Say(p, "You can't pay yourself."); return; }
                if (!_online.TryGetValue(to, out target) || Players[target] == null) { Say(p, "That person is not available right now."); return; }

                int src = int.Parse(p.Handle);
                string note = $"Phone payment {own} -> {to}";
                try
                {
                    // Take it first (checked and deducted in one step), then credit; if the credit fails, give it back.
                    if (!(bool)Exports["bank-cs"].removeMoney(src, "bank", amount, note)) { Say(p, "Not enough money in your bank account."); return; }
                    if (!(bool)Exports["bank-cs"].addMoney(target, "bank", amount, note))
                    {
                        Exports["bank-cs"].addMoney(src, "bank", amount, "refund: " + note);
                        Say(p, "The payment could not be completed, you were not charged.");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine("[phone] payment aborted, bank-cs unavailable: " + e.Message);
                    Say(p, "Banking is unavailable right now.");
                    return;
                }

                Say(p, $"Sent ${amount} to {to}.");
                Say(Players[target], $"{own} sent you ${amount}.");
                TriggerClientEvent(p, "phone:push", "{\"type\":\"balance\",\"bank\":" + Bank(src) + "}", false);
                TriggerClientEvent(Players[target], "phone:push", "{\"type\":\"balance\",\"bank\":" + Bank(target) + "}", false);
            });
        }

        int Bank(int src)
        {
            try
            {
                string balances = Exports["bank-cs"].getBalances(src);
                int bank;
                if (balances != null && int.TryParse(balances.Split(',')[1], out bank)) return bank;
            }
            catch (Exception) { }
            return 0;
        }

        // ==================================================================
        //  Helpers
        // ==================================================================

        static string MessageJson(IDictionary<string, object> r) =>
            MessageJson(r["from_number"].ToString().Trim(), r["to_number"].ToString().Trim(), r["body"].ToString(),
                        Convert.ToInt64(r["id"]), Convert.ToInt64(r["at"]), Convert.ToInt32(r["seen"]) == 1);

        static string MessageJson(string from, string to, string body, long id, long at, bool seen) =>
            "{\"id\":" + id + ",\"from\":" + J(from) + ",\"to\":" + J(to) + ",\"body\":" + J(body) + ",\"at\":" + at + ",\"seen\":" + (seen ? "true" : "false") + "}";

        // Strips control characters and trims; never longer than max.
        static string Clean(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c)) sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        // Minimal JSON string encoder (the NUI gets JSON built here, so user text must be escaped).
        static string J(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        bool Gate(Player p)
        {
            if (p == null) return false;
            int src = int.Parse(p.Handle);
            long now = GetGameTimer(), last;
            if (_lastUse.TryGetValue(src, out last) && now - last < CooldownMs) return false;
            _lastUse[src] = now;
            return true;
        }

        static string License(Player p)
        {
            string id = p.Identifiers["license"];
            return string.IsNullOrEmpty(id) ? null : id;
        }

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

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 90, 200, 255 }, args = new[] { "Phone", msg } });
    }
}
