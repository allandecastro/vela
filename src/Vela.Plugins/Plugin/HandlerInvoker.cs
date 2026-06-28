using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Xrm.Sdk;

namespace Vela.Plugin
{
    /// <summary>
    /// Resolves and invokes a named handler method on a plugin / Custom API instance.
    /// Centralizes the resolve → validate-signature → invoke flow shared by step plugins and
    /// Custom APIs, and guarantees that the business exception thrown by a handler is rethrown
    /// unwrapped (without the surrounding <see cref="TargetInvocationException"/>) so that the
    /// Dataverse error mapping sees the original <see cref="InvalidPluginExecutionException"/>.
    /// </summary>
    internal static class HandlerInvoker
    {
        /// <summary>
        /// Resolves the single handler method matching <paramref name="methodName"/> and
        /// <paramref name="signaturePredicate"/>, then invokes it with <paramref name="arguments"/>.
        /// </summary>
        /// <param name="instance">The plugin / Custom API instance owning the handler.</param>
        /// <param name="methodName">The configured handler method name.</param>
        /// <param name="signaturePredicate">Predicate validating the expected method signature.</param>
        /// <param name="arguments">Arguments passed to the handler, in declaration order.</param>
        /// <param name="subjectLabel">Caller label for diagnostics, e.g. <c>Plugin 'Foo'</c>.</param>
        /// <param name="handlerNoun">Handler kind for diagnostics, e.g. <c>step handler</c> or <c>handler</c>.</param>
        /// <param name="expectedSignature">Human-readable expected signature for the mismatch message.</param>
        public static void ResolveAndInvoke(
            object instance,
            string methodName,
            Func<MethodInfo, bool> signaturePredicate,
            object[] arguments,
            string subjectLabel,
            string handlerNoun,
            string expectedSignature)
        {
            MethodInfo method = Resolve(instance.GetType(), methodName, signaturePredicate, subjectLabel, handlerNoun, expectedSignature);
            Invoke(method, instance, arguments);
        }

        private static MethodInfo Resolve(
            Type type,
            string methodName,
            Func<MethodInfo, bool> signaturePredicate,
            string subjectLabel,
            string handlerNoun,
            string expectedSignature)
        {
            MethodInfo[] namedMethods = HandlerResolver.GetNamedMethods(type, methodName);
            if (namedMethods.Length == 0)
            {
                throw new InvalidPluginExecutionException(
                    $"{subjectLabel} {handlerNoun} '{methodName}' was not found.");
            }

            MethodInfo[] matchingMethods = namedMethods.Where(signaturePredicate).ToArray();
            if (matchingMethods.Length == 0)
            {
                throw new InvalidPluginExecutionException(
                    $"{subjectLabel} {handlerNoun} '{methodName}' must match signature: {expectedSignature}.");
            }

            if (matchingMethods.Length > 1)
            {
                throw new InvalidPluginExecutionException(
                    $"{subjectLabel} has multiple overloads for {handlerNoun} '{methodName}'.");
            }

            return matchingMethods[0];
        }

        private static void Invoke(MethodInfo method, object instance, object[] arguments)
        {
            try
            {
                method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
