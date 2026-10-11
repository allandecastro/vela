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
        /// <summary>The highest index the 12-digit last group can hold.</summary>
        public const long MaxIndex = 999_999_999_999;

        /// <summary>The id at a 1-based position in the sequence, e.g. <c>At(1)</c> = <c>...000000000001</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is below 1 (0 would be
        /// <see cref="Guid.Empty"/>) or above <see cref="MaxIndex"/>.</exception>
        public static Guid At(long index)
        {
            if (index < 1 || index > MaxIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Sequence index must be between 1 and {MaxIndex}.");
            }

            return new Guid($"00000000-0000-0000-0000-{index:D12}");
        }

        /// <summary>A thread-safe generator yielding <see cref="At"/> values from <paramref name="first"/> upward.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="first"/> is outside 1..<see cref="MaxIndex"/>.</exception>
        public static Func<Guid> Starting(long first = 1)
        {
            At(first);
            long next = first - 1;
            return () => At(Interlocked.Increment(ref next));
        }
    }
}
