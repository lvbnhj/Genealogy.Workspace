namespace Genealogy.Workspace.Data.Models;

/// <summary>
/// One row of <see cref="Repositories.PersonSearchRepository.FindTreePersonAsync"/>,
/// at parity with the SQL Server proc <c>ged.FindTreePerson</c>
/// (Database/Procedures/ged/FindTreePerson.sql). No DNA columns.
/// </summary>
public sealed record TreePersonSearchResult(
    Guid PersonId,
    string? FullName,
    char? Sex,
    bool? IsLiving,
    short? BirthYear,
    string? BirthPlace,
    short? DeathYear);
