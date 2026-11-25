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
    public class AttributeCollection { public object? this[string name] { get => null; set { } } }
    public class Entity
    {
        public object? this[string name] { get => null; set { } }
        public AttributeCollection Attributes { get; } = new AttributeCollection();
        public T ToEntity<T>() where T : Entity, new() => new T();
    }
}

namespace Demo
{
    public class Account : Microsoft.Xrm.Sdk.Entity
    {
        public string? Name { get; set; }
        public int Count { get; set; }
        public static Account operator ++(Account a) => new Account();
        public static Account operator --(Account a) => new Account();
    }
    public sealed class Snapshot : Microsoft.Xrm.Sdk.Entity
    {
        public string? Name { get; set; }
        public static implicit operator Snapshot(Account a) => new Snapshot { Name = a.Name };
    }
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
        T GetPreImage<T>() where T : Microsoft.Xrm.Sdk.Entity;
        Vela.Messages.IRequest Request { get; }
    }
    public interface IGeneratedContext : ILocalPluginContext { }
}

namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface ITrigger : IFluentStep { IStages OnUpdate(); }
    public interface IStages : IFluentStep { IMode PostOperation(); }
    public interface IMode : IFluentStep { IConfig Sync(); IConfig Async(); }
    public interface IConfig : IFluentStep { void Handle(Action<Vela.Context.ILocalPluginContext> handler); void Handle(Delegate handler); }
}

namespace Demo
{
    using Vela.Steps;
    public class Plugin { protected ITrigger RegisterStep() => null!; protected ITrigger Step => RegisterStep(); }
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

    private static string AsyncHandler(string body, string members = "") =>
        InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { " + body + " }\n" +
            "        " + members);

    [Theory]
    [InlineData("var t = ctx.GetTarget<Account>(); {|#0:t.Name|} = \"x\";")]
    [InlineData("var t = ctx.Request.Target; {|#0:t[\"name\"]|} = \"x\";")]
    [InlineData("var t = ctx.Request.Target; {|#0:t.Attributes[\"name\"]|} = \"x\";")]
    [InlineData("{|#0:ctx.Request.Target.Attributes[\"name\"]|} = \"x\";")]
    [InlineData("var t = ctx.Request.Target; {|#0:t.Count|}++;")]
    [InlineData("var t = ctx.Request.Target; {|#0:t.Name|} ??= \"x\";")]
    [InlineData("var a = ctx.Request.Target; var b = a; {|#0:b.Name|} = \"x\";")]
    [InlineData("Account t; t = ctx.GetTarget<Account>(); {|#0:t.Name|} = \"x\";")]
    [InlineData("var t = ctx.Request.Target; t = ctx.Request.Target; {|#0:t.Name|} = \"x\";")]
    [InlineData("if (ctx.Request.Target is Account t) { {|#0:t.Name|} = \"x\"; }")]
    [InlineData("if (ctx.Request.Target is Account { } t) { {|#0:t.Name|} = \"x\"; }")]
    [InlineData("var e = (Microsoft.Xrm.Sdk.Entity)ctx.Request.Target; {|#0:e[\"name\"]|} = \"x\";")]
    [InlineData("var e = ctx.Request.Target as Microsoft.Xrm.Sdk.Entity; {|#0:e![\"name\"]|} = \"x\";")]
    [InlineData("var t = ctx.Request.Target; System.Action a = () => { {|#0:t.Name|} = \"x\"; };")]
    public async Task Flags_a_write_through_a_local_alias_of_the_Target(string body)
    {
        await Verify.VerifyAnalyzerAsync(AsyncHandler(body), Expected());
    }

    [Theory]
    // Re-assigned to something else: the local may no longer hold the Target at the write.
    [InlineData("var t = ctx.Request.Target; t = new Account(); t.Name = \"x\";")]
    // Copies of the Target, not the Target itself.
    [InlineData("var c = new Account { Name = ctx.Request.Target.Name }; c.Name = \"x\";")]
    [InlineData("var c = ctx.Request.Target.ToEntity<Account>(); c.Name = \"x\";")]
    [InlineData("var c = ctx.Request.Target ?? new Account(); c.Name = \"x\";")]
    // Compound-assigned, deconstructed into, or passed by ref/out: not tracked.
    [InlineData("Account? t = null; t ??= ctx.Request.Target; t.Name = \"x\";")]
    [InlineData("var t = ctx.Request.Target; var o = new Account(); (t, o) = (o, t); t.Name = \"x\";")]
    [InlineData("var t = ctx.Request.Target; Swap(ref t); t.Name = \"x\";")]
    [InlineData("var t = ctx.Request.Target; ref Account r = ref t; t.Name = \"x\";")]
    // Declared another way (out var): its first value is not the Target.
    [InlineData("Make(out var t); t = ctx.Request.Target; t.Name = \"x\";")]
    // Re-assigning the local itself is not a write into the Target.
    [InlineData("var t = ctx.Request.Target; t = ctx.GetTarget<Account>();")]
    // An alias chain that never reaches the Target.
    [InlineData("var a = ctx.GetPreImage<Account>(); var b = a; b.Name = \"x\";")]
    // An implicit user-defined conversion builds another object.
    [InlineData("Snapshot s = ctx.Request.Target; s.Name = \"x\";")]
    [InlineData("Snapshot s; s = ctx.GetTarget<Account>(); s.Name = \"x\";")]
    // An overloaded ++/-- on the local itself may hand back another instance.
    [InlineData("var t = ctx.Request.Target; t++; t.Name = \"x\";")]
    [InlineData("var t = ctx.Request.Target; --t; t.Name = \"x\";")]
    public async Task Does_not_flag_a_local_that_may_not_hold_the_Target(string body)
    {
        string members =
            "private static void Swap(ref Account a) { a = new Account(); }\n" +
            "        private static void Make(out Account a) { a = new Account(); }";
        await Verify.VerifyAnalyzerAsync(AsyncHandler(body, members));
    }

    [Theory]
    [InlineData("{|#0:Mutate(ctx.Request.Target)|};", "private void Mutate(Account a) { a.Name = \"x\"; }")]
    [InlineData("var t = ctx.GetTarget<Account>(); {|#0:Mutate(t)|};", "private static void Mutate(Account a) { a[\"name\"] = \"x\"; }")]
    [InlineData("{|#0:Mutate(ctx.Request.Target)|};", "private void Mutate(Account a) { var b = a; b.Attributes[\"name\"] = \"x\"; }")]
    [InlineData("{|#0:Mutate(ctx.Request.Target)|};", "private void Mutate(Account a) => a.Count++;")]
    [InlineData("{|#0:Mutate(1, ctx.Request.Target)|};", "private void Mutate(int n, Account a) { a.Count += n; }")]
    [InlineData("{|#0:Mutate(a: ctx.Request.Target, n: 1)|};", "private void Mutate(int n, Account a) { a.Count += n; }")]
    public async Task Flags_a_helper_of_the_same_type_that_writes_the_Target_it_is_given(string body, string helper)
    {
        await Verify.VerifyAnalyzerAsync(AsyncHandler(body, helper), Expected());
    }

    [Theory]
    // The helper only reads its parameter, or writes another one.
    [InlineData("Mutate(ctx.Request.Target);", "private void Mutate(Account a) { string? n = a.Name; }")]
    [InlineData("Mutate(ctx.Request.Target, new Account());", "private void Mutate(Account a, Account b) { b.Name = a.Name; }")]
    // The helper re-assigns its parameter before the write.
    [InlineData("Mutate(ctx.Request.Target);", "private void Mutate(Account a) { a = new Account(); a.Name = \"x\"; }")]
    // The argument is not the Target.
    [InlineData("Mutate(new Account());", "private void Mutate(Account a) { a.Name = \"x\"; }")]
    // An overridable helper may be replaced by one that does not write.
    [InlineData("Mutate(ctx.Request.Target);", "protected virtual void Mutate(Account a) { a.Name = \"x\"; }")]
    // One level deep only: a helper calling another helper is not followed.
    [InlineData("Outer(ctx.Request.Target);", "private void Outer(Account a) { Inner(a); }\n        private void Inner(Account a) { a.Name = \"x\"; }")]
    // A params array, or a by-reference parameter, is not tracked.
    [InlineData("Mutate(ctx.Request.Target);", "private void Mutate(params Account[] a) { a[0].Name = \"x\"; }")]
    [InlineData("var t = ctx.Request.Target; Mutate(in t);", "private void Mutate(in Account a) { a.Name = \"x\"; }")]
    // The argument goes through an implicit user-defined conversion: the helper gets a copy.
    [InlineData("Mutate(ctx.Request.Target);", "private void Mutate(Snapshot s) { s.Name = \"x\"; }")]
    // The helper replaces its parameter through an overloaded ++/--.
    [InlineData("Mutate(ctx.Request.Target);", "private void Mutate(Account a) { a++; a.Name = \"x\"; }")]
    [InlineData("Mutate(ctx.Request.Target);", "private void Mutate(Account a) { --a; a.Name = \"x\"; }")]
    public async Task Does_not_flag_a_helper_call_that_does_not_write_the_Target(string body, string helper)
    {
        await Verify.VerifyAnalyzerAsync(AsyncHandler(body, helper));
    }

    [Fact]
    public async Task Does_not_flag_a_helper_of_another_type()
    {
        string code = Harness + @"
namespace Demo
{
    using Vela.Context;
    public static class Helpers { public static void Mutate(Account a) { a.Name = ""x""; } }
    public class Use : Plugin
    {
        public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }
        private void OnUpd(ILocalPluginContext ctx) { Helpers.Mutate(ctx.Request.Target); }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_helper_call_from_a_sync_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { var t = ctx.Request.Target; t.Name = \"x\"; Mutate(t); }\n" +
            "        private void Mutate(Account a) { a.Name = \"x\"; }");
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

    [Fact]
    public async Task Flags_a_write_through_a_parenthesized_GetTarget_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { {|#0:(ctx.GetTarget<Account>()).Name|} = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Does_not_flag_a_write_to_a_non_Vela_Target_or_GetTarget_in_an_async_step()
    {
        // A user type with its own Target property and GetTarget() helper is not the step Target.
        string code = Harness + @"
namespace Demo
{
    using Vela.Context;
    public sealed class Envelope { public Account Target { get; } = new Account(); public Account GetTarget() => Target; }
    public class Use : Plugin
    {
        private readonly Envelope _envelope = new Envelope();
        public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }
        private void OnUpd(ILocalPluginContext ctx)
        {
            _envelope.Target.Name = ""x"";
            _envelope.GetTarget().Count++;
        }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_write_to_the_pre_image_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.GetPreImage<Account>().Name = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_assigning_a_Target_value_to_a_local_in_an_async_step()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { string? name; name = ctx.Request.Target.Name; int count = 0; count++; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_sync_step_started_from_a_property()
    {
        string code = InClass(
            "public void Reg() { this.Step.OnUpdate().PostOperation().Sync().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { ctx.Request.Target.Name = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_an_async_step_started_from_a_property()
    {
        string code = InClass(
            "public void Reg() { this.Step.OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(ILocalPluginContext ctx) { {|#0:ctx.Request.Target.Name|} = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Does_not_flag_a_Target_write_in_a_lambda_registered_from_a_constructor()
    {
        // Conservative: only method-group handlers are tracked; a lambda outside any method has no handler symbol.
        string code = InClass(
            "public Use() { RegisterStep().OnUpdate().PostOperation().Async().Handle(ctx => { ctx.Request.Target.Name = \"x\"; }); }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_Vela_is_not_referenced()
    {
        string code = @"
namespace Demo
{
    public sealed class Account { public string? Name { get; set; } }
    public sealed class Request { public Account Target { get; } = new Account(); }
    public sealed class Use
    {
        public void Run(Request request) { request.Target.Name = ""x""; }
    }
}
";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_target_write_in_a_handler_bound_through_Handle_Delegate()
    {
        string code = InClass(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }\n" +
            "        private void OnUpd(IGeneratedContext ctx) { {|#0:ctx.Request.Target[\"name\"]|} = \"x\"; }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    // A plugin split across files: the RegisterStep() chain in one part of the partial class, the handler in another.
    private static async Task VerifyPartsAsync(string registration, string handler, params DiagnosticResult[] expected)
    {
        var test = new Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<Vela.Analyzers.AsyncTargetWriteAnalyzer, DefaultVerifier>();
        test.TestState.Sources.Add(("Registration.cs", Harness + @"
namespace Demo
{
    using Vela.Context;
    public partial class Use : Plugin
    {
        " + registration + @"
    }
}
"));
        test.TestState.Sources.Add(("Handlers.cs", @"
namespace Demo
{
    using Vela.Context;
    public partial class Use
    {
        " + handler + @"
    }
}
"));
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }

    [Fact]
    public async Task Flags_a_target_write_when_the_handler_is_in_another_part_of_a_partial_class()
    {
        await VerifyPartsAsync(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }",
            "private void OnUpd(ILocalPluginContext ctx) { {|#0:ctx.Request.Target.Name|} = \"x\"; }",
            Expected());
    }

    [Fact]
    public async Task Flags_a_helper_write_when_the_handler_and_helper_are_in_another_part_of_a_partial_class()
    {
        // The helper sits in the handler's file, so the same-file helper tracking applies.
        await VerifyPartsAsync(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Async().Handle(OnUpd); }",
            "private void OnUpd(ILocalPluginContext ctx) { {|#0:Rename(ctx.Request.Target)|}; }\n" +
            "        private void Rename(Account a) { a.Name = \"x\"; }",
            Expected());
    }

    [Fact]
    public async Task Does_not_flag_a_target_write_when_the_chain_in_another_part_is_synchronous()
    {
        await VerifyPartsAsync(
            "public void Reg() { RegisterStep().OnUpdate().PostOperation().Sync().Handle(OnUpd); }",
            "private void OnUpd(ILocalPluginContext ctx) { ctx.Request.Target.Name = \"x\"; }");
    }
}
