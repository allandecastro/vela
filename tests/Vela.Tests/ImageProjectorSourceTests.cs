using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.Steps;
using Xunit;

namespace Vela.Tests
{
    public sealed class ImageProjectorSourceTests
    {
        // Stand-in for a generated projection: the mapped column plus the IImageProjection marker the generator
        // emits so the explicit (TEntity) cast is lossless.
        private sealed class NameProjection : IImageProjection
        {
            public Entity? Source { get; set; }

            [AttributeLogicalName("name")]
            public string? Name { get; set; }
        }

        [Fact]
        public void Project_retains_the_source_image_on_an_IImageProjection()
        {
            Entity image = new Entity("account") { Id = Guid.NewGuid() };
            image["name"] = "Zava";

            NameProjection? projection = ImageProjector.Project<NameProjection>(image);

            Assert.NotNull(projection);
            Assert.Equal("Zava", projection!.Name);
            // The whole image is retained (same reference), so the generated cast rebuilds the full early-bound
            // entity - Id and logical name included - rather than a reconstruction limited to the mapped columns.
            Assert.Same(image, ((IImageProjection)projection).Source);
        }

        [Fact]
        public void Project_returns_null_and_sets_no_source_for_an_absent_image()
        {
            Assert.Null(ImageProjector.Project<NameProjection>(null));
        }
    }
}
