using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags a <c>new HttpClient(...)</c> in a plugin / Custom API type whose <c>Timeout</c> is never set. The default
    /// <c>HttpClient</c> timeout (100 seconds) is close to the time a plug-in has to complete, so Microsoft recommends
    /// setting an explicit, short <c>Timeout</c> on outbound calls. The check is local to the member that creates the
    /// client (a method, constructor, accessor, or a field / property initializer of the plugin type, lambdas and
    /// local functions in it included): the client is fine when its object initializer sets <c>Timeout</c>, or when the
    /// local, field or property it is stored in gets <c>.Timeout = ...</c> anywhere in that member. One level only:
    /// a client returned from a method or local function, passed as an argument, or copied into another variable is
    /// not followed and not reported. A client a lambda returns (a <c>services.AddScoped(_ => new HttpClient())</c>
    /// factory in <c>ConfigureServices</c>) is checked like any other.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HttpClientTimeoutAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.HttpClientTimeout;

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "HttpClient without a timeout",
            messageFormat: "This HttpClient never sets Timeout; its 100-second default is close to the time a plug-in has to complete",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Microsoft recommends setting an explicit Timeout on outbound calls from a plug-in so a slow service fails fast instead of holding the operation: the default HttpClient timeout is 100 seconds. Set Timeout in the object initializer or on the variable that holds the client, in the member that creates it.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                PluginTypes? plugins = PluginTypes.From(start.Compilation);
                INamedTypeSymbol? httpClient = start.Compilation.GetTypeByMetadataName("System.Net.Http.HttpClient");
                if (plugins is null || httpClient is null)
                {
                    return; // no plugin type, or no HttpClient to create
                }

                start.RegisterOperationAction(c => AnalyzeCreation(c, plugins, httpClient), OperationKind.ObjectCreation);
            });
        }

        private static void AnalyzeCreation(OperationAnalysisContext context, PluginTypes plugins, INamedTypeSymbol httpClient)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (!SymbolEqualityComparer.Default.Equals(creation.Type, httpClient) || SetsTimeout(creation.Initializer, httpClient))
            {
                return;
            }

            if (NeedsTimeout(creation, httpClient) && plugins.Encloses(creation.Syntax, creation.SemanticModel!, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Syntax.GetLocation()));
            }
        }

        // Where the new client goes decides whether it is reported: used in place, stored in a local / field /
        // property of this member, or returned by a lambda -> checked; anything else (returned from a method, passed
        // as an argument, part of a larger expression) -> not followed.
        private static bool NeedsTimeout(IObjectCreationOperation creation, INamedTypeSymbol httpClient)
        {
            IOperation node = creation;
            IOperation? parent = node.Parent;
            while (parent is IConversionOperation)
            {
                node = parent;
                parent = parent.Parent;
            }

            switch (parent)
            {
                case IInvocationOperation invocation when invocation.Instance == node:
                case IPropertyReferenceOperation property when property.Instance == node:
                case IFieldInitializerOperation:
                case IPropertyInitializerOperation:
                    return true;
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                    return !TimeoutSetOrEscapes(Root(creation), declarator.Symbol, httpClient);
                case ISimpleAssignmentOperation assignment when assignment.Value == node:
                    ISymbol? target = Referenced(assignment.Target);
                    return target is not null && !TimeoutSetOrEscapes(Root(creation), target, httpClient);
                case IReturnOperation ret:
                    return ReturnsFromLambda(ret);
                default:
                    return false;
            }
        }

        private static bool ReturnsFromLambda(IReturnOperation ret)
        {
            for (IOperation? current = ret.Parent; current is not null; current = current.Parent)
            {
                if (current is IAnonymousFunctionOperation)
                {
                    return true;
                }

                if (current is ILocalFunctionOperation)
                {
                    return false;
                }
            }

            return false; // a method's own return: the caller owns the client
        }

        private static IOperation Root(IOperation operation)
        {
            while (operation.Parent is not null)
            {
                operation = operation.Parent;
            }

            return operation;
        }

        private static ISymbol? Referenced(IOperation operation) => operation switch
        {
            ILocalReferenceOperation local => local.Local,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation { Arguments.Length: 0 } property => property.Property,
            _ => null,
        };

        // True when the member sets .Timeout on the symbol (in any order), or hands a local holding the client to
        // someone else (an argument, a method's return, an assignment into another variable).
        private static bool TimeoutSetOrEscapes(IOperation root, ISymbol holder, INamedTypeSymbol httpClient)
        {
            foreach (IOperation operation in root.Descendants())
            {
                if (operation is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } &&
                    IsTimeout(target.Property, httpClient) && target.Instance is not null &&
                    SymbolEqualityComparer.Default.Equals(Referenced(Unwrap(target.Instance)), holder))
                {
                    return true;
                }

                if (holder is ILocalSymbol && operation is ILocalReferenceOperation reference &&
                    SymbolEqualityComparer.Default.Equals(reference.Local, holder) && Escapes(reference))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Escapes(IOperation reference)
        {
            IOperation node = reference;
            IOperation? parent = node.Parent;
            while (parent is IConversionOperation)
            {
                node = parent;
                parent = parent.Parent;
            }

            return parent switch
            {
                // The receiver of an extension method (client.GetFromJsonAsync(...)) is a use, not a hand-off.
                IArgumentOperation argument => argument.Parameter is not { Ordinal: 0, ContainingSymbol: IMethodSymbol { IsExtensionMethod: true } },
                IReturnOperation ret => !ReturnsFromLambda(ret),
                ISimpleAssignmentOperation assignment => assignment.Value == node,
                IVariableInitializerOperation => true,
                _ => false,
            };
        }

        private static IOperation Unwrap(IOperation operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }

            return operation;
        }

        private static bool SetsTimeout(IObjectOrCollectionInitializerOperation? initializer, INamedTypeSymbol httpClient)
        {
            if (initializer is null)
            {
                return false;
            }

            foreach (IOperation entry in initializer.Initializers)
            {
                if (entry is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } && IsTimeout(target.Property, httpClient))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTimeout(IPropertySymbol property, INamedTypeSymbol httpClient)
            => property.Name == "Timeout" && SymbolEqualityComparer.Default.Equals(property.ContainingType, httpClient);
    }
}
