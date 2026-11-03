using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MineOS.Application;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Domain.Entities;
using ServerStatusStrings = MineOS.Infrastructure.Constants.ServerStatus;

namespace MineOS.Infrastructure.Services;

public sealed class CronSchedulerService : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundJobService _jobService;
    private readonly ILogger<CronSchedulerService> _logger;
    private Timer? _timer;

    public CronSchedulerService(
        IServiceScopeFactory scopeFactory,
        IBackgroundJobService jobService,
        ILogger<CronSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _jobService = jobService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cron scheduler started");
        // Check every 60 seconds, aligned to the start of each minute
        var now = DateTimeOffset.UtcNow;
        var delayToNextMinute = 60 - now.Second;
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(delayToNextMinute), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cron scheduler stopped");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private async void OnTick(object? state)
    {
        try
        {
            await TickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in cron scheduler tick");
        }
    }

    /// <summary>One scheduler pass: warn players about upcoming jobs, then queue the jobs due now.</summary>
    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<CronJob>>();
        var jobs = await repo.ToListAsync(j => j.Enabled, cancellationToken);

        foreach (var job in jobs)
        {
            await SendDueWarningAsync(scope.ServiceProvider, job, now, cancellationToken);
        }

        foreach (var job in jobs)
        {
            if (!CronExpressionMatcher.Matches(job.CronExpression, now))
                continue;

            _logger.LogInformation("Cron trigger: {Action} for server {Server} (expression: {Expr})",
                job.Action, job.ServerName, job.CronExpression);

            QueueCronAction(job);

            job.LastRunAt = now;
            await repo.UpdateAsync(job, cancellationToken);
        }
    }

    /// <summary>
    /// Tells the players on a server that a restart or stop is coming, when this
    /// minute is one of the job's warning points.
    /// </summary>
    private async Task SendDueWarningAsync(
        IServiceProvider services, CronJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!CronActions.SupportsWarning(job.Action))
            return;

        // Smallest matching offset only: a job that is due again soon (e.g. every
        // 5 minutes with a 10-minute warning) should not announce two times at once.
        var minutesLeft = CronWarnings.Offsets(job.WarningMinutes)
            .Where(m => CronExpressionMatcher.Matches(job.CronExpression, now.AddMinutes(m)))
            .DefaultIfEmpty(0)
            .Min();
        if (minutesLeft == 0)
            return;

        try
        {
            await BroadcastAsync(services, job.ServerName,
                CronWarnings.WarningText(job.Action, minutesLeft), cancellationToken);
        }
        catch (Exception ex)
        {
            // A missed warning must not cost the other jobs their tick.
            _logger.LogWarning(ex, "Could not warn players on {Server} about the scheduled {Action}",
                job.ServerName, job.Action);
        }
    }

    /// <summary>
    /// Shows a message to everyone on a running server. Does nothing when the server
    /// is not running or has no broadcast command (proxies).
    /// </summary>
    private static async Task BroadcastAsync(
        IServiceProvider services, string serverName, string text, CancellationToken cancellationToken)
    {
        var serverService = services.GetRequiredService<IServerService>();
        if (!await IsRunningAsync(serverService, serverName, cancellationToken))
            return;

        var server = await serverService.GetServerAsync(serverName, cancellationToken);
        var command = CronWarnings.BroadcastCommand(server.ServerType, text);
        if (command is null)
            return;

        var console = services.GetRequiredService<IConsoleService>();
        await console.SendCommandAsync(serverName, command, cancellationToken);
    }

    private static async Task<bool> IsRunningAsync(
        IServerService serverService, string serverName, CancellationToken cancellationToken)
    {
        var status = await serverService.GetServerStatusAsync(serverName, cancellationToken);
        return status.Status == ServerStatusStrings.Running;
    }

    private void QueueCronAction(CronJob job)
    {
        var action = CronActions.Normalize(job.Action);
        if (!CronActions.IsValid(action))
        {
            _logger.LogWarning("Unknown cron action: {Action}", job.Action);
            return;
        }

        _jobService.QueueJob(action, job.ServerName,
            (sp, progress, ct) => RunCronActionAsync(job, sp, progress, ct));
    }

    /// <summary>The work of one scheduled job, run on the background job queue.</summary>
    internal static async Task RunCronActionAsync(
        CronJob job, IServiceProvider services, IProgress<JobProgressDto> progress, CancellationToken ct)
    {
        var action = CronActions.Normalize(job.Action);
        var server = job.ServerName;
        void Report(string status, int percentage, string message) =>
            progress.Report(new JobProgressDto(
                string.Empty, action, server, status, percentage, message, DateTimeOffset.UtcNow));

        var serverService = services.GetRequiredService<IServerService>();

        switch (action)
        {
            case CronActions.Backup:
                var backupService = services.GetRequiredService<IBackupService>();
                Report("running", 10, "Starting scheduled backup...");
                await backupService.CreateBackupAsync(server, ct);
                Report("completed", 100, "Scheduled backup completed");
                break;

            case CronActions.Restart:
                // A server someone stopped on purpose stays stopped; restarting it
                // would bring back a server its owner took down.
                if (!await IsRunningAsync(serverService, server, ct))
                {
                    Report("completed", 100, "Server is not running; scheduled restart skipped");
                    break;
                }
                if (job.WarningMinutes > 0)
                    await TryBroadcastAsync(services, server, CronWarnings.NowText(action), ct);
                Report("running", 30, "Restarting server...");
                // One operation under the server's lifecycle gate, so nothing can
                // start the server in the gap between stopping and starting it.
                await serverService.RestartServerAsync(server, 30, ct);
                Report("completed", 100, "Scheduled restart completed");
                break;

            case CronActions.Start:
                Report("running", 30, "Starting server...");
                await serverService.StartServerAsync(server, ct);
                Report("completed", 100, "Scheduled start completed");
                break;

            case CronActions.Stop:
                if (!await IsRunningAsync(serverService, server, ct))
                {
                    Report("completed", 100, "Server is not running; scheduled stop skipped");
                    break;
                }
                if (job.WarningMinutes > 0)
                    await TryBroadcastAsync(services, server, CronWarnings.NowText(action), ct);
                Report("running", 30, "Stopping server...");
                await serverService.StopServerAsync(server, 30, ct);
                Report("completed", 100, "Scheduled stop completed");
                break;

            case CronActions.Command:
                if (string.IsNullOrWhiteSpace(job.Command))
                    throw new InvalidOperationException("This scheduled command job has no command to send.");
                if (!await IsRunningAsync(serverService, server, ct))
                {
                    Report("completed", 100, "Server is not running; scheduled command skipped");
                    break;
                }
                var console = services.GetRequiredService<IConsoleService>();
                await console.SendCommandAsync(server, job.Command, ct);
                Report("completed", 100, $"Sent scheduled command: {job.Command}");
                break;
        }
    }

    /// <summary>A last "now" notice is a courtesy; failing to send it must not cancel the job.</summary>
    private static async Task TryBroadcastAsync(
        IServiceProvider services, string serverName, string text, CancellationToken ct)
    {
        try
        {
            await BroadcastAsync(services, serverName, text, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignored; see summary.
        }
    }
}

