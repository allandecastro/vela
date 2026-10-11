using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vela.Tests.TestSupport;

/// <summary>
/// Runs a body on <c>count</c> dedicated threads released together by a barrier, to maximize a race. Unlike
/// <c>Parallel.For</c> (which may run fewer workers than iterations, so a full barrier would never trip), every
/// iteration gets its own thread. A thread that does not finish within the timeout fails the test instead of hanging it.
/// </summary>
internal static class ConcurrentStart
{
    public static IReadOnlyCollection<Exception> Run(int count, Action<int> body, TimeSpan timeout)
    {
        ConcurrentBag<Exception> failures = new ConcurrentBag<Exception>();
        using Barrier gate = new Barrier(count);
        Thread[] threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    if (!gate.SignalAndWait(timeout))
                    {
                        throw new TimeoutException("Not every thread reached the start barrier.");
                    }

                    body(index);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            })
            { IsBackground = true };
        }

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        DateTime deadline = DateTime.UtcNow + timeout + timeout;
        foreach (Thread thread in threads)
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (!thread.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                throw new TimeoutException($"A worker thread did not finish within {timeout + timeout}.");
            }
        }

        return failures.ToArray();
    }
}
