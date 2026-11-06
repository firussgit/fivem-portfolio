using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using Portfolio.Core;
using static CitizenFX.Core.Native.API;

namespace Dispatch.Server
{
    /// <summary>A named part of the map, used to describe a call without trusting the client.</summary>
    class AreaDef
    {
        public string Name;
        public Vector3 Pos;

        public AreaDef(string name, float x, float y)
        {
            Name = name;
            Pos = new Vector3(x, y, 0f);
        }
    }

    /// <summary>One active call. Once closed it is only a database row.</summary>
    class CallState
    {
        public int Id;
        public string CallerLicense;
        public string CallerName;
        public bool Synthetic;
        public string Category;      // police | medical
        public string Message;
        public Vector3 Pos;
        public string Area;
        public string Status = "open";
        public string ClaimedBy;
        public string ClaimedName;
        public long CreatedAt;
        public long ClaimedAt;
        public Vector3 ClaimPos;
    }

    /// <summary>
    /// Dispatch radio for police and EMS.
    ///
    /// A call is created by a 911/912 command or by the phone (through the createCall export).
    /// The server records the caller's real position (OneSync) and the area it maps to, then offers
    /// the call only to players who are on duty in the matching job. Claiming is a single atomic
    /// UPDATE, and "on scene" / "resolved" are checked against the server's own position, so a
    /// modified client cannot respond to a call from across the map.
    /// </summary>
    public class DispatchServer : PortfolioScript
    {
        const string Police = "police";
        const string Medical = "medical";
        const int MaxMessage = 160;
        const int MaxActiveCalls = 30;
        const double MaxTravelSpeed = 65.0; // m/s: faster than any normal vehicle, so only teleports fail

        // Fixed server-side landmarks. The client never supplies the location text.
        static readonly AreaDef[] Areas =
        {
            new AreaDef("Legion Square", 215.7f, -810.2f),
            new AreaDef("Mission Row", 425.1f, -979.6f),
            new AreaDef("Pillbox Hill", 307.7f, -595.3f),
            new AreaDef("Downtown Vinewood", 293.0f, 180.0f),
            new AreaDef("Rockford Hills", -550.0f, -170.0f),
            new AreaDef("Del Perro", -1850.0f, -1231.0f),
            new AreaDef("Vespucci Beach", -1200.0f, -1500.0f),
            new AreaDef("LS International", -1037.8f, -2737.8f),
            new AreaDef("Mirror Park", 1050.0f, -700.0f),
            new AreaDef("La Mesa", 800.0f, -1300.0f),
            new AreaDef("Grove Street", -20.0f, -1660.0f),
            new AreaDef("Elysian Island", 100.0f, -3000.0f),
            new AreaDef("Terminal", -250.0f, -2700.0f),
            new AreaDef("Vinewood Hills", 500.0f, 600.0f),
            new AreaDef("Chumash", -3200.0f, 1000.0f),
            new AreaDef("Sandy Shores", 1699.0f, 3599.0f),
            new AreaDef("Grapeseed", 1690.0f, 4800.0f),
            new AreaDef("Paleto Bay", -275.0f, 6635.0f),
            new AreaDef("Fort Zancudo", -2200.0f, 3200.0f),
        };

        readonly Dictionary<int, CallState> _calls = new Dictionary<int, CallState>();
        readonly Dictionary<string, long> _lastCall = new Dictionary<string, long>();
        readonly HashSet<string> _calling = new HashSet<string>();
        readonly HashSet<string> _cancelPending = new HashSet<string>(); // cancelled while the insert was in flight

        readonly int _pay;
        readonly float _sceneRadius;
        readonly int _ttlMinutes;
        readonly long _callCooldownMs;

