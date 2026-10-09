using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class LayoutMaster
    {
        [FirestoreProperty]
        public int Id { get; set; }

        [FirestoreProperty]
        public int CCId { get; set; }

        // Missing on legacy Firestore documents is deserialized as 0; all
        // consumers normalize that value to layout 1 for compatibility.
        [FirestoreProperty]
        public int LayoutNo { get; set; } = 1;

        [FirestoreProperty]
        public int SNo { get; set; }

        [FirestoreProperty]
        public int OperationId { get; set; }

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationGrade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string MachineType { get; set; } = string.Empty;

        [FirestoreProperty]
        public int DisplayOrder { get; set; }

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;
        [FirestoreProperty]
        public string Section { get; set; } = "MAIN";

        /// Whether this operation has to be manned for the line to count as
        /// fully allocated.
        ///
        /// An operation the floor has decided not to run still belongs to
        /// the style, so deleting its row is the wrong answer - and an
        /// actively dangerous one, because a layout save pairs request row
        /// i with existing row i and the allocation binds a person to the
        /// row's id. Removing a row from the middle therefore slides every
        /// operation below it onto somebody else's id. This leaves the row
        /// exactly where it is and takes it out of the count instead.
        ///
        /// Defaults to true, which is also what an absent field
        /// deserialises to on both stores - so every row that existed
        /// before this was added goes on being required.
        [FirestoreProperty]
        public bool IsRequired { get; set; } = true;
    }
}
