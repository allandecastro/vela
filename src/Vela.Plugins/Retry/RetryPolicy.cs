using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.ServiceModel;
using System.Threading;
using Microsoft.Xrm.Sdk;

namespace Vela.Retry
{
    /// <summary>
    /// Lightweight transient retry policy for sandbox-safe service calls.
    /// </summary>
    /// <remarks>
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
    /// Backoff is exponential with full jitter (<c>initialDelayMs * 2^(attempt-1)</c>, then a random point in
    /// <c>[0, that]</c>) to avoid a synchronized retry storm, capped so a sandbox thread never sleeps too long. When
    /// a throttling fault carries a server <c>Retry-After</c> hint it is honored instead of the computed backoff.
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

        [ThreadStatic]
        private static Random? _rng;

        private static Random Rng => _rng ??= new Random();

        /// <summary>
        /// Executes an operation, retrying only safe-to-replay throttling faults.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Operation to execute. Must be idempotent to be safe under retry.</param>
        /// <param name="maxAttempts">Maximum attempts (must be at least 1).</param>
        /// <param name="initialDelayMs">Base backoff delay in milliseconds (must be non-negative).</param>
        /// <returns>Operation result.</returns>
        public static T Execute<T>(Func<T> operation, int maxAttempts = 3, int initialDelayMs = 100)
        {
            return Execute(operation, retryOnTimeout: false, maxAttempts, initialDelayMs);
        }

        /// <summary>
        /// Executes an operation with retries. Throttling faults are always retried; timeouts are retried only when
        /// <paramref name="retryOnTimeout"/> is set, which the caller must do only for an idempotent operation.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Operation to execute.</param>
        /// <param name="retryOnTimeout">
        /// When true, also retries timeouts. Only pass true for an idempotent operation (a read, or a write that is
        /// safe to replay); a non-idempotent write may already have committed before the timeout surfaced.
        /// </param>
        /// <param name="maxAttempts">Maximum attempts (must be at least 1).</param>
        /// <param name="initialDelayMs">Base backoff delay in milliseconds (must be non-negative).</param>
        /// <returns>Operation result.</returns>
        public static T Execute<T>(Func<T> operation, bool retryOnTimeout, int maxAttempts = 3, int initialDelayMs = 100)
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

            Exception? lastException = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (Exception ex) when (attempt < maxAttempts && ShouldRetry(ex, retryOnTimeout))
                {
                    lastException = ex;
                    Thread.Sleep(ComputeDelay(ex, attempt, initialDelayMs));
                }
            }

            throw lastException ?? new InvalidPluginExecutionException("Retry policy failed without exception detail.");
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
                if (code == NumberOfRequestsCode || code == ExecutionTimeCode || code == ConcurrencyCode)
                {
                    return true;
                }

                string message = fault.Detail.Message ?? string.Empty;
                return message.IndexOf("throttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("temporar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private static TimeSpan ComputeDelay(Exception exception, int attempt, int initialDelayMs)
        {
            TimeSpan? retryAfter = GetRetryAfter(exception);
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                return retryAfter.Value < TimeSpan.FromMilliseconds(MaxBackoffMs)
                    ? retryAfter.Value
                    : TimeSpan.FromMilliseconds(MaxBackoffMs);
            }

            // Exponential base with full jitter: sleep a random point in [0, initialDelay * 2^(attempt-1)], capped.
            double exponential = initialDelayMs * Math.Pow(2, attempt - 1);
            int ceiling = (int)Math.Min(exponential, MaxBackoffMs);
            int delay = ceiling <= 0 ? 0 : Rng.Next(0, ceiling + 1);
            return TimeSpan.FromMilliseconds(delay);
        }

        private static TimeSpan? GetRetryAfter(Exception exception)
        {
            if (exception is not FaultException<OrganizationServiceFault> fault || fault.Detail == null)
            {
                return null;
            }

            ErrorDetailCollection details = fault.Detail.ErrorDetails;
            if (details == null || !details.TryGetValue("Retry-After", out object value) || value == null)
            {
                return null;
            }

            switch (value)
            {
                case TimeSpan timeSpan:
                    return timeSpan;
                case int seconds:
                    return TimeSpan.FromSeconds(seconds);
                case double secondsDouble:
                    return TimeSpan.FromSeconds(secondsDouble);
                case string text when double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed):
                    return TimeSpan.FromSeconds(parsed);
                default:
                    return null;
            }
        }
    }
}
