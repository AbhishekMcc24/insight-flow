using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InsightFlow.Repository.Configurations;

internal sealed class KnowledgeBaseConfigConfiguration : IEntityTypeConfiguration<KnowledgeBaseConfig>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseConfig> builder)
    {
        builder.ToTable("knowledge_base_configs");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Uri).HasMaxLength(KnowledgeBaseConfig.MaxUriLength).IsRequired();
        builder.Property(c => c.User).HasMaxLength(KnowledgeBaseConfig.MaxUserLength).IsRequired();
        builder.Property(c => c.Database).HasMaxLength(KnowledgeBaseConfig.MaxDatabaseLength).IsRequired();
        builder.Property(c => c.PasswordSecretName).HasMaxLength(128);
        builder.Property(c => c.LastTestMessage).HasMaxLength(KnowledgeBaseConfig.MaxMessageLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => c.TenantId).IsUnique().HasDatabaseName("ux_knowledge_base_configs_tenant");
    }
}

internal sealed class KnowledgeTableConfiguration : IEntityTypeConfiguration<KnowledgeTable>
{
    public void Configure(EntityTypeBuilder<KnowledgeTable> builder)
    {
        builder.ToTable("knowledge_tables");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(KnowledgeTable.MaxNameLength).IsRequired();
        builder.Property(t => t.SchemaName).HasMaxLength(KnowledgeTable.MaxSchemaLength);
        builder.Property(t => t.DatabaseName).HasMaxLength(KnowledgeTable.MaxSchemaLength);
        builder.Property(t => t.Description).HasMaxLength(KnowledgeTable.MaxDescriptionLength);
        builder.Property(t => t.Source).HasConversion<string>().HasMaxLength(32);
        builder.Ignore(t => t.DisplayName);
        builder.Ignore(t => t.IsStructureLocked);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.TenantId, t.ConnectionId, t.DatabaseName, t.SchemaName, t.Name })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_knowledge_tables_identity");
    }
}

internal sealed class KnowledgeColumnConfiguration : IEntityTypeConfiguration<KnowledgeColumn>
{
    public void Configure(EntityTypeBuilder<KnowledgeColumn> builder)
    {
        builder.ToTable("knowledge_columns");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(KnowledgeColumn.MaxNameLength).IsRequired();
        builder.Property(c => c.DataType).HasMaxLength(KnowledgeColumn.MaxTypeLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(KnowledgeColumn.MaxDescriptionLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<KnowledgeTable>().WithMany().HasForeignKey(c => c.TableId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.TableId, c.Name }).IsUnique().HasDatabaseName("ux_knowledge_columns_name");
    }
}

internal sealed class KnowledgeRelationshipConfiguration : IEntityTypeConfiguration<KnowledgeRelationship>
{
    public void Configure(EntityTypeBuilder<KnowledgeRelationship> builder)
    {
        builder.ToTable("knowledge_relationships");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Source).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.Reason).HasMaxLength(KnowledgeRelationship.MaxReasonLength).IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<KnowledgeColumn>().WithMany().HasForeignKey(r => r.FromColumnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeColumn>().WithMany().HasForeignKey(r => r.ToColumnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.FromColumnId, r.ToColumnId }).IsUnique().HasDatabaseName("ux_knowledge_relationships_pair");
        builder.HasIndex(r => r.GroupId).HasDatabaseName("ix_knowledge_relationships_group");
    }
}

internal sealed class KnowledgeDatabaseConfiguration : IEntityTypeConfiguration<KnowledgeDatabase>
{
    public void Configure(EntityTypeBuilder<KnowledgeDatabase> builder)
    {
        builder.ToTable("knowledge_databases");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Name).HasMaxLength(KnowledgeDatabase.MaxNameLength).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(KnowledgeDatabase.MaxDescriptionLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.TenantId, d.ConnectionId, d.Name }).IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_knowledge_databases_identity");
    }
}

internal sealed class KnowledgeProcedureConfiguration : IEntityTypeConfiguration<KnowledgeProcedure>
{
    public void Configure(EntityTypeBuilder<KnowledgeProcedure> builder)
    {
        builder.ToTable("knowledge_procedures");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.DatabaseName).HasMaxLength(KnowledgeDatabase.MaxNameLength).IsRequired();
        builder.Property(p => p.SchemaName).HasMaxLength(KnowledgeTable.MaxSchemaLength);
        builder.Property(p => p.Name).HasMaxLength(KnowledgeProcedure.MaxNameLength).IsRequired();
        builder.Property(p => p.Definition).IsRequired();
        builder.Property(p => p.TableIds).HasColumnType("uuid[]");
        builder.Ignore(p => p.DisplayName);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.TenantId).HasDatabaseName("ix_knowledge_procedures_tenant");
    }
}

internal sealed class QueryFeedbackConfiguration : IEntityTypeConfiguration<QueryFeedback>
{
    public void Configure(EntityTypeBuilder<QueryFeedback> builder)
    {
        builder.ToTable("query_feedback");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.UserId).HasMaxLength(200).IsRequired();
        builder.Property(f => f.Question).HasMaxLength(QueryFeedback.MaxQuestionLength).IsRequired();
        builder.Property(f => f.Sql).HasMaxLength(QueryFeedback.MaxSqlLength);
        builder.Property(f => f.Reason).HasMaxLength(QueryFeedback.MaxReasonLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(f => new { f.TenantId, f.CreatedAt }).HasDatabaseName("ix_query_feedback_tenant_created");
    }
}
