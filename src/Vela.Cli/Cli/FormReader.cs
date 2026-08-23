using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>A published <c>systemform</c> row: its id, name, owning table, form type and raw formxml.</summary>
public sealed record SystemForm(Guid FormId, string Name, string ObjectTypeCode, string FormType, string FormXml);

/// <summary>
/// Reads published <c>systemform</c> rows for a set of tables over the same Dataverse connection as models/plan.
/// Scopes to Main (type 2) and Quick Create (type 6) forms in the given tables, active state only (no unpublished
/// draft forms). Pure reader: no metadata resolution and no generation. Deterministic order (table, type, name).
/// </summary>
internal static class FormReader
{
    // systemform.type option-set values we generate models for.
    private const int MainFormType = 2;
    private const int QuickCreateFormType = 6;

    // systemform.formactivationstate: 1 = Active (published). Draft/inactive forms are skipped.
    private const int ActiveState = 1;

    public static IReadOnlyList<SystemForm> ReadForms(IOrganizationService service, IReadOnlyCollection<string> entityLogicalNames)
    {
        if (entityLogicalNames == null || entityLogicalNames.Count == 0)
        {
            return Array.Empty<SystemForm>();
        }

        QueryExpression query = new QueryExpression("systemform")
        {
            ColumnSet = new ColumnSet("formid", "name", "objecttypecode", "type", "formxml"),
        };
        query.Criteria.AddCondition("objecttypecode", ConditionOperator.In, entityLogicalNames.Cast<object>().ToArray());
        query.Criteria.AddCondition("type", ConditionOperator.In, MainFormType, QuickCreateFormType);
        query.Criteria.AddCondition("formactivationstate", ConditionOperator.Equal, ActiveState);

        return service.RetrieveAll(query)
            .Select(Map)
            .Where(f => f != null)
            .Select(f => f!)
            .OrderBy(f => f.ObjectTypeCode, StringComparer.Ordinal)
            .ThenBy(f => f.FormType, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    // Maps the systemform.type option-set value to the FormModel FormType literal, or null for a type we don't emit.
    public static string? MapFormType(int? typeCode) => typeCode switch
    {
        MainFormType => "Main",
        QuickCreateFormType => "QuickCreate",
        _ => null,
    };

    private static SystemForm? Map(Entity form)
    {
        string? formType = MapFormType(form.GetAttributeValue<OptionSetValue>("type")?.Value);
        string formXml = form.GetAttributeValue<string>("formxml") ?? string.Empty;
        string objectTypeCode = form.GetAttributeValue<string>("objecttypecode") ?? string.Empty;
        if (formType == null || string.IsNullOrWhiteSpace(formXml) || string.IsNullOrWhiteSpace(objectTypeCode))
        {
            return null;
        }

        return new SystemForm(
            form.Id,
            form.GetAttributeValue<string>("name") ?? string.Empty,
            objectTypeCode,
            formType,
            formXml);
    }
}
