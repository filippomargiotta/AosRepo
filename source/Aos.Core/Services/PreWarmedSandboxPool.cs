namespace Aos.WebApi.Services;

public sealed class PreWarmedSandboxPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<ProcessSandboxSlot> _slots = new();
    private readonly int _maxPoolSize;
    private readonly Func<ProcessSandboxSlot> _slotFactory;
    private int _refillsInFlight;
    private bool _disposed;

    public PreWarmedSandboxPool(int poolSize)
        : this(poolSize, () => new ProcessSandboxSlot())
    {
    }

    public PreWarmedSandboxPool(int poolSize, Func<ProcessSandboxSlot> slotFactory)
    {
        if (poolSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize), "Pool size cannot be negative.");
        }

        _slotFactory = slotFactory ?? throw new ArgumentNullException(nameof(slotFactory));
        _maxPoolSize = poolSize;
        for (var i = 0; i < poolSize; i++)
        {
            _slots.Enqueue(_slotFactory());
        }
    }

    public int CurrentPoolSize
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    public (ProcessSandboxSlot Slot, bool WasWarm) Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_slots.Count > 0)
            {
                return (_slots.Dequeue(), true);
            }
        }

        var coldSlot = _slotFactory();
        lock (_gate)
        {
            if (!_disposed)
            {
                return (coldSlot, false);
            }
        }

        coldSlot.Dispose();
        throw new ObjectDisposedException(GetType().FullName);
    }

    // One-shot slot semantics: release discards the used slot and schedules a fresh warm replacement.
    public void Release(ProcessSandboxSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        slot.Dispose();
        ScheduleRefillIfNeeded();
    }

    public void Dispose()
    {
        List<ProcessSandboxSlot> slotsToDispose;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            slotsToDispose = _slots.ToList();
            _slots.Clear();
        }

        foreach (var slot in slotsToDispose)
        {
            slot.Dispose();
        }
    }

    private void ScheduleRefillIfNeeded()
    {
        lock (_gate)
        {
            if (_disposed || _maxPoolSize == 0 || _slots.Count + _refillsInFlight >= _maxPoolSize)
            {
                return;
            }

            _refillsInFlight++;
        }

        _ = Task.Run(RefillAsync);
    }

    private void RefillAsync()
    {
        ProcessSandboxSlot? slot = null;
        try
        {
            slot = _slotFactory();
        }
        catch
        {
            // Refill failure leaves the pool under-filled; the next acquire can still create a cold slot.
        }

        lock (_gate)
        {
            _refillsInFlight--;
            if (slot is not null && !_disposed && _slots.Count < _maxPoolSize)
            {
                _slots.Enqueue(slot);
                slot = null;
            }
        }

        slot?.Dispose();
    }
}
