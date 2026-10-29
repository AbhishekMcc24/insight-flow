using InsightFlow.Domain.Admin;
using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Repository.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.UserName).HasMaxLength(AppUser.MaxUserNameLength).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(AppUser.MaxDisplayNameLength).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(AppUser.MaxEmailLength);
        builder.Property(u => u.PasswordHash).HasMaxLength(512);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(u => new { u.TenantId, u.UserName }).IsUnique().HasDatabaseName("ux_app_users_user_name");
    }
}

internal sealed class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> builder)
    {
        builder.ToTable("menu_items");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Name).HasMaxLength(MenuItem.MaxNameLength).IsRequired();
        builder.Property(m => m.Icon).HasMaxLength(MenuItem.MaxIconLength);
        builder.Property(m => m.Route).HasMaxLength(MenuItem.MaxRouteLength);
        builder.Property(m => m.SystemKey).HasMaxLength(64);
        builder.Ignore(m => m.IsSystem);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MenuItem>().WithMany().HasForeignKey(m => m.ParentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.TenantId, m.ParentId, m.Sequence });
        builder.HasIndex(m => new { m.TenantId, m.SystemKey })
            .IsUnique()
            .HasFilter("system_key IS NOT NULL")
            .HasDatabaseName("ux_menu_items_system_key");
    }
}

internal sealed class UserMenuAccessConfiguration : IEntityTypeConfiguration<UserMenuAccess>
{
    public void Configure(EntityTypeBuilder<UserMenuAccess> builder)
    {
        builder.ToTable("user_menu_access");
        builder.HasKey(a => new { a.UserId, a.MenuItemId });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MenuItem>().WithMany().HasForeignKey(a => a.MenuItemId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.TenantId);
    }
}