internal static class CronExpressionMatcher
{
    /// <summary>
    /// Matches a standard 5-field cron expression (minute hour day-of-month month day-of-week)
    /// against a given time. Supports: * (any), specific values, ranges (1-5), lists (1,3,5), steps (*/5).
    /// </summary>
    public static bool Matches(string expression, DateTimeOffset time)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;

        return FieldMatches(parts[0], time.Minute, 0, 59)
            && FieldMatches(parts[1], time.Hour, 0, 23)
            && FieldMatches(parts[2], time.Day, 1, 31)
            && FieldMatches(parts[3], time.Month, 1, 12)
            && FieldMatches(parts[4], (int)time.DayOfWeek, 0, 6);
    }

    private static bool FieldMatches(string field, int value, int min, int max)
    {
        foreach (var part in field.Split(','))
        {
            if (PartMatches(part.Trim(), value, min, max))
                return true;
        }
        return false;
    }

    private static bool PartMatches(string part, int value, int min, int max)
    {
        // Handle step: */5 or 1-10/2
        var stepParts = part.Split('/');
        var rangeStr = stepParts[0];
        var step = stepParts.Length > 1 && int.TryParse(stepParts[1], out var s) ? s : 1;

        if (rangeStr == "*")
        {
            return step == 1 || (value - min) % step == 0;
        }

        // Handle range: 1-5
        var rangeParts = rangeStr.Split('-');
        if (rangeParts.Length == 2
            && int.TryParse(rangeParts[0], out var from)
            && int.TryParse(rangeParts[1], out var to))
        {
            if (value < from || value > to) return false;
            return step == 1 || (value - from) % step == 0;
        }

        // Exact value
        return int.TryParse(rangeStr, out var exact) && exact == value;
    }
}
