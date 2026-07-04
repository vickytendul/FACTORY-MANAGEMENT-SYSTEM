using FactoryManagementSystem.Data;
using FactoryManagementSystem.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var firebasePath = Path.Combine(
    builder.Environment.ContentRootPath,
    "Firebase",
    "factorymanagementsystem-1ea9a-firebase-adminsdk-fbsvc-07261a7548.json");

FirebaseApp.Create(new AppOptions
{
    Credential = GoogleCredential.FromFile(firebasePath)
});

builder.Services.AddSingleton(provider =>
{
    var credential = GoogleCredential.FromFile(firebasePath);

    var client = new FirestoreClientBuilder
    {
        Credential = credential
    }.Build();

    return FirestoreDb.Create("factorymanagementsystem-1ea9a", client);
});
builder.Services.AddSingleton<FirestoreService>();

// Add services to the container.
builder.Services.AddControllers();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Enable CORS
app.UseCors("AllowFlutter");

app.UseAuthorization();

app.MapControllers();

app.Run();