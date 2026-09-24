using System;
using System.Linq;
using static Vela.Cli.CommandLineOptions;

namespace Vela.Cli;

/// <summary>
/// The parsed scope of a <c>vela models</c> run: what to generate, taken from the combinable
/// <c>--solution</c> / <c>--entities</c> / <c>--custom-apis</c> / <c>--messages</c> / <c>--roles</c> /
/// <c>--solution-roles</c> options. Pure parsing with no console/exit side effects, so <see cref="ModelsCommand"/>
/// keeps the validation and warnings; split out so the scope-reading is one place.
/// </summary>
internal readonly record struct ModelsScope(
    string? Solution,
    string[] Entities,
    string[] CustomApis,
    bool AllCustomApis,
    string[] Messages,
    bool AllMessages,
    string[] Roles,
    bool SolutionRoles)
{
    // True when no scope at all was requested: refusing this (rather than generating every table) is the caller's job.
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Solution) && Entities.Length == 0 && CustomApis.Length == 0 && !AllCustomApis
        && Messages.Length == 0 && !AllMessages && Roles.Length == 0 && !SolutionRoles;

    // True when a Custom-API / message / role scope is present. The TypeScript (--forms/--tables) and --verify paths
    // read tables only, so they use this to warn that these scopes are ignored.
    public bool HasCustomApiMessageOrRoleScope =>
        CustomApis.Length > 0 || AllCustomApis || Messages.Length > 0 || AllMessages || Roles.Length > 0 || SolutionRoles;

    public static ModelsScope Parse(string[] args)
    {
        string? solution = TryReadOption(args, "--solution");
        string[] entities = (TryReadOption(args, "--entities") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            // Dataverse entity logical names are lower-case; normalize so `--entities Account` resolves the same as
            // `account` (B7 - matches how add form-script/ribbon-script already lower-case the entity name).
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .ToArray();

        // --custom-apis mirrors --messages: absent, targeted (a list), or bare (present with no value = all).
        bool customApisRequested = HasFlag(args, "--custom-apis");
        string[] customApis = (TryReadOptionValue(args, "--custom-apis") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Bare `--custom-apis` (present, no explicit list) = generate every Custom API in the environment.
        bool allCustomApis = customApisRequested && customApis.Length == 0;

        // --messages has three states: absent, present-with-a-list (targeted), and present-with-no-value (all).
        // HasFlag detects presence; TryReadOptionValue reads the list only when the next token is a real value
        // (not another option), so a bare `--messages` (all mode) doesn't swallow the following flag as its value.
        bool messagesRequested = HasFlag(args, "--messages");
        string[] messages = (TryReadOptionValue(args, "--messages") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Bare `--messages` (present, but no explicit list) = generate all consumable messages, filtered.
        bool allMessages = messagesRequested && messages.Length == 0;

        string[] roles = (TryReadOption(args, "--roles") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // --solution-roles pulls every security role in --solution (like --solution does for tables).
        bool solutionRoles = HasFlag(args, "--solution-roles");

        return new ModelsScope(solution, entities, customApis, allCustomApis, messages, allMessages, roles, solutionRoles);
    }
}
