using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using InsightFlow.Contracts;
using InsightFlow.Contracts.Connections;
using InsightFlow.Contracts.Query;
using InsightFlow.Contracts.Workspace;
using InsightFlow.Domain.Viz;
using InsightFlow.ServiceDefaults.Security;
using InsightFlow.Testing;

namespace InsightFlow.IntegrationTests;

/// <summary>
/// Workspace explorer over HTTP against the full AppHost: folder tree rules, dropped-folder uploads, permissions across
/// roles/users/tenants, connections, and the vertical slice "upload CSV → Create dataset → Worker extract → chart".
/// Each test uses a fresh random tenant (provisioned on first access) so tests never share data.
/// </summary>
public sealed class WorkspaceApiTests(AppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContractsJsonContext Json => ContractsJsonContext.Default;

    private sealed record Caller(Guid Tenant, string User, string Role)
    {
        public string Token => new DevToken(User, Tenant, [Role]).ToHeaderValue();

        public static Caller NewTenant(string role = InsightFlowRoles.Creator) => new(Guid.NewGuid(), "alice", role);
    }

    private HttpClient Client(string resource, Caller caller)
    {
        var client = fixture.CreateHttpClient(resource);
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(caller.Token);
        return client;
    }

    private async Task<T> GetAsync<T>(Caller caller, string path, JsonTypeInfo<T> type)
    {
        using var client = Client("api", caller);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync(type, Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync<T>(Caller caller, HttpMethod method, string path, T body, JsonTypeInfo<T> type)
    {
        using var client = Client("api", caller);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, type) };
        return await client.SendAsync(request, Ct);
    }

    private async Task<FolderDto> CreateFolderAsync(Caller caller, Guid parentId, string name)
    {
        using var response = await SendAsync(caller, HttpMethod.Post, "/api/v1/workspace/folders", new CreateFolderRequest(parentId, name), Json.CreateFolderRequest);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync(Json.FolderDto, Ct))!;
    }

    private async Task<UploadResponse> UploadAsync(Caller caller, Guid folderId, params (string Path, byte[] Bytes)[] files)
    {
        using var client = Client("api", caller);
        using var content = new MultipartFormDataContent();
        foreach (var (path, bytes) in files)
        {
            content.Add(new StringContent(path, Encoding.UTF8), "path");
            content.Add(new ByteArrayContent(bytes), "file", Path.GetFileName(path));
        }

        using var response = await client.PostAsync(new Uri($"/api/v1/workspace/folders/{folderId}/files", UriKind.Relative), content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync(Json.UploadResponse, Ct))!;
    }

    private Task<FolderContentsResponse> ContentsAsync(Caller caller, Guid folderId) =>
        GetAsync(caller, $"/api/v1/workspace/folders/{folderId}/children", Json.FolderContentsResponse);

    private Task<WorkspaceRootsResponse> RootsAsync(Caller caller) =>
        GetAsync(caller, "/api/v1/workspace/roots", Json.WorkspaceRootsResponse);

    [Fact]
    public async Task Roots_NewTenant_AreProvisionedOnFirstAccess()
    {
        fixture.RequireRunning();
        var caller = Caller.NewTenant();

        var roots = await RootsAsync(caller);
        var again = await RootsAsync(caller);

        roots.MyWorkspace.Scope.ShouldBe("Personal");
        roots.Shared.Scope.ShouldBe("Shared");
        roots.Shared.CanWrite.ShouldBeTrue();
        again.ShouldBe(roots);
    }

    [Fact]
    public async Task FolderTree_CreateRenameMoveDelete_FollowsRules()
    {
        fixture.RequireRunning();
        var caller = Caller.NewTenant();
        var root = (await RootsAsync(caller)).MyWorkspace;

        var reports = await CreateFolderAsync(caller, root.Id, "Reports");
        var q3 = await CreateFolderAsync(caller, reports.Id, "Q3");

        using (var duplicate = await SendAsync(caller, HttpMethod.Post, "/api/v1/workspace/folders", new CreateFolderRequest(root.Id, "REPORTS"), Json.CreateFolderRequest))
        {
            duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var cycle = await SendAsync(caller, HttpMethod.Post, "/api/v1/workspace/move", new MoveRequest([reports.Id], [], q3.Id), Json.MoveRequest))
        {
            cycle.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await cycle.Content.ReadAsStringAsync(Ct)).ShouldContain("move_into_descendant");
        }

        using (var badName = await SendAsync(caller, HttpMethod.Post, "/api/v1/workspace/folders", new CreateFolderRequest(root.Id, "../etc"), Json.CreateFolderRequest))
        {
            badName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var rename = await SendAsync(caller, HttpMethod.Patch, $"/api/v1/workspace/folders/{q3.Id}", new RenameRequest("Q3 2026"), Json.RenameRequest))
        {
            rename.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var move = await SendAsync(caller, HttpMethod.Post, "/api/v1/workspace/move", new MoveRequest([q3.Id], [], root.Id), Json.MoveRequest))
        {
            move.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var client = Client("api", caller))
        using (var delete = await client.DeleteAsync(new Uri($"/api/v1/workspace/folders/{reports.Id}", UriKind.Relative), Ct))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var contents = await ContentsAsync(caller, root.Id);
        contents.Folders.Select(f => f.Name).ShouldBe(["Q3 2026"]);
    }

    [Fact]
    public async Task Upload_DroppedFolder_PreservesStructure_ResolvesConflicts_RejectsUnsafe()
    {
        fixture.RequireRunning();
        var caller = Caller.NewTenant();
        var root = (await RootsAsync(caller)).MyWorkspace;
        var csv = Encoding.UTF8.GetBytes("a,b\n1,2\n");

        var result = await UploadAsync(caller, root.Id,
            ("Q3/eu/sales.csv", csv),
            ("Q3/eu/sales.csv", csv),
            ("Q3/readme.md", Encoding.UTF8.GetBytes("# notes")),
            ("Q3/../escape.csv", csv),
            ("Q3/tool.exe", [0x4D, 0x5A]));

        result.Files.Select(f => f.Success).ShouldBe([true, true, true, false, false]);
        result.Files[1].Name.ShouldBe("sales (2).csv");

        var q3 = (await ContentsAsync(caller, root.Id)).Folders.ShouldHaveSingleItem();
        q3.Name.ShouldBe("Q3");
        var q3Contents = await ContentsAsync(caller, q3.Id);
        q3Contents.Folders.Select(f => f.Name).ShouldBe(["eu"]);
        q3Contents.Items.Select(i => i.Name).ShouldBe(["readme.md"]);
        var eu = await ContentsAsync(caller, q3Contents.Folders[0].Id);
        eu.Items.Select(i => i.Name).ShouldBe(["sales (2).csv", "sales.csv"], ignoreOrder: true);
        eu.Items.ShouldAllBe(i => i.CanCreateDataset && i.SizeBytes == csv.Length);
        eu.Path.Select(p => p.Name).ShouldBe(["My Workspace", "Q3", "eu"]);

        using var client = Client("api", caller);
        var downloaded = await client.GetByteArrayAsync(new Uri($"/api/v1/workspace/items/{eu.Items[0].Id}/content", UriKind.Relative), Ct);
        downloaded.ShouldBe(csv);
    }

    [Fact]
    public async Task Permissions_ViewerCannotWriteShared_OthersCannotSeePersonal_TenantsAreIsolated()
    {
        fixture.RequireRunning();
        var alice = Caller.NewTenant();
        var roots = await RootsAsync(alice);
        var private1 = await CreateFolderAsync(alice, roots.MyWorkspace.Id, "Private");
        var shared1 = await CreateFolderAsync(alice, roots.Shared.Id, "Team");

        var viewer = alice with { User = "victor", Role = InsightFlowRoles.Viewer };
        using (var viewerWrite = await SendAsync(viewer, HttpMethod.Post, "/api/v1/workspace/folders", new CreateFolderRequest(shared1.Id, "X"), Json.CreateFolderRequest))
        {
            viewerWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await ContentsAsync(viewer, shared1.Id)).Folder.CanWrite.ShouldBeFalse();

        var bob = alice with { User = "bob" };
        using (var client = Client("api", bob))
        using (var peek = await client.GetAsync(new Uri($"/api/v1/workspace/folders/{private1.Id}/children", UriKind.Relative), Ct))
        {
            peek.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var outsider = Caller.NewTenant(InsightFlowRoles.TenantAdmin);
        using (var client = Client("api", outsider))
        using (var cross = await client.GetAsync(new Uri($"/api/v1/workspace/folders/{shared1.Id}/children", UriKind.Relative), Ct))
        {
            cross.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task CreateDataset_FromUploadedCsv_WorkerExtracts_AndQueryServiceCharts()
    {
        fixture.RequireRunning();
        var caller = Caller.NewTenant();
        var root = (await RootsAsync(caller)).MyWorkspace;
        var csvPath = Path.Combine(Path.GetTempPath(), $"insightflow-it-{Guid.NewGuid():N}.csv");
        await RetailDataGenerator.WriteDenormalizedAsync(csvPath, 1_000, csv: true, Ct);
        var upload = await UploadAsync(caller, root.Id, ("retail_sales.csv", await File.ReadAllBytesAsync(csvPath, Ct)));
        File.Delete(csvPath);
        var fileItemId = upload.Files.ShouldHaveSingleItem().ItemId!.Value;

        using var queued = await SendAsync(caller, HttpMethod.Post, $"/api/v1/workspace/items/{fileItemId}/dataset", new RenameRequest(string.Empty), Json.RenameRequest);
        queued.StatusCode.ShouldBe(HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync(Ct));
        var runId = (await queued.Content.ReadFromJsonAsync(Json.ExtractQueuedResponse, Ct))!.RunId;

        var run = await WaitForRunAsync(caller, runId);
        run.Status.ShouldBe("Succeeded", run.Error);
        run.RowCount.ShouldBe(1_000);

        var items = (await ContentsAsync(caller, root.Id)).Items;
        var dataset = items.Single(i => i.Kind == "Dataset");
        dataset.Name.ShouldBe("retail_sales");
        dataset.TargetId.ShouldBe(run.DatasetVersionId!.Value);

        var spec = new VizSpec(1, dataset.TargetId, Mark.Bar, new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)), []);
        using var query = Client("queryservice", caller);
        using var chart = await query.PostAsJsonAsync(new Uri("/api/v1/query/viz", UriKind.Relative), new VizQueryRequest(spec), Json.VizQueryRequest, Ct);
        chart.StatusCode.ShouldBe(HttpStatusCode.OK, await chart.Content.ReadAsStringAsync(Ct));
        (await chart.Content.ReadFromJsonAsync(Json.VizQueryResponse, Ct))!.Result.Rows.Count.ShouldBe(RetailDataGenerator.Regions.Length);
    }

    [Fact]
    public async Task CreateDataset_FromNonTabularFile_IsRejected_AndExcelIsNotImplementedYet()
    {
        fixture.RequireRunning();
        var caller = Caller.NewTenant();
        var root = (await RootsAsync(caller)).MyWorkspace;
        var upload = await UploadAsync(caller, root.Id, ("notes.md", Encoding.UTF8.GetBytes("x")), ("book.xlsx", [0x50, 0x4B]));

        using var md = await SendAsync(caller, HttpMethod.Post, $"/api/v1/workspace/items/{upload.Files[0].ItemId}/dataset", new RenameRequest(string.Empty), Json.RenameRequest);
        using var xlsx = await SendAsync(caller, HttpMethod.Post, $"/api/v1/workspace/items/{upload.Files[1].ItemId}/dataset", new RenameRequest(string.Empty), Json.RenameRequest);

        md.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        xlsx.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Connections_SecretIsNeverReturned_StubsAreNotImplemented_UnreachableTestFailsGracefully()
    {
        fixture.RequireRunning();
        var caller = Caller.NewTenant();
        await RootsAsync(caller); // provisions the tenant row

        var request = new CreateConnectionRequest("Sales DB", "SqlServer",
            new Dictionary<string, string> { ["server"] = "tcp:127.0.0.1,1", ["database"] = "sales", ["user"] = "reader" }, "Sup3r-S3cret!");
        using var created = await SendAsync(caller, HttpMethod.Post, "/api/v1/connections", request, Json.CreateConnectionRequest);
        var body = await created.Content.ReadAsStringAsync(Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        body.ShouldNotContain("Sup3r-S3cret!");
        var connection = (await created.Content.ReadFromJsonAsync(Json.ConnectionDto, Ct))!;
        connection.HasSecret.ShouldBeTrue();

        using var test = await SendAsync(caller, HttpMethod.Post, $"/api/v1/connections/{connection.Id}/test", new RenameRequest(string.Empty), Json.RenameRequest);
        test.StatusCode.ShouldBe(HttpStatusCode.OK);
        var testResult = (await test.Content.ReadFromJsonAsync(Json.ConnectionTestResponse, Ct))!;
        testResult.Success.ShouldBeFalse();
        testResult.Message.ShouldNotContain("Sup3r-S3cret!");

        using var mongo = await SendAsync(caller, HttpMethod.Post, "/api/v1/connections",
            request with { Kind = "MongoDb" }, Json.CreateConnectionRequest);
        mongo.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);

        var viewer = caller with { Role = InsightFlowRoles.Viewer };
        using var forbidden = await SendAsync(viewer, HttpMethod.Post, "/api/v1/connections", request, Json.CreateConnectionRequest);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DevelopmentSampleData_IsSeededIntoContosoSharedFolder()
    {
        fixture.RequireRunning();
        var contoso = new Caller(DevelopmentIdentity.TenantId, DevelopmentIdentity.UserId, InsightFlowRoles.Creator);
        var shared = (await RootsAsync(contoso)).Shared;
        var sample = (await ContentsAsync(contoso, shared.Id)).Folders.Single(f => f.Name == "Sample Data");

        var deadline = DateTime.UtcNow.AddMinutes(3);
        IReadOnlyList<ContentItemDto> items;
        do
        {
            items = (await ContentsAsync(contoso, sample.Id)).Items;
            if (items.Any(i => i.Kind == "Dataset"))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }
        while (DateTime.UtcNow < deadline);

        items.ShouldContain(i => i.Kind == "File" && i.Name == "retail_sales.csv");
        items.ShouldContain(i => i.Kind == "Dataset" && i.Name == "Retail sales");
    }

    private async Task<ExtractRunDto> WaitForRunAsync(Caller caller, Guid runId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            var run = await GetAsync(caller, $"/api/v1/extract-runs/{runId}", Json.ExtractRunDto);
            if (run.Status is "Succeeded" or "Failed" || DateTime.UtcNow > deadline)
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }
    }
}
