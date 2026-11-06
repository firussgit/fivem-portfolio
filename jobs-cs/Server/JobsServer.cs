using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using Portfolio.Core;
using static CitizenFX.Core.Native.API;

namespace Jobs.Server
{
    class Grade
    {
        public string Name;
        public int Salary;
        public Grade(string name, int salary) { Name = name; Salary = salary; }
    }

    class JobDef
    {
        public string Label;
        public Grade[] Grades;
        public JobDef(string label, params Grade[] grades) { Label = label; Grades = grades; }
    }

    class JobState
    {
        public string Job = "unemployed";
        public int Grade;
        public bool OnDuty;
    }

    class StopDef
    {
        public string Label;
        public Vector3 Pos;
        public StopDef(string label, Vector3 pos) { Label = label; Pos = pos; }
    }

    class RouteDef
    {
        public string Name;
        public StopDef[] Stops;
        public RouteDef(string name, params StopDef[] stops) { Name = name; Stops = stops; }
    }

    class ActivityDef
    {
        public string Id;
        public string Label;
        public string Job;
        public Vector3 Hub;
        public int BasePay;
        public int PayPerKm;
        public int MinSeconds;
        public RouteDef[] Routes;

        public ActivityDef(string id, string label, string job, Vector3 hub, int basePay, int payPerKm, int minSeconds, params RouteDef[] routes)
        {
            Id = id;
            Label = label;
            Job = job;
            Hub = hub;
            BasePay = basePay;
            PayPerKm = payPerKm;
            MinSeconds = minSeconds;
            Routes = routes;
        }
    }

    class RunState
    {
        public string License;
        public int Src;
        public ActivityDef Activity;
        public RouteDef Route;
        public int Index;
        public int Payout;
        public int TimeLimit;
        public int Seconds;
        public long StartedAt;
        public long LastClaimAt;
        public long LastAttemptAt;
        public Vector3 LastPos;
    }

    /// <summary>
    /// Jobs with grades, a duty toggle and periodic paychecks, plus repeatable job activities
    /// (courier deliveries and taxi fares). Salaries and routes live in code (the server is the
    /// only authority) and money is paid through bank-cs' server-only exports.
    /// </summary>
    public class JobsServer : PortfolioScript
    {
        // The definitions are the source of truth: the database only stores job name + grade index.
        static readonly Dictionary<string, JobDef> Jobs = new Dictionary<string, JobDef>
        {
            ["unemployed"] = new JobDef("Unemployed", new Grade("Citizen", 0)),
            ["courier"] = new JobDef("Go Postal", new Grade("Courier", 110), new Grade("Senior Courier", 160)),
            ["police"] = new JobDef("Los Santos Police", new Grade("Cadet", 150), new Grade("Officer", 250), new Grade("Sergeant", 350)),
            ["ems"] = new JobDef("Emergency Medical Services", new Grade("Trainee", 140), new Grade("Paramedic", 260), new Grade("Chief", 380)),
            ["mechanic"] = new JobDef("Mechanic", new Grade("Apprentice", 120), new Grade("Mechanic", 200)),
            ["taxi"] = new JobDef("Downtown Cab Co.", new Grade("Driver", 110)),
        };

