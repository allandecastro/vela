using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.ParallelExecutionAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class ParallelExecutionAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { void Execute(System.IServiceProvider sp); } }
namespace Vela.Plugin { public abstract class EntityPluginBase<T> { } }
";

    private static DiagnosticResult Expected(int location, string member, string shape) =>
        new DiagnosticResult(ParallelExecutionAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(location)
            .WithArguments(member, shape);

    [Fact]
    public async Task Flags_work_started_on_another_thread()
    {
        string code = Harness + @"
namespace Demo
{
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(System.IServiceProvider sp)
        {
            {|#0:Task.Run(() => { })|};
            {|#1:Task.Factory.StartNew(() => { })|};
            {|#2:Parallel.For(0, 3, i => { })|};
            {|#3:Parallel.ForEach(new[] { 1 }, i => { })|};
            {|#4:Parallel.Invoke(() => { })|};
            {|#5:ThreadPool.QueueUserWorkItem(_ => { })|};
            var t = {|#6:new Thread(() => { })|};
            var sum = {|#7:new[] { 1 }.AsParallel()|}.Sum();
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(
            code,
            Expected(0, "Task.Run", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(1, "TaskFactory.StartNew", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(2, "Parallel.For", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(3, "Parallel.ForEach", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(4, "Parallel.Invoke", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(5, "ThreadPool.QueueUserWorkItem", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(6, "new Thread", ParallelExecutionAnalyzer.StartsParallelWork),
            Expected(7, "ParallelEnumerable.AsParallel", ParallelExecutionAnalyzer.StartsParallelWork));
    }

    [Fact]
    public async Task Flags_blocking_on_a_task()
    {
        string code = Harness + @"
namespace Demo
{
    using System.Threading.Tasks;
    using Vela.Plugin;
    public sealed class Account { }
    public sealed class MyPlugin : EntityPluginBase<Account>
    {
        private void Handle(Task<int> pending, Task other)
        {
            int a = {|#0:pending.Result|};
            {|#1:other.Wait()|};
            {|#2:Task.WaitAll(other)|};
            {|#3:Task.WaitAny(other)|};
            int b = {|#4:pending.GetAwaiter().GetResult()|};
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(
            code,
            Expected(0, "Task.Result", ParallelExecutionAnalyzer.BlocksOnTask),
            Expected(1, "Task.Wait", ParallelExecutionAnalyzer.BlocksOnTask),
            Expected(2, "Task.WaitAll", ParallelExecutionAnalyzer.BlocksOnTask),
            Expected(3, "Task.WaitAny", ParallelExecutionAnalyzer.BlocksOnTask),
            Expected(4, "TaskAwaiter.GetResult", ParallelExecutionAnalyzer.BlocksOnTask));
    }

    [Fact]
    public async Task Flags_inside_a_lambda_of_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(IServiceProvider sp)
        {
            Action start = () => {|#0:Task.Run(() => { })|};
            start();
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected(0, "Task.Run", ParallelExecutionAnalyzer.StartsParallelWork));
    }

    [Fact]
    public async Task Does_not_flag_await_completed_tasks_or_sequential_code()
    {
        // await, Task.FromResult / CompletedTask and a sequential LINQ query never leave the calling thread or block.
        string code = Harness + @"
namespace Demo
{
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(System.IServiceProvider sp) { }
        private async Task<int> LoadAsync(Task<int> pending)
        {
            await Task.CompletedTask;
            int n = await pending;
            return n + await Task.FromResult(new[] { 1, 2 }.Select(i => i * 2).Sum());
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_code_outside_a_plugin_type()
    {
        // Scoped like VELA019: a test or tool in the same solution, and a type nested in a plugin, are left alone.
        string code = Harness + @"
namespace Demo
{
    using System.Threading.Tasks;
    using Microsoft.Xrm.Sdk;
    public class PluginTests
    {
        public void Run() { Task.Run(() => { }).Wait(); Parallel.For(0, 3, i => { }); }
    }
    public class MyPlugin : IPlugin
    {
        public void Execute(System.IServiceProvider sp) { }
        private sealed class Helper { public int Get(Task<int> t) => t.Result; }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_look_alike_types_in_other_namespaces()
    {
        string code = Harness + @"
namespace Other
{
    public static class Task { public static void Run(System.Action a) { } }
    public static class Parallel { public static void For(int a, int b, System.Action<int> body) { } }
    public sealed class Thread { public Thread(System.Action a) { } }
}
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public void Execute(System.IServiceProvider sp)
        {
            Other.Task.Run(() => { });
            Other.Parallel.For(0, 1, i => { });
            var t = new Other.Thread(() => { });
        }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_neither_the_SDK_nor_Vela_is_referenced()
    {
        string code = @"
namespace Demo
{
    using System.Threading.Tasks;
    public class Worker { public void Run() { Task.Run(() => { }).Wait(); } }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
