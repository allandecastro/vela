namespace Vela.Cli;

/// <summary>Project-file (.csproj) templates for the generated solution.</summary>
internal static partial class ScaffoldTemplates
{
    // ---- Shared project ---------------------------------------------------

    public static string SharedCsproj(ScaffoldOptions options) =>
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- net48 is consumed by the plugin projects; net10.0 by the ConsoleApp. -->
    <TargetFrameworks>net48;net10.0</TargetFrameworks>
    <RootNamespace>{{options.Prefix}}.Shared</RootNamespace>
    <AssemblyName>{{options.Prefix}}.Shared</AssemblyName>
  </PropertyGroup>

  <!-- net48: the plugin sandbox provides the SDK, so these reference assemblies are PrivateAssets. -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="{{CrmCoreAssembliesVersion}}" PrivateAssets="all" />
    <!-- `vela models` emits [StringLength]/[Range] DataAnnotations by default; on net48 those types live in the
         System.ComponentModel.DataAnnotations framework assembly, which is not referenced by default. -->
    <Reference Include="System.ComponentModel.DataAnnotations" />
    <!-- Pin the patched System.Security.Cryptography.Xml (a transitive SDK dependency ships an advisory version,
         NU1903). PrivateAssets so it stays compile-only and is not deployed to the plugin sandbox, which
         provides its own. -->
    <PackageReference Include="System.Security.Cryptography.Xml" Version="{{CryptographyXmlVersion}}" PrivateAssets="all" />
  </ItemGroup>

  <!-- net10.0: the cross-platform client supplies Microsoft.Xrm.Sdk for the ConsoleApp. -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Microsoft.PowerPlatform.Dataverse.Client" Version="{{DataverseClientVersion}}" />
    <!-- Pin the patched System.Security.Cryptography.Xml and .Pkcs over the client's advisory transitive versions
         (NU1902/NU1903). -->
    <PackageReference Include="System.Security.Cryptography.Xml" Version="{{CryptographyXmlVersion}}" />
    <PackageReference Include="System.Security.Cryptography.Pkcs" Version="{{CryptographyPkcsVersion}}" />
  </ItemGroup>

