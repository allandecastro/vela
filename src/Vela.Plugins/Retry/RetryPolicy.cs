using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.ServiceModel;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Vela.Context;

namespace Vela.Retry
{
    /// <summary>
    /// Lightweight transient retry policy for sandbox-safe service calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where it helps.</b> Dataverse service-protection limits don't apply to data operations that originate from
    /// plug-ins and custom workflow activities, so a call through the plugin's own <c>ctx.Service</c> rarely, if ever,
    /// gets a throttling fault to retry. The policy earns its keep around calls that leave the sandbox (another
    /// Dataverse environment, an external service that reports throttling the same way) and in code shared with a
    /// client application, where the limits do apply.
    /// </para>
    /// <para>
    /// <b>Idempotency contract.</b> By default this retries only faults that are safe to replay: server-side
    /// throttling / service-protection limits, where the request was rejected <i>before</i> it executed, so no
    /// side effect occurred. Timeouts (<see cref="TimeoutException"/>, <see cref="WebException"/>, or a fault whose
    /// message mentions "timeout") are <b>not</b> retried by default: a write that times out client-side may already
    /// have committed on the server, so a blind retry would create a duplicate. Only wrap an <i>idempotent</i>
    /// operation (a read, or a write guarded by an alternate key / duplicate-detection) and pass
    /// <c>retryOnTimeout: true</c> to also retry timeouts.
    /// </para>
    /// <para>
    /// <b>Classification.</b> A throttling fault is recognised by its Dataverse service-protection error code only
    /// (number of requests, combined execution time, concurrent requests), never by its message text, so a business
    /// error whose message happens to say "temporarily" or "throttle" is not replayed.
    /// </para>
    /// <para>
    /// <b>Backoff.</b> A service-protection fault carries a <c>Retry-After</c> duration in
    /// <c>OrganizationServiceFault.ErrorDetails</c>; the policy waits exactly that long, because an earlier
    /// retry is rejected again. When the requested wait is longer than <see cref="MaxDelay"/>, the fault is rethrown
    /// instead: a plugin cannot wait that long inside the sandbox time limit. Without a <c>Retry-After</c>, backoff
    /// is exponential with full jitter (<c>initialDelayMs * 2^(attempt-1)</c>, then a random point in
    /// <c>[0, that]</c>) to avoid a synchronized retry storm, with the same cap.
    /// </para>
    /// <para>
    /// <b>Time budget.</b> The cap bounds each wait, not their sum: <c>maxAttempts</c> waits of up to
    /// <see cref="MaxDelay"/> each can consume most of the ~2-minute plugin execution budget. Pass <c>maxElapsed</c>
    /// to bound the whole call: a retry whose wait would end past that budget (measured from the first attempt), or
    /// whose wait overran it, is not made, and the last fault propagates.
    /// </para>
    /// <para>
    /// <b>Transactions.</b> Inside a database transaction (a synchronous pre/post-operation step), a failed
    /// organization service call aborts the whole transaction, so replaying the call cannot succeed. The policy therefore
    /// always takes the plugin context: when <see cref="ILocalPluginContext.IsInTransaction"/> is true the operation
    /// runs once and its failure propagates.
    /// </para>
    /// </remarks>
    public static class RetryPolicy
    {
        // Well-known Dataverse service-protection (throttling) error codes: the request was rejected before it ran,
        // so retrying it replays no side effect. These are the only faults retried by default.
        private const int NumberOfRequestsCode = -2147015902; // 0x80072322 - number of requests over the limit
        private const int ExecutionTimeCode = -2147015903;    // 0x80072321 - combined execution time over the limit
        private const int ConcurrencyCode = -2147015898;      // 0x80072326 - concurrent requests over the limit

        private const int MaxBackoffMs = 30000;

        // The ErrorDetails key under which a service-protection fault carries the wait the platform asks for.
        private const string RetryAfterKey = "Retry-After";

        // Seeds each thread's Random. On .NET Framework, new Random() is seeded from Environment.TickCount, so threads
        // that create theirs within the same tick would draw the same jitter; a shared, locked seed source avoids that.
        private static readonly Random SeedSource = new Random();

        [ThreadStatic]
        private static Random? _rng;

        private static Random Rng => _rng ??= new Random(NextSeed());

        /// <summary>The longest the policy waits between two attempts (30 seconds).</summary>
        public static TimeSpan MaxDelay { get; } = TimeSpan.FromMilliseconds(MaxBackoffMs);

        // The retry loop. Private: every public entry point takes the context, so the transaction check cannot be
        // skipped by accident.
        private static T ExecuteCore<T>(Func<T> operation, bool retryOnTimeout, int maxAttempts, int initialDelayMs, TimeSpan? maxElapsed, Action<TimeSpan> sleep)
        {
            operation = operation ?? throw new ArgumentNullException(nameof(operation));
            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be at least 1.");
            }

