using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// SQLite 值与 CLR 类型的统一转换。
/// 时间统一以 UTC ISO-8601（"o"）字符串落库，读回时用 RoundtripKind 精确还原偏移量。
/// </summary>
internal static class SqlConvert
{
    public static string? ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static string? ToDb(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? default
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ParseDateNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    public static string Str(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    public static string? StrOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int Int(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);

    public static long Long(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0L : reader.GetInt64(ordinal);

    public static bool Bool(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;
}
