using Microsoft.EntityFrameworkCore;
using WorkstationGame.Api.Data;
using WorkstationGame.Api.Endpoints;
using WorkstationGame.Api.Hubs;
using WorkstationGame.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();
var connectionString = ConnectionStringResolver.Resolve(builder.Configuration);

builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure(3)));
builder.Services.AddScoped<AnswerService>();
builder.Services.AddSingleton<GameNotifier>();
builder.Services.AddHttpClient<SupabaseStorageService>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

var origins = (builder.Configuration["Game:AllowedOrigins"] ?? "http://localhost:5173")
    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<GameDbContext>();
    await database.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Ok(new { name = "Desk Detective API", status = "ready" }));
app.MapHealthChecks("/health");
app.MapHub<GameHub>("/hubs/game");
app.MapGameEndpoints();
app.MapHostEndpoints();

app.Run();

public partial class Program;

