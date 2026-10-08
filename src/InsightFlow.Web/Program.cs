using InsightFlow.Web.Components;
using InsightFlow.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInsightFlowWebSecurity();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ThemeState>();

// RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the documented way to replace the defaults per client.
#pragma warning disable EXTEXP0001
// Typed clients for the internal services. Base addresses use Aspire service discovery names (see AppHost).
builder.Services.AddScoped<ServiceCallCredentials>();
builder.Services.AddHttpClient<WorkspaceApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddHttpClient<QueryApiClient>(c => c.BaseAddress = new Uri("https+http://queryservice"));
builder.Services.AddHttpClient<AgentApiClient>(c => c.BaseAddress = new Uri("https+http://agentservice"))
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(o =>
    {
        // Model calls and SSE streams are slow; no retries for non-idempotent agent runs.
        o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
        o.Retry.MaxRetryAttempts = 1;
        o.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
    });
builder.Services.AddHttpClient<WorkspaceUploadClient>(c =>
    {
        c.BaseAddress = new Uri("https+http://api");
        c.Timeout = Timeout.InfiniteTimeSpan;
    })
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseInsightFlowSecurity();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapBffEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
