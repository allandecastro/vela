using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Moq;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Xunit;

namespace Vela.Tests
{
    /// <summary>
    /// Guards the "parallel per-message lists" that must be kept in lockstep for every modelled SDK message:
    /// the <c>PluginMessages</c> constant, the pre/post context interface + class, the <c>I{Msg}StepStage</c>
    /// step interface, the <c>On{Msg}()</c> builder method, and — critically — the <c>BuildTypedContext</c> arm.
    /// Adding a message but forgetting one arm previously degraded silently (the handler received the plain
    /// <see cref="ILocalPluginContext"/> instead of its typed context, with no compile error); these tests turn
    /// that silent divergence into a red test. See <c>.context/review/message-model.md</c>.
    /// </summary>
    public sealed class MessageModelConsistencyTests
    {
        // Messages intentionally present as a constant only (reachable via OnMessage(...)), with no typed
        // context/step/builder/dispatch entry. Modelling one of these means removing it from this set on purpose.
        private static readonly HashSet<string> ConstantOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            PluginMessages.Assign,
            PluginMessages.SetState,
            PluginMessages.SetStateDynamicEntity,
            // WhoAmI is a global (entity-less) message: it cannot be registered as an entity-bound plugin step, so
            // it is not modelled with a typed On.../context. It stays a constant (reachable via OnMessage / used by
            // `vela models --messages` to generate the SDK request), just not a step.
            PluginMessages.WhoAmI,
        };

        private static readonly Assembly PluginsAssembly = typeof(PluginMessages).Assembly;

        private static IEnumerable<string> AllMessageConstants() =>
            typeof(PluginMessages)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!);

        public static IEnumerable<object[]> ModelledMessages() =>
            AllMessageConstants().Where(m => !ConstantOnly.Contains(m)).Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(ModelledMessages))]
        public void Modelled_message_has_every_parallel_list_entry(string message)
        {
            // 1. Builder entry On{Msg}() on the public IStepTrigger<TEntity>.
            Assert.True(
                typeof(Vela.Steps.IStepTrigger<>).GetMethod($"On{message}") != null,
                $"IStepTrigger<TEntity> is missing On{message}().");

            // 2. Step-stage interface I{Msg}Stages<TEntity>.
            AssertTypeExists($"Vela.Steps.I{message}Stages`1");

            // 3. Pre/Post context interfaces and their runtime classes.
            AssertTypeExists($"Vela.Context.I{message}PreContext`1");
            AssertTypeExists($"Vela.Context.I{message}PostContext`1");
            AssertTypeExists($"Vela.Context.{message}PreContext`1");
            AssertTypeExists($"Vela.Context.{message}PostContext`1");

            // 4. BuildTypedContext arm actually dispatches to the typed context (the arm that silently degrades to
            //    the untyped ILocalPluginContext when forgotten). Invoke it for a pre and a post stage.
            AssertBuildsTypedContext(message, PluginStages.PreOperation, $"{message}PreContext`1");
            AssertBuildsTypedContext(message, PluginStages.PostOperation, $"{message}PostContext`1");
        }

        [Fact]
        public void Constant_only_messages_have_no_typed_builder_method()
        {
            // If one of these gains a typed On{Msg}() it must be removed from ConstantOnly (a deliberate change),
            // so the theory above starts guarding the rest of its parallel lists too.
            foreach (string message in ConstantOnly)
            {
                Assert.True(
                    typeof(Vela.Steps.IStepTrigger<>).GetMethod($"On{message}") == null,
                    $"'{message}' is listed as constant-only but IStepTrigger now models On{message}(); remove it from ConstantOnly.");
            }
        }

        private static void AssertTypeExists(string fullNameWithArity)
            => Assert.True(PluginsAssembly.GetType(fullNameWithArity) != null, $"Missing type {fullNameWithArity}.");

        private static readonly MethodInfo BuildTypedContext =
            typeof(EntityPluginBase<>).MakeGenericType(typeof(Entity))
                .GetMethod("BuildTypedContext", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("EntityPluginBase<TEntity>.BuildTypedContext not found.");

        private static void AssertBuildsTypedContext(string message, PluginStages stage, string expectedTypeName)
        {
            ILocalPluginContext ctx = Mock.Of<ILocalPluginContext>();
            object? built = BuildTypedContext.Invoke(null, new object?[] { message, stage, ctx });
            Assert.True(built != null, $"BuildTypedContext returned null for '{message}' at {stage} (missing switch arm → silent untyped fallback).");
            Assert.Equal(expectedTypeName, built!.GetType().Name);
        }
    }
}
