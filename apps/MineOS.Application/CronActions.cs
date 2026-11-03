using MineOS.Application.Dtos;

namespace MineOS.Application;

/// <summary>
/// The canonical set of cron job actions. The scheduler dispatch, API
/// validation, and web UI must all agree on this list.
/// </summary>
public static class CronActions
{
    public const string Backup = "backup";
    public const string Restart = "restart";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Command = "command";

    public static readonly IReadOnlyList<string> All = new[] { Backup, Restart, Start, Stop, Command };

    /// <summary>Longest console command a "command" job may carry (matches the DB column).</summary>
    public const int MaxCommandLength = 1024;

    /// <summary>Furthest ahead a restart/stop warning may start.</summary>
    public const int MaxWarningMinutes = 60;

    public static bool IsValid(string? action) =>
        action is not null && All.Contains(Normalize(action));

    public static string Normalize(string action) => action.Trim().ToLowerInvariant();

    /// <summary>True for the actions that take players offline and can warn them first.</summary>
    public static bool SupportsWarning(string? action) =>
        action is not null && Normalize(action) is Restart or Stop;

    /// <summary>
    /// The command as it is stored and sent: trimmed, without the leading slash
    /// players type in chat (the server console does not need it).
    /// </summary>
    public static string NormalizeCommand(string command)
    {
        var trimmed = command.Trim();
        return trimmed.StartsWith('/') ? trimmed[1..].TrimStart() : trimmed;
    }

    /// <summary>
    /// Checks a create request beyond the action name. Returns an error message
    /// for the caller, or null when the request is acceptable.
    /// </summary>
    public static string? Validate(CreateCronRequest request)
    {
        if (!IsValid(request.Action))
        {
            return $"Unknown cron action '{request.Action}'. Valid actions: {string.Join(", ", All)}";
        }

        var action = Normalize(request.Action);

        if (action == Command)
        {
            if (string.IsNullOrWhiteSpace(request.Command))
            {
                return "A command job needs a command to send.";
            }

            var command = NormalizeCommand(request.Command);
            if (command.Length == 0)
            {
                return "A command job needs a command to send.";
            }
            if (command.Length > MaxCommandLength)
            {
                return $"Commands are limited to {MaxCommandLength} characters.";
            }
            // One job sends one line. A line break would be typed into the console
            // as a second command; other control characters have no business there.
            if (command.Any(char.IsControl))
            {
                return "The command must be a single line without control characters.";
            }
        }
        else if (!string.IsNullOrEmpty(request.Command))
        {
            return $"Only '{Command}' jobs take a command.";
        }

        if (request.WarningMinutes is { } minutes && minutes != 0)
        {
            if (!SupportsWarning(action))
            {
                return $"Only '{Restart}' and '{Stop}' jobs can warn players.";
            }
            if (minutes < 0 || minutes > MaxWarningMinutes)
            {
                return $"The warning must be between 0 and {MaxWarningMinutes} minutes.";
            }
        }

        return null;
    }
}

/// <summary>
/// When and how players are told about a scheduled restart or stop.
/// </summary>
/// <remarks>
/// The job still runs at its scheduled time; the warnings go out in the minutes
/// before it. That keeps "restart daily at 04:00" meaning 04:00, and needs no
/// long-running countdown that would hold up the (serial) job queue.
/// </remarks>
public static class CronWarnings
{
    /// <summary>Reminder points, in minutes before the job, used inside the warning window.</summary>
    private static readonly int[] Marks = { 60, 30, 15, 10, 5, 2, 1 };

    /// <summary>
    /// Minutes before the job at which a warning goes out, largest first: the
    /// start of the window itself plus every standard mark inside it.
    /// </summary>
    public static IReadOnlyList<int> Offsets(int? warningMinutes)
    {
        if (warningMinutes is not { } window || window <= 0)
        {
            return Array.Empty<int>();
        }

        return Marks
            .Where(m => m < window)
            .Prepend(window)
            .Distinct()
            .OrderByDescending(m => m)
            .ToArray();
    }

    public static string WarningText(string action, int minutesLeft)
    {
        var when = minutesLeft == 1 ? "1 minute" : $"{minutesLeft} minutes";
        return CronActions.Normalize(action) == CronActions.Stop
            ? $"Server shutting down in {when}."
            : $"Server restarting in {when}.";
    }

    public static string NowText(string action) =>
        CronActions.Normalize(action) == CronActions.Stop
            ? "Server shutting down now."
            : "Server restarting now, back in a moment.";

    /// <summary>
    /// The console command that shows <paramref name="text"/> to every player,
    /// or null for a proxy (Velocity has no broadcast command of its own). Every
    /// Java flavour and Bedrock understand <c>say</c>.
    /// </summary>
    public static string? BroadcastCommand(string serverType, string text) =>
        serverType.Trim().ToLowerInvariant() switch
        {
            "proxy" or "velocity" or "bungeecord" => null,
            _ => $"say {text}"
        };
}
