using InsightFlow.Foundation.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using InsightFlow.Web.Components;
using InsightFlow.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInsightFlowWebSecurity();

// In the cloud an unauthenticated visitor lands on our own sign-in screen first (it then hands over to Entra External ID), not straight on Microsoft.
if ((builder.Configuration.GetSection(InsightFlowAuthOptions.SectionName).Get<InsightFlowAuthOptions>() ?? new()).Mode == AuthenticationMode.Entra)
{
    builder.Services.PostConfigure<AuthenticationOptions>(o => o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme);
    builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o => o.LoginPath = "/login");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ThemeState>();
builder.Services.AddScoped<NavigationState>();

// RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the documented way to replace the defaults per client.
#pragma warning disable EXTEXP0001
// Typed clients for the internal services. Base addresses use Aspire service discovery names (see AppHost).
builder.Services.AddScoped<ServiceCallCredentials>();
builder.Services.AddHttpClient<WorkspaceApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddHttpClient<AdminApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddHttpClient<ConnectionsApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddHttpClient<DocumentsApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddScoped<InsightFlow.Web.Components.Pages.Query.QueryChatState>();
builder.Services.AddScoped<InsightFlow.Web.Components.Workspace.SpreadsheetChatState>();
builder.Services.AddHttpClient<SkillsApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddHttpClient<PlaygroundApiClient>(c => c.BaseAddress = new Uri("https+http://api"));
builder.Services.AddHttpClient<KnowledgeApiClient>(c => c.BaseAddress = new Uri("https+http://api"))
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(o =>
    {
        // Neo4j calls and the first copy of a big table can take a while, and writes must not be retried.
        o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(10);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(20);
        o.Retry.MaxRetryAttempts = 1;
        o.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
    });
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
app.MapAuthEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
