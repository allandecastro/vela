using System.Runtime.CompilerServices;

// The Vela.Messages request/response base types moved out of Vela.Plugins into the multi-targeted Vela.Messages
// package (so generated Custom API contracts compile on net10.0 clients as well as net462 plugins). These type
// forwarders keep Vela.Plugins binary-compatible: assemblies compiled against the old location still resolve
// these types at runtime through Vela.Plugins.dll. The types keep their original Vela.Messages namespace, so
// source stays compatible too.
[assembly: TypeForwardedTo(typeof(Vela.Messages.TypedRequest))]
[assembly: TypeForwardedTo(typeof(Vela.Messages.TypedResponse))]
[assembly: TypeForwardedTo(typeof(Vela.Messages.CreateRequest<>))]
[assembly: TypeForwardedTo(typeof(Vela.Messages.UpdateRequest<>))]
[assembly: TypeForwardedTo(typeof(Vela.Messages.SendEmailFromTemplateRequest<>))]
[assembly: TypeForwardedTo(typeof(Vela.Messages.UpsertRequest<>))]
[assembly: TypeForwardedTo(typeof(Vela.Messages.UpsertResponse))]
