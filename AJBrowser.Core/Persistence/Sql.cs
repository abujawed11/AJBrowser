using Microsoft.Data.Sqlite;

namespace AJBrowser.Core.Persistence;

/// <summary>Small helpers shared by the repositories.</summary>
internal static class Sql
{
    public static SqliteCommand Command(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>A LIKE pattern matching <paramref name="text"/> anywhere, with % _ and \ escaped (use with ESCAPE '\').</summary>
    public static string ContainsPattern(string text) =>
        "%" + text.Replace(@"\", @"\\", StringComparison.Ordinal)
                  .Replace("%", @"\%", StringComparison.Ordinal)
                  .Replace("_", @"\_", StringComparison.Ordinal) + "%";

    /// <summary>Cuts overly long text (titles) so one page can't bloat the database.</summary>
    public static string Truncate(string? text, int maxLength) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= maxLength ? text : text[..maxLength];
}
