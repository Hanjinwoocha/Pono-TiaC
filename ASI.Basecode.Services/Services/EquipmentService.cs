using System;
using System.Collections.Generic;
using System.Linq;
using ASI.Basecode.Data.Interfaces;
using ASI.Basecode.Data.Models;
using ASI.Basecode.Services.Interfaces;
using ASI.Basecode.Services.ServiceModels;

namespace ASI.Basecode.Services.Services
{
    public class EquipmentService : IEquipmentService
    {
        private static readonly string[] BlankWords = { "n/a", "na", "none", "-", "--" };
        private static readonly string[] Conditions = { "excellent", "good", "minor_wear", "needs_maintenance" };

        private readonly IEquipmentRepository _equipmentRepository;

        public EquipmentService(IEquipmentRepository equipmentRepository)
        {
            _equipmentRepository = equipmentRepository;
        }

        // null code = no filter (students, admin). An unknown code resolves to 0 and matches nothing.
        private int? OwnerDbId(string custodianCode) =>
            string.IsNullOrEmpty(custodianCode) ? (int?)null : _equipmentRepository.GetUserDbId(custodianCode);

        public IEnumerable<EquipmentItem> GetAllEquipment(string custodianCode = null)
        {
            var q = _equipmentRepository.GetEquipmentItems();
            var ownerDbId = OwnerDbId(custodianCode);
            if (ownerDbId.HasValue) q = q.Where(e => e.CustodianId == ownerDbId.Value);
            else q = HideInactiveCustodians(q);
            return q.ToList();
        }

        // Students and the catalog never show equipment whose custodian is inactive or disabled,
        // because nobody could approve a request for it
        private IQueryable<EquipmentItem> HideInactiveCustodians(IQueryable<EquipmentItem> items)
        {
            var inactive = _equipmentRepository.GetInactiveCustodianIds().ToList();
            if (inactive.Count == 0) return items;
            return items.Where(e => e.CustodianId == null || !inactive.Contains(e.CustodianId.Value));
        }

        public IEnumerable<EquipmentCategory> GetCategories()
        {
            return _equipmentRepository.GetCategories().ToList();
        }

