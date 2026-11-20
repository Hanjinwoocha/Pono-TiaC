using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ASI.Basecode.Data.Models;
using ASI.Basecode.Services.ServiceModels;

namespace ASI.Basecode.Services.Interfaces
{
    public interface IReservationService
    {
        ReservationsPageViewModel GetPage(string search, int? categoryId, string custodianCode = null);
        ServiceResult Approve(int reservationId, string notes, string custodianCode);
        ServiceResult Decline(int reservationId, string reason, string custodianCode);
        ServiceResult CancelApproved(int reservationId, string reason, string custodianCode);
        // unitDbId: hand over a different free unit of the same model (null = the assigned unit)
        ServiceResult Release(int reservationId, string custodianCode, int? unitDbId = null);
        ServiceResult Return(int transactionId, string condition, string notes, string custodianCode);

        // ---- Student side ----
        List<Reservation> GetForUser(string userCode);
        int CountActiveForUser(string userCode);
        int CountPendingForUser(string userCode);

        // Student asks to borrow an item; the custodian approves or declines it
        // quantity > 1 requests that many units of the same model (one reservation per unit, saved together)
        ServiceResult CreateReservation(string borrowerCode, int equipmentItemId, DateTime pickup, DateTime returnDate, int quantity = 1);

        // Requests + approved + borrowed items that count toward the custodian's "at most N items" rule
        int CountHoldingForUser(string userCode);

        // Student cancels their own request (pending, or approved but not yet picked up)
        ServiceResult CancelReservation(int reservationId, string borrowerCode);
    }
}
