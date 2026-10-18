using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Inventory.Server
{
    class ItemDef
    {
        public string Label;
        public int Weight;     // grams per unit
        public int MaxStack;
        public bool Usable;
        public bool Consumed;  // using it removes one unit
        public ItemDef(string label, int weight, int maxStack, bool usable, bool consumed)
        {
            Label = label; Weight = weight; MaxStack = maxStack; Usable = usable; Consumed = consumed;
        }
    }

    class Stack
    {
        public string Item;
        public int Count;
    }

    class Inv
    {
        // 1-based; index 0 is unused so slot numbers match what the UI shows.
        public readonly Stack[] Slots = new Stack[InventoryServer.SlotCount + 1];
    }

    /// <summary>
    /// Slot and weight based inventory. The server owns every number: the client sends *intent*
    /// ("move slot 3 to 7"), the server validates it completely and only then mutates state.
    /// </summary>
    public class InventoryServer : BaseScript
    {
        public const int SlotCount = 30;
        const int MaxWeight = 30000;   // grams
        const float GiveRange = 3.0f;  // metres, measured from server-side positions
        const long CooldownMs = 100;

        // Definitions live in code; the database only stores which item is in which slot.
        static readonly Dictionary<string, ItemDef> Items = new Dictionary<string, ItemDef>
        {
            ["water"] = new ItemDef("Water", 500, 10, true, true),
            ["sandwich"] = new ItemDef("Sandwich", 400, 10, true, true),
            ["bandage"] = new ItemDef("Bandage", 150, 10, true, true),
            ["lockpick"] = new ItemDef("Lockpick", 300, 5, true, false),
            ["repair_kit"] = new ItemDef("Repair Kit", 2500, 2, true, true),
            ["radio"] = new ItemDef("Radio", 1000, 1, false, false),
        };

        readonly Dictionary<string, Inv> _inv = new Dictionary<string, Inv>();                // license -> inventory (online players)
        readonly Dictionary<string, HashSet<int>> _dirty = new Dictionary<string, HashSet<int>>(); // license -> slots awaiting a DB write
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        public InventoryServer()
        {
            RegisterCommand("giveitem", new Action<int, List<object>, string>(GiveItemCommand), false);

            // For other resources (synchronous, served from the in-memory inventory of online players).
            Exports.Add("addItem", new Func<int, string, int, bool>(ExportAdd));
            Exports.Add("removeItem", new Func<int, string, int, bool>(ExportRemove));
            Exports.Add("hasItem", new Func<int, string, int, bool>((src, item, n) => ExportCount(src, item) >= n && n > 0));
            Exports.Add("getCount", new Func<int, string, int>(ExportCount));

            CreateTable();
            Tick += FlushLoop;
        }

        void CreateTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS player_inventory (license VARCHAR(64) NOT NULL, slot INT NOT NULL, item VARCHAR(32) NOT NULL, " +
                   "amount INT NOT NULL, PRIMARY KEY (license, slot))", new object[0], _ => Debug.WriteLine("[inventory] ready"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[inventory] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Pure inventory operations (no I/O; each returns the slots it changed)
        // ==================================================================

        static int WeightOf(Inv inv)
        {
            int w = 0;
            foreach (var s in inv.Slots) if (s != null) w += Items[s.Item].Weight * s.Count;
            return w;
        }

        static int CountOf(Inv inv, string item) =>
            inv.Slots.Where(s => s != null && s.Item == item).Sum(s => s.Count);

        // All-or-nothing check: weight, plus room in existing stacks and empty slots.
        static bool CanAdd(Inv inv, string item, int count)
        {
            ItemDef d;
            if (count <= 0 || !Items.TryGetValue(item, out d)) return false;
            if (WeightOf(inv) + (long)d.Weight * count > MaxWeight) return false;

            long room = 0;
            for (int i = 1; i <= SlotCount; i++)
            {
                var s = inv.Slots[i];
                if (s == null) room += d.MaxStack;
                else if (s.Item == item) room += d.MaxStack - s.Count;
            }
            return room >= count;
        }

        // Call only after CanAdd succeeded. Tops up existing stacks first, then opens new ones.
        static List<int> Add(Inv inv, string item, int count)
        {
            var d = Items[item];
            var changed = new List<int>();

            for (int i = 1; i <= SlotCount && count > 0; i++)
            {
                var s = inv.Slots[i];
                if (s == null || s.Item != item || s.Count >= d.MaxStack) continue;
                int n = Math.Min(count, d.MaxStack - s.Count);
                s.Count += n; count -= n; changed.Add(i);
            }
            for (int i = 1; i <= SlotCount && count > 0; i++)
            {
                if (inv.Slots[i] != null) continue;
                int n = Math.Min(count, d.MaxStack);
                inv.Slots[i] = new Stack { Item = item, Count = n };
                count -= n; changed.Add(i);
            }
            return changed;
        }

        // Call only after CountOf >= count.
        static List<int> Remove(Inv inv, string item, int count)
        {
            var changed = new List<int>();
            for (int i = 1; i <= SlotCount && count > 0; i++)
            {
                var s = inv.Slots[i];
                if (s == null || s.Item != item) continue;
                int n = Math.Min(count, s.Count);
                s.Count -= n; count -= n; changed.Add(i);
                if (s.Count == 0) inv.Slots[i] = null;
            }
            return changed;
        }

        // Move/split/merge/swap. Anything that doesn't fit the rules is rejected and changes nothing.
        static bool TryMove(Inv inv, int from, int to, int count, List<int> changed)
        {
            if (from < 1 || from > SlotCount || to < 1 || to > SlotCount || from == to) return false;
            var a = inv.Slots[from];
            if (a == null || count < 1 || count > a.Count) return false;

            var b = inv.Slots[to];
            if (b == null)
            {
                if (count == a.Count) { inv.Slots[to] = a; inv.Slots[from] = null; }
                else { a.Count -= count; inv.Slots[to] = new Stack { Item = a.Item, Count = count }; }
            }
            else if (b.Item == a.Item)
            {
                int n = Math.Min(count, Items[a.Item].MaxStack - b.Count);
                if (n <= 0) return false;
                b.Count += n; a.Count -= n;
                if (a.Count == 0) inv.Slots[from] = null;
            }
            else
            {
                if (count != a.Count) return false; // swapping only makes sense for whole stacks
                inv.Slots[from] = b; inv.Slots[to] = a;
            }
            changed.Add(from); changed.Add(to);
            return true;
        }

        // ==================================================================
        //  Events from the client / NUI
        // ==================================================================

        [EventHandler("inv:request")]
        void OnRequest([FromSource] Player p)
        {
            if (Gate(p)) With(p, (lic, inv) => SendState(p, inv));
        }

        [EventHandler("inv:move")]
        void OnMove([FromSource] Player p, int from, int to, int count)
        {
            if (!Gate(p)) return;
            With(p, (lic, inv) =>
            {
                var changed = new List<int>();
                if (!TryMove(inv, from, to, count, changed)) { SendState(p, inv); return; } // resync the UI
                MarkDirty(lic, changed);
                SendState(p, inv);
            });
        }

        [EventHandler("inv:use")]
        void OnUse([FromSource] Player p, int slot)
        {
            if (!Gate(p)) return;
            With(p, (lic, inv) =>
            {
                var s = InRange(slot) ? inv.Slots[slot] : null;
                if (s == null) { SendState(p, inv); return; }

                string item = s.Item;
                var d = Items[item];
                if (!d.Usable) { Say(p, $"{d.Label} can't be used."); return; }

                if (d.Consumed)
                {
                    s.Count--;
                    if (s.Count == 0) inv.Slots[slot] = null;
                    MarkDirty(lic, new[] { slot });
                }
                TriggerClientEvent(p, "inv:effect", item); // the server decided; the client only plays the effect
                SendState(p, inv);
            });
        }

        [EventHandler("inv:discard")]
        void OnDiscard([FromSource] Player p, int slot, int count)
        {
            if (!Gate(p)) return;
            With(p, (lic, inv) =>
            {
                var s = InRange(slot) ? inv.Slots[slot] : null;
                if (s == null || count < 1 || count > s.Count) { SendState(p, inv); return; }

                var label = Items[s.Item].Label;
                s.Count -= count;
                if (s.Count == 0) inv.Slots[slot] = null;
                MarkDirty(lic, new[] { slot });
                Debug.WriteLine($"[inventory] {p.Name} discarded {count}x {label}");
                SendState(p, inv);
            });
        }

        // The receiver is chosen by the SERVER (nearest player within range of real positions),
        // so a client can't hand items to someone far away or to an arbitrary id.
        [EventHandler("inv:give")]
        void OnGive([FromSource] Player p, int slot, int count)
        {
            if (!Gate(p)) return;

            var target = NearestPlayer(p);
            if (target == null) { Say(p, "Nobody is close enough."); return; }

            With(p, (lic, inv) => With(target, (tlic, tinv) =>
            {
                // Re-read everything here: either inventory may have changed while loading.
                var s = InRange(slot) ? inv.Slots[slot] : null;
                if (s == null || count < 1 || count > s.Count) { SendState(p, inv); return; }
                if (tlic == lic) return;

                string item = s.Item;
                if (!CanAdd(tinv, item, count)) { Say(p, $"{target.Name} can't carry that."); return; }

                // Both halves happen in the same synchronous step, so there's no window where the
                // item exists twice or nowhere. Take from the chosen slot, then add to the receiver.
                s.Count -= count;
                if (s.Count == 0) inv.Slots[slot] = null;
                MarkDirty(lic, new[] { slot });
                MarkDirty(tlic, Add(tinv, item, count));

                Debug.WriteLine($"[inventory] {p.Name} gave {count}x {Items[item].Label} to {target.Name}");
                SendState(p, inv);
                SendState(target, tinv);
                Say(p, $"You gave {count}x {Items[item].Label} to {target.Name}.");
                Say(target, $"{p.Name} gave you {count}x {Items[item].Label}.");
            }));
        }

        // ==================================================================
        //  Admin command & exports
        // ==================================================================

        void GiveItemCommand(int src, List<object> args, string raw)
        {
            if (src <= 0) return;
            var p = Players[src];
            if (p == null || !Gate(p)) return;
            if (!IsPlayerAceAllowed(p.Handle, "inventory.admin")) { Say(p, "You are not allowed to use that."); return; }

            int id, count = 1;
            if (args.Count < 2 || !int.TryParse(args[0]?.ToString(), out id) || (args.Count > 2 && !int.TryParse(args[2]?.ToString(), out count)) || count < 1 || count > 100)
            {
                Say(p, "Usage: /giveitem <player id> <" + string.Join("|", Items.Keys) + "> [count 1-100]");
                return;
            }
            string item = args[1]?.ToString().ToLowerInvariant();
            if (!Items.ContainsKey(item)) { Say(p, "Unknown item."); return; }

            var target = Players[id];
            if (target == null) { Say(p, "That player is not online."); return; }

            With(target, (lic, inv) =>
            {
                if (!CanAdd(inv, item, count)) { Say(p, "They can't carry that (weight or slots)."); return; }
                MarkDirty(lic, Add(inv, item, count));
                SendState(target, inv);
                Say(p, $"Gave {count}x {Items[item].Label} to {target.Name}.");
                Say(target, $"You received {count}x {Items[item].Label}.");
            });
        }

        bool ExportAdd(int src, string item, int count)
        {
            string lic; var inv = Cached(src, out lic);
            if (inv == null || !CanAdd(inv, item, count)) return false;
            MarkDirty(lic, Add(inv, item, count));
            SendState(Players[src], inv);
            return true;
        }

        bool ExportRemove(int src, string item, int count)
        {
            string lic; var inv = Cached(src, out lic);
            if (inv == null || count <= 0 || CountOf(inv, item) < count) return false;
            MarkDirty(lic, Remove(inv, item, count));
            SendState(Players[src], inv);
            return true;
        }

        int ExportCount(int src, string item)
        {
            string lic; var inv = Cached(src, out lic);
            return inv == null ? 0 : CountOf(inv, item);
        }

        // ==================================================================
        //  State, persistence, helpers
        // ==================================================================

        // Runs the action with the player's inventory, loading it from MySQL the first time.
        void With(Player p, Action<string, Inv> action)
        {
            string lic = License(p);
            if (lic == null) return;
            Inv inv;
            if (_inv.TryGetValue(lic, out inv)) { action(lic, inv); return; }

            Query("SELECT slot, item, amount FROM player_inventory WHERE license = ?", new object[] { lic }, rows =>
            {
                if (Players[int.Parse(p.Handle)] == null) return; // left while loading

                // A parallel request may already have finished loading: use that one, never create a second copy.
                if (!_inv.TryGetValue(lic, out inv))
                {
                    inv = new Inv();
                    foreach (var r in rows)
                    {
                        int slot = Convert.ToInt32(r["slot"]), amount = Convert.ToInt32(r["amount"]);
                        string item = r["item"].ToString();
                        ItemDef d;
                        if (slot < 1 || slot > SlotCount || !Items.TryGetValue(item, out d) || amount < 1 || amount > d.MaxStack || inv.Slots[slot] != null)
                        {
                            Debug.WriteLine($"[inventory] ignoring invalid row for {lic}: slot {slot}, {item} x{amount}");
                            continue;
                        }
                        inv.Slots[slot] = new Stack { Item = item, Count = amount };
                    }
                    _inv[lic] = inv;
                }
                action(lic, inv);
            });
        }

        Inv Cached(int src, out string license)
        {
            license = null;
            var p = Players[src];
            if (p == null) return null;
            license = License(p);
            Inv inv;
            return license != null && _inv.TryGetValue(license, out inv) ? inv : null;
        }

        // Writes are coalesced: mutations just mark slots dirty, and one loop writes the final values.
        // That also keeps two quick changes to the same slot from reaching MySQL out of order.
        void MarkDirty(string lic, IEnumerable<int> slots)
        {
            HashSet<int> set;
            if (!_dirty.TryGetValue(lic, out set)) _dirty[lic] = set = new HashSet<int>();
            foreach (var s in slots) set.Add(s);
        }

        void Flush(string lic)
        {
            HashSet<int> set; Inv inv;
            if (!_dirty.TryGetValue(lic, out set) || !_inv.TryGetValue(lic, out inv)) return;
            _dirty.Remove(lic);

            foreach (int i in set)
            {
                var s = inv.Slots[i];
                if (s == null) Db("DELETE FROM player_inventory WHERE license = ? AND slot = ?", new object[] { lic, i }, _ => { });
                else Db("INSERT INTO player_inventory (license, slot, item, amount) VALUES (?, ?, ?, ?) " +
                        "ON DUPLICATE KEY UPDATE item = VALUES(item), amount = VALUES(amount)", new object[] { lic, i, s.Item, s.Count }, _ => { });
            }
        }

        async Task FlushLoop()
        {
            await Delay(500);
            foreach (var lic in _dirty.Keys.ToList()) Flush(lic);
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            _lastUse.Remove(int.Parse(p.Handle));
            string lic = License(p);
            if (lic == null) return;
            Flush(lic);
            _inv.Remove(lic);
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            foreach (var lic in _dirty.Keys.ToList()) Flush(lic);
        }

        void SendState(Player p, Inv inv)
        {
            if (p == null) return;
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"state\",\"slots\":").Append(SlotCount)
              .Append(",\"maxWeight\":").Append(MaxWeight)
              .Append(",\"weight\":").Append(WeightOf(inv))
              .Append(",\"items\":[");
            bool first = true;
            for (int i = 1; i <= SlotCount; i++)
            {
                var s = inv.Slots[i];
                if (s == null) continue;
                var d = Items[s.Item];
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"slot\":").Append(i)
                  .Append(",\"item\":\"").Append(s.Item).Append('"')
                  .Append(",\"label\":\"").Append(d.Label.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"')
                  .Append(",\"count\":").Append(s.Count)
                  .Append(",\"weight\":").Append(d.Weight)
                  .Append(",\"max\":").Append(d.MaxStack)
                  .Append(",\"usable\":").Append(d.Usable ? "true" : "false")
                  .Append('}');
            }
            sb.Append("]}");
            TriggerClientEvent(p, "inv:state", sb.ToString());
        }

        Player NearestPlayer(Player p)
        {
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return null;
            var me = GetEntityCoords(ped);

            Player best = null;
            float bestDist = GiveRange;
            foreach (var other in Players)
            {
                if (other.Handle == p.Handle) continue;
                int otherPed = GetPlayerPed(other.Handle);
                if (otherPed == 0) continue;
                float dist = Vector3.Distance(me, GetEntityCoords(otherPed));
                if (dist <= bestDist) { best = other; bestDist = dist; }
            }
            return best;
        }

        static bool InRange(int slot) => slot >= 1 && slot <= SlotCount;

        // Null player or too-frequent calls are dropped before any work happens.
        bool Gate(Player p)
        {
            if (p == null) return false;
            int src = int.Parse(p.Handle);
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < CooldownMs) return false;
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
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 255, 210, 90 }, args = new[] { "Inventory", msg } });
    }
}
