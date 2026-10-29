using InsightFlow.Domain.Playground;
using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Repository.Configurations;

internal sealed class PlaygroundSheetConfiguration : IEntityTypeConfiguration<PlaygroundSheet>
{
    public void Configure(EntityTypeBuilder<PlaygroundSheet> builder)
    {
        builder.ToTable("playground_sheets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.OwnerId).HasMaxLength(128).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(PlaygroundSheet.MaxNameLength).IsRequired();
        builder.Property(s => s.ElementsJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.TenantId, s.OwnerId, s.Sequence }).HasDatabaseName("ix_playground_sheets_owner");
    }
}
