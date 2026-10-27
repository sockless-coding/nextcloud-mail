using SocklessMail.Api.Api;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
}
else
{
    builder.Logging.AddJsonConsole();
}

var appApi = AppApiOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(appApi);
builder.ConfigureAppApiListener(appApi);

builder.Services.AddAppApiAuthentication();
builder.Services.AddHttpClient<NextcloudClient>(NextcloudClient.Configure);
builder.Services.AddScoped<UiRegistration>();

var app = builder.Build();
app.MakeHarpSocketAccessible(appApi);

app.UseMiddleware<RequestLogging>();
// Before authentication: bundles and icons carry no secrets, and the top-menu icon is requested
// without user context. Fixed file names (AppAPI registers paths, not hashes), so revalidate.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
// Explicitly after static files: otherwise routing runs first, the catch-all 404 below matches
// every asset path, and the static file middleware skips requests that already have an endpoint.
app.UseRouting();
app.UseMiddleware<ApiRequestGuard>();
app.UseAuthentication();
app.UseAuthorization();

app.MapLifecycleEndpoints();

var api = app.MapGroup("/api").RequireAuthorization(AppApiAuthentication.UserPolicy);
api.MapMeEndpoints();

// Unmatched paths must answer 404, not the fallback policy's 401/403: AppAPI calls POST /init
// with system credentials and treats only 404/501 as "no init needed".
app.MapFallback("{*path}", () => Results.NotFound(new { error = "not_found" })).AllowAnonymous();

app.Logger.LogInformation("Sockless Mail {Version} starting ({Mode})", appApi.AppVersion, appApi.IsHarp ? "HaRP" : "direct");
app.Run();

public partial class Program;
