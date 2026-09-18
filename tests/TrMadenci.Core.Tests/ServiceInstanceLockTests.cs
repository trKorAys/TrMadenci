using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class ServiceInstanceLockTests
{
    [Fact]
    public void Second_process_role_is_rejected_until_the_first_lease_is_released()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciInstanceLockTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var first = ServiceInstanceLock.TryAcquire(ServiceInstanceLock.MiningRole, directory);
            using var blocked = ServiceInstanceLock.TryAcquire(ServiceInstanceLock.MiningRole, directory);

            Assert.True(first.Acquired);
            Assert.False(blocked.Acquired);
            Assert.NotNull(blocked.Owner);
            Assert.Equal(Environment.ProcessId, blocked.Owner.ProcessId);
            Assert.Equal(ServiceInstanceLock.MiningRole, blocked.Owner.Role);

            first.Dispose();
            using var replacement = ServiceInstanceLock.TryAcquire(
                ServiceInstanceLock.MiningRole, directory);
            Assert.True(replacement.Acquired);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Supervisor_and_mining_roles_have_independent_leases()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciInstanceLockTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var supervisor = ServiceInstanceLock.TryAcquire(
                ServiceInstanceLock.SupervisorRole, directory);
            using var miner = ServiceInstanceLock.TryAcquire(ServiceInstanceLock.MiningRole, directory);

            Assert.True(supervisor.Acquired);
            Assert.True(miner.Acquired);
            Assert.NotEqual(supervisor.LockPath, miner.LockPath);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
