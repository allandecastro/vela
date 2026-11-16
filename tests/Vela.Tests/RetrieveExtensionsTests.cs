using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Context;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// service.Retrieve<T>(id, columns...): the typed read by id. It sends Retrieve (never RetrieveMultiple) for the
// table of T with exactly the selected columns, and returns the record early-bound.
public sealed class RetrieveExtensionsTests
{
    private static (Mock<IOrganizationService> Service, List<(string Table, Guid Id, ColumnSet Columns)> Calls) Retrieving()
    {
        List<(string, Guid, ColumnSet)> calls = new();
        Mock<IOrganizationService> service = new(MockBehavior.Strict);
        service.Setup(s => s.Retrieve(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<ColumnSet>()))
            .Returns((string table, Guid id, ColumnSet columns) =>
            {
                calls.Add((table, id, columns));
                return new Entity(table, id) { ["name"] = "Zava" };
            });
        return (service, calls);
    }

    [Fact]
    public void Sends_a_Retrieve_for_the_table_of_the_type_with_the_selected_columns()
    {
        (Mock<IOrganizationService> service, List<(string Table, Guid Id, ColumnSet Columns)> calls) = Retrieving();
        Guid id = Guid.NewGuid();

        TestAccount account = service.Object.Retrieve<TestAccount>(id, a => a.Name, a => a.Tags, a => a.AccountId);

        (string table, Guid sentId, ColumnSet columns) = Assert.Single(calls);
        Assert.Equal("account", table);
        Assert.Equal(id, sentId);
        Assert.Equal(new[] { "name", "vela_tags", "accountid" }, columns.Columns);
        Assert.False(columns.AllColumns);
        Assert.Equal(id, account.Id);
        Assert.Equal("Zava", account.Name);
    }

    [Fact]
    public void One_column_is_enough()
    {
        (Mock<IOrganizationService> service, List<(string Table, Guid Id, ColumnSet Columns)> calls) = Retrieving();

        service.Object.Retrieve<TestAccount>(Guid.NewGuid(), a => a.Name);

        Assert.Equal(new[] { "name" }, Assert.Single(calls).Columns.Columns);
    }

    [Fact]
    public void A_null_rest_array_reads_the_first_column_only()
    {
        (Mock<IOrganizationService> service, List<(string Table, Guid Id, ColumnSet Columns)> calls) = Retrieving();

        service.Object.Retrieve<TestAccount>(Guid.NewGuid(), a => a.Name, null!);

        Assert.Equal(new[] { "name" }, Assert.Single(calls).Columns.Columns);
    }

    [Fact]
    public void Rejects_missing_arguments_before_calling_the_service()
    {
        (Mock<IOrganizationService> service, List<(string Table, Guid Id, ColumnSet Columns)> calls) = Retrieving();
        Guid id = Guid.NewGuid();

        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => ((IOrganizationService)null!).Retrieve<TestAccount>(id, a => a.Name)).ParamName);
        Assert.Equal("id", Assert.Throws<ArgumentException>(() => service.Object.Retrieve<TestAccount>(Guid.Empty, a => a.Name)).ParamName);
        Assert.Equal("column", Assert.Throws<ArgumentNullException>(() => service.Object.Retrieve<TestAccount>(id, null!)).ParamName);
        Assert.Empty(calls);
    }

    [Fact]
    public void Rejects_a_selector_that_is_not_a_column()
    {
        (Mock<IOrganizationService> service, List<(string Table, Guid Id, ColumnSet Columns)> calls) = Retrieving();
        Guid id = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => service.Object.Retrieve<RelAccount>(id, a => a.Name, a => a.PrimaryContact));
        Assert.Throws<ArgumentException>(() => service.Object.Retrieve<TestAccount>(id, a => a.LogicalName));
        Assert.Empty(calls);
    }
}
