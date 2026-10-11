namespace Vela.StepApiGen;

/// <summary>
/// Emits src/Vela.Plugins/Steps/StepBuilder.Impl.cs: <c>StepBuilder&lt;TEntity&gt;</c> explicitly implements every
/// interface of StepApi.cs (each call just records into the step definition and returns <c>this</c>), and
/// <c>PluginStepsRegistrar&lt;TEntity&gt;</c> forwards each trigger to a fresh builder.
/// </summary>
internal static class StepBuilderImplEmitter
{
    public const string RelativePath = "src/Vela.Plugins/Steps/StepBuilder.Impl.cs";

    public static string Emit()
    {
        var messages = MessageModel.Messages;
        var w = new CodeWriter();
        w.Header("Step builder impl.");
        w.Block(Usings);

        // Implemented interfaces.
        var implemented = new List<string> { "IStepTrigger<TEntity>" };
        implemented.AddRange(messages.Select(m => StepConfig.StagesType(m.Name)));
        foreach (var message in messages)
        {
            foreach (var stage in StageExtensions.All)
            {
                string config = StepConfig.ConfigType(message.Name, stage);
                implemented.Add($"{stage.ModeInterface()}<{config}>");
                implemented.Add(config);
            }
        }
        implemented.Add("IPermissiveStages<TEntity>");
        foreach (var stage in StageExtensions.All)
        {
            string config = StepConfig.ForPermissive(stage).Type;
            implemented.Add($"{stage.ModeInterface()}<{config}>");
            implemented.Add(config);
        }
        w.Line("    public sealed partial class StepBuilder<TEntity> :");
        for (int i = 0; i < implemented.Count; i++)
        {
            w.Line($"        {implemented[i]}{(i < implemented.Count - 1 ? "," : "")}");
        }
        w.Line("        where TEntity : Entity");
        w.Line("    {");

        // Triggers: record the message, return the builder as the stage selector.
        foreach (var message in messages)
        {
            string entity = message.Entity is null ? string.Empty : $", \"{message.Entity}\"";
            w.Line($"        {StepConfig.StagesType(message.Name)} IStepTrigger<TEntity>.On{message.Name}() {{ SetMessage(PluginMessages.{message.Name}{entity}); return this; }}");
        }
        w.Block(UnmodelledTriggers);


        // Permissive surface.
        WriteStages(w, "IPermissiveStages<TEntity>", stage => StepConfig.ForPermissive(stage).Type);
        w.Line();
        WriteModes(w, stage => StepConfig.ForPermissive(stage).Type);
        w.Line();
        foreach (var stage in StageExtensions.All)
        {
            WriteConfig(w, StepConfig.ForPermissive(stage));
            w.Line();
        }

        // Internal setters used by TypedStepAdapter.cs.
        w.Block(InternalSetters);

        foreach (var message in messages)
        {
            WriteStages(w, StepConfig.StagesType(message.Name), stage => StepConfig.ConfigType(message.Name, stage));
        }
        w.Line();
        foreach (var message in messages)
        {
            WriteModes(w, stage => StepConfig.ConfigType(message.Name, stage));
        }
        w.Line();

        for (int i = 0; i < messages.Count; i++)
        {
            foreach (var stage in StageExtensions.All)
            {
                WriteConfig(w, StepConfig.ForMessage(messages[i], stage));
            }
            if (i == 0)
            {
                w.Block(SharedDelegateHandle);
            }
            w.Line();
        }
        w.Line("    }");
        w.Line("}");
        w.Line();

        // The registrar: every trigger starts a fresh builder.
        w.Line("namespace Vela.Plugin");
        w.Line("{");
        w.Line("    internal sealed partial class PluginStepsRegistrar<TEntity> : IStepTrigger<TEntity> where TEntity : Entity");
        w.Line("    {");
        foreach (var message in messages)
        {
            w.Line($"        {StepConfig.StagesType(message.Name)} IStepTrigger<TEntity>.On{message.Name}() => ((IStepTrigger<TEntity>)BeginStep()).On{message.Name}();");
        }
        w.Block(RegistrarTail);
        return w.ToString();
    }

    private static void WriteStages(CodeWriter w, string stagesType, Func<Stage, string> configType)
    {
        foreach (var stage in StageExtensions.All)
        {
            w.Line($"        {stage.ModeInterface()}<{configType(stage)}> {stagesType}.{stage}() {{ _definition.Stage = PluginStages.{stage}; return this; }}");
        }
    }

    private static void WriteModes(CodeWriter w, Func<Stage, string> configType)
    {
        foreach (var stage in StageExtensions.All)
        {
            string config = configType(stage);
            string mode = $"{stage.ModeInterface()}<{config}>";
            w.Line($"        {config} {mode}.Sync() {{ _definition.Mode = ExecutionMode.Synchronous; return this; }}");
            if (stage == Stage.PostOperation)
            {
                w.Line($"        {config} {mode}.Async() {{ _definition.Mode = ExecutionMode.Asynchronous; return this; }}");
                w.Line($"        {config} {mode}.Async(bool deleteJobOnSuccess) {{ SetAsyncMode(deleteJobOnSuccess); return this; }}");
            }
        }
    }

    private static void WriteConfig(CodeWriter w, StepConfig config)
    {
        foreach (var modifier in config.Modifiers())
        {
            w.Line($"        {config.Type} {config.Type}.{modifier.Signature} {{ {modifier.Body} return this; }}");
        }
        foreach (var handle in config.Handles)
        {
            // Constraints are inherited by an explicit implementation, so they are not repeated here.
            w.Line($"        void {config.Type}.Handle{handle.TypeParameters}({handle.Parameter}) {{ SetHandler(handler); }}");
        }
    }

    private const string Usings = """
        using System;
        using System.Linq.Expressions;
        using Vela.Constants;
        using Vela.Context;
        using Vela.Steps;
        using Microsoft.Xrm.Sdk;

        namespace Vela.Steps
        {
        """;

    private const string UnmodelledTriggers = """
                IPermissiveStages<TEntity> IStepTrigger<TEntity>.OnMessage(string messageName) { if (string.IsNullOrWhiteSpace(messageName)) throw new ArgumentNullException(nameof(messageName)); SetMessage(messageName); return this; }
                IPermissiveStages<TEntity> IStepTrigger<TEntity>.OnMessage<TMessage>() { SetMessage(ExpressionHelper.GetMessageName<TMessage>()); return this; }
                ITypedStages<TEntity, TRequest> IStepTrigger<TEntity>.On<TRequest>() { SetTypedRequestMessage(ExpressionHelper.GetMessageName<TRequest>(), typeof(TRequest), "On"); return new TypedStepAdapter<TEntity, TRequest>(this); }
                IAsyncOnlyTypedStages<TEntity, TRequest> IStepTrigger<TEntity>.OnAsyncOnly<TRequest>() { SetTypedRequestMessage(ExpressionHelper.GetMessageName<TRequest>(), typeof(TRequest), "OnAsyncOnly"); return new TypedStepAdapter<TEntity, TRequest>(this); }

        """;

    private const string InternalSetters = """
                internal void AddImageInternal(string alias, bool isPre, Expression<Func<TEntity, object?>>[] attributes) => AddImageNow(alias, isPre, attributes);
                internal void SetExecutionOrderInternal(int order) => SetExecutionOrder(order);
                internal void SetStepKeyInternal(string key) => SetKey(key);
                internal void SetSkipWhenDepthAboveInternal(int maxDepth) => SetSkipWhenDepthAbove(maxDepth);
                internal void SetDescriptionInternal(string description) => SetDescription(description);
                internal void SetImpersonationUserInternal(System.Guid systemUserId) => SetImpersonationUser(systemUserId);
                internal void SetSupportedDeploymentInternal(SupportedDeployment deployment) => SetSupportedDeployment(deployment);
                internal void SetUnsecureConfigInternal(string configuration) => SetUnsecureConfig(configuration);
                internal void SetSecureConfigNameInternal(string name) => SetSecureConfigName(name);
                internal void SetDisabledInternal() => SetDisabled();

        """;

    private const string SharedDelegateHandle = """
                // One impl of the shared IStepConfig<TEntity>.Handle(Delegate) serves every config interface (they all derive
                // from it), so a generated {Handler}Context binds on any stage. Each config keeps its own typed Handle(Action)
                // above; overload resolution prefers it for a handler taking the plain message context.
                void IStepConfig<TEntity>.Handle(Delegate handler) { SetHandler(handler); }
        """;

    private const string RegistrarTail = """
                IPermissiveStages<TEntity> IStepTrigger<TEntity>.OnMessage(string messageName) => ((IStepTrigger<TEntity>)BeginStep()).OnMessage(messageName);
                IPermissiveStages<TEntity> IStepTrigger<TEntity>.OnMessage<TMessage>() => ((IStepTrigger<TEntity>)BeginStep()).OnMessage<TMessage>();
                ITypedStages<TEntity, TRequest> IStepTrigger<TEntity>.On<TRequest>() => ((IStepTrigger<TEntity>)BeginStep()).On<TRequest>();
                IAsyncOnlyTypedStages<TEntity, TRequest> IStepTrigger<TEntity>.OnAsyncOnly<TRequest>() => ((IStepTrigger<TEntity>)BeginStep()).OnAsyncOnly<TRequest>();
            }
        }
        """;
}
