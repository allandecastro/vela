using System;
using System.ComponentModel.DataAnnotations;
using Vela.Context;
using Vela.Plugin;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class ConfigDeserializationTests
    {
        [Fact]
        public void PluginBaseOfTConfig_DeserializesAndValidates()
        {
            TestConfigPlugin plugin = new TestConfigPlugin("{\"name\":\"from-unsecure\"}", "{\"requiredValue\":\"ok\"}");
            Assert.Equal("from-unsecure", plugin.CurrentConfig.Name);
            Assert.Equal("ok", plugin.CurrentConfig.RequiredValue);
        }

        [Fact]
        public void PluginBaseOfTConfig_ThrowsOnFailedValidation()
        {
            // Secure config omits the [Required] RequiredValue → validation (not JSON parsing) must fail.
            Action act = () => new TestConfigPlugin("{\"name\":\"test\"}", "{}");
            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(act);
            Assert.Contains("validation failed", ex.Message);
        }

        [Fact]
        public void PluginBaseOfTConfig_ThrowsOnMalformedJson()
        {
            Action act = () => new TestConfigPlugin("{\"name\":", "{\"requiredValue\":\"ok\"}");
            Assert.Throws<InvalidPluginExecutionException>(act);
        }

        private sealed class TestConfigPlugin : PluginBase<TestConfig>
        {
            public TestConfigPlugin(string unsecureConfig, string secureConfig)
                : base(unsecureConfig, secureConfig)
            {
            }

            public TestConfig CurrentConfig => Configuration;

            protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
            {
            }
        }

        public sealed class TestConfig
        {
            public string? Name { get; set; }

            [Required]
            public string? RequiredValue { get; set; }
        }
    }
}