        // Routes are fixed server-side data. Hubs can be moved in-game with /setworkhub, so the
        // defaults only need to be roughly right (they are replaced as soon as an admin places one).
        static readonly Dictionary<string, ActivityDef> Activities = new Dictionary<string, ActivityDef>
        {
            ["courier"] = new ActivityDef("courier", "Courier deliveries", "courier", new Vector3(75f, -1360f, 29f), 350, 28, 180,
                new RouteDef("Downtown loop",
                    new StopDef("Mission Row", new Vector3(425.1f, -979.6f, 30.7f)),
                    new StopDef("Pillbox Hill", new Vector3(307.7f, -595.3f, 43.3f)),
                    new StopDef("Vinewood Blvd", new Vector3(293.0f, 180.0f, 104.0f)),
                    new StopDef("Rockford Hills", new Vector3(-550.0f, -170.0f, 38.0f))),
                new RouteDef("Airport run",
                    new StopDef("LS International", new Vector3(-1037.8f, -2737.8f, 20.2f)),
                    new StopDef("Vespucci Beach", new Vector3(-1200.0f, -1500.0f, 4.0f)),
                    new StopDef("Del Perro Pier", new Vector3(-1850.0f, -1231.0f, 13.0f)),
                    new StopDef("Legion Square", new Vector3(215.7f, -810.2f, 30.7f))),
                new RouteDef("Southside run",
                    new StopDef("Mirror Park", new Vector3(1050.0f, -700.0f, 57.0f)),
                    new StopDef("La Mesa", new Vector3(800.0f, -1300.0f, 26.0f)),
                    new StopDef("Grove Street", new Vector3(-20.0f, -1660.0f, 29.0f)),
                    new StopDef("Legion Square", new Vector3(215.7f, -810.2f, 30.7f)))),

            ["taxi"] = new ActivityDef("taxi", "Taxi fares", "taxi", new Vector3(895f, -179f, 74f), 250, 22, 180,
                new RouteDef("Legion Square to LSIA",
                    new StopDef("Pick up the fare", new Vector3(215.7f, -810.2f, 30.7f)),
                    new StopDef("Drop off at LSIA", new Vector3(-1037.8f, -2737.8f, 20.2f))),
                new RouteDef("Vinewood to Del Perro",
                    new StopDef("Pick up the fare", new Vector3(293.0f, 180.0f, 104.0f)),
                    new StopDef("Drop off at Del Perro", new Vector3(-1850.0f, -1231.0f, 13.0f))),
                new RouteDef("Sandy to Paleto",
                    new StopDef("Pick up the fare", new Vector3(1699.0f, 3599.0f, 35.4f)),
                    new StopDef("Drop off in Paleto Bay", new Vector3(-275.0f, 6635.0f, 7.5f)))),
        };

        const long CooldownMs = 700;
        const float HubRange = 18.0f;
        const float ClaimRadius = 12.0f;
        const double MaxTravelSpeed = 65.0; // m/s: faster than any normal vehicle, so only teleports fail
        const long CompleteCooldownMs = 30000;
        const long AbandonCooldownMs = 60000;
        const string HubFile = "workhubs.txt";

        readonly Dictionary<string, JobState> _state = new Dictionary<string, JobState>(); // license -> state (online players only)
        readonly Dictionary<string, RunState> _runs = new Dictionary<string, RunState>();   // license -> active activity
        readonly Dictionary<string, long> _cooldown = new Dictionary<string, long>();       // license -> next allowed start
        readonly Random _rng = new Random();
        readonly int _payMinutes;
        readonly bool _requireVehicle;

        public JobsServer()
        {
            ChatPrefix = "Jobs";
            ChatColour = new[] { 120, 200, 255 };
            _payMinutes = Math.Max(1, GetConvarInt("jobs_pay_interval_minutes", 15));
            _requireVehicle = GetConvar("jobs_require_vehicle", "1") != "0";

            LoadHubs();

            Cmd("jobs", ListJobs);
            Cmd("job", ShowJob);
            Cmd("duty", ToggleDuty);
            Cmd("quitjob", QuitJob);
            Cmd("setjob", SetJob);
            Cmd("setworkhub", SetWorkHub);
            Cmd("work", WorkCommand);

            // Other resources can read a player's job (cache only, so it's synchronous and cheap).
            Exports.Add("getJob", new Func<int, string>(src => JobOf(src)?.Job));
            Exports.Add("getGrade", new Func<int, int>(src => JobOf(src)?.Grade ?? 0));
            Exports.Add("isOnDuty", new Func<int, string, bool>((src, job) =>
            {
                var s = JobOf(src);
                return s != null && s.OnDuty && s.Job == job;
            }));

            CreateTable();
            Tick += PaycheckLoop;
            Tick += ActivityLoop;
        }

