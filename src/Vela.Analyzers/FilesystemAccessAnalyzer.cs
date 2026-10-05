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
    /// type-identity check (not a behavioural guess), so it is precise: in-memory types (<c>MemoryStream</c>,
    /// <c>StringReader</c>/<c>Writer</c>, <c>BinaryReader</c>/<c>Writer</c>) and the pure-string <c>System.IO.Path</c>
    /// helpers are never flagged, and an embedded resource read via <c>Assembly.GetManifestResourceStream</c> (a
    /// <c>Stream</c>, not a filesystem type) is fine.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class FilesystemAccessAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "VELA019";

        // Static classes whose every member hits the filesystem - flagged on member access (they are never instantiated).
        private static readonly ImmutableHashSet<string> StaticFilesystemTypes =
            ImmutableHashSet.Create("File", "Directory");

        // Instantiable types whose constructor opens a path - flagged on object creation (their member accesses are
        // left alone to avoid double-reporting the same usage).
        private static readonly ImmutableHashSet<string> InstantiableFilesystemTypes =
            ImmutableHashSet.Create("FileStream", "FileInfo", "DirectoryInfo", "DriveInfo", "FileSystemWatcher");

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Filesystem access in a plugin",
            messageFormat: "'{0}' accesses the filesystem, which the Dataverse sandbox denies - it throws at runtime",
            category: "Vela.Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The Dataverse sandbox denies file I/O, so System.IO filesystem types (File, Directory, FileStream, FileInfo, DirectoryInfo, DriveInfo, FileSystemWatcher) throw at runtime in a plugin. Keep data in memory (MemoryStream), read embedded resources via Assembly.GetManifestResourceStream, or move file work out of the sandbox.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creation = (ObjectCreationExpressionSyntax)context.Node;
            if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type)
            {
                return;
            }

            if (!IsFilesystemType(type, InstantiableFilesystemTypes) || !InPluginType(context))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Type.GetLocation(), type.Name));
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var member = (MemberAccessExpressionSyntax)context.Node;

            // The containing type of the accessed member (e.g. File for File.ReadAllText). Resolve the member's
            // symbol; its ContainingType is the filesystem class.
            if (context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol?.ContainingType is not INamedTypeSymbol type)
            {
                return;
            }

            if (!IsFilesystemType(type, StaticFilesystemTypes) || !InPluginType(context))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, member.GetLocation(), type.Name));
        }

        private static bool IsFilesystemType(INamedTypeSymbol type, ImmutableHashSet<string> names)
            => names.Contains(type.Name) && type.ContainingNamespace?.ToDisplayString() == "System.IO";

        private static bool InPluginType(SyntaxNodeAnalysisContext context)
        {
            for (SyntaxNode? current = context.Node; current is not null; current = current.Parent)
            {
                if (current is TypeDeclarationSyntax type)
                {
                    return PluginTypes.IsPluginType(context.SemanticModel.GetDeclaredSymbol(type, context.CancellationToken));
                }
            }

            return false;
        }
    }
}
