using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Persistence.Configurations;

/// <summary>
/// Workspace tree tables. Name uniqueness is enforced by the database (case-insensitive via a stored
/// <c>name_key = lower(name)</c> column), so concurrent uploads of the same name cannot both succeed.
/// </summary>
internal sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    internal const string NameKey = "NameKey";

    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("folders");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Name).IsRequired();
        builder.Property(f => f.OwnerUserId).HasMaxLength(256);
        builder.Property(f => f.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property<string>(NameKey).HasComputedColumnSql("lower(name)", stored: true);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Folder>().WithMany().HasForeignKey(f => f.ParentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(nameof(Folder.TenantId), nameof(Folder.ParentId), NameKey)
            .IsUnique()
            .HasFilter("deleted_at IS NULL AND parent_id IS NOT NULL")
            .HasDatabaseName("ux_folders_sibling_name");

        builder.HasIndex(f => f.TenantId)
            .IsUnique()
            .HasFilter("parent_id IS NULL AND scope = 'Shared'")
            .HasDatabaseName("ux_folders_shared_root");

        builder.HasIndex(f => new { f.TenantId, f.OwnerUserId })
            .IsUnique()
            .HasFilter("parent_id IS NULL AND scope = 'Personal'")
            .HasDatabaseName("ux_folders_personal_root");
    }
}

internal sealed class ContentItemConfiguration : IEntityTypeConfiguration<ContentItem>
{
    public void Configure(EntityTypeBuilder<ContentItem> builder)
    {
        builder.ToTable("content_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Name).IsRequired();
        builder.Property(i => i.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property<string>(FolderConfiguration.NameKey).HasComputedColumnSql("lower(name)", stored: true);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Folder>().WithMany().HasForeignKey(i => i.FolderId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(nameof(ContentItem.TenantId), nameof(ContentItem.FolderId), FolderConfiguration.NameKey)
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_content_items_sibling_name");

        builder.HasIndex(i => new { i.TenantId, i.Kind, i.TargetId });
    }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.ToTable("stored_files");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.OriginalName).HasMaxLength(ItemName.MaxLength).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(256).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(f => f.BlobPath).HasMaxLength(512).IsRequired();
        builder.Property(f => f.CreatedBy).HasMaxLength(256).IsRequired();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(f => new { f.TenantId, f.Sha256 });
    }
}