        void CreateTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS player_jobs (license VARCHAR(64) PRIMARY KEY, job VARCHAR(32) NOT NULL DEFAULT 'unemployed', " +
                   "grade INT NOT NULL DEFAULT 0, on_duty TINYINT NOT NULL DEFAULT 0, " +
                   "updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)", new object[0], _ =>
                    // Nobody is online after a restart, so nobody can still be "on duty".
                    Db("UPDATE player_jobs SET on_duty = 0 WHERE on_duty = 1", new object[0],
                       n => Debug.WriteLine($"[jobs] ready, paychecks every {_payMinutes} min ({n} stale duty flag(s) cleared)")));

                Db("CREATE TABLE IF NOT EXISTS job_runs (id INT AUTO_INCREMENT PRIMARY KEY, license VARCHAR(64) NOT NULL, " +
                   "job VARCHAR(32) NOT NULL, activity VARCHAR(32) NOT NULL, route VARCHAR(64) NOT NULL, stops INT NOT NULL, " +
                   "payout INT NOT NULL, seconds INT NOT NULL, status VARCHAR(16) NOT NULL, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX idx_license (license))", new object[0],
                   _ => Debug.WriteLine("[jobs] activity history table ready"));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[jobs] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ---------- paychecks ----------

        async Task PaycheckLoop()
        {
            await Delay(_payMinutes * 60000);

            foreach (var p in Players.ToList())
            {
                var player = p;
                With(player, s =>
                {
                    if (!s.OnDuty) return;
                    int salary = SalaryOf(s);
                    if (salary <= 0) return;

                    string label = Jobs[s.Job].Label;
                    try
                    {
                        // The bank validates amount and player again; we never touch balances ourselves.
                        bool ok = Exports["bank-cs"].addBank(int.Parse(player.Handle), salary, $"{label} paycheck");
                        if (!ok) Debug.WriteLine($"[jobs] paycheck for {player.Name} was rejected by the bank");
                        else Debug.WriteLine($"[jobs] paid {player.Name} ${salary} ({s.Job}/{s.Grade})");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("[jobs] bank-cs export unavailable: " + e.Message);
                    }
                });
            }
        }

        // ---------- job activities ----------

        async Task ActivityLoop()
        {
            await Delay(1000);
            long now = GetGameTimer();
            foreach (var kv in _runs.ToList())
                if (now - kv.Value.StartedAt > kv.Value.TimeLimit * 1000L)
                    EndRun(kv.Key, "expired", "The job timed out.");
        }

        void WorkCommand(Player p, List<object> args)
        {
            string sub = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;
            if (sub == "stop") { StopWork(p); return; }
            if (sub == "stats") { ShowStats(p); return; }
            StartWork(p);
        }

        void StartWork(Player p)
        {
            With(p, s =>
            {
                string license = License(p);
                if (license == null) return;

                if (_runs.TryGetValue(license, out var active)) { ShowProgress(p, active); return; }
                if (_cooldown.TryGetValue(license, out long until) && GetGameTimer() < until)
                {
                    Say(p, $"You need a short break before starting another job ({(until - GetGameTimer()) / 1000 + 1}s).");
                    return;
                }

                if (!s.OnDuty) { Say(p, "Clock in with /duty before starting work."); return; }

                ActivityDef def;
                if (!Activities.TryGetValue(s.Job, out def))
                {
                    Say(p, "Your job has no active work. /jobs lists what is available.");
                    return;
                }

                if (_requireVehicle && !InVehicle(p)) { Say(p, "Get in a vehicle before starting a shift."); return; }

                int ped = GetPlayerPed(p.Handle);
                if (ped == 0) return;
                Vector3 pos = GetEntityCoords(ped);
                if (Dist2D(pos, def.Hub) > HubRange)
                {
                    Say(p, $"Go to the {def.Label} start point (marked on your map) to begin.");
                    return;
                }

                RouteDef route = def.Routes[_rng.Next(def.Routes.Length)];
                double meters = Dist2D(def.Hub, route.Stops[0].Pos);
                for (int i = 1; i < route.Stops.Length; i++)
                    meters += Dist2D(route.Stops[i - 1].Pos, route.Stops[i].Pos);

                int payout = def.BasePay + (int)Math.Round(meters / 1000.0 * def.PayPerKm);
                payout += payout * s.Grade / 10; // grade bonus, server-owned
                int limit = def.MinSeconds + (int)(meters / 15.0);

                long now = GetGameTimer();
                var run = new RunState
                {
                    License = license,
                    Src = int.Parse(p.Handle),
                    Activity = def,
                    Route = route,
                    Index = 0,
                    Payout = payout,
                    TimeLimit = limit,
                    StartedAt = now,
                    LastClaimAt = now,
                    LastAttemptAt = now,
                    LastPos = def.Hub,
                };
                _runs[license] = run;

                TriggerClientEvent(p, "jobs:activity", StartPayload(run));
                Say(p, $"{def.Label}: {route.Stops.Length} stop(s), {meters / 1000.0:0.0} km, ${payout} on completion. You have {limit / 60} min.");
                Debug.WriteLine($"[jobs] {p.Name} started {def.Id}/{route.Name} ({meters / 1000.0:0.0} km, ${payout}, {limit}s)");
            });
        }

