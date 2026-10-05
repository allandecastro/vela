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

            // A second driver keyed on the terminal .Handle(methodGroup) of every step, so a generated {Handler}Context
            // is emitted for EVERY named entity-bound handler (not only image-capturing ones). When the step also
            // captures a typed image, the matching projection above supplies the typed PreImage/PostImage; otherwise
            // the context is a stable, bare subclass of the message context - adding an image later does not change
            // the handler signature.
            IncrementalValuesProvider<HandlerModel> handlers = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => IsHandleInvocation(node),
                    transform: static (ctx, _) => ExtractHandler(ctx))
                .Where(static h => h is not null)
                .Select(static (h, _) => h!.Value);

            context.RegisterSourceOutput(models.Collect().Combine(handlers.Collect()),
                static (spc, pair) => Emit(spc, pair.Left, pair.Right));
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
            // For a typed On<TRequest>() step the generated context derives from MessagePre/PostContext<TRequest,
            // TEntity>; capture the request type so the context can name it. null for the guided / string paths.
            string? requestType = FindTypedRequestType(member.Expression, model);

            return new ProjectionModel(ns, containingTypes.ToImmutableArray(), typeName, columns.ToImmutableArray(),
                handlerName, messageLabel, isPost, isPre, entityType, requestType);
        }

        private static bool IsHandleInvocation(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name.Identifier.ValueText != "Handle" ||
                invocation.ArgumentList.Arguments.Count != 1)
            {
                return false;
            }

            // Only a method-group handler (.Handle(OnX)) gets a named context; a lambda has no name to key off.
            return invocation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax;
        }

        // One HandlerModel per named .Handle(OnX) step, so a {Handler}Context is emitted for every entity-bound
        // modelled handler - with or without a captured image (a bare subclass when imageless, so adding an image
        // later never changes the handler signature). OnMessage("string") / OnMessage<T>() and Custom API handlers
        // are intentionally skipped (no modelled typed context to derive from).
        private static HandlerModel? ExtractHandler(GeneratorSyntaxContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
            MemberAccessExpressionSyntax member = (MemberAccessExpressionSyntax)invocation.Expression;
            SemanticModel model = context.SemanticModel;

            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                !ImplementsFluentStep(method.ContainingType))
            {
                return null;
            }

            string? handlerName = invocation.ArgumentList.Arguments[0].Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                _ => null,
            };
            if (handlerName is null)
            {
                return null;
            }

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

            (string? messageLabel, string? stageLabel) = ResolveMessageAndStageForContext(member.Expression);
            if (messageLabel is null || stageLabel is null)
            {
                return null;
            }

            List<string> containingTypes = new List<string>();
            for (INamedTypeSymbol? t = pluginSymbol; t is not null; t = t.ContainingType)
            {
                containingTypes.Insert(0, t.Name);
            }

            string ns = pluginSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : pluginSymbol.ContainingNamespace.ToDisplayString();

            string? requestType = FindTypedRequestType(member.Expression, model);
            return new HandlerModel(ns, containingTypes.ToImmutableArray(), handlerName, messageLabel,
                stageLabel == "PostOp", entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), requestType);
        }

        // Message + stage for the context driver. Like ResolveMessageAndStage but recognizes EVERY On{Msg}() trigger
        // (strips the "On" prefix: OnAssign -> Assign, OnGrantAccess -> GrantAccess, OnCreateMultiple ->
        // CreateMultiple), plus the typed On<TRequest>(). OnMessage(...) stays unmapped (returns null message), so a
        // string/marker-triggered step gets no {Handler}Context.
        private static (string? Message, string? Stage) ResolveMessageAndStageForContext(ExpressionSyntax? receiver)
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
                        case "PreValidation": stage ??= "PreVal"; break;
                        case "PreOperation": stage ??= "PreOp"; break;
                        case "PostOperation": stage ??= "PostOp"; break;
                    }

                    // Only the CRUD guided triggers and the typed On<TRequest>() path are mapped: those are the stages
                    // whose fluent config carries the Handle(Delegate) overload that a {Handler}Context (a more-derived
                    // parameter) binds to. Other triggers (bulk, the ~45 message triggers, generated On{X} Custom API
                    // extensions, OnMessage(...)) stay unmapped, so no context is generated for a step that could not
                    // bind it.
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

        // The fully-qualified request type of an On<TRequest>() step in the chain, or null (guided OnCreate()...,
        // permissive OnMessage(...)). Used to build the generic message context base for a typed step.
        private static string? FindTypedRequestType(ExpressionSyntax? receiver, SemanticModel model)
        {
            for (ExpressionSyntax? current = receiver; current is not null;)
            {
                if (current is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
                {
                    if (ma.Name is GenericNameSyntax generic && generic.Identifier.ValueText == "On" &&
                        generic.TypeArgumentList.Arguments.Count == 1)
                    {
                        return model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0]).Symbol is INamedTypeSymbol request
                            ? request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            : null;
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

            return null;
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
            // A captured image column can be absent. Make the projected member nullable so "absent" reads as null:
            //  - a reference type must be nullable anyway (#nullable enable would otherwise warn CS8618);
            //  - a non-nullable value type would otherwise read its default (e.g. 0 / false) for an absent column,
            //    indistinguishable from a real value - so make it T? too (early-bound models are already T?; this
            //    only changes an authored non-nullable value-type selector) (#309 L4).
            bool alreadyNullable = typeName.EndsWith("?", System.StringComparison.Ordinal);
            if (!alreadyNullable && (property.Type.IsReferenceType || property.Type.IsValueType))
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

        private static void Emit(SourceProductionContext context, ImmutableArray<ProjectionModel> models, ImmutableArray<HandlerModel> handlers)
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

            // Order deterministically by name then column signature so that, on a collision, the SAME shape always
            // keeps the plain (un-suffixed) name across builds regardless of source order.
            List<ProjectionModel> ordered = distinct
                .OrderBy(m => FullName(m), System.StringComparer.Ordinal)
                .ThenBy(m => ColumnSignature(m), System.StringComparer.Ordinal)
                .ToList();

            List<ProjectionModel> finalized = new List<ProjectionModel>();
            HashSet<string> plainNameClaimed = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (ProjectionModel projectionModel in ordered)
            {
                string fullName = FullName(projectionModel);
                ProjectionModel toEmit = projectionModel;
                if (nameCounts[fullName] > 1 && !plainNameClaimed.Add(fullName))
                {
                    // Name collision across distinct column shapes (same plugin class, same handler name, different
                    // captured columns): the FIRST shape keeps the plain, guessable type name; only later colliding
                    // shapes get a stable hash suffix. This keeps a lambda-handler projection nameable in the common
                    // single-shape case rather than every shape becoming an unguessable hashed name (#309 L3).
                    toEmit = projectionModel.WithTypeName(projectionModel.TypeName + "_" + StableHash(ColumnSignature(projectionModel)));
                }

                context.AddSource(Sanitize(FullName(toEmit)) + ".g.cs", SourceText.From(Render(toEmit), Encoding.UTF8));
                finalized.Add(toEmit);
            }

            // Build contexts from the FINALIZED projection names (post collision-hash) so a generated context never
            // references a projection type that was renamed for a collision.
            EmitContexts(context, finalized, handlers);
        }

        // Emit a {Handler}Context for every named entity-bound handler (driven by the .Handle(OnX) handler models),
        // nested in the plugin partial. It derives from the step's message context; when the step also captured a
        // typed image (a matching projection), it shadows PreImage/PostImage with the typed projection types. An
        // imageless handler gets a bare subclass - a stable signature that gains a typed image without any change if
        // the step later adds a capture.
        private static void EmitContexts(SourceProductionContext context, List<ProjectionModel> projections, ImmutableArray<HandlerModel> handlers)
        {
            // Index the finalized projections by (plugin class + handler + pre/post) so a handler's context can pick
            // up its captured typed image(s). Keyed on handler + stage-role, not message, so it is robust to the two
            // drivers labelling a message slightly differently.
            Dictionary<string, string> preByHandler = new Dictionary<string, string>(System.StringComparer.Ordinal);
            Dictionary<string, string> postByHandler = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (ProjectionModel model in projections)
            {
                if (model.HandlerName is null)
                {
                    continue;
                }

                string key = model.Namespace + "|" + string.Join(".", model.ContainingTypes) + "|" + model.HandlerName;
                if (model.IsPre)
                {
                    if (!preByHandler.ContainsKey(key)) { preByHandler[key] = model.TypeName; }
                }
                else
                {
                    if (!postByHandler.ContainsKey(key)) { postByHandler[key] = model.TypeName; }
                }
            }

            HashSet<string> emitted = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (HandlerModel handler in handlers)
            {
                string baseTypeFull;
                bool baseHasPre;
                bool baseHasPost;
                if (handler.RequestType is not null)
                {
                    // Typed On<TRequest>() uses the permissive config, whose Handle(Delegate) overload exists only at
                    // PostOperation; a pre-stage typed config cannot bind a {Handler}Context, so skip it.
                    if (!handler.IsPost)
                    {
                        continue;
                    }

                    baseHasPre = true;
                    baseHasPost = true;
                    baseTypeFull = $"global::Vela.Context.MessagePostContext<{handler.RequestType}, {handler.EntityType}>";
                }
                else if (IsSingleImageMessage(handler.Message) &&
                    ResolveBaseContext(handler.Message, handler.IsPost, out baseHasPre, out baseHasPost) is string crud)
                {
                    // Create/Update/Delete at an image-bearing stage (the stages whose config exposes Handle(Delegate)):
                    // derive from the concrete context and expose a single typed pre/post image when captured.
                    baseTypeFull = $"global::Vela.Context.{crud}<{handler.EntityType}>";
                }
                else
                {
                    // Any other (message, stage) - e.g. Create pre-stage - has no Handle(Delegate) overload, so a
                    // {Handler}Context could not be bound there. Skip it rather than emit a context that would not
                    // compile. (Bulk and the other modelled messages are already unmapped by ResolveMessageAndStageForContext.)
                    continue;
                }

                string handlerKey = handler.Namespace + "|" + string.Join(".", handler.ContainingTypes) + "|" + handler.HandlerName;
                string? preName = baseHasPre && preByHandler.TryGetValue(handlerKey, out string? p) ? p : null;
                string? postName = baseHasPost && postByHandler.TryGetValue(handlerKey, out string? po) ? po : null;

                string contextName = Sanitize(handler.HandlerName) + "Context";
                string prefix = handler.Namespace.Length == 0 ? string.Empty : handler.Namespace + ".";
                string contextFull = prefix + string.Join(".", handler.ContainingTypes) + "." + contextName;

                // A handler reused across steps would key two contexts to the same name; emit once (VELA016 reports
                // the reuse as an error).
                if (!emitted.Add(contextFull))
                {
                    continue;
                }

                context.AddSource(Sanitize(contextFull) + ".Context.g.cs",
                    SourceText.From(RenderContext(handler, contextName, baseTypeFull, preName, postName), Encoding.UTF8));
            }
        }

        private static bool IsSingleImageMessage(string message) =>
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

        private static string RenderContext(HandlerModel model, string contextName, string baseTypeFull, string? preName, string? postName)
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

            string images = preName != null || postName != null
                ? "plus the typed image(s) this step captured"
                : "for this step";
            sb.AppendLine($"{Pad(indent)}/// <summary>");
            sb.AppendLine($"{Pad(indent)}/// Generated step context for the '{model.HandlerName}' handler: the message context {images}.");
            sb.AppendLine($"{Pad(indent)}/// Bind it with .Handle({model.HandlerName}). Capturing an image later adds a typed ctx.PreImage / ctx.PostImage without changing this signature.");
            sb.AppendLine($"{Pad(indent)}/// </summary>");
            sb.AppendLine($"{Pad(indent)}public sealed class {contextName} : {baseTypeFull}");
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
            sb.AppendLine($"{Pad(indent)}public sealed class {model.TypeName} : global::Vela.Context.IImageProjection");
            sb.AppendLine($"{Pad(indent)}{{");
            foreach (ProjectionColumn column in model.Columns)
            {
                sb.AppendLine($"{Pad(indent + 1)}[global::Microsoft.Xrm.Sdk.AttributeLogicalName(\"{column.LogicalName}\")]");
                sb.AppendLine($"{Pad(indent + 1)}public {column.TypeName} {column.PropertyName} {{ get; set; }}");
            }

            // The source image, retained so the explicit cast below is lossless. Implemented explicitly so it stays
            // off the projection's public column surface (a handler still sees only the captured columns).
            sb.AppendLine();
            sb.AppendLine($"{Pad(indent + 1)}global::Microsoft.Xrm.Sdk.Entity? global::Vela.Context.IImageProjection.Source {{ get; set; }}");

            // Widen the captured-columns view to the full early-bound image. Explicit on purpose: leaving the view,
            // where an uncaptured column reads null again, is a deliberate, visible step - never an implicit one.
            sb.AppendLine();
            sb.AppendLine($"{Pad(indent + 1)}/// <summary>");
            sb.AppendLine($"{Pad(indent + 1)}/// Widens this captured-columns projection to the full early-bound image (Id, logical name and every");
            sb.AppendLine($"{Pad(indent + 1)}/// attribute the image carried). Explicit on purpose: prefer reading the projection directly; cast only");
            sb.AppendLine($"{Pad(indent + 1)}/// when a service needs the whole entity. Equivalent to ctx.{(model.IsPre ? "GetPreImage" : "GetPostImage")}&lt;{model.EntityType}&gt;()");
            sb.AppendLine($"{Pad(indent + 1)}/// (both go through Entity.ToEntity, so the Id is preserved); returns null for an absent image.");
            sb.AppendLine($"{Pad(indent + 1)}/// </summary>");
            sb.AppendLine($"{Pad(indent + 1)}public static explicit operator {model.EntityType}({model.TypeName}? projection)");
            sb.AppendLine($"{Pad(indent + 2)}=> projection is global::Vela.Context.IImageProjection {{ Source: {{ }} source }} ? source.ToEntity<{model.EntityType}>() : null!;");

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
                string? handlerName, string message, bool isPost, bool isPre, string entityType, string? requestType)
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
                RequestType = requestType;
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
            public string? RequestType { get; }

            public ProjectionModel WithTypeName(string typeName) =>
                new ProjectionModel(Namespace, ContainingTypes, typeName, Columns, HandlerName, Message, IsPost, IsPre, EntityType, RequestType);

            public bool Equals(ProjectionModel other) =>
                Namespace == other.Namespace &&
                ContainingTypes.SequenceEqual(other.ContainingTypes) &&
                TypeName == other.TypeName &&
                Columns.SequenceEqual(other.Columns) &&
                HandlerName == other.HandlerName &&
                Message == other.Message &&
                IsPost == other.IsPost &&
                IsPre == other.IsPre &&
                EntityType == other.EntityType &&
                RequestType == other.RequestType;

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

        // One per named .Handle(OnX) step. Value equality (ContainingTypes element-wise) so the incremental generator
        // caches between keystrokes instead of re-running every edit.
        private readonly struct HandlerModel : System.IEquatable<HandlerModel>
        {
            public HandlerModel(string @namespace, ImmutableArray<string> containingTypes, string handlerName,
                string message, bool isPost, string entityType, string? requestType)
            {
                Namespace = @namespace;
                ContainingTypes = containingTypes;
                HandlerName = handlerName;
                Message = message;
                IsPost = isPost;
                EntityType = entityType;
                RequestType = requestType;
            }

            public string Namespace { get; }
            public ImmutableArray<string> ContainingTypes { get; }
            public string HandlerName { get; }
            public string Message { get; }
            public bool IsPost { get; }
            public string EntityType { get; }
            public string? RequestType { get; }

            public bool Equals(HandlerModel other) =>
                Namespace == other.Namespace &&
                ContainingTypes.SequenceEqual(other.ContainingTypes) &&
                HandlerName == other.HandlerName &&
                Message == other.Message &&
                IsPost == other.IsPost &&
                EntityType == other.EntityType &&
                RequestType == other.RequestType;

            public override bool Equals(object? obj) => obj is HandlerModel other && Equals(other);

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

                    hash = (hash * 31) + (HandlerName?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (Message?.GetHashCode() ?? 0);
                    hash = (hash * 31) + IsPost.GetHashCode();
                    hash = (hash * 31) + (EntityType?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (RequestType?.GetHashCode() ?? 0);
                    return hash;
                }
            }
        }
    }
}
