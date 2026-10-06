using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Vela.CustomApi;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The type name each builder emits for a Custom API parameter is the contract `vela apply` registers from, so it must
// name a real Custom API type: string[] as a String collection (StringArray), an early-bound entity as Entity, and any
// type with no exact Dataverse equivalent rejected when the step is built (never registered as a lookalike).
public sealed class CustomApiParameterTypesTests
{
    private enum Color { Red }

    [Theory]
    [InlineData(typeof(string[]), "System.String", true)]
    [InlineData(typeof(TestAccount), "Microsoft.Xrm.Sdk.Entity", false)]
    [InlineData(typeof(Entity), "Microsoft.Xrm.Sdk.Entity", false)]
    [InlineData(typeof(int?), "System.Int32", false)]
    [InlineData(typeof(double), "System.Double", false)]
    [InlineData(typeof(OptionSetValue), "Microsoft.Xrm.Sdk.OptionSetValue", false)]
    [InlineData(typeof(EntityCollection), "Microsoft.Xrm.Sdk.EntityCollection", false)]
    [InlineData(typeof(string), "System.String", false)]
    public void Supported_types_map_to_their_registered_name(Type clrType, string typeName, bool isCollection)
    {
        Assert.Equal((typeName, isCollection), CustomApiParameterTypes.Describe(clrType, "P"));
    }

    [Theory]
    [InlineData(typeof(long), "use int")]
    [InlineData(typeof(float), "use double")]
    [InlineData(typeof(Color), "OptionSetValue")]
    [InlineData(typeof(byte[]), "Use one of")]
    [InlineData(typeof(List<string>), "Use one of")] // Dataverse sends string[]; a List<string> would read as null
    public void Unsupported_types_fail_with_the_alternative(Type clrType, string hint)
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => CustomApiParameterTypes.Describe(clrType, "Count"));

        Assert.Contains("'Count'", ex.Message);
        Assert.Contains(hint, ex.Message);
    }
}
