using TrMadenci.Core.Fees;

namespace TrMadenci.Service.Mining;

internal sealed class OctopusWorkRouter(IReadOnlyList<IOctopusWorkSink> workers)
{
    private readonly object _gate = new();
    private OctopusPoolWork? _latestUserWork;
    private OctopusPoolWork? _latestDeveloperWork;
    private MiningBeneficiary _beneficiary = MiningBeneficiary.User;
    private bool _manuallyPaused;

    public bool Receive(OctopusPoolWork work)
    {
        lock (_gate)
        {
            if (work.Beneficiary == MiningBeneficiary.User)
                _latestUserWork = work;
            else
                _latestDeveloperWork = work;
            if (work.Beneficiary != _beneficiary || _manuallyPaused)
                return false;
            Assign(work);
            return true;
        }
    }

    public bool Select(MiningBeneficiary beneficiary)
    {
        lock (_gate)
        {
            _beneficiary = beneficiary;
            if (_manuallyPaused)
            {
                PauseWorkers();
                return false;
            }
            var work = beneficiary == MiningBeneficiary.User ? _latestUserWork : _latestDeveloperWork;
            if (work is null)
            {
                PauseWorkers();
                return false;
            }
            Assign(work);
            return true;
        }
    }

    public bool Disconnect(MiningBeneficiary beneficiary)
    {
        lock (_gate)
        {
            if (beneficiary == MiningBeneficiary.User)
                _latestUserWork = null;
            else
                _latestDeveloperWork = null;
            if (beneficiary != _beneficiary)
                return false;
            PauseWorkers();
            return true;
        }
    }

    public bool SetManuallyPaused(bool paused)
    {
        lock (_gate)
        {
            _manuallyPaused = paused;
            if (paused)
            {
                PauseWorkers();
                return true;
            }

            var work = _beneficiary == MiningBeneficiary.User
                ? _latestUserWork
                : _latestDeveloperWork;
            if (work is null)
            {
                PauseWorkers();
                return false;
            }
            Assign(work);
            return true;
        }
    }

    private void Assign(OctopusPoolWork work)
    {
        foreach (var worker in workers)
            worker.Assign(work);
    }

    private void PauseWorkers()
    {
        foreach (var worker in workers)
            worker.Pause();
    }
}
