using System;
using System.Globalization;

namespace Bank.Server
{
    class Account
    {
        public int Cash;
        public int Bank;
        public int Credit;
        public Loan Loan;

        public string Serialize() => Cash + "," + Bank;

        public static Account Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int c) || !int.TryParse(parts[1], out int b)) return null;
            return new Account { Cash = c, Bank = b };
        }
    }

    /// <summary>
    /// One loan per account, stored as a single KVP string next to the balances. A defaulted loan is
    /// kept until it is repaid, so defaulting is never a way to erase the debt. The format is
    /// pipe-separated and invariant-culture so a save/load round-trip never drifts.
    /// </summary>
    class Loan
    {
        public string Id;
        public int Principal;
        public int Balance;
        public int Installment;
        public int Paid;
        public int Missed;
        public int Total;
        public int IntervalMinutes;
        public long NextDue;
        public long Created;
        public string Status;

        public string Serialize() => string.Join("|",
            Id ?? "",
            Principal.ToString(CultureInfo.InvariantCulture),
            Balance.ToString(CultureInfo.InvariantCulture),
            Installment.ToString(CultureInfo.InvariantCulture),
            Paid.ToString(CultureInfo.InvariantCulture),
            Missed.ToString(CultureInfo.InvariantCulture),
            Total.ToString(CultureInfo.InvariantCulture),
            NextDue.ToString(CultureInfo.InvariantCulture),
            Created.ToString(CultureInfo.InvariantCulture),
            IntervalMinutes.ToString(CultureInfo.InvariantCulture),
            Status ?? "");

        public static Loan Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split('|');
            if (parts.Length != 11) return null;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int principal)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int balance)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int installment)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int paid)
                || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int missed)
                || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total)
                || !long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out long nextDue)
                || !long.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out long created)
                || !int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval)) return null;

            string status = parts[10];
            if (status != "active" && status != "defaulted") return null;
            if (principal <= 0 || balance < 0 || installment <= 0 || total <= 0 || interval <= 0) return null;

            return new Loan
            {
                Id = parts[0],
                Principal = principal,
                Balance = balance,
                Installment = installment,
                Paid = paid,
                Missed = missed,
                Total = total,
                NextDue = nextDue,
                Created = created,
                IntervalMinutes = interval,
                Status = status
            };
        }
    }
}
