// The samples of the localization guide (docs/guides/localization.md). Edit the regions, not the page.
#region registrar
[assembly: Vela.DI.VelaServiceRegistrar(typeof(Vela.DocSamples.ProjectServices))]
#endregion

namespace Vela.DocSamples
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.DI;
    using Vela.DocSamples.Models;
    using Vela.Localization;
    using Vela.Plugin;
    using Vela.Testing;
    using Xunit;

    #region messages
    // The project's messages: the keys, and the one source that holds their texts.
    public static class ProjectMessages
    {
        public const string NameRequired = "ERR_NAME_REQUIRED";

        // Built once and only read afterwards, so every execution can share it.
        public static readonly IMessageSource Source = new DictionaryMessageSource()
            .Add(1033, NameRequired, "The account name is required.")
            .Add(1036, NameRequired, "Le nom du compte est obligatoire.");
    }

    // Runs for every plugin and Custom API of the assembly, before each one's ConfigureServices.
    public sealed class ProjectServices : IServiceRegistrar
    {
        public void Register(SimpleServiceResolver services, ILocalPluginContext context) =>
            context.GetRequiredService<IMessageService>().AddSource(ProjectMessages.Source);
    }
    #endregion

    #region plugin
    public sealed partial class AccountNamePlugin(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    if (string.IsNullOrWhiteSpace(ctx.Request.Target.Name))
                    {
                        throw ctx.BusinessError(ProjectMessages.NameRequired);
                    }
                });
    }
    #endregion

    public sealed class AccountNamePluginTests
    {
        #region test
        [Fact]
        public void Rejects_An_Empty_Name_In_The_Users_Language()
        {
            // The message service reads the initiating user's UI language through the system service.
            Guid user = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(user)
                .Seed(new Entity("usersettings", Guid.NewGuid()) { ["systemuserid"] = user, ["uilanguageid"] = 1036 })
                .Register<AccountNamePlugin>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava" });

            InvalidPluginExecutionException error = Assert.ThrowsAny<InvalidPluginExecutionException>(() =>
                pipeline.Service.Update(new Account { Id = id, Name = "" }));

            Assert.Equal("Le nom du compte est obligatoire.", error.Message);
        }
        #endregion
    }
}
