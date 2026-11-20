using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ASI.Basecode.Data.Models;

namespace ASI.Basecode.Data.Interfaces
{
    public interface IEquipmentRepository
    {
        IQueryable<EquipmentItem> GetEquipmentItems();
        IQueryable<EquipmentCategory> GetCategories();
        EquipmentItem GetEquipmentById(int id);
        IQueryable<Reservation> GetReservations();
        IQueryable<BorrowingTransaction> GetBorrowingTransactions();
        bool EquipmentExists(string itemCode);
        void AddEquipment(EquipmentItem equipment);
        // Several units in one save: all are added, or none
        void AddEquipmentRange(IEnumerable<EquipmentItem> equipment);
        // True when another item (archived ones included) still uses this photo file
        bool ImagePathInUse(string imagePath, int exceptItemId);
        void UpdateEquipment(EquipmentItem equipment);
        void DeleteEquipment(EquipmentItem equipment);

        // Converts the login code (user.user_id) into the numeric primary key (user.id)
        int GetUserDbId(string userCode);
        // Custodians who are inactive or disabled: students must not see or reserve their equipment
        IQueryable<int> GetInactiveCustodianIds();
    }
}
