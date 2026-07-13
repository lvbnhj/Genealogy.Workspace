using Npgsql;

namespace Genealogy.Workspace.Data.Repositories;

/// <summary>
/// Small helpers for reading nullable columns from an <see cref="NpgsqlDataReader"/>
/// by name, shared across the repositories in this project.
/// </summary>
internal static class NpgsqlDataReaderExtensions
{
    public static string? GetNullableString(this NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    public static T? GetNullableValue<T>(this NpgsqlDataReader reader, string column)
        where T : struct
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    /// <summary>
    /// Reads a PostgreSQL <c>char(1)</c>/<c>character(1)</c> column, which
    /// Npgsql surfaces as a (possibly blank-padded) string, as a nullable
    /// <see cref="char"/>.
    /// </summary>
    public static char? GetNullableChar(this NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetFieldValue<string>(ordinal);
        return value.Length > 0 ? value[0] : null;
    }
}
