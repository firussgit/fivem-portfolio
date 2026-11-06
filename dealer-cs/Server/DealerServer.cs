using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using Portfolio.Core;
using static CitizenFX.Core.Native.API;

namespace Dealer.Server
{
    class CarModel
    {
        public readonly string Model;
        public readonly string Label;
        public readonly int Price;

        public CarModel(string model, string label, int price)
        {
            Model = model;
            Label = label;
            Price = price;
        }
    }

    class TestDrive
    {
        public string License;
        public string Model;
        public string Label;
        public string Plate;
        public int NetId;
        public long StartedAt;
        public long ExpiresAt;
    }

    /// <summary>
    /// Player vehicle dealership. Prices, the showroom position, the trade-in value and the test
    /// drive clock are all decided here. The client only says *which* model or vehicle it wants;
    /// every purchase is an atomic bank charge + owned-row insert with a refund path, and a sold
    /// car is removed with a single conditional DELETE so it can never be sold twice.
    /// </summary>
    public class DealerServer : PortfolioScript
    {
        const int MaxVehicles = 10;
        const int TestDriveScene = 150; // metres: a reported test car must be at the showroom
        const int TestSpawnTimeoutMs = 15000;
        const string PlateChars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
        const string ShowroomName = "Premium Deluxe Motorsport";

        // The catalogue is the authority. A model that is not here can't be bought, sold or
        // test driven, no matter what the client asks for.
        static readonly CarModel[] Catalog =
        {
            new CarModel("faggio",   "Faggio",      5000),
            new CarModel("stanier",  "Stanier",     12000),
            new CarModel("bati",     "Bati 801",    15000),
            new CarModel("sultan",   "Sultan",      18000),
            new CarModel("buffalo",  "Buffalo",     22000),
            new CarModel("comet2",   "Comet",       28000),
            new CarModel("kuruma",   "Kuruma",      32000),
            new CarModel("baller",   "Baller",      35000),
            new CarModel("sandking", "Sandking XL", 40000),
            new CarModel("adder",    "Adder",      120000),
        };

        static readonly Dictionary<string, CarModel> ByModel =
            Catalog.ToDictionary(c => c.Model, c => c);

        readonly Random _rng = new Random();
        readonly Dictionary<string, TestDrive> _testDrives = new Dictionary<string, TestDrive>();

        Vector3 _showroom;
        float _range;
        int _testSeconds;
        int _sellMinPercent;
        int _sellMaxPercent;

        public DealerServer()
        {
            ChatPrefix = "Dealer";
            ChatColour = new[] { 255, 190, 70 };

            // The showroom is tunable from server.cfg; the client is told where it is, so both
            // sides always agree and the client never decides where the dealership is.
            _showroom = new Vector3(
                GetConvarFloat("dealer_showroom_x", -45.0f),
                GetConvarFloat("dealer_showroom_y", -1098.0f),
                GetConvarFloat("dealer_showroom_z", 26.4f));
            _range = ClampFloat(GetConvarFloat("dealer_range", 8.0f), 3f, 30f);
            _testSeconds = Math.Max(30, Math.Min(300, GetConvarInt("dealer_testdrive_seconds", 90)));
            _sellMinPercent = Math.Max(0, Math.Min(100, GetConvarInt("dealer_sell_min_percent", 35)));
            _sellMaxPercent = Math.Max(_sellMinPercent, Math.Min(100, GetConvarInt("dealer_sell_max_percent", 60)));

            Cmd("dealer", DealerCommand);
            Cmd("buycar", BuyCommand);
            Cmd("testdrive", TestDriveCommand);
            Cmd("sellcar", SellCommand);

            try
            {
                Db("CREATE TABLE IF NOT EXISTS dealer_sales (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, " +
                   "kind VARCHAR(8) NOT NULL, model VARCHAR(32) NOT NULL, plate VARCHAR(8) NOT NULL, " +
                   "amount INT NOT NULL, account VARCHAR(8) NOT NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "INDEX idx_license (license), INDEX idx_kind (kind))",
                   new object[0], _ => Debug.WriteLine("[dealer] sales table ready"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dealer] database unavailable (is oxmysql started?): " + e.Message);
            }

            Tick += TestDriveLoop;
        }

