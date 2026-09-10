namespace VrfInsights.Data;

/// <summary>
/// Parquet.Net's untyped deserializer (<see cref="Parquet.Serialization.ParquetSerializer.DeserializeUntypedAsync"/>)
/// hands back each row as a <c>Dictionary&lt;string, object&gt;</c> keyed by column name, with the
/// value boxed as whatever CLR type the column's physical/logical Parquet type maps to (e.g. an
/// unsigned 32-bit column may come back boxed as <see cref="int"/>, <see cref="uint"/> or
/// <see cref="long"/> depending on the exact logical-type annotation the writer used).
///
/// Rather than hard-code one exact CLR type per column, every accessor here goes through
/// <see cref="Convert"/> against whatever numeric type actually arrives, and returns null for a
/// missing key, a genuine Parquet null, or a value that isn't convertible. This keeps the table
/// readers correct even if a future vrfkit release changes a column's exact physical width.
/// </summary>
public static class RowConvert
{
    public static long? ToLong(IReadOnlyDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v is null) return null;
        try
        {
            return v switch
            {
                long l => l,
                int i => i,
                uint ui => ui,
                short s => s,
                ushort us => us,
                byte b => b,
                sbyte sb => sb,
                ulong ul => unchecked((long)ul),
                _ => System.Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return null;
        }
    }

    public static double? ToDouble(IReadOnlyDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v is null) return null;
        try
        {
            return v switch
            {
                double d => d,
                float f => f,
                _ => System.Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool? ToBool(IReadOnlyDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v is null) return null;
        return v switch
        {
            bool b => b,
            _ => null
        };
    }

    public static string? ToStringValue(IReadOnlyDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v is null) return null;
        return v as string ?? v.ToString();
    }

    public static byte[]? ToBytes(IReadOnlyDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v is null) return null;
        return v as byte[];
    }
}
