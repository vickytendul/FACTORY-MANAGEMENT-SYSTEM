using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;

namespace FactoryManagementSystem.Services
{
    public class FirestoreService
    {
        private readonly FirestoreDb _db;

        public FirestoreService(FirestoreDb db)
        {
            _db = db;
        }

        public FirestoreDb Db => _db;

        public CollectionReference CCs => _db.Collection("CCs");
        public CollectionReference Zones => _db.Collection("Zones");
        public CollectionReference Lines => _db.Collection("Lines");
        public CollectionReference OperationMasters => _db.Collection("OperationMasters");
        public CollectionReference CCLayouts => _db.Collection("CCLayouts");
        public CollectionReference LayoutMasters =>_db.Collection("LayoutMasters");
        public CollectionReference EmployeeMasters =>_db.Collection("EmployeeMasters");
        public CollectionReference LayoutTransactions =>
    _db.Collection("LayoutTransactions");
        public CollectionReference AttendanceTransactions =>
    _db.Collection("AttendanceTransactions");
        public CollectionReference OutputTransactions =>
    _db.Collection("OutputTransactions");
        public CollectionReference Counters => _db.Collection("Counters");
    }
}