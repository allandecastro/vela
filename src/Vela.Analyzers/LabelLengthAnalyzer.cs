using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Vela.Constants;

namespace Vela.Analyzers
{
    /// <summary>
    /// Reports a compile-time constant label longer than the Dataverse column it is deployed to: a Custom API display
    /// name (100) or description (300), a request parameter description (300), a response property description (100),
    /// and a step description once Vela appends its step-key marker (256 in all). <c>vela plan</c> rejects the same
    /// labels (it stays the authority, and covers values only known at run time); this rule moves the failure to the
    /// build. The limits come from <see cref="DataverseLabelLimits"/>, shared with the CLI.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LabelLengthAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = DiagnosticIds.LabelTooLong;

        private const string Category = "Vela.Usage";
        private const string Description = "Dataverse stores at most 100 characters in a Custom API display name, 300 in its description, 300 in a request parameter description, 100 in a response property description and 256 in a step description (with the step-key marker Vela appends). vela plan rejects a longer label; this rule reports a constant one at build time.";

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Label longer than Dataverse stores",
            messageFormat: "{0} has {1} characters (Dataverse stores at most {2})",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description);

        internal static readonly DiagnosticDescriptor StepRule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Label longer than Dataverse stores",
            messageFormat: "The step description has {0} characters, but Dataverse stores at most {1} once Vela appends its {2}-character step-key marker",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description);

        private const string MetadataInterface = "Vela.CustomApi.ICustomApiMetadata`1";
        private const string LightBuilderInterface = "Vela.CustomApi.ICustomApiBuilder";
        private const string ParameterBuilderInterface = "Vela.CustomApi.ICustomApiParameterBuilder`1";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, StepRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                Types types = new Types(start.Compilation);
                if (types.Metadata is not null || types.LightBuilder is not null || types.ParameterBuilder is not null || types.Fluent is not null)
                {
                    start.RegisterOperationAction(operation => AnalyzeInvocation(operation, types), OperationKind.Invocation);
                }

                if (types.Description is not null && (types.OrganizationRequest is not null || types.OrganizationResponse is not null))
                {
                    start.RegisterSymbolAction(symbol => AnalyzeProperty(symbol, types), SymbolKind.Property);
                }
            });
        }

        private sealed class Types
        {
            public Types(Compilation compilation)
            {
                Metadata = compilation.GetTypeByMetadataName(MetadataInterface);
                LightBuilder = compilation.GetTypeByMetadataName(LightBuilderInterface);
                ParameterBuilder = compilation.GetTypeByMetadataName(ParameterBuilderInterface);
                Fluent = compilation.GetTypeByMetadataName(FluentStep.FluentStepMetadataName);
                Description = compilation.GetTypeByMetadataName("System.ComponentModel.DescriptionAttribute");
                OrganizationRequest = compilation.GetTypeByMetadataName("Microsoft.Xrm.Sdk.OrganizationRequest");
                OrganizationResponse = compilation.GetTypeByMetadataName("Microsoft.Xrm.Sdk.OrganizationResponse");
            }

            public INamedTypeSymbol? Metadata { get; }
            public INamedTypeSymbol? LightBuilder { get; }
            public INamedTypeSymbol? ParameterBuilder { get; }
            public INamedTypeSymbol? Fluent { get; }
            public INamedTypeSymbol? Description { get; }
            public INamedTypeSymbol? OrganizationRequest { get; }
            public INamedTypeSymbol? OrganizationResponse { get; }
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, Types types)
        {
            IInvocationOperation invocation = (IInvocationOperation)context.Operation;
            IMethodSymbol method = invocation.TargetMethod;
            INamedTypeSymbol type = method.ContainingType;

            switch (method.Name)
            {
                case "DisplayName" when Implements(type, types.Metadata):
                    Check(context, Argument(invocation, "displayName"), "The Custom API display name", DataverseLabelLimits.CustomApiDisplayName);
                    break;
                case "Description" when Implements(type, types.Metadata):
                    Check(context, Argument(invocation, "description"), "The Custom API description", DataverseLabelLimits.CustomApiDescription);
                    break;
                case "RequestParameter" when Implements(type, types.LightBuilder):
                    Check(context, Argument(invocation, "description"), ParameterLabel("request parameter", ConstantString(Argument(invocation, "name"))), DataverseLabelLimits.CustomApiRequestParameterDescription);
                    break;
                case "ResponseParameter" when Implements(type, types.LightBuilder):
                    Check(context, Argument(invocation, "description"), ParameterLabel("response property", ConstantString(Argument(invocation, "name"))), DataverseLabelLimits.CustomApiResponsePropertyDescription);
                    break;
                case "Add" when Implements(type, types.ParameterBuilder):
                    AnalyzeParameterAdd(context, invocation, types);
                    break;
                case "WithDescription" when types.Fluent is not null && FluentStep.ImplementsFluent(type, types.Fluent):
                    AnalyzeStepDescription(context, invocation, types.Fluent);
                    break;
            }
        }

        // Add(x => x.Prop, optional, description): the model is the request (TModel derives from OrganizationRequest)
        // inside .Request(...), the response inside .Response(...). A model of neither kind is left alone.
        private static void AnalyzeParameterAdd(OperationAnalysisContext context, IInvocationOperation invocation, Types types)
        {
            IArgumentOperation? description = Argument(invocation, "description");
            if (description is null || ConstantString(description) is null)
            {
                return;
            }

            INamedTypeSymbol builder = invocation.TargetMethod.ContainingType;
            ITypeSymbol? model = builder.AllInterfaces.Add(builder)
                .FirstOrDefault(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, types.ParameterBuilder))?
                .TypeArguments[0];

            string? name = PropertyName(Argument(invocation, "property"));
            if (DerivesFrom(model, types.OrganizationResponse))
            {
                Check(context, description, ParameterLabel("response property", name), DataverseLabelLimits.CustomApiResponsePropertyDescription);
            }
            else if (DerivesFrom(model, types.OrganizationRequest))
            {
                Check(context, description, ParameterLabel("request parameter", name), DataverseLabelLimits.CustomApiRequestParameterDescription);
            }
        }

        // [Description("...")] on a property of a request or response model: CustomApiBuilder<TRequest, TResponse>
        // takes it as the parameter description when none is passed to Add(...), and for every inferred parameter.
        private static void AnalyzeProperty(SymbolAnalysisContext context, Types types)
        {
            IPropertySymbol property = (IPropertySymbol)context.Symbol;
            if (property.IsStatic || property.IsIndexer)
            {
                return;
            }

            bool response = DerivesFrom(property.ContainingType, types.OrganizationResponse);
            if (!response && !DerivesFrom(property.ContainingType, types.OrganizationRequest))
            {
                return;
            }

            foreach (AttributeData attribute in property.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, types.Description)
                    || attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not string text)
                {
                    continue;
                }

                int max = response ? DataverseLabelLimits.CustomApiResponsePropertyDescription : DataverseLabelLimits.CustomApiRequestParameterDescription;
                if (text.Length <= max)
                {
                    continue;
                }

                SyntaxNode? syntax = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken);
                Location location = syntax is AttributeSyntax { ArgumentList.Arguments.Count: > 0 } applied
                    ? applied.ArgumentList.Arguments[0].GetLocation()
                    : syntax?.GetLocation() ?? property.Locations[0];
                string label = ParameterLabel(response ? "response property" : "request parameter", property.Name);
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, label, text.Length, max));
            }
        }

        // The step description Vela deploys is the trimmed text, a line break and [vela:stepkey=<key>]. The key is the
        // chain's WithStepKey(...) when it has one (a non-constant key is not analyzed), otherwise the automatic key
        // ("dv-" and 12 hex characters). A chain split across a local is not analyzed: its key may be set after the split.
        private static void AnalyzeStepDescription(OperationAnalysisContext context, IInvocationOperation invocation, INamedTypeSymbol fluent)
        {
            IArgumentOperation? argument = Argument(invocation, "description");
            string? text = ConstantString(argument)?.Trim();
            if (argument is null || string.IsNullOrEmpty(text) || invocation.Syntax is not InvocationExpressionSyntax syntax)
            {
                return;
            }

            SemanticModel model = invocation.SemanticModel!;
            int? keyLength = DataverseLabelLimits.AutoStepKeyPrefix.Length + DataverseLabelLimits.AutoStepKeyHashLength;
            InvocationExpressionSyntax outermost = syntax;
            foreach (InvocationExpressionSyntax call in LaterCalls(syntax))
            {
                outermost = call;
                keyLength = StepKeyLength(call, model, fluent, context) ?? keyLength;
                if (keyLength < 0)
                {
                    return;
                }
            }

            if (!IsWholeChain(outermost))
            {
                return;
            }

            if (syntax.Expression is MemberAccessExpressionSyntax member)
            {
                foreach (ChainCall call in FluentStep.Spine(member.Expression))
                {
                    keyLength = StepKeyLength(call.Invocation, model, fluent, context) ?? keyLength;
                    if (keyLength < 0)
                    {
                        return;
                    }
                }
            }

            int markerLength = DataverseLabelLimits.StepKeyMarkerLength(keyLength!.Value);
            int max = DataverseLabelLimits.StepDescription - markerLength;
            if (text!.Length > max)
            {
                context.ReportDiagnostic(Diagnostic.Create(StepRule, argument.Value.Syntax.GetLocation(), text.Length, max, markerLength));
            }
        }

        // The trimmed length of the key a WithStepKey(...) call sets, -1 for a key that is not a constant, or null when
        // the call is not a WithStepKey on the step builder.
        private static int? StepKeyLength(InvocationExpressionSyntax call, SemanticModel model, INamedTypeSymbol fluent, OperationAnalysisContext context)
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithStepKey" }
                || !FluentStep.IsFluentMethod(model, call, fluent, context.CancellationToken)
                || call.ArgumentList.Arguments.Count != 1)
            {
                return null;
            }

            Optional<object?> key = model.GetConstantValue(call.ArgumentList.Arguments[0].Expression, context.CancellationToken);
            return key.HasValue && key.Value is string value ? value.Trim().Length : -1;
        }

        // The calls chained after this one, nearest first: in RegisterStep().WithDescription("x").WithStepKey("k").Handle(H)
        // the calls after WithDescription(...) are WithStepKey(...) then Handle(...).
        private static System.Collections.Generic.IEnumerable<InvocationExpressionSyntax> LaterCalls(InvocationExpressionSyntax invocation)
        {
            SyntaxNode current = invocation;
            while (true)
            {
                SyntaxNode? parent = current.Parent;
                while (parent is ParenthesizedExpressionSyntax || (parent is PostfixUnaryExpressionSyntax suppressed && suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression)))
                {
                    current = parent;
                    parent = parent.Parent;
                }

                if (parent is MemberAccessExpressionSyntax member && member.Expression == current && member.Parent is InvocationExpressionSyntax next && next.Expression == member)
                {
                    yield return next;
                    current = next;
                    continue;
                }

                yield break;
            }
        }

        // A chain is whole when it is a statement of its own (or a lambda's expression body), not stored for later calls.
        private static bool IsWholeChain(InvocationExpressionSyntax outermost)
        {
            SyntaxNode? parent = outermost.Parent;
            while (parent is ParenthesizedExpressionSyntax)
            {
                parent = parent.Parent;
            }

            return parent is ExpressionStatementSyntax || parent is ArrowExpressionClauseSyntax || parent is LambdaExpressionSyntax;
        }

        private static void Check(OperationAnalysisContext context, IArgumentOperation? argument, string label, int max)
        {
            string? value = ConstantString(argument);
            if (value is not null && value.Length > max)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, argument!.Value.Syntax.GetLocation(), label, value.Length, max));
            }
        }

        private static string ParameterLabel(string kind, string? name)
            => name is null ? $"The description of a {kind}" : $"The description of {kind} '{name}'";

        // An argument the caller wrote (a parameter left at its default is not a label the caller set).
        private static IArgumentOperation? Argument(IInvocationOperation invocation, string parameterName)
        {
            foreach (IArgumentOperation argument in invocation.Arguments)
            {
                if (argument.Parameter?.Name == parameterName && argument.ArgumentKind != ArgumentKind.DefaultValue)
                {
                    return argument;
                }
            }

            return null;
        }

        private static string? ConstantString(IArgumentOperation? argument)
        {
            if (argument is null)
            {
                return null;
            }

            IOperation value = argument.Value;
            while (value is IConversionOperation conversion && !value.ConstantValue.HasValue)
            {
                value = conversion.Operand;
            }

            return value.ConstantValue.HasValue ? value.ConstantValue.Value as string : null;
        }

        // The property a parameter selector names: x => x.Prop.
        private static string? PropertyName(IArgumentOperation? argument)
        {
            if (argument?.Value.Syntax is LambdaExpressionSyntax lambda && FluentStep.SelectedMember(lambda) is MemberAccessExpressionSyntax member)
            {
                return member.Name.Identifier.ValueText;
            }

            return null;
        }

        private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol? target)
        {
            if (target is null)
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target))
            {
                return true;
            }

            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, target))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DerivesFrom(ITypeSymbol? type, INamedTypeSymbol? target)
        {
            if (type is null || target is null)
            {
                return false;
            }

            if (type is ITypeParameterSymbol parameter)
            {
                return parameter.ConstraintTypes.Any(constraint => DerivesFrom(constraint, target));
            }

            for (ITypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, target))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
