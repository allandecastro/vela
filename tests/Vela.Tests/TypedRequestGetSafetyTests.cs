using Vela.Messages;
using Xunit;

namespace Vela.Tests
{
    // Guards TypedRequest/TypedResponse.Get<T>: a missing or null parameter returns default(T), a base Entity converts
    // to the early-bound type, and a wrong-typed value throws (it used to read as default(T), which hid a property
    // declared with a type Dataverse never sends, e.g. long for an Integer parameter).
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
        public void Get_ThrowsAnInvalidPluginExecutionException_WhenStoredValueTypeDiffers()
        {
            SampleRequest request = new SampleRequest();
            request.Parameters["Count"] = "not-an-int"; // wrong type in the bag

            Microsoft.Xrm.Sdk.InvalidPluginExecutionException ex = Assert.Throws<Microsoft.Xrm.Sdk.InvalidPluginExecutionException>(() => request.Count);
            Assert.Contains("'Count'", ex.Message);
        }

        private sealed class EntityRequest : TypedRequest
        {
            public Vela.Tests.TestSupport.TestAccount? Account { get => Get<Vela.Tests.TestSupport.TestAccount?>(); set => Set(value); }
        }

        [Fact]
        public void Get_ConvertsABaseEntityToTheEarlyBoundType()
        {
            EntityRequest request = new EntityRequest();
            request.Parameters["Account"] = new Microsoft.Xrm.Sdk.Entity("account") { ["name"] = "Zava" }; // as Dataverse sends it

            Assert.Equal("Zava", request.Account!.Name);
        }

        private sealed class EntityResponse : TypedResponse
        {
            public Vela.Tests.TestSupport.TestAccount? Account => Get<Vela.Tests.TestSupport.TestAccount?>();

            public Microsoft.Xrm.Sdk.Entity? Raw => Get<Microsoft.Xrm.Sdk.Entity?>();
        }

        [Fact]
        public void Get_ConvertsABaseEntityOnEveryRead_AndKeepsABaseEntityDeclaredAsSuch()
        {
            EntityResponse response = new EntityResponse();
            response.Results["Account"] = new Microsoft.Xrm.Sdk.Entity("account") { ["name"] = "First" };
            response.Results["Raw"] = new Microsoft.Xrm.Sdk.Entity("contact");

            Assert.Equal("First", response.Account!.Name);
            response.Results["Account"] = new Microsoft.Xrm.Sdk.Entity("account") { ["name"] = "Second" };
            Assert.Equal("Second", response.Account!.Name); // the cached converter serves the next read
            Assert.Equal("contact", response.Raw!.LogicalName);
        }
    }
}
