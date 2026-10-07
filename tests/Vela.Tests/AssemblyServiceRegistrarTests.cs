using System;
using Vela.Constants;
using Vela.Context;
using Vela.DI;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

// Declares the Vela.Tests assembly's service registrar. This applies to every plugin executed from this assembly,
// so the registrar only adds a uniquely-named sentinel service (nothing else resolves it) and never overrides a
// framework or plugin registration - keeping the global declaration harmless to the other tests.
[assembly: VelaServiceRegistrar(typeof(Vela.Tests.TestAssemblyRegistrar))]

namespace Vela.Tests
{
    /// <summary>A sentinel service used only to prove the assembly registrar ran; its value records who registered it.</summary>
    public interface ISentinel
    {
        string Value { get; }
    }

    internal sealed class Sentinel : ISentinel
    {
        public Sentinel(string value) => Value = value;

        public string Value { get; }
    }

    internal sealed class TestAssemblyRegistrar : IServiceRegistrar
    {
        // Shared variable that makes this registrar fail, for the registration-failure tests ("business" throws a
        // PluginBusinessException, any other string an InvalidOperationException). Absent everywhere else.
        public const string ThrowSwitch = "vela.tests.registrar.throw";

        public void Register(SimpleServiceResolver services, ILocalPluginContext context)
        {
            if (context.GetSharedVariable(ThrowSwitch) is string mode)
            {
                if (mode == "business")
                {
                    throw new PluginBusinessException("Registrar says no.");
                }

                throw new InvalidOperationException("registrar boom");
            }

            services.AddScoped<ISentinel>(_ => new Sentinel("assembly"));
        }
    }

    // A registrar type that does not implement IServiceRegistrar via a parameterless ctor, for the guard tests.
    internal sealed class RegistrarWithoutParameterlessCtor : IServiceRegistrar
    {
        public RegistrarWithoutParameterlessCtor(int _)
        {
        }

        public void Register(SimpleServiceResolver services, ILocalPluginContext context)
        {
        }
    }

    // Implements the interface but is not instantiable (abstract / open generic), for the guard tests.
    internal abstract class AbstractRegistrar : IServiceRegistrar
    {
        public abstract void Register(SimpleServiceResolver services, ILocalPluginContext context);
    }

    internal sealed class OpenGenericRegistrar<T> : IServiceRegistrar
    {
        public void Register(SimpleServiceResolver services, ILocalPluginContext context)
        {
        }
    }

    /// <summary>
    /// Assembly-level DI hook: <c>[assembly: VelaServiceRegistrar(typeof(...))]</c> registers shared services for
    /// every plugin/Custom API in the assembly, before each plugin's <c>ConfigureServices</c> (so a plugin can still
    /// override). Unit tests cover the attribute + registrar resolution; the execution tests prove the hook fires in
    /// the pipeline and composes with <c>ConfigureServices</c> (last-registration-wins).
    /// </summary>
    public sealed class AssemblyServiceRegistrarTests
    {
        // ---- Attribute + registrar resolution --------------------------------------------------------

        [Fact]
        public void CreateRegistrar_returns_null_when_no_attribute_is_declared()
            => Assert.Null(AssemblyServiceRegistrars.CreateRegistrar(null));

        [Fact]
        public void CreateRegistrar_instantiates_the_declared_registrar()
        {
            IServiceRegistrar? registrar = AssemblyServiceRegistrars.CreateRegistrar(
                new VelaServiceRegistrarAttribute(typeof(TestAssemblyRegistrar)));

            Assert.IsType<TestAssemblyRegistrar>(registrar);
        }

        [Fact]
        public void CreateRegistrar_rejects_a_type_that_does_not_implement_the_interface()
            => Assert.Throws<InvalidPluginExecutionException>(() =>
                AssemblyServiceRegistrars.CreateRegistrar(new VelaServiceRegistrarAttribute(typeof(string))));

        [Fact]
        public void CreateRegistrar_rejects_a_registrar_without_a_parameterless_constructor()
            => Assert.Throws<InvalidPluginExecutionException>(() =>
                AssemblyServiceRegistrars.CreateRegistrar(new VelaServiceRegistrarAttribute(typeof(RegistrarWithoutParameterlessCtor))));

        [Fact]
        public void CreateRegistrar_rejects_an_abstract_registrar()
            => Assert.Throws<InvalidPluginExecutionException>(() =>
                AssemblyServiceRegistrars.CreateRegistrar(new VelaServiceRegistrarAttribute(typeof(AbstractRegistrar))));

        [Fact]
        public void CreateRegistrar_rejects_an_open_generic_registrar()
            => Assert.Throws<InvalidPluginExecutionException>(() =>
                AssemblyServiceRegistrars.CreateRegistrar(new VelaServiceRegistrarAttribute(typeof(OpenGenericRegistrar<>))));

        [Fact]
        public void Attribute_rejects_a_null_type()
            => Assert.Throws<ArgumentNullException>(() => new VelaServiceRegistrarAttribute(null!));

        [Fact]
        public void Resolve_reads_and_caches_the_assembly_attribute()
        {
            IServiceRegistrar? first = AssemblyServiceRegistrars.Resolve(typeof(SentinelProbe));
            IServiceRegistrar? second = AssemblyServiceRegistrars.Resolve(typeof(SentinelProbe));

            Assert.IsType<TestAssemblyRegistrar>(first);
            Assert.Same(first, second); // resolved once per assembly and cached
        }

        // ---- Execution: the hook runs in the pipeline ------------------------------------------------

        [Fact]
        public void Assembly_registrar_services_are_available_in_a_handler()
        {
            SentinelProbe probe = new SentinelProbe();

            PluginContext.For<TestAccount>()
                .OnCreate().Stage(PluginStages.PostOperation).Sync()
                .WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Zava" })
                .Execute(probe);

            Assert.Equal("assembly", probe.Seen); // registered by the assembly registrar, not the plugin
        }

        [Fact]
        public void Per_plugin_ConfigureServices_overrides_the_assembly_registrar()
        {
            OverrideProbe probe = new OverrideProbe();

            PluginContext.For<TestAccount>()
                .OnCreate().Stage(PluginStages.PostOperation).Sync()
                .WithTarget(new TestAccount { Id = Guid.NewGuid(), Name = "Zava" })
                .Execute(probe);

            Assert.Equal("plugin", probe.Seen); // ConfigureServices runs after the assembly registrar and wins
        }

        private sealed class SentinelProbe : EntityPluginBase<TestAccount>
        {
            public string? Seen;

            public SentinelProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);

            private void OnCreated(ICreatePostContext<TestAccount> ctx)
                => Seen = ctx.GetRequiredService<ISentinel>().Value;
        }

        private sealed class OverrideProbe : EntityPluginBase<TestAccount>
        {
            public string? Seen;

            public OverrideProbe() : base(string.Empty, string.Empty) { }

            protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
                => services.AddScoped<ISentinel>(_ => new Sentinel("plugin"));

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);

            private void OnCreated(ICreatePostContext<TestAccount> ctx)
                => Seen = ctx.GetRequiredService<ISentinel>().Value;
        }
    }
}
