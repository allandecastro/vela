using System;

namespace Vela.Constants;

/// <summary>
/// Declares a strongly-typed SDK message name for <c>OnMessage&lt;TMessage&gt;</c>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CustomMessageAttribute"/> class.
/// </remarks>
/// <param name="messageName">The SDK message name.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CustomMessageAttribute(string messageName) : Attribute
{
    /// <summary>
    /// Gets the SDK message name.
    /// </summary>
    public string MessageName { get; } = string.IsNullOrWhiteSpace(messageName)
        ? throw new ArgumentNullException(nameof(messageName))
        : messageName;
}