        public DispatchServer()
        {
            ChatPrefix = "Dispatch";
            ChatColour = new[] { 255, 150, 70 };

            _pay = Math.Max(0, Math.Min(100000, GetConvarInt("dispatch_pay_per_call", 150)));
            _sceneRadius = Math.Max(5, GetConvarInt("dispatch_scene_radius", 40));
            _ttlMinutes = Math.Max(1, GetConvarInt("dispatch_call_ttl_minutes", 15));
            _callCooldownMs = Math.Max(5, GetConvarInt("dispatch_call_cooldown_seconds", 20)) * 1000L;

            // Commands are a fallback for players without the phone UI (and handy for testing).
            Cmd("911", (p, a) => CreateCall(Src(p), Police, string.Join(" ", a.Select(x => x?.ToString()))));
            Cmd("912", (p, a) => CreateCall(Src(p), Medical, string.Join(" ", a.Select(x => x?.ToString()))));
            Cmd("cancelcall", (p, a) => CancelCall(Src(p)));
            Cmd("dispatchtest", DispatchTest);
            Cmd("dispatchclose", DispatchClose);

            // Server-only exports: phone-cs creates and cancels calls through these, never a client event.
            Exports.Add("createCall", new Func<int, string, string, bool>(CreateCall));
            Exports.Add("cancelCall", new Func<int, bool>(CancelCall));
            Exports.Add("resolveFromAction", new Func<int, string, string, bool>(ResolveFromAction));

            CreateTable();
            Tick += ExpireLoop;

            Debug.WriteLine("[dispatch] radio ready");
        }

        void CreateTable()
        {
            try
            {
                Db("CREATE TABLE IF NOT EXISTS dispatch_calls (" +
                   "id INT AUTO_INCREMENT PRIMARY KEY, " +
                   "caller_license VARCHAR(64) NOT NULL, " +
                   "caller_name VARCHAR(64) NOT NULL, " +
                   "category VARCHAR(16) NOT NULL, " +
                   "message VARCHAR(160) NOT NULL, " +
                   "x DOUBLE NOT NULL, y DOUBLE NOT NULL, z DOUBLE NOT NULL, " +
                   "area VARCHAR(64) NOT NULL, " +
                   "status VARCHAR(16) NOT NULL DEFAULT 'open', " +
                   "claimed_by VARCHAR(64) NULL, " +
                   "claimed_name VARCHAR(64) NULL, " +
                   "claimed_at DATETIME NULL, " +
                   "outcome VARCHAR(24) NULL, " +
                   "payout INT NOT NULL DEFAULT 0, " +
                   "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                   "closed_at DATETIME NULL, " +
                   "INDEX idx_status (status), INDEX idx_caller (caller_license), INDEX idx_claimed (claimed_by))", new object[0], _ =>
                    // Nobody can be responding after a restart, so stale calls become history.
                    Db("UPDATE dispatch_calls SET status = 'closed', outcome = 'expired', closed_at = NOW() WHERE status <> 'closed'",
                       new object[0], n => Debug.WriteLine($"[dispatch] ready ({n} stale call(s) closed)")));
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dispatch] database unavailable (is oxmysql started?): " + e.Message);
            }
        }

        // ==================================================================
        //  Calls
        // ==================================================================

        bool CreateCall(int src, string category, string message)
        {
            var p = Players[src];
            if (p == null) return false;

            string license = License(p);
            if (license == null) return false;

            category = NormalizeCategory(category);
            message = Clean(message, MaxMessage);
            if (category == null) { Say(p, "Unknown call type."); return false; }
            if (message.Length < 4) { Say(p, "Describe the emergency in a few words."); return false; }

            long now = GetGameTimer();
            if (_lastCall.TryGetValue(license, out long last) && now - last < _callCooldownMs)
            {
                Say(p, $"Wait {(int)((_callCooldownMs - (now - last)) / 1000) + 1}s before calling again.");
                return false;
            }
            if (_calling.Contains(license)) { Say(p, "Your call is already being placed."); return false; }

            var existing = _calls.Values.FirstOrDefault(c => c.CallerLicense == license);
            if (existing != null) { Say(p, $"You already have an open call (#{existing.Id})."); return false; }
            if (_calls.Count >= MaxActiveCalls) { Say(p, "Dispatch is busy right now. Try again shortly."); return false; }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return false;
            Vector3 pos = GetEntityCoords(ped);

            _lastCall[license] = now;
            _calling.Add(license);
            AddCall(p, license, Clean(p.Name, 60), category, message, pos, false);
            return true;
        }

