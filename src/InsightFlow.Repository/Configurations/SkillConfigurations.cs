using InsightFlow.Domain.Skills;
using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Repository.Configurations;

internal sealed class SkillCategoryConfiguration : IEntityTypeConfiguration<SkillCategory>
{
    public void Configure(EntityTypeBuilder<SkillCategory> builder)
    {
        builder.ToTable("skill_categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(SkillCategory.MaxNameLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(SkillCategory.MaxDescriptionLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.Name }).IsUnique().HasDatabaseName("ux_skill_categories_name");
    }
}

internal sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("skills");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(SkillVersion.MaxNameLength).IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SkillCategory>().WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.TenantId, s.CategoryId }).HasDatabaseName("ix_skills_category");
    }
}

internal sealed class SkillVersionConfiguration : IEntityTypeConfiguration<SkillVersion>
{
    public void Configure(EntityTypeBuilder<SkillVersion> builder)
    {
        builder.ToTable("skill_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Name).HasMaxLength(SkillVersion.MaxNameLength).IsRequired();
        builder.Property(v => v.Description).HasMaxLength(SkillVersion.MaxDescriptionLength).IsRequired();
        builder.Property(v => v.License).HasMaxLength(SkillVersion.MaxShortLength);
        builder.Property(v => v.Compatibility).HasMaxLength(SkillVersion.MaxCompatibilityLength);
        builder.Property(v => v.Disclosure).HasConversion<string>().HasMaxLength(32);
        builder.Property(v => v.Instructions).IsRequired();
        builder.Property(v => v.Note).HasMaxLength(SkillVersion.MaxNoteLength);
        builder.Property(v => v.CreatedBy).HasMaxLength(128);
        builder.Ignore(v => v.Label);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Skill>().WithMany().HasForeignKey(v => v.SkillId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(v => new { v.SkillId, v.Number }).IsUnique().HasDatabaseName("ux_skill_versions_number");
    }
}
