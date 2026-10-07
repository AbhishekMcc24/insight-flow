using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Viz;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Persistence.Configurations;

internal sealed class DatasetVersionConfiguration : IEntityTypeConfiguration<DatasetVersion>
{
    public void Configure(EntityTypeBuilder<DatasetVersion> builder)
    {
        builder.ToTable("dataset_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.ParentIds)
            .HasColumnType("uuid[]")
            .HasConversion(new GuidListConverter(), new GuidListComparer())
            .IsRequired();
        builder.Property(v => v.ParquetPath).HasMaxLength(512).IsRequired();
        builder.Property(v => v.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property(v => v.Schema)
            .HasColumnType("jsonb")
            .HasConversion(
                new JsonValueConverter<DatasetSchema>(PersistenceJsonContext.Default.DatasetSchema),
                new JsonValueComparer<DatasetSchema>(PersistenceJsonContext.Default.DatasetSchema));

        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(v => new { v.TenantId, v.CreatedAt });

        // Children lookup ("what was derived from X?") scans parent_ids with a GIN index.
        builder.HasIndex(v => v.ParentIds).HasMethod("gin");
    }
}

internal sealed class DataThreadConfiguration : IEntityTypeConfiguration<DataThread>
{
    public void Configure(EntityTypeBuilder<DataThread> builder)
    {
        builder.ToTable("data_threads");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Title).HasMaxLength(DataThread.MaxTitleLength).IsRequired();
        builder.Property(t => t.CreatedBy).HasMaxLength(256).IsRequired();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DatasetVersion>().WithMany().HasForeignKey(t => t.RootVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.TenantId, t.CreatedAt });
    }
}

internal sealed class ThreadNodeConfiguration : IEntityTypeConfiguration<ThreadNode>
{
    public void Configure(EntityTypeBuilder<ThreadNode> builder)
    {
        builder.ToTable("thread_nodes");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property(n => n.VizSpec!)
            .HasColumnType("jsonb")
            .HasConversion(
                new JsonValueConverter<VizSpec>(VizJsonContext.Default.VizSpec),
                new JsonValueComparer<VizSpec>(VizJsonContext.Default.VizSpec));

        builder.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DataThread>().WithMany().HasForeignKey(n => n.ThreadId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ThreadNode>().WithMany().HasForeignKey(n => n.ParentNodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DatasetVersion>().WithMany().HasForeignKey(n => n.DatasetVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(n => new { n.TenantId, n.ThreadId });
    }
}