        void AddCall(Player actor, string callerLicense, string callerName, string category, string message, Vector3 pos, bool synthetic)
        {
            string area = AreaOf(pos);

            try
            {
                Exports["oxmysql"].insert(
                    "INSERT INTO dispatch_calls (caller_license, caller_name, category, message, x, y, z, area) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    new object[] { callerLicense, callerName, category, message, pos.X, pos.Y, pos.Z, area },
                    new Action<dynamic>(rawId =>
                    {
                        _calling.Remove(callerLicense);

                        long id = rawId == null ? 0 : Convert.ToInt64(rawId);
                        if (id <= 0)
                        {
                            if (actor != null) Say(actor, "The call could not be logged.");
                            return;
                        }

                        var call = new CallState
                        {
                            Id = (int)id,
                            CallerLicense = callerLicense,
                            CallerName = callerName,
                            Synthetic = synthetic,
                            Category = category,
                            Message = message,
                            Pos = pos,
                            Area = area,
                            CreatedAt = GetGameTimer(),
                        };
                        _calls[call.Id] = call;

                        // Cancel arrived while this insert was in flight: close it now and never
                        // offer it to units.
                        if (_cancelPending.Remove(callerLicense))
                        {
                            Debug.WriteLine($"[dispatch] call #{call.Id} {category} cancelled before it was offered");
                            CloseSystem(call, "cancelled", 0, $"You cancelled call #{call.Id}.");
                            return;
                        }

                        if (actor != null) Say(actor, $"Call #{call.Id} received. Units have your location.");
                        NotifyNew(call);
                        BroadcastStates();

                        Debug.WriteLine($"[dispatch] call #{call.Id} {category} from {callerName} at {area}");
                    }));
            }
            catch (Exception e)
            {
                _calling.Remove(callerLicense);
                Debug.WriteLine("[dispatch] could not log call, oxmysql unavailable: " + e.Message);
                if (actor != null) Say(actor, "Dispatch is unavailable right now.");
            }
        }

        bool CancelCall(int src)
        {
            var p = Players[src];
            if (p == null) return false;

            string license = License(p);
            if (license == null) return false;

            var call = _calls.Values.FirstOrDefault(c => c.CallerLicense == license);
            if (call == null)
            {
                // The 911 request can still be waiting for its database insert, so the caller may
                // hit Cancel a moment before the call exists. Remember it and close the call the
                // instant it appears instead of leaving a phantom open call behind.
                if (_calling.Contains(license))
                {
                    _cancelPending.Add(license);
                    Say(p, "Cancelling your call\u2026");
                    return true;
                }
                Say(p, "You have no active call.");
                return false;
            }

            CloseSystem(call, "cancelled", 0, $"You cancelled call #{call.Id}.");
            return true;
        }

        // ==================================================================
        //  Field-action integration (jobs-cs)
        // ==================================================================

        /// <summary>
        /// Called by jobs-cs after it has validated a field action on the caller: a police fine or
        /// impound resolves the caller's police call, and an EMS revive resolves their medical call.
        /// The action already checked job, duty and proximity to the target, so this only has to
        /// match the caller's active call and make sure another unit isn't handling it. The actual
        /// close is deferred by a tick so dispatch can safely call back into jobs-cs for the duty
        /// check and the console broadcast. A server-only export, so a client can never call it.
        /// </summary>
        bool ResolveFromAction(int actorSrc, string callerLicense, string category)
        {
            var actor = Players[actorSrc];
            if (actor == null) return false;

            category = NormalizeCategory(category);
            if (category == null || string.IsNullOrEmpty(callerLicense)) return false;

            string actorLicense = License(actor);
            if (actorLicense == null) return false;

            var call = _calls.Values.FirstOrDefault(c =>
                c.CallerLicense == callerLicense && c.Category == category);
            if (call == null) return false;

            // Another unit is handling it: don't close their call or collect their payout.
            if (call.ClaimedBy != null && call.ClaimedBy != actorLicense) return false;

            _ = ResolveFromActionDeferred(call, actorSrc, actorLicense, Clean(actor.Name, 60), category);
            return true;
        }

