using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VrcPicSorter.App.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceService(string applicationId, string? instanceScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        var scope = instanceScope ?? GetCurrentUserScope();
        var nameHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{applicationId}\0{scope}")));
        var prefix = $"Local\\{applicationId}.{nameHash[..24]}";

        MutexName = $"{prefix}.Mutex";
        ActivationEventName = $"{prefix}.Activate";
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
    }

    public string MutexName { get; }

    public string ActivationEventName { get; }

    public bool IsOwner => _ownsMutex;

    public bool TryAcquireOwnership()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex)
        {
            return true;
        }

        try
        {
            _ownsMutex = _mutex.WaitOne(millisecondsTimeout: 0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public void StartActivationListener(Action onActivated)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onActivated);

        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the owning instance can listen for activation.");
        }

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The activation listener is already running.");
        }

        _listenerCancellation = new CancellationTokenSource();
        var cancellationToken = _listenerCancellation.Token;
        _listenerTask = Task.Factory.StartNew(
            () => ListenForActivation(onActivated, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public bool SignalExistingInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _activationEvent.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation?.Cancel();
        _listenerTask?.GetAwaiter().GetResult();
        _listenerCancellation?.Dispose();
        _activationEvent.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The owning thread may have terminated during abandoned-mutex recovery.
            }
        }

        _mutex.Dispose();
        _ownsMutex = false;
    }

    private void ListenForActivation(Action onActivated, CancellationToken cancellationToken)
    {
        var handles = new[] { _activationEvent, cancellationToken.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                onActivated();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Activation is best-effort; one UI callback failure must not disable IPC.
            }
        }
    }

    private static string GetCurrentUserScope()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
    }
}
