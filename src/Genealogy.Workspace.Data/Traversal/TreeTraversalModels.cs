namespace Genealogy.Workspace.Data.Traversal;

/// <summary>
/// One ancestor of a person, produced by
/// <see cref="TreeTraversalRepository.GetAncestorsAsync"/>. Ported from the
/// SQL Server proc <c>ged.GetAncestors</c>, reading
/// <c>genealogy.parent_child</c> (child -&gt; parent) instead of
/// <c>ged.TreeParentOf</c>, with all DNA columns dropped. <see cref="Generation"/>
/// is 1-based (1 = direct parent). Birth/death enrichment comes from
/// <c>genealogy.event</c> (BIRT/CHR for birth, DEAT for death) with place text
/// resolved via <c>place_id -&gt; genealogy.place.place_raw</c>.
/// </summary>
public sealed record AncestorRow(
    Guid PersonId,
    string? FullName,
    char? Sex,
    bool? IsLiving,
    int Generation,
    int? BirthYear,
    string? BirthDateRaw,
    string? BirthPlace,
    int? DeathYear,
    string? DeathPlace);

/// <summary>
/// One descendant of an ancestor, produced by
/// <see cref="TreeTraversalRepository.GetDescendantsAsync"/>. Ported from
/// <c>ged.GetDescendants</c> (parent -&gt; child) with the source's DNA-link
/// columns (<c>LinkedDnaPersonName</c>, <c>LinkedDnaKitId</c>) dropped.
/// <see cref="Generation"/> is 1-based from the ancestor (0 = the ancestor,
/// excluded). <see cref="ParentPersonId"/> is the in-selection parent through
/// which this descendant was reached.
/// </summary>
public sealed record DescendantRow(
    Guid PersonId,
    string? PrimaryDisplayName,
    char? Sex,
    bool? IsLiving,
    int Generation,
    Guid ParentPersonId);

/// <summary>
/// One person (blood descendant or married-in spouse) alive at a reference
/// year, produced by <see cref="TreeTraversalRepository.GetDescendantsAtYearAsync"/>.
/// Ported from <c>ged.GetDescendantsAtYear</c>. <see cref="PersonType"/> is
/// either <c>DESCENDANT</c> or <c>SPOUSE</c>. For SPOUSE rows
/// <see cref="SpouseOfPersonId"/> is the descendant they married and
/// <see cref="Generation"/>/<see cref="ParentPersonId"/> are copied from that
/// descendant. Spouses are included via marriage regardless of their own
/// birth/death years (their years are still reported here for information).
/// </summary>
public sealed record DescendantAtYearRow(
    Guid PersonId,
    string? FullName,
    char? Sex,
    string PersonType,
    int Generation,
    Guid? ParentPersonId,
    Guid? SpouseOfPersonId,
    int? BirthYear,
    int? DeathYear);

/// <summary>
/// The winning closest common ancestor across a set of input persons. Ported
/// from the first result set of <c>ged.GetClosestCommonAncestorByIds</c>.
/// <see cref="MaxDepth"/>/<see cref="SumDepth"/> are computed over the minimum
/// reachable depth per input person (see repository notes).
/// </summary>
public sealed record CommonAncestorWinner(
    Guid AncestorId,
    string? AncestorName,
    int MaxDepth,
    int SumDepth,
    int PersonCount);

/// <summary>
/// The minimum depth from a single input person up to the winning common
/// ancestor. Ported from the second result set of
/// <c>ged.GetClosestCommonAncestorByIds</c>. A person is their own ancestor at
/// depth 0.
/// </summary>
public sealed record CommonAncestorInputDepth(
    Guid PersonId,
    string? PersonName,
    int Depth);

/// <summary>
/// Combined result of <see cref="TreeTraversalRepository.GetClosestCommonAncestorAsync"/>:
/// the winning <see cref="Ancestor"/> (null when the inputs share no common
/// ancestor within the depth limit) and the per-input depths to it (empty when
/// there is no winner).
/// </summary>
public sealed record CommonAncestorResult(
    CommonAncestorWinner? Ancestor,
    IReadOnlyList<CommonAncestorInputDepth> InputDepths);

/// <summary>
/// One node of a generic up/down walk, produced by
/// <see cref="TreeTraversalRepository.GetPersonTreeAsync"/>. Ported from
/// <c>ged.GetTreePersonTree</c> with DNA-link columns dropped.
/// <see cref="Path"/> is the '&gt;'-joined chain of person ids from the root to
/// this node. Edge columns describe the <c>parent_child</c> edge that produced
/// this node (always oriented parent -&gt; child, regardless of walk
/// direction); they are null on the root row.
/// </summary>
public sealed record PersonTreeNode(
    Guid RootPersonId,
    string Direction,
    Guid PersonId,
    string? FullName,
    char? Sex,
    bool? IsLiving,
    int Generation,
    Guid? EdgeFromPersonId,
    string? EdgeFromName,
    Guid? EdgeToPersonId,
    string? EdgeToName,
    string Path,
    int? BirthYear,
    string? BirthDateRaw,
    string? BirthPlace,
    int? DeathYear,
    string? DeathDateRaw,
    string? DeathPlace);

/// <summary>
/// One step of a shortest relationship path, produced by
/// <see cref="TreeTraversalRepository.GetPathBetweenPersonsAsync"/>. Ported
/// from <c>ged.GetPathBetweenPersonsByName</c>. <see cref="Relation"/> is
/// <c>PARENT_OF</c> when the step walks a parent -&gt; child edge and
/// <c>CHILD_OF</c> when it walks a child -&gt; parent edge. <see cref="Step"/>
/// is 1-based.
/// </summary>
public sealed record RelationshipPathStep(
    int Step,
    Guid FromId,
    string? FromName,
    Guid ToId,
    string? ToName,
    string Relation);
