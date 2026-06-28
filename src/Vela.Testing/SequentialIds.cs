using System;
using System.Threading;

namespace Vela.Testing
{
    /// <summary>
    /// Deterministic, sequential <see cref="Guid"/> generation for the in-memory store, so records created
    /// without an id get reproducible values (<c>00000000-0000-0000-0000-000000000001</c>, <c>...0002</c>, …).
    /// Pass <see cref="Starting"/> to <c>WithIdSequence(...)</c> and assert expected ids with <see cref="At"/>.
    /// </summary>
    public static class SequentialIds
    {
        /// <summary>The id at a 1-based position in the sequence, e.g. <c>At(1)</c> = <c>...000000000001</c>.</summary>
        public static Guid At(long index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Sequence index must be non-negative.");
            }

            return new Guid($"00000000-0000-0000-0000-{index:D12}");
        }

        /// <summary>A thread-safe generator yielding <see cref="At"/> values from <paramref name="first"/> upward.</summary>
        public static Func<Guid> Starting(long first = 1)
        {
            long next = first - 1;
            return () => At(Interlocked.Increment(ref next));
        }
    }
}
