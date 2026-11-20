using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ASI.Basecode.Data.Models;

namespace ASI.Basecode.Services.ServiceModels
{
    public class HistoryFilter
    {
        public string Search { get; set; }       // borrower ID, name, item code, brand
        public int? CategoryId { get; set; }
        public string Result { get; set; }       // active | ontime | overdue | damaged
        public DateTime? DateFrom { get; set; }  // filters on handover date
        public DateTime? DateTo { get; set; }
    }

    public class HistoryPageViewModel
    {
        public HistoryFilter Filter { get; set; } = new HistoryFilter();
        public List<EquipmentCategory> Categories { get; set; } = new List<EquipmentCategory>();

        public List<BorrowingTransaction> Rows { get; set; } = new List<BorrowingTransaction>();
        public List<BorrowingTransaction> PenaltyRows { get; set; } = new List<BorrowingTransaction>();

        // Summary metrics
        public int TotalTransactions { get; set; }
        public int ActiveLoans { get; set; }
        public int LateReturns { get; set; }
        public int CurrentlyOverdue { get; set; }
        public int TotalDelayHours { get; set; }
        public int DamagedReturns { get; set; }
        public int OnTimeRate { get; set; }      // percent of returned items
                                                 // Unresolved penalties (late fees and replacements)
        public List<StudentChargeRow> StudentCharges { get; set; } = new List<StudentChargeRow>();
        public List<Penalty> OpenPenalties { get; set; } = new List<Penalty>();
        public decimal TotalLateOwed => StudentCharges.Sum(s => s.LateTotal);
        public int TotalReplacementsPending => StudentCharges.Sum(s => s.ReplacementCount);
        public static string TxnCode(BorrowingTransaction t) => $"TXN-{t.ActualPickupTime:yyyy}-{t.Id:D4}";

        // ---- Overdue & Damage Penalty Summary (shared by the page, the CSV and the PDF) ----

        public const string PenaltyResolved = "Resolved";
        public const string PenaltyNotResolved = "Not Resolved";
        public const string PenaltyNoCharge = "No charge";

        // Resolved = every charge on this loan (late fee and/or replacement) was marked paid or replaced
        // in Student Charges. Still out past its due date = not resolved. Loans from before penalties
        // were recorded have no charge to settle.
        public static string PenaltyStatus(BorrowingTransaction t, DateTime now)
        {
            var stillOutLate = t.ActualReturnTime == null &&
                               t.Reservation != null && t.Reservation.EstimatedReturnDate < now;
            if (stillOutLate) return PenaltyNotResolved;

            var charges = t.Penalties ?? new List<Penalty>();
            if (!charges.Any()) return PenaltyNoCharge;

            return charges.Any(p => p.Status == ServiceModels.PenaltyRules.Unresolved)
                ? PenaltyNotResolved
                : PenaltyResolved;
        }

        // The problems on a loan, e.g. "Late +5 h", "Overdue - not returned", "Damaged"
        public static List<string> PenaltyIssues(BorrowingTransaction t, DateTime now)
        {
            var issues = new List<string>();
            if (t.ActualReturnTime != null && t.IsOverdue)
                issues.Add($"Late +{t.DelayDurationHours} h");
            if (t.ActualReturnTime == null && t.Reservation != null && t.Reservation.EstimatedReturnDate < now)
                issues.Add("Overdue - not returned");
            if (t.ReturnCondition == "needs_maintenance")
                issues.Add("Damaged");
            return issues;
        }
    }
}
