using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRequirementRevision> RequirementRevisions => Set<CampaignRequirementRevision>();
    public DbSet<RequirementItem> RequirementItems => Set<RequirementItem>();
    public DbSet<PlatformProfile> PlatformProfiles => Set<PlatformProfile>();
    public DbSet<SourceAsset> SourceAssets => Set<SourceAsset>();
    public DbSet<CampaignDocument> CampaignDocuments => Set<CampaignDocument>();
    public DbSet<ReferenceVideo> ReferenceVideos => Set<ReferenceVideo>();
    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<AiAnalysisRun> AiAnalysisRuns => Set<AiAnalysisRun>();
    public DbSet<HighlightCandidate> HighlightCandidates => Set<HighlightCandidate>();
    public DbSet<EditPlan> EditPlans => Set<EditPlan>();
    public DbSet<MediaLibraryLocation> MediaLibraryLocations => Set<MediaLibraryLocation>();
    public DbSet<MediaLibraryAsset> MediaLibraryAssets => Set<MediaLibraryAsset>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("clipper");
        modelBuilder.Entity<Campaign>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<Campaign>().Property(x => x.PayoutAmount).HasPrecision(18, 4);
        modelBuilder.Entity<Campaign>().HasIndex(x => x.Status);
        modelBuilder.Entity<CampaignRequirementRevision>().HasIndex(x => new { x.CampaignId, x.Version }).IsUnique();
        modelBuilder.Entity<CampaignRequirementRevision>().HasMany(x => x.Items).WithOne(x => x.Revision).HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Campaign>().HasMany(x => x.RequirementRevisions).WithOne(x => x.Campaign).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Campaign>().HasMany(x => x.PlatformProfiles).WithOne(x => x.Campaign).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Campaign>().HasMany(x => x.Sources).WithOne(x => x.Campaign).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Campaign>().HasMany(x => x.Documents).WithOne(x => x.Campaign).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Campaign>().HasMany(x => x.ReferenceVideos).WithOne(x => x.Campaign).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CampaignDocument>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<CampaignDocument>().Property(x => x.ExtractionStatus).HasConversion<string>();
        modelBuilder.Entity<CampaignDocument>().HasIndex(x => new { x.CampaignId, x.ContentHash });
        modelBuilder.Entity<ReferenceVideo>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<ReferenceVideo>().HasIndex(x => new { x.CampaignId, x.IsPrimary });
        modelBuilder.Entity<PlatformProfile>().Property(x => x.Platform).HasConversion<string>();
        modelBuilder.Entity<PlatformProfile>().HasIndex(x => new { x.CampaignId, x.Platform }).IsUnique();
        modelBuilder.Entity<JobRecord>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<JobRecord>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<JobRecord>().HasIndex(x => new { x.Status, x.CreatedAt });
        modelBuilder.Entity<AiAnalysisRun>().Property(x => x.Mode).HasConversion<string>();
        modelBuilder.Entity<AiAnalysisRun>().HasMany(x => x.Highlights).WithOne(x => x.AnalysisRun).HasForeignKey(x => x.AiAnalysisRunId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EditPlan>().Property(x => x.Platform).HasConversion<string>();
        modelBuilder.Entity<MediaLibraryLocation>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<MediaLibraryAsset>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<MediaLibraryAsset>().HasIndex(x => new { x.LocationId, x.FullPath }).IsUnique();
        modelBuilder.Entity<ApplicationSetting>().HasKey(x => x.Key);

        // Npgsql refuses to write a DateTimeOffset with a non-zero offset into
        // `timestamp with time zone`. A deadline entered in the UI arrives with the
        // machine's local offset, so normalize every DateTimeOffset to UTC on write
        // rather than relying on each call site to remember.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties()
                .Where(x => x.ClrType == typeof(DateTimeOffset) || x.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(UtcDateTimeOffsetConverter);
            }
        }
    }

    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> UtcDateTimeOffsetConverter =
        new(value => value.ToUniversalTime(), value => value);
}

public sealed class AppDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Postgres before running EF Core design-time commands.");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AppDbContext(options);
    }
}
