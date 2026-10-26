using System.Text.Json;
using InsightFlow.Contracts;
using InsightFlow.Contracts.Playground;
using InsightFlow.Domain.Playground;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Api.Playground;

/// <summary>
/// The Playground: each person's own sheets of free-form canvas (text, shapes, charts, tables). Reads and writes only the caller's sheets in the
/// caller's tenant; whatever a browser sends is checked and tidied (<see cref="PlaygroundElementRules"/>) before it is stored. Charts and tables
/// from Query and the Workspace arrive through <see cref="ExportAsync"/>.
/// </summary>
public sealed partial class PlaygroundService(InsightFlowDbContext db, ITenantContext caller, TimeProvider clock, ILogger<PlaygroundService> logger)
{
    private TenantId Tenant => new(caller.TenantId ?? throw new InvalidOperationException("No tenant in scope."));

    private string Owner => (caller.UserId ?? throw new InvalidOperationException("No user in scope.")).Trim().ToLowerInvariant();

    private IQueryable<PlaygroundSheet> Mine => db.PlaygroundSheets.Where(s => s.OwnerId == Owner);

    public async Task<IReadOnlyList<PlaygroundSheetSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var sheets = await Mine.AsNoTracking().OrderBy(s => s.Sequence).ThenBy(s => s.CreatedAt).Select(s => new { s.Id, s.Name, s.Sequence, s.ElementCount, s.UpdatedAt }).ToListAsync(cancellationToken);
        return [.. sheets.Select(s => new PlaygroundSheetSummaryDto(s.Id, s.Name, s.Sequence, s.ElementCount, s.UpdatedAt))];
    }

    public async Task<PlaygroundSheetDto> GetAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        var sheet = await Mine.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw ApiProblemException.NotFound("The sheet");
        return ToDto(sheet);
    }

    public async Task<PlaygroundSheetDto> CreateAsync(CreatePlaygroundSheetRequest request, CancellationToken cancellationToken)
    {
        var sheet = await AddSheetAsync(request.Name, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        LogSaved(logger, sheet.Id, 0);
        return ToDto(sheet);
    }

    public async Task<PlaygroundSheetDto> SaveAsync(Guid sheetId, SavePlaygroundSheetRequest request, CancellationToken cancellationToken)
    {
        var sheet = await Mine.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw ApiProblemException.NotFound("The sheet");
        var now = clock.GetUtcNow();
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if ((await Mine.Where(s => s.Id != sheetId).Select(s => s.Name).ToListAsync(cancellationToken)).Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw ApiProblemException.Conflict($"You already have a sheet named “{name}”.");
            }

            sheet.Rename(name, now);
        }

        if (request.Elements is not null)
        {
            var elements = PlaygroundElementRules.Sanitize(request.Elements);
            sheet.SetElements(Serialize(elements), elements.Count, now);
        }

        if (request.Sequence is { } sequence)
        {
            await MoveAsync(sheet, sequence, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        LogSaved(logger, sheet.Id, sheet.ElementCount);
        return ToDto(sheet);
    }

    public async Task DeleteAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        var sheet = await Mine.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw ApiProblemException.NotFound("The sheet");
        db.PlaygroundSheets.Remove(sheet);
        await db.SaveChangesAsync(cancellationToken);
        var rest = await Mine.OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        for (var i = 0; i < rest.Count; i++)
        {
            rest[i].MoveTo(i, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlaygroundSheetDto> DuplicateAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        var source = await Mine.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw ApiProblemException.NotFound("The sheet");
        var copy = await AddSheetAsync(null, cancellationToken, preferredName: $"{source.Name} (copy)");
        var elements = PlaygroundElementRules.Sanitize(Deserialize(source.ElementsJson).Select(e => e with { Id = Guid.Empty }).ToList());
        copy.SetElements(Serialize(elements), elements.Count, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(copy);
    }

    /// <summary>
    /// Puts a chart or table made in Query or the Workspace on a sheet. With no sheet named, the first sheet is used or, when the person has none,
    /// one is created. The element lands in the first free space below what is already there.
    /// </summary>
    public async Task<ExportToPlaygroundResult> ExportAsync(ExportToPlaygroundRequest request, CancellationToken cancellationToken)
    {
        if (request.Element is null || request.Element.Type is not ("chart" or "table"))
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "invalid_element", "Only a chart or a table can be exported.");
        }

        PlaygroundSheet sheet;
        var created = false;
        if (!string.IsNullOrWhiteSpace(request.NewSheetName))
        {
            sheet = await AddSheetAsync(request.NewSheetName, cancellationToken);
            created = true;
        }
        else if (request.SheetId is { } id)
        {
            sheet = await Mine.FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw ApiProblemException.NotFound("The sheet");
        }
        else
        {
            var first = await Mine.OrderBy(s => s.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (first is null)
            {
                sheet = await AddSheetAsync(null, cancellationToken);
                created = true;
            }
            else
            {
                sheet = first;
            }
        }

        var existing = Deserialize(sheet.ElementsJson).ToList();
        var bottom = existing.Count == 0 ? 0 : existing.Max(e => e.Y + e.H);
        var top = existing.Count == 0 ? 24 : bottom + 24;
        var z = existing.Count == 0 ? 1 : existing.Max(e => e.Z) + 1;
        var placed = request.Element with { Id = Guid.Empty, X = 24, Y = top, Z = z };
        existing.AddRange(PlaygroundElementRules.Sanitize([placed]));
        sheet.SetElements(Serialize(existing), existing.Count, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogSaved(logger, sheet.Id, sheet.ElementCount);
        return new ExportToPlaygroundResult(sheet.Id, sheet.Name, created);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<PlaygroundSheet> AddSheetAsync(string? requestedName, CancellationToken cancellationToken, string? preferredName = null)
    {
        var existing = await Mine.Select(s => new { s.Name, s.Sequence }).ToListAsync(cancellationToken);
        var names = existing.Select(s => s.Name).ToList();
        string name;
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            name = requestedName.Trim();
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw ApiProblemException.Conflict($"You already have a sheet named “{name}”.");
            }
        }
        else
        {
            var baseName = preferredName ?? "Sheet";
            name = preferredName is not null ? baseName : $"{baseName} {existing.Count + 1}";
            for (var i = 2; names.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
            {
                name = preferredName is not null ? $"{baseName} {i}" : $"{baseName} {existing.Count + i}";
            }
        }

        var sheet = PlaygroundSheet.Create(Tenant, Owner, name.Length > PlaygroundSheet.MaxNameLength ? name[..PlaygroundSheet.MaxNameLength] : name, existing.Count == 0 ? 0 : existing.Max(s => s.Sequence) + 1, clock.GetUtcNow());
        db.PlaygroundSheets.Add(sheet);
        return sheet;
    }

    /// <summary>Moves a sheet to a position among the owner's sheets and renumbers the rest.</summary>
    private async Task MoveAsync(PlaygroundSheet sheet, int sequence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var all = await Mine.OrderBy(s => s.Sequence).ThenBy(s => s.CreatedAt).ToListAsync(cancellationToken);
        all.RemoveAll(s => s.Id == sheet.Id);
        all.Insert(Math.Clamp(sequence, 0, all.Count), sheet);
        for (var i = 0; i < all.Count; i++)
        {
            all[i].MoveTo(i, now);
        }
    }

    private static string Serialize(IReadOnlyList<PlaygroundElementDto> elements) =>
        JsonSerializer.Serialize(elements, ContractsJsonContext.Default.IReadOnlyListPlaygroundElementDto);

    private static IReadOnlyList<PlaygroundElementDto> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.IReadOnlyListPlaygroundElementDto) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static PlaygroundSheetDto ToDto(PlaygroundSheet sheet) => new(sheet.Id, sheet.Name, sheet.Sequence, Deserialize(sheet.ElementsJson), sheet.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Playground sheet {SheetId} saved with {Elements} element(s)")]
    private static partial void LogSaved(ILogger logger, Guid sheetId, int elements);
}
