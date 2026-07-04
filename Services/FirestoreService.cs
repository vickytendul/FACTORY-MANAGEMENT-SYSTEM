using Google.Cloud.Firestore;

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
    }
}