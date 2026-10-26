using InsightFlow.Domain.Knowledge;
using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Tests.Knowledge;

public sealed class KnowledgeDefinitionsTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    private static KnowledgeTable Table(string name, string? schema = "dbo", string? description = null) =>
        KnowledgeTable.Create(Tenant, Guid.NewGuid(), schema, name, description, DefinitionSource.Database, Now);

[Theory]    [InlineData(DefinitionSource.Database, true)]    [InlineData(DefinitionSource.Dataset, true)]    [InlineData(DefinitionSource.Manual, false)]    [InlineData(DefinitionSource.Ai, false)]    public void KnowledgeTable_IsStructureLocked_OnlyForTablesThatMirrorASource(DefinitionSource source, bool locked) =>        KnowledgeTable.Create(Tenant, null, null, "T", null, source, Now).IsStructureLocked.ShouldBe(locked);    [Fact]    public void KnowledgeTable_DatabaseName_IsPartOfTheDisplayName() =>        KnowledgeTable.Create(Tenant, Guid.NewGuid(), "dbo", "Orders", null, DefinitionSource.Database, Now, "shop").DisplayName.ShouldBe("shop.dbo.Orders");
    [Fact]
    public void KnowledgeTable_DisplayName_IncludesTheSchema()
    {
        Table("Orders").DisplayName.ShouldBe("dbo.Orders");
        Table("Orders", schema: null).DisplayName.ShouldBe("Orders");
    }

    [Fact]
    public void KnowledgeTable_Create_RejectsEmptyAndOverlongNames()
    {
        Should.Throw<DomainRuleException>(() => Table("  ")).Code.ShouldBe("invalid_table_name");
        Should.Throw<DomainRuleException>(() => Table(new string('x', 201))).Code.ShouldBe("invalid_table_name");
    }

    [Fact]
    public void KnowledgeTable_SetDescription_BlankClearsAndOverlongIsRejected()
    {
        var table = Table("Orders", description: "x");

        table.SetDescription("   ", Now);
        table.Description.ShouldBeNull();

        Should.Throw<DomainRuleException>(() => table.SetDescription(new string('a', 1_001), Now)).Code.ShouldBe("invalid_description");
    }

    [Fact]
    public void KnowledgeColumn_Create_UnknownTypeDefaultsAndNameIsRequired()
    {
        var table = Table("Orders");

        KnowledgeColumn.Create(table, "id", "  ", null, true, false, 0).DataType.ShouldBe("unknown");
        Should.Throw<DomainRuleException>(() => KnowledgeColumn.Create(table, "", "int", null, false, true, 0)).Code.ShouldBe("invalid_column_name");
    }

    [Fact]
    public void KnowledgeRelationship_Create_SelfLinkIsRejected_ConfidenceClamped()
    {
        var id = Guid.NewGuid();

        Should.Throw<DomainRuleException>(() =>
            KnowledgeRelationship.Create(Tenant, id, id, RelationshipKind.ForeignKey, 1, null, DefinitionSource.Manual)).Code.ShouldBe("invalid_relationship");

        KnowledgeRelationship.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), RelationshipKind.SameAttribute, 9, new string('r', 999), DefinitionSource.Ai)
            .Confidence.ShouldBe(1);
    }

    [Fact]
    public void FromDefinitions_BuildsTablesColumnsAndNamedRelationships_WithUniqueNames()
    {
        var orders = Table("Orders");
        var customers = Table("Customers", description: "People who buy");
        var clash = KnowledgeTable.Create(Tenant, Guid.NewGuid(), "dbo", "Orders", null, DefinitionSource.Database, Now.AddMinutes(1)); // same display name, other connection
        var orderCustomer = KnowledgeColumn.Create(orders, "customer_id", "int", null, false, false, 1);
        var orderId = KnowledgeColumn.Create(orders, "id", "int", "Order number", true, false, 0);
        var customerId = KnowledgeColumn.Create(customers, "id", "int", null, true, false, 0);
        var relationship = KnowledgeRelationship.Create(Tenant, orderCustomer.Id, customerId.Id, RelationshipKind.ForeignKey, 1, "declared", DefinitionSource.Database);

        var plan = KnowledgeGraphPlanRules.FromDefinitions(
            [orders, customers, clash], [orderCustomer, orderId, customerId], [relationship]);

        plan.Tables.Select(t => t.Name).ShouldBe(["dbo.Orders", "dbo.Customers", "dbo.Orders (2)"]);
        plan.Tables[0].Columns.Select(c => c.Name).ShouldBe(["id", "customer_id"]); // ordered by ordinal
        plan.Tables[0].Columns[0].Description.ShouldBe("Order number");
        plan.Tables[0].Columns[0].IsPrimaryKey.ShouldBeTrue();
        plan.Tables[1].Description.ShouldBe("People who buy");
        plan.Relationships.Single().ShouldBe(new GraphRelationship("dbo.Orders", "customer_id", "dbo.Customers", "id", RelationshipKind.ForeignKey, 1, "declared"));
    }

    [Fact]
    public void FromDefinitions_CompositeRelationship_PairsShareAGroupAndKnowTheirSize()
    {
        var orders = Table("Orders");
        var lines = Table("Lines");
        var company = KnowledgeColumn.Create(orders, "company", "int", null, true, false, 0);
        var number = KnowledgeColumn.Create(orders, "number", "int", null, true, false, 1);
        var lineCompany = KnowledgeColumn.Create(lines, "company", "int", null, false, false, 0);
        var lineNumber = KnowledgeColumn.Create(lines, "order_no", "int", null, false, false, 1);
        var plain = KnowledgeColumn.Create(lines, "note", "text", null, false, true, 2);
        var group = Guid.NewGuid();

        var plan = KnowledgeGraphPlanRules.FromDefinitions(
            [orders, lines],
            [company, number, lineCompany, lineNumber, plain],
            [
                KnowledgeRelationship.Create(Tenant, lineCompany.Id, company.Id, RelationshipKind.ForeignKey, 1, "", DefinitionSource.Manual, group),
                KnowledgeRelationship.Create(Tenant, lineNumber.Id, number.Id, RelationshipKind.ForeignKey, 1, "", DefinitionSource.Manual, group),
                KnowledgeRelationship.Create(Tenant, plain.Id, number.Id, RelationshipKind.SameAttribute, 0.5, "", DefinitionSource.Manual),
            ]);

        plan.Relationships.Take(2).ShouldAllBe(r => r.GroupId == group && r.GroupSize == 2);
        plan.Relationships[2].GroupId.ShouldBeNull();
        plan.Relationships[2].GroupSize.ShouldBe(1);
    }

    [Fact]
    public void FromDefinitions_GroupsTablesUnderTheirDatabases_AndKeepsRelationshipsAcrossThem()
    {
        var connection = Guid.NewGuid();
        var orders = KnowledgeTable.Create(Tenant, connection, "dbo", "Orders", null, DefinitionSource.Database, Now, "Sales");
        var items = KnowledgeTable.Create(Tenant, connection, "dbo", "Items", null, DefinitionSource.Database, Now.AddMinutes(1), "Warehouse");
        var loose = KnowledgeTable.Create(Tenant, null, null, "Loose", null, DefinitionSource.Manual, Now.AddMinutes(2));
        var itemId = KnowledgeColumn.Create(orders, "item_id", "int", null, false, false, 0);
        var id = KnowledgeColumn.Create(items, "id", "int", null, true, false, 0);
        var link = KnowledgeRelationship.Create(Tenant, itemId.Id, id.Id, RelationshipKind.ForeignKey, 1, "across databases", DefinitionSource.Manual);
        var described = KnowledgeDatabase.Create(Tenant, connection, "sales", "Sales data", Now); // matched ignoring case

        var plan = KnowledgeGraphPlanRules.FromDefinitions([orders, items, loose], [itemId, id], [link], [described]);

        plan.Databases!.Select(d => d.Name).ShouldBe(["Sales", "Warehouse"]);
        plan.Databases![0].Description.ShouldBe("Sales data");
        plan.Databases![1].Description.ShouldBeNull();
        plan.Tables.Single(t => t.Id == orders.Id).DatabaseName.ShouldBe("Sales");
        plan.Tables.Single(t => t.Id == loose.Id).DatabaseName.ShouldBeNull();
        plan.Relationships.Single().ShouldSatisfyAllConditions(
            r => r.FromTable.ShouldBe("Sales.dbo.Orders"),
            r => r.ToTable.ShouldBe("Warehouse.dbo.Items"));
    }

    [Fact]
    public void KnowledgeDatabase_Create_NeedsANameAndTrimsTheDescription()
    {
        Should.Throw<DomainRuleException>(() => KnowledgeDatabase.Create(Tenant, null, "  ", null, Now)).Code.ShouldBe("invalid_database_name");
        KnowledgeDatabase.Create(Tenant, null, "Sales", "  hello ", Now).Description.ShouldBe("hello");
    }

    [Fact]
    public void FromDefinitions_RelationshipToAMissingColumn_IsLeftOut()
    {
        var orders = Table("Orders");
        var column = KnowledgeColumn.Create(orders, "id", "int", null, true, false, 0);
        var dangling = KnowledgeRelationship.Create(Tenant, column.Id, Guid.NewGuid(), RelationshipKind.ForeignKey, 1, null, DefinitionSource.Manual);

        KnowledgeGraphPlanRules.FromDefinitions([orders], [column], [dangling]).Relationships.ShouldBeEmpty();
    }
}
