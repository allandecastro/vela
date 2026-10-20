using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags an outbound HTTP call to an IP address from plugin code: a plugin / Custom API type, or any type of a
    /// plugin assembly (its services and helpers). The Dataverse sandbox only lets a
    /// plug-in reach a named host resolved by DNS, so a request to <c>http(s)://10.0.0.1/...</c> or
    /// <c>http(s)://[::1]/...</c> fails at runtime. Checked: the string given to <c>new Uri(...)</c>, to an
    /// <c>HttpClient</c> / <c>HttpRequestMessage</c> / <c>WebRequest</c> / <c>HttpWebRequest</c> member or constructor
    /// (<c>BaseAddress = new Uri(...)</c> goes through the <c>Uri</c>), when it is a compile-time constant, or an
    /// interpolated string / concatenation whose constant prefix already holds the whole host. A local is followed to
    /// its declaration initializer (through a chain of such locals too) when nothing else in the member writes it: no
    /// assignment, compound assignment, increment, deconstruction, <c>ref</c> / <c>out</c> / <c>in</c> argument or
    /// <c>ref</c> alias. The host is parsed with <see cref="Uri"/>, not guessed: only <c>http</c> / <c>https</c> URIs
    /// whose host is an IPv4 or IPv6 literal are reported, so a named host (<c>localhost</c> included), a relative or
    /// non-HTTP string, or a URI built from a field, a parameter, a method result or a local written after its
    /// declaration is left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class IpAddressCallAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.IpAddressCall;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Outbound call to an IP address",
            messageFormat: "'{0}' is an IP address; a plug-in in the Dataverse sandbox can only call a named host resolved by DNS",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The Dataverse sandbox does not let a plug-in call an IP address: outbound HTTP/HTTPS calls must use a named web address that requires DNS name resolution. Give the service a DNS name and call that.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                if (plugins is null)
                {
                    return; // neither the Dataverse SDK nor Vela: no plugin type to analyze
                }

                start.RegisterOperationAction(c => AnalyzeArgument(c, plugins), OperationKind.Argument);
            });
        }

        private static void AnalyzeArgument(OperationAnalysisContext context, PluginTypes plugins)
        {
            var argument = (IArgumentOperation)context.Operation;
            if (argument.Parameter is not { Type.SpecialType: SpecialType.System_String } parameter ||
                !IsHttpSink(parameter.ContainingSymbol))
            {
                return;
            }

            string? host = IpHost(argument.Value);
            if (host is not null && plugins.InPluginAssembly(argument.Syntax, argument.SemanticModel!, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Value.Syntax.GetLocation(), host));
            }
        }

        // A string parameter of a Uri constructor, or of a method / constructor of the HTTP client types.
        private static bool IsHttpSink(ISymbol member)
        {
            if (member is not IMethodSymbol { ContainingType: INamedTypeSymbol type } method)
            {
                return false;
            }

            string? ns = type.ContainingNamespace?.ToDisplayString();
            switch (type.Name)
            {
                case "Uri":
                    return ns == "System" && method.MethodKind == MethodKind.Constructor;
                case "HttpClient":
                case "HttpRequestMessage":
                    return ns == "System.Net.Http";
                case "WebRequest":
                case "HttpWebRequest":
                    return ns == "System.Net";
                default:
                    return false;
            }
        }

        /// <summary>The IP host of <paramref name="value"/> when it is an http(s) URI to an IP literal, else null.</summary>
        internal static string? IpHost(IOperation value)
        {
            value = Resolve(value);
            if (value.ConstantValue is { HasValue: true, Value: string constant })
            {
                return IpHostOf(constant);
            }

            string? prefix = ConstantPrefix(value);
            return prefix is null ? null : IpHostOfPrefix(prefix);
        }

        // The constant text an interpolated string or a string concatenation starts with.
        private static string? ConstantPrefix(IOperation value)
        {
            switch (value)
            {
                case IInterpolatedStringOperation interpolated:
                    var text = new StringBuilder();
                    foreach (IInterpolatedStringContentOperation part in interpolated.Parts)
                    {
                        if (part is not IInterpolatedStringTextOperation { Text.ConstantValue: { HasValue: true, Value: string s } })
                        {
                            break;
                        }

                        text.Append(s);
                    }

                    return text.Length == 0 ? null : text.ToString();
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary:
                    IOperation left = Resolve(binary.LeftOperand);
                    return left.ConstantValue is { HasValue: true, Value: string constant } ? constant : ConstantPrefix(left);
                default:
                    return null;
            }
        }

        // The value behind conversions and single-assignment locals: string url = "..."; new Uri(url) reads the literal.
        private static IOperation Resolve(IOperation value)
        {
            var seen = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            while (true)
            {
                while (value is IConversionOperation conversion)
                {
                    value = conversion.Operand;
                }

                if (value is not ILocalReferenceOperation reference || !seen.Add(reference.Local) ||
                    InitializerOfUnwrittenLocal(reference) is not IOperation initializer)
                {
                    return value;
                }

                value = initializer;
            }
        }

        // The declaration initializer of the referenced local when nothing else in its member writes it, else null.
        private static IOperation? InitializerOfUnwrittenLocal(ILocalReferenceOperation reference)
        {
            ILocalSymbol local = reference.Local;
            IOperation root = reference;
            while (root.Parent is IOperation parent)
            {
                root = parent;
            }

            IOperation? initializer = null;
            foreach (IOperation operation in root.Descendants())
            {
                switch (operation)
                {
                    case IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local):
                        initializer = declarator.GetVariableInitializer()?.Value;
                        break;
                    case ILocalReferenceOperation other when SymbolEqualityComparer.Default.Equals(other.Local, local) && IsWritten(other):
                        return null;
                }
            }

            return initializer;
        }

        // A reference that may change the local: assigned (also compound or ??=), incremented, part of a tuple (a
        // deconstruction target), passed by ref / out / in, or aliased by ref (ref string r = ref url).
        private static bool IsWritten(ILocalReferenceOperation reference)
        {
            switch (reference.Parent)
            {
                case IAssignmentOperation assignment when assignment.Target == reference:
                case IIncrementOrDecrementOperation:
                case ITupleOperation:
                case IArgumentOperation { Parameter.RefKind: not RefKind.None }:
                    return true;
                default:
                    return reference.Syntax.Parent is RefExpressionSyntax;
            }
        }

        // A prefix counts only once it holds the whole host: it is followed by '/', '?', '#' or a ':port' in the
        // prefix itself ("https://10.0.0.1/" + path), not cut inside it ($"https://10.0.0.{n}/").
        internal static string? IpHostOfPrefix(string prefix)
        {
            int scheme = prefix.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0)
            {
                return null;
            }

            string rest = prefix.Substring(scheme + 3);
            int end = rest.IndexOfAny(new[] { '/', '?', '#' });
            if (end < 0)
            {
                int bracket = rest.StartsWith("[", StringComparison.Ordinal) ? rest.IndexOf(']') : -1;
                end = bracket >= 0
                    ? (bracket + 1 < rest.Length && rest[bracket + 1] == ':' ? bracket + 1 : -1)
                    : rest.LastIndexOf(':');
                if (end < 0)
                {
                    return null;
                }
            }

            return IpHostOf(prefix.Substring(0, scheme + 3) + rest.Substring(0, end) + "/");
        }

        internal static string? IpHostOf(string candidate)
        {
            if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ? uri.Host : null;
        }
    }
}
