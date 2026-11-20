using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ASI.Basecode.Data.Interfaces;
using ASI.Basecode.Data.Models;
using ASI.Basecode.Services.Interfaces;
using ASI.Basecode.Services.ServiceModels;

namespace ASI.Basecode.Services.Services
{
    public class ReservationService : IReservationService
    {
        private readonly IReservationRepository _reservationRepository;
        private readonly ISettingsService _settingsService;
        // Restricts a borrower with an unresolved penalty past its deadline, or an item
        // still out more than 3 days past its due date. Never lifts a restriction (admin only).
        private void RefreshRestriction(int borrowerDbId)
        {
            var user = _reservationRepository.GetUserByDbId(borrowerDbId);
            if (user == null || user.BorrowerStatus == PenaltyRules.Restricted) return;

            var now = DateTime.Now;
            var cutoff = now.AddDays(-PenaltyRules.SettleLateWithinDays);

            var pastDeadline = _reservationRepository.GetPenalties().Any(p =>
                p.BorrowerId == borrowerDbId && p.Status == PenaltyRules.Unresolved && p.DueBy < now);

            var notReturned = _reservationRepository.GetTransactions().Any(t =>
                t.BorrowerId == borrowerDbId && t.ActualReturnTime == null &&
                t.Reservation.EstimatedReturnDate < cutoff);

            if (pastDeadline || notReturned)
            {
                user.BorrowerStatus = PenaltyRules.Restricted;
                user.UpdatedTime = now;
                _reservationRepository.SaveChanges();
            }
        }
        public ReservationService(IReservationRepository reservationRepository, ISettingsService settingsService)
        {
            _reservationRepository = reservationRepository;
            _settingsService = settingsService;
        }

        // ---------- Units of the same model ----------

        // The units of this item's model that could be handed over for [start, end): available right now
        // and not held by an approved reservation (not yet returned) that overlaps that time.
        // The given item comes first when it is free, then the others by item code.
        private List<EquipmentItem> FreeUnits(EquipmentItem item, DateTime start, DateTime end, int exceptReservationId = 0)
        {
            var units = _reservationRepository.GetEquipmentItems()
                .Where(EquipmentGrouping.SameModelAs(item))
                .Where(u => u.Status == "available")
                .ToList();
            if (units.Count == 0) return units;

            var unitIds = units.Select(u => u.Id).ToList();
            var busy = _reservationRepository.GetReservations()
                .Where(x => x.Id != exceptReservationId &&
                            unitIds.Contains(x.EquipmentItemId) &&
                            x.Status == "approved" &&
                            (x.BorrowingTransaction == null || x.BorrowingTransaction.ActualReturnTime == null) &&
                            x.RequestedPickupDate < end &&
                            x.EstimatedReturnDate > start)
                .Select(x => x.EquipmentItemId)
                .Distinct()
                .ToList();

            return units.Where(u => !busy.Contains(u.Id))
                        .OrderBy(u => u.Id == item.Id ? 0 : 1)
                        .ThenBy(u => u.ItemCode)
                        .ToList();
        }

        private int UnitCount(EquipmentItem item) =>
            _reservationRepository.GetEquipmentItems().Count(EquipmentGrouping.SameModelAs(item));

