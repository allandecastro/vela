using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vela.Analyzers
{
    /// <summary>
    /// Flags filesystem access inside a plugin / Custom API type. The Dataverse sandbox denies file I/O
    /// (<c>FileIOPermission</c>), so a call to <c>System.IO.File</c> / <c>Directory</c> or a <c>new FileStream</c> /
    /// <c>FileInfo</c> / <c>DirectoryInfo</c> / <c>DriveInfo</c> / <c>FileSystemWatcher</c> throws at runtime. This is a
    /// type-identity check (not a behavioural guess), so it is precise: a <c>StreamReader</c> / <c>StreamWriter</c> is flagged
    /// only when built from a path, a <c>File</c> / <c>Directory</c> call also through <c>using static</c>; in-memory
    /// types (<c>MemoryStream</c>, <c>StringReader</c>/<c>Writer</c>, <c>BinaryReader</c>/<c>Writer</c>, a stream reader
    /// over a <c>Stream</c>) and the pure-string <c>System.IO.Path</c>
    /// helpers are never flagged, and an embedded resource read via <c>Assembly.GetManifestResourceStream</c> (a
    /// <c>Stream</c>, not a filesystem type) is fine.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class FilesystemAccessAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.FilesystemAccess;

        // Static classes whose every member hits the filesystem - flagged on member access (they are never instantiated).
        private static readonly ImmutableHashSet<string> StaticFilesystemTypes =
            ImmutableHashSet.Create("File", "Directory");

        // Instantiable types whose constructor opens a path - flagged on object creation (their member accesses are
        // left alone to avoid double-reporting the same usage).
        private static readonly ImmutableHashSet<string> InstantiableFilesystemTypes =
            ImmutableHashSet.Create("FileStream", "FileInfo", "DirectoryInfo", "DriveInfo", "FileSystemWatcher");

        // Readers and writers that open a file only through their path constructors (new StreamReader(path)); over a
        // Stream (new StreamReader(memoryStream)) they are in-memory and left alone.
        private static readonly ImmutableHashSet<string> PathConstructedTypes =
            ImmutableHashSet.Create("StreamReader", "StreamWriter");

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Filesystem access in a plugin",
            messageFormat: "'{0}' accesses the filesystem, which the Dataverse sandbox denies - it throws at runtime",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The Dataverse sandbox denies file I/O, so System.IO filesystem types (File, Directory, FileStream, FileInfo, DirectoryInfo, DriveInfo, FileSystemWatcher, and a StreamReader / StreamWriter opened on a path) throw at runtime in a plugin. Keep data in memory (MemoryStream), read embedded resources via Assembly.GetManifestResourceStream, or move file work out of the sandbox.");

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

                start.RegisterSyntaxNodeAction(c => AnalyzeObjectCreation(c, plugins), SyntaxKind.ObjectCreationExpression);
                start.RegisterSyntaxNodeAction(c => AnalyzeMemberAccess(c, plugins), SyntaxKind.SimpleMemberAccessExpression);
                start.RegisterSyntaxNodeAction(c => AnalyzeUnqualifiedCall(c, plugins), SyntaxKind.InvocationExpression);
            });
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context, PluginTypes plugins)
        {
            var creation = (ObjectCreationExpressionSyntax)context.Node;
            if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type)
            {
                return;
            }

            bool opensPath = IsFilesystemType(type, InstantiableFilesystemTypes)
                || (IsFilesystemType(type, PathConstructedTypes) && TakesPath(creation, context.SemanticModel, context.CancellationToken));
            if (!opensPath || !plugins.Encloses(context.Node, context.SemanticModel, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Type.GetLocation(), type.Name));
        }

        // The bound constructor's first parameter is the path string (StreamReader(string path, ...)), not a Stream.
        private static bool TakesPath(ObjectCreationExpressionSyntax creation, SemanticModel model, System.Threading.CancellationToken cancellationToken)
            => model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol constructor
                && constructor.Parameters.Length > 0
                && constructor.Parameters[0].Type.SpecialType == SpecialType.System_String;

        // A File / Directory member called without its type name, through `using static System.IO.File;`
        // (ReadAllText(path)). A qualified call (File.ReadAllText) is a member access, reported above.
        private static void AnalyzeUnqualifiedCall(SyntaxNodeAnalysisContext context, PluginTypes plugins)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression is not SimpleNameSyntax name
                || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol { IsStatic: true } method
                || !IsFilesystemType(method.ContainingType, StaticFilesystemTypes)
                || !plugins.Encloses(context.Node, context.SemanticModel, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), method.ContainingType.Name));
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context, PluginTypes plugins)
        {
            var member = (MemberAccessExpressionSyntax)context.Node;

            // The containing type of the accessed member (e.g. File for File.ReadAllText). Resolve the member's
            // symbol; its ContainingType is the filesystem class.
            if (context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol?.ContainingType is not INamedTypeSymbol type)
            {
                return;
            }

            if (!IsFilesystemType(type, StaticFilesystemTypes) || !plugins.Encloses(context.Node, context.SemanticModel, context.CancellationToken))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, member.GetLocation(), type.Name));
        }

        private static bool IsFilesystemType(INamedTypeSymbol type, ImmutableHashSet<string> names)
            => names.Contains(type.Name) && type.ContainingNamespace?.ToDisplayString() == "System.IO";
    }
}
