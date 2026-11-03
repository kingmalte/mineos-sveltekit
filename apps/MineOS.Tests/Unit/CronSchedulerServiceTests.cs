using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MineOS.Application.Dtos;
using MineOS.Application.Interfaces;
using MineOS.Domain.Entities;
using ServerStatus = MineOS.Infrastructure.Constants.ServerStatus;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

public class CronSchedulerServiceTests
{
    private const string Server = "survival";

    private readonly Mock<IRepository<CronJob>> _repo = new();
    private readonly Mock<IServerService> _servers = new();
    private readonly Mock<IConsoleService> _console = new();
    private readonly Mock<IBackgroundJobService> _jobs = new();
    private readonly List<string> _sent = new();
    private readonly ServiceProvider _provider;

    private string _serverType = "java";
    private bool _running = true;

    public CronSchedulerServiceTests()
    {
        _servers
            .Setup(s => s.GetServerStatusAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ServerHeartbeatDto(
                Server, _running ? ServerStatus.Running : ServerStatus.Stopped, null, null, null, null));
        _servers
            .Setup(s => s.GetServerAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ServerDetailDto(
                Server, DateTimeOffset.UtcNow, 1000, 1000, "mc", "mc",
                _running ? ServerStatus.Running : ServerStatus.Stopped,
                null, null, null, _serverType, true, false));
        _console
            .Setup(c => c.SendCommandAsync(Server, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, command, _) => _sent.Add(command))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_repo.Object);
        services.AddSingleton(_servers.Object);
        services.AddSingleton(_console.Object);
        _provider = services.BuildServiceProvider();
    }

    private CronSchedulerService CreateScheduler() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _jobs.Object,
        Mock.Of<ILogger<CronSchedulerService>>());

    private void GivenJobs(params CronJob[] jobs) =>
        _repo
            .Setup(r => r.ToListAsync(It.IsAny<Expression<Func<CronJob, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobs.ToList());

    private static CronJob DailyRestartAt4(int? warningMinutes, string action = "restart") => new()
    {
        Id = 1,
        ServerName = Server,
        CronExpression = "0 4 * * *",
        Action = action,
        WarningMinutes = warningMinutes,
        Enabled = true
    };

    private static DateTimeOffset At(int hour, int minute) => new(2026, 11, 3, hour, minute, 0, TimeSpan.Zero);

    private static Task RunAsync(CronJob job, IServiceProvider services) =>
        CronSchedulerService.RunCronActionAsync(job, services, new Progress<JobProgressDto>(), CancellationToken.None);

    [Theory]
    [InlineData(3, 45, "say Server restarting in 15 minutes.")]
    [InlineData(3, 50, "say Server restarting in 10 minutes.")]
    [InlineData(3, 55, "say Server restarting in 5 minutes.")]
    [InlineData(3, 58, "say Server restarting in 2 minutes.")]
    [InlineData(3, 59, "say Server restarting in 1 minute.")]
    public async Task Warns_Players_At_Each_Point_Before_A_Restart(int hour, int minute, string expected)
    {
        GivenJobs(DailyRestartAt4(15));

        await CreateScheduler().TickAsync(At(hour, minute), CancellationToken.None);

        Assert.Equal(new[] { expected }, _sent);
    }

    [Theory]
    [InlineData(3, 44)]
    [InlineData(3, 47)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    public async Task Stays_Quiet_Between_Warning_Points(int hour, int minute)
    {
        GivenJobs(DailyRestartAt4(15));

        await CreateScheduler().TickAsync(At(hour, minute), CancellationToken.None);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Stop_Jobs_Say_Shutting_Down()
    {
        GivenJobs(DailyRestartAt4(5, action: "stop"));

        await CreateScheduler().TickAsync(At(3, 55), CancellationToken.None);

        Assert.Equal(new[] { "say Server shutting down in 5 minutes." }, _sent);
    }

    [Fact]
    public async Task No_Warning_Without_A_Warning_Window()
    {
        GivenJobs(DailyRestartAt4(null));

        await CreateScheduler().TickAsync(At(3, 59), CancellationToken.None);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task No_Warning_When_The_Server_Is_Not_Running()
    {
        _running = false;
        GivenJobs(DailyRestartAt4(5));

        await CreateScheduler().TickAsync(At(3, 55), CancellationToken.None);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task No_Warning_On_A_Proxy()
    {
        _serverType = "proxy";
        GivenJobs(DailyRestartAt4(5));

        await CreateScheduler().TickAsync(At(3, 55), CancellationToken.None);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_Job_Due_Again_Soon_Announces_Only_The_Nearest_Run()
    {
        // Every 5 minutes with a 10-minute window: at 03:50 runs are due in 5 and
        // 10 minutes. Two messages at once would contradict each other.
        GivenJobs(new CronJob
        {
            Id = 1, ServerName = Server, CronExpression = "*/5 * * * *", Action = "restart",
            WarningMinutes = 10, Enabled = true
        });

        await CreateScheduler().TickAsync(At(3, 50), CancellationToken.None);

        Assert.Equal(new[] { "say Server restarting in 5 minutes." }, _sent);
    }

    [Fact]
    public async Task A_Failed_Warning_Does_Not_Stop_The_Due_Job()
    {
        _console
            .Setup(c => c.SendCommandAsync(Server, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("screen gone"));
        var warned = DailyRestartAt4(5);
        var due = new CronJob
        {
            Id = 2, ServerName = Server, CronExpression = "55 3 * * *", Action = "backup", Enabled = true
        };
        GivenJobs(warned, due);

        await CreateScheduler().TickAsync(At(3, 55), CancellationToken.None);

        _jobs.Verify(j => j.QueueJob("backup", Server,
            It.IsAny<Func<IServiceProvider, IProgress<JobProgressDto>, CancellationToken, Task>>()), Times.Once);
    }

    [Fact]
    public async Task The_Restart_Itself_Still_Runs_On_Schedule()
    {
        GivenJobs(DailyRestartAt4(15));

        await CreateScheduler().TickAsync(At(4, 0), CancellationToken.None);

        _jobs.Verify(j => j.QueueJob("restart", Server,
            It.IsAny<Func<IServiceProvider, IProgress<JobProgressDto>, CancellationToken, Task>>()), Times.Once);
    }

    [Fact]
    public async Task Restart_Announces_Now_And_Restarts_As_One_Operation()
    {
        await RunAsync(DailyRestartAt4(5), _provider);

        Assert.Equal(new[] { "say Server restarting now, back in a moment." }, _sent);
        _servers.Verify(s => s.RestartServerAsync(Server, 30, It.IsAny<CancellationToken>()), Times.Once);
        _servers.Verify(s => s.StopServerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restart_Without_Warning_Says_Nothing()
    {
        await RunAsync(DailyRestartAt4(null), _provider);

        Assert.Empty(_sent);
        _servers.Verify(s => s.RestartServerAsync(Server, 30, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("stop")]
    public async Task Restart_And_Stop_Leave_A_Stopped_Server_Alone(string action)
    {
        _running = false;

        await RunAsync(DailyRestartAt4(5, action), _provider);

        Assert.Empty(_sent);
        _servers.Verify(s => s.RestartServerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _servers.Verify(s => s.StopServerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _servers.Verify(s => s.StartServerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Command_Job_Sends_Its_Command()
    {
        var job = new CronJob
        {
            Id = 3, ServerName = Server, CronExpression = "0 12 * * *", Action = "command",
            Command = "time set day", Enabled = true
        };

        await RunAsync(job, _provider);

        Assert.Equal(new[] { "time set day" }, _sent);
    }

    [Fact]
    public async Task Command_Job_Is_Skipped_While_The_Server_Is_Stopped()
    {
        _running = false;
        var job = new CronJob
        {
            Id = 3, ServerName = Server, CronExpression = "0 12 * * *", Action = "command",
            Command = "time set day", Enabled = true
        };

        await RunAsync(job, _provider);

        Assert.Empty(_sent);
    }
}
