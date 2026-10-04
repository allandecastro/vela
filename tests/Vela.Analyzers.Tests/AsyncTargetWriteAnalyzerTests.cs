using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.AsyncTargetWriteAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class AsyncTargetWriteAnalyzerTests
{
    private const string Harness = @"
using System;

namespace Microsoft.Xrm.Sdk
{
    public class Entity { public object? this[string name] { get => null; set { } } }
}

namespace Demo
{
    public class Account : Microsoft.Xrm.Sdk.Entity { public string? Name { get; set; } public int Count { get; set; } }
}

namespace Vela.Messages
{
    public interface IRequest { Demo.Account Target { get; } }
}

namespace Vela.Context
{
    public interface ILocalPluginContext
    {
        T GetTarget<T>() where T : Microsoft.Xrm.Sdk.Entity;
        Vela.Messages.IRequest Request { get; }
    }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnUpdate(); }
    public interface IStages : IFluentStep { IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); }
    public interface IConfig : IFluentStep { void Handle(Action<Vela.Context.ILocalPluginContext> handler); }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; }
}
";

    private static string InClass(string body) => Harness + @"
namespace Demo
{
    using Vela.Context;
    public class Use : Plugin
    {
        " + body + @"
    }
}
";

    private static DiagnosticResult Expected() =>
        new DiagnosticResult(Vela.Analyzers.AsyncTargetWriteAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(0);

    [Fact]
    public async Task Flags_a_write_to_Request_Target_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { {|#0:ctx.Request.Target[\"name\"]|} = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_a_write_through_GetTarget_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { {|#0:ctx.GetTarget<Account>().Name|} = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_a_compound_assignment_to_the_Target_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { {|#0:ctx.GetTarget<Account>().Name|} ??= \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_an_increment_of_a_Target_property_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { {|#0:ctx.Request.Target.Count|}++; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Does_not_flag_a_write_to_the_Target_in_a_sync_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.Request.Target[\"name\"] = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_read_of_the_Target_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { object? x = ctx.Request.Target[\"name\"]; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_write_through_a_local_copy_of_the_Target()
    {
        // Conservative: the write base is a local, not the Target expression itself, so it is not tracked.
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { var t = ctx.GetTarget<Account>(); t.Name = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_when_the_handler_is_shared_with_a_sync_step()
    {
        // Bound by two chains (async + sync): which stage runs at the write site is ambiguous, so stay silent.
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.Request.Target[\"name\"] = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code);
    }
}
