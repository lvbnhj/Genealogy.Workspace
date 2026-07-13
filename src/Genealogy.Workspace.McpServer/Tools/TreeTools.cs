using System.ComponentModel;
using System.Text.Json;
using Genealogy.Workspace.Data.Models;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Traversal;
using ModelContextProtocol.Server;

namespace Genealogy.Workspace.McpServer.Tools;

/// <summary>
/// Tools for browsing genealogy tree datasets in the workspace: tree
/// management, person lookup/search, family context, life events, and
/// ancestor/descendant/common-ancestor/path traversal. Every tree-scoped tool
/// resolves its <c>tree</c> parameter via <see cref="TreeResolver"/> first
/// (an unresolved tree returns <c>{ error }</c>) and every person-scoped tool
/// then resolves its name-or-GUID parameter via <see cref="PersonResolver"/>
/// within that tree (a multi-match returns <c>{ error, candidates }</c>,
/// never a silent pick; a not-found returns <c>{ error }</c>). No DNA
/// terminology or DNA-linked fields anywhere — this is the product-neutral
/// genealogy workspace server.
/// </summary>
[McpServerToolType]
public sealed class TreeTools(
    TreeRepository treeRepository,
    PersonRepository personRepository,
    PersonSearchRepository personSearchRepository,
    RichFamilyContextRepository familyContextRepository,
    PersonEventsRepository personEventsRepository,
    TreeTraversalRepository traversalRepository,
    TreeResolver treeResolver,
    PersonResolver personResolver)
{
    [McpServerTool(Name = "list_tree_datasets")]
    [Description("Lists every genealogy tree dataset in the workspace, ordered by name.")]
    public async Task<string> ListTreeDatasetsAsync()
    {
        try
        {
            var trees = await treeRepository.ListTreesAsync();

            var result = new
            {
                trees = trees.Select(t => new
                {
                    treeId = t.TreeId,
                    name = t.Name,
                    description = t.Description,
                    isDefault = t.IsDefault,
                    rootPersonId = t.RootPersonId,
                    createdAt = t.CreatedAt
                })
            };

            return JsonSerializer.Serialize(result, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[list_tree_datasets] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "create_tree_dataset")]
    [Description("Creates a new genealogy tree dataset. Tree names must be unique; at most one tree in the workspace may be marked as default.")]
    public async Task<string> CreateTreeDatasetAsync(
        [Description("Unique tree name, e.g. 'Paternal line' or 'Smith family'")] string name,
        [Description("Optional human-readable description")] string? description = null,
        [Description("Make this tree the default tree used by calls that omit tree")] bool isDefault = false)
    {
        try
        {
            var created = await treeRepository.CreateTreeAsync(name, description, isDefault);

            return JsonSerializer.Serialize(new
            {
                tree = new
                {
                    treeId = created.TreeId,
                    name = created.Name,
                    description = created.Description,
                    isDefault = created.IsDefault,
                    rootPersonId = created.RootPersonId,
                    createdAt = created.CreatedAt,
                }
            }, McpJson.Options);
        }
        catch (DuplicateTreeNameException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (DefaultTreeConflictException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[create_tree_dataset] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_tree_person")]
    [Description("Looks up a single person in a genealogy tree by name or GUID, including their primary name details.")]
    public async Task<string> GetTreePersonAsync(
        [Description("Person name or person GUID")] string person,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, person);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var record = await personRepository.GetPersonAsync(treeRes.TreeId, personRes.PersonId);
            if (record is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"person '{personRes.PersonId}' not found in tree" }, McpJson.Options);
            }

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                person = new
                {
                    personId = record.PersonId,
                    fullName = record.PrimaryDisplayName,
                    sex = record.Sex,
                    isLiving = record.IsLiving,
                    externalId = record.ExternalId,
                    primaryName = record.PrimaryName is null ? null : new
                    {
                        given = record.PrimaryName.Given,
                        surname = record.PrimaryName.Surname,
                        fullName = record.PrimaryName.FullName,
                        fullNameNormalized = record.PrimaryName.FullNameNormalized,
                        scriptCode = record.PrimaryName.ScriptCode,
                        nameType = record.PrimaryName.NameType,
                        isPrimary = record.PrimaryName.IsPrimary,
                    },
                },
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_tree_person] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "find_tree_person")]
    [Description("Searches for a person in a genealogy tree by name with optional constraints: father, mother, child, spouse, place, and birth year range. All name parameters use substring matching. Returns up to maxResults matches.")]
    public async Task<string> FindTreePersonAsync(
        [Description("Person's name or partial name to search for (required)")] string name,
        [Description("Father's name or partial name")] string? father = null,
        [Description("Mother's name or partial name")] string? mother = null,
        [Description("Child's name or partial name")] string? child = null,
        [Description("Spouse's name or partial name")] string? spouse = null,
        [Description("Place name (birth, death, or any life event place)")] string? place = null,
        [Description("Minimum birth year")] int? yearFrom = null,
        [Description("Maximum birth year")] int? yearTo = null,
        [Description("Maximum number of results to return (default 20)")] int maxResults = 20,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var rows = await personSearchRepository.FindTreePersonAsync(
                treeRes.TreeId, name, father, mother, child, spouse, place, yearFrom, yearTo, maxResults);

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                query = new { name, father, mother, child, spouse, place, yearFrom, yearTo },
                count = rows.Count,
                people = rows.Select(r => new
                {
                    personId = r.PersonId,
                    fullName = r.FullName,
                    sex = r.Sex,
                    isLiving = r.IsLiving,
                    birthYear = r.BirthYear,
                    birthPlace = r.BirthPlace,
                    deathYear = r.DeathYear,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[find_tree_person] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_person_family_context")]
    [Description("Returns the full family context for a person in a genealogy tree: their life events, parents, siblings, marriages (with dates/places), and children.")]
    public async Task<string> GetPersonFamilyContextAsync(
        [Description("Person name or GUID")] string person,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, person);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var context = await familyContextRepository.GetRichFamilyContextAsync(treeRes.TreeId, personRes.PersonId);
            if (context is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"person '{personRes.PersonId}' not found in tree" }, McpJson.Options);
            }

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                person = new
                {
                    personId = context.PersonId,
                    fullName = context.PrimaryDisplayName,
                    sex = context.Sex,
                    isLiving = context.IsLiving,
                },
                events = context.LifeEvents.Select(MapEvent),
                parents = context.Parents.Select(p => new
                {
                    personId = p.PersonId,
                    fullName = p.PrimaryDisplayName,
                    sex = p.Sex,
                    isLiving = p.IsLiving,
                    birthYear = p.BirthYear,
                    birthPlace = p.BirthPlace,
                    deathYear = p.DeathYear,
                    deathPlace = p.DeathPlace,
                }),
                siblings = context.Siblings.Select(s => new
                {
                    personId = s.PersonId,
                    fullName = s.PrimaryDisplayName,
                    sex = s.Sex,
                    isLiving = s.IsLiving,
                    birthYear = s.BirthYear,
                    birthPlace = s.BirthPlace,
                    deathYear = s.DeathYear,
                }),
                marriages = context.Marriages.Select(m => new
                {
                    familyId = m.FamilyId,
                    spousePersonId = m.SpousePersonId,
                    spouseName = m.SpouseName,
                    spouseSex = m.SpouseSex,
                    spouseIsLiving = m.SpouseIsLiving,
                    spouseBirthYear = m.SpouseBirthYear,
                    spouseBirthPlace = m.SpouseBirthPlace,
                    spouseDeathYear = m.SpouseDeathYear,
                    marriageDateRaw = m.MarriageDateRaw,
                    marriageYear = m.MarriageYear,
                    marriagePlaceRaw = m.MarriagePlaceRaw,
                }),
                children = context.Children.Select(c => new
                {
                    personId = c.PersonId,
                    fullName = c.PrimaryDisplayName,
                    sex = c.Sex,
                    isLiving = c.IsLiving,
                    birthYear = c.BirthYear,
                    birthDateRaw = c.BirthDateRaw,
                    birthPlace = c.BirthPlace,
                    deathYear = c.DeathYear,
                    otherParentName = c.OtherParentName,
                    otherParentSex = c.OtherParentSex,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_person_family_context] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_person_life_events")]
    [Description("Returns all life events for a person in a genealogy tree, in chronological order. Preserves the original DateRaw and PlaceRaw text verbatim — useful for identifying which archive/record series to search next.")]
    public async Task<string> GetPersonLifeEventsAsync(
        [Description("Person name or GUID")] string person,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, person);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var lifeEvents = await personEventsRepository.GetLifeEventsAsync(treeRes.TreeId, personRes.PersonId);
            if (lifeEvents is null)
            {
                return JsonSerializer.Serialize(
                    new { error = $"person '{personRes.PersonId}' not found in tree" }, McpJson.Options);
            }

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                person = new
                {
                    personId = lifeEvents.Header.PersonId,
                    fullName = lifeEvents.Header.FullName,
                    sex = lifeEvents.Header.Sex,
                    isLiving = lifeEvents.Header.IsLiving,
                },
                eventCount = lifeEvents.Events.Count,
                events = lifeEvents.Events.Select(MapEvent),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_person_life_events] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_ancestors")]
    [Description("Returns ancestors of a person in a genealogy tree up to N generations back. Each ancestor includes birth/death years and places.")]
    public async Task<string> GetAncestorsAsync(
        [Description("Person name or GUID")] string person,
        [Description("Maximum generations back to traverse (default 6)")] int maxGenerations = 6,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, person);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var rows = await traversalRepository.GetAncestorsAsync(treeRes.TreeId, personRes.PersonId, maxGenerations);

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                person,
                maxGenerations,
                count = rows.Count,
                ancestors = rows.Select(a => new
                {
                    personId = a.PersonId,
                    fullName = a.FullName,
                    sex = a.Sex,
                    isLiving = a.IsLiving,
                    generation = a.Generation,
                    birthYear = a.BirthYear,
                    birthDateRaw = a.BirthDateRaw,
                    birthPlace = a.BirthPlace,
                    deathYear = a.DeathYear,
                    deathPlace = a.DeathPlace,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_ancestors] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_descendants")]
    [Description("Returns all descendants of a person in a genealogy tree up to N generations.")]
    public async Task<string> GetDescendantsAsync(
        [Description("Ancestor name or GUID")] string ancestor,
        [Description("Maximum generations to traverse (default 8)")] int maxGenerations = 8,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, ancestor);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var rows = await traversalRepository.GetDescendantsAsync(treeRes.TreeId, personRes.PersonId, maxGenerations);

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                ancestor,
                maxGenerations,
                count = rows.Count,
                descendants = rows.Select(d => new
                {
                    personId = d.PersonId,
                    fullName = d.PrimaryDisplayName,
                    sex = d.Sex,
                    isLiving = d.IsLiving,
                    generation = d.Generation,
                    parentPersonId = d.ParentPersonId,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_descendants] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_descendants_by_year")]
    [Description("Returns all descendants of a person in a genealogy tree who were alive at a given year. Married descendants also have their spouses listed as separate rows (personType = SPOUSE).")]
    public async Task<string> GetDescendantsByYearAsync(
        [Description("Ancestor name or GUID")] string ancestor,
        [Description("Reference year — only people alive at this year are returned")] int year,
        [Description("Maximum generations to traverse (default 8)")] int maxGenerations = 8,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, ancestor);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var rows = await traversalRepository.GetDescendantsAtYearAsync(
                treeRes.TreeId, personRes.PersonId, year, maxGenerations);

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                ancestor,
                year,
                maxGenerations,
                count = rows.Count,
                people = rows.Select(p => new
                {
                    personId = p.PersonId,
                    fullName = p.FullName,
                    sex = p.Sex,
                    personType = p.PersonType,
                    generation = p.Generation,
                    parentPersonId = p.ParentPersonId,
                    spouseOfPersonId = p.SpouseOfPersonId,
                    birthYear = p.BirthYear,
                    deathYear = p.DeathYear,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_descendants_by_year] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_closest_common_ancestor")]
    [Description("Finds the closest common ancestor of two (or three) people in a genealogy tree. Returns the ancestor and each person's depth to that ancestor.")]
    public async Task<string> GetClosestCommonAncestorAsync(
        [Description("First person's name or GUID")] string name1,
        [Description("Second person's name or GUID")] string name2,
        [Description("Optional third person's name or GUID")] string? name3 = null,
        [Description("Maximum ancestor depth to search (default 12)")] int maxDepth = 12,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var res1 = await personResolver.ResolvePersonAsync(treeRes.TreeId, name1);
            if (!res1.IsResolved) return PersonErrorJson(res1);

            var res2 = await personResolver.ResolvePersonAsync(treeRes.TreeId, name2);
            if (!res2.IsResolved) return PersonErrorJson(res2);

            PersonResolution? res3 = null;
            if (!string.IsNullOrWhiteSpace(name3))
            {
                res3 = await personResolver.ResolvePersonAsync(treeRes.TreeId, name3);
                if (!res3.IsResolved) return PersonErrorJson(res3);
            }

            var ids = new List<Guid> { res1.PersonId, res2.PersonId };
            if (res3 is not null)
            {
                ids.Add(res3.PersonId);
            }

            var result = await traversalRepository.GetClosestCommonAncestorAsync(treeRes.TreeId, ids, maxDepth);

            var inputs = new List<object>
            {
                new { name = name1, resolvedId = res1.PersonId, resolvedName = res1.FullName },
                new { name = name2, resolvedId = res2.PersonId, resolvedName = res2.FullName },
            };
            if (res3 is not null)
            {
                inputs.Add(new { name = name3, resolvedId = res3.PersonId, resolvedName = res3.FullName });
            }

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                inputs,
                ancestor = result.Ancestor is null ? null : new
                {
                    ancestorId = result.Ancestor.AncestorId,
                    ancestorName = result.Ancestor.AncestorName,
                    maxDepth = result.Ancestor.MaxDepth,
                    sumDepth = result.Ancestor.SumDepth,
                    personCount = result.Ancestor.PersonCount,
                },
                personDepths = result.InputDepths.Select(d => new
                {
                    personId = d.PersonId,
                    personName = d.PersonName,
                    depth = d.Depth,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_closest_common_ancestor] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_person_tree")]
    [Description("Returns a traversal tree rooted at one person in a genealogy tree. direction='ancestors' walks upward to parents; direction='descendants' walks downward to children. Nodes include edge metadata so the caller can reconstruct the tree.")]
    public async Task<string> GetPersonTreeAsync(
        [Description("Person name or GUID to use as the root of the traversal")] string person,
        [Description("Traversal direction: ancestors/up or descendants/down")] string direction = "ancestors",
        [Description("Maximum number of generations to traverse (default 6, capped at 50)")] int maxGenerations = 6,
        [Description("Include the root person as generation 0")] bool includeRoot = true,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var personRes = await personResolver.ResolvePersonAsync(treeRes.TreeId, person);
            if (!personRes.IsResolved) return PersonErrorJson(personRes);

            var rows = await traversalRepository.GetPersonTreeAsync(
                treeRes.TreeId, personRes.PersonId, direction, maxGenerations, includeRoot);

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                rootPerson = new { personId = personRes.PersonId, fullName = personRes.FullName },
                direction,
                maxGenerations,
                count = rows.Count,
                nodes = rows.Select(n => new
                {
                    personId = n.PersonId,
                    fullName = n.FullName,
                    sex = n.Sex,
                    isLiving = n.IsLiving,
                    generation = n.Generation,
                    edgeFromPersonId = n.EdgeFromPersonId,
                    edgeFromName = n.EdgeFromName,
                    edgeToPersonId = n.EdgeToPersonId,
                    edgeToName = n.EdgeToName,
                    path = n.Path,
                    birthYear = n.BirthYear,
                    birthDateRaw = n.BirthDateRaw,
                    birthPlace = n.BirthPlace,
                    deathYear = n.DeathYear,
                    deathDateRaw = n.DeathDateRaw,
                    deathPlace = n.DeathPlace,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_person_tree] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    [McpServerTool(Name = "get_path_between_persons")]
    [Description("Finds the shortest relationship path between two people in a genealogy tree, exploring parent/child edges in both directions. Returns an empty steps list when the two people are not connected within maxDepth hops.")]
    public async Task<string> GetPathBetweenPersonsAsync(
        [Description("First person's name or GUID")] string person1,
        [Description("Second person's name or GUID")] string person2,
        [Description("Maximum number of hops to search (default 20)")] int maxDepth = 20,
        [Description("Tree name or GUID. Defaults to the workspace's default tree.")] string? tree = null)
    {
        try
        {
            var treeRes = await treeResolver.ResolveTreeAsync(tree);
            if (!treeRes.IsResolved) return TreeErrorJson(treeRes);

            var res1 = await personResolver.ResolvePersonAsync(treeRes.TreeId, person1);
            if (!res1.IsResolved) return PersonErrorJson(res1);

            var res2 = await personResolver.ResolvePersonAsync(treeRes.TreeId, person2);
            if (!res2.IsResolved) return PersonErrorJson(res2);

            var steps = await traversalRepository.GetPathBetweenPersonsAsync(
                treeRes.TreeId, res1.PersonId, res2.PersonId, maxDepth);

            return JsonSerializer.Serialize(new
            {
                tree = MapTree(treeRes),
                from = new { personId = res1.PersonId, fullName = res1.FullName },
                to = new { personId = res2.PersonId, fullName = res2.FullName },
                stepCount = steps.Count,
                steps = steps.Select(s => new
                {
                    step = s.Step,
                    fromId = s.FromId,
                    fromName = s.FromName,
                    toId = s.ToId,
                    toName = s.ToName,
                    relation = s.Relation,
                }),
            }, McpJson.Options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[get_path_between_persons] {ex.GetType().Name}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = ex.Message }, McpJson.Options);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object MapTree(TreeResolution resolution) => new
    {
        treeId = resolution.TreeId,
        name = resolution.Name,
        isDefault = resolution.IsDefault,
    };

    private static string TreeErrorJson(TreeResolution resolution) =>
        JsonSerializer.Serialize(new { error = resolution.FailureReason }, McpJson.Options);

    private static string PersonErrorJson(PersonResolution resolution) =>
        resolution.Candidates.Count > 0
            ? JsonSerializer.Serialize(new
            {
                error = resolution.FailureReason,
                candidates = resolution.Candidates.Select(c => new { personId = c.PersonId, fullName = c.FullName }),
            }, McpJson.Options)
            : JsonSerializer.Serialize(new { error = resolution.FailureReason }, McpJson.Options);

    private static object MapEvent(PersonEvent e) => new
    {
        eventId = e.EventId,
        externalEventKey = e.ExternalEventKey,
        eventType = e.EventType,
        eventValue = e.EventValue,
        dateRaw = e.DateRaw,
        dateFrom = e.DateFrom,
        dateTo = e.DateTo,
        yearFrom = e.YearFrom,
        yearTo = e.YearTo,
        placeRaw = e.PlaceRaw,
        placeNormalized = e.PlaceNormalized,
        familyId = e.FamilyId,
        relatedPersonId = e.RelatedPersonId,
        relatedPersonName = e.RelatedPersonName,
        isDerived = e.IsDerived,
        notes = e.Notes,
    };
}
