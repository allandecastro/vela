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
    /// trigger paths. Each type is emitted as a nested type inside the plugin's own <c>partial</c> class (named
    /// <c>{Handler}_{PreImage|PostImage}</c>, so the handler reads it unqualified), which is why a plugin declaring
    /// typed images must be <c>partial</c> (VELA014). The generator is purely additive (it never rewrites the fluent
    /// chain) and silently skips anything it cannot resolve statically (the developer keeps the full early-bound
    /// image in that case).
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

            context.RegisterSourceOutput(models.Collect(), static (spc, collected) => Emit(spc, collected));
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
            // Name the projection after the (named-method) handler bound with .Handle(...), so a developer can guess
            // it from their own method: OnAccountUpdated_PostImage. Fall back to message + stage for a lambda
            // handler, which has no name. The plugin class name is no longer prefixed: the projection is emitted as a
            // nested type inside the plugin's own partial class, so the class scope already keeps it unique and lets
            // the handler read it unqualified (ctx.PostImageAs&lt;OnAccountUpdated_PostImage&gt;()).
            string? handlerName = FindHandlerMethodName(invocation);
            string baseName = handlerName is null
                ? $"{messageLabel}{stageLabel}"
                : Sanitize(handlerName);
            string typeName = $"{baseName}_{role}{aliasSuffix}";

            // The containing type chain (outermost first, ending at the plugin class) so the projection can be
            // nested inside the plugin's partial class, plus the enclosing namespace.
            List<string> containingTypes = new List<string>();
            for (INamedTypeSymbol? t = pluginSymbol; t is not null; t = t.ContainingType)
            {
                containingTypes.Insert(0, t.Name);
            }

            string ns = pluginSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : pluginSymbol.ContainingNamespace.ToDisplayString();

            // Extra facts used to also emit a generated per-step context (typed step contexts): the handler name keys
            // the context, the message + stage pick its base concrete context, and the entity types its generic arg.
            bool isPost = stageLabel == "PostOp";
            string entityType = entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            return new ProjectionModel(ns, containingTypes.ToImmutableArray(), typeName, columns.ToImmutableArray(),
                handlerName, messageLabel, isPost, isPre, entityType);
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
            if (selector.Body is not ExpressionSyntax body)
            {
                return null;
            }

            // a => (object)a.Value / a => a.Value as object — see through a leading cast (CastExpression) or `as`
            // (an AsExpression BinaryExpression) to the member access, matching ColumnSelectorAnalyzer, so a cast
            // selector is not silently dropped (which would suppress the whole projection).
            while (true)
            {
                switch (body)
                {
                    case CastExpressionSyntax cast:
                        body = cast.Expression;
                        continue;
                    case BinaryExpressionSyntax asExpr when asExpr.IsKind(SyntaxKind.AsExpression):
                        body = asExpr.Left;
                        continue;
                }

                break;
            }

            if (body is not MemberAccessExpressionSyntax memberAccess ||
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

        // A stable string identity of a projection's captured columns, used both to collapse identical shapes and
        // to disambiguate a name collision across distinct shapes.
        private static string ColumnSignature(ProjectionModel model)
            => string.Join(";", model.Columns.Select(c => c.PropertyName + ":" + c.TypeName + ":" + c.LogicalName));

        // A deterministic (process-independent) FNV-1a hash rendered as 8 hex chars. string.GetHashCode is
        // randomized per process, so it must not feed a generated type name; this keeps the disambiguated name
        // stable across builds.
        private static string StableHash(string value)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                uint hash = offset;
                foreach (char c in value)
                {
                    hash = (hash ^ c) * prime;
                }

                return hash.ToString("x8");
            }
        }

        private static void Emit(SourceProductionContext context, ImmutableArray<ProjectionModel> models)
        {
            // Collapse genuinely-identical projections (same name AND same column shape reached twice) to one type,
            // keyed on name + column shape. Two steps that resolve to the same name but capture different columns
            // (for example two steps binding the same named handler with different WithPostImage columns) are NOT
            // the same projection: they are kept distinct here and disambiguated below, so the later one is not
            // silently dropped.
            HashSet<string> seen = new HashSet<string>(System.StringComparer.Ordinal);
            Dictionary<string, int> nameCounts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            List<ProjectionModel> distinct = new List<ProjectionModel>();
            foreach (ProjectionModel projectionModel in models)
            {
                string fullName = FullName(projectionModel);
                if (!seen.Add(fullName + "|" + ColumnSignature(projectionModel)))
                {
                    continue;
                }

                distinct.Add(projectionModel);
                nameCounts[fullName] = nameCounts.TryGetValue(fullName, out int count) ? count + 1 : 1;
            }

            foreach (ProjectionModel projectionModel in distinct)
            {
                string fullName = FullName(projectionModel);
                ProjectionModel toEmit = projectionModel;
                if (nameCounts[fullName] > 1)
                {
                    // Name collision across distinct column shapes (same plugin class, same handler name, different
                    // captured columns): append a stable hash of the shape so both projections are emitted with
                    // unique, legal type names instead of one winning.
                    toEmit = projectionModel.WithTypeName(projectionModel.TypeName + "_" + StableHash(ColumnSignature(projectionModel)));
                }

                context.AddSource(Sanitize(FullName(toEmit)) + ".g.cs", SourceText.From(Render(toEmit), Encoding.UTF8));
            }

            EmitContexts(context, distinct);
        }

        // Emit, per step with a named handler on a modelled CRUD message, a generated context nested in the plugin
        // partial: it derives from the concrete step context and shadows PreImage/PostImage with the typed projections
        // the step captured. The handler binds it with .Handle(OnX) and reads ctx.PreImage / ctx.PostImage typed.
        private static void EmitContexts(SourceProductionContext context, List<ProjectionModel> models)
        {
            // Group a step's projections by (plugin class + handler + message + stage). Only a named handler on a
            // Create/Update/Delete message gets a context; a lambda handler (null name) does not.
            Dictionary<string, List<ProjectionModel>> groups = new Dictionary<string, List<ProjectionModel>>(System.StringComparer.Ordinal);
            foreach (ProjectionModel model in models)
            {
                if (model.HandlerName is null || !IsContextMessage(model.Message))
                {
                    continue;
                }

                string key = model.Namespace + "|" + string.Join(".", model.ContainingTypes) + "|" +
                    model.HandlerName + "|" + model.Message + "|" + (model.IsPost ? "post" : "pre");
                if (!groups.TryGetValue(key, out List<ProjectionModel>? list))
                {
                    list = new List<ProjectionModel>();
                    groups[key] = list;
                }

                list.Add(model);
            }

            HashSet<string> emitted = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (List<ProjectionModel> group in groups.Values)
            {
                ProjectionModel any = group[0];
                string? baseContext = ResolveBaseContext(any.Message, any.IsPost, out bool baseHasPre, out bool baseHasPost);
                if (baseContext is null)
                {
                    continue;
                }

                string? preName = null;
                string? postName = null;
                foreach (ProjectionModel model in group)
                {
                    if (model.IsPre && baseHasPre)
                    {
                        preName ??= model.TypeName;
                    }
                    else if (!model.IsPre && baseHasPost)
                    {
                        postName ??= model.TypeName;
                    }
                }

                if (preName is null && postName is null)
                {
                    continue;
                }

                string contextName = Sanitize(any.HandlerName!) + "Context";
                string prefix = any.Namespace.Length == 0 ? string.Empty : any.Namespace + ".";
                string contextFull = prefix + string.Join(".", any.ContainingTypes) + "." + contextName;

                // A handler reused across steps would key two contexts to the same name; emit once (VELA016 reports
                // the reuse as an error).
                if (!emitted.Add(contextFull))
                {
                    continue;
                }

                context.AddSource(Sanitize(contextFull) + ".Context.g.cs",
                    SourceText.From(RenderContext(any, contextName, baseContext, preName, postName), Encoding.UTF8));
            }
        }

        private static bool IsContextMessage(string message) =>
            message == "Create" || message == "Update" || message == "Delete";

        // The concrete step context a generated context derives from, and which typed images it can expose, for a
        // Create/Update/Delete message at the given stage. Returns null when the stage carries no image.
        private static string? ResolveBaseContext(string message, bool isPost, out bool hasPre, out bool hasPost)
        {
            hasPre = false;
            hasPost = false;
            switch (message)
            {
                case "Create":
                    if (!isPost)
                    {
                        return null; // create pre-stage has no image
                    }

                    hasPost = true;
                    return "CreatePostContext";
                case "Update":
                    if (isPost)
                    {
                        hasPre = true;
                        hasPost = true;
                        return "UpdatePostContext";
                    }

                    hasPre = true;
                    return "UpdatePreContext";
                case "Delete":
                    hasPre = true;
                    return isPost ? "DeletePostContext" : "DeletePreContext";
                default:
                    return null;
            }
        }

        private static string RenderContext(ProjectionModel model, string contextName, string baseContext, string? preName, string? postName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/> Vela typed step context. Do not edit.");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();

            int indent = 0;
            bool hasNamespace = model.Namespace.Length != 0;
            if (hasNamespace)
            {
                sb.AppendLine($"namespace {model.Namespace}");
                sb.AppendLine("{");
                indent++;
            }

            foreach (string containingType in model.ContainingTypes)
            {
                sb.AppendLine($"{Pad(indent)}partial class {containingType}");
                sb.AppendLine($"{Pad(indent)}{{");
                indent++;
            }

            sb.AppendLine($"{Pad(indent)}/// <summary>");
            sb.AppendLine($"{Pad(indent)}/// Generated step context for the '{model.HandlerName}' handler: the message context plus the typed image(s)");
            sb.AppendLine($"{Pad(indent)}/// this step captured. Bind it with .Handle({model.HandlerName}) and read ctx.PreImage / ctx.PostImage (typed).");
            sb.AppendLine($"{Pad(indent)}/// </summary>");
            sb.AppendLine($"{Pad(indent)}public sealed class {contextName} : global::Vela.Context.{baseContext}<{model.EntityType}>");
            sb.AppendLine($"{Pad(indent)}{{");
            sb.AppendLine($"{Pad(indent + 1)}public {contextName}(global::Vela.Context.ILocalPluginContext inner) : base(inner) {{ }}");
            if (preName != null)
            {
                sb.AppendLine($"{Pad(indent + 1)}public new {preName}? PreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<{preName}>(this);");
            }

            if (postName != null)
            {
                sb.AppendLine($"{Pad(indent + 1)}public new {postName}? PostImage => global::Vela.Context.ImageProjectionExtensions.PostImageAs<{postName}>(this);");
            }

            sb.AppendLine($"{Pad(indent)}}}");

            foreach (string _ in model.ContainingTypes)
            {
                indent--;
                sb.AppendLine($"{Pad(indent)}}}");
            }

            if (hasNamespace)
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        // The fully-qualified generated name: namespace (if any) + the containing type chain + the projection type,
        // used to collapse duplicates, disambiguate collisions and key the generated file name.
        private static string FullName(ProjectionModel model)
        {
            string prefix = model.Namespace.Length == 0 ? string.Empty : model.Namespace + ".";
            return prefix + string.Join(".", model.ContainingTypes) + "." + model.TypeName;
        }

        private static string Render(ProjectionModel model)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/> Vela typed image projection. Do not edit.");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();

            int indent = 0;
            bool hasNamespace = model.Namespace.Length != 0;
            if (hasNamespace)
            {
                sb.AppendLine($"namespace {model.Namespace}");
                sb.AppendLine("{");
                indent++;
            }

            // Reopen the plugin class (and any enclosing types) as partial parts so the projection is a nested type
            // the handler can read unqualified. The plugin's own declaration must therefore be partial (VELA014).
            foreach (string containingType in model.ContainingTypes)
            {
                sb.AppendLine($"{Pad(indent)}partial class {containingType}");
                sb.AppendLine($"{Pad(indent)}{{");
                indent++;
            }

            sb.AppendLine($"{Pad(indent)}/// <summary>");
            sb.AppendLine($"{Pad(indent)}/// Generated projection: exposes only the columns this step captured in WithPreImage/WithPostImage(...).");
            sb.AppendLine($"{Pad(indent)}/// A column the step did not capture is absent here (reading it is a compile error, not a silent null);");
            sb.AppendLine($"{Pad(indent)}/// add it to the capture to read it. Read this via ctx.PreImageAs&lt;T&gt;() / ctx.PostImageAs&lt;T&gt;().");
            sb.AppendLine($"{Pad(indent)}/// </summary>");
            sb.AppendLine($"{Pad(indent)}public sealed class {model.TypeName}");
            sb.AppendLine($"{Pad(indent)}{{");
            foreach (ProjectionColumn column in model.Columns)
            {
                sb.AppendLine($"{Pad(indent + 1)}[global::Microsoft.Xrm.Sdk.AttributeLogicalName(\"{column.LogicalName}\")]");
                sb.AppendLine($"{Pad(indent + 1)}public {column.TypeName} {column.PropertyName} {{ get; set; }}");
            }

            sb.AppendLine($"{Pad(indent)}}}");

            foreach (string _ in model.ContainingTypes)
            {
                indent--;
                sb.AppendLine($"{Pad(indent)}}}");
            }

            if (hasNamespace)
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private static string Pad(int levels) => new string(' ', levels * 4);

        // Value equality (not the default ImmutableArray reference equality) so the incremental generator caches the
        // model between keystrokes instead of re-running every edit.
        private readonly struct ProjectionColumn : System.IEquatable<ProjectionColumn>
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

            public bool Equals(ProjectionColumn other) =>
                PropertyName == other.PropertyName && TypeName == other.TypeName && LogicalName == other.LogicalName;

            public override bool Equals(object? obj) => obj is ProjectionColumn other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) + (PropertyName?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (TypeName?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (LogicalName?.GetHashCode() ?? 0);
                    return hash;
                }
            }
        }

        // Value equality (Columns compared element-wise via SequenceEqual, not by ImmutableArray reference) so the
        // incremental generator caches the model between keystrokes instead of re-running every edit.
        private readonly struct ProjectionModel : System.IEquatable<ProjectionModel>
        {
            public ProjectionModel(string @namespace, ImmutableArray<string> containingTypes, string typeName, ImmutableArray<ProjectionColumn> columns,
                string? handlerName, string message, bool isPost, bool isPre, string entityType)
            {
                Namespace = @namespace;
                ContainingTypes = containingTypes;
                TypeName = typeName;
                Columns = columns;
                HandlerName = handlerName;
                Message = message;
                IsPost = isPost;
                IsPre = isPre;
                EntityType = entityType;
            }

            public string Namespace { get; }
            public ImmutableArray<string> ContainingTypes { get; }
            public string TypeName { get; }
            public ImmutableArray<ProjectionColumn> Columns { get; }
            // Facts for the generated per-step context (null HandlerName = lambda handler, which gets no context).
            public string? HandlerName { get; }
            public string Message { get; }
            public bool IsPost { get; }
            public bool IsPre { get; }
            public string EntityType { get; }

            public ProjectionModel WithTypeName(string typeName) =>
                new ProjectionModel(Namespace, ContainingTypes, typeName, Columns, HandlerName, Message, IsPost, IsPre, EntityType);

            public bool Equals(ProjectionModel other) =>
                Namespace == other.Namespace &&
                ContainingTypes.SequenceEqual(other.ContainingTypes) &&
                TypeName == other.TypeName &&
                Columns.SequenceEqual(other.Columns) &&
                HandlerName == other.HandlerName &&
                Message == other.Message &&
                IsPost == other.IsPost &&
                IsPre == other.IsPre &&
                EntityType == other.EntityType;

            public override bool Equals(object? obj) => obj is ProjectionModel other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) + (Namespace?.GetHashCode() ?? 0);
                    foreach (string containing in ContainingTypes)
                    {
                        hash = (hash * 31) + (containing?.GetHashCode() ?? 0);
                    }

                    hash = (hash * 31) + (TypeName?.GetHashCode() ?? 0);
                    foreach (ProjectionColumn column in Columns)
                    {
                        hash = (hash * 31) + column.GetHashCode();
                    }

                    return hash;
                }
            }
        }
    }
}
