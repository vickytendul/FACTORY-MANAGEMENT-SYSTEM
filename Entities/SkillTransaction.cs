using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class SkillTransaction
    {
        [FirestoreProperty]
        public int TransactionId { get; set; }

        [FirestoreProperty]
        public int OperationId { get; set; }

        [FirestoreProperty]
        public string EmployeeCode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        /// [OperationName] with punctuation, spacing and case removed, so
        /// the operation-roster lookup can match on it SERVER-SIDE.
        ///
        /// The roster has always had to match a skill either by OperationId
        /// or by normalised name, because the same operation carries
        /// different ids across layouts - measured live: 154 distinct
        /// operation names but 253 distinct operation ids. Normalising in
        /// memory meant reading every skill record to find the handful that
        /// matched. Storing the normalised form lets Firestore do it.
        ///
        /// Nullable, because documents written before this field existed do
        /// not have it. Readers must fall back to normalising [OperationName]
        /// themselves rather than assuming this is populated.
        [FirestoreProperty]
        public string? NormalizedOperationName { get; set; }

        [FirestoreProperty]
        public string MachineType { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationGrade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Section { get; set; } = string.Empty;

        [FirestoreProperty]
        public int CCId { get; set; }

        [FirestoreProperty]
        public string CCNo { get; set; } = string.Empty;

        [FirestoreProperty]
        public int TargetQty { get; set; }

        [FirestoreProperty]
        public int ActualQty { get; set; }

        [FirestoreProperty]
        public int EligiblePercentage { get; set; }

        [FirestoreProperty]
        public string Grade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string UpdatedBy { get; set; } = string.Empty;

        [FirestoreProperty]
        public DateTime UpdatedOn { get; set; }

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;

        /// The ONE definition of the normalised form. It lives on the entity
        /// so that whatever writes a skill record and whatever queries one
        /// cannot drift apart - a writer using a different rule from the
        /// reader would silently hide backup operators.
        public static string Normalize(string? value) =>
            new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        /// The normalised name to match against: the stored one when the
        /// document has it, otherwise derived on the spot. Lets a record
        /// written before the field existed still match in memory.
        public string EffectiveNormalizedOperationName =>
            string.IsNullOrEmpty(NormalizedOperationName)
                ? Normalize(OperationName)
                : NormalizedOperationName;
    }
}
