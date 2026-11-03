using MineOS.Application.Dtos;
using MineOS.Application;

namespace MineOS.Tests.Unit;

public class CronActionsTests
{
    [Theory]
    [InlineData("backup")]
    [InlineData("restart")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("command")]
    [InlineData("STOP")]
    [InlineData(" start ")]
    public void IsValid_Accepts_Known_Actions(string action)
    {
        Assert.True(CronActions.IsValid(action));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("kill")]
    [InlineData("delete")]
    [InlineData("backup;rm -rf /")]
    public void IsValid_Rejects_Unknown_Actions(string? action)
    {
        Assert.False(CronActions.IsValid(action));
    }

    [Fact]
    public void All_Contains_The_Supported_Actions()
    {
        Assert.Equal(new[] { "backup", "restart", "start", "stop", "command" }, CronActions.All);
    }

    [Theory]
    [InlineData("say hello", "say hello")]
    [InlineData("  /say hello  ", "say hello")]
    [InlineData("/ time set day", "time set day")]
    public void NormalizeCommand_Trims_And_Drops_The_Chat_Slash(string input, string expected)
    {
        Assert.Equal(expected, CronActions.NormalizeCommand(input));
    }

    [Fact]
    public void Validate_Accepts_A_Command_Job()
    {
        Assert.Null(CronActions.Validate(new CreateCronRequest("0 * * * *", "command", null, "/say hi")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Validate_Rejects_A_Command_Job_Without_A_Command(string? command)
    {
        Assert.NotNull(CronActions.Validate(new CreateCronRequest("0 * * * *", "command", null, command)));
    }

    [Theory]
    [InlineData("say a\nstop")]
    [InlineData("say a\rstop")]
    [InlineData("say a\u0007")]
    public void Validate_Rejects_Multi_Line_Or_Control_Characters(string escaped)
    {
        var command = System.Text.RegularExpressions.Regex.Unescape(escaped);
        Assert.NotNull(CronActions.Validate(new CreateCronRequest("0 * * * *", "command", null, command)));
    }

    [Fact]
    public void Validate_Rejects_An_Overlong_Command()
    {
        var command = "say " + new string('x', CronActions.MaxCommandLength);
        Assert.NotNull(CronActions.Validate(new CreateCronRequest("0 * * * *", "command", null, command)));
    }

    [Fact]
    public void Validate_Rejects_A_Command_On_Other_Actions()
    {
        Assert.NotNull(CronActions.Validate(new CreateCronRequest("0 * * * *", "backup", null, "say hi")));
    }

    [Theory]
    [InlineData("restart", 5)]
    [InlineData("stop", 60)]
    [InlineData("restart", 0)]
    [InlineData("backup", 0)]
    [InlineData("backup", null)]
    public void Validate_Accepts_Warnings_Where_They_Apply(string action, int? minutes)
    {
        Assert.Null(CronActions.Validate(new CreateCronRequest("0 4 * * *", action, null, null, minutes)));
    }

    [Theory]
    [InlineData("restart", -1)]
    [InlineData("restart", 61)]
    [InlineData("backup", 5)]
    [InlineData("start", 5)]
    public void Validate_Rejects_Bad_Warnings(string action, int minutes)
    {
        Assert.NotNull(CronActions.Validate(new CreateCronRequest("0 4 * * *", action, null, null, minutes)));
    }
}

public class CronWarningsTests
{
    [Theory]
    [InlineData(null, new int[0])]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 1 })]
    [InlineData(5, new[] { 5, 2, 1 })]
    [InlineData(7, new[] { 7, 5, 2, 1 })]
    [InlineData(15, new[] { 15, 10, 5, 2, 1 })]
    [InlineData(60, new[] { 60, 30, 15, 10, 5, 2, 1 })]
    public void Offsets_Start_With_The_Window_Then_Count_Down(int? window, int[] expected)
    {
        Assert.Equal(expected, CronWarnings.Offsets(window));
    }

    [Fact]
    public void WarningText_Names_The_Action_And_Time()
    {
        Assert.Equal("Server restarting in 5 minutes.", CronWarnings.WarningText("restart", 5));
        Assert.Equal("Server shutting down in 1 minute.", CronWarnings.WarningText("stop", 1));
    }

    [Theory]
    [InlineData("java")]
    [InlineData("paper")]
    [InlineData("fabric")]
    [InlineData("bedrock")]
    public void Game_Servers_Broadcast_With_Say(string serverType)
    {
        Assert.Equal("say hi", CronWarnings.BroadcastCommand(serverType, "hi"));
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("velocity")]
    [InlineData("bungeecord")]
    public void Proxies_Have_No_Broadcast(string serverType)
    {
        Assert.Null(CronWarnings.BroadcastCommand(serverType, "hi"));
    }
}
