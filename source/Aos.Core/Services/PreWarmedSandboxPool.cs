using System.Collections.Concurrent;

namespace Aos.WebApi.Services;

public sealed class PreWarmedSandboxPool
{
    private readonly ConcurrentQueue<InProcessSandboxSlot> _slots = new();
    private readonly int _maxPoolSize;

    public PreWarmedSandboxPool(int poolSize)
    {
        if (poolSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize), "Pool size cannot be negative.");
        }

        _maxPoolSize = poolSize;
        for (var i = 0; i < poolSize; i++)
        {
            _slots.Enqueue(new InProcessSandboxSlot());
        }
    }

    public int CurrentPoolSize => _slots.Count;

    public (InProcessSandboxSlot Slot, bool WasWarm) Acquire()
    {
        if (_slots.TryDequeue(out var slot))
        {
            return (slot, true);
        }

        return (new InProcessSandboxSlot(), false);
    }

    // One-shot slot semantics: the used slot is discarded and a fresh one takes its place.
    public void Release(InProcessSandboxSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        slot.Dispose();

        if (_slots.Count < _maxPoolSize)
        {
            _slots.Enqueue(new InProcessSandboxSlot());
        }
    }
}
