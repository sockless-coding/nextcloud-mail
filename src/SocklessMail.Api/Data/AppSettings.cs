using Microsoft.EntityFrameworkCore;

namespace SocklessMail.Api.Data;

/// <summary>An instance-wide setting admins change in the app (as opposed to the environment variables in info.xml).</summary>
public sealed class AppSetting
{
    public required string Key { get; init; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What admins see and change under /api/admin/settings.</summary>
public sealed record AppSettingsDto(bool DomainAgeChecks);

/// <summary>Admins' settings, read from the database once and kept in memory (this process is their only writer).</summary>
public sealed class AppSettings(IServiceScopeFactory scopes, TimeProvider clock)
{
    private const string DomainAgeChecksKey = "domain_age_checks";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyDictionary<string, string>? _values;

    /// <summary>Look up when sender domains were registered (RDAP, see <see cref="Senders.DomainAges"/>). On unless turned off.</summary>
    public async Task<bool> DomainAgeChecksAsync(CancellationToken ct) =>
        !(await ValuesAsync(ct)).TryGetValue(DomainAgeChecksKey, out var value) || value != "false";

    public async Task<AppSettingsDto> GetAsync(CancellationToken ct) => new(await DomainAgeChecksAsync(ct));

    public async Task<AppSettingsDto> SaveAsync(AppSettingsDto settings, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            await SetAsync(db, DomainAgeChecksKey, settings.DomainAgeChecks ? "true" : "false", ct);
            await db.SaveChangesAsync(ct);
            _values = await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        }
        finally
        {
            _lock.Release();
        }

        return await GetAsync(ct);
    }

    private async Task SetAsync(MailDbContext db, string key, string value, CancellationToken ct)
    {
        var setting = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = clock.GetUtcNow() });
        }
        else if (setting.Value != value)
        {
            setting.Value = value;
            setting.UpdatedAt = clock.GetUtcNow();
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ValuesAsync(CancellationToken ct)
    {
        if (_values is { } values)
        {
            return values;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_values is null)
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
                _values = await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            }

            return _values;
        }
        finally
        {
            _lock.Release();
        }
    }
}