        async Task ResolveFromActionDeferred(CallState call, int actorSrc, string actorLicense, string actorName, string category)
        {
            try
            {
                // Let the caller's export call return before dispatch calls jobs-cs again.
                await Delay(0);

                if (!_calls.ContainsKey(call.Id)) return;
                if (call.ClaimedBy != null && call.ClaimedBy != actorLicense) return;

                // Defence in depth: the action already proved job and duty, but only the matching
                // on-duty department may resolve the call.
                if (!OnDuty(actorSrc, CategoryJob(category))) return;

                bool autoClaim = call.ClaimedBy == null;
                string callerMessage = category == Police
                    ? $"Your call #{call.Id} was resolved: an officer dealt with the offender."
                    : $"Your call #{call.Id} was resolved: EMS treated you.";

                // One atomic update: an unclaimed call is taken and closed in the same step, and the
                // WHERE clause makes sure another unit cannot claim it between the read and this write.
                Db("UPDATE dispatch_calls SET status = 'closed', outcome = 'resolved', closed_at = NOW(), payout = ?, " +
                   "claimed_by = COALESCE(claimed_by, ?), claimed_name = COALESCE(claimed_name, ?), " +
                   "claimed_at = COALESCE(claimed_at, NOW()) " +
                   "WHERE id = ? AND status <> 'closed' AND (claimed_by IS NULL OR claimed_by = ?)",
                   new object[] { _pay, actorLicense, actorName, call.Id, actorLicense }, affected =>
                   {
                       if (affected != 1) return;

                       if (autoClaim)
                       {
                           call.ClaimedBy = actorLicense;
                           call.ClaimedName = actorName;
                           call.ClaimedAt = GetGameTimer();
                           call.ClaimPos = call.Pos;
                       }

                       call.Status = "closed";
                       FinalizeClose(call, "resolved", _pay, callerMessage);

                       // FinalizeClose already tells the unit when a zero-payout call closes.
                       var actor = _pay > 0 ? FindPlayer(actorLicense) : null;
                       if (actor != null) Say(actor, $"Call #{call.Id} resolved by your field action.");
                   });
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dispatch] field-action resolve failed: " + e.Message);
            }
        }

        // ==================================================================
        //  Unit actions
        // ==================================================================

        [EventHandler("dispatch:request")]
        void OnRequest([FromSource] Player p)
        {
            if (p == null || RateLimited(Src(p), 500)) return;
            SendState(p);
        }

        [EventHandler("dispatch:claim")]
        void OnClaim([FromSource] Player p, int id)
        {
            if (p == null || RateLimited(Src(p), 400)) return;

            string license = License(p);
            if (license == null) return;

            if (!_calls.TryGetValue(id, out var call)) { Say(p, "That call is no longer active."); SendState(p); return; }
            if (call.Status != "open") { Say(p, "That call has already been taken."); SendState(p); return; }
            if (call.CallerLicense == license) { Say(p, "You can't respond to your own call."); return; }

            if (!CanClaim(p, call))
            {
                Say(p, $"You must be on duty as {(call.Category == Police ? "police" : "EMS")} to respond.");
                return;
            }

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;
            Vector3 claimPos = GetEntityCoords(ped);

            // Atomic: only one unit can move the row out of 'open'.
            Db("UPDATE dispatch_calls SET status = 'claimed', claimed_by = ?, claimed_name = ?, claimed_at = NOW() " +
               "WHERE id = ? AND status = 'open'",
               new object[] { license, Clean(p.Name, 60), call.Id }, affected =>
               {
                   if (affected != 1)
                   {
                       Say(p, "That call was taken by another unit.");
                       SendState(p);
                       return;
                   }

                   call.Status = "claimed";
                   call.ClaimedBy = license;
                   call.ClaimedName = Clean(p.Name, 60);
                   call.ClaimedAt = GetGameTimer();
                   call.ClaimPos = claimPos;

                   TriggerClientEvent(p, "dispatch:assigned",
                       $"{call.Id}|{F(call.Pos.X)}|{F(call.Pos.Y)}|{F(call.Pos.Z)}|{call.Category}|{call.Area}");
                   Say(p, $"Call #{call.Id} assigned. A GPS route has been set.");

                   var caller = FindPlayer(call.CallerLicense);
                   if (caller != null) Say(caller, $"{call.ClaimedName} is responding to your call.");

                   BroadcastStates();
                   Debug.WriteLine($"[dispatch] call #{call.Id} claimed by {call.ClaimedName}");
               });
        }

        [EventHandler("dispatch:release")]
        void OnRelease([FromSource] Player p, int id)
        {
            if (p == null || RateLimited(Src(p), 400)) return;

            string license = License(p);
            if (license == null) return;

            if (!_calls.TryGetValue(id, out var call)) { Say(p, "That call is no longer active."); return; }
            if (call.ClaimedBy != license) { Say(p, "That call isn't assigned to you."); return; }

            ReleaseInternal(call, "You released the call.", "The unit released your call; it is open again.");
        }

        [EventHandler("dispatch:status")]
        void OnStatus([FromSource] Player p, int id, string status)
        {
            if (p == null || RateLimited(Src(p), 400)) return;

            string license = License(p);
            if (license == null) return;

            status = (status ?? "").Trim().ToLowerInvariant();
            if (status != "enroute" && status != "onscene") return;

            if (!_calls.TryGetValue(id, out var call)) { Say(p, "That call is no longer active."); return; }
            if (call.ClaimedBy != license) { Say(p, "That call isn't assigned to you."); return; }
            if (call.Status == status) return;

            if (status == "enroute" && call.Status != "claimed")
            {
                Say(p, "You are already past that stage.");
                return;
            }

            if (status == "onscene")
            {
                int ped = GetPlayerPed(p.Handle);
                if (ped == 0 || Dist2D(GetEntityCoords(ped), call.Pos) > _sceneRadius)
                {
                    Say(p, $"Get within {_sceneRadius:0} m of the call before marking on scene.");
                    return;
                }

                // Same time-based check as the job activities: the server knows where the unit
                // claimed the call, so it can tell whether they could physically be here yet.
                double minSeconds = Dist2D(call.ClaimPos, call.Pos) / MaxTravelSpeed;
                if (call.ClaimedAt > 0 && (GetGameTimer() - call.ClaimedAt) / 1000.0 + 2.0 < minSeconds)
                {
                    Say(p, "That was too fast. Drive to the call.");
                    return;
                }
            }

            Db("UPDATE dispatch_calls SET status = ? WHERE id = ? AND claimed_by = ? AND status <> 'closed'",
               new object[] { status, call.Id, license }, affected =>
               {
                   if (affected != 1) { Say(p, "That call is no longer yours."); return; }
                   call.Status = status;
                   Say(p, status == "enroute" ? "Marked en route." : "Marked on scene.");
                   BroadcastStates();
               });
        }

        [EventHandler("dispatch:close")]
        void OnCloseCall([FromSource] Player p, int id, string outcome)
        {
            if (p == null || RateLimited(Src(p), 400)) return;

            string license = License(p);
            if (license == null) return;

            if (!_calls.TryGetValue(id, out var call)) { Say(p, "That call is no longer active."); return; }
            if (call.ClaimedBy != license) { Say(p, "That call isn't assigned to you."); return; }

            outcome = NormalizeOutcome(outcome);
            if (outcome == null) { Say(p, "Choose a valid result."); return; }

            int payout = 0;
            if (outcome == "resolved")
            {
                if (call.Status != "onscene") { Say(p, "Mark yourself on scene before closing the call."); return; }

                int ped = GetPlayerPed(p.Handle);
                if (ped == 0 || Dist2D(GetEntityCoords(ped), call.Pos) > _sceneRadius)
                {
                    Say(p, "You must be at the call location to close it as resolved.");
                    return;
                }

                payout = _pay;
            }

            Db("UPDATE dispatch_calls SET status = 'closed', outcome = ?, closed_at = NOW(), payout = ? " +
               "WHERE id = ? AND claimed_by = ? AND status IN ('claimed', 'enroute', 'onscene')",
               new object[] { outcome, payout, call.Id, license }, affected =>
               {
                   if (affected != 1) { Say(p, "That call was already closed."); SendState(p); return; }
                   FinalizeClose(call, outcome, payout, CallerCloseText(call.Id, outcome));
               });
        }

        // ==================================================================
        //  State helpers
        // ==================================================================

        // Category strings are not job ids: medical calls belong to the "ems" job. Always map
        // through CategoryJob before asking jobs-cs who is on duty.
        bool Eligible(int src) => IsAdmin(src) || OnDuty(src, Police) || OnDuty(src, CategoryJob(Medical));

        bool CanView(int src, CallState call)
        {
            if (IsAdmin(src)) return true;
            return OnDuty(src, CategoryJob(call.Category));
        }

        bool CanClaim(Player p, CallState call)
        {
            return OnDuty(Src(p), CategoryJob(call.Category));
        }

        // dispatch.admin: staff overview (both queues) and moderation. dispatch.test: dev/test hooks.
        bool IsAdmin(int src) => IsAce(src, "dispatch.admin");
        bool CanTest(int src) => IsAdmin(src) || IsAce(src, "dispatch.test");

        bool IsAce(int src, string permission)
        {
            try { return IsPlayerAceAllowed(src.ToString(CultureInfo.InvariantCulture), permission); }
            catch (Exception) { return false; }
        }

        bool OnDuty(int src, string job)
        {
            try { return (bool)Exports["jobs-cs"].isOnDuty(src, job); }
            catch (Exception) { return false; }
        }

        string RoleOf(int src)
        {
            if (IsAdmin(src)) return "admin";
            if (OnDuty(src, Police)) return "police";
            if (OnDuty(src, CategoryJob(Medical))) return "ems";
            return "none";
        }

        void SendState(Player p)
        {
            if (p == null) return;
            int src = Src(p);
            if (!Eligible(src))
            {
                TriggerClientEvent(p, "dispatch:denied");
                Say(p, "Dispatch requires on-duty police or EMS.");
                return;
            }
            TriggerClientEvent(p, "dispatch:state", BuildState(src));
        }

        void BroadcastStates()
        {
            foreach (var p in Players.ToList())
            {
                if (p == null) continue;
                int src = Src(p);
                if (Eligible(src)) TriggerClientEvent(p, "dispatch:state", BuildState(src));
            }
        }

        string BuildState(int src)
        {
            string viewerLicense = License(Players[src]);
            Vector3 me = Vector3.Zero;
            int ped = GetPlayerPed(src.ToString(CultureInfo.InvariantCulture));
            if (ped != 0) me = GetEntityCoords(ped);

            long now = GetGameTimer();
            var calls = _calls.Values
                .Where(c => CanView(src, c))
                .OrderBy(c => c.Status == "open" ? 0 : 1)
                .ThenByDescending(c => c.Id)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"state\",\"role\":").Append(J(RoleOf(src)))
              .Append(",\"pay\":").Append(_pay)
              .Append(",\"calls\":[");

            for (int i = 0; i < calls.Count; i++)
            {
                var c = calls[i];
                bool canClaim = c.Status == "open" && c.CallerLicense != viewerLicense &&
                    OnDuty(src, CategoryJob(c.Category));
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(c.Id)
                  .Append(",\"category\":").Append(J(c.Category))
                  .Append(",\"message\":").Append(J(c.Message))
                  .Append(",\"area\":").Append(J(c.Area))
                  .Append(",\"status\":").Append(J(c.Status))
                  .Append(",\"caller\":").Append(J(c.CallerName))
                  .Append(",\"claimedBy\":").Append(J(c.ClaimedName ?? ""))
                  .Append(",\"mine\":").Append(c.ClaimedBy == viewerLicense ? "true" : "false")
                  .Append(",\"canClaim\":").Append(canClaim ? "true" : "false")
                  .Append(",\"age\":").Append((now - c.CreatedAt) / 1000)
                  .Append(",\"dist\":").Append((int)Dist2D(me, c.Pos))
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        void NotifyNew(CallState call)
        {
            string payload = call.Id + "|" + call.Category + "|" + call.Area;
            foreach (var p in Players.ToList())
            {
                if (p == null) continue;
                if (CanView(Src(p), call)) TriggerClientEvent(p, "dispatch:new", payload);
            }
        }

        void ReleaseInternal(CallState call, string claimerMessage, string callerMessage)
        {
            if (call == null || call.ClaimedBy == null || !_calls.ContainsKey(call.Id)) return;

            string holder = call.ClaimedBy;
            Db("UPDATE dispatch_calls SET status = 'open', claimed_by = NULL, claimed_name = NULL, claimed_at = NULL " +
               "WHERE id = ? AND claimed_by = ? AND status IN ('claimed', 'enroute', 'onscene')",
               new object[] { call.Id, holder }, affected =>
               {
                   if (affected != 1) return;

                   var claimer = FindPlayer(holder);
                   if (claimer != null)
                   {
                       TriggerClientEvent(claimer, "dispatch:unassigned", call.Id);
                       if (!string.IsNullOrEmpty(claimerMessage)) Say(claimer, claimerMessage);
                   }

                   var caller = FindPlayer(call.CallerLicense);
                   if (caller != null && !string.IsNullOrEmpty(callerMessage)) Say(caller, callerMessage);

                   call.Status = "open";
                   call.ClaimedBy = null;
                   call.ClaimedName = null;
                   call.ClaimedAt = 0;
                   BroadcastStates();
               });
        }

        void CloseSystem(CallState call, string outcome, int payout, string callerMessage)
        {
            if (call == null || !_calls.ContainsKey(call.Id)) return;

            Db("UPDATE dispatch_calls SET status = 'closed', outcome = ?, closed_at = NOW(), payout = ? " +
               "WHERE id = ? AND status <> 'closed'",
               new object[] { outcome, payout, call.Id }, affected =>
               {
                   if (affected != 1) return;
                   FinalizeClose(call, outcome, payout, callerMessage);
               });
        }

        void FinalizeClose(CallState call, string outcome, int payout, string callerMessage)
        {
            if (!_calls.Remove(call.Id)) return;

            string holder = call.ClaimedBy;
            var claimer = FindPlayer(holder);
            if (claimer != null)
            {
                TriggerClientEvent(claimer, "dispatch:unassigned", call.Id);
                if (payout > 0) Pay(claimer, call.Id, payout);
                else Say(claimer, $"Call #{call.Id} closed ({OutcomeLabel(outcome)}).");
            }
            else if (payout > 0)
            {
                // They left before the close completed; don't leave a payout in the row that nobody received.
                Db("UPDATE dispatch_calls SET payout = 0 WHERE id = ?", new object[] { call.Id }, _ => { });
            }

            var caller = FindPlayer(call.CallerLicense);
            if (caller != null && !string.IsNullOrEmpty(callerMessage)) Say(caller, callerMessage);

            BroadcastStates();
            Debug.WriteLine($"[dispatch] call #{call.Id} closed: {outcome}, payout {payout}");
        }

        void Pay(Player p, int callId, int amount)
        {
            try
            {
                bool paid = (bool)Exports["bank-cs"].addBank(Src(p), amount, $"dispatch call #{callId}");
                if (!paid)
                {
                    Db("UPDATE dispatch_calls SET payout = 0 WHERE id = ?", new object[] { callId }, _ => { });
                    Say(p, $"Call #{callId} closed, but the bank could not pay the bonus.");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("[dispatch] payout failed, bank-cs unavailable: " + e.Message);
                Db("UPDATE dispatch_calls SET payout = 0 WHERE id = ?", new object[] { callId }, _ => { });
                Say(p, $"Call #{callId} closed, but the bank could not pay the bonus.");
            }
        }

        Player FindPlayer(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            foreach (var p in Players.ToList())
                if (p != null && License(p) == license) return p;
            return null;
        }

        // ==================================================================
        //  Jobs integration: duty/job changes release the unit's calls
        // ==================================================================

        [EventHandler("jobs:dutyChanged")]
        void OnDutyChanged(int src, string job, bool onDuty)
        {
            var p = Players[src];
            if (p == null) return;

            string license = License(p);
            if (license == null) return;

            if (!onDuty)
            {
                TriggerClientEvent(p, "dispatch:denied");
                foreach (var call in _calls.Values
                    .Where(c => c.ClaimedBy == license && CategoryJob(c.Category) == job).ToList())
                    ReleaseInternal(call, "You went off duty, so the call was released.",
                        "The unit went off duty; your call is open again.");
            }
            else
            {
                SendState(p);
            }
        }

        [EventHandler("jobs:jobChanged")]
        void OnJobChanged(int src, string job)
        {
            var p = Players[src];
            if (p == null) return;

            string license = License(p);
            if (license == null) return;

            // A job change forces the unit off duty (jobs-cs clears the flag), so the console cannot
            // stay open, but this is not a refusal: close it quietly with its own message instead of
            // reusing dispatch:denied, which is the "you are not allowed" path.
            TriggerClientEvent(p, "dispatch:jobChanged");
            foreach (var call in _calls.Values.Where(c => c.ClaimedBy == license).ToList())
                ReleaseInternal(call, "Your job changed, so the call was released.",
                    "The unit changed job; your call is open again.");
        }

        [EventHandler("playerDropped")]
        void OnPlayerDropped([FromSource] Player p, string reason)
        {
            if (p == null) return;

            int src = Src(p);
            Forget(src);

            string license = License(p);
            if (license == null) return;

            _lastCall.Remove(license);
            _calling.Remove(license);
            _cancelPending.Remove(license);

            // A unit that disconnects must not keep a call locked forever.
            foreach (var call in _calls.Values.Where(c => c.ClaimedBy == license).ToList())
                ReleaseInternal(call, null, "The unit disconnected; your call is open again.");
        }

        // ==================================================================
        //  Expiry
        // ==================================================================

        async Task ExpireLoop()
        {
            while (true)
            {
                await Delay(15000);
                try
                {
                    long now = GetGameTimer();
                    foreach (var call in _calls.Values.ToList())
                    {
                        if (now - call.CreatedAt > _ttlMinutes * 60000L)
                            CloseSystem(call, "expired", 0,
                                $"Call #{call.Id} expired without a unit closing it.");
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine("[dispatch] expire loop: " + e.Message);
                }
            }
        }

        [EventHandler("onResourceStop")]
        void OnStop(string name)
        {
            if (name != GetCurrentResourceName()) return;
            _calls.Clear();
            Debug.WriteLine("[dispatch] stopped");
        }

        // ==================================================================
        //  Commands
        // ==================================================================

        void DispatchTest(Player p, List<object> args)
        {
            if (!CanTest(Src(p))) { Say(p, "You are not allowed to use that."); return; }

            string category = args.Count > 0 ? NormalizeCategory(args[0]?.ToString()) : Police;
            if (category == null) { Say(p, "Usage: /dispatchtest <police|medical> [message]"); return; }

            string message = args.Count > 1
                ? Clean(string.Join(" ", args.Skip(1).Select(x => x?.ToString())), MaxMessage)
                : "Test call for dispatch training.";

            int ped = GetPlayerPed(p.Handle);
            if (ped == 0) return;

            AddCall(p, "test:" + Guid.NewGuid().ToString("N"), "Test Caller", category, message,
                GetEntityCoords(ped), true);
        }

        void DispatchClose(Player p, List<object> args)
        {
            if (!CanTest(Src(p))) { Say(p, "You are not allowed to use that."); return; }
            if (args.Count < 1 || !int.TryParse(args[0]?.ToString(), out int id)) { Say(p, "Usage: /dispatchclose <call id>"); return; }
            if (!_calls.TryGetValue(id, out var call)) { Say(p, "That call is not active."); return; }

            CloseSystem(call, "cancelled", 0, $"Call #{id} was closed by staff.");
            Say(p, $"Call #{id} closed.");
        }

        void Cmd(string name, Action<Player, List<object>> handler)
        {
            RegisterCommand(name, new Action<int, List<object>, string>((src, args, raw) =>
            {
                if (src <= 0) { Debug.WriteLine($"[dispatch] /{name} is in-game only"); return; }
                var p = Players[src];
                if (p == null || RateLimited(src, 500)) return;
                handler(p, args);
            }), false);
        }

        // ==================================================================
        //  Formatting / validation helpers
        // ==================================================================

        static string NormalizeCategory(string category)
        {
            category = (category ?? "").Trim().ToLowerInvariant();
            if (category == "police" || category == "911") return Police;
            if (category == "medical" || category == "ems" || category == "912") return Medical;
            return null;
        }

        static string NormalizeOutcome(string outcome)
        {
            outcome = (outcome ?? "").Trim().ToLowerInvariant();
            if (outcome == "resolved" || outcome == "false_alarm" || outcome == "no_response") return outcome;
            return null;
        }

        static string CategoryJob(string category) => category == Police ? "police" : "ems";

        static string OutcomeLabel(string outcome)
        {
            if (outcome == "false_alarm") return "false alarm";
            if (outcome == "no_response") return "no response";
            return outcome;
        }

        static string CallerCloseText(int id, string outcome)
        {
            if (outcome == "resolved") return $"Your call #{id} was resolved.";
            if (outcome == "false_alarm") return $"Your call #{id} was closed as a false alarm.";
            if (outcome == "no_response") return $"Your call #{id} was closed; no unit could respond.";
            return $"Your call #{id} was closed.";
        }

        static string AreaOf(Vector3 pos)
        {
            string best = "Los Santos";
            double bestDistance = 2500.0;
            foreach (var area in Areas)
            {
                double d = Dist2D(pos, area.Pos);
                if (d < bestDistance) { bestDistance = d; best = area.Name; }
            }
            return best;
        }

        static double Dist2D(Vector3 a, Vector3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

        static string Clean(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c)) sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

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
    }
}
