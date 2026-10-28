using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using SocklessMail.Api;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Api;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Data;
using SocklessMail.Api.Hosting;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Security;
using SocklessMail.Api.Sync;

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
var options = SocklessMailOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(appApi);
builder.Services.AddSingleton(options);
builder.ConfigureAppApiListener(appApi);

builder.Services.AddAppApiAuthentication();
builder.Services.AddHttpClient<NextcloudClient>(NextcloudClient.Configure);
builder.Services.AddScoped<UiRegistration>();

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddCredentialDataProtection(options, appApi);
builder.Services.AddMailDatabase(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton(new ServerAddressPolicy(options.AllowLocalServers));
builder.Services.AddSingleton<PolicySocketConnector>();
builder.Services.AddSingleton(new ImapFeatures());
builder.Services.AddSingleton<MailServerConnector>();
builder.Services.AddSingleton<ConnectionTester>();
builder.Services.AddSingleton<ImapConnections>();
builder.Services.AddHostedService<ImapConnectionJanitor>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<MailboxService>();
builder.Services.AddScoped<FolderCache>();
builder.Services.AddScoped<UnifiedInbox>();
builder.Services.AddSingleton<SyncLocks>();
builder.Services.AddSingleton(new WatcherOptions());
builder.Services.AddSingleton<UserActivity>();
builder.Services.AddSingleton<MailboxWatchers>();
builder.Services.AddHostedService(services => services.GetRequiredService<MailboxWatchers>());
builder.Services.AddHostedService<MailCacheJanitor>();
builder.Services.AddSingleton<ImageProxyTokens>();
builder.Services.AddSingleton<AttachmentUploads>();
builder.Services.AddHostedService<AttachmentUploadJanitor>();
builder.Services.AddSingleton<ImageProxy>();
ImageProxy.ConfigureServices(builder.Services);

// Connection tests open sockets to user-chosen hosts; keep them from being used as a scanner.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(AccountEndpoints.ConnectionTestRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // Sending: generous for people, a brake for a hijacked session or a runaway script.
    limiter.AddPolicy(ComposeEndpoints.SendRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
});

var app = builder.Build();
app.MakeHarpSocketAccessible(appApi);

if (options.EncryptionKey is null)
{
    app.Logger.LogWarning(
        "SOCKLESS_MAIL_ENCRYPTION_KEY is not set: stored mail credentials are protected with APP_SECRET, which AppAPI regenerates when the app is registered again (users would have to re-enter their passwords). See README, \"Security model\".");
}

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
app.UseUserActivity();
app.UseRateLimiter();

app.MapLifecycleEndpoints();

var api = app.MapGroup("/api").RequireAuthorization(AppApiAuthentication.UserPolicy);
api.MapMeEndpoints();
api.MapAccountEndpoints();
api.MapMailboxEndpoints();
api.MapComposeEndpoints();

// Unmatched paths must answer 404, not the fallback policy's 401/403: AppAPI calls POST /init
// with system credentials and treats only 404/501 as "no init needed".
app.MapFallback("{*path}", () => Results.NotFound(new { error = "not_found" })).AllowAnonymous();

await app.MigrateMailDatabaseAsync();

app.Logger.LogInformation("Sockless Mail {Version} starting ({Mode})", appApi.AppVersion, appApi.IsHarp ? "HaRP" : "direct");
await app.RunAsync();

public partial class Program;
