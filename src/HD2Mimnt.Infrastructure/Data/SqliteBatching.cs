using Microsoft.Data.Sqlite;
using System.Text;

namespace HD2Mimnt.Infrastructure.Data;

/// <summary>
/// 批量 IN 查询支持：单条 SQL 的绑定参数数量有上限，调用方需按块大小分批查询。
/// </summary>
internal static class SqliteBatching
{
    /// <summary>保守分块大小，远低于 SQLite 32766 的参数上限。</summary>
    public const int MaxParametersPerChunk = 500;

    /// <summary>
    /// 为 <paramref name="values"/> 绑定 @p0..@pN 参数，返回可直接嵌入 SQL 的占位符列表。
    /// </summary>
    public static string BindInParameters<T>(SqliteCommand command, IReadOnlyList<T> values, Func<T, object> convert)
    {
        var placeholders = new StringBuilder(values.Count * 5);
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                placeholders.Append(',');
            }
            var name = "@p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders.Append(name);
            command.Parameters.AddWithValue(name, convert(values[i]));
        }
        return placeholders.ToString();
    }
}