        [EventHandler("jobs:activityCheckpoint")]
        void OnCheckpoint([FromSource] Player p)
        {
            if (p == null) return;
            string license = License(p);
            if (license == null) return;
            if (!_runs.TryGetValue(license, out var run)) return;

            long now = GetGameTimer();
            if (now - run.LastAttemptAt < CooldownMs) return; // the client cannot spam claims
            run.LastAttemptAt = now;

            if (run.Index < 0 || run.Index >= run.Route.Stops.Length)
            {
                EndRun(license, "cancelled", "That job is already over.");
                return;
            }
            if (now - run.StartedAt > run.TimeLimit * 1000L)
            {
                EndRun(license, "expired", "The job timed out.");
                return;
            }

            StopDef stop = run.Route.Stops[run.Index];
            int ped = GetPlayerPed(p.Handle);
            if (ped == 0 || GetEntityHealth(ped) <= 0) { Error(p, "You can't finish a job while down."); return; }
            if (_requireVehicle && GetVehiclePedIsIn(ped, false) == 0) { Error(p, "Get in a vehicle first."); return; }

            Vector3 pos = GetEntityCoords(ped);
            if (Dist2D(pos, stop.Pos) > ClaimRadius) { Error(p, "You are not at the stop yet."); return; }

            // Reject a claim that moved faster than any vehicle could have. The tolerance covers
            // network jitter and the fact that checkpoints are measured centre-to-centre.
            double minSeconds = Dist2D(run.LastPos, stop.Pos) / MaxTravelSpeed;
            if ((now - run.LastClaimAt) / 1000.0 + 2.0 < minSeconds)
            {
                Error(p, "That was too fast. Drive the route to the next stop.");
                return;
            }

            run.LastClaimAt = now;
            run.LastPos = stop.Pos;
            run.Index++;

            if (run.Index >= run.Route.Stops.Length) { CompleteRun(license, run); return; }

            TriggerClientEvent(p, "jobs:activity", "next|" + run.Index);
            Say(p, $"Stop {run.Index}/{run.Route.Stops.Length} reached. The next one is marked.");
        }

        void CompleteRun(string license, RunState run)
        {
            long now = GetGameTimer();
            run.Seconds = (int)((now - run.StartedAt) / 1000);
            _runs.Remove(license);
            _cooldown[license] = now + CompleteCooldownMs;

            bool paid = false;
            try
            {
                paid = (bool)Exports["bank-cs"].addMoney(run.Src, "cash", run.Payout, $"job: {run.Route.Name}");
            }
            catch (Exception e)
            {
                Debug.WriteLine("[jobs] activity payout failed, bank-cs unavailable: " + e.Message);
            }

            LogRun(license, run, "completed", run.Payout);

            Player p = Players[run.Src];
            if (p != null)
            {
                TriggerClientEvent(p, "jobs:activity", "end|completed|" + run.Payout);
                Say(p, paid
                    ? $"Job complete. ${run.Payout} was paid in cash."
                    : $"Job complete, but the bank could not pay ${run.Payout} right now. It has been logged.");
            }
            Debug.WriteLine($"[jobs] {license} completed {run.Activity.Id}/{run.Route.Name} in {run.Seconds}s for ${run.Payout} (paid={paid})");
        }

