using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
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

    /// <summary>
    /// Jobs with grades, a duty toggle and periodic paychecks. Salaries live in code (the server is the
    /// only authority) and are paid into bank-cs through its server-only addBank export.
    /// </summary>
    public class JobsServer : BaseScript
    {
        // The definitions are the source of truth: the database only stores job name + grade index.
        static readonly Dictionary<string, JobDef> Jobs = new Dictionary<string, JobDef>
        {
            ["unemployed"] = new JobDef("Unemployed", new Grade("Citizen", 0)),
            ["police"] = new JobDef("Los Santos Police", new Grade("Cadet", 150), new Grade("Officer", 250), new Grade("Sergeant", 350)),
            ["ems"] = new JobDef("Emergency Medical Services", new Grade("Trainee", 140), new Grade("Paramedic", 260), new Grade("Chief", 380)),
            ["mechanic"] = new JobDef("Mechanic", new Grade("Apprentice", 120), new Grade("Mechanic", 200)),
            ["taxi"] = new JobDef("Downtown Cab Co.", new Grade("Driver", 110)),
        };

        const long CooldownMs = 700;

        readonly Dictionary<string, JobState> _state = new Dictionary<string, JobState>(); // license -> state (online players only)
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();
        readonly int _payMinutes;

        public JobsServer()
        {
            _payMinutes = Math.Max(1, GetConvarInt("jobs_pay_interval_minutes", 15));

            Cmd("jobs", ListJobs);
            Cmd("job", ShowJob);
            Cmd("duty", ToggleDuty);
            Cmd("quitjob", QuitJob);
            Cmd("setjob", SetJob);

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

        // ---------- commands ----------

        void ListJobs(Player p, List<object> args)
        {
            Say(p, "Available jobs:");
            foreach (var kv in Jobs.Where(j => j.Key != "unemployed"))
                Say(p, $"{kv.Key}: " + string.Join(", ", kv.Value.Grades.Select((g, i) => $"{i}={g.Name} (${g.Salary})")));
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

            bool next = !s.OnDuty;
            Db("UPDATE player_jobs SET on_duty = ? WHERE license = ?", new object[] { next ? 1 : 0, License(p) }, _ =>
            {
                s.OnDuty = next;
                Say(p, next ? "You are now ON duty. Paychecks are active." : "You are now off duty.");
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
        // for a role they no longer hold.
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
                   string text = job == "unemployed" ? "You are now unemployed." : $"Your job is now {Jobs[job].Label} - {Jobs[job].Grades[grade].Name}. Use /duty to clock in.";
                   Say(target, text);
                   if (silentFor != null) Say(silentFor, $"{target.Name}: {job}/{grade} set.");
                   Debug.WriteLine($"[jobs] {target.Name} ({license}) -> {job}/{grade}");
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
            string license = p == null ? null : License(p);
            return license != null && _state.TryGetValue(license, out var s) ? s : null;
        }

        static Grade GradeOf(JobState s) => Jobs[s.Job].Grades[s.Grade];
        static int SalaryOf(JobState s) => Jobs.ContainsKey(s.Job) ? GradeOf(s).Salary : 0;

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;
            _lastUse.Remove(int.Parse(p.Handle));
            string license = License(p);
            if (license == null) return;

            if (_state.TryGetValue(license, out var s) && s.OnDuty)
                Db("UPDATE player_jobs SET on_duty = 0 WHERE license = ?", new object[] { license }, _ => { });
            _state.Remove(license);
        }

        // ---------- helpers ----------

        static string License(Player p)
        {
            string id = p.Identifiers["license"];
            return string.IsNullOrEmpty(id) ? null : id;
        }

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[jobs] /{name} is in-game only"); return; }
                var p = Players[src];
                if (p == null || RateLimited(src)) return;
                handler(p, args);
            }), false);
        }

        bool RateLimited(int src)
        {
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < CooldownMs) return true;
            _lastUse[src] = now;
            return false;
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
            TriggerClientEvent(p, "chat:addMessage", new { color = new[] { 120, 200, 255 }, args = new[] { "Jobs", msg } });
    }
}
