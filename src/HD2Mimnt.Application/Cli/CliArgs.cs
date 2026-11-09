namespace HD2Mimnt.Application.Cli;

/// <summary>
/// 单条命令参数扫描器。
/// 值选项与开关在 handler 中按需消费并标记已用，剩余未以 -- 开头的 token 即位置参数；
/// 未知开关通过 <see cref="UnknownFlags"/> 暴露，由 handler 决定是否按用法错误拒绝。
/// </summary>
public sealed class CliArgs
{
    private readonly IReadOnlyList<string> _tokens;
    private readonly bool[] _consumed;

    public CliArgs(IReadOnlyList<string> tokens)
    {
        _tokens = tokens;
        _consumed = new bool[tokens.Count];
    }

    /// <summary>读取一次值选项（如 --method copy）；不存在返回 null。</summary>
    public string? Option(params string[] names)
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_consumed[index] || !_tokens[index].StartsWith("--")) continue;
            var token = _tokens[index];
            // 支持 --method=copy 形式。
            var equals = token.IndexOf('=');
            var key = equals > 0 ? token[..equals] : token;
            if (!names.Contains(key)) continue;
            _consumed[index] = true;
            if (equals > 0) return token[(equals + 1)..];
            if (index + 1 < _tokens.Count && !_tokens[index + 1].StartsWith("--"))
            {
                _consumed[index + 1] = true;
                return _tokens[index + 1];
            }
            return "";
        }
        return null;
    }

    /// <summary>读取布尔开关（如 --yes）。</summary>
    public bool Flag(params string[] names)
    {
        var found = false;
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_consumed[index] || !_tokens[index].StartsWith("--")) continue;
            if (!names.Contains(_tokens[index])) continue;
            _consumed[index] = true;
            found = true;
        }
        return found;
    }

    /// <summary>剩余未被消费且不以 -- 开头的 token（位置参数）。</summary>
    public List<string> Positionals()
    {
        var result = new List<string>();
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_consumed[index]) continue;
            if (_tokens[index].StartsWith("--")) continue;
            result.Add(_tokens[index]);
            _consumed[index] = true;
        }
        return result;
    }

    /// <summary>未被识别的开关，供 handler 报用法错误。</summary>
    public List<string> UnknownFlags()
    {
        var result = new List<string>();
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_consumed[index] || !_tokens[index].StartsWith("--")) continue;
            // 未知开关的值不算未知开关。
            if (index + 1 < _tokens.Count && !_tokens[index + 1].StartsWith("--")) _consumed[index + 1] = true;
            result.Add(_tokens[index]);
        }
        return result;
    }
}
