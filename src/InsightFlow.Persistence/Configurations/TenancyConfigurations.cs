using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tenant.MaxNameLength).IsRequired();
    }
}

internal sealed class SemanticModelConfiguration : IEntityTypeConfiguration<SemanticModelRecord>
{
    public void Configure(EntityTypeBuilder<SemanticModelRecord> builder)
    {
        builder.ToTable("semantic_models");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Definition)
            .HasColumnType("jsonb")
            .HasConversion(
                new JsonValueConverter<SemanticModelDefinition>(PersistenceJsonContext.Default.SemanticModelDefinition),
                new JsonValueComparer<SemanticModelDefinition>(PersistenceJsonContext.Default.SemanticModelDefinition));
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.TenantId, m.Name });
    }
}
