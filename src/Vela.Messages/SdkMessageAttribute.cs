using System;

namespace Vela.Messages;

/// <summary>
/// Declares the sdkmessage a request runs on when it differs from the request's <c>RequestName</c>. For example,
/// <c>CloseQuote</c> is executed under that name but plugin steps register on, and see, the <c>Close</c> message.
/// A step trigger built from the request (<c>On&lt;TRequest&gt;()</c>) and the test harness register and run the
/// request's steps on this sdkmessage. <c>vela models --messages</c> emits it on a generated request whose name
/// differs from its sdkmessage.
/// </summary>
/// <param name="messageName">The sdkmessage name, for example <c>Close</c>.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SdkMessageAttribute(string messageName) : Attribute
{
    /// <summary>
    /// Gets the sdkmessage name.
    /// </summary>
    public string MessageName { get; } = string.IsNullOrWhiteSpace(messageName)
        ? throw new ArgumentNullException(nameof(messageName))
        : messageName;
}
