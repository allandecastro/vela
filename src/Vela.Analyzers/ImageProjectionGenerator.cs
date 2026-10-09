using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vela.Analyzers
{
    /// <summary>
    /// Emits, per step, a projection type that exposes only the columns the step captured in
    /// <c>WithPreImage(...)</c> / <c>WithPostImage(...)</c> (and, on Merge, <c>WithSubordinatePreImage(...)</c> /
    /// <c>WithSubordinatePostImage(...)</c>). The handler reads it via
    /// <c>ctx.PreImageAs&lt;T&gt;()</c> / <c>ctx.PostImageAs&lt;T&gt;()</c>, so reading a column the step did not
    /// capture is a compile error instead of a silent <c>null</c>. Covers the guided (<c>OnCreate()</c>...), typed
    /// (<c>On&lt;TRequest&gt;()</c>) and permissive (<c>OnMessage("...")</c> / <c>OnMessage&lt;TMessage&gt;()</c>)
    /// trigger paths. Each type is emitted as a nested type inside the plugin's own <c>partial</c> class (named
    /// <c>{Handler}_{PreImage|PostImage|SubordinatePreImage|SubordinatePostImage}</c>, so the handler reads it unqualified), which is why a plugin declaring
    /// typed images must be <c>partial</c> (VELA014). The generator is purely additive (it never rewrites the fluent
    /// chain). When a capture lists columns it cannot read statically it emits no projection (the developer keeps the
    /// full early-bound image) and reports why (VELA021), unless an analyzer already reports the selector.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ImageProjectionGenerator : IIncrementalGenerator
    {
        public const string NotGeneratedDiagnosticId = DiagnosticIds.ImageProjectionNotGenerated;

        // Reported (as a warning, on the image call) when a step lists image columns but no typed projection can be
        // generated for it, so the developer learns why instead of meeting only a CS0246 on the missing type.
        internal static readonly DiagnosticDescriptor NotGeneratedRule = new DiagnosticDescriptor(
            id: NotGeneratedDiagnosticId,
            title: "Typed image projection not generated",
            messageFormat: "No typed projection is generated for this {0}(...): {1}; read the full image with ctx.Get{2}<T>() instead, or fix the capture",
            category: "Vela.Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The source generator emits a typed projection for a WithPreImage/WithPostImage capture only when it can read the capture statically: a compile-time constant alias, column properties of the entity, and a chain whose message and stage it can read. Otherwise no projection type exists and code that names it fails with CS0246; this warning says why.");

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<ProjectionResult> results = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => IsImageInvocation(node),
                    transform: static (ctx, ct) => ExtractProjection((InvocationExpressionSyntax)ctx.Node, ctx.SemanticModel, ct));

            IncrementalValuesProvider<ProjectionModel> models = results
                .Where(static r => r.Model is not null)
                .Select(static (r, _) => r.Model!.Value);

            IncrementalValuesProvider<SkippedProjection> skipped = results
                .Where(static r => r.Skip is not null)
                .Select(static (r, _) => r.Skip!.Value);

            // A second driver keyed on the terminal .Handle(methodGroup) of every step, so a generated {Handler}Context
            // is emitted for EVERY named entity-bound handler (not only image-capturing ones). When the step also
            // captures a typed image, the matching projection above supplies the typed PreImage/PostImage; otherwise
            // the context is a stable, bare subclass of the message context - adding an image later does not change
            // the handler signature.
            IncrementalValuesProvider<HandlerModel> handlers = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: static (node, _) => IsHandleInvocation(node),
                    transform: static (ctx, ct) => ExtractHandler((InvocationExpressionSyntax)ctx.Node, ctx.SemanticModel, ct))
                .Where(static h => h is not null)
                .Select(static (h, _) => h!.Value);

            context.RegisterSourceOutput(models.Collect().Combine(handlers.Collect()),
                static (spc, pair) => Emit(spc, pair.Left, pair.Right));

            context.RegisterSourceOutput(skipped,
                static (spc, skip) => spc.ReportDiagnostic(Diagnostic.Create(NotGeneratedRule, skip.ToLocation(), skip.Method, skip.Reason, IsPreImageMethod(skip.Method) ? "PreImage" : "PostImage")));
        }

        /// <summary>
        /// True when the generator emits a nested type (a typed image projection or a {Handler}Context) into the class
        /// that encloses <paramref name="invocation"/>, which therefore must be partial. Shared with VELA014 so the
        /// analyzer asks for <c>partial</c> exactly when the generator needs it, never when it would emit nothing.
        /// </summary>
        internal static bool EmitsNestedType(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
        {
            if (IsImageInvocation(invocation))
            {
                return ExtractProjection(invocation, model, cancellationToken).Model is not null;
            }

            return IsHandleInvocation(invocation) && ExtractHandler(invocation, model, cancellationToken) is not null;
        }

        private static bool IsImageInvocation(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                return false;
            }

            string name = member.Name.Identifier.ValueText;
            return name is "WithPreImage" or "WithPostImage" or "WithSubordinatePreImage" or "WithSubordinatePostImage";
        }

        private static bool IsPreImageMethod(string method) => method is "WithPreImage" or "WithSubordinatePreImage";

        // The enclosing entity plugin of a fluent step call: its class symbol and TEntity. Null when the call is not
        // Vela's (its declaring type does not implement Vela.Steps.IFluentStep, compared by symbol) or the enclosing
        // class does not derive from Vela.Plugin.EntityPluginBase<TEntity>.
        private static (INamedTypeSymbol Plugin, INamedTypeSymbol Entity)? ResolvePlugin(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
        {
            INamedTypeSymbol? fluent = model.Compilation.GetTypeByMetadataName(FluentStep.FluentStepMetadataName);
            if (fluent is null || !FluentStep.IsFluentMethod(model, invocation, fluent, cancellationToken))
            {
                return null;
            }

            ClassDeclarationSyntax? pluginClass = invocation.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (pluginClass is null || model.GetDeclaredSymbol(pluginClass, cancellationToken) is not INamedTypeSymbol pluginSymbol)
            {
                return null;
            }

            INamedTypeSymbol? entity = PluginTypes.EntityOf(pluginSymbol, model.Compilation);
            return entity is null ? null : (pluginSymbol, entity);
        }

        private static ProjectionResult ExtractProjection(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
        {
            MemberAccessExpressionSyntax member = (MemberAccessExpressionSyntax)invocation.Expression;
            string method = member.Name.Identifier.ValueText;
            bool isPre = IsPreImageMethod(method);
            // A Merge step's image of the subordinate record (WithSubordinate*Image): its own projection and property.
            bool isSubordinate = method.StartsWith("WithSubordinate", System.StringComparison.Ordinal);

            // Confirm this is Vela's image method inside an entity plugin, so a same-named method on an unrelated
            // type is ignored.
            if (ResolvePlugin(invocation, model, cancellationToken) is not { } plugin)
            {
                return default;
            }

            // Arguments: optional leading string alias, then the column selector lambdas.
            (string? alias, bool aliasUnresolved, List<LambdaExpressionSyntax> selectors, bool opaqueColumns) = SplitArguments(invocation, model, cancellationToken);
            if (opaqueColumns)
            {
                // A column argument that is not an inline lambda (a selector held in a variable, an array): the
                // captured columns are not known here, so a projection would be wrong or incomplete.
                return ProjectionResult.Skipped(invocation, method, "a column argument is not an inline selector lambda");
            }

            if (selectors.Count == 0)
            {
                // No explicit columns (only an alias, or nothing) -> the image carries every column; nothing to narrow.
                return default;
            }

            if (aliasUnresolved)
            {
                // An alias that is not a compile-time constant: the image's runtime key is unknown here, so a typed
                // projection could not read it. Generate nothing rather than a context that reads null.
                return ProjectionResult.Skipped(invocation, method, "its image alias is not a compile-time constant");
            }

            // Message + stage from the chain that precedes this call.
            (string? messageLabel, string? stageLabel) = ResolveMessageAndStage(member.Expression, model, cancellationToken);
            if (messageLabel is null || stageLabel is null)
            {
                // A chain split across locals legitimately hides its trigger here, so only a chain that starts at
                // RegisterStep() in this expression (whose trigger cannot be read) is reported.
                return FluentStep.SpineHasCall(member.Expression, "RegisterStep")
                    ? ProjectionResult.Skipped(invocation, method, "the step's message or stage cannot be read from the chain")
                    : default;
            }

            List<ProjectionColumn> columns = new List<ProjectionColumn>();
            foreach (LambdaExpressionSyntax selector in selectors)
            {
                ProjectionColumn? column = ResolveColumn(selector, model, cancellationToken, out bool reportedElsewhere);
                if (column is null)
                {
                    // Any unresolved column makes the projection incomplete; fall back to no generation. A nested path,
                    // an Entity member or a relationship is already an error of VELA001/VELA010, so only the rest is
                    // reported here.
                    return reportedElsewhere
                        ? default
                        : ProjectionResult.Skipped(invocation, method, $"'{selector}' does not select a column property of the entity");
                }

                columns.Add(column.Value);
            }

            string role = (isSubordinate ? "Subordinate" : string.Empty) + (isPre ? "PreImage" : "PostImage");
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

            // Extra facts used to also emit a generated per-step context (typed step contexts): the handler name keys
            // the context, the message + stage pick its base concrete context, and the entity types its generic arg.
            bool isPost = stageLabel == "PostOp";
            string entityType = plugin.Entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            // For a typed On<TRequest>() step the generated context derives from MessagePre/PostContext<TRequest,
            // TEntity>; capture the request type so the context can name it. null for the guided / string paths.
            string? requestType = FindTypedRequestType(member.Expression, model, cancellationToken);

            return new ProjectionResult(new ProjectionModel(NamespaceOf(plugin.Plugin), ContainingDeclarations(plugin.Plugin), typeName,
                columns.ToImmutableArray(), handlerName, messageLabel, isPost, isPre, isSubordinate, entityType, requestType, alias));
        }

        private static bool IsHandleInvocation(SyntaxNode node)
        {
            // Only a method-group handler (.Handle(OnX)) gets a named context; a lambda has no name to key off.
            return node is InvocationExpressionSyntax invocation &&
                FluentStep.IsHandleCall(invocation, out _, out ExpressionSyntax handler) &&
                FluentStep.MethodGroupName(handler) is not null;
        }

        // One HandlerModel per named .Handle(OnX) step, so a {Handler}Context is emitted for every entity-bound
        // modelled handler - with or without a captured image (a bare subclass when imageless, so adding an image
        // later never changes the handler signature). OnMessage("string") / OnMessage<T>() and Custom API handlers
        // are intentionally skipped (no modelled typed context to derive from).
        private static HandlerModel? ExtractHandler(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
        {
            if (!FluentStep.IsHandleCall(invocation, out MemberAccessExpressionSyntax member, out ExpressionSyntax handler) ||
                FluentStep.MethodGroupName(handler) is not string handlerName ||
                ResolvePlugin(invocation, model, cancellationToken) is not { } plugin)
            {
                return null;
            }

            (string? messageLabel, string? stageLabel) = ResolveMessageAndStageForContext(member.Expression);
            if (messageLabel is null || stageLabel is null)
            {
                return null;
            }

            string? requestType = FindTypedRequestType(member.Expression, model, cancellationToken);
            bool isPost = stageLabel == "PostOp";

            // Emit only when a real modelled context base exists. The typed On<TRequest>() path always has
            // MessagePre/PostContext; a guided On{Msg}() needs its Vela.Context.{Msg}{Pre|Post}Context to exist. This
            // is what excludes generated Custom API On{X} extensions (e.g. OnRecalc -> no RecalcPostContext) and any
            // other non-modelled trigger, without classifying the trigger method itself.
            if (requestType is null)
            {
                string contextMetadata = $"Vela.Context.{messageLabel}{(isPost ? "Post" : "Pre")}Context`1";
                if (model.Compilation.GetTypeByMetadataName(contextMetadata) is null)
                {
                    return null;
                }
            }

            return new HandlerModel(NamespaceOf(plugin.Plugin), ContainingDeclarations(plugin.Plugin), handlerName, messageLabel,
                isPost, plugin.Entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), requestType);
        }

        private static string NamespaceOf(INamedTypeSymbol plugin)
            => plugin.ContainingNamespace.IsGlobalNamespace ? string.Empty : plugin.ContainingNamespace.ToDisplayString();

        // The containing type chain (outermost first, ending at the plugin class), each as the declaration the
        // generated partial part reopens: its kind (class, struct, record, record struct, interface) and its type
        // parameters, so a nested plugin inside a struct or record, or a generic plugin, gets a matching partial
        // part instead of a new, unrelated 'partial class Name'.
        private static ImmutableArray<string> ContainingDeclarations(INamedTypeSymbol plugin)
        {
            List<string> declarations = new List<string>();
            for (INamedTypeSymbol? t = plugin; t is not null; t = t.ContainingType)
            {
                string keyword = t.TypeKind switch
                {
                    TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
                    TypeKind.Interface => "interface",
                    _ => t.IsRecord ? "record" : "class",
                };
                string typeParameters = t.TypeParameters.Length == 0
                    ? string.Empty
                    : "<" + string.Join(",", t.TypeParameters.Select(VariantName)) + ">";
                declarations.Insert(0, keyword + " " + t.Name + typeParameters);
            }

            return declarations.ToImmutableArray();
        }

        // The type name of a containing declaration ("record struct Outer" -> "Outer", "class P<T>" -> "P<T>").
        // Variance (in/out) is kept: a partial interface must repeat it (CS1067).
        private static string VariantName(ITypeParameterSymbol parameter) => parameter.Variance switch
        {
            VarianceKind.Out => "out " + parameter.Name,
            VarianceKind.In => "in " + parameter.Name,
            _ => parameter.Name,
        };

        // The type name of a containing declaration, without its kind or any variance, for full names and file names
        // ("record struct Outer" -> "Outer", "interface Box<out T>" -> "Box<T>").
        private static string DeclarationName(string declaration)
        {
            int typeParameters = declaration.IndexOf('<');
            string head = typeParameters < 0 ? declaration : declaration.Substring(0, typeParameters);
            string name = head.Substring(head.LastIndexOf(' ') + 1);
            return typeParameters < 0
                ? name
                : name + declaration.Substring(typeParameters).Replace("out ", string.Empty).Replace("in ", string.Empty);
        }

        // Message + stage for the context driver. Like ResolveMessageAndStage but recognizes EVERY On{Msg}() trigger
        // (strips the "On" prefix: OnAssign -> Assign, OnGrantAccess -> GrantAccess, OnCreateMultiple ->
        // CreateMultiple), plus the typed On<TRequest>(). OnMessage<TMessage>() stays unmapped (returns null message), so a
        // marker-triggered step gets no {Handler}Context.
        private static (string? Message, string? Stage) ResolveMessageAndStageForContext(ExpressionSyntax? receiver)
        {
            string? message = null;
            string? stage = null;

            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                string name = call.MethodName;
                stage ??= StageLabel(name);

                if (message is null && name == "On" && call.Name is GenericNameSyntax generic &&
                    generic.TypeArgumentList.Arguments.Count == 1)
                {
                    message = DeriveMessageFromRequest(generic.TypeArgumentList.Arguments[0]);
                }
                else if (message is null && name.Length > 2 && name.StartsWith("On", System.StringComparison.Ordinal) &&
                    name != "OnMessage" && name != "OnAsyncOnly" && char.IsUpper(name[2]))
                {
                    // Any On{Msg}() trigger -> {Msg}. Whether a context is actually emitted is gated downstream by
                    // whether a real Vela.Context.{Msg}{Pre|Post}Context type exists (ExtractHandler), which
                    // excludes generated Custom API On{X} extensions and any non-modelled trigger.
                    message = name.Substring(2);
                }
            }

            return (message, stage);
        }

        private static string? StageLabel(string methodName) => methodName switch
        {
            "PreValidation" => "PreVal",
            "PreOperation" => "PreOp",
            "PostOperation" => "PostOp",
            _ => null,
        };

        // The fully-qualified request type of an On<TRequest>() step in the chain, or null (guided OnCreate()...,
        // permissive OnMessage<TMessage>()). Used to build the generic message context base for a typed step.
        private static string? FindTypedRequestType(ExpressionSyntax? receiver, SemanticModel model, CancellationToken cancellationToken)
        {
            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                if (call.Name is GenericNameSyntax generic && generic.Identifier.ValueText == "On" &&
                    generic.TypeArgumentList.Arguments.Count == 1)
                {
                    return model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Symbol is INamedTypeSymbol request
                        ? request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        : null;
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
                if (node is InvocationExpressionSyntax inv && FluentStep.IsHandleCall(inv, out _, out ExpressionSyntax handler))
                {
                    return FluentStep.MethodGroupName(handler); // a lambda -> fall back to the message/stage name
                }

                // Stop at the enclosing member/statement boundary; the terminal Handle is within this chain.
                if (node is MemberDeclarationSyntax || node is StatementSyntax)
                {
                    break;
                }
            }

            return null;
        }

        private static (string? Message, string? Stage) ResolveMessageAndStage(ExpressionSyntax? receiver, SemanticModel model, CancellationToken cancellationToken)
        {
            string? message = null;
            string? stage = null;

            foreach (ChainCall call in FluentStep.Spine(receiver))
            {
                string name = call.MethodName;
                stage ??= StageLabel(name);
                switch (name)
                {
                    case "OnCreate": message ??= "Create"; break;
                    case "OnUpdate": message ??= "Update"; break;
                    case "OnDelete": message ??= "Delete"; break;
                    case "OnCreateMultiple": message ??= "CreateMultiple"; break;
                    case "OnUpdateMultiple": message ??= "UpdateMultiple"; break;
                    case "OnAssign": message ??= "Assign"; break;
                    case "OnMerge": message ??= "Merge"; break;
                }

                // Typed path: On<TRequest>() derives the message from the request type name (DeleteRequest ->
                // Delete, UpdateRequest<T> -> Update), matching how the runtime maps it.
                if (message is null && name == "On" && call.Name is GenericNameSyntax generic &&
                    generic.TypeArgumentList.Arguments.Count == 1)
                {
                    message = DeriveMessageFromRequest(generic.TypeArgumentList.Arguments[0]);
                }

                // Permissive path: OnMessage<TMessage>() names the projection from the marker/request type, with the
                // same type-name rule as On<TRequest>(), so it gets a typed image like the guided/typed paths do.
                if (message is null && name == "OnMessage" && call.Name is GenericNameSyntax marker &&
                    marker.TypeArgumentList.Arguments.Count == 1)
                {
                    message = DeriveMessageFromRequest(marker.TypeArgumentList.Arguments[0]);
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

        // The optional leading alias and the column selectors. The alias may be any compile-time string constant (a
        // literal or a const); an explicit alias that is not constant cannot be known here, so it is reported as
        // unresolved and no projection is generated (a context reading the default alias would read null).
        // Any other argument (a selector in a variable, an explicit array) is an opaque column list.
        private static (string? Alias, bool AliasUnresolved, List<LambdaExpressionSyntax> Selectors, bool OpaqueColumns) SplitArguments(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
        {
            string? alias = null;
            bool unresolved = false;
            bool opaque = false;
            List<LambdaExpressionSyntax> selectors = new List<LambdaExpressionSyntax>();

            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (argument.Expression is LambdaExpressionSyntax lambda)
                {
                    selectors.Add(lambda);
                    continue;
                }

                if (model.GetTypeInfo(argument.Expression, cancellationToken).Type?.SpecialType == SpecialType.System_String)
                {
                    Optional<object?> constant = model.GetConstantValue(argument.Expression, cancellationToken);
                    if (constant.HasValue && constant.Value is string text)
                    {
                        alias = text;
                    }
                    else
                    {
                        unresolved = true;
                    }
                }
                else
                {
                    opaque = true;
                }
            }

            return (alias, unresolved, selectors, opaque);
        }

        // The projected column a selector reads, or null. reportedElsewhere is true when the selector is a mistake an
        // analyzer already reports (a nested path or an Entity member: VELA001; a relationship: VELA010), so the
        // generator does not add a second diagnostic on it.
        private static ProjectionColumn? ResolveColumn(LambdaExpressionSyntax selector, SemanticModel model, CancellationToken cancellationToken, out bool reportedElsewhere)
        {
            reportedElsewhere = false;

            // a => (object)a.Value / a => a.Value as object - see through a leading cast or `as` to the member access,
            // matching ColumnSelectorAnalyzer. Only a column of the entity itself: a nested path (a.ParentAccount.Name)
            // is another record's member (VELA001 reports it), so it must not become a column of the projection.
            MemberAccessExpressionSyntax? memberAccess = FluentStep.SelectedMember(selector);
            if (memberAccess is null)
            {
                reportedElsewhere = selector.Body is ExpressionSyntax body && FluentStep.UnwrapConversions(body) is MemberAccessExpressionSyntax;
                return null;
            }

            if (model.GetSymbolInfo(memberAccess, cancellationToken).Symbol is not IPropertySymbol property)
            {
                return null;
            }

            if (property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Microsoft.Xrm.Sdk.RelationshipSchemaNameAttribute"))
            {
                reportedElsewhere = true; // VELA010
                return null;
            }

            string? logicalName = GetAttributeLogicalName(property);
            if (logicalName is null)
            {
                // Mirror the runtime (ExpressionHelper.ResolveColumn): a member of Entity itself is not a column
                // (VELA001 reports it); any other property without [AttributeLogicalName] is the lower-cased name.
                if (IsEntityMember(property))
                {
                    reportedElsewhere = true;
                    return null;
                }

                logicalName = property.Name.ToLowerInvariant();
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

        private static bool IsEntityMember(IPropertySymbol property)
        {
            for (IPropertySymbol? p = property; p is not null; p = p.OverriddenProperty)
            {
                if (p.ContainingType?.ToDisplayString() == "Microsoft.Xrm.Sdk.Entity")
                {
                    return true;
                }
            }

            return false;
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
            // The projection each handler's context exposes; the model carries the image alias the context reads.
            // Keyed on handler + role (pre/post, master/subordinate); the first projection of a role wins.
            Dictionary<string, ProjectionModel> byHandlerAndRole = new Dictionary<string, ProjectionModel>(System.StringComparer.Ordinal);
            foreach (ProjectionModel model in projections)
            {
                if (model.HandlerName is null)
                {
                    continue;
                }

                string key = model.Namespace + "|" + string.Join(".", model.ContainingTypes) + "|" + model.HandlerName + "|" + RoleKey(model.IsPre, model.IsSubordinate);
                if (!byHandlerAndRole.ContainsKey(key)) { byHandlerAndRole[key] = model; }
            }

            HashSet<string> emitted = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (HandlerModel handler in handlers)
            {
                string baseTypeFull;
                bool baseHasPre;
                bool baseHasPost;
                if (handler.RequestType is not null)
                {
                    // Typed On<TRequest>() -> generic message context (pre at a pre-stage, post with a Response at
                    // post-stage). Every stage config carries Handle(Delegate), so both bind.
                    baseHasPre = true;
                    baseHasPost = handler.IsPost;
                    string messageBase = handler.IsPost ? "MessagePostContext" : "MessagePreContext";
                    baseTypeFull = $"global::Vela.Context.{messageBase}<{handler.RequestType}, {handler.EntityType}>";
                }
                else if (IsSingleImageMessage(handler.Message) &&
                    ResolveBaseContext(handler.Message, handler.IsPost, out baseHasPre, out baseHasPost) is string crud)
                {
                    // Create/Update/Delete/Assign/Merge at an image-bearing stage: derive from the concrete context and expose a
                    // single typed pre/post image when captured.
                    baseTypeFull = $"global::Vela.Context.{crud}<{handler.EntityType}>";
                }
                else
                {
                    // Every other (message, stage) - bulk, the ~45 message triggers, and the image-less CRUD stages
                    // (e.g. Create pre) - gets a bare subclass of the plain message context. No single-image property
                    // (bulk images are per-record; those messages carry none). ExtractHandler already verified the
                    // base context type exists. A stable signature either way: capturing an image later only enriches
                    // the CRUD branch above.
                    string stageSuffix = handler.IsPost ? "PostContext" : "PreContext";
                    baseTypeFull = $"global::Vela.Context.{handler.Message}{stageSuffix}<{handler.EntityType}>";
                    baseHasPre = false;
                    baseHasPost = false;
                }

                string handlerKey = handler.Namespace + "|" + string.Join(".", handler.ContainingTypes) + "|" + handler.HandlerName + "|";
                // Only Merge has a subordinate record, and its context exposes the subordinate images at the same stages
                // as the master's.
                bool baseHasSubordinate = handler.RequestType is null && handler.Message == "Merge";
                ProjectionModel? pre = baseHasPre && byHandlerAndRole.TryGetValue(handlerKey + RoleKey(true, false), out ProjectionModel p) ? p : null;
                ProjectionModel? post = baseHasPost && byHandlerAndRole.TryGetValue(handlerKey + RoleKey(false, false), out ProjectionModel po) ? po : null;
                ProjectionModel? subordinatePre = baseHasSubordinate && baseHasPre && byHandlerAndRole.TryGetValue(handlerKey + RoleKey(true, true), out ProjectionModel sp) ? sp : null;
                ProjectionModel? subordinatePost = baseHasSubordinate && baseHasPost && byHandlerAndRole.TryGetValue(handlerKey + RoleKey(false, true), out ProjectionModel spo) ? spo : null;

                string contextName = Sanitize(handler.HandlerName) + "Context";
                string prefix = handler.Namespace.Length == 0 ? string.Empty : handler.Namespace + ".";
                string contextFull = prefix + string.Join(".", handler.ContainingTypes.Select(DeclarationName)) + "." + contextName;

                // A handler reused across steps would key two contexts to the same name; emit once (VELA016 reports
                // the reuse as an error).
                if (!emitted.Add(contextFull))
                {
                    continue;
                }

                context.AddSource(Sanitize(contextFull) + ".Context.g.cs",
                    SourceText.From(RenderContext(handler, contextName, baseTypeFull, pre, post, subordinatePre, subordinatePost), Encoding.UTF8));
            }
        }

        private static string RoleKey(bool isPre, bool isSubordinate) => (isSubordinate ? "Subordinate" : string.Empty) + (isPre ? "Pre" : "Post");

        // The alias argument a subordinate image read passes: its default alias is not the one PreImageAs/PostImageAs
        // default to, so it is always explicit.
        private static string SubordinateAliasArgument(ProjectionModel image, string defaultAlias) =>
            ", " + SymbolDisplay.FormatLiteral(string.IsNullOrEmpty(image.Alias) ? defaultAlias : image.Alias!, quote: true);

        // ", \"snap\"" for a custom alias, nothing for the default one (the extension's default argument matches it).
        private static string AliasArgument(ProjectionModel image) =>
            string.IsNullOrEmpty(image.Alias) ? string.Empty : ", " + SymbolDisplay.FormatLiteral(image.Alias!, quote: true);

        private static bool IsSingleImageMessage(string message) =>
            message == "Create" || message == "Update" || message == "Delete" || message == "Assign" || message == "Merge";

        // The concrete step context a generated context derives from, and which typed images it can expose, for a
        // Create/Update/Delete/Assign/Merge message at the given stage. Returns null when the stage carries no image.
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
                case "Assign":
                    hasPre = true;
                    hasPost = isPost;
                    return isPost ? "AssignPostContext" : "AssignPreContext";
                case "Merge":
                    hasPre = true;
                    hasPost = isPost;
                    return isPost ? "MergePostContext" : "MergePreContext";
                default:
                    return null;
            }
        }

        private static string RenderContext(HandlerModel model, string contextName, string baseTypeFull, ProjectionModel? pre, ProjectionModel? post,
            ProjectionModel? subordinatePre, ProjectionModel? subordinatePost)
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
                sb.AppendLine($"{Pad(indent)}partial {containingType}");
                sb.AppendLine($"{Pad(indent)}{{");
                indent++;
            }

            string images = pre != null || post != null || subordinatePre != null || subordinatePost != null
                ? "plus the typed image(s) this step captured"
                : "for this step";
            sb.AppendLine($"{Pad(indent)}/// <summary>");
            sb.AppendLine($"{Pad(indent)}/// Generated step context for the '{model.HandlerName}' handler: the message context {images}.");
            sb.AppendLine($"{Pad(indent)}/// Bind it with .Handle({model.HandlerName}). Capturing an image later adds a typed ctx.PreImage / ctx.PostImage without changing this signature.");
            sb.AppendLine($"{Pad(indent)}/// </summary>");
            sb.AppendLine($"{Pad(indent)}public sealed class {contextName} : {baseTypeFull}");
            sb.AppendLine($"{Pad(indent)}{{");
            sb.AppendLine($"{Pad(indent + 1)}public {contextName}(global::Vela.Context.ILocalPluginContext inner) : base(inner) {{ }}");
            if (pre is { } preImage)
            {
                sb.AppendLine($"{Pad(indent + 1)}public new {preImage.TypeName}? PreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<{preImage.TypeName}>(this{AliasArgument(preImage)});");
            }

            if (post is { } postImage)
            {
                sb.AppendLine($"{Pad(indent + 1)}public new {postImage.TypeName}? PostImage => global::Vela.Context.ImageProjectionExtensions.PostImageAs<{postImage.TypeName}>(this{AliasArgument(postImage)});");
            }

            if (subordinatePre is { } subordinatePreImage)
            {
                sb.AppendLine($"{Pad(indent + 1)}public new {subordinatePreImage.TypeName}? SubordinatePreImage => global::Vela.Context.ImageProjectionExtensions.PreImageAs<{subordinatePreImage.TypeName}>(this{SubordinateAliasArgument(subordinatePreImage, "SubordinatePreImage")});");
            }

            if (subordinatePost is { } subordinatePostImage)
            {
                sb.AppendLine($"{Pad(indent + 1)}public new {subordinatePostImage.TypeName}? SubordinatePostImage => global::Vela.Context.ImageProjectionExtensions.PostImageAs<{subordinatePostImage.TypeName}>(this{SubordinateAliasArgument(subordinatePostImage, "SubordinatePostImage")});");
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
            return prefix + string.Join(".", model.ContainingTypes.Select(DeclarationName)) + "." + model.TypeName;
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

            // Reopen the plugin class (and any enclosing types, with their own kind and type parameters) as partial parts so the projection is a nested type
            // the handler can read unqualified. The plugin's own declaration must therefore be partial (VELA014).
            foreach (string containingType in model.ContainingTypes)
            {
                sb.AppendLine($"{Pad(indent)}partial {containingType}");
                sb.AppendLine($"{Pad(indent)}{{");
                indent++;
            }

            sb.AppendLine($"{Pad(indent)}/// <summary>");
            sb.AppendLine($"{Pad(indent)}/// Generated projection: exposes only the columns this step captured in {(model.IsSubordinate ? "WithSubordinatePreImage/WithSubordinatePostImage" : "WithPreImage/WithPostImage")}(...).");
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
            sb.AppendLine($"{Pad(indent + 1)}/// when a service needs the whole entity. Equivalent to ctx.{(model.IsPre ? "GetPreImage" : "GetPostImage")}&lt;{model.EntityType}&gt;({ReadAlias(model)})");
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

        // The alias a doc comment shows for reading the full image: none for the default master alias, the subordinate
        // default otherwise.
        private static string ReadAlias(ProjectionModel model)
        {
            string? alias = !string.IsNullOrEmpty(model.Alias)
                ? model.Alias
                : model.IsSubordinate ? (model.IsPre ? "SubordinatePreImage" : "SubordinatePostImage") : null;
            return alias is null ? string.Empty : "\"" + alias + "\"";
        }

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
                string? handlerName, string message, bool isPost, bool isPre, bool isSubordinate, string entityType, string? requestType, string? alias)
            {
                IsSubordinate = isSubordinate;
                Alias = alias;
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
            // An image of a Merge step's subordinate record (WithSubordinatePreImage / WithSubordinatePostImage).
            public bool IsSubordinate { get; }
            public string EntityType { get; }
            public string? RequestType { get; }
            // The image alias passed to WithPreImage/WithPostImage, or null for the default (PreImage / PostImage).
            // The generated context must read the image under this alias, or ctx.PreImage / ctx.PostImage is null.
            public string? Alias { get; }

            public ProjectionModel WithTypeName(string typeName) =>
                new ProjectionModel(Namespace, ContainingTypes, typeName, Columns, HandlerName, Message, IsPost, IsPre, IsSubordinate, EntityType, RequestType, Alias);

            public bool Equals(ProjectionModel other) =>
                Namespace == other.Namespace &&
                ContainingTypes.SequenceEqual(other.ContainingTypes) &&
                TypeName == other.TypeName &&
                Columns.SequenceEqual(other.Columns) &&
                HandlerName == other.HandlerName &&
                Message == other.Message &&
                IsPost == other.IsPost &&
                IsPre == other.IsPre &&
                IsSubordinate == other.IsSubordinate &&
                EntityType == other.EntityType &&
                RequestType == other.RequestType &&
                Alias == other.Alias;

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
                    hash = (hash * 31) + (Alias?.GetHashCode() ?? 0);
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

        // The outcome of reading one image capture: a projection to emit, a reason it cannot be emitted (reported as
        // VELA021), or neither (not a typed capture at all). Value equality so the incremental pipeline caches it.
        private readonly struct ProjectionResult : System.IEquatable<ProjectionResult>
        {
            public ProjectionResult(ProjectionModel model)
            {
                Model = model;
                Skip = null;
            }

            private ProjectionResult(SkippedProjection skip)
            {
                Model = null;
                Skip = skip;
            }

            public ProjectionModel? Model { get; }
            public SkippedProjection? Skip { get; }

            public static ProjectionResult Skipped(InvocationExpressionSyntax invocation, string method, string reason)
            {
                SyntaxNode name = ((MemberAccessExpressionSyntax)invocation.Expression).Name;
                FileLinePositionSpan line = name.GetLocation().GetLineSpan();
                return new ProjectionResult(new SkippedProjection(line.Path, name.Span, line.Span, method, reason));
            }

            public bool Equals(ProjectionResult other) => Nullable.Equals(Model, other.Model) && Nullable.Equals(Skip, other.Skip);

            public override bool Equals(object? obj) => obj is ProjectionResult other && Equals(other);

            public override int GetHashCode() => ((Model?.GetHashCode() ?? 0) * 397) ^ (Skip?.GetHashCode() ?? 0);
        }

        // Where and why a projection was not generated. Holds the location as plain values (not a Location, which
        // pins a syntax tree) so it stays equatable across incremental runs.
        private readonly struct SkippedProjection : System.IEquatable<SkippedProjection>
        {
            public SkippedProjection(string path, TextSpan span, LinePositionSpan lineSpan, string method, string reason)
            {
                Path = path;
                Span = span;
                LineSpan = lineSpan;
                Method = method;
                Reason = reason;
            }

            public string Path { get; }
            public TextSpan Span { get; }
            public LinePositionSpan LineSpan { get; }
            public string Method { get; }
            public string Reason { get; }

            public Location ToLocation() => Location.Create(Path, Span, LineSpan);

            public bool Equals(SkippedProjection other) =>
                Path == other.Path && Span == other.Span && LineSpan == other.LineSpan && Method == other.Method && Reason == other.Reason;

            public override bool Equals(object? obj) => obj is SkippedProjection other && Equals(other);

            public override int GetHashCode() => (Path?.GetHashCode() ?? 0) ^ Span.GetHashCode() ^ (Reason?.GetHashCode() ?? 0);
        }
    }
}
