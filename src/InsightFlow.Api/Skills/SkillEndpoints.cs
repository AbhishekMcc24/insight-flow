using InsightFlow.Domain.Contracts.Skills;
using InsightFlow.Foundation.Security;

namespace InsightFlow.Api.Skills;

/// <summary><c>/api/v1/skills</c>: organization-level skill categories and skills with versions. Sys-admin only.</summary>
internal static class SkillEndpoints
{
    public static RouteGroupBuilder MapSkillEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization(InsightFlowPolicies.CanAdministerTenant);

        group.MapGet("/categories", (SkillService skills, CancellationToken ct) => skills.ListCategoriesAsync(ct)).WithName("ListSkillCategories");
        group.MapPost("/categories", async (SaveSkillCategoryRequest request, SkillService skills, CancellationToken ct) =>
        {
            var category = await skills.CreateCategoryAsync(request, ct);
            return TypedResults.Created($"/api/v1/skills/categories/{category.Id}", category);
        }).WithName("CreateSkillCategory");
        group.MapPut("/categories/{categoryId:guid}", (Guid categoryId, SaveSkillCategoryRequest request, SkillService skills, CancellationToken ct) =>
            skills.UpdateCategoryAsync(categoryId, request, ct)).WithName("UpdateSkillCategory");
        group.MapDelete("/categories/{categoryId:guid}", async (Guid categoryId, SkillService skills, CancellationToken ct) =>
        {
            await skills.DeleteCategoryAsync(categoryId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteSkillCategory");

        group.MapPost("/suggestions/apply", (ApplySkillSuggestionsRequest request, SkillService skills, CancellationToken ct) =>
            skills.ApplySuggestionsAsync(request, ct)).WithName("ApplySkillSuggestions");

        group.MapGet("/", (SkillService skills, CancellationToken ct) => skills.ListSkillsAsync(ct)).WithName("ListSkills");
        group.MapGet("/{skillId:guid}", (Guid skillId, SkillService skills, CancellationToken ct) => skills.GetSkillAsync(skillId, ct)).WithName("GetSkill");
        group.MapPost("/", async (SaveSkillRequest request, SkillService skills, CancellationToken ct) =>
        {
            var skill = await skills.CreateSkillAsync(request, ct);
            return TypedResults.Created($"/api/v1/skills/{skill.Id}", skill);
        }).WithName("CreateSkill");
        group.MapPut("/{skillId:guid}", (Guid skillId, SaveSkillRequest request, SkillService skills, CancellationToken ct) =>
            skills.SaveSkillAsync(skillId, request, ct)).WithName("SaveSkill");
        group.MapPost("/{skillId:guid}/enabled", (Guid skillId, SetSkillEnabledRequest request, SkillService skills, CancellationToken ct) =>
            skills.SetEnabledAsync(skillId, request.Enabled, ct)).WithName("SetSkillEnabled");
        group.MapPost("/{skillId:guid}/versions/{versionId:guid}/activate", (Guid skillId, Guid versionId, SkillService skills, CancellationToken ct) =>
            skills.ActivateVersionAsync(skillId, versionId, ct)).WithName("ActivateSkillVersion");
        group.MapPost("/{skillId:guid}/duplicate", async (Guid skillId, SkillService skills, CancellationToken ct) =>
        {
            var copy = await skills.DuplicateAsync(skillId, ct);
            return TypedResults.Created($"/api/v1/skills/{copy.Id}", copy);
        }).WithName("DuplicateSkill");
        group.MapDelete("/{skillId:guid}", async (Guid skillId, SkillService skills, CancellationToken ct) =>
        {
            await skills.DeleteSkillAsync(skillId, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteSkill");

        return group;
    }
}
