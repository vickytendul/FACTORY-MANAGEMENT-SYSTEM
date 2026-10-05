using FactoryManagementSystem.Services.Attendance;
using FactoryManagementSystem.Services.Ccs;
using FactoryManagementSystem.Services.Departments;
using FactoryManagementSystem.Services.Employees;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Services.Placements;
using FactoryManagementSystem.Services.Skills;
using FactoryManagementSystem.Services.Users;
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

// Keeps the free Render instance from spinning down - see the service.
// Does nothing unless KeepAwake:Url is set.
builder.Services.AddHostedService<KeepAwakeService>();

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
var layoutsSource = (builder.Configuration["Layouts:Source"] ?? "firebase").Trim().ToLowerInvariant();
var ccsSource = (builder.Configuration["CCs:Source"] ?? "firebase").Trim().ToLowerInvariant();
var attendanceSource = (builder.Configuration["Attendance:Source"] ?? "firebase").Trim().ToLowerInvariant();
var usersSource = (builder.Configuration["Users:Source"] ?? "firebase").Trim().ToLowerInvariant();
var employeesSource = (builder.Configuration["Employees:Source"] ?? "firebase").Trim().ToLowerInvariant();

// One shared connection pool, registered when EITHER migration needs it.
// Scoping this to the Skills flag alone would mean Layouts:Source=dual with
// Skills:Source=firebase could not resolve a data source at all.
// Confirmed placements live in Supabase and nowhere else - new data with
// no Firestore history, so no source flag. Its presence is simply whether
// a connection string was configured at all.
var hasSupabase = !string.IsNullOrWhiteSpace(builder.Configuration["Supabase:ConnectionString"]);

if (skillsSource is "supabase" or "dual"
    || layoutsSource is "supabase" or "dual"
    || ccsSource is "supabase" or "dual"
    || attendanceSource is "supabase" or "dual"
    || usersSource is "supabase" or "dual"
    || employeesSource is "supabase" or "dual"
    || (builder.Configuration["LayoutIds:Source"] ?? "").Trim().ToLowerInvariant() == "supabase"
    || hasSupabase)
{
    var supabaseConnection = builder.Configuration["Supabase:ConnectionString"]
        ?? throw new Exception(
            "Supabase:ConnectionString is required when Skills:Source, Layouts:Source, "
            + "CCs:Source, Attendance:Source or Users:Source is 'supabase' or 'dual'.");

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
}

if (skillsSource is "supabase" or "dual")
    builder.Services.AddSingleton<SupabaseSkillRepository>();

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
// Layout records: Firebase or Supabase, chosen SEPARATELY
// =====================================================
//
// Layouts__Source = firebase | dual | supabase   (default firebase)
//
// A DIFFERENT flag from Skills__Source on purpose. The two migrations
// are independent, and coupling them would mean a layout problem could
// only be rolled back by also reverting skills.
//
// dual reads both stores, SERVES FIREBASE, and logs disagreements.
// Every write stays Firebase-authoritative in dual mode - layout writes
// allocate ids through two Firestore transactions and produce their own
// document ids, so mirroring them is not yet proven safe.
//
// The id allocator is registered OUTSIDE the switch and is Firestore-backed
// in every mode, supabase included. LayoutMaster ids and OperationIds stay
// allocated from Counters/LayoutMasterId and Counters/LayoutMasterOperation
// so that an id means the same row in both stores, and so that switching
// back to firebase mode cannot re-issue ids Supabase already handed out.
//
// LayoutIds__Source = firebase | supabase   (default firebase)
//
// The exception above holds while this is firebase, which it is unless
// somebody says otherwise. Set it to supabase only on a deployment that
// has stopped going back - see SupabaseLayoutIdAllocator.
var layoutIdsSource =
    (builder.Configuration["LayoutIds:Source"] ?? "firebase").Trim().ToLowerInvariant();

builder.Services.AddSingleton<FirestoreLayoutIdAllocator>();

if (layoutIdsSource == "supabase")
    builder.Services.AddSingleton<SupabaseLayoutIdAllocator>();

builder.Services.AddSingleton<ILayoutIdAllocator>(sp => layoutIdsSource == "supabase"
    ? sp.GetRequiredService<SupabaseLayoutIdAllocator>()
    : sp.GetRequiredService<FirestoreLayoutIdAllocator>());

// =====================================================
// CC master: Firebase or Supabase, on its OWN flag
// =====================================================
//
// CCs__Source = firebase | dual | supabase   (default firebase)
//
// Separate from Layouts__Source and Skills__Source for the same reason
// those are separate from each other: three independent migrations, three
// independent rollbacks. The CC id keeps coming from the Firestore
// CCCounter in every mode - see ICcRepository.
// =====================================================
// Attendance: Firebase or Supabase, on its OWN flag
// =====================================================
//
// Attendance__Source = firebase | dual | supabase   (default firebase)
//
// Identity is the Firestore document id, in firebase_doc_id, the same
// choice LayoutTransaction made. AttendanceId is 0 on every row and is
// deliberately not stored.
builder.Services.AddSingleton<FirestoreAttendanceRepository>();

builder.Services.AddSingleton<FirestoreCcRepository>();

builder.Services.AddSingleton<FirestoreLayoutRepository>();

// layoutsSource is declared above, beside skillsSource, because the shared
// NpgsqlDataSource registration needs to see both flags.
if (layoutsSource is "supabase" or "dual")
    builder.Services.AddSingleton<SupabaseLayoutRepository>();

if (attendanceSource is "supabase" or "dual")
    builder.Services.AddSingleton<SupabaseAttendanceRepository>();

