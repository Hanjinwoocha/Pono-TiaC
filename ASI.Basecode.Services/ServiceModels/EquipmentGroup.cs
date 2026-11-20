using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using ASI.Basecode.Data.Models;

namespace ASI.Basecode.Services.ServiceModels
{
    /// <summary>
    /// Several physical units of the same equipment model (e.g. 20 Ethernet cables).
    /// Each unit is still its own EquipmentItem with its own code, serial and history;
    /// students see the model once, with how many units are free.
    /// </summary>
    public class EquipmentGroup
    {
        public List<EquipmentItem> Units { get; set; } = new List<EquipmentItem>();

        // Shown on cards and used for links: an available unit when there is one
        public EquipmentItem Representative { get; set; }

        // Units on the shelf and not promised to anyone
        public int AvailableCount { get; set; }
        // Units on the shelf but held by an approved reservation that hasn't been picked up yet
        public int ReservedCount { get; set; }
        public int Total => Units.Count;
        public bool IsAvailable => AvailableCount > 0;

        // Label for a single-unit model that isn't available: out on loan, or promised to someone
        public string UnavailableLabel =>
            Units.Any(u => u.Status == "borrowed") && ReservedCount == 0 ? "Currently Borrowed"
            : ReservedCount > 0 ? "Reserved"
            : "Unavailable";

        // The first unit that has a photo (units added together share one)
        public string ImagePath => Units.Select(u => u.ImagePath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
    }

    /// <summary>
    /// Units belong to the same model when they share the name (brand &amp; model), the category
    /// and the custodian. Including the custodian keeps every unit of a model with one owner,
    /// so the same person approves, releases and receives all of them.
    /// </summary>
    public static class EquipmentGrouping
    {
        // In-memory key. Name is trimmed and case-insensitive, matching how SQL Server compares it.
        public static string Key(EquipmentItem item) =>
            $"{(item.BrandModel ?? "").Trim().ToLowerInvariant()}|{item.CategoryId}|{item.CustodianId}";

        // reservedUnitIds: units held by an approved reservation awaiting pickup. They are on the shelf
        // (status "available") but promised to a student, so they don't count as available.
        public static List<EquipmentGroup> Group(IEnumerable<EquipmentItem> units, ISet<int> reservedUnitIds = null)
        {
            reservedUnitIds ??= new HashSet<int>();

            return units
                .GroupBy(Key)
                .Select(g =>
                {
                    var list = g.OrderBy(u => u.ItemCode).ToList();
                    var onShelf = list.Where(u => u.Status == "available").ToList();
                    var free = onShelf.Where(u => !reservedUnitIds.Contains(u.Id)).ToList();
                    return new EquipmentGroup
                    {
                        Units = list,
                        AvailableCount = free.Count,
                        ReservedCount = onShelf.Count - free.Count,
                        Representative = free.FirstOrDefault() ?? onShelf.FirstOrDefault() ?? list[0]
                    };
                })
                .ToList();
        }

        // Database query for the other units of the same model (not archived)
        public static Expression<Func<EquipmentItem, bool>> SameModelAs(EquipmentItem item)
        {
            var name = item.BrandModel;
            var categoryId = item.CategoryId;
            var custodianId = item.CustodianId;
            return e => e.BrandModel == name &&
                        e.CategoryId == categoryId &&
                        e.CustodianId == custodianId &&
                        e.Status != "archived";
        }
    }
}
