namespace HD2Mimnt.UI.ViewModels;

public sealed record VersionDiffLines(IReadOnlyList<DiffLineViewModel> Lines, bool Truncated, bool Approximate);

/// <summary>
/// 把当前版本（左）与所选版本（右）的文本按行对齐。
/// 相邻的删除块与新增块按行配对，形成并排的“修改”行；文件过大时退化为按行号并列。
/// </summary>
public static class VersionDiffBuilder
{
    public const int MaxRows = 800;
    private const long MaxLcsCells = 2_000_000;

    public static VersionDiffLines Build(string? current, string? selected)
    {
        var left = SplitLines(current);
        var right = SplitLines(selected);
        if (left.Length == 0 && right.Length == 0) return new(Array.Empty<DiffLineViewModel>(), false, false);
        if (left.Length == 0) return new(SingleSide(right, rightSide: true), false, false);
        if (right.Length == 0) return new(SingleSide(left, rightSide: false), false, false);

        var approximate = (long)left.Length * right.Length > MaxLcsCells;
        var rows = approximate ? BuildByIndex(left, right) : BuildAligned(left, right);
        var truncated = rows.Count > MaxRows;
        return new(truncated ? rows.Take(MaxRows).ToArray() : rows, truncated, approximate);
    }

    private static string[] SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static IReadOnlyList<DiffLineViewModel> SingleSide(string[] lines, bool rightSide)
        => lines.Select((text, index) => rightSide
            ? new DiffLineViewModel(null, (index + 1).ToString(), "", text, false, true)
            : new DiffLineViewModel((index + 1).ToString(), null, text, "", true, false)).ToArray();

    private static List<DiffLineViewModel> BuildByIndex(string[] left, string[] right)
    {
        var rows = new List<DiffLineViewModel>(Math.Max(left.Length, right.Length));
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var hasLeft = index < left.Length;
            var hasRight = index < right.Length;
            var same = hasLeft && hasRight && left[index] == right[index];
            rows.Add(new DiffLineViewModel(hasLeft ? (index + 1).ToString() : null,
                hasRight ? (index + 1).ToString() : null,
                hasLeft ? left[index] : "", hasRight ? right[index] : "",
                hasLeft && !same, hasRight && !same));
        }
        return rows;
    }

    private static List<DiffLineViewModel> BuildAligned(string[] left, string[] right)
    {
        var lcs = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
            for (var j = right.Length - 1; j >= 0; j--)
                lcs[i, j] = left[i] == right[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var rows = new List<DiffLineViewModel>();
        var removed = new List<(int Number, string Text)>();
        var added = new List<(int Number, string Text)>();
        int x = 0, y = 0;
        while (x < left.Length || y < right.Length)
        {
            if (x < left.Length && y < right.Length && left[x] == right[y])
            {
                Flush();
                rows.Add(new DiffLineViewModel((x + 1).ToString(), (y + 1).ToString(), left[x], right[y], false, false));
                x++;
                y++;
            }
            else if (y < right.Length && (x == left.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                added.Add((y + 1, right[y]));
                y++;
            }
            else
            {
                removed.Add((x + 1, left[x]));
                x++;
            }
        }
        Flush();
        return rows;

        void Flush()
        {
            for (var index = 0; index < Math.Max(removed.Count, added.Count); index++)
            {
                var hasLeft = index < removed.Count;
                var hasRight = index < added.Count;
                rows.Add(new DiffLineViewModel(
                    hasLeft ? removed[index].Number.ToString() : null,
                    hasRight ? added[index].Number.ToString() : null,
                    hasLeft ? removed[index].Text : "",
                    hasRight ? added[index].Text : "",
                    hasLeft, hasRight));
            }
            removed.Clear();
            added.Clear();
        }
    }
}