builder.Services.AddSingleton<IAttendanceRepository>(sp => attendanceSource switch
{
    "supabase" => sp.GetRequiredService<SupabaseAttendanceRepository>(),
    "dual" => new DualReadAttendanceRepository(
        sp.GetRequiredService<FirestoreAttendanceRepository>(),
        sp.GetRequiredService<SupabaseAttendanceRepository>(),
        sp.GetRequiredService<ILogger<DualReadAttendanceRepository>>()),
    _ => sp.GetRequiredService<FirestoreAttendanceRepository>(),
});

if (ccsSource is "supabase" or "dual")
    builder.Services.AddSingleton<SupabaseCcRepository>();

builder.Services.AddSingleton<ICcRepository>(sp => ccsSource switch
{
    "supabase" => sp.GetRequiredService<SupabaseCcRepository>(),
    "dual" => new DualReadCcRepository(
        sp.GetRequiredService<FirestoreCcRepository>(),
        sp.GetRequiredService<SupabaseCcRepository>(),
        sp.GetRequiredService<ILogger<DualReadCcRepository>>()),
    _ => sp.GetRequiredService<FirestoreCcRepository>(),
});

// =====================================================
// Login accounts: Firebase or Supabase, on their OWN flag
// =====================================================
//
// Users__Source = firebase | dual | supabase   (default firebase)
//
// Separate from the other four for the same reason they are separate
// from each other, and more urgently: this is the only store whose
// failure shuts the whole application. If Firestore cannot be read -
// a quota exhausted, a credential expired - nobody can log in and no
// other migration matters, because nobody gets as far as using it.
//
// Identity is the username (the employee code). It was already the
// Firestore document id and it is the primary key in Postgres, so
// there is no firebase_doc_id here: a row means the same account in
// both stores by its own name.
//
// Note that dual mode reads BOTH stores and so costs MORE Firestore
// reads than firebase mode. It is for proving the accounts copied
// across, not for running on.
builder.Services.AddSingleton<FirestoreUserRepository>();

if (usersSource is "supabase" or "dual")
    builder.Services.AddSingleton<SupabaseUserRepository>();

builder.Services.AddSingleton<IUserRepository>(sp => usersSource switch
{
    "supabase" => sp.GetRequiredService<SupabaseUserRepository>(),
    "dual" => new DualReadUserRepository(
        sp.GetRequiredService<FirestoreUserRepository>(),
        sp.GetRequiredService<SupabaseUserRepository>(),
        sp.GetRequiredService<ILogger<DualReadUserRepository>>()),
    _ => sp.GetRequiredService<FirestoreUserRepository>(),
});

// =====================================================
// Employee master: Firebase or Supabase, on its OWN flag
// =====================================================
//
// Employees__Source = firebase | supabase   (default firebase)
//
// No dual mode here. Dual reads both stores and the whole point of this
// one is a roster that is fetched once and filtered in memory - Skill
// Update's search calls it on every keystroke - so reading twice is the
// opposite of what it is for. Rollback is the flag.
//
// Grade is why this store exists. The Company API carries the roster but
// no grade, and grade is what Layout Allocation matches an operator to
// an operation with. The roster can be re-fetched from the vendor any
// time; the grade cannot, because it is only ever entered here.
//
// Only the READS move. Add, update, sync and the audits still write to
// Firestore - they are off-menu admin tools, and moving a write is a
// different risk from moving a read.
builder.Services.AddSingleton<FirestoreEmployeeRepository>();

if (employeesSource is "supabase" or "dual")
    builder.Services.AddSingleton<SupabaseEmployeeRepository>();

builder.Services.AddSingleton<IEmployeeRepository>(sp => employeesSource switch
{
    "supabase" => sp.GetRequiredService<SupabaseEmployeeRepository>(),
    _ => sp.GetRequiredService<FirestoreEmployeeRepository>(),
});

// Supabase only, no flag - see EmployeePlacementRepository. Registered
// only when a connection string exists, so a Firestore-only deployment
// still boots; PlacementsController is the one thing that will not
// resolve there, which is correct, because it has nowhere to read from.
if (hasSupabase)
{
    builder.Services.AddSingleton<EmployeePlacementRepository>();
    builder.Services.AddSingleton<DepartmentLayoutRepository>();
}

builder.Services.AddSingleton<ILayoutRepository>(sp => layoutsSource switch
{
    "supabase" => sp.GetRequiredService<SupabaseLayoutRepository>(),
    "dual" => new DualReadLayoutRepository(
        sp.GetRequiredService<FirestoreLayoutRepository>(),
        sp.GetRequiredService<SupabaseLayoutRepository>(),
        sp.GetRequiredService<ILogger<DualReadLayoutRepository>>()),
    _ => sp.GetRequiredService<FirestoreLayoutRepository>(),
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
// Auth__Required = true | false   (default true)
// =====================================================
//
// false opens the API: no token is asked for, and the app opens straight
// onto the Home screen with no login. Asked for when the Firestore read
// quota ran out and login - a Firestore query - was the one thing
// standing between everybody and the whole application.
//
// It is a flag and not a deletion so it can be put back in one
// environment variable, with the login page, the accounts and the JWT
// plumbing all still here.
//
// Said plainly: with this off, anybody who knows the URL can read and
// write this factory's data over the public internet.
var authRequired = builder.Configuration.GetValue("Auth:Required", true);

if (!authRequired)
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, OpenAccessPolicyProvider>();

// =====================================================
// Services
// =====================================================

// Every endpoint requires a valid JWT by default; controllers/actions opt
// out with [AllowAnonymous] (e.g. AuthController's login/bootstrap).
// With Auth__Required=false the filter is not added at all, and the
// policy provider above satisfies the per-controller [Authorize] besides.
builder.Services.AddControllers(options =>
{
    if (authRequired) options.Filters.Add(new AuthorizeFilter());
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