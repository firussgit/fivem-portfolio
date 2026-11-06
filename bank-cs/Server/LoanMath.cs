using System;

namespace Bank.Server
{
    /// <summary>
    /// Pure loan arithmetic. Kept free of FiveM types so the credit tiers, interest and
    /// instalment rounding can be checked on their own.
    /// </summary>
    static class LoanMath
    {
        public static int ClampCredit(int score) => Math.Max(300, Math.Min(850, score));

        // Larger loans unlock as the score grows. The server's bank_loan_max convar is a hard cap.
        public static int LimitFor(int score)
        {
            if (score >= 800) return 25000;
            if (score >= 700) return 10000;
            if (score >= 600) return 5000;
            if (score >= 500) return 2500;
            return 1000;
        }

        // Better credit pays less interest.
        public static int RateFor(int score)
        {
            if (score >= 800) return 6;
            if (score >= 700) return 9;
            if (score >= 600) return 12;
            if (score >= 500) return 18;
            return 24;
        }

        // Flat interest over the whole term; the last payment can be smaller than the rest.
        public static int TotalFor(int principal, int ratePercent) =>
            principal + (int)Math.Ceiling(principal * (ratePercent / 100.0));

        public static int InstallmentFor(int total, int payments) =>
            payments <= 0 ? 0 : (int)Math.Ceiling(total / (double)payments);
    }
}
