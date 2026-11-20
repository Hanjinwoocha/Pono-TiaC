using System.Collections.Generic;
using ASI.Basecode.Data.Models;
using ASI.Basecode.Services.ServiceModels;

namespace ASI.Basecode.Services.Interfaces
{
    public interface IEquipmentService
    {
        // custodianCode = null means "all equipment" (students, admin)
        IEnumerable<EquipmentItem> GetAllEquipment(string custodianCode = null);
        IEnumerable<EquipmentCategory> GetCategories();
        // quantity > 1 adds that many units of the same model, coded <ItemCode>-01, -02, ...
        // serialNumbers (quantity > 1 only): one per unit, in order; blank entries leave that unit without one
        ServiceResult AddEquipment(EquipmentItem item, string custodianCode = null, int quantity = 1,
                                   IList<string> serialNumbers = null);
        // Units held by an approved reservation that hasn't been picked up (and hasn't expired)
        HashSet<int> GetReservedUnitIds();

        // True when another item still uses this photo (units added together share one file)
        bool ImageInUse(string imagePath, int exceptItemId);
        DashboardViewModel GetDashboardSummary(string search, int? categoryId, string status, string custodianCode = null);

        // ---- Edit / archive (custodian) ----
        EquipmentItem GetEquipmentById(int id);
        ServiceResult UpdateEquipment(EquipmentItem edited, string custodianCode = null);
        ServiceResult ArchiveEquipment(int id, string updatedBy, string custodianCode = null);
    }
}
