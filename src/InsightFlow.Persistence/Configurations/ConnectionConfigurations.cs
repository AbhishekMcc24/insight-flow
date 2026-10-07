using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InsightFlow.Persistence.Configurations;

internal sealed class SecretReferenceConverter() : ValueConverter<SecretReference, string>(v => v.Name, v => Parse(v))
{
    private static SecretReference Parse(string value) =>
        SecretReference.TryParse(value, out var reference) ? reference : throw new InvalidOperationException("Invalid secret reference in database.");
}

internal sealed class ConnectionProfileConfiguration : IEntityTypeConfiguration<ConnectionProfile>
{
    public void Configure(EntityTypeBuilder<ConnectionProfile> builder)
    {
        builder.ToTable("connection_profiles");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(ConnectionProfile.MaxNameLength).IsRequired();
        builder.Property(c => c.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property(c => c.Secret).HasConversion(new SecretReferenceConverter()).HasMaxLength(127);
        builder.Property(c => c.Settings)
            .HasColumnType("jsonb")
            .HasConversion(
                new JsonValueConverter<IReadOnlyDictionary<string, string>>(PersistenceJsonContext.Default.IReadOnlyDictionaryStringString),
                new JsonValueComparer<IReadOnlyDictionary<string, string>>(PersistenceJsonContext.Default.IReadOnlyDictionaryStringString));
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.Name });
    }
}

internal sealed class ExtractDefinitionConfiguration : IEntityTypeConfiguration<ExtractDefinition>
{
    public void Configure(EntityTypeBuilder<ExtractDefinition> builder)
    {
        builder.ToTable("extract_definitions");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Name).IsRequired();
        builder.Property(d => d.SourceTable).HasMaxLength(512);
        builder.Property(d => d.CreatedBy).HasMaxLength(256).IsRequired();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConnectionProfile>().WithMany().HasForeignKey(d => d.ConnectionProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(d => d.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Folder>().WithMany().HasForeignKey(d => d.TargetFolderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DatasetVersion>().WithMany().HasForeignKey(d => d.LatestVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_extract_definitions_one_source",
            "(connection_profile_id IS NOT NULL AND stored_file_id IS NULL) OR (connection_profile_id IS NULL AND stored_file_id IS NOT NULL)"));
    }
}

internal sealed class ExtractRunConfiguration : IEntityTypeConfiguration<ExtractRun>
{
    public void Configure(EntityTypeBuilder<ExtractRun> builder)
    {
        builder.ToTable("extract_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.RequestedBy).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Error).HasMaxLength(ExtractRun.MaxErrorLength);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExtractDefinition>().WithMany().HasForeignKey(r => r.DefinitionId).OnDelete(DeleteBehavior.Cascade);

        // The Worker claims work in request order across tenants.
        builder.HasIndex(r => new { r.Status, r.RequestedAt });
        builder.HasIndex(r => new { r.TenantId, r.DefinitionId, r.RequestedAt });
    }
}
