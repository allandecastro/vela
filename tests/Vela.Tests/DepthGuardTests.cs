using System;
using Vela.Context;
using Vela.Plugin;
using Vela.Testing;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class DepthGuardTests
    {
        [Fact]
        public void PluginBase_StopsExecutionWhenDepthExceeded()
        {
            DepthPlugin plugin = new DepthPlugin(maxDepth: 2);
            IServiceProvider provider = new PluginContextBuilder().Depth(3).BuildServiceProvider();

            Action act = () => plugin.Execute(provider);
            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(act);
            Assert.Contains("prevent recursion", exception.Message);
        }

        [Fact]
        public void PluginBase_RunsWhenDepthEqualsMaxDepth()
        {
            // Boundary: the guard fires only when Depth EXCEEDS MaxDepth, so Depth == MaxDepth must run.
            DepthPlugin plugin = new DepthPlugin(maxDepth: 2);
            IServiceProvider provider = new PluginContextBuilder().Depth(2).BuildServiceProvider();

            plugin.Execute(provider);

            Assert.True(plugin.Ran);
        }

        private sealed class DepthPlugin : PluginBase
        {
            public DepthPlugin(int maxDepth)
                : base(string.Empty, string.Empty, maxDepth)
            {
            }

            public bool Ran { get; private set; }

            protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
            {
                Ran = true;
            }
        }
    }
}
