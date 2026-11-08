namespace MineOS.Domain.Entities;

public sealed class CronJob
{
    public int Id { get; set; }
    public required string ServerName { get; set; }
    public required string CronExpression { get; set; }
    public required string Action { get; set; }
    public string? Message { get; set; }
    /// <summary>The console command a "command" job sends; null for every other action.</summary>
    public string? Command { get; set; }
    /// <summary>
    /// For "restart" and "stop": how many minutes ahead players are warned in chat.
    /// Null or 0 means no warning.
    /// </summary>
    public int? WarningMinutes { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}