        public ReservationsPageViewModel GetPage(string search, int? categoryId, string custodianCode = null)
        {
            ExpireMissedPickups();

            var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

            var reservations = _reservationRepository.GetReservations();
            var transactions = _reservationRepository.GetTransactions().Where(t => t.ActualReturnTime == null);

            if (term != null)
            {
                reservations = reservations.Where(r =>
                    r.EquipmentItem.ItemCode.Contains(term) ||
                    r.EquipmentItem.BrandModel.Contains(term) ||
                    r.Borrower.FirstName.Contains(term) ||
                    r.Borrower.LastName.Contains(term) ||
                    r.Borrower.UserId.Contains(term));

                transactions = transactions.Where(t =>
                    t.EquipmentItem.ItemCode.Contains(term) ||
                    t.EquipmentItem.BrandModel.Contains(term) ||
                    t.Borrower.FirstName.Contains(term) ||
                    t.Borrower.LastName.Contains(term) ||
                    t.Borrower.UserId.Contains(term));
            }

            if (categoryId.HasValue)
            {
                reservations = reservations.Where(r => r.EquipmentItem.CategoryId == categoryId.Value);
                transactions = transactions.Where(t => t.EquipmentItem.CategoryId == categoryId.Value);
            }

            // A custodian only sees requests and loans for their own equipment
            if (!string.IsNullOrEmpty(custodianCode))
            {
                var ownerDbId = _reservationRepository.GetUserDbId(custodianCode);
                reservations = reservations.Where(r => r.EquipmentItem.CustodianId == ownerDbId);
                transactions = transactions.Where(t => t.EquipmentItem.CustodianId == ownerDbId);
            }

            var settings = _settingsService.Get();

            var approved = reservations.Where(r => r.Status == "approved" && r.BorrowingTransaction == null)
                                       .OrderBy(r => r.RequestedPickupDate).ToList();

            // Which unit to hand over is the custodian's choice when the model has several free ones
            var releaseUnits = new Dictionary<int, List<EquipmentItem>>();
            var now = DateTime.Now;
            foreach (var r in approved)
            {
                var start = now < r.RequestedPickupDate ? now : r.RequestedPickupDate;
                var free = FreeUnits(r.EquipmentItem, start, r.EstimatedReturnDate, r.Id);
                if (free.Count > 1) releaseUnits[r.Id] = free;
            }

            return new ReservationsPageViewModel
            {
                Pending = reservations.Where(r => r.Status == "pending")
                                      .OrderBy(r => r.RequestedPickupDate).ToList(),
                Approved = approved,
                ReleaseUnits = releaseUnits,
                Active = transactions.OrderBy(t => t.ActualPickupTime).ToList(),
                Categories = _reservationRepository.GetCategories().OrderBy(c => c.CategoryName).ToList(),
                Search = search,
                CategoryId = categoryId,
                GraceMinutes = settings.OverdueGraceMinutes,
                DueSoonHours = settings.DueSoonHours
            };

        }

        // ---------- Student side ----------

        // All reservations of one borrower (userCode = Borrower.UserId, i.e. the session "UserId"), newest first
        public List<Reservation> GetForUser(string userCode)
        {
            ExpireMissedPickups();

            var borrowerDbId = _reservationRepository.GetUserDbId(userCode);
            if (borrowerDbId != 0) RefreshRestriction(borrowerDbId);

            return _reservationRepository.GetReservations()
                .Where(r => r.Borrower.UserId == userCode)
                .OrderByDescending(r => r.CreatedTime)
                .ToList();
        }

        // Items the student has picked up and not yet returned
        public int CountActiveForUser(string userCode)
        {
            return _reservationRepository.GetReservations().Count(r =>
                r.Borrower.UserId == userCode &&
                r.BorrowingTransaction != null &&
                r.BorrowingTransaction.ActualReturnTime == null);
        }

        // Requests still waiting for the custodian's decision
        public int CountPendingForUser(string userCode)
        {
            return _reservationRepository.GetReservations().Count(r =>
                r.Borrower.UserId == userCode &&
                r.Status == "pending");
        }
        // Pending requests, approved reservations and items not yet returned all count toward the limit
        private int HoldingCount(int borrowerDbId) =>
            _reservationRepository.GetReservations().Count(x =>
                x.BorrowerId == borrowerDbId &&
                (x.Status == "pending" ||
                 (x.Status == "approved" &&
                  (x.BorrowingTransaction == null || x.BorrowingTransaction.ActualReturnTime == null))));

        public int CountHoldingForUser(string userCode)
        {
            var borrowerDbId = _reservationRepository.GetUserDbId(userCode);
            return borrowerDbId == 0 ? 0 : HoldingCount(borrowerDbId);
        }

