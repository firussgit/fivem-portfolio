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

    class Rental
    {
        public string License;
        public string Model;
        public string Label;
        public string Plate;
        public string Account;
        public int Fee;
        public int Deposit;
        public long StartedMs;
        public long ExpiresMs;
        public long ReadyByMs;   // spawn deadline; refreshed when a lease is resumed
        public bool Resumed;     // true when the car is being handed back after a relog/restart
        public int SpawnAttempts;
        public int NetId;
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
        const int RentalSpawnTimeoutMs = 15000;
        const float RentalCarScene = 40f; // metres: a reported rental car must be near its driver
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
        readonly Dictionary<string, Rental> _rentals = new Dictionary<string, Rental>();
        readonly HashSet<string> _rentalLoaded = new HashSet<string>();

        Vector3 _showroom;
        float _range;
        int _testSeconds;
        int _sellMinPercent;
        int _sellMaxPercent;
        int _rentMinutes;
        int _rentFeePercent;
        int _rentDepositPercent;
        int _rentDepositMax;
        long _lastRentalSweep;

        public DealerServer()
        {
            ChatPrefix = "Dealer";
            ChatColour = new[] { 255, 190, 70 };

            // The showroom is tunable from server.cfg; the client is told where it is, so both
            // sides always agree and the client never decides where the dealership is.
            // The marker sits on the forecourt, not inside the building: the vanilla PDM
            // interior floor is not streamed on every client, so standing inside drops a
            // player through the map. (-45, -1098) is the middle of the showroom floor.
            _showroom = new Vector3(
                GetConvarFloat("dealer_showroom_x", -56.8f),
                GetConvarFloat("dealer_showroom_y", -1109.9f),
                GetConvarFloat("dealer_showroom_z", 25.44f));
            _range = ClampFloat(GetConvarFloat("dealer_range", 8.0f), 3f, 30f);
            _testSeconds = Math.Max(30, Math.Min(300, GetConvarInt("dealer_testdrive_seconds", 90)));
            _sellMinPercent = Math.Max(0, Math.Min(100, GetConvarInt("dealer_sell_min_percent", 35)));
            _sellMaxPercent = Math.Max(_sellMinPercent, Math.Min(100, GetConvarInt("dealer_sell_max_percent", 60)));
            _rentMinutes = Math.Max(5, Math.Min(180, GetConvarInt("dealer_rent_minutes", 20)));
            _rentFeePercent = Math.Max(1, Math.Min(25, GetConvarInt("dealer_rent_fee_percent", 4)));
            _rentDepositPercent = Math.Max(0, Math.Min(50, GetConvarInt("dealer_rent_deposit_percent", 10)));
            _rentDepositMax = Math.Max(0, Math.Min(100000, GetConvarInt("dealer_rent_deposit_max", 5000)));

            Cmd("dealer", DealerCommand);
            Cmd("buycar", BuyCommand);
            Cmd("testdrive", TestDriveCommand);
            Cmd("sellcar", SellCommand);
            Cmd("rentcar", RentCommand);
            Cmd("returncar", ReturnCommand);

            try
            {
                Db("CREATE TABLE IF NOT EXISTS dealer_sales (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, " +
                   "kind VARCHAR(8) NOT NULL, model VARCHAR(32) NOT NULL, plate VARCHAR(8) NOT NULL, " +
                   "amount INT NOT NULL, account VARCHAR(8) NOT NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "INDEX idx_license (license), INDEX idx_kind (kind))",
                   new object[0], _ => Debug.WriteLine("[dealer] sales table ready"));

                Db("CREATE TABLE IF NOT EXISTS rentals (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, " +
                   "model VARCHAR(32) NOT NULL, plate VARCHAR(8) NOT NULL, account VARCHAR(8) NOT NULL, " +
                   "fee INT NOT NULL, deposit INT NOT NULL, started_ms BIGINT NOT NULL, " +
                   "expires_ms BIGINT NOT NULL, ended_ms BIGINT NULL, end_reason VARCHAR(24) NULL, " +
                   "active_license VARCHAR(64) GENERATED ALWAYS AS (IF(ended_ms IS NULL, license, NULL)) STORED, " +
                   "INDEX idx_license (license), INDEX idx_open (license, ended_ms), " +
                   "UNIQUE KEY uq_active_license (active_license))",
                   new object[0], _ =>
                   {
                       Debug.WriteLine("[dealer] rentals table ready");

                       // Nobody is online at start, so any row still open past its expiry is dead.
                       // Closing these keeps the audit log honest and lets the player rent again.
                       long now = NowMs();
                       Db("UPDATE rentals SET ended_ms = ?, end_reason = 'expired' " +
                          "WHERE ended_ms IS NULL AND expires_ms <= ?",
                          new object[] { now, now }, closed =>
                          {
                              Debug.WriteLine($"[dealer] closed {closed} rental(s) that expired while offline");
                              // A resource restart keeps players connected: give their cars back.
                              // A full server start has nobody to resume; joins handle that.
                              ResumeAll();
                          });
                   });
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dealer] database unavailable (is oxmysql started?): " + e.Message);
            }

            Tick += TestDriveLoop;
            Tick += RentalLoop;
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

        void RentCommand(Player p, List<object> args)
        {
            if (args.Count < 1)
            {
                Say(p, "Usage: /rentcar <model> [cash|bank]");
                Say(p, "Models: " + string.Join(", ", Catalog.Select(c => c.Model)));
                Say(p, $"Rentals last {_rentMinutes} minutes: a fee plus a refundable deposit.");
                return;
            }

            string account = args.Count > 1 ? args[1]?.ToString() : "bank";
            RentCar(p, args[0]?.ToString(), account);
        }

        void ReturnCommand(Player p, List<object> args) => ReturnCar(p);

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
            if (_rentals.ContainsKey(license)) { Say(p, "Return your rental first (/returncar)."); return; }

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
            // Signed-int comparison on purpose: see the note in races-cs; casting one side to uint
            // breaks every model whose hash has the high bit set.
            bool modelOk = GetEntityModel(entity) == GetHashKey(td.Model);
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
        //  Rentals (a timed lease, never an owned row)
        // ==================================================================

        // A rental is deliberately not a player_vehicles row: it can't show up in /garage, be
        // sold, insured or parked. The server keeps the lease in `rentals`, spawns a temp car
        // and ends it on return or expiry. The client is only told which model and plate to spawn.
        void RentCar(Player p, string model, string account)
        {
            string license = License(p);
            if (license == null) { Say(p, "No license identifier found."); return; }
            if (!AtShowroom(p)) { Deny(p, $"Rentals start at {ShowroomName}."); return; }
            if (InVehicle(p)) { Deny(p, "Get out of the vehicle first."); return; }

            model = (model ?? "").Trim().ToLowerInvariant();
            CarModel car;
            if (!ByModel.TryGetValue(model, out car)) { Deny(p, "That model is not available to rent."); return; }

            account = (account ?? "").Trim().ToLowerInvariant();
            if (account != "cash" && account != "bank") { Deny(p, "Choose cash or bank."); return; }

            if (_testDrives.ContainsKey(license)) { Deny(p, "Finish your test drive first."); return; }
            if (_rentals.ContainsKey(license)) { Deny(p, "You already have a rental; return it first (/returncar)."); return; }

            int fee = RentalFee(car.Price);
            int deposit = RentalDeposit(car.Price);
            int total = fee + deposit;

            // Fee and deposit are taken together, so a failed charge can never leave half a lease.
            bool paid;
            try
            {
                paid = (bool)Exports["bank-cs"].removeMoney(Src(p), account, total, $"dealer: rental {car.Label}");
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dealer] bank-cs unavailable: " + e.Message);
                Deny(p, "The bank is unavailable right now; nothing was charged.");
                return;
            }
            if (!paid) { Deny(p, $"You need ${total} in {account} (${fee} fee + ${deposit} deposit)."); return; }

            string plate = NewRentPlate();
            long started = NowMs();
            long expires = started + _rentMinutes * 60000L;

            // Two steps on purpose: first close a lease that expired while this player was away,
            // then insert. uq_active_license (one open row per licence) is the database-level
            // single-winner guard, so two requests can never both start a rental.
            Db("UPDATE rentals SET ended_ms = ?, end_reason = 'expired' " +
               "WHERE license = ? AND ended_ms IS NULL AND expires_ms <= ?",
               new object[] { started, license, started }, _ =>
               Db("INSERT IGNORE INTO rentals " +
                  "(license, model, plate, account, fee, deposit, started_ms, expires_ms) " +
                  "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                  new object[] { license, car.Model, plate, account, fee, deposit, started, expires },
                  affected =>
                  {
                      if (affected != 1)
                      {
                          // The money is already in; put it all back. No car was handed over.
                          Refund(p, account, total, "dealer: rental refund");
                          Deny(p, "You already have a rental; nothing was charged.");
                          return;
                      }

                      var r = new Rental
                      {
                          License = license,
                          Model = car.Model,
                          Label = car.Label,
                          Plate = plate,
                          Account = account,
                          Fee = fee,
                          Deposit = deposit,
                          StartedMs = started,
                          ExpiresMs = expires,
                          ReadyByMs = started + RentalSpawnTimeoutMs,
                          Resumed = false,
                          NetId = 0
                      };
                      _rentals[license] = r;

                      TriggerClientEvent(p, "dealer:rentalSpawn", car.Model, plate);
                      Say(p, $"Rented {car.Label} for {_rentMinutes} minutes. Fee ${fee}, deposit ${deposit} " +
                             $"(returned with the car). Plate [{plate}]. Return it at the showroom with /returncar.");
                      Debug.WriteLine($"[dealer] {p.Name} ({license}) rented {car.Model} [{plate}] for {_rentMinutes} min ({account})");
                      }));
        }

        // The client reports the entity it really spawned. Nothing is accepted until the server
        // has seen the issued plate and model next to the player who rented it.
        [EventHandler("dealer:rentalStarted")]
        void OnRentalStarted([FromSource] Player p, int netId, string plate)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;

            Rental r;
            if (!_rentals.TryGetValue(license, out r)) return;

            plate = (plate ?? "").Trim();
            if (r.NetId != 0 || plate != r.Plate) return;

            int entity = NetworkGetEntityFromNetworkId(netId);
            if (entity == 0 || !DoesEntityExist(entity))
            {
                FailRental(r, "The rental car did not appear; your money was returned.",
                              "The rental could not be restored; the lease has ended.");
                return;
            }

            // Never delete an entity that has not proved it is the car we issued: a client could
            // otherwise report any network id and have someone else's vehicle removed.
            if ((GetVehicleNumberPlateText(entity) ?? "").Trim() != r.Plate)
            {
                FailRental(r, "The rental car could not be verified; your money was returned.",
                              "The rental could not be restored; the lease has ended.");
                return;
            }

            int ped = GetPlayerPed(p.Handle);
            bool modelOk = GetEntityModel(entity) == GetHashKey(r.Model);
            bool nearOk = ped != 0 && Vector3.Distance(GetEntityCoords(entity), GetEntityCoords(ped)) <= RentalCarScene;

            if (!modelOk || !nearOk)
            {
                if (DoesEntityExist(entity)) DeleteEntity(entity);
                FailRental(r, "The rental car could not be verified; your money was returned.",
                              "The rental could not be restored; the lease has ended.");
                return;
            }

            r.NetId = netId;

            long remaining = r.ExpiresMs - NowMs();
            if (remaining <= 0)
            {
                EndRental(r, "expired", false, false, "Rental over. The car was collected and the deposit kept.", true);
                return;
            }

            TriggerClientEvent(p, "dealer:rentalTick", r.Model, r.Label, remaining);
            SendState(p, false);
            Debug.WriteLine($"[dealer] rental verified for {license} [{r.Plate}] netId {netId}");
        }

        [EventHandler("dealer:rentalFailed")]
        void OnRentalFailed([FromSource] Player p)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;

            Rental r;
            if (!_rentals.TryGetValue(license, out r)) return;

            // Only a lease that never got a verified car may be cancelled for a refund. Once the
            // car is out, ending it is /returncar (deposit) or expiry (kept), never a free undo.
            if (r.NetId != 0) { Say(p, "The rental car is already with you; use /returncar."); return; }

            // The client says spawning failed; make sure it isn't quietly holding the car. The
            // plate is server-issued, so anything carrying it is ours to remove.
            DeletePlateVehicles(r.Plate);

            FailRental(r, "The rental car could not be spawned; your money was returned.",
                          "The rental could not be restored; the lease has ended.");
        }

        async Task RentalLoop()
        {
            while (true)
            {
                await Delay(1000);

                long now = NowMs();

                // A client can miss its startup message (or connect during a restart); make sure
                // every connected player's lease is looked up at least once.
                if (now - _lastRentalSweep > 10000)
                {
                    _lastRentalSweep = now;
                    foreach (Player p in Players.ToList())
                    {
                        if (p == null) continue;
                        string license = License(p);
                        if (license != null && !_rentalLoaded.Contains(license)) EnsureRental(p);
                    }
                }

                foreach (Rental r in _rentals.Values.ToList())
                {
                    Player p = FindPlayer(r.License);
                    if (p == null)
                    {
                        // Dropped: the car goes with the client, but the lease keeps running, so
                        // relogging can't reset the clock. A reconnect resumes it from the row.
                        DeleteNet(r.NetId);
                        _rentals.Remove(r.License);
                        _rentalLoaded.Remove(r.License);
                        continue;
                    }

                    int ped = GetPlayerPed(p.Handle);
                    if (ped == 0) continue; // still loading; keep the lease as it is

                    if (r.NetId == 0)
                    {
                        if (now > r.ReadyByMs)
                        {
                            // A resumed lease is retried a few times: a spawn message lost during
                            // a relog or restart must not silently end a lease already paid for.
                            if (r.Resumed && r.SpawnAttempts < 3)
                            {
                                r.SpawnAttempts++;
                                r.ReadyByMs = now + RentalSpawnTimeoutMs;
                                TriggerClientEvent(p, "dealer:rentalSpawn", r.Model, r.Plate);
                                Debug.WriteLine($"[dealer] re-sending rental spawn for [{r.Plate}] (attempt {r.SpawnAttempts})");
                                continue;
                            }

                            // Same rule as a reported failure: never refund while the issued car
                            // is still around, or a client could keep it for free.
                            DeletePlateVehicles(r.Plate);
                            FailRental(r, "The rental car did not appear; your money was returned.",
                                          "The rental could not be restored; the lease has ended.");
                        }
                        continue;
                    }

                    int entity = NetworkGetEntityFromNetworkId(r.NetId);
                    if (entity == 0 || !DoesEntityExist(entity))
                    {
                        EndRental(r, "lost", false, false,
                            "The rental car was lost; the deposit was kept. You can rent again.", true);
                        continue;
                    }

                    long remaining = r.ExpiresMs - now;
                    if (remaining <= 0)
                    {
                        EndRental(r, "expired", false, false,
                            "Rental over. The car was collected and the deposit kept.", true);
                        continue;
                    }

                    TriggerClientEvent(p, "dealer:rentalTick", r.Model, r.Label, remaining);
                }
            }
        }

        // Resource restart with players still connected: bring every open lease back. A full
        // server start has nobody to resume and leaves it to EnsureRental on join.
        void ResumeAll()
        {
            long now = NowMs();
            Query("SELECT license, model, plate, account, fee, deposit, started_ms, expires_ms FROM rentals " +
                  "WHERE ended_ms IS NULL AND expires_ms > ? ORDER BY id",
                new object[] { now }, rows =>
                {
                    foreach (IDictionary<string, object> row in rows)
                    {
                        string license = row["license"].ToString();
                        Player p = FindPlayer(license);
                        if (p == null) continue;
                        if (_rentalLoaded.Contains(license) || _rentals.ContainsKey(license)) continue;

                        CarModel car;
                        if (!ByModel.TryGetValue(row["model"].ToString(), out car)) continue;

                        var r = new Rental
                        {
                            License = license,
                            Model = car.Model,
                            Label = car.Label,
                            Plate = row["plate"].ToString(),
                            Account = row["account"].ToString(),
                            Fee = Convert.ToInt32(row["fee"]),
                            Deposit = Convert.ToInt32(row["deposit"]),
                            StartedMs = Convert.ToInt64(row["started_ms"]),
                            ExpiresMs = Convert.ToInt64(row["expires_ms"]),
                            ReadyByMs = NowMs() + RentalSpawnTimeoutMs,
                            Resumed = true,
                            NetId = 0
                        };
                        _rentals[license] = r;
                        _rentalLoaded.Add(license);

                        TriggerClientEvent(p, "dealer:rentalSpawn", car.Model, r.Plate);
                        Say(p, $"Resumed your {car.Label} rental [{r.Plate}] after the restart.");
                    }
                });
        }

        // Reconnect / resource restart: pick the open lease up from the database. Expiry is a
        // fixed timestamp, so time spent offline still counts against the rental.
        void EnsureRental(Player p)
        {
            string license = License(p);
            if (license == null || !_rentalLoaded.Add(license)) return;

            long now = NowMs();
            Query("SELECT model, plate, account, fee, deposit, started_ms, expires_ms FROM rentals " +
                  "WHERE license = ? AND ended_ms IS NULL ORDER BY id DESC LIMIT 1",
                new object[] { license }, rows =>
                {
                    if (rows.Count != 1) return;

                    IDictionary<string, object> row = rows[0];
                    string plate = row["plate"].ToString();
                    long expires = Convert.ToInt64(row["expires_ms"]);

                    if (expires <= now)
                    {
                        Db("UPDATE rentals SET ended_ms = ?, end_reason = 'expired' " +
                           "WHERE license = ? AND plate = ? AND ended_ms IS NULL",
                           new object[] { now, license, plate }, _ => { });
                        Say(p, "Your rental expired while you were away; the deposit was kept.");
                        return;
                    }

                    CarModel car;
                    if (!ByModel.TryGetValue(row["model"].ToString(), out car)) return;
                    if (_rentals.ContainsKey(license)) return;

                    var r = new Rental
                    {
                        License = license,
                        Model = car.Model,
                        Label = car.Label,
                        Plate = plate,
                        Account = row["account"].ToString(),
                        Fee = Convert.ToInt32(row["fee"]),
                        Deposit = Convert.ToInt32(row["deposit"]),
                        StartedMs = Convert.ToInt64(row["started_ms"]),
                        ExpiresMs = expires,
                        ReadyByMs = now + RentalSpawnTimeoutMs,
                        Resumed = true,
                        NetId = 0
                    };
                    _rentals[license] = r;

                    int minutes = (int)Math.Ceiling((expires - now) / 60000.0);
                    TriggerClientEvent(p, "dealer:rentalSpawn", car.Model, plate);
                    Say(p, $"Resumed your {car.Label} rental [{plate}]: about {minutes} minute(s) left.");
                });
        }

        void ReturnCar(Player p)
        {
            string license = License(p);
            if (license == null) { Say(p, "No license identifier found."); return; }
            if (!AtShowroom(p)) { Deny(p, $"Bring the car back to {ShowroomName} to return it."); return; }

            Rental r;
            if (!_rentals.TryGetValue(license, out r)) { Deny(p, "You have no active rental."); return; }

            int entity = r.NetId == 0 ? 0 : NetworkGetEntityFromNetworkId(r.NetId);
            bool here = entity != 0 && DoesEntityExist(entity)
                && Vector3.Distance(GetEntityCoords(entity), _showroom) <= _range + 8f;
            if (!here)
            {
                Deny(p, "The rental car has to be at the showroom; the deposit is held until it is back.");
                return;
            }

            EndRental(r, "returned", false, true,
                $"Returned {r.Label} [{r.Plate}]. Deposit ${r.Deposit} refunded.", true);
        }

        // A fresh lease that never delivered a car is refunded in full; a resumed lease that
        // can't be restored is simply closed, because the player already had the car.
        void FailRental(Rental r, string freshMessage, string resumedMessage)
        {
            bool refund = !r.Resumed;
            EndRental(r, "failed", refund, refund, refund ? freshMessage : resumedMessage, true);
        }

        // The one place a lease ends. The conditional UPDATE decides which caller wins, and the
        // money only moves in the winner, so an expiry and a return can never both refund.
        void EndRental(Rental r, string reason, bool refundFee, bool refundDeposit, string message, bool notify)
        {
            if (r == null || !_rentals.ContainsKey(r.License)) return;
            _rentals.Remove(r.License);
            DeleteNet(r.NetId);
            r.NetId = 0;

            long now = NowMs();
            Db("UPDATE rentals SET ended_ms = ?, end_reason = ? " +
               "WHERE license = ? AND plate = ? AND ended_ms IS NULL",
               new object[] { now, reason, r.License, r.Plate }, affected =>
               {
                   if (affected != 1)
                   {
                       Debug.WriteLine($"[dealer] rental [{r.Plate}] was already closed; no money moved");
                       return;
                   }

                   Player p = FindPlayer(r.License);
                   if (p == null) return;

                   if (refundFee && r.Fee > 0) Refund(p, r.Account, r.Fee, "dealer: rental fee refund");
                   if (refundDeposit && r.Deposit > 0) Refund(p, r.Account, r.Deposit, "dealer: rental deposit refund");

                   if (notify)
                   {
                       TriggerClientEvent(p, "dealer:rentalEnd", message ?? "");
                       if (!string.IsNullOrEmpty(message)) Say(p, message);
                       SendState(p, false);
                   }
               });

            Debug.WriteLine($"[dealer] rental ended for {r.License} [{r.Plate}] ({reason})");
        }

        int RentalFee(int price) => Math.Max(100, RoundTo(price * _rentFeePercent / 100.0, 10));

        int RentalDeposit(int price) =>
            _rentDepositMax <= 0 ? 0 : Math.Min(_rentDepositMax, RoundTo(price * _rentDepositPercent / 100.0, 10));

        static int RoundTo(double value, int step) => (int)(Math.Round(value / step) * step);

        string NewRentPlate()
        {
            var digits = new char[4];
            for (int i = 0; i < digits.Length; i++) digits[i] = (char)('0' + _rng.Next(10));
            return "RENT" + new string(digits);
        }

        // Wall-clock milliseconds, so a lease survives a restart and offline time still counts.
        static long NowMs() =>
            (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

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

                    sb.Append("],\"rentMinutes\":").Append(_rentMinutes).Append(",\"rent\":[");

                    for (int i = 0; i < Catalog.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        CarModel c = Catalog[i];
                        sb.Append("{\"model\":").Append(Json(c.Model))
                          .Append(",\"label\":").Append(Json(c.Label))
                          .Append(",\"price\":").Append(c.Price)
                          .Append(",\"fee\":").Append(RentalFee(c.Price))
                          .Append(",\"deposit\":").Append(RentalDeposit(c.Price)).Append('}');
                    }

                    sb.Append("],\"rental\":");
                    Rental rental;
                    if (_rentals.TryGetValue(license, out rental))
                    {
                        int remaining = (int)Math.Max(0, Math.Ceiling((rental.ExpiresMs - NowMs()) / 1000.0));
                        sb.Append("{\"model\":").Append(Json(rental.Model))
                          .Append(",\"label\":").Append(Json(rental.Label))
                          .Append(",\"plate\":").Append(Json(rental.Plate))
                          .Append(",\"deposit\":").Append(rental.Deposit)
                          .Append(",\"remaining\":").Append(remaining).Append('}');
                    }
                    else sb.Append("null");

                    sb.Append(",\"owned\":[");
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
            // A lease outlives a relog or a restart, so every client that reports in also asks
            // whether it still has one to resume.
            EnsureRental(p);
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

        [EventHandler("dealer:rent")]
        void OnRent([FromSource] Player p, string model, string account)
        {
            if (p == null || RateLimited(Src(p))) return;
            RentCar(p, model, account);
        }

        [EventHandler("dealer:returncar")]
        void OnReturnCar([FromSource] Player p)
        {
            if (p == null || RateLimited(Src(p))) return;
            ReturnCar(p);
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

            Rental rental;
            if (_rentals.TryGetValue(license, out rental))
            {
                // The car belongs to the client's session; the lease row stays open so the clock
                // keeps running. EnsureRental picks it up if they come back before it expires.
                DeleteNet(rental.NetId);
                _rentals.Remove(license);
            }
            _rentalLoaded.Remove(license);
        }

        [EventHandler("onResourceStop")]
        void OnResourceStop(string resourceName)
        {
            if (resourceName != GetCurrentResourceName()) return;
            foreach (string license in _testDrives.Keys.ToList())
                EndTestDrive(license, null, false);

            // Deleting the temp cars is all we do: the rows stay open so unexpired rentals
            // resume after the restart instead of silently disappearing.
            foreach (Rental rental in _rentals.Values.ToList())
                DeleteNet(rental.NetId);
            _rentals.Clear();
            _rentalLoaded.Clear();
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

        // Last-resort cleanup when a lease ends before verification: find anything carrying the
        // server-issued plate so a client can't keep the car after claiming the spawn failed.
        void DeletePlateVehicles(string plate)
        {
            if (string.IsNullOrEmpty(plate)) return;
            foreach (int vehicle in GetAllVehicles())
            {
                if (vehicle == 0 || !DoesEntityExist(vehicle)) continue;
                if ((GetVehicleNumberPlateText(vehicle) ?? "").Trim() != plate) continue;
                DeleteEntity(vehicle);
                Debug.WriteLine($"[dealer] removed unverified rental vehicle [{plate}]");
            }
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
