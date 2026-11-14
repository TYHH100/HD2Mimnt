namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>上游 <c>ias_binary</c> 的 3x4 仿射矩阵与四元数投影。</summary>
public static class IasAffine
{
    public static double[] Product(double[] a, double[] b)
    {
        var result = new double[12];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 4; column++)
        {
            var value = 0.0;
            for (var k = 0; k < 3; k++) value += a[row * 4 + k] * b[k * 4 + column];
            if (column == 3) value += a[row * 4 + 3];
            result[row * 4 + column] = value;
        }
        return result;
    }

    public static double[] Inverse(double[] a)
    {
        double x = a[0], y = a[1], z = a[2], px = a[3], d = a[4], e = a[5], f = a[6], py = a[7],
            g = a[8], h = a[9], i = a[10], pz = a[11];
        var determinant = x * (e * i - f * h) - y * (d * i - f * g) + z * (d * h - e * g);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= 1e-12)
            throw new InvalidDataException("模型数据检查：singular affine matrix");
        double[] inverse =
        [
            (e * i - f * h) / determinant, (z * h - y * i) / determinant, (y * f - z * e) / determinant,
            (f * g - d * i) / determinant, (x * i - z * g) / determinant, (z * d - x * f) / determinant,
            (d * h - e * g) / determinant, (y * g - x * h) / determinant, (x * e - y * d) / determinant
        ];
        var result = new double[12];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++) result[row * 4 + column] = inverse[row * 3 + column];
            result[row * 4 + 3] = -(inverse[row * 3] * px + inverse[row * 3 + 1] * py + inverse[row * 3 + 2] * pz);
        }
        return result;
    }

    public static double[] Point(double[] matrix, IReadOnlyList<double> position)
        => [matrix[0] * position[0] + matrix[1] * position[1] + matrix[2] * position[2] + matrix[3],
            matrix[4] * position[0] + matrix[5] * position[1] + matrix[6] * position[2] + matrix[7],
            matrix[8] * position[0] + matrix[9] * position[1] + matrix[10] * position[2] + matrix[11]];

    /// <summary>把列主序 4x4 矩阵转为上游使用的行主序 3x4。</summary>
    public static double[] FromColumnMatrix(IReadOnlyList<double> values)
    {
        var result = new double[12];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 4; column++)
            result[row * 4 + column] = values[column * 4 + row];
        return result;
    }

    public static IasPose Pose(double[] matrix)
    {
        var columns = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var sum = 0.0;
            for (var r = 0; r < 3; r++) sum += matrix[r * 4 + c] * matrix[r * 4 + c];
            columns[c] = Math.Sqrt(sum);
        }
        if (columns.Min() <= 1e-12) throw new InvalidDataException("模型数据检查：zero-scale pose matrix");
        var a = matrix[0] / columns[0];
        var b = matrix[1] / columns[1];
        var c2 = matrix[2] / columns[2];
        var d = matrix[4] / columns[0];
        var e = matrix[5] / columns[1];
        var f = matrix[6] / columns[2];
        var g = matrix[8] / columns[0];
        var h = matrix[9] / columns[1];
        var i = matrix[10] / columns[2];
        var trace = a + e + i;
        double[] q;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1) * 2;
            q = [(h - f) / s, (c2 - g) / s, (d - b) / s, s / 4];
        }
        else if (a > e && a > i)
        {
            var s = Math.Sqrt(Math.Max(0.0, 1 + a - e - i)) * 2;
            q = [s / 4, (b + d) / s, (c2 + g) / s, (h - f) / s];
        }
        else if (e > i)
        {
            var s = Math.Sqrt(Math.Max(0.0, 1 + e - a - i)) * 2;
            q = [(b + d) / s, s / 4, (f + h) / s, (c2 - g) / s];
        }
        else
        {
            var s = Math.Sqrt(Math.Max(0.0, 1 + i - a - e)) * 2;
            q = [(c2 + g) / s, (f + h) / s, s / 4, (d - b) / s];
        }
        var length = Math.Sqrt(q.Sum(value => value * value));
        for (var index = 0; index < q.Length; index++) q[index] /= length;
        return new IasPose(q, [matrix[3], matrix[7], matrix[11]]);
    }

    /// <summary>按父节点在前的顺序把局部 Rest 合成世界 Rest。</summary>
    public static double[][] Worlds(IReadOnlyList<IasRigNode> nodes)
    {
        var result = new double[nodes.Count][];
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node.Parent < -1 || node.Parent >= index)
                throw new InvalidDataException("模型数据检查：Rig parent order");
            result[index] = node.Parent >= 0 ? Product(result[node.Parent], node.RestLocal) : (double[])node.RestLocal.Clone();
        }
        return result;
    }
}

/// <summary>Rig/Profile 节点：父索引、调色板槽位、继承标记与局部 Rest。</summary>
public sealed record IasRigNode(string Name, int Parent, int SourceIndex, int PaletteSlot, uint Flags, double TranslationScale, double[] RestLocal)
{
    public bool Inherit => Flags == 288;
}
