using System;
using System.Net;
using System.ServiceModel;
using System.Threading;
using Microsoft.Xrm.Sdk;

namespace Vela.Retry
{
    /// <summary>
    /// Lightweight transient retry policy for sandbox-safe service calls.
    /// </summary>
    public static class RetryPolicy
    {
        /// <summary>
        /// Executes an operation with exponential backoff retries.
        /// </summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="operation">Operation to execute.</param>
        /// <param name="maxAttempts">Maximum attempts.</param>
        /// <param name="initialDelayMs">Initial delay in milliseconds.</param>
        /// <returns>Operation result.</returns>
        public static T Execute<T>(Func<T> operation, int maxAttempts = 3, int initialDelayMs = 100)
        {
            operation = operation ?? throw new ArgumentNullException(nameof(operation));

            Exception? lastException = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
                {
                    lastException = ex;
                    Thread.Sleep(initialDelayMs * attempt);
                }
            }

            throw lastException ?? new InvalidPluginExecutionException("Retry policy failed without exception detail.");
        }

        private static bool IsTransient(Exception exception)
        {
            if (exception is TimeoutException || exception is WebException)
            {
                return true;
            }

            if (exception is FaultException<OrganizationServiceFault> fault && fault.Detail != null)
            {
                string message = fault.Detail.Message ?? string.Empty;
                return message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("throttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("temporar", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }
    }
}
