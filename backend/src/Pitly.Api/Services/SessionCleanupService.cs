using Microsoft.EntityFrameworkCore;
using Pitly.Api.Data;

namespace Pitly.Api.Services;

public class SessionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _interval;

    public SessionCleanupService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<SessionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ttl = TimeSpan.FromHours(configuration.GetValue("SessionCleanup:TtlHours", 24));
        _interval = TimeSpan.FromMinutes(configuration.GetValue("SessionCleanup:IntervalMinutes", 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow - _ttl;

                var deleted = await db.Sessions
                    .Where(s => s.CreatedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation("Cleaned up {Count} expired sessions", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session cleanup failed");
            }
        }
    }
}
