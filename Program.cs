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
// Hosting / Port Binding
// =====================================================
//
// Render assigns the actual port to listen on via the PORT environment
// variable when the container starts - this varies per deploy/service and
// is never a value we can hard-code. Binding explicitly here (rather than
// relying on a fixed ASPNETCORE_URLS baked into the image) is what lets
// Render's own port scan find the app - a mismatched hard-coded port was
// the previous cause of "Port scan timeout reached, no open ports
// detected." Falls back to 10000 (matching the Dockerfile's EXPOSE and
// prior local-container convention) when PORT isn't set.
//
// Scoped to non-Development only: `dotnet run` locally sets
// ASPNETCORE_ENVIRONMENT=Development via Properties/launchSettings.json,
// which has no PORT variable - if this ran unconditionally it would
// override launchSettings.json's own applicationUrl (localhost:5271/7004)
// and break local development. The Dockerfile's runtime image never sets
// ASPNETCORE_ENVIRONMENT to Development, so this only ever applies to the
// Render/container deployment, exactly where the dynamic PORT matters.
if (!builder.Environment.IsDevelopment())
{
    var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

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

// The Flutter client is either run locally via `flutter run` (a dev
// server on a port Flutter assigns each run, e.g. http://localhost:55957)
// or served as the hosted Flutter Web build at the fixed Firebase Hosting
// origin below. Allowing exactly these origins covers every legitimate
// caller without falling back to AllowAnyOrigin() (which would also
// accept requests from any arbitrary external website).
const string HostedFlutterWebOrigin = "https://factorymanagementsystem-1ea9a.web.app";

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutter", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                (uri.Host == "localhost" || uri.Host == "127.0.0.1" ||
                 origin.Equals(HostedFlutterWebOrigin, StringComparison.OrdinalIgnoreCase)))
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