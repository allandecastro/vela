using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags parallel execution and blocking on a task inside a plugin / Custom API type. Microsoft's guidance is not to
    /// use parallel execution in plug-ins: the execution context and the organization services are not thread-safe,
    /// work on another thread runs outside the step's transaction, and a background thread can outlive the step.
    /// Flagged: <c>Task.Run</c>, <c>TaskFactory.StartNew</c>, <c>Parallel.For</c> / <c>ForEach</c> / <c>Invoke</c>,
    /// PLINQ <c>AsParallel</c>, <c>ThreadPool.QueueUserWorkItem</c>, <c>new Thread(...)</c>, and blocking on a task
    /// with <c>.Result</c>, <c>.Wait()</c>, <c>Task.WaitAll</c> / <c>WaitAny</c> or <c>GetAwaiter().GetResult()</c>.
    /// Like VELA019, code outside a plugin type (tests, tools, a nested helper type) is left alone.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ParallelExecutionAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.ParallelExecution;

        internal const string StartsParallelWork = "starts parallel work";
        internal const string BlocksOnTask = "blocks on a task";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Parallel execution in a plugin",
            messageFormat: "'{0}' {1} in a plugin; plug-in code must run on the calling thread",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Microsoft advises against parallel execution in plug-ins: the execution context and the organization services are not thread-safe, work on another thread runs outside the step's transaction and can outlive it, and blocking on a task risks deadlocks and timeouts. Run the work sequentially, or move it out of the sandbox (an asynchronous step, a Power Automate flow, an Azure function).");

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

                start.RegisterOperationAction(c => AnalyzeInvocation(c, plugins), OperationKind.Invocation);
                start.RegisterOperationAction(c => AnalyzeCreation(c, plugins), OperationKind.ObjectCreation);
                start.RegisterOperationAction(c => AnalyzePropertyReference(c, plugins), OperationKind.PropertyReference);
            });
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, PluginTypes plugins)
        {
            IMethodSymbol method = ((IInvocationOperation)context.Operation).TargetMethod;
            string? shape = ClassifyMethod(method);
            if (shape is not null && InPluginType(context, plugins))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), method.ContainingType.Name + "." + method.Name, shape));
            }
        }

        private static void AnalyzeCreation(OperationAnalysisContext context, PluginTypes plugins)
        {
            if (((IObjectCreationOperation)context.Operation).Type is INamedTypeSymbol { Name: "Thread" } type &&
                InNamespace(type, "System.Threading") && InPluginType(context, plugins))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), "new Thread", StartsParallelWork));
            }
        }

        private static void AnalyzePropertyReference(OperationAnalysisContext context, PluginTypes plugins)
        {
            IPropertySymbol property = ((IPropertyReferenceOperation)context.Operation).Property;
            if (property.Name == "Result" && IsTaskType(property.ContainingType) && InPluginType(context, plugins))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), property.ContainingType.Name + ".Result", BlocksOnTask));
            }
        }

        private static string? ClassifyMethod(IMethodSymbol method)
        {
            INamedTypeSymbol type = method.ContainingType;
            switch (type.Name)
            {
                case "Task" when InNamespace(type, "System.Threading.Tasks"):
                    return method.Name switch
                    {
                        "Run" => StartsParallelWork,
                        "Wait" or "WaitAll" or "WaitAny" => BlocksOnTask,
                        _ => null,
                    };
                case "TaskFactory" when InNamespace(type, "System.Threading.Tasks") && method.Name == "StartNew":
                case "Parallel" when InNamespace(type, "System.Threading.Tasks"):
                case "ParallelEnumerable" when InNamespace(type, "System.Linq") && method.Name == "AsParallel":
                case "ThreadPool" when InNamespace(type, "System.Threading") &&
                                       (method.Name == "QueueUserWorkItem" || method.Name == "UnsafeQueueUserWorkItem"):
                    return StartsParallelWork;
                // task.GetAwaiter().GetResult(), and its ConfigureAwait(...).GetAwaiter().GetResult() form, whose
                // awaiter is nested in ConfiguredTaskAwaitable / ConfiguredValueTaskAwaitable.
                case "TaskAwaiter" or "ValueTaskAwaiter" or "ConfiguredTaskAwaiter" or "ConfiguredValueTaskAwaiter"
                    when method.Name == "GetResult" && InNamespace(type, "System.Runtime.CompilerServices"):
                    return BlocksOnTask;
                default:
                    return null;
            }
        }

        private static bool IsTaskType(INamedTypeSymbol type)
            => (type.Name == "Task" || type.Name == "ValueTask") && InNamespace(type, "System.Threading.Tasks");

        private static bool InNamespace(INamedTypeSymbol type, string ns)
            => type.ContainingNamespace?.ToDisplayString() == ns;

        // The innermost type around the operation (a lambda's or local function's owner included) must be a plugin type.
        private static bool InPluginType(OperationAnalysisContext context, PluginTypes plugins)
            => plugins.Encloses(context.Operation.Syntax, context.Operation.SemanticModel!, context.CancellationToken);
    }
}
