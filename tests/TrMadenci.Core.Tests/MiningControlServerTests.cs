using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class MiningControlServerTests
{
    [Fact]
    public async Task Pipe_commands_control_and_report_the_same_pause_state()
    {
        var pipeName = $"TrMadenci.Tests.{Guid.NewGuid():N}";
        var controller = new MiningPauseController();
        await using var server = new MiningControlServer(controller, pipeName: pipeName);
        server.Start();

        var initial = await MiningControlClient.SendAsync(
            MiningControlCommand.Status, pipeName, TimeSpan.FromSeconds(5));
        Assert.True(initial.Success);
        Assert.Equal("running", initial.State);

        var paused = await MiningControlClient.SendAsync(
            MiningControlCommand.Pause, pipeName, TimeSpan.FromSeconds(5));
        Assert.Equal("paused", paused.State);
        Assert.Equal(1, paused.PauseCount);
        Assert.True(controller.IsPaused);

        var duplicate = await MiningControlClient.SendAsync(
            MiningControlCommand.Pause, pipeName, TimeSpan.FromSeconds(5));
        Assert.Equal(1, duplicate.PauseCount);

        var resumed = await MiningControlClient.SendAsync(
            MiningControlCommand.Resume, pipeName, TimeSpan.FromSeconds(5));
        Assert.Equal("running", resumed.State);
        Assert.False(controller.IsPaused);
    }
}
