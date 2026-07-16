using FactoryManagementSystem.Data;
using FactoryManagementSystem.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// Firebase Authentication
// Render -> Environment Variable
// Local -> Firebase JSON File
// =====================================================


GoogleCredential credential;

if (builder.Environment.IsDevelopment())
{
    var firebasePath = Path.Combine(
        builder.Environment.ContentRootPath,
        "Firebase",
        "factorymanagementsystem-1ea9a-firebase-adminsdk-fbsvc-07261a7548.json");

    credential = GoogleCredential.FromFile(firebasePath);
}
else
{
    var firebaseJson = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT");

    if (string.IsNullOrWhiteSpace(firebaseJson))
        throw new Exception("FIREBASE_SERVICE_ACCOUNT environment variable is missing.");

    credential = GoogleCredential.FromJson(firebaseJson);
}

FirebaseApp.Create(new AppOptions
{
    Credential = credential
});

builder.Services.AddSingleton(provider =>
{
    var client = new FirestoreClientBuilder
    {
        Credential = credential
    }.Build();

    return FirestoreDb.Create("factorymanagementsystem-1ea9a", client);
});

builder.Services.AddSingleton<FirestoreService>();
builder.Services.AddSingleton<SummaryService>();

// =====================================================
// Services
// =====================================================

builder.Services.AddControllers();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutter", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// =====================================================
// Middleware
// =====================================================

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseCors("AllowFlutter");

app.UseAuthorization();

// Test Endpoint
app.MapGet("/", () => "Factory Management API Running");
app.MapGet("/test", () => "OK");

app.MapControllers();

app.Run();