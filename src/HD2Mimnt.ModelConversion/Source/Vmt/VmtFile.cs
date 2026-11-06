using System.Text;

namespace HD2Mimnt.ModelConversion.Source.Vmt;

public sealed record VmtNode(string Key, string? Value, IReadOnlyList<VmtNode> Children, string? Condition);
public sealed record VmtTextureReference(string Parameter, string Path, string? Condition);

public sealed class VmtFile
{
    private static readonly HashSet<string> TextureParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "$basetexture", "$basetexture2", "$bumpmap", "$bumpmap2", "$normalmap", "$detail",
        "$envmap", "$envmapmask", "$envmapmask2", "$phongexponenttexture", "$phongwarptexture",
        "$lightwarptexture", "$selfillummask", "$blendmodulatetexture", "$dudvmap",
        "$flowmap", "$flow_noise_texture", "$emissiveblendtexture", "$emissiveblendflowtexture",
        "$emissiveblendbasetexture", "$refracttexture", "$reflecttexture"
    };

    private VmtFile(byte[] rawData, string text, VmtNode root)
    {
        RawData = rawData;
        Text = text;
        Root = root;
    }

    public ReadOnlyMemory<byte> RawData { get; }
    public string Text { get; }
    public VmtNode Root { get; }
    public string Shader => Root.Key;
    public bool IsPatch => Shader.Equals("patch", StringComparison.OrdinalIgnoreCase);
    public IEnumerable<VmtTextureReference> TextureReferences => EnumerateTextures(Root);

    public string? GetParameter(string key) => Root.Children
        .LastOrDefault(node => node.Condition is null && node.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    public static VmtFile Parse(byte[] data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException error)
        {
            throw new SourceFormatException("VMT text is not valid UTF-8", error);
        }
        var parser = new Parser(text);
        var root = parser.ReadRoot();
        return new VmtFile(data, text, root);
    }

    private static IEnumerable<VmtTextureReference> EnumerateTextures(VmtNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Value is not null && TextureParameters.Contains(child.Key))
                yield return new VmtTextureReference(child.Key, child.Value, child.Condition);
            foreach (var reference in EnumerateTextures(child))
                yield return reference;
        }
    }

    private sealed class Parser(string text)
    {
        private int _position;
        private string? _lookahead;

        public VmtNode ReadRoot()
        {
            var shader = Take() ?? throw Error("Missing shader");
            if (shader is "{" or "}") throw Error("Invalid shader");
            Expect("{");
            var root = new VmtNode(shader, null, ReadChildren(0), null);
            if (Take() is not null) throw Error("Unexpected content after material");
            return root;
        }

        private IReadOnlyList<VmtNode> ReadChildren(int depth)
        {
            if (depth > 64) throw Error("Nested KeyValues exceed 64 levels");
            var children = new List<VmtNode>();
            while (true)
            {
                var key = Take() ?? throw Error("Unterminated KeyValues block");
                if (key == "}") return children.AsReadOnly();
                if (key == "{") throw Error("Missing KeyValues key");
                var value = Take() ?? throw Error($"Missing value for {key}");
                if (value == "}") throw Error($"Missing value for {key}");
                var nested = value == "{" ? ReadChildren(depth + 1) : Array.Empty<VmtNode>();
                var condition = Peek();
                if (condition is not null && condition.StartsWith('[') && condition.EndsWith(']'))
                    Take();
                else
                    condition = null;
                children.Add(new VmtNode(key, value == "{" ? null : value, nested, condition));
            }
        }

        private string? Peek() => _lookahead ??= ReadToken();
        private string? Take()
        {
            var token = Peek();
            _lookahead = null;
            return token;
        }

        private void Expect(string token)
        {
            if (Take() != token) throw Error($"Expected {token}");
        }

        private string? ReadToken()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position])) { _position++; continue; }
                if (_position + 1 < text.Length && text[_position..(_position + 2)] == "//")
                {
                    while (_position < text.Length && text[_position] != '\n') _position++;
                    continue;
                }
                if (_position + 1 < text.Length && text[_position..(_position + 2)] == "/*")
                {
                    var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("Unterminated comment");
                    _position = end + 2;
                    continue;
                }
                break;
            }
            if (_position == text.Length) return null;
            var first = text[_position++];
            if (first is '{' or '}') return first.ToString();
            if (first == '"')
            {
                var value = new StringBuilder();
                while (_position < text.Length)
                {
                    var ch = text[_position++];
                    if (ch == '"') return value.ToString();
                    // VMT texture paths use literal backslashes, not C-style escapes.
                    if (ch == '\\' && _position < text.Length && text[_position] == '"')
                        ch = text[_position++];
                    value.Append(ch);
                }
                throw Error("Unterminated string");
            }
            var start = _position - 1;
            if (first == '[')
            {
                var end = text.IndexOf(']', _position);
                if (end < 0) throw Error("Unterminated bracket value");
                _position = end + 1;
                return text[start.._position];
            }
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not ('{' or '}'))
            {
                if (text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] is '/' or '*') break;
                _position++;
            }
            return text[start.._position];
        }

        private SourceFormatException Error(string message) => new($"VMT at character {_position}: {message}");
    }
}
