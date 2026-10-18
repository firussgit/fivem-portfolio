using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Shops.Server
{
    class ShopItem
    {
        public string Item;     // id understood by inventory-cs
        public string Label;
        public int Price;
        public int MaxStock;
        public ShopItem(string item, string label, int price, int maxStock) { Item = item; Label = label; Price = price; MaxStock = maxStock; }
    }

    class ShopDef
    {
        public string Label;
        public ShopItem[] Items;
        public ShopDef(string label, params ShopItem[] items) { Label = label; Items = items; }
    }

    /// <summary>
    /// Shops. A purchase touches two other resources (bank-cs for money, inventory-cs for the item),
    /// so every check happens first and the money-out / item-in steps then run back to back in the
    /// same synchronous handler: a purchase either fully happens or changes nothing.
    /// </summary>
    public class ShopsServer : BaseScript
    {
        // Prices, stock limits and what each shop sells live in code: the server is the only authority.
        static readonly Dictionary<string, ShopDef> Shops = new Dictionary<string, ShopDef>
        {
            ["general"] = new ShopDef("General Store",
                new ShopItem("water", "Water", 5, 30),
                new ShopItem("sandwich", "Sandwich", 8, 30),
                new ShopItem("bandage", "Bandage", 25, 15),
                new ShopItem("radio", "Radio", 150, 5)),
            ["hardware"] = new ShopDef("Hardware Store",
                new ShopItem("lockpick", "Lockpick", 60, 10),
                new ShopItem("repair_kit", "Repair Kit", 200, 5)),
        };

        const float ShopRange = 4.0f;
        const int MaxQuantity = 20;
        const long CooldownMs = 300;
        const string LocationFile = "shops.txt";

        readonly Dictionary<string, Vector3> _locations = new Dictionary<string, Vector3>();
        readonly Dictionary<string, Dictionary<string, int>> _stock = new Dictionary<string, Dictionary<string, int>>(); // shop -> item -> units left
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();
        readonly Dictionary<int, string> _openShop = new Dictionary<int, string>(); // player -> shop whose UI they have open
        readonly int _restockMinutes;

        public ShopsServer()
        {
            _restockMinutes = Math.Max(1, GetConvarInt("shops_restock_minutes", 5));

            foreach (var shop in Shops)
                _stock[shop.Key] = shop.Value.Items.ToDictionary(i => i.Item, i => i.MaxStock);

            LoadLocations();
            RegisterCommand("setshop", new Action<int, List<object>, string>(SetShopCommand), false);

            CreateTable();
            Tick += RestockLoop;
        }

        void CreateTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS shop_purchases (id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, shop VARCHAR(32) NOT NULL, " +
                   "item VARCHAR(32) NOT NULL, quantity INT NOT NULL, total INT NOT NULL, account VARCHAR(8) NOT NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX idx_license (license))", new object[0],
                   _ => Debug.WriteLine($"[shops] ready, {_locations.Count} shop location(s), restock every {_restockMinutes} min"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[shops] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Purchase
        // ==================================================================

        [EventHandler("shops:buy")]
        void OnBuy([FromSource] Player p, string shopId, string item, int quantity, string account)
        {
            if (!Gate(p)) return;
            string license = License(p);
            if (license == null) return;

            // 1. Validate every input; the client is only asking.
            ShopDef shop; ShopItem entry;
            if (shopId == null || !Shops.TryGetValue(shopId, out shop)) return;
            if (account != "cash" && account != "bank") return;
            if (quantity < 1 || quantity > MaxQuantity) { Say(p, $"You can buy 1 to {MaxQuantity} at a time."); return; }
            if (!AtShop(p, shopId)) { Say(p, "You are not at that shop."); return; }
            entry = shop.Items.FirstOrDefault(i => i.Item == item);
            if (entry == null) { Say(p, "That item isn't sold here."); return; }

            int stock = _stock[shopId][item];
            if (stock < quantity) { Say(p, $"Only {stock} {entry.Label} left."); SendState(p, shopId); return; }

            long total = (long)entry.Price * quantity;
            if (total > 1000000) return;
            int src = int.Parse(p.Handle);
            string note = $"{shop.Label}: {quantity}x {entry.Label}";

            try
            {
                // 2. Can they carry it? (so we never take money for something that won't fit)
                if (!(bool)Exports["inventory-cs"].canAdd(src, item, quantity)) { Say(p, "You can't carry that much."); return; }

                // 3. Take the money. The bank checks the balance and deducts in one step.
                if (!(bool)Exports["bank-cs"].removeMoney(src, account, (int)total, note)) { Say(p, $"Not enough money in your {account} account."); SendState(p, shopId); return; }

                // 4. Hand over the item. If that somehow fails, give the money straight back.
                if (!(bool)Exports["inventory-cs"].addItem(src, item, quantity))
                {
                    Exports["bank-cs"].addMoney(src, account, (int)total, "refund: " + note);
                    Say(p, "Purchase failed, you were refunded.");
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("[shops] purchase aborted, a required resource is unavailable: " + e.Message);
                Say(p, "The shop is unavailable right now.");
                return;
            }

            _stock[shopId][item] = stock - quantity;
            Db("INSERT INTO shop_purchases (license, shop, item, quantity, total, account) VALUES (?, ?, ?, ?, ?, ?)",
               new object[] { license, shopId, item, quantity, (int)total, account }, _ => { });
            Debug.WriteLine($"[shops] {p.Name} bought {note} for ${total} ({account})");

            Say(p, $"Bought {quantity}x {entry.Label} for ${total}.");
            SendState(p, shopId);
        }

        // ==================================================================
        //  Opening a shop, state for the UI
        // ==================================================================

        [EventHandler("shops:open")]
        void OnOpen([FromSource] Player p, string shopId)
        {
            if (!Gate(p)) return;
            if (shopId == null || !Shops.ContainsKey(shopId)) return;
            if (!AtShop(p, shopId)) { Say(p, "You are not at that shop."); return; }

            _openShop[int.Parse(p.Handle)] = shopId;
            TriggerClientEvent(p, "shops:show", BuildState(p, shopId));
        }

        [EventHandler("shops:closed")]
        void OnClosed([FromSource] Player p)
        {
            if (p != null) _openShop.Remove(int.Parse(p.Handle));
        }

        void SendState(Player p, string shopId) =>
            TriggerClientEvent(p, "shops:state", BuildState(p, shopId));

        string BuildState(Player p, string shopId)
        {
            var shop = Shops[shopId];
            string cash = "0", bank = "0";
            try
            {
                string balances = Exports["bank-cs"].getBalances(int.Parse(p.Handle));
                if (balances != null) { var parts = balances.Split(','); cash = parts[0]; bank = parts[1]; }
            }
            catch (Exception) { /* bank unavailable: show zeros, purchases will fail safely */ }

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"state\",\"shop\":\"").Append(shopId).Append("\",\"label\":\"").Append(shop.Label)
              .Append("\",\"cash\":").Append(cash).Append(",\"bank\":").Append(bank).Append(",\"items\":[");
            bool first = true;
            foreach (var i in shop.Items)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"item\":\"").Append(i.Item).Append("\",\"label\":\"").Append(i.Label)
                  .Append("\",\"price\":").Append(i.Price).Append(",\"stock\":").Append(_stock[shopId][i.Item]).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ==================================================================
        //  Locations (placed in-game by an admin, validated against server-side positions)
        // ==================================================================

        // The player's position comes from the server (OneSync), never from the client.
        bool AtShop(Player p, string shopId)
        {
            Vector3 loc;
            if (!_locations.TryGetValue(shopId, out loc)) return false;
            int ped = GetPlayerPed(p.Handle);
            return ped != 0 && Vector3.Distance(GetEntityCoords(ped), loc) <= ShopRange;
        }

        void SetShopCommand(int src, List<object> args, string raw)
        {
            if (src <= 0) return;
            var p = Players[src];
            if (p == null || !Gate(p)) return;
            if (!IsPlayerAceAllowed(p.Handle, "shops.admin")) { Say(p, "You are not allowed to use that."); return; }

            string id = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;
            if (id == null || !Shops.ContainsKey(id)) { Say(p, "Usage: /setshop <" + string.Join("|", Shops.Keys) + ">  (stand where the shop should be)"); return; }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            _locations[id] = GetEntityCoords(ped);
            SaveLocations();
            BroadcastLocations();
            Say(p, $"{Shops[id].Label} placed at your position.");
        }

        // Clients ask for the list when they join; it also goes out to everyone whenever it changes.
        [EventHandler("shops:requestLocations")]
        void OnRequestLocations([FromSource] Player p)
        {
            if (p != null) TriggerClientEvent(p, "shops:locations", LocationsPayload());
        }

        void BroadcastLocations() => TriggerClientEvent("shops:locations", LocationsPayload());

        // "id,label,x,y,z;id,label,x,y,z". Labels come from our own config and contain no commas or semicolons.
        string LocationsPayload() =>
            string.Join(";", _locations.Select(kv => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F2},{3:F2},{4:F2}",
                kv.Key, Shops[kv.Key].Label, kv.Value.X, kv.Value.Y, kv.Value.Z)));

        void LoadLocations()
        {
            string raw = LoadResourceFile(GetCurrentResourceName(), LocationFile);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                float x, y, z;
                if (parts.Length != 4 || !Shops.ContainsKey(parts[0])
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                _locations[parts[0]] = new Vector3(x, y, z);
            }
        }

        void SaveLocations()
        {
            var lines = _locations.Select(kv => string.Format(CultureInfo.InvariantCulture, "{0},{1:F2},{2:F2},{3:F2}", kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Z));
            SaveResourceFile(GetCurrentResourceName(), LocationFile, string.Join("\n", lines), -1);
        }

        // ==================================================================
        //  Restock, cleanup, helpers
        // ==================================================================

        // A little stock comes back on a timer, so shops can run low but never stay empty forever.
        async Task RestockLoop()
        {
            await Delay(_restockMinutes * 60000);
            foreach (var shop in Shops)
                foreach (var i in shop.Value.Items)
                    _stock[shop.Key][i.Item] = Math.Min(i.MaxStock, _stock[shop.Key][i.Item] + Math.Max(1, i.MaxStock / 5));

            foreach (var kv in _openShop.ToList())
            {
                var p = Players[kv.Key];
                if (p != null) SendState(p, kv.Value); // keep open shop windows accurate
            }
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            int src = int.Parse(p.Handle);
            _lastUse.Remove(src);
            _openShop.Remove(src);
        }

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

        void Db(string sql, object[] args, Action<int> done) =>
            Exports["oxmysql"].update(sql, args, new Action<dynamic>(n => done(n == null ? 0 : Convert.ToInt32(n))));

        void Say(Player p, string msg) =>
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 100, 220, 160 }, args = new[] { "Shop", msg } });
    }
}
