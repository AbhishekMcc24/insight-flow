using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InsightFlow.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without running a host. The connection string is never
/// opened when adding migrations; to apply them locally, run the AppHost (the migration service does it).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<InsightFlowDbContext>
{
    public InsightFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InsightFlowDbContext>()
            .UseNpgsql("Host=localhost;Database=insightflow_design_time", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .Options;
        return new InsightFlowDbContext(options, SystemCurrentTenant.Instance);
    }
}