        public ServiceResult CreateReservation(string borrowerCode, int equipmentItemId, DateTime pickup, DateTime returnDate, int quantity = 1)
        {
            var borrowerDbId = _reservationRepository.GetUserDbId(borrowerCode);
            if (borrowerDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");
            if (quantity < 1)
                return ServiceResult.Fail("Please request at least 1 unit.");

            ExpireMissedPickups();

            RefreshRestriction(borrowerDbId);
            if (_reservationRepository.GetUserByDbId(borrowerDbId)?.BorrowerStatus == PenaltyRules.Restricted)
                return ServiceResult.Fail("Your borrowing is restricted because of an unresolved penalty (overdue or damaged equipment). Settle it with the custodian, then ask an administrator to reactivate your account.");

            // The student requests a model; the item they opened is just one of its units
            var item = _reservationRepository.GetEquipmentItemById(equipmentItemId);
            if (item == null || item.Status == "archived")
                return ServiceResult.Fail("This equipment item was not found.");
            if (item.CustodianId.HasValue && !_reservationRepository.IsAccountActive(item.CustodianId.Value))
                return ServiceResult.Fail("This item is not available for reservation right now.");

            if (pickup == default || returnDate == default)
                return ServiceResult.Fail("Please choose both a pickup and a return date and time.");

            // The custodian's rules: duration, how far ahead, operating hours
            var rules = _settingsService.Get();
            var scheduleError = BorrowingRules.CheckSchedule(rules, pickup, returnDate, DateTime.Now);
            if (scheduleError != null)
                return ServiceResult.Fail(scheduleError);

            // Item limit (set by the custodian): every requested unit counts, together with what the
            // student already has pending, approved or out
            var holding = HoldingCount(borrowerDbId);
            if (holding >= rules.MaxActiveItems)
                return ServiceResult.Fail(
                    $"You already have {holding} request(s) or item(s) in progress. The limit is {rules.MaxActiveItems}. " +
                    "Wait for a decision, cancel a request, or return an item first.");
            if (holding + quantity > rules.MaxActiveItems)
                return ServiceResult.Fail(
                    $"You can request at most {rules.MaxActiveItems - holding} more item(s) right now " +
                    $"(the limit is {rules.MaxActiveItems} and you have {holding} in progress).");

            // A unit of this model that no approved reservation holds during that time
            var free = FreeUnits(item, pickup, returnDate);
            if (free.Count == 0)
            {
                var anyAvailable = _reservationRepository.GetEquipmentItems()
                    .Where(EquipmentGrouping.SameModelAs(item))
                    .Any(u => u.Status == "available");
                return ServiceResult.Fail(anyAvailable
                    ? "This item is already reserved for that time. Please choose different dates."
                    : "This item is not available for reservation right now.");
            }

            // One open request per student per unit: a second request for the same model goes to another unit
            var myPendingUnitIds = _reservationRepository.GetReservations()
                .Where(x => x.BorrowerId == borrowerDbId && x.Status == "pending")
                .Select(x => x.EquipmentItemId)
                .ToList();
            var choices = free.Where(u => !myPendingUnitIds.Contains(u.Id)).ToList();
            if (choices.Count == 0)
                return ServiceResult.Fail("You already have a pending request for this item.");
            if (choices.Count < quantity)
                return ServiceResult.Fail(
                    $"Only {choices.Count} unit(s) of this item are free for that time. Request fewer, or choose different dates.");

            // Spread requests over the units: pick the one with the fewest other requests for that time
            var choiceIds = choices.Select(u => u.Id).ToList();
            var requestsPerUnit = _reservationRepository.GetReservations()
                .Where(x => x.Status == "pending" &&
                            choiceIds.Contains(x.EquipmentItemId) &&
                            x.RequestedPickupDate < returnDate &&
                            x.EstimatedReturnDate > pickup)
                .GroupBy(x => x.EquipmentItemId)
                .Select(g => new { UnitId = g.Key, Count = g.Count() })
                .ToList();
            // Least-requested units first; ties keep the order of FreeUnits (the opened unit, then by item code)
            var units = choices
                .OrderBy(u => requestsPerUnit.FirstOrDefault(p => p.UnitId == u.Id)?.Count ?? 0)
                .Take(quantity)
                .ToList();

            // One reservation per unit, so each is approved, released and returned on its own.
            // They are saved together: either every unit is requested, or none.
            var now = DateTime.Now;
            foreach (var unit in units)
            {
                _reservationRepository.AddReservation(new Reservation
                {
                    BorrowerId = borrowerDbId,
                    EquipmentItemId = unit.Id,
                    RequestedPickupDate = pickup,
                    EstimatedReturnDate = returnDate,
                    Status = "pending",
                    CreatedTime = now,
                    UpdatedTime = now
                });
            }
            _reservationRepository.SaveChanges();

            return ServiceResult.Ok(quantity == 1
                ? "Request sent! The custodian will review it and you will see the result here."
                : $"Request for {quantity} units sent! Each unit is listed separately in My Reservations, and the custodian reviews them one by one.");
        }

        public ServiceResult CancelReservation(int reservationId, string borrowerCode)
        {
            var borrowerDbId = _reservationRepository.GetUserDbId(borrowerCode);
            if (borrowerDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");

            var reservation = _reservationRepository.GetReservationById(reservationId);
            if (reservation == null || reservation.BorrowerId != borrowerDbId)
                return ServiceResult.Fail("Reservation not found.");

            // Once the item has been handed over it can no longer be cancelled
            var canCancel = reservation.BorrowingTransaction == null &&
                            (reservation.Status == "pending" || reservation.Status == "approved");
            if (!canCancel)
                return ServiceResult.Fail("This reservation can no longer be cancelled.");

            reservation.Status = "cancelled";
            reservation.ReviewNotes = null;   // a cancelled request that has a note was cancelled by someone else
            reservation.UpdatedTime = DateTime.Now;

            try
            {
                _reservationRepository.SaveChanges();
            }
            catch (Exception)
            {
                return ServiceResult.Fail("Could not cancel the reservation. Please try again.");
            }

            return ServiceResult.Ok("Your reservation was cancelled.");
        }

        // ---------- Custodian actions ----------

        public ServiceResult Approve(int reservationId, string notes, string custodianCode)
        {
            var custodianDbId = _reservationRepository.GetUserDbId(custodianCode);
            if (custodianDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");

            ExpireMissedPickups();

            var reservation = _reservationRepository.GetReservationById(reservationId);
            if (reservation == null) return ServiceResult.Fail("Reservation not found.");
            if (reservation.EquipmentItem.CustodianId != custodianDbId)
                return ServiceResult.Fail("This request belongs to another custodian.");
            if (reservation.Status != "pending") return ServiceResult.Fail("This request was already processed.");
            // An approval whose return time has passed would be expired again right away
            if (reservation.EstimatedReturnDate <= DateTime.Now)
                return ServiceResult.Fail("This request's return time has already passed, so it can't be approved. Decline it, and the student can send a new request.");
            if (reservation.Borrower?.BorrowerStatus == PenaltyRules.Restricted)
                return ServiceResult.Fail("This borrower is restricted because of an unresolved penalty.");

            // Only an approved reservation that covers part of the same time blocks a unit (the same rule
            // students are held to when they request). If the requested unit is taken, another unit of
            // the same model is used instead.
            var free = FreeUnits(reservation.EquipmentItem, reservation.RequestedPickupDate,
                                 reservation.EstimatedReturnDate, reservation.Id);
            if (free.Count == 0)
            {
                if (UnitCount(reservation.EquipmentItem) > 1)
                    return ServiceResult.Fail("Every unit of this item is reserved or unavailable for that time. Decline this request, or cancel another reservation first.");
                return ServiceResult.Fail(reservation.EquipmentItem.Status != "available"
                    ? "This item is not available right now."
                    : "Another approved reservation already covers part of this time for the item. Cancel that one first, or decline this request.");
            }

            var limit = _settingsService.Get().MaxActiveItems;
            var holding = _reservationRepository.GetReservations().Count(x =>
                x.Id != reservation.Id &&
                x.BorrowerId == reservation.BorrowerId &&
                x.Status == "approved" &&
                (x.BorrowingTransaction == null || x.BorrowingTransaction.ActualReturnTime == null));
            if (holding >= limit)
                return ServiceResult.Fail($"This borrower already has {holding} approved or borrowed item(s). The limit is {limit}. Decline this request, or approve it after an item is returned.");

            var unit = free[0];   // the requested unit when it is free
            if (unit.Id != reservation.EquipmentItemId)
            {
                reservation.EquipmentItemId = unit.Id;
                reservation.EquipmentItem = unit;
            }

            reservation.Status = "approved";
            reservation.ReviewedBy = custodianDbId;
            reservation.ReviewedAt = DateTime.Now;
            reservation.ReviewNotes = notes;
            reservation.UpdatedTime = DateTime.Now;
            _reservationRepository.SaveChanges();

            return ServiceResult.Ok(UnitCount(unit) > 1
                ? $"Request approved for unit {unit.ItemCode}. Release it when the borrower arrives."
                : "Request approved. Release the item when the borrower arrives.");
        }

        public ServiceResult Decline(int reservationId, string reason, string custodianCode)
        {
            var custodianDbId = _reservationRepository.GetUserDbId(custodianCode);
            if (custodianDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");

            var reservation = _reservationRepository.GetReservationById(reservationId);
            if (reservation == null) return ServiceResult.Fail("Reservation not found.");
            if (reservation.EquipmentItem.CustodianId != custodianDbId)
                return ServiceResult.Fail("This request belongs to another custodian.");
            if (reservation.Status != "pending") return ServiceResult.Fail("This request was already processed.");

            reservation.Status = "declined";
            reservation.ReviewedBy = custodianDbId;
            reservation.ReviewedAt = DateTime.Now;
            reservation.ReviewNotes = reason;
            reservation.UpdatedTime = DateTime.Now;
            _reservationRepository.SaveChanges();

            return ServiceResult.Ok("Request declined.");
        }

        // The custodian cancels an approved reservation that hasn't been picked up (no-show, maintenance, ...)
        public ServiceResult CancelApproved(int reservationId, string reason, string custodianCode)
        {
            var custodianDbId = _reservationRepository.GetUserDbId(custodianCode);
            if (custodianDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");

            var reservation = _reservationRepository.GetReservationById(reservationId);
            if (reservation == null) return ServiceResult.Fail("Reservation not found.");
            if (reservation.EquipmentItem.CustodianId != custodianDbId)
                return ServiceResult.Fail("This reservation belongs to another custodian.");
            if (reservation.Status != "approved" || reservation.BorrowingTransaction != null)
                return ServiceResult.Fail("Only an approved reservation that hasn't been picked up can be cancelled.");

            reason = (reason ?? "").Trim();
            if (reason.Length < 3 || reason.Length > 255)
                return ServiceResult.Fail("Please give a reason (3 to 255 characters).");

            var now = DateTime.Now;
            reservation.Status = "cancelled";
            reservation.ReviewNotes = reason;
            reservation.ReviewedBy = custodianDbId;
            reservation.ReviewedAt = now;
            reservation.UpdatedTime = now;

            try
            {
                _reservationRepository.SaveChanges();
            }
            catch (Exception)
            {
                return ServiceResult.Fail("Could not cancel the reservation. Please try again.");
            }

            return ServiceResult.Ok("Reservation cancelled. The borrower will see your reason.");
        }

        // A reservation that was never picked up expires once its return time has passed:
        // an approved one (no-show) so it can't keep blocking the item, and a pending one because
        // it can no longer be approved. Runs whenever reservations are read or changed.
        private void ExpireMissedPickups()
        {
            var now = DateTime.Now;
            var missed = _reservationRepository.GetReservations()
                .Where(r => (r.Status == "approved" || r.Status == "pending") &&
                            r.BorrowingTransaction == null && r.EstimatedReturnDate < now)
                .ToList();
            if (missed.Count == 0) return;

            foreach (var r in missed)
            {
                r.ReviewNotes = r.Status == "pending"
                    ? "Expired: the request was not reviewed before the return time."
                    : "Expired: the item was not picked up before the return time.";
                r.Status = "cancelled";
                r.UpdatedTime = now;
            }
            _reservationRepository.SaveChanges();
        }

        // unitDbId: the unit the custodian hands over (any free unit of the same model); null = the assigned one
        public ServiceResult Release(int reservationId, string custodianCode, int? unitDbId = null)
        {
            var custodianDbId = _reservationRepository.GetUserDbId(custodianCode);
            if (custodianDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");

            ExpireMissedPickups();

            var reservation = _reservationRepository.GetReservationById(reservationId);
            if (reservation == null) return ServiceResult.Fail("Reservation not found.");
            if (reservation.EquipmentItem.CustodianId != custodianDbId)
                return ServiceResult.Fail("This request belongs to another custodian.");
            if (reservation.Status != "approved" || reservation.BorrowingTransaction != null)
                return ServiceResult.Fail("This reservation can't be released. It may have expired or been cancelled.");

            // The unit handed over: the one picked by the custodian, else the assigned one, else another free unit
            var start = DateTime.Now < reservation.RequestedPickupDate ? DateTime.Now : reservation.RequestedPickupDate;
            var free = FreeUnits(reservation.EquipmentItem, start, reservation.EstimatedReturnDate, reservation.Id);
            EquipmentItem unit;
            if (unitDbId.HasValue)
            {
                unit = free.FirstOrDefault(u => u.Id == unitDbId.Value);
                if (unit == null)
                    return ServiceResult.Fail("That unit isn't free for this reservation. Please choose another unit.");
            }
            else
            {
                unit = free.FirstOrDefault();
                if (unit == null)
                    return ServiceResult.Fail("This item is not available right now.");
            }

            if (unit.Id != reservation.EquipmentItemId)
            {
                reservation.EquipmentItemId = unit.Id;
                reservation.EquipmentItem = unit;
                reservation.UpdatedTime = DateTime.Now;
            }

            _reservationRepository.AddTransaction(new BorrowingTransaction
            {
                ReservationId = reservation.Id,
                EquipmentItemId = unit.Id,
                BorrowerId = reservation.BorrowerId,
                ReleasedBy = custodianDbId,
                ActualPickupTime = DateTime.Now,
                IsOverdue = false,
                DelayDurationHours = 0,
                CreatedTime = DateTime.Now,
                UpdatedTime = DateTime.Now
            });

            reservation.EquipmentItem.Status = "borrowed";
            reservation.EquipmentItem.UpdatedTime = DateTime.Now;
            _reservationRepository.SaveChanges();

            return ServiceResult.Ok($"{unit.ItemCode} released to the borrower.");
        }

        public ServiceResult Return(int transactionId, string condition, string notes, string custodianCode)
        {
            var custodianDbId = _reservationRepository.GetUserDbId(custodianCode);
            if (custodianDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");

            var transaction = _reservationRepository.GetTransactionById(transactionId);
            if (transaction == null) return ServiceResult.Fail("Transaction not found.");
            if (transaction.EquipmentItem.CustodianId != custodianDbId)
                return ServiceResult.Fail("This loan belongs to another custodian.");
            if (transaction.ActualReturnTime != null) return ServiceResult.Fail("This item was already returned.");

            var now = DateTime.Now;
            transaction.ActualReturnTime = now;
            transaction.ReturnedTo = custodianDbId;
            transaction.ReturnCondition = condition;
            transaction.DamageNotes = notes;
            transaction.UpdatedTime = now;

            var due = transaction.Reservation?.EstimatedReturnDate;
            if (due.HasValue)
            {
                var lateBy = now - due.Value;
                if (lateBy.TotalMinutes > _settingsService.Get().OverdueGraceMinutes)
                {
                    transaction.IsOverdue = true;
                    transaction.DelayDurationHours = (int)Math.Ceiling(lateBy.TotalHours);
                }
            }
            // ---- Penalties ----
            if (transaction.IsOverdue && due.HasValue)
            {
                var days = PenaltyRules.LateDays(due.Value, now);
                _reservationRepository.AddPenalty(new Penalty
                {
                    TransactionId = transaction.Id,
                    BorrowerId = transaction.BorrowerId,
                    PenaltyType = PenaltyRules.Late,
                    Amount = days * PenaltyRules.LateFeePerDay,
                    Notes = $"{days} day(s) late",
                    Status = PenaltyRules.Unresolved,
                    DueBy = now.AddDays(PenaltyRules.SettleLateWithinDays),
                    CreatedTime = now
                });
            }

            if (PenaltyRules.IsDamage(condition))
            {
                _reservationRepository.AddPenalty(new Penalty
                {
                    TransactionId = transaction.Id,
                    BorrowerId = transaction.BorrowerId,
                    PenaltyType = PenaltyRules.Damage,
                    Amount = 0,
                    Notes = notes,
                    Status = PenaltyRules.Unresolved,
                    DueBy = now.AddDays(PenaltyRules.ReplaceWithinDays),
                    CreatedTime = now
                });
            }

            transaction.EquipmentItem.ConditionRating = condition;
            transaction.EquipmentItem.Status = condition == "needs_maintenance" ? "maintenance" : "available";
            transaction.EquipmentItem.UpdatedTime = now;
            _reservationRepository.SaveChanges();

            return ServiceResult.Ok("Item marked as returned.");
        }

    }
}