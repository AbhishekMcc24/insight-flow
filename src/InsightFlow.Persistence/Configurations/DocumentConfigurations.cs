using InsightFlow.Domain.Documents;
using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Persistence.Configurations;

internal sealed class DocumentIngestRunConfiguration : IEntityTypeConfiguration<DocumentIngestRun>
{
    public void Configure(EntityTypeBuilder<DocumentIngestRun> builder)
    {
        builder.ToTable("document_ingest_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.RequestedBy).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.ItemIds).HasColumnType("uuid[]");
        builder.Property(r => r.Error).HasMaxLength(1_000);
        builder.Property(r => r.Steps)
            .HasColumnType("jsonb")
            .HasConversion(
                new JsonValueConverter<IReadOnlyList<IngestStep>>(PersistenceJsonContext.Default.IReadOnlyListIngestStep),
                new JsonValueComparer<IReadOnlyList<IngestStep>>(PersistenceJsonContext.Default.IReadOnlyListIngestStep));
        builder.Property(r => r.Problems)
            .HasColumnType("jsonb")
            .HasConversion(
                new JsonValueConverter<IReadOnlyList<IngestProblem>>(PersistenceJsonContext.Default.IReadOnlyListIngestProblem),
                new JsonValueComparer<IReadOnlyList<IngestProblem>>(PersistenceJsonContext.Default.IReadOnlyListIngestProblem));

        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.RequestedAt }).HasDatabaseName("ix_document_ingest_runs_tenant_requested");
        builder.HasIndex(r => r.Status).HasDatabaseName("ix_document_ingest_runs_status");
    }
}

internal sealed class IndexedSheetConfiguration : IEntityTypeConfiguration<IndexedSheet>
{
    public void Configure(EntityTypeBuilder<IndexedSheet> builder)
    {
        builder.ToTable("indexed_sheets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.FileName).HasMaxLength(512).IsRequired();
        builder.Property(s => s.SheetName).HasMaxLength(IndexedSheet.MaxNameLength).IsRequired();
        builder.Property(s => s.TableName).HasMaxLength(128).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.Note).HasMaxLength(500);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.TenantId, s.ContentItemId }).HasDatabaseName("ix_indexed_sheets_item");
    }
}
