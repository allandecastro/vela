using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vela.Analyzers
{
    /// <summary>
    /// Emits, per step, a projection type that exposes only the columns the step captured in
    /// <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c>. A handler bound with the typed-image
    /// <c>Handle&lt;TImg&gt;(...)</c> terminal then receives that projection, so reading a column the step did not
    /// capture is a compile error instead of a silent <c>null</c>. Generated types land in
    /// <c>&lt;pluginNamespace&gt;.Generated.Images</c>; the generator is purely additive (it never rewrites the
    /// fluent chain) and silently skips anything it cannot resolve statically (the developer keeps the full
    /// early-bound image in that case).
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ImageProjectionGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<ProjectionModel> models = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => IsImageInvocation(node),
                    transform: static (ctx, _) => Extract(ctx))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

            context.RegisterSourceOutput(models.Collect(), static (spc, items) => Emit(spc, items));
        }

        private static bool IsImageInvocation(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return false;
            }

            string name = member.Name.Identifier.ValueText;
            return name == "WithPreImage" || name == "WithPostImage";
        }

        private static ProjectionModel? Extract(GeneratorSyntaxContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            MemberAccessExpressionSyntax member = (MemberAccessExpressionSyntax)invocation.Expression;
            SemanticModel model = context.SemanticModel;

            // Confirm this is Vela's image method (its declaring type implements the fluent step marker), so a
            // same-named method on an unrelated type is ignored.
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                !ImplementsFluentStep(method.ContainingType))
            {
                return null;
            }

            bool isPre = member.Name.Identifier.ValueText == "WithPreImage";

            // Enclosing plugin class -> EntityPluginBase<TEntity>.
            ClassDeclarationSyntax? pluginClass = invocation.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (pluginClass is null || model.GetDeclaredSymbol(pluginClass) is not INamedTypeSymbol pluginSymbol)
            {
                return null;
            }

            INamedTypeSymbol? entity = ResolveEntityType(pluginSymbol);
            if (entity is null)
            {
                return null;
            }

            // Message + stage from the chain that precedes this call.
            (string? messageLabel, string? stageLabel) = ResolveMessageAndStage(member.Expression);
            if (messageLabel is null || stageLabel is null)
            {
                return null;
            }

            // Arguments: optional leading string alias, then the column selector lambdas.
            (string? alias, List<LambdaExpressionSyntax> selectors) = SplitArguments(invocation);
            if (selectors.Count == 0)
            {
                // No explicit columns -> the image carries every column; there is nothing to narrow, so skip.
                return null;
            }

            List<ProjectionColumn> columns = new List<ProjectionColumn>();
            foreach (LambdaExpressionSyntax selector in selectors)
            {
                ProjectionColumn? column = ResolveColumn(selector, model);
                if (column is null)
                {
                    // Any unresolved column makes the projection incomplete; fall back to no generation.
                    return null;
                }

                columns.Add(column.Value);
            }

            string role = isPre ? "PreImage" : "PostImage";
            string aliasSuffix = string.IsNullOrEmpty(alias) ? string.Empty : "_" + Sanitize(alias!);
            string typeName = $"{pluginSymbol.Name}_{messageLabel}{stageLabel}_{role}{aliasSuffix}";
            string ns = pluginSymbol.ContainingNamespace.IsGlobalNamespace
                ? "Generated.Images"
                : pluginSymbol.ContainingNamespace.ToDisplayString() + ".Generated.Images";

            return new ProjectionModel(ns, typeName, columns.ToImmutableArray());
        }

        private static INamedTypeSymbol? ResolveEntityType(INamedTypeSymbol pluginSymbol)
        {
            for (INamedTypeSymbol? t = pluginSymbol.BaseType; t is not null; t = t.BaseType)
            {
                if (t.Name == "EntityPluginBase" && t.TypeArguments.Length == 1 &&
                    t.TypeArguments[0] is INamedTypeSymbol entity)
                {
                    return entity;
                }
            }

            return null;
        }

        private static (string? Message, string? Stage) ResolveMessageAndStage(ExpressionSyntax? receiver)
        {
            string? message = null;
            string? stage = null;

            for (ExpressionSyntax? current = receiver; current is not null;)
            {
                if (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
                {
                    string name = ma.Name.Identifier.ValueText;
                    switch (name)
                    {
                        case "OnCreate": message ??= "Create"; break;
                        case "OnUpdate": message ??= "Update"; break;
                        case "OnDelete": message ??= "Delete"; break;
                        case "OnCreateMultiple": message ??= "CreateMultiple"; break;
                        case "OnUpdateMultiple": message ??= "UpdateMultiple"; break;
                        case "PreValidation": stage ??= "PreVal"; break;
                        case "PreOperation": stage ??= "PreOp"; break;
                        case "PostOperation": stage ??= "PostOp"; break;
                    }

                    // Typed path: On<TRequest>() derives the message from the request type name (DeleteRequest ->
                    // Delete, UpdateRequest<T> -> Update), matching how the runtime maps it.
                    if (message is null && name == "On" && ma.Name is GenericNameSyntax generic &&
                        generic.TypeArgumentList.Arguments.Count == 1)
                    {
                        message = DeriveMessageFromRequest(generic.TypeArgumentList.Arguments[0]);
                    }

                    current = ma.Expression;
                }
                else if (current is MemberAccessExpressionSyntax bare)
                {
                    current = bare.Expression;
                }
                else
                {
                    break;
                }
            }

            return (message, stage);
        }

        // The message label for a typed On<TRequest>() step: the request type's simple name minus a trailing
        // "Request" (DeleteRequest -> Delete, UpdateRequest<T> -> Update). Returns null if the shape is unexpected.
        private static string? DeriveMessageFromRequest(TypeSyntax requestType)
        {
            string? typeName = requestType switch
            {
                GenericNameSyntax g => g.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                _ => null,
            };

            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            const string suffix = "Request";
            return typeName!.Length > suffix.Length && typeName.EndsWith(suffix, System.StringComparison.Ordinal)
                ? typeName.Substring(0, typeName.Length - suffix.Length)
                : typeName;
        }

        private static (string? Alias, List<LambdaExpressionSyntax> Selectors) SplitArguments(InvocationExpressionSyntax invocation)
        {
            string? alias = null;
            List<LambdaExpressionSyntax> selectors = new List<LambdaExpressionSyntax>();

            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                switch (argument.Expression)
                {
                    case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                        alias = literal.Token.ValueText;
                        break;
                    case LambdaExpressionSyntax lambda:
                        selectors.Add(lambda);
                        break;
                }
            }

            return (alias, selectors);
        }

        private static ProjectionColumn? ResolveColumn(LambdaExpressionSyntax selector, SemanticModel model)
        {
            if (selector.Body is not MemberAccessExpressionSyntax memberAccess ||
                model.GetSymbolInfo(memberAccess).Symbol is not IPropertySymbol property)
            {
                return null;
            }

            string? logicalName = GetAttributeLogicalName(property);
            if (logicalName is null)
            {
                return null;
            }

            string typeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new ProjectionColumn(property.Name, typeName, logicalName);
        }

        private static string? GetAttributeLogicalName(IPropertySymbol property)
        {
            foreach (AttributeData attribute in property.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "AttributeLogicalNameAttribute" &&
                    attribute.ConstructorArguments.Length == 1 &&
                    attribute.ConstructorArguments[0].Value is string name &&
                    !string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return null;
        }

        private static bool ImplementsFluentStep(INamedTypeSymbol type)
        {
            if (type.Name == "IFluentStep")
            {
                return true;
            }

            return type.AllInterfaces.Any(static i => i.Name == "IFluentStep");
        }

        private static string Sanitize(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            string result = builder.ToString();
            return result.Length == 0 || char.IsDigit(result[0]) ? "_" + result : result;
        }

        private static void Emit(SourceProductionContext context, ImmutableArray<ProjectionModel> models)
        {
            // Dedup by fully-qualified type name: identical captures (same shape reached twice) collapse to one
            // type; distinct shapes have distinct names by construction (plugin + message + stage + role + alias).
            HashSet<string> emitted = new HashSet<string>();
            foreach (ProjectionModel projectionModel in models)
            {
                string fullName = projectionModel.Namespace + "." + projectionModel.TypeName;
                if (!emitted.Add(fullName))
                {
                    continue;
                }

                context.AddSource(Sanitize(fullName) + ".g.cs", SourceText.From(Render(projectionModel), Encoding.UTF8));
            }
        }

        private static string Render(ProjectionModel model)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/> Vela typed image projection. Do not edit.");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine($"namespace {model.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>Generated projection of the columns captured for this step's image.</summary>");
            sb.AppendLine($"    public sealed class {model.TypeName}");
            sb.AppendLine("    {");
            foreach (ProjectionColumn column in model.Columns)
            {
                sb.AppendLine($"        [global::Microsoft.Xrm.Sdk.AttributeLogicalName(\"{column.LogicalName}\")]");
                sb.AppendLine($"        public {column.TypeName} {column.PropertyName} {{ get; set; }}");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private readonly struct ProjectionColumn
        {
            public ProjectionColumn(string propertyName, string typeName, string logicalName)
            {
                PropertyName = propertyName;
                TypeName = typeName;
                LogicalName = logicalName;
            }

            public string PropertyName { get; }
            public string TypeName { get; }
            public string LogicalName { get; }
        }

        private readonly struct ProjectionModel
        {
            public ProjectionModel(string @namespace, string typeName, ImmutableArray<ProjectionColumn> columns)
            {
                Namespace = @namespace;
                TypeName = typeName;
                Columns = columns;
            }

            public string Namespace { get; }
            public string TypeName { get; }
            public ImmutableArray<ProjectionColumn> Columns { get; }
        }
    }
}
