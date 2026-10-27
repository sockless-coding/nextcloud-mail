using System.Diagnostics;

namespace SocklessMail.Api.Hosting;

/// <summary>
/// One structured line per request: method, path, status, duration. Headers, query strings and
/// bodies are never logged, since they carry AppAPI credentials, session cookies and mail data.
/// </summary>
public sealed class RequestLogging(RequestDelegate next, ILogger<RequestLogging> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/heartbeat")
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            logger.LogInformation("{Method} {Path} -> {StatusCode} in {ElapsedMs:0.0} ms",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