        // Blank, "N/A", "none" and "-" all become empty (NULL), so they never clash with the unique serial index
        private static string Optional(string value)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v) || BlankWords.Contains(v, StringComparer.OrdinalIgnoreCase)) return null;
            return v;
        }

        private static bool BadSuggestion(string suggestion) =>
            suggestion == null || suggestion.Length < 2 || suggestion.Length > 100;

        // ---------- Add ----------
        public const int MaxUnitsPerAdd = 50;
        private const int ItemCodeMaxLength = 50;

        public ServiceResult AddEquipment(EquipmentItem item, string custodianCode = null, int quantity = 1,
                                          IList<string> serialNumbers = null)
        {
            item.Id = 0;

            // The owner always comes from the logged-in custodian, never from the posted form
            var ownerDbId = OwnerDbId(custodianCode);
            if (ownerDbId == 0) return ServiceResult.Fail("Your session expired. Please log in again.");
            item.CustodianId = ownerDbId;
            item.ItemCode = item.ItemCode?.Trim();
            item.BrandModel = item.BrandModel?.Trim();

            if (string.IsNullOrEmpty(item.ItemCode) || string.IsNullOrEmpty(item.BrandModel))
                return ServiceResult.Fail("Item code and brand & model are required.");

            if (quantity < 1 || quantity > MaxUnitsPerAdd)
                return ServiceResult.Fail($"Quantity must be between 1 and {MaxUnitsPerAdd}.");

            item.SerialNumber = Optional(item.SerialNumber);
            item.StorageLocation = Optional(item.StorageLocation);

            // Several units: each gets the code <ItemCode>-01, -02, ... and its own record
            var codes = quantity == 1
                ? new List<string> { item.ItemCode }
                : Enumerable.Range(1, quantity).Select(n => $"{item.ItemCode}-{n:D2}").ToList();

            // Several units: one serial per unit, in order (unit 1 gets the first). Blank = no serial yet.
            var unitSerials = new List<string>();
            if (quantity > 1)
            {
                if (item.ItemCode.Length > ItemCodeMaxLength - 3)
                    return ServiceResult.Fail($"When adding several units, the item code can be at most {ItemCodeMaxLength - 3} characters (a number like -01 is added to it).");

                unitSerials = (serialNumbers ?? new List<string>()).Select(Optional).ToList();
                while (unitSerials.Count > 0 && unitSerials[unitSerials.Count - 1] == null)
                    unitSerials.RemoveAt(unitSerials.Count - 1);   // trailing blank lines don't count

                if (unitSerials.Count > quantity)
                    return ServiceResult.Fail($"You entered {unitSerials.Count} serial numbers for {quantity} units. Enter one per unit, or fewer and add the rest later.");

                var tooLong = unitSerials.FirstOrDefault(s => s != null && s.Length > 100);
                if (tooLong != null)
                    return ServiceResult.Fail("Each serial number can be at most 100 characters.");

                var repeated = unitSerials.Where(s => s != null)
                                          .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                                          .FirstOrDefault(g => g.Count() > 1);
                if (repeated != null)
                    return ServiceResult.Fail($"The serial number \"{repeated.Key}\" is entered more than once.");

                var entered = unitSerials.Where(s => s != null).ToList();
                var takenSerial = _equipmentRepository.GetEquipmentItems()
                    .Where(e => entered.Contains(e.SerialNumber))
                    .Select(e => e.SerialNumber)
                    .FirstOrDefault();
                if (takenSerial != null)
                    return ServiceResult.Fail($"The serial number \"{takenSerial}\" is already in use.");

                item.SerialNumber = null;   // the single serial field is for single units only

                // "Other" sends each unit to the admin separately, so it is only for single units
                if (!item.CategoryId.HasValue)
                    return ServiceResult.Fail("To add several units at once, choose a category. For a new category, add one unit first; once the admin approves its category, add the rest.");
            }

            var takenCode = codes.FirstOrDefault(_equipmentRepository.EquipmentExists);
            if (takenCode != null)
                return ServiceResult.Fail($"The item code \"{takenCode}\" is already in use.");

            if (item.SerialNumber != null &&
                _equipmentRepository.GetEquipmentItems().Any(e => e.SerialNumber == item.SerialNumber))
                return ServiceResult.Fail($"The serial number \"{item.SerialNumber}\" is already in use.");

            // Category: a real one, or "Other" (no category id) which waits for the admin
            if (item.CategoryId.HasValue)
            {
                var cat = _equipmentRepository.GetCategories().FirstOrDefault(c => c.Id == item.CategoryId.Value);
                if (cat == null || !cat.IsActive)
                    return ServiceResult.Fail("Please select a valid category.");

                item.Status = "available";
                item.SuggestedCategory = null;
            }
            else
            {
                var suggestion = Optional(item.SuggestedCategory);
                if (BadSuggestion(suggestion))
                    return ServiceResult.Fail("Please type the category this item needs (2 to 100 characters).");

                item.Status = "pending_category";
                item.SuggestedCategory = suggestion;
            }

            item.CategoryReviewNote = null;
            if (!Conditions.Contains(item.ConditionRating)) item.ConditionRating = "good";

            item.Category = null;   // only the foreign key is used
            item.CreatedTime = DateTime.Now;
            item.UpdatedTime = DateTime.Now;
            if (string.IsNullOrEmpty(item.CreatedBy)) item.CreatedBy = "Custodian";
            if (string.IsNullOrEmpty(item.UpdatedBy)) item.UpdatedBy = "Custodian";

            try
            {
                if (quantity == 1)
                {
                    _equipmentRepository.AddEquipment(item);
                }
                else
                {
                    // Copies of the filled-in item, one per code; they share the photo file
                    var units = codes.Select((code, index) => new EquipmentItem
                    {
                        ItemCode = code,
                        BrandModel = item.BrandModel,
                        CategoryId = item.CategoryId,
                        CustodianId = item.CustodianId,
                        SerialNumber = index < unitSerials.Count ? unitSerials[index] : null,
                        StorageLocation = item.StorageLocation,
                        ConditionRating = item.ConditionRating,
                        Status = item.Status,
                        ImagePath = item.ImagePath,
                        CreatedTime = item.CreatedTime,
                        UpdatedTime = item.UpdatedTime,
                        CreatedBy = item.CreatedBy,
                        UpdatedBy = item.UpdatedBy
                    }).ToList();

                    _equipmentRepository.AddEquipmentRange(units);
                }
            }
            catch (Exception)
            {
                return ServiceResult.Fail("Could not save the item. The item code or serial number may already be in use.");
            }

            if (quantity > 1)
                return ServiceResult.Ok($"{quantity} units registered ({codes.First()} to {codes.Last()}).");

            return ServiceResult.Ok(item.Status == "pending_category"
                ? "Item saved and sent to the admin for category review. It will be available to students once the category is approved."
                : "Equipment asset registered successfully.");
        }

        public HashSet<int> GetReservedUnitIds()
        {
            // Past its return time it is about to be expired, so it no longer holds the unit
            var now = DateTime.Now;
            return _equipmentRepository.GetReservations()
                .Where(r => r.Status == "approved" && r.BorrowingTransaction == null && r.EstimatedReturnDate > now)
                .Select(r => r.EquipmentItemId)
                .Distinct()
                .ToList()
                .ToHashSet();
        }

        public bool ImageInUse(string imagePath, int exceptItemId)
        {
            return !string.IsNullOrEmpty(imagePath) && _equipmentRepository.ImagePathInUse(imagePath, exceptItemId);
        }

        // ---------- Edit ----------
        public EquipmentItem GetEquipmentById(int id)
        {
            return _equipmentRepository.GetEquipmentById(id);
        }

        public ServiceResult UpdateEquipment(EquipmentItem edited, string custodianCode = null)
        {
            var existing = _equipmentRepository.GetEquipmentById(edited.Id);
            if (existing == null || existing.Status == "archived")
                return ServiceResult.Fail("Equipment item not found.");

            var ownerDbId = OwnerDbId(custodianCode);
            if (ownerDbId.HasValue && existing.CustodianId != ownerDbId)
                return ServiceResult.Fail("Equipment item not found.");

            var code = edited.ItemCode?.Trim();
            var brand = edited.BrandModel?.Trim();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(brand))
                return ServiceResult.Fail("Item code and brand & model are required.");

            if (_equipmentRepository.GetEquipmentItems().Any(e => e.Id != existing.Id && e.ItemCode == code))
                return ServiceResult.Fail($"Another item already uses the code \"{code}\".");

            var serial = Optional(edited.SerialNumber);
            if (serial != null &&
                _equipmentRepository.GetEquipmentItems().Any(e => e.Id != existing.Id && e.SerialNumber == serial))
                return ServiceResult.Fail($"Another item already uses the serial number \"{serial}\".");

            var wasAwaiting = ItemStatuses.AwaitingCategory.Contains(existing.Status);

            if (edited.CategoryId.HasValue)
            {
                var cat = _equipmentRepository.GetCategories().FirstOrDefault(c => c.Id == edited.CategoryId.Value);
                // a disabled category is only accepted if the item already has it
                if (cat == null || (!cat.IsActive && existing.CategoryId != cat.Id))
                    return ServiceResult.Fail("Please select a valid category.");

                existing.CategoryId = cat.Id;
                if (wasAwaiting)
                {
                    existing.Status = "available";
                    existing.SuggestedCategory = null;
                    existing.CategoryReviewNote = null;
                }
            }
            else
            {
                // "Other" is only for items that don't have a category yet
                if (!wasAwaiting)
                    return ServiceResult.Fail("This item already has a category. Please pick one from the list.");

                var suggestion = Optional(edited.SuggestedCategory);
                if (BadSuggestion(suggestion))
                    return ServiceResult.Fail("Please type the category this item needs (2 to 100 characters).");

                existing.SuggestedCategory = suggestion;
                existing.CategoryReviewNote = null;
                existing.Status = "pending_category";
            }

            existing.ItemCode = code;
            existing.BrandModel = brand;
            existing.SerialNumber = serial;
            existing.StorageLocation = Optional(edited.StorageLocation);

            if (Conditions.Contains(edited.ConditionRating))
                existing.ConditionRating = edited.ConditionRating;

            // Availability toggles between available <-> maintenance, never while out on loan
            // and never for items still waiting for a category
            if (!wasAwaiting && existing.Status != "borrowed" &&
                (edited.Status == "available" || edited.Status == "maintenance"))
            {
                existing.Status = edited.Status;
            }

            if (!string.IsNullOrEmpty(edited.ImagePath))
                existing.ImagePath = edited.ImagePath;

            existing.UpdatedBy = string.IsNullOrEmpty(edited.UpdatedBy) ? "Custodian" : edited.UpdatedBy;
            existing.UpdatedTime = DateTime.Now;

            try
            {
                _equipmentRepository.UpdateEquipment(existing);
            }
            catch (Exception)
            {
                return ServiceResult.Fail("Could not save the changes. The item code or serial number may already be in use.");
            }

            return ServiceResult.Ok(existing.Status == "pending_category"
                ? "Changes saved. The item is waiting for the admin to review its category."
                : "Equipment updated successfully.");
        }

        // ---------- Archive (soft delete) ----------
        public ServiceResult ArchiveEquipment(int id, string updatedBy, string custodianCode = null)
        {
            var existing = _equipmentRepository.GetEquipmentById(id);
            if (existing == null || existing.Status == "archived")
                return ServiceResult.Fail("Equipment item not found.");

            var ownerDbId = OwnerDbId(custodianCode);
            if (ownerDbId.HasValue && existing.CustodianId != ownerDbId)
                return ServiceResult.Fail("Equipment item not found.");

            var isOut = existing.Status == "borrowed" ||
                        _equipmentRepository.GetBorrowingTransactions()
                            .Any(t => t.EquipmentItemId == id && t.ActualReturnTime == null);
            if (isOut)
                return ServiceResult.Fail("This item is currently borrowed. Archive it after it is returned.");

            var hasOpenReservation = _equipmentRepository.GetReservations()
                .Any(r => r.EquipmentItemId == id &&
                          (r.Status == "pending" ||
                           (r.Status == "approved" && r.BorrowingTransaction == null)));
            if (hasOpenReservation)
                return ServiceResult.Fail("This item has pending or approved reservations. Resolve them first.");

            existing.UpdatedBy = string.IsNullOrEmpty(updatedBy) ? "Custodian" : updatedBy;
            existing.UpdatedTime = DateTime.Now;

            try
            {
                _equipmentRepository.DeleteEquipment(existing);   // soft delete: status = "archived"
            }
            catch (Exception)
            {
                return ServiceResult.Fail("Could not archive the item.");
            }

            return ServiceResult.Ok("Equipment archived. Its borrowing history is kept.");
        }

        // ---------- Custodian dashboard ----------
        public DashboardViewModel GetDashboardSummary(string search, int? categoryId, string status, string custodianCode = null)
        {
            // Items still waiting for a category are not part of the inventory yet
            var ownerDbId = OwnerDbId(custodianCode);

            var items = _equipmentRepository.GetEquipmentItems()   // non-archived, includes Category
                .Where(i => !ItemStatuses.AwaitingCategory.Contains(i.Status));
            var openLoans = _equipmentRepository.GetBorrowingTransactions()
                .Where(t => t.ActualReturnTime == null);
            var pending = _equipmentRepository.GetReservations()
                .Where(r => r.Status == "pending");

            if (ownerDbId.HasValue)
            {
                items = items.Where(i => i.CustodianId == ownerDbId.Value);
                openLoans = openLoans.Where(t => t.EquipmentItem.CustodianId == ownerDbId.Value);
                pending = pending.Where(r => r.EquipmentItem.CustodianId == ownerDbId.Value);
            }
            else
            {
                items = HideInactiveCustodians(items);
            }

            var borrowedIds = openLoans
                .Select(t => t.EquipmentItemId)
                .Distinct()
                .ToList();

            // On the shelf but promised to a student (approved, waiting for pickup): not available
            var reservedIds = GetReservedUnitIds().ToList();

            var model = new DashboardViewModel
            {
                TotalEquipment = items.Count(),
                CurrentlyBorrowed = items.Count(i => borrowedIds.Contains(i.Id)),
                AvailableItems = items.Count(i => i.Status == "available" && !borrowedIds.Contains(i.Id) && !reservedIds.Contains(i.Id)),
                PendingRequests = pending.Count(),
                BorrowedItemIds = borrowedIds,
                Categories = _equipmentRepository.GetCategories().OrderBy(c => c.CategoryName).ToList(),
                Search = search,
                CategoryId = categoryId,
                Status = status
            };

            var filtered = items;

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                filtered = filtered.Where(i => i.ItemCode.Contains(term)
                                            || i.BrandModel.Contains(term)
                                            || i.SerialNumber.Contains(term));
            }

            if (categoryId.HasValue)
            {
                filtered = filtered.Where(i => i.CategoryId == categoryId.Value);
            }

            if (status == "available")
            {
                filtered = filtered.Where(i => i.Status == "available" && !borrowedIds.Contains(i.Id) && !reservedIds.Contains(i.Id));
            }
            else if (status == "borrowed")
            {
                filtered = filtered.Where(i => borrowedIds.Contains(i.Id));
            }

            model.Items = filtered.OrderBy(i => i.ItemCode).ToList();
            return model;
        }
    }
}