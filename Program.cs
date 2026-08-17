using FactoryManagementSystem.Data;
using FactoryManagementSystem.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FirestoreService>();
builder.Services.AddSingleton<SummaryService>();
builder.Services.AddSingleton<LineStrengthReportService>();
builder.Services.AddSingleton<CompanyApiClient>();
builder.Services.AddSingleton<EmployeeSyncService>();
builder.Services.AddSingleton<EmployeeReplacementService>();

// =====================================================
// Authentication / Authorization
// =====================================================

var jwtTokenService = new JwtTokenService(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(jwtTokenService);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtTokenService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtTokenService.SigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

builder.Services.AddAuthorization();

// =====================================================
// Services
// =====================================================

// Every endpoint requires a valid JWT by default; controllers/actions opt
// out with [AllowAnonymous] (e.g. AuthController's login/bootstrap).
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new AuthorizeFilter());
});

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

app.UseAuthentication();
app.UseAuthorization();

// Test Endpoint
app.MapGet("/", () => "Factory Management API Running");
app.MapGet("/test", () => "OK");

app.MapControllers();

app.Run();