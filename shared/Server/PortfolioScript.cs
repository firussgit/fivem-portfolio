using System;
using System.Collections.Generic;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace Portfolio.Core
{
    /// <summary>
    /// Small shared base class for the *-cs server resources.
    ///
    /// It is compiled into each resource (linked from shared/Server/PortfolioScript.cs by the
    /// resource's .csproj), so there is no extra assembly to deploy and no runtime dependency
    /// between resources. It gives every resource the same licence lookup, chat output, oxmysql
    /// calls and per-player rate limit instead of a slightly different copy of each.
    /// </summary>
    public abstract class PortfolioScript : BaseScript
    {
        readonly Dictionary<int, long> _lastUse = new Dictionary<int, long>();

        protected string ChatPrefix = "Resource";
        protected int[] ChatColour = { 120, 200, 255 };

        // ---------- players ----------

        protected static string License(Player p)
        {
            if (p == null) return null;
            string id = p.Identifiers["license"];
            return string.IsNullOrEmpty(id) ? null : id;
        }

        protected static int Src(Player p) => p == null ? 0 : int.Parse(p.Handle);

        // OneSync exposes the real ped/vehicle state on the server, so this is authoritative.
        protected static bool InVehicle(Player p)
        {
            if (p == null) return false;
            int ped = GetPlayerPed(p.Handle);
            return ped != 0 && GetVehiclePedIsIn(ped, false) != 0;
        }

        // ---------- output ----------

        protected void Say(Player p, string message)
        {
            if (p == null) return;
            TriggerClientEvent(p, "chat:addMessage", new { color = ChatColour, args = new[] { ChatPrefix, message } });
        }

        // ---------- rate limiting ----------

        protected bool RateLimited(int src, long cooldownMs = 700)
        {
            long now = GetGameTimer();
            if (_lastUse.TryGetValue(src, out long last) && now - last < cooldownMs) return true;
            _lastUse[src] = now;
            return false;
        }

        protected void Forget(int src) => _lastUse.Remove(src);

        // ---------- database (oxmysql) ----------

        protected void Query(string sql, object[] args, Action<List<IDictionary<string, object>>> done)
        {
            Exports["oxmysql"].query(sql, args, new Action<dynamic>(res =>
            {
                var rows = new List<IDictionary<string, object>>();
                if (res != null)
                    foreach (var row in res) rows.Add((IDictionary<string, object>)row);
                done(rows);
            }));
        }

        protected void Db(string sql, object[] args, Action<int> done) =>
            Exports["oxmysql"].update(sql, args, new Action<dynamic>(n => done(n == null ? 0 : Convert.ToInt32(n))));
    }
}
