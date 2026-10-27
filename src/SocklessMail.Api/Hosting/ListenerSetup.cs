using System.Net;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Hosting;

/// <summary>
/// Under HaRP the FRP client tunnels traffic to a unix socket; everywhere else AppAPI connects to
/// APP_HOST:APP_PORT. Only applies when Kestrel is the server (not under the test host).
/// </summary>
public static class ListenerSetup
{
    public static void ConfigureAppApiListener(this WebApplicationBuilder builder, AppApiOptions options)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.IsHarp)
            {
                // A socket left over from a previous run makes the bind fail.
                File.Delete(AppApiOptions.HarpSocketPath);
                kestrel.ListenUnixSocket(AppApiOptions.HarpSocketPath);
            }
            else
            {
                if (options.AppPort <= 0)
                {
                    throw new InvalidOperationException("APP_PORT must be set when not running under HaRP.");
                }

                kestrel.Listen(IPAddress.Parse(options.AppHost), options.AppPort);
            }
        });
    }

    public static void MakeHarpSocketAccessible(this WebApplication app, AppApiOptions options)
    {
        if (!options.IsHarp)
        {
            return;
        }

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            // frpc runs as the same user today, but must keep working if that ever changes.
            File.SetUnixFileMode(AppApiOptions.HarpSocketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            app.Logger.LogInformation("Listening on unix socket {Path} (HaRP)", AppApiOptions.HarpSocketPath);
        });
    }
}
