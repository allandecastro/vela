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
    /// <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c>. The handler reads it via
    /// <c>ctx.PreImageAs&lt;T&gt;()</c> / <c>ctx.PostImageAs&lt;T&gt;()</c>, so reading a column the step did not
    /// capture is a compile error instead of a silent <c>null</c>. Covers the guided (<c>OnCreate()</c>...), typed
    /// (<c>On&lt;TRequest&gt;()</c>) and permissive (<c>OnMessage("...")</c> / <c>OnMessage&lt;TMessage&gt;()</c>)
    /// trigger paths. Generated types land in <c>&lt;pluginNamespace&gt;.Generated.Images</c>; the generator is
    /// purely additive (it never rewrites the fluent chain) and silently skips anything it cannot resolve
    /// statically (the developer keeps the full early-bound image in that case).
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

            var withOptions = models.Collect().Combine(context.ParseOptionsProvider);
            context.RegisterSourceOutput(withOptions, static (spc, pair) => Emit(spc, pair.Left, pair.Right));
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
            // Prefer naming the projection after the (named-method) handler bound with .Handle(...), so a developer
            // can guess it from their own method: OnAccountUpdated_PostImage. Fall back to plugin + message + stage
            // for a lambda handler, which has no name.
            string? handlerName = FindHandlerMethodName(invocation);
            // Always prefix with the plugin class so the name is unique: the handler method name alone (e.g.
            // "OnUpdated") is common and would collide across plugin classes in the same namespace.
            string baseName = handlerName is null
                ? $"{pluginSymbol.Name}_{messageLabel}{stageLabel}"
                : $"{pluginSymbol.Name}_{Sanitize(handlerName)}";
            string typeName = $"{baseName}_{role}{aliasSuffix}";
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

        // The method name bound in the terminal .Handle(...) of this step's chain (walking outward from the
        // WithPreImage/WithPostImage call), or null when the handler is a lambda (no name to key off).
        private static string? FindHandlerMethodName(InvocationExpressionSyntax imageInvocation)
        {
            for (SyntaxNode? node = imageInvocation.Parent; node is not null; node = node.Parent)
            {
                if (node is InvocationExpressionSyntax inv &&
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.ValueText == "Handle" &&
                    inv.ArgumentList.Arguments.Count == 1)
                {
                    return inv.ArgumentList.Arguments[0].Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.ValueText,
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        _ => null, // lambda or other -> fall back to the plugin/message/stage name
                    };
                }

                // Stop at the enclosing member/statement boundary; the terminal Handle is within this chain.
                if (node is MemberDeclarationSyntax || node is StatementSyntax)
                {
                    break;
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

                    // Permissive path: OnMessage("create") / OnMessage<TMessage>() name the projection from the
                    // message too, so a string-triggered step gets a typed image like the guided/typed paths do.
                    // Only a statically resolvable message (string literal or type argument) yields a name; a
                    // variable message stays null -> graceful fallback (no projection), like any unresolved capture.
                    if (message is null && name == "OnMessage")
                    {
                        message = ResolvePermissiveMessage(inv, ma);
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

        // The message label for a permissive OnMessage(...) step. OnMessage<TMessage>() reuses the request/marker
        // type-name rule (same as On<TRequest>()); OnMessage("literal") uses the string literal, sanitized and
        // capitalized into a legal, guessable type-name segment (e.g. "create" -> Create). A non-literal argument
        // (a variable/const reference) is not statically resolvable, so it returns null and no projection is emitted.
        private static string? ResolvePermissiveMessage(InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax member)
        {
            if (member.Name is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count == 1)
            {
                return DeriveMessageFromRequest(generic.TypeArgumentList.Arguments[0]);
            }

            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count == 1 &&
                arguments[0].Expression is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                string raw = literal.Token.ValueText.Trim();
                return string.IsNullOrEmpty(raw) ? null : Capitalize(Sanitize(raw));
            }

            return null;
        }

        private static string Capitalize(string value)
            => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);

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
            // A captured image column can be absent/null, and the generated file is `#nullable enable`, so a
            // reference-type property must be nullable or it warns CS8618 (non-nullable never initialized).
            if (property.Type.IsReferenceType && !typeName.EndsWith("?", System.StringComparison.Ordinal))
            {
                typeName += "?";
            }

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

        private static void Emit(SourceProductionContext context, ImmutableArray<ProjectionModel> models, ParseOptions parseOptions)
        {
            // Dedup by fully-qualified type name: identical captures (same shape reached twice) collapse to one
            // type; distinct shapes have distinct names by construction (handler/plugin + message + stage + role).
            HashSet<string> emitted = new HashSet<string>();
            SortedSet<string> namespaces = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (ProjectionModel projectionModel in models)
            {
                string fullName = projectionModel.Namespace + "." + projectionModel.TypeName;
                if (!emitted.Add(fullName))
                {
                    continue;
                }

                namespaces.Add(projectionModel.Namespace);
                context.AddSource(Sanitize(fullName) + ".g.cs", SourceText.From(Render(projectionModel), Encoding.UTF8));
            }

            // Emit a global using per generated namespace so the projection types surface in completion (typing
            // ctx.PostImageAs<...>) without an explicit using. global using is a C# 10 feature, so this is skipped
            // on older LangVersion consumers, where import completion or an explicit using still work.
            if (namespaces.Count > 0 &&
                parseOptions is CSharpParseOptions options &&
                options.LanguageVersion >= LanguageVersion.CSharp10)
            {
                StringBuilder usings = new StringBuilder();
                usings.AppendLine("// <auto-generated/> Vela typed image projection global usings. Do not edit.");
                foreach (string ns in namespaces)
                {
                    usings.AppendLine($"global using global::{ns};");
                }

                context.AddSource("VelaGeneratedImages.GlobalUsings.g.cs", SourceText.From(usings.ToString(), Encoding.UTF8));
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
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Generated projection: exposes only the columns this step captured in WithPreImage/WithPostImage(...).");
            sb.AppendLine("    /// A column the step did not capture is absent here (reading it is a compile error, not a silent null);");
            sb.AppendLine("    /// add it to the capture to read it. Read this via ctx.PreImageAs&lt;T&gt;() / ctx.PostImageAs&lt;T&gt;().");
            sb.AppendLine("    /// </summary>");
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
