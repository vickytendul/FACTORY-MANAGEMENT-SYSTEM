using FactoryManagementSystem.Services.Skills;
using Npgsql;
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
builder.Services.AddSingleton<LineAllocationSummaryService>();
builder.Services.AddSingleton<CompanyApiClient>();
builder.Services.AddSingleton<CompanyAttendanceService>();
builder.Services.AddSingleton<ProductionLineService>();
builder.Services.AddSingleton<EmployeeSyncService>();

// =====================================================
// Skill records: Firebase or Supabase, chosen by configuration
// =====================================================
//
// Skills__Source = firebase | dual | supabase   (default firebase)
//
//   firebase - unchanged behaviour, the rollback target
//   dual     - reads both, SERVES FIREBASE, logs any disagreement.
//              Writes go to Firebase only.
//   supabase - Supabase is the source of truth
//
// Firebase data is never deleted by any of these, so switching the flag
// back is a complete rollback for everything except records written while
// Supabase was authoritative.
builder.Services.AddSingleton<FirestoreSkillRepository>();

var skillsSource = (builder.Configuration["Skills:Source"] ?? "firebase").Trim().ToLowerInvariant();

if (skillsSource is "supabase" or "dual")
{
    var supabaseConnection = builder.Configuration["Supabase:ConnectionString"]
        ?? throw new Exception(
            "Supabase:ConnectionString is required when Skills:Source is 'supabase' or 'dual'.");

    builder.Services.AddSingleton(_ =>
    {
        // Supabase hands out a postgresql:// URI; Npgsql wants keywords.
        // Converted here rather than asking whoever sets the environment
        // variable to reshape what the dashboard gave them.
        var connectionString = supabaseConnection;
        if (connectionString.Contains("://"))
        {
            var uri = new Uri(connectionString);
            var userInfo = uri.UserInfo.Split(':', 2);
            connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
                Database = uri.AbsolutePath.Trim('/'),
                SslMode = SslMode.Require,
            }.ConnectionString;
        }
        return NpgsqlDataSource.Create(connectionString);
    });
    builder.Services.AddSingleton<SupabaseSkillRepository>();
}

builder.Services.AddSingleton<ISkillRepository>(sp => skillsSource switch
{
    "supabase" => sp.GetRequiredService<SupabaseSkillRepository>(),
    "dual" => new DualReadSkillRepository(
        sp.GetRequiredService<FirestoreSkillRepository>(),
        sp.GetRequiredService<SupabaseSkillRepository>(),
        sp.GetRequiredService<ILogger<DualReadSkillRepository>>()),
    _ => sp.GetRequiredService<FirestoreSkillRepository>(),
});

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