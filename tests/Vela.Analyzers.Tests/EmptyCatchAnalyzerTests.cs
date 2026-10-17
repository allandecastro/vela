using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.EmptyCatchAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class EmptyCatchAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk
{
    public interface IPlugin { }
    public interface IOrganizationService { void Delete(string entity, System.Guid id); }
    public sealed class OrganizationServiceFault { }
}
namespace System.ServiceModel { public class FaultException<T> : System.Exception { } }
namespace Demo
{
    using System;
    using System.ServiceModel;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private IOrganizationService _service;
        public void Execute(IServiceProvider sp)
        {
            BODY
        }
    }
    public class Helper
    {
        public void Run()
        {
            try { } catch { }
        }
    }
}";

    private static string Code(string body) => Harness.Replace("BODY", body);

    private static DiagnosticResult Expected(int location = 0) =>
        new DiagnosticResult(EmptyCatchAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(location);

    [Fact]
    public async Task Flags_a_bare_empty_catch()
    {
        await Verify.VerifyAnalyzerAsync(Code("try { _service.Delete(\"account\", Guid.Empty); } {|#0:catch|} { }"), Expected());
    }

    [Fact]
    public async Task Flags_an_empty_typed_catch_and_underlines_its_declaration()
    {
        await Verify.VerifyAnalyzerAsync(Code("try { _service.Delete(\"account\", Guid.Empty); } {|#0:catch (Exception)|} { }"), Expected());
    }

    [Fact]
    public async Task Flags_an_empty_catch_of_an_organization_service_fault_with_a_variable()
    {
        await Verify.VerifyAnalyzerAsync(
            Code("try { _service.Delete(\"account\", Guid.Empty); } {|#0:catch (FaultException<OrganizationServiceFault> ex)|}\n{\n}"),
            Expected());
    }

    [Fact]
    public async Task Flags_an_empty_catch_with_a_when_filter()
    {
        await Verify.VerifyAnalyzerAsync(
            Code("try { _service.Delete(\"account\", Guid.Empty); } {|#0:catch (Exception ex) when (ex.Message.Length > 0)|} { }"),
            Expected());
    }

    [Fact]
    public async Task Flags_each_empty_clause_of_one_try()
    {
        await Verify.VerifyAnalyzerAsync(
            Code("try { } {|#0:catch (InvalidOperationException)|} { } catch (ArgumentException) { throw; } {|#1:catch|} { }"),
            Expected(0), Expected(1));
    }

    [Fact]
    public async Task Flags_an_empty_catch_in_a_lambda_and_a_local_function()
    {
        await Verify.VerifyAnalyzerAsync(
            Code("Action a = () => { try { } {|#0:catch|} { } }; void Local() { try { } {|#1:catch (Exception)|} { } } a(); Local();"),
            Expected(0), Expected(1));
    }

    [Theory]
    [InlineData("try { } catch { throw; }")]
    [InlineData("try { } catch (Exception ex) { throw new InvalidOperationException(ex.Message, ex); }")]
    [InlineData("try { } catch (Exception ex) { Console.WriteLine(ex); }")]
    [InlineData("try { } catch { return; }")]
    [InlineData("try { } catch { ; }")]
    [InlineData("try { } catch { /* the record may already be gone */ }")]
    [InlineData("try { } catch (Exception)\n{\n    // a missing record is fine here\n}")]
    [InlineData("try { } catch\n{\n#if DEBUG\n    throw;\n#endif\n}")]
    public async Task Does_not_flag_a_catch_with_a_statement_or_a_comment(string body)
    {
        await Verify.VerifyAnalyzerAsync(Code(body));
    }

    [Fact]
    public async Task Does_not_flag_a_nested_helper_type_inside_a_plugin()
    {
        const string Code = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Demo
{
    public class MyPlugin : Microsoft.Xrm.Sdk.IPlugin
    {
        public void Execute(System.IServiceProvider sp) { }
        private sealed class Nested { public void Run() { try { } catch { } } }
    }
}";
        await Verify.VerifyAnalyzerAsync(Code);
    }

    [Fact]
    public async Task Flags_a_plugin_deriving_from_a_Vela_base()
    {
        const string Code = @"
namespace Vela.Plugin { public abstract class PluginBase { } }
namespace Demo
{
    public class MyPlugin : Vela.Plugin.PluginBase
    {
        public void Run() { try { } {|#0:catch|} { } }
    }
}";
        await Verify.VerifyAnalyzerAsync(Code, Expected());
    }

    [Fact]
    public async Task Does_nothing_without_the_Dataverse_SDK_or_Vela()
    {
        const string Code = @"
namespace Demo
{
    public class Tool { public void Run() { try { } catch { } } }
}";
        await Verify.VerifyAnalyzerAsync(Code);
    }
}