            if (initialDelayMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialDelayMs), "initialDelayMs must be non-negative.");
            }

            if (maxElapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maxElapsed), "maxElapsed must be non-negative.");
            }

            Stopwatch elapsed = Stopwatch.StartNew();
            Exception? lastException = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (Exception ex) when (attempt < maxAttempts
                    && ShouldRetry(ex, retryOnTimeout)
                    && TryComputeDelay(ex, attempt, initialDelayMs, out TimeSpan delay)
                    && (!maxElapsed.HasValue || elapsed.Elapsed + delay <= maxElapsed.Value))
                {
                    lastException = ex;
                    sleep(delay);

                    // A sleep can overrun (scheduling, a GC pause): when the wait actually ended past the budget, no
                    // further attempt starts and the fault propagates.
                    if (maxElapsed.HasValue && elapsed.Elapsed > maxElapsed.Value)
                    {
                        break;
                    }
                }
            }

            throw lastException ?? new InvalidPluginExecutionException("Retry policy failed without exception detail.");
        }

        /// <summary>
        /// Executes an operation, retrying safe-to-replay throttling faults (and timeouts when
        /// <paramref name="retryOnTimeout"/> is set), unless the execution runs inside a database transaction: there a
        /// failed call aborts the transaction, so a replay cannot succeed and the operation runs exactly once.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="context">The plugin context, used to detect a transaction.</param>
        /// <param name="operation">Operation to execute.</param>
        /// <param name="retryOnTimeout">
        /// When true, also retries timeouts. Only pass true for an idempotent operation (a read, or a write that is
        /// safe to replay); a non-idempotent write may already have committed before the timeout surfaced.
        /// </param>
        /// <param name="maxAttempts">Maximum attempts outside a transaction (must be at least 1).</param>
        /// <param name="initialDelayMs">Base backoff delay in milliseconds (must be non-negative).</param>
        /// <param name="maxElapsed">
        /// Optional time budget for the whole call, measured from the first attempt (must be non-negative). A retry
        /// whose wait would end past it, or did end past it, is not made and the last fault propagates. Null (the
        /// default) bounds only each wait, by <see cref="MaxDelay"/>.
        /// </param>
        /// <returns>Operation result.</returns>
        public static T Execute<T>(
            ILocalPluginContext context,
            Func<T> operation,
            bool retryOnTimeout = false,
            int maxAttempts = 3,
            int initialDelayMs = 100,
            TimeSpan? maxElapsed = null)
            => Execute(context, operation, retryOnTimeout, maxAttempts, initialDelayMs, maxElapsed, Thread.Sleep);

        // The public entry point with the wait made explicit, so a test can make a wait overrun deterministically.
        internal static T Execute<T>(
            ILocalPluginContext context,
            Func<T> operation,
            bool retryOnTimeout,
            int maxAttempts,
            int initialDelayMs,
            TimeSpan? maxElapsed,
            Action<TimeSpan> sleep)
        {
            context = context ?? throw new ArgumentNullException(nameof(context));
            if (context.IsInTransaction && maxAttempts > 1)
            {
                context.Trace("RetryPolicy: the execution is in a database transaction, so the operation runs once without retry.");
                maxAttempts = 1;
            }

            return ExecuteCore(operation, retryOnTimeout, maxAttempts, initialDelayMs, maxElapsed, sleep);
        }

        private static bool ShouldRetry(Exception exception, bool retryOnTimeout)
        {
            if (IsThrottling(exception))
            {
                return true;
            }

            return retryOnTimeout && IsTimeout(exception);
        }

        private static bool IsThrottling(Exception exception)
        {
            if (exception is FaultException<OrganizationServiceFault> fault && fault.Detail != null)
            {
                int code = fault.Detail.ErrorCode;
                return code == NumberOfRequestsCode || code == ExecutionTimeCode || code == ConcurrencyCode;
            }

            return false;
        }

        private static bool IsTimeout(Exception exception)
        {
            if (exception is TimeoutException || exception is WebException)
            {
                return true;
            }

            if (exception is FaultException<OrganizationServiceFault> fault && fault.Detail != null)
            {
                return (fault.Detail.Message ?? string.Empty).IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        // The wait before the next attempt: the fault's Retry-After when it has one (false when that exceeds the cap,
        // so the fault propagates rather than being retried too early), else exponential backoff with full jitter.
        private static bool TryComputeDelay(Exception exception, int attempt, int initialDelayMs, out TimeSpan delay)
        {
            TimeSpan? retryAfter = ReadRetryAfter(exception);
            if (retryAfter.HasValue)
            {
                delay = retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value;
                return delay <= MaxDelay;
            }

            delay = ComputeDelay(attempt, initialDelayMs);
            return true;
        }

        private static TimeSpan? ReadRetryAfter(Exception exception)
        {
            if (exception is FaultException<OrganizationServiceFault> fault
                && fault.Detail?.ErrorDetails != null
                && fault.Detail.ErrorDetails.TryGetValue(RetryAfterKey, out object? value))
            {
                return value switch
                {
                    TimeSpan span => span,
                    int seconds => TimeSpan.FromSeconds(seconds),
                    double seconds => TimeSpan.FromSeconds(seconds),
                    string text when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan parsed) => parsed,
                    _ => null,
                };
            }

            return null;
        }

        private static int NextSeed()
        {
            lock (SeedSource)
            {
                return SeedSource.Next();
            }
        }

        private static TimeSpan ComputeDelay(int attempt, int initialDelayMs)
        {
            // Exponential base with full jitter: sleep a random point in [0, initialDelay * 2^(attempt-1)], capped so
            // a sandbox thread never sleeps too long.
            double exponential = initialDelayMs * Math.Pow(2, attempt - 1);
            int ceiling = (int)Math.Min(exponential, MaxBackoffMs);
            int delay = ceiling <= 0 ? 0 : Rng.Next(0, ceiling + 1);
            return TimeSpan.FromMilliseconds(delay);
        }
    }
}