  <!-- The On<Message>() step extensions `vela models` writes to Messages/*.Steps.cs need Vela.Plugins: the plugin
       projects compile them, not this one. -->
  <ItemGroup>
    <Compile Remove="Models\Generated\Messages\*.Steps.cs" />
  </ItemGroup>

  <!-- Vela.Messages holds the SDK-only TypedRequest/TypedResponse base types and multi-targets net462;net10.0, so
       generated Custom API request/response contracts compile here and can be called from anywhere (plugins on
       net48, the ConsoleApp / integrations on net10.0). -->
  <ItemGroup>
    <PackageReference Include="Vela.Messages" Version="{{options.CoreVersion}}" />
  </ItemGroup>

</Project>
""";

    // ---- Plugin / Integration project file -------------------------------

    public static string PluginCsproj(string projectName, string sharedProjectName, string coreVersion, bool withSigningTarget = false, string? publisherPrefix = null)
    {
        string signingTarget = withSigningTarget ? SigningBuildTarget() : string.Empty;
        return
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- .NET Framework 4.8 is the version Microsoft supports and recommends for plug-ins (4.8.1 is not supported).
         Plugin packages above net471 register only on Dataverse 9.2.26092 or later; on an older environment, set
         net471 here until it is updated. -->
    <TargetFramework>net48</TargetFramework>
    <RootNamespace>{{projectName}}</RootNamespace>
    <AssemblyName>{{projectName}}</AssemblyName>
    <PackageId>{{projectName}}</PackageId>
    <Version>1.0.0</Version>
    <!-- Auto-increment the assembly version on every build. Dataverse caches plugin assemblies by version, so
         re-deploying changed code under the SAME version can serve a stale assembly and fail at runtime with
         "The plug-in type could not be found in the plug-in assembly". The 1.0.* wildcard needs a
         non-deterministic build. (vela apply also fails fast if you add a new plugin type without bumping this.) -->
    <Deterministic>false</Deterministic>
    <AssemblyVersion>1.0.*</AssemblyVersion>
    <!-- The 1.0.* wildcard is deliberate (see above), so silence CS7035 ("version string not in the recommended
         major.minor.build.revision format") - the compiler's nag about the wildcard form, not a real problem. -->
    <NoWarn>$(NoWarn);CS7035</NoWarn>
    <!-- The Power Platform plugin SDK bundles the assembly plus its dependent assemblies into a plugin-package
         .nupkg. That bundling runs on BUILD (it packs the publish output), so `dotnet build -c Release` produces
         the deployable package, NOT `dotnet pack` (run cold it skips the publish and emits an assembly-less
         package that vela plan rejects with "No assemblies found"). PackageOutputPath drops the package into the
         solution's artifacts folder so vela plan can read it from there. -->
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
    <PackageOutputPath>$(MSBuildProjectDirectory)\..\..\artifacts</PackageOutputPath>
    <IsWorkflowActivity>false</IsWorkflowActivity>{{PublisherPrefixProperty(publisherPrefix)}}
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="{{CrmCoreAssembliesVersion}}" PrivateAssets="all" />
    <PackageReference Include="Microsoft.PowerApps.MSBuild.Plugin" Version="{{PluginSdkVersion}}" PrivateAssets="all" />
    <PackageReference Include="Vela.Plugins" Version="{{coreVersion}}" />
    <!-- HttpClient on .NET Framework binds against this GAC facade; without it, outbound HTTP (e.g. a managed-identity
         call) fails to compile/load. Harmless when unused. -->
    <Reference Include="System.Net.Http" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\{{sharedProjectName}}\{{sharedProjectName}}.csproj" />
  </ItemGroup>

  <!-- `vela models` (messages and Custom APIs) writes each message's On<Message>() step extension to
       Messages/*.Steps.cs beside its contract. They extend the Vela.Plugins step builder, which the Shared project
       does not reference, so the plugin project compiles them. -->
  <ItemGroup>
    <Compile Include="..\{{sharedProjectName}}\Models\Generated\Messages\*.Steps.cs" Link="Models\Generated\Messages\%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- Register this assembly's early-bound entity types with the SDK serializer. Without it, passing an
       early-bound entity (e.g. new Account { ... }) to ctx.Service.Create/Update inside a plugin fails at
       runtime with a SerializationException ("The deserializer has no knowledge of any type that maps to the
       name '<namespace>:Account'"), because the sandbox cannot resolve the generated subclass. -->
  <ItemGroup>
    <AssemblyAttribute Include="Microsoft.Xrm.Sdk.Client.ProxyTypesAssemblyAttribute" />
  </ItemGroup>

  <!-- The package version is pinned to 1.0.0 (so `vela plan/apply` can reference a stable artifacts path) while
       the assembly version auto-increments (1.0.*). Because the .nupkg filename never changes, Pack's incremental
       check can keep a STALE package after a code-only change and deploy an old assembly. Delete the package
       before each build so GeneratePackageOnBuild always repacks it from the freshly built assembly. -->
  <Target Name="RefreshPluginPackage" BeforeTargets="Build">
    <PropertyGroup>
      <_PluginPackagePath>$([MSBuild]::EnsureTrailingSlash('$(PackageOutputPath)'))$(PackageId).$(Version).nupkg</_PluginPackagePath>
    </PropertyGroup>
    <Delete Files="$(_PluginPackagePath)" Condition="Exists('$(_PluginPackagePath)')" />
  </Target>
{{signingTarget}}
</Project>
""";
    }

    // Opt-in MSBuild target that NuGet-signs the packed plugin package on a developer machine by shelling
    // out to `vela sign`. Inert until a certificate source is configured, and suppressed inside CI (each CI
    // system sets its own variable) so the pipeline owns its own signing step - no double-signing.
    private static string SigningBuildTarget() =>
$$"""

  <!--
    Opt-in local package signing (a trusted signature is needed when the plugin uses a managed identity).
    Configure ONE certificate source and `dotnet build -c Release` signs automatically (this target runs
    AfterTargets="Pack", which fires during the build via GeneratePackageOnBuild); inert otherwise, and skipped
    inside CI (GITHUB_ACTIONS / TF_BUILD), which should own their own signing step.
    Requires the `vela` CLI on PATH: dotnet tool install -g Vela.Cli

      PluginSigningFingerprint : SHA-256 fingerprint of a cert in CurrentUser\My (recommended; no secret on disk)
      PluginSigningCertificate : path to a .pfx file, plus ONE of -
        PluginSigningCredential : a Windows Credential Manager entry holding the password (preferred)
        PluginSigningPassword   : the password directly (avoid persisting it)
  -->
  <Target Name="SignPluginPackage" AfterTargets="Pack"
          Condition=" ('$(PluginSigningFingerprint)' != '' or '$(PluginSigningCertificate)' != '') and '$(GITHUB_ACTIONS)' != 'true' and '$(TF_BUILD)' != 'True' ">
    <!-- EnsureTrailingSlash keeps the path correct whether or not PackageOutputPath already ends in a separator (e.g. `pack -o artifacts`). -->
    <PropertyGroup>
      <_SignPackage>$([MSBuild]::EnsureTrailingSlash('$(PackageOutputPath)'))$(PackageId).$(PackageVersion).nupkg</_SignPackage>
    </PropertyGroup>
    <!-- 1) Certificate store, by fingerprint (takes precedence). -->
    <Exec Condition=" '$(PluginSigningFingerprint)' != '' "
          Command="vela sign --package &quot;$(_SignPackage)&quot; --fingerprint &quot;$(PluginSigningFingerprint)&quot;" />
    <!-- 2) .pfx file + password from Windows Credential Manager. -->
    <Exec Condition=" '$(PluginSigningFingerprint)' == '' and '$(PluginSigningCertificate)' != '' and '$(PluginSigningCredential)' != '' "
          Command="vela sign --package &quot;$(_SignPackage)&quot; --pfx &quot;$(PluginSigningCertificate)&quot; --pfx-credential &quot;$(PluginSigningCredential)&quot;" />
    <!-- 3) .pfx file + password passed directly. The password reaches vela through an environment variable, never
         its command line (which other users can read on a shared host); Escape keeps a ';' in it intact. -->
    <Exec Condition=" '$(PluginSigningFingerprint)' == '' and '$(PluginSigningCertificate)' != '' and '$(PluginSigningCredential)' == '' "
          EnvironmentVariables="VELA_SIGN_PFX_PASSWORD=$([MSBuild]::Escape('$(PluginSigningPassword)'))"
          Command="vela sign --package &quot;$(_SignPackage)&quot; --pfx &quot;$(PluginSigningCertificate)&quot; --pfx-password-env VELA_SIGN_PFX_PASSWORD" />
  </Target>
""";

    // The publisher customization prefix recorded by `vela init --publisher-prefix`; `vela add customapi` reads it.
    // Emitted only when the prefix was given explicitly, so a derived guess is never persisted as if it were known.
    private static string PublisherPrefixProperty(string? publisherPrefix) =>
        string.IsNullOrEmpty(publisherPrefix)
            ? string.Empty
            : $"\n    <!-- Dataverse publisher prefix for Custom API unique names (read by `vela add customapi`). -->\n    <VelaPublisherPrefix>{publisherPrefix}</VelaPublisherPrefix>";

    // ---- Tests project ----------------------------------------------------

    public static string TestsCsproj(string testProjectName, string pluginsProjectName, string sharedProjectName, string coreVersion, string? publisherPrefix = null) =>
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- Plugins target net48, so their tests do too (Vela.Testing is a net462 package, consumable from net48). -->
    <TargetFramework>net48</TargetFramework>
    <RootNamespace>{{testProjectName}}</RootNamespace>
    <AssemblyName>{{testProjectName}}</AssemblyName>
    <IsPackable>false</IsPackable>{{PublisherPrefixProperty(publisherPrefix)}}
    <!-- In a solution build, a ProjectReference that MSBuild cannot match to the solution configuration (its path
         differs in form, e.g. through a symlink) is otherwise built WITHOUT Configuration/Platform, i.e. a second,
         Debug build of the plugin project running in parallel with the solution's Release one. Both pack into the
         same artifacts/<Plugin>.1.0.0.nupkg and fail with "being used by another process". Inheriting this
         project's Configuration/Platform makes it the same build, run once. -->
    <ShouldUnsetParentConfigurationAndPlatform>false</ShouldUnsetParentConfigurationAndPlatform>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{{TestSdkVersion}}" />
    <PackageReference Include="xunit" Version="{{XunitVersion}}" />
    <PackageReference Include="xunit.runner.visualstudio" Version="{{XunitRunnerVersion}}" />
    <!-- The test host runs against the real SDK assemblies (not the sandbox), so they are copied to output. -->
    <PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="{{CrmCoreAssembliesVersion}}" />
    <!-- Vela.Testing resolves from nuget.org, the same source as Vela.Plugins. -->
    <PackageReference Include="Vela.Testing" Version="{{coreVersion}}" />
    <!-- HttpClient on .NET Framework binds against this GAC facade, as in the plugin project; without it a test that
         builds an HttpClient (e.g. around a fake HttpMessageHandler) does not compile. Harmless when unused. -->
    <Reference Include="System.Net.Http" />
  </ItemGroup>

  <ItemGroup>
    <!-- Test projects live under tests/; the projects under test are under src/. -->
    <ProjectReference Include="..\..\src\{{pluginsProjectName}}\{{pluginsProjectName}}.csproj" />
    <ProjectReference Include="..\..\src\{{sharedProjectName}}\{{sharedProjectName}}.csproj" />
  </ItemGroup>

</Project>
""";

    // ---- ConsoleApp project ----------------------------------------------

    public static string ConsoleCsproj(ScaffoldOptions options) =>
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>{{options.Prefix}}.ConsoleApp</RootNamespace>
    <AssemblyName>{{options.Prefix}}.ConsoleApp</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RollForward>Major</RollForward>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.PowerPlatform.Dataverse.Client" Version="{{DataverseClientVersion}}" />
    <!-- Pin the patched System.Security.Cryptography.Xml and .Pkcs over the client's advisory transitive versions
         (NU1902/NU1903). -->
    <PackageReference Include="System.Security.Cryptography.Xml" Version="{{CryptographyXmlVersion}}" />
    <PackageReference Include="System.Security.Cryptography.Pkcs" Version="{{CryptographyPkcsVersion}}" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\{{options.Prefix}}.Shared\{{options.Prefix}}.Shared.csproj" />
  </ItemGroup>

</Project>
""";
}
