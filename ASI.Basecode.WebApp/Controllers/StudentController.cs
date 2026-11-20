
using System;
using System.Collections.Generic;
using System.Linq;
using ASI.Basecode.Data.Models;
using ASI.Basecode.Services.Interfaces;
using ASI.Basecode.Services.ServiceModels;
using ASI.Basecode.WebApp.Mvc;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ASI.Basecode.WebApp.Controllers
{
    [Authorize(Roles = "Student,user")]
    public class StudentController : ControllerBase<StudentController>
    {
        private readonly IEquipmentService _equipmentService;
        private readonly IReservationService _reservationService;
        private readonly IHistoryService _historyService;
        private readonly ISettingsService _settingsService;

        public StudentController(
            IEquipmentService equipmentService,
            IReservationService reservationService,
            IHistoryService historyService,
            IHttpContextAccessor httpContextAccessor,
            ILoggerFactory loggerFactory,
            IConfiguration configuration,
            ISettingsService settingsService,
            IMapper mapper) : base(
                httpContextAccessor,
                loggerFactory,
                configuration,
                mapper)
        {
            _equipmentService = equipmentService;
            _reservationService = reservationService;
            _historyService = historyService;
            _settingsService = settingsService;
        }

        private string CurrentUserCode =>
            _session.GetString("UserId");

        /// <summary>
        /// Student Catalog / Dashboard
        /// </summary>
        public IActionResult Index(
            string search,
            int? categoryId,
            string status,
            int page = 1)
        {
            const int pageSize = 5;

            // Status is applied to whole models below, so units are fetched without it
            var catalog = _equipmentService.GetDashboardSummary(
                search, categoryId, null);
            catalog.Status = status;

            // One row per model ("3 of 5 available") instead of one per physical unit
            var allItems = EquipmentGrouping.Group(catalog.Items, _equipmentService.GetReservedUnitIds())
                .OrderBy(g => g.Representative.BrandModel)
                .ToList();
            if (status == "available")
                allItems = allItems.Where(g => g.IsAvailable).ToList();
            else if (status == "borrowed")
                allItems = allItems.Where(g => !g.IsAvailable).ToList();

            var totalPages = Math.Max(
                1,
                (int)Math.Ceiling(allItems.Count / (double)pageSize));

            page = Math.Clamp(page, 1, totalPages);

            var model = new StudentDashboardViewModel
            {
                Catalog = catalog,

                MyActiveBorrowings =
                    _reservationService.CountActiveForUser(CurrentUserCode),

                MyPendingRequests =
                    _reservationService.CountPendingForUser(CurrentUserCode),

                RecentReservations =
                    _reservationService.GetForUser(CurrentUserCode)
                        .Take(5)
                        .ToList(),

                PagedItems = allItems
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList(),

                Page = page,
                PageSize = pageSize,
                TotalPages = totalPages,
                TotalFiltered = allItems.Count
            };

            return View(model);
        }

        /// <summary>
        /// Search Equipment page.
        /// </summary>
        public IActionResult Equipment(
            string search,
            int? categoryId,
            string status,
            string sort,
            int page = 1)
        {
            var units = _equipmentService.GetAllEquipment()
                .Where(i =>
                    i.Status == "available" ||
                    i.Status == "borrowed");

            // One card per model, with how many of its units are free
            IEnumerable<EquipmentGroup> items = EquipmentGrouping.Group(units, _equipmentService.GetReservedUnitIds());

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();

                items = items.Where(g =>
                    (g.Representative.BrandModel ?? "").Contains(
                        term, StringComparison.OrdinalIgnoreCase) ||
                    (g.Representative.Category?.CategoryName ?? "").Contains(
                        term, StringComparison.OrdinalIgnoreCase) ||
                    g.Units.Any(u => (u.ItemCode ?? "").Contains(
                        term, StringComparison.OrdinalIgnoreCase)));
            }

            if (categoryId.HasValue)
            {
                items = items.Where(g =>
                    g.Representative.CategoryId == categoryId.Value);
            }

            // Available = at least one unit free; Currently Borrowed = every unit is out
            if (status == "available")
            {
                items = items.Where(g => g.IsAvailable);
            }
            else if (status == "borrowed")
            {
                items = items.Where(g => !g.IsAvailable);
            }

            items = sort == "category"
                ? items.OrderBy(g => g.Representative.Category?.CategoryName)
                    .ThenBy(g => g.Representative.BrandModel)
                : items.OrderBy(g => g.Representative.BrandModel);

            const int pageSize = 6;

            var all = items.ToList();

            var totalPages = Math.Max(
                1,
                (int)Math.Ceiling(all.Count / (double)pageSize));

            page = Math.Clamp(page, 1, totalPages);

            var pageItems = all
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalItems = all.Count;
            ViewBag.CategoryId = categoryId;

            ViewBag.Search = search;
            ViewBag.Status = status;
            ViewBag.Sort = sort;

            ViewBag.CategoryList = _equipmentService.GetCategories()
                .Select(c => new SelectListItem
                {
                    Value = c.Id.ToString(),
                    Text = c.CategoryName,
                    Selected = categoryId == c.Id
                })
                .ToList();

            return View(pageItems);
        }

        /// <summary>
        /// View the student's reservations.
        /// </summary>
        public IActionResult MyReservations()
        {
            return View(
                _reservationService.GetForUser(CurrentUserCode));
        }

        /// <summary>
        /// View the logged-in student's borrowing history.
        /// </summary>
        public IActionResult History(
            string search,
            int? categoryId,
            string result,
            DateTime? dateFrom,
            DateTime? dateTo)
        {
            var filter = new HistoryFilter
            {
                Search = search,
                CategoryId = categoryId,
                Result = result,
                DateFrom = dateFrom,
                DateTo = dateTo
            };

            var model = _historyService.GetStudentPage(
                filter,
                CurrentUserCode);

            return View(model);
        }

        /// <summary>
        /// View specific equipment details.
        /// </summary>
        public IActionResult EquipmentDetails(int id)
        {
            var all = _equipmentService.GetAllEquipment().ToList();
            var item = all.FirstOrDefault(i => i.Id == id);

            // Items still waiting for a category are not visible to students
            if (item == null || ItemStatuses.AwaitingCategory.Contains(item.Status))
            {
                TempData["ErrorMessage"] =
                    "That equipment item was not found.";

                return RedirectToAction(nameof(Equipment));
            }

            // The page shows the whole model: the opened unit plus the other units in circulation.
            // A request is for the model; the system picks a free unit when it is sent.
            var key = EquipmentGrouping.Key(item);
            var group = EquipmentGrouping.Group(all.Where(u =>
                    EquipmentGrouping.Key(u) == key &&
                    (u.Id == item.Id || u.Status == "available" || u.Status == "borrowed")),
                    _equipmentService.GetReservedUnitIds())
                .First();

            ViewBag.Group = group;
            var rules = _settingsService.Get();
            ViewBag.Rules = rules;

            // How many more items the student may request under the custodian's limit
            var holding = _reservationService.CountHoldingForUser(CurrentUserCode);
            ViewBag.Holding = holding;
            ViewBag.CanRequest = Math.Max(0, rules.MaxActiveItems - holding);
            return View(group.Representative);
        }

        /// <summary>
        /// Student sends a borrow request.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reserve(
            int equipmentItemId,
            DateTime pickupDate,
            DateTime returnDate,
            int quantity = 1)
        {
            var result = _reservationService.CreateReservation(
                CurrentUserCode,
                equipmentItemId,
                pickupDate,
                returnDate,
                quantity);

            if (result.Success)
            {
                TempData["SuccessMessage"] = result.Message;

                return RedirectToAction(nameof(MyReservations));
            }

            TempData["ErrorMessage"] = result.Message;

            // Keep what the student entered so they only have to fix the wrong part
            if (pickupDate != default)
                TempData["ReservePickup"] = pickupDate.ToString("yyyy-MM-dd'T'HH:mm");
            if (returnDate != default)
                TempData["ReserveReturn"] = returnDate.ToString("yyyy-MM-dd'T'HH:mm");
            TempData["ReserveQuantity"] = quantity;

            return RedirectToAction(
                nameof(EquipmentDetails),
                new { id = equipmentItemId });
        }

        /// <summary>
        /// Student cancels their own reservation.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CancelReservation(int id)
        {
            var result = _reservationService.CancelReservation(
                id, CurrentUserCode);

            TempData[result.Success
                ? "SuccessMessage"
                : "ErrorMessage"] = result.Message;

            return RedirectToAction(nameof(MyReservations));
        }
    }
}
