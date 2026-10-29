using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using InsightFlow.Logic.Admin;
using InsightFlow.Logic.Documents;
using InsightFlow.Logic.Knowledge;
using InsightFlow.Logic.Playground;
using InsightFlow.Logic.Skills;
using InsightFlow.Logic.Workspace;

namespace InsightFlow.Logic.DependencyInjection;

/// <summary>Registers the application services of the Logic layer, so a host adds them with one call.</summary>
public static class LogicServiceCollectionExtensions
{
    public static IServiceCollection AddInsightFlowLogic(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UploadOptions>()
            .Bind(configuration.GetSection(UploadOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .PostConfigure(o => o.StorageRoot = LocalStorage.ChooseRoot(
                configuration[LocalStorage.RootConfigurationKey],
                configuration.GetConnectionString(LocalStorage.ConnectionStringName)));
        services.AddSingleton<IFileStore, DirectoryFileStore>();
        services.AddSingleton<IUploadScanner, NoOpUploadScanner>();
        services.AddSingleton<IContentPermissionEvaluator, ContentPermissionEvaluator>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<AdminService>();
        services.AddScoped<KnowledgeService>();
        services.AddScoped<KnowledgeSourceService>();
        services.AddScoped<SkillService>();
        services.AddScoped<PlaygroundService>();
        return services;
    }
}
