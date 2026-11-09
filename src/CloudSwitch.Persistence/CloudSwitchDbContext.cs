using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSwitch.Persistence.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudSwitch.Persistence;

public sealed class CloudSwitchDbContext(DbContextOptions<CloudSwitchDbContext> options) : DbContext(options)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public DbSet<User> Users => Set<User>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<TargetConfiguration> Targets => Set<TargetConfiguration>();
    public DbSet<DeploymentRuntime> Runtimes => Set<DeploymentRuntime>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<HealthObservation> Health => Set<HealthObservation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var users = model.Entity<User>();
        users.HasKey(x => x.Id);
        users.Property(x => x.Issuer).HasMaxLength(512);
        users.Property(x => x.Subject).HasMaxLength(512);
        users.Property(x => x.Username).HasMaxLength(256);
        users.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();

        var deployments = model.Entity<Deployment>();
        deployments.HasKey(x => x.Id);
        deployments.HasAlternateKey(x => new { x.Id, x.UserId });
        deployments.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        deployments.Property(x => x.Revision).IsConcurrencyToken();
        deployments.HasIndex(x => new { x.UserId, x.CreatedAt });
        Json(deployments.Property(x => x.Definition));

        var targets = model.Entity<TargetConfiguration>();
        targets.HasKey(x => x.Id);
        targets.HasAlternateKey(x => new { x.Id, x.DeploymentId });
        targets.HasOne<Deployment>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
        targets.Property(x => x.Revision).IsConcurrencyToken();
        Json(targets.Property(x => x.Configuration));

        var runtime = model.Entity<DeploymentRuntime>();
        runtime.HasKey(x => x.DeploymentId);
        runtime.HasOne<Deployment>().WithOne().HasForeignKey<DeploymentRuntime>(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
        runtime.Property(x => x.Endpoint).HasConversion(uri => uri.ToString(), value => new Uri(value));

        var jobs = model.Entity<Job>();
        jobs.HasKey(x => x.Id);
        jobs.HasOne<Deployment>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.UserId })
            .HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        jobs.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        jobs.HasOne<TargetConfiguration>().WithMany().HasForeignKey(x => new { x.TargetConfigurationId, x.DeploymentId })
            .HasPrincipalKey(x => new { x.Id, x.DeploymentId }).OnDelete(DeleteBehavior.Restrict);
        jobs.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        jobs.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        jobs.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(16);
        jobs.Property(x => x.Phase).HasConversion<string>().HasMaxLength(32);
        jobs.Property(x => x.Cutover).HasConversion<string>().HasMaxLength(16);
        jobs.Property(x => x.IdempotencyKey).HasMaxLength(128);
        Json(jobs.Property(x => x.Checkpoints));
        jobs.Property(x => x.TargetEndpoint).HasConversion(uri => uri == null ? null : uri.ToString(), value => value == null ? null : new Uri(value));
        Json(jobs.Property(x => x.Request));
        jobs.HasIndex(x => x.DeploymentId).IsUnique().HasDatabaseName("UX_Jobs_ActiveDeployment")
            .HasFilter("\"Status\" IN ('Pending', 'Running')");
        jobs.HasIndex(x => new { x.DeploymentId, x.IdempotencyKey }).IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        jobs.HasIndex(x => new { x.DeploymentId, x.IncidentId }).IsUnique()
            .HasFilter("\"Trigger\" = 'Automatic'");
        jobs.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
        jobs.HasIndex(x => new { x.UserId, x.CreatedAt });
        jobs.ToTable(table => table.HasCheckConstraint("CK_Jobs_Trigger",
            "(\"Trigger\" = 'Manual' AND \"IdempotencyKey\" IS NOT NULL AND \"IncidentId\" IS NULL) OR (\"Trigger\" = 'Automatic' AND \"IncidentId\" IS NOT NULL AND \"IdempotencyKey\" IS NULL)"));
        jobs.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Jobs_Status", "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed')");
            table.HasCheckConstraint("CK_Jobs_Counters", "\"Attempt\" >= 0 AND \"Sequence\" >= 0 AND \"FencingToken\" >= 0");
        });

        var events = model.Entity<JobEvent>();
        events.HasKey(x => x.EventId);
        events.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        events.HasIndex(x => new { x.JobId, x.Sequence }).IsUnique();
        events.ToTable(table => table.HasCheckConstraint("CK_JobEvents_Sequence", "\"Sequence\" > 0"));
        Json(events.Property(x => x.Event));

        var incidents = model.Entity<Incident>();
        incidents.HasKey(x => x.Id);
        incidents.HasOne<Deployment>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
        incidents.HasIndex(x => x.DeploymentId).IsUnique().HasFilter("\"ResolvedAt\" IS NULL");

        var health = model.Entity<HealthObservation>();
        health.HasKey(x => x.DeploymentId);
        health.HasOne<Deployment>().WithOne().HasForeignKey<HealthObservation>(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
        health.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        health.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        health.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Health_Status", "\"Status\" IN ('Unknown', 'Healthy', 'Unhealthy') AND (\"Status\" <> 'Unhealthy' OR \"IncidentId\" IS NOT NULL)");
            table.HasCheckConstraint("CK_Health_Counters", "\"Sequence\" > 0 AND \"ConsecutiveFailures\" >= 0 AND \"ConsecutiveSuccesses\" >= 0");
        });

        model.AddInboxStateEntity();
        model.AddOutboxMessageEntity();
        model.AddOutboxStateEntity();
    }

    private static void Json<T>(PropertyBuilder<T> property) where T : class => property.HasColumnType("jsonb")
        .HasConversion(value => JsonSerializer.Serialize(value, JsonOptions), value => DeserializeRequired<T>(value));

    private static T DeserializeRequired<T>(string value) where T : class
        => JsonSerializer.Deserialize<T>(value, JsonOptions)
            ?? throw new JsonException($"Stored {typeof(T).Name} must not be null.");
}