        // ==================================================================
        //  Commands
        // ==================================================================

        void DealerCommand(Player p, List<object> args)
        {
            if (!AtShowroom(p)) { Say(p, $"The showroom is at {ShowroomName}."); return; }
            if (InVehicle(p)) { Say(p, "Get out of the vehicle to browse the showroom."); return; }
            SendState(p, true);
        }

        void BuyCommand(Player p, List<object> args)
        {
            if (args.Count < 1)
            {
                Say(p, "Usage: /buycar <model> [cash|bank]");
                Say(p, "Models: " + string.Join(", ", Catalog.Select(c => c.Model)));
                return;
            }

            string account = args.Count > 1 ? args[1]?.ToString() : "bank";
            BuyCar(p, args[0]?.ToString(), account);
        }

        void TestDriveCommand(Player p, List<object> args)
        {
            if (args.Count < 1)
            {
                Say(p, "Usage: /testdrive <model>");
                Say(p, "Models: " + string.Join(", ", Catalog.Select(c => c.Model)));
                return;
            }
            BeginTestDrive(p, args[0]?.ToString());
        }

        void SellCommand(Player p, List<object> args)
        {
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id) || id <= 0)
            {
                Say(p, "Usage: /sellcar <vehicle id>  (see /garage)");
                return;
            }
            SellCar(p, id);
        }

        // ==================================================================
        //  Buying
        // ==================================================================

        void BuyCar(Player p, string model, string account)
        {
            string license = License(p);
            if (license == null) { Say(p, "No license identifier found."); return; }
            if (!AtShowroom(p)) { Say(p, $"You need to be at {ShowroomName} to buy a car."); return; }
            if (InVehicle(p)) { Say(p, "Get out of the vehicle first."); return; }

            model = (model ?? "").Trim().ToLowerInvariant();
            CarModel car;
            if (!ByModel.TryGetValue(model, out car)) { Say(p, "That model is not for sale."); return; }

            account = (account ?? "").Trim().ToLowerInvariant();
            if (account != "cash" && account != "bank") { Say(p, "Choose cash or bank."); return; }

            Query("SELECT COUNT(*) AS c FROM player_vehicles WHERE owner = ?", new object[] { license }, rows =>
            {
                int owned = rows.Count == 0 ? 0 : Convert.ToInt32(rows[0]["c"]);
                if (owned >= MaxVehicles) { Say(p, $"Your garage is full ({MaxVehicles} vehicles)."); return; }

                // Check-and-deduct happens inside bank-cs, so this can never overdraw.
                bool paid;
                try
                {
                    paid = (bool)Exports["bank-cs"].removeMoney(Src(p), account, car.Price, $"dealer: {car.Label}");
                }
                catch (Exception e)
                {
                    Debug.WriteLine("[dealer] bank-cs unavailable: " + e.Message);
                    Say(p, "The bank is unavailable right now; nothing was charged.");
                    return;
                }
                if (!paid) { Say(p, $"You don't have ${car.Price} in {account}."); return; }

                string plate = NewPlate();
                Insert("INSERT INTO player_vehicles (owner, model, plate, state) VALUES (?, ?, ?, 1)",
                    new object[] { license, car.Model, plate }, id =>
                    {
                        if (id == 0)
                        {
                            // The charge already happened, so put it back. The car was never created.
                            Refund(p, account, car.Price, "dealer: purchase refund");
                            Say(p, "The sale could not be completed; your money was returned.");
                            Debug.WriteLine($"[dealer] insert failed for {car.Model} [{plate}] ({account}), refunded");
                            return;
                        }

                        LogSale(license, "buy", car.Model, plate, car.Price, account);
                        Say(p, $"Bought {car.Label} for ${car.Price} from your {account}. Plate [{plate}].");
                        Debug.WriteLine($"[dealer] {p.Name} ({license}) bought {car.Model} [{plate}] for ${car.Price} ({account})");

                        // Hand the stored row to the garage so the normal spawn path, ownership
                        // tracking, /park, /lock and /locate all keep working. If the garage is
                        // unavailable the car simply stays stored and can be taken out later.
                        try
                        {
                            bool accepted = (bool)Exports["garage-cs"].deliver(Src(p), plate);
                            if (!accepted) Say(p, $"It is stored in your garage; use /takeout {id}.");
                        }
                        catch (Exception e)
                        {
                            Debug.WriteLine("[dealer] garage-cs deliver unavailable: " + e.Message);
                            Say(p, $"It is stored in your garage; use /takeout {id}.");
                        }
                    });
            });
        }

        // ==================================================================
        //  Selling / trade-in
        // ==================================================================

        void SellCar(Player p, int id)
        {
            string license = License(p);
            if (license == null) { Deny(p, "No license identifier found."); return; }
            if (!AtShowroom(p)) { Deny(p, $"You need to be at {ShowroomName} to sell a car."); return; }

            Query("SELECT model, plate, state, stolen, engine, body FROM player_vehicles WHERE id = ? AND owner = ?",
                new object[] { id, license }, rows =>
                {
                    if (rows.Count != 1) { Deny(p, "That vehicle doesn't exist or isn't yours."); return; }

                    IDictionary<string, object> row = rows[0];
                    string model = row["model"].ToString();
                    string plate = row["plate"].ToString();

                    CarModel car;
                    if (!ByModel.TryGetValue(model, out car)) { Deny(p, "We don't buy that model."); return; }
                    if (Convert.ToInt32(row["state"]) != 1) { Deny(p, "Store the vehicle in your garage first (/park)."); return; }
                    if (Convert.ToInt32(row["stolen"]) == 1) { Deny(p, "A stolen vehicle can't be sold."); return; }

                    int price = TradeIn(car.Price, Convert.ToInt32(row["engine"]), Convert.ToInt32(row["body"]));

                    bool paid;
                    try
                    {
                        paid = (bool)Exports["bank-cs"].addBank(Src(p), price, $"dealer: sold {car.Label}");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("[dealer] bank-cs unavailable: " + e.Message);
                        Deny(p, "The bank is unavailable right now; the car was not sold.");
                        return;
                    }
                    if (!paid) { Deny(p, "The bank could not accept the payment; the car was not sold."); return; }

                    // The payment is in. Only one conditional DELETE can win, so two sale requests
                    // can never both be paid for the same row. If this one loses, reverse the pay.
                    Db("DELETE FROM player_vehicles WHERE id = ? AND owner = ? AND state = 1 AND stolen = 0",
                        new object[] { id, license }, affected =>
                        {
                            if (affected != 1)
                            {
                                ReverseSale(p, price);
                                return;
                            }

                            LogSale(license, "sell", car.Model, plate, price, "bank");
                            string message = $"Sold {car.Label} [{plate}] for ${price} to the dealer (bank).";
                            Say(p, message);
                            Toast(p, message, true);
                            Debug.WriteLine($"[dealer] {p.Name} ({license}) sold {car.Model} [{plate}] for ${price}");
                            SendState(p, false);
                        });
                });
        }

        void ReverseSale(Player p, int price)
        {
            try
            {
                bool reverted = (bool)Exports["bank-cs"].removeMoney(Src(p), "bank", price, "dealer: sale reverted");
                Deny(p, reverted
                    ? "The vehicle changed; the sale was cancelled and the payment reversed."
                    : "The vehicle changed; the sale was cancelled. Contact an admin about the payment.");
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dealer] sale reversal failed: " + e.Message);
                Deny(p, "The vehicle changed; the sale was cancelled. Contact an admin about the payment.");
            }
        }

        // ==================================================================
        //  Test drives (local temp vehicle, never a database row)
        // ==================================================================

        void BeginTestDrive(Player p, string model)
        {
            string license = License(p);
            if (license == null) { Say(p, "No license identifier found."); return; }
            if (!AtShowroom(p)) { Say(p, $"Test drives start at {ShowroomName}."); return; }
            if (InVehicle(p)) { Say(p, "Get out of the vehicle first."); return; }

            model = (model ?? "").Trim().ToLowerInvariant();
            CarModel car;
            if (!ByModel.TryGetValue(model, out car)) { Say(p, "That model is not available for a test drive."); return; }

            if (_testDrives.ContainsKey(license)) { Say(p, "Finish your current test drive first."); return; }

            var td = new TestDrive
            {
                License = license,
                Model = car.Model,
                Label = car.Label,
                Plate = NewTestPlate(),
                StartedAt = GetGameTimer(),
                ExpiresAt = 0
            };
            _testDrives[license] = td;

            TriggerClientEvent(p, "dealer:testdrive", car.Model, td.Plate);
            Say(p, $"Test drive: {car.Label}. You have {_testSeconds} seconds; leaving the car ends it.");
            Debug.WriteLine($"[dealer] {p.Name} ({license}) test driving {car.Model} [{td.Plate}]");
        }

        // The client reports the entity it really spawned. Nothing starts until the server has
        // seen the real car at the showroom with the right plate, model and seated driver.
        [EventHandler("dealer:testdriveStarted")]
        void OnTestDriveStarted([FromSource] Player p, int netId, string plate)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;

            TestDrive td;
            if (!_testDrives.TryGetValue(license, out td)) return;

            plate = (plate ?? "").Trim();
            if (td.NetId != 0 || plate != td.Plate) return;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity))
            {
                EndTestDrive(license, "The test car did not appear; try again.");
                return;
            }

            // Never delete an entity that has not proved it is the car we issued: a client
            // could otherwise report any network id and have someone else's vehicle removed.
            if ((GetVehicleNumberPlateText(entity) ?? "").Trim() != td.Plate)
            {
                EndTestDrive(license, "The test car could not be verified; try again.");
                return;
            }

            int ped = GetPlayerPed(p.Handle);
            bool seated = ped != 0 && GetVehiclePedIsIn(ped, false) == entity;
            bool modelOk = GetEntityModel(entity) == (uint)GetHashKey(td.Model);
            bool nearOk = Vector3.Distance(GetEntityCoords(entity), _showroom) <= TestDriveScene;

            if (!seated || !modelOk || !nearOk)
            {
                if (DoesEntityExist(entity)) DeleteEntity(entity);
                EndTestDrive(license, "The test car could not be verified; try again.");
                return;
            }

            td.NetId = netId;
            td.ExpiresAt = GetGameTimer() + _testSeconds * 1000L;
            SendTestDriveTick(p, td, _testSeconds * 1000L);
            Debug.WriteLine($"[dealer] test drive verified for {license} [{td.Plate}] netId {netId}");
        }

        [EventHandler("dealer:testdriveFailed")]
        void OnTestDriveFailed([FromSource] Player p)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;
            EndTestDrive(license, "The test car could not be spawned; try again.");
        }

        async Task TestDriveLoop()
        {
            while (true)
            {
                await Delay(1000);

                long now = GetGameTimer();
                foreach (TestDrive td in _testDrives.Values.ToList())
                {
                    if (td.NetId == 0)
                    {
                        if (now - td.StartedAt > TestSpawnTimeoutMs)
                            EndTestDrive(td.License, "The test car did not appear; try again.");
                        continue;
                    }

                    Player p = FindPlayer(td.License);
                    if (p == null)
                    {
                        EndTestDrive(td.License, null);
                        continue;
                    }

                    int entity = NetworkGetEntityFromNetworkId(td.NetId);
                    if (entity == 0 || !DoesEntityExist(entity))
                    {
                        EndTestDrive(td.License, "The test car is gone; the drive is over.");
                        continue;
                    }

                    int ped = GetPlayerPed(p.Handle);
                    if (ped == 0 || GetVehiclePedIsIn(ped, false) != entity)
                    {
                        EndTestDrive(td.License, "Test drive ended: you left the vehicle.");
                        continue;
                    }

                    long remaining = td.ExpiresAt - now;
                    if (remaining <= 0)
                    {
                        EndTestDrive(td.License, "Test drive over.");
                        continue;
                    }

                    SendTestDriveTick(p, td, remaining);
                }
            }
        }

        void SendTestDriveTick(Player p, TestDrive td, long remainingMs)
        {
            int remaining = (int)Math.Ceiling(remainingMs / 1000.0);
            TriggerClientEvent(p, "dealer:testdriveTick", td.Model, td.Label, remaining);
        }

        void EndTestDrive(string license, string reason, bool notify = true)
        {
            TestDrive td;
            if (!_testDrives.TryGetValue(license, out td)) return;
            _testDrives.Remove(license);

            DeleteNet(td.NetId);

            Player p = FindPlayer(license);
            if (p != null && notify)
            {
                TriggerClientEvent(p, "dealer:testdriveEnd", reason ?? "");
                if (!string.IsNullOrEmpty(reason)) Say(p, reason);
            }

            Debug.WriteLine($"[dealer] test drive ended for {license} [{td.Plate}] ({reason ?? "quiet"})");
        }

        // ==================================================================
        //  NUI state
        // ==================================================================

        void SendState(Player p, bool open)
        {
            string license = License(p);
            if (license == null) return;

            Query("SELECT id, model, plate, state, stolen, engine, body FROM player_vehicles WHERE owner = ? ORDER BY id",
                new object[] { license }, rows =>
                {
                    string cash = "0", bank = "0";
                    try
                    {
                        string balances = Exports["bank-cs"].getBalances(Src(p));
                        if (!string.IsNullOrEmpty(balances))
                        {
                            string[] parts = balances.Split(',');
                            if (parts.Length == 2) { cash = parts[0]; bank = parts[1]; }
                        }
                    }
                    catch (Exception)
                    {
                        // The page shows zeros; purchases fail safely if the bank is really down.
                    }

                    var sb = new StringBuilder(1024);
                    sb.Append("{\"type\":\"state\",\"showroom\":").Append(Json(ShowroomName))
                      .Append(",\"cash\":").Append(cash)
                      .Append(",\"bank\":").Append(bank)
                      .Append(",\"maxVehicles\":").Append(MaxVehicles)
                      .Append(",\"ownedCount\":").Append(rows.Count)
                      .Append(",\"catalog\":[");

                    for (int i = 0; i < Catalog.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        CarModel c = Catalog[i];
                        sb.Append("{\"model\":").Append(Json(c.Model))
                          .Append(",\"label\":").Append(Json(c.Label))
                          .Append(",\"price\":").Append(c.Price).Append('}');
                    }

                    sb.Append("],\"owned\":[");
                    bool first = true;
                    foreach (IDictionary<string, object> row in rows)
                    {
                        if (Convert.ToInt32(row["state"]) != 1) continue;
                        if (Convert.ToInt32(row["stolen"]) != 0) continue;

                        CarModel car;
                        if (!ByModel.TryGetValue(row["model"].ToString(), out car)) continue;

                        int engine = Convert.ToInt32(row["engine"]);
                        int body = Convert.ToInt32(row["body"]);
                        if (!first) sb.Append(',');
                        first = false;

                        sb.Append("{\"id\":").Append(Convert.ToInt32(row["id"]))
                          .Append(",\"model\":").Append(Json(car.Model))
                          .Append(",\"label\":").Append(Json(car.Label))
                          .Append(",\"plate\":").Append(Json(row["plate"].ToString()))
                          .Append(",\"condition\":").Append(Condition(engine, body))
                          .Append(",\"value\":").Append(TradeIn(car.Price, engine, body)).Append('}');
                    }
                    sb.Append("]}");

                    if (Players[Src(p)] == null) return;
                    TriggerClientEvent(p, open ? "dealer:show" : "dealer:state", sb.ToString());
                });
        }

        // ==================================================================
        //  Events from the client / NUI
        // ==================================================================

        [EventHandler("dealer:requestShowroom")]
        void OnRequestShowroom([FromSource] Player p)
        {
            if (p == null || RateLimited(Src(p))) return;
            TriggerClientEvent(p, "dealer:showroom", _showroom.X, _showroom.Y, _showroom.Z, _range, ShowroomName);
        }

        [EventHandler("dealer:requestOpen")]
        void OnRequestOpen([FromSource] Player p)
        {
            if (p == null || RateLimited(Src(p))) return;
            if (!AtShowroom(p)) { Say(p, $"The showroom is at {ShowroomName}."); return; }
            if (InVehicle(p)) { Say(p, "Get out of the vehicle to browse the showroom."); return; }
            SendState(p, true);
        }

        [EventHandler("dealer:buy")]
        void OnBuy([FromSource] Player p, string model, string account)
        {
            if (p == null || RateLimited(Src(p))) return;
            BuyCar(p, model, account);
        }

        [EventHandler("dealer:sell")]
        void OnSell([FromSource] Player p, int id)
        {
            if (p == null || RateLimited(Src(p))) return;
            SellCar(p, id);
        }

        [EventHandler("dealer:testdrive")]
        void OnTestDrive([FromSource] Player p, string model)
        {
            if (p == null || RateLimited(Src(p))) return;
            BeginTestDrive(p, model);
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            Forget(Src(p));

            string license = License(p);
            if (license == null) return;
            EndTestDrive(license, null);
        }

        [EventHandler("onResourceStop")]
        void OnResourceStop(string resourceName)
        {
            if (resourceName != GetCurrentResourceName()) return;
            foreach (string license in _testDrives.Keys.ToList())
                EndTestDrive(license, null, false);
        }

        // ==================================================================
        //  Helpers
        // ==================================================================

        // The server reads the real position; the client is never asked where it is.
        bool AtShowroom(Player p)
        {
            if (p == null) return false;
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return false;
            return Vector3.Distance(GetEntityCoords(ped), _showroom) <= _range;
        }

        int TradeIn(int basePrice, int engine, int body)
        {
            double condition = (ClampHealth(engine) + ClampHealth(body)) / 2000.0;
            int percent = (int)Math.Round(_sellMinPercent + (_sellMaxPercent - _sellMinPercent) * condition);
            int price = (int)Math.Round(basePrice * percent / 100.0);
            return Math.Max(1, price);
        }

        static int Condition(int engine, int body) =>
            (int)Math.Round((ClampHealth(engine) + ClampHealth(body)) / 20.0);

        static int ClampHealth(int v) => v < 0 ? 0 : v > 1000 ? 1000 : v;

        static float ClampFloat(float v, float min, float max) => v < min ? min : v > max ? max : v;

        static float GetConvarFloat(string name, float fallback)
        {
            string raw = GetConvar(name, "");
            float value;
            return !string.IsNullOrEmpty(raw)
                   && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        string NewPlate()
        {
            var chars = new char[8];
            for (int i = 0; i < chars.Length; i++) chars[i] = PlateChars[_rng.Next(PlateChars.Length)];
            return new string(chars);
        }

        string NewTestPlate()
        {
            var digits = new char[4];
            for (int i = 0; i < digits.Length; i++) digits[i] = (char)('0' + _rng.Next(10));
            return "TEST" + new string(digits);
        }

        void DeleteNet(int netId)
        {
            if (netId == 0) return;
            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity != 0 && DoesEntityExist(entity)) DeleteEntity(entity);
        }

        Player FindPlayer(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            foreach (Player p in Players.ToList())
                if (p != null && License(p) == license) return p;
            return null;
        }

        void Refund(Player p, string account, int amount, string reason)
        {
            try
            {
                bool ok = (bool)Exports["bank-cs"].addMoney(Src(p), account, amount, reason);
                if (!ok) Debug.WriteLine($"[dealer] refund failed for {Src(p)} ${amount} ({account})");
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dealer] refund failed, bank-cs unavailable: " + e.Message);
            }
        }

        void LogSale(string license, string kind, string model, string plate, int amount, string account)
        {
            Db("INSERT INTO dealer_sales (license, kind, model, plate, amount, account) VALUES (?, ?, ?, ?, ?, ?)",
                new object[] { license, kind, model, plate, amount, account }, _ => { });
        }

        // Chat is always visible; the toast only appears if the page is still open (trade-ins).
        void Deny(Player p, string message)
        {
            Say(p, message);
            Toast(p, message, false);
        }

        void Toast(Player p, string message, bool ok)
        {
            TriggerClientEvent(p, "dealer:toast", message, ok);
        }

        void Insert(string sql, object[] args, Action<int> done)
        {
            Exports["oxmysql"].insert(sql, args, new Action<dynamic>(id => done(id == null ? 0 : Convert.ToInt32(id))));
        }

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[dealer] /{name} is in-game only"); return; }
                Player p = Players[src];
                if (p == null || RateLimited(src)) return;
                handler(p, args);
            }), false);
        }

        static string Json(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        }
    }
}