        void StopWork(Player p)
        {
            string license = License(p);
            if (license == null) return;
            if (!_runs.ContainsKey(license)) { Say(p, "You are not on a job."); return; }
            EndRun(license, "cancelled", "You stopped the job.");
        }

        void EndRun(string license, string status, string message)
        {
            if (!_runs.TryGetValue(license, out var run)) return;
            _runs.Remove(license);

            long now = GetGameTimer();
            run.Seconds = (int)((now - run.StartedAt) / 1000);
            _cooldown[license] = now + AbandonCooldownMs;
            LogRun(license, run, status, 0);

            Player p = Players[run.Src];
            if (p != null)
            {
                TriggerClientEvent(p, "jobs:activity", "end|" + status + "|0");
                if (!string.IsNullOrEmpty(message)) Say(p, message);
            }
        }

        void ShowProgress(Player p, RunState run)
        {
            int left = Math.Max(0, run.TimeLimit - (int)((GetGameTimer() - run.StartedAt) / 1000));
            Say(p, $"{run.Activity.Label}: stop {run.Index + 1}/{run.Route.Stops.Length}, " +
                          $"{left / 60}:{left % 60:00} left, ${run.Payout} on completion. /work stop cancels.");
        }

        void ShowStats(Player p)
        {
            string license = License(p);
            if (license == null) return;
            if (_runs.TryGetValue(license, out var run)) ShowProgress(p, run);

            Query("SELECT COUNT(*) AS runs, COALESCE(SUM(payout), 0) AS earned FROM job_runs " +
                        "WHERE license = ? AND status = 'completed'", new object[] { license }, rows =>
            {
                int runs = rows.Count > 0 ? Convert.ToInt32(rows[0]["runs"]) : 0;
                long earned = rows.Count > 0 ? Convert.ToInt64(rows[0]["earned"]) : 0;
                Say(p, $"Completed {runs} job(s), earned ${earned.ToString("N0", CultureInfo.InvariantCulture)} in total.");
            });
        }

        void Error(Player p, string message)
        {
            TriggerClientEvent(p, "jobs:activityError", message);
            Say(p, message);
        }

        string StartPayload(RunState run)
        {
            var sb = new StringBuilder();
            sb.Append("start|").Append(run.Activity.Label).Append('|').Append(run.Payout).Append('|').Append(run.TimeLimit);
            foreach (var stop in run.Route.Stops)
                sb.Append('|').Append(stop.Label).Append('|').Append(F(stop.Pos.X))
                  .Append('|').Append(F(stop.Pos.Y)).Append('|').Append(F(stop.Pos.Z));
            return sb.ToString();
        }

        // ---------- work hubs (placed in-game, saved to workhubs.txt) ----------

        [EventHandler("jobs:requestHubs")]
        void OnRequestHubs([FromSource] Player p)
        {
            if (p != null) TriggerClientEvent(p, "jobs:hubs", HubsPayload());
        }

