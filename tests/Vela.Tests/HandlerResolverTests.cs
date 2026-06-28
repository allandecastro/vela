using System.Reflection;
using Vela.Plugin;
using Xunit;

namespace Vela.Tests
{
    public class HandlerResolverTests
    {
        private class Sample
        {
            public void Handler() { }
            private void Other() { }
        }

        [Fact]
        public void GetNamedMethods_returns_methods_matching_name()
        {
            MethodInfo[] methods = HandlerResolver.GetNamedMethods(typeof(Sample), "Handler");

            Assert.Single(methods);
            Assert.Equal("Handler", methods[0].Name);
        }

        [Fact]
        public void GetNamedMethods_finds_non_public_methods()
        {
            MethodInfo[] methods = HandlerResolver.GetNamedMethods(typeof(Sample), "Other");

            Assert.Single(methods);
        }

        [Fact]
        public void GetNamedMethods_returns_empty_when_no_match()
        {
            MethodInfo[] methods = HandlerResolver.GetNamedMethods(typeof(Sample), "DoesNotExist");

            Assert.Empty(methods);
        }

        [Fact]
        public void GetNamedMethods_caches_result_across_calls()
        {
            MethodInfo[] first = HandlerResolver.GetNamedMethods(typeof(Sample), "Handler");
            MethodInfo[] second = HandlerResolver.GetNamedMethods(typeof(Sample), "Handler");

            // Same cached array instance is returned, proving the reflection is not repeated.
            Assert.Same(first, second);
        }
    }
}
