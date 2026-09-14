using Vela.Messages;
using Xunit;

namespace Vela.Tests
{
    // Guards TypedRequest/TypedResponse.Get<T>: a missing, null, or wrong-typed parameter must return default(T)
    // rather than throwing InvalidCastException, matching the safe accessor pattern used elsewhere.
    public sealed class TypedRequestGetSafetyTests
    {
        private sealed class SampleRequest : TypedRequest
        {
            public int Count { get => Get<int>(); set => Set(value); }

            public string? Label { get => Get<string?>(); set => Set(value); }
        }

        [Fact]
        public void Get_ReturnsDefault_WhenParameterMissing()
        {
            SampleRequest request = new SampleRequest();

            Assert.Equal(0, request.Count); // value type -> default, no unbox-of-null throw
            Assert.Null(request.Label);     // reference type -> null
        }

        [Fact]
        public void Get_ReturnsDefault_WhenStoredValueTypeDiffers()
        {
            SampleRequest request = new SampleRequest();
            request.Parameters["Count"] = "not-an-int"; // wrong type in the bag

            Assert.Equal(0, request.Count); // mismatched type -> default, not InvalidCastException
        }
    }
}
