using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Endpoints;
using WhopClipper.Api.App.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(LocalSettingsFile.Path, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "whop-clipper-.log"), rollingInterval: RollingInterval.Day));

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("WhopClipper"));
builder.Services.Configure<PublishingApiOptions>(builder.Configuration.GetSection("PublishingApis"));
var protectionKeyRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhopClipper", "keys");
Directory.CreateDirectory(protectionKeyRoot);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("WhopClipper")
    .PersistKeysToFileSystem(new DirectoryInfo(protectionKeyRoot));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: false);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString,
    postgres => postgres.EnableRetryOnFailure(3)));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Whop Clipping Assistant API", Version = "v1" });
    options.SupportNonNullableReferenceTypes();
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddValidatorsFromAssemblyContaining<CreateCampaignValidator>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<DiskSpaceGuard>();
builder.Services.AddSingleton<SettingsRuntimeState>();
builder.Services.AddSingleton<IProcessRunner, SafeProcessRunner>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<IRequirementExtractor, HeuristicRequirementExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddSingleton<ReferenceStyleAnalyzer>();
builder.Services.AddSingleton<SubjectTracker>();
builder.Services.AddSingleton<HookCardRenderer>();
builder.Services.AddSingleton<ClipStylist>();
builder.Services.AddSingleton<BriefLinkDetector>();
builder.Services.AddScoped<CampaignDocumentService>();
builder.Services.AddScoped<CampaignPageFetcher>();
// Redirects are followed by hand inside the fetcher so each hop is re-checked against the
// private-address guard; letting the handler follow them would check only the typed address.
builder.Services.AddHttpClient(CampaignPageFetcher.ClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.MaxResponseContentBufferSize = CampaignPageFetcher.MaxPageBytes;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    });
builder.Services.AddScoped<CampaignBrandingService>();
builder.Services.AddScoped<LocalSourceScanner>();
builder.Services.AddScoped<RequirementService>();
builder.Services.AddScoped<JobCoordinator>();
builder.Services.AddScoped<JobExecutionService>();
builder.Services.AddScoped<MediaLibraryService>();
builder.Services.AddScoped<DependencyService>();
builder.Services.AddSingleton<DatabaseInitializationState>();
builder.Services.AddScoped<DatabaseReadinessService>();
builder.Services.AddSingleton<LocalPublishingRequestGuard>();
builder.Services.AddSingleton<YouTubeOAuthStateStore>();
builder.Services.AddSingleton<IPublishingCredentialVault, PublishingCredentialVault>();
builder.Services.AddScoped<PlatformAccountKitService>();
builder.Services.AddScoped<PlatformConnectionService>();
builder.Services.AddScoped<PublishingMediaValidator>();
builder.Services.AddScoped<PublishJobService>();
builder.Services.AddScoped<YouTubePublisher>();
builder.Services.AddScoped<InstagramPublisher>();
builder.Services.AddScoped<IPlatformPublisher>(services => services.GetRequiredService<YouTubePublisher>());
builder.Services.AddScoped<IPlatformConnectionVerifier>(services => services.GetRequiredService<YouTubePublisher>());
builder.Services.AddScoped<IPlatformConnectionVerifier>(services => services.GetRequiredService<InstagramPublisher>());
builder.Services.AddScoped<YouTubeOAuthService>();
builder.Services.AddHttpClient("PublishingExternal", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    });
builder.Services.AddSingleton<IAiMediaProvider, ClaudeCliProvider>();
builder.Services.AddSingleton<IAiMediaProvider, CodexCliProvider>();
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<MediaJobWorker>();
builder.Services.AddHostedService<PublishingWorker>();

var allowedOrigins = builder.Configuration.GetSection("WhopClipper:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/api/info", () => Results.Ok(new { name = "Whop Clipping Assistant API", status = "ready", version = "0.1.0" }));
app.MapHealthChecks("/health");
app.MapGet("/health/ready", async (DatabaseReadinessService databaseService, WorkspaceService workspace, CancellationToken cancellationToken) =>
{
    var database = await databaseService.CheckAsync(cancellationToken);
    return database.Ready
        ? Results.Ok(new { status = "ready", database, workspace = workspace.Root })
        : Results.Json(new { status = "degraded", database, workspace = workspace.Root, error = database.Error }, statusCode: 503);
});
app.MapWhopClipperApi();
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = "Frontend bundle is not installed. Build FrontendGame and copy dist into backend wwwroot." });
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

app.Run();

public partial class Program;