        string HubsPayload() =>
            string.Join(";", Activities.Values.Select(a =>
                string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2:F2}|{3:F2}|{4:F2}", a.Id, a.Label, a.Hub.X, a.Hub.Y, a.Hub.Z)));

        void SetWorkHub(Player p, List<object> args)
        {
            if (!IsPlayerAceAllowed(p.Handle, "jobs.admin")) { Say(p, "You are not allowed to use that."); return; }

            string id = args.Count > 0 ? args[0]?.ToString().ToLowerInvariant() : null;
            if (id == null || !Activities.ContainsKey(id))
            {
                Say(p, "Usage: /setworkhub <" + string.Join("|", Activities.Keys) + ">  (stand where the hub should be)");
                return;
            }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            Activities[id].Hub = GetEntityCoords(ped);
            SaveHubs();
            BroadcastHubs();
            Say(p, $"{Activities[id].Label} start point moved here.");
        }

        void LoadHubs()
        {
            string raw = LoadResourceFile(GetCurrentResourceName(), HubFile);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                float x, y, z;
                if (parts.Length != 4 || !Activities.ContainsKey(parts[0])
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                Activities[parts[0]].Hub = new Vector3(x, y, z);
            }
        }

        void SaveHubs()
        {
            var lines = Activities.Values.Select(a =>
                string.Format(CultureInfo.InvariantCulture, "{0},{1:F2},{2:F2},{3:F2}", a.Id, a.Hub.X, a.Hub.Y, a.Hub.Z));
            SaveResourceFile(GetCurrentResourceName(), HubFile, string.Join("\n", lines), -1);
        }

        void BroadcastHubs() => TriggerClientEvent("jobs:hubs", HubsPayload());

        // ---------- commands ----------

        void ListJobs(Player p, List<object> args)
        {
            Say(p, "Available jobs:");
            foreach (var kv in Jobs.Where(j => j.Key != "unemployed"))
                Say(p, $"{kv.Key}: " + string.Join(", ", kv.Value.Grades.Select((g, i) => $"{i}={g.Name} (${g.Salary})")));
            Say(p, "Active work: /work to start, /work stop to cancel, /work stats for totals.");
        }

        void ShowJob(Player p, List<object> args) => With(p, s =>
        {
            var g = GradeOf(s);
            Say(p, s.Job == "unemployed"
                ? "You are unemployed."
                : $"{Jobs[s.Job].Label} - {g.Name} (${g.Salary}/paycheck), {(s.OnDuty ? "ON duty" : "off duty")}");
        });

        void ToggleDuty(Player p, List<object> args) => With(p, s =>
        {
            if (s.Job == "unemployed") { Say(p, "You don't have a job."); return; }

            string license = License(p);
            if (license == null) return;
            bool next = !s.OnDuty;
            Db("UPDATE player_jobs SET on_duty = ? WHERE license = ?", new object[] { next ? 1 : 0, license }, _ =>
            {
                s.OnDuty = next;
                if (!next) EndRun(license, "cancelled", "You went off duty, so the job was cancelled.");
                Say(p, next ? "You are now ON duty. Paychecks are active." : "You are now off duty.");
                // Other resources (dispatch) listen for this so they can drop the player's calls.
                TriggerEvent("jobs:dutyChanged", int.Parse(p.Handle), s.Job, next);
            });
        });

        void QuitJob(Player p, List<object> args) => Assign(p, "unemployed", 0, silentFor: null);

        void SetJob(Player p, List<object> args)
        {
            // Dev permission. A real server would tie this to its admin roles (see admin-cs).
            if (!IsPlayerAceAllowed(p.Handle, "jobs.admin")) { Say(p, "You are not allowed to use that."); return; }

            if (args.Count < 2 || !int.TryParse(args[0]?.ToString(), out int id)) { Say(p, "Usage: /setjob <player id> <job> [grade]"); return; }
            string job = args[1]?.ToString().ToLowerInvariant();
            int grade = 0;
            if (args.Count > 2 && !int.TryParse(args[2]?.ToString(), out grade)) { Say(p, "Grade must be a number."); return; }

            var target = Players[id];
            if (target == null) { Say(p, "That player is not online."); return; }

            Assign(target, job, grade, silentFor: p);
        }

        // Validates against the definitions, then upserts. Resets duty so nobody keeps being paid
        // for a role they no longer hold, and cancels any activity that belonged to the old job.
        void Assign(Player target, string job, int grade, Player silentFor)
        {
            var reply = silentFor ?? target;
            if (job == null || !Jobs.ContainsKey(job)) { Say(reply, "Unknown job. See /jobs."); return; }
            if (grade < 0 || grade >= Jobs[job].Grades.Length) { Say(reply, $"Grade must be 0-{Jobs[job].Grades.Length - 1} for {job}."); return; }

            string license = License(target);
            if (license == null) return;

            Db("INSERT INTO player_jobs (license, job, grade, on_duty) VALUES (?, ?, ?, 0) " +
               "ON DUPLICATE KEY UPDATE job = VALUES(job), grade = VALUES(grade), on_duty = 0",
               new object[] { license, job, grade }, _ =>
               {
                   _state[license] = new JobState { Job = job, Grade = grade, OnDuty = false };
                   EndRun(license, "cancelled", "Your job changed, so the job was cancelled.");
                   string text = job == "unemployed" ? "You are now unemployed." : $"Your job is now {Jobs[job].Label} - {Jobs[job].Grades[grade].Name}. Use /duty to clock in.";
                   Say(target, text);
                   if (silentFor != null) Say(silentFor, $"{target.Name}: {job}/{grade} set.");
                   Debug.WriteLine($"[jobs] {target.Name} ({license}) -> {job}/{grade}");
                   TriggerEvent("jobs:jobChanged", int.Parse(target.Handle), job);
               });
        }

        // ---------- state ----------

        // Loads the player's row once, caches it for as long as they're online, then runs the action.
        void With(Player p, Action<JobState> action)
        {
            string license = License(p);
            if (license == null) return;
            if (_state.TryGetValue(license, out var cached)) { action(cached); return; }

            Query("SELECT job, grade, on_duty FROM player_jobs WHERE license = ?", new object[] { license }, rows =>
            {
                JobState s;
                if (rows.Count == 0) s = new JobState();
                else
                {
                    s = new JobState { Job = rows[0]["job"].ToString(), Grade = Convert.ToInt32(rows[0]["grade"]), OnDuty = Convert.ToInt32(rows[0]["on_duty"]) == 1 };
                    // A job removed from the config must not crash or keep paying.
                    if (!Jobs.ContainsKey(s.Job) || s.Grade < 0 || s.Grade >= Jobs[s.Job].Grades.Length) s = new JobState();
                }
                _state[license] = s;
                action(s);
            });
        }

        JobState JobOf(int src)
        {
            var p = Players[src];
            string license = License(p);
            return license != null && _state.TryGetValue(license, out var s) ? s : null;
        }

        static Grade GradeOf(JobState s) => Jobs[s.Job].Grades[s.Grade];
        static int SalaryOf(JobState s) => Jobs.ContainsKey(s.Job) ? GradeOf(s).Salary : 0;

        // Load the row as soon as the player joins so exports like dispatch's eligibility check
        // (isOnDuty) are accurate without the player having to run a jobs command first.
        [EventHandler("playerJoining")]
        void OnPlayerJoining([FromSource] Player p, string oldId)
        {
            if (p != null) With(p, _ => { });
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            int src = int.Parse(p.Handle);
            Forget(src);

            string license = License(p);
            if (license == null) return;

            if (_runs.ContainsKey(license)) EndRun(license, "abandoned", null);
            if (_state.TryGetValue(license, out var s) && s.OnDuty)
                Db("UPDATE player_jobs SET on_duty = 0 WHERE license = ?", new object[] { license }, _ => { });
            _state.Remove(license);
            _cooldown.Remove(license);
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            long now = GetGameTimer();
            foreach (var kv in _runs.ToList())
            {
                kv.Value.Seconds = (int)((now - kv.Value.StartedAt) / 1000);
                LogRun(kv.Key, kv.Value, "abandoned", 0);
            }
            _runs.Clear();
        }

        void LogRun(string license, RunState run, string status, int payout)
        {
            Db("INSERT INTO job_runs (license, job, activity, route, stops, payout, seconds, status) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                new object[] { license, run.Activity.Job, run.Activity.Id, run.Route.Name, run.Route.Stops.Length, payout, run.Seconds, status },
                _ => { });
        }

        // ---------- helpers ----------

        static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

        static double Dist2D(Vector3 a, Vector3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[jobs] /{name} is in-game only"); return; }
                var p = Players[src];
                if (p == null || RateLimited(src, CooldownMs)) return;
                handler(p, args);
            }), false);
        }
    }
}
