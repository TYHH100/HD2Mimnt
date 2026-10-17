using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Data;
using Avalonia.Threading;
using HD2Mimnt.Application.Localization;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.UI.Localization;

/// <summary>
/// UI 线程上的语言适配器。静态文字按资源键绑定；旧服务消息仅在明确的展示绑定中翻译，
/// 保留 ViewModel 中的分类标识、用户输入及原始业务状态。
/// </summary>
public sealed class UiLocalization : INotifyPropertyChanged, IDisposable
{
    public static UiLocalization Current { get; } = new();
    private readonly TranslationCatalog _catalog = new();
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly List<MessageTemplate> _templates = [];
    private readonly Dictionary<string, string> _resourceNames = new(StringComparer.Ordinal);
    private readonly Regex _resourceNamePattern;
    private ILocalizationService? _service;
    private int _revision;

    public UiLocalization()
    {
        foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
        foreach (var (key, source) in _catalog.GetStrings(CultureInfo.GetCultureInfo(code)))
        {
            _sources.TryAdd(source.Replace("\r\n", "\n"), key);
            if (key.StartsWith("ResourceType.", StringComparison.Ordinal) && !source.Contains('{'))
                _resourceNames.TryAdd(source, key);
            if (Regex.IsMatch(source, @"\{\d+(?:[^}]*)\}"))
                _templates.Add(new(key, source));
        }
        _templates.Sort((left, right) => right.LiteralLength.CompareTo(left.LiteralLength));
        _resourceNamePattern = new Regex(string.Join("|", _resourceNames.Keys.OrderByDescending(value => value.Length)
            .Select(Regex.Escape)), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public ILocalizationService? Service => _service;
    public CultureInfo Culture => _service?.CurrentCulture ?? CultureInfo.GetCultureInfo("zh-CN");
    public int Revision => _revision;
    public string this[string key] => _service?.GetString(key) ?? _catalog.GetString(key, Culture);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Attach(ILocalizationService service)
    {
        if (ReferenceEquals(_service, service)) return;
        if (_service != null) _service.LanguageChanged -= OnLanguageChanged;
        _service = service;
        _service.LanguageChanged += OnLanguageChanged;
        PublishChange();
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _service)) PublishChange();
    }

    private void PublishChange()
    {
        void Apply()
        {
            _revision++;
            PropertyChanged?.Invoke(this, new(nameof(Revision)));
            PropertyChanged?.Invoke(this, new(nameof(Culture)));
            PropertyChanged?.Invoke(this, new("Item[]"));
        }
        if (global::Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    public string Translate(object? value)
    {
        if (value is null || ReferenceEquals(value, AvaloniaProperty.UnsetValue)) return string.Empty;
        if (value is HD2Mimnt.UI.Services.ModPlatformErrorMessage error) return error.Localize();
        if (value is ModPlatformLink link)
            return link.SupportsUpdates
                ? Format("Metadata.Summary", link.PlatformId == "ayakamods" ? "Ayaka Mods" : "Nexus Mods", link.ModId,
                    link.Name ?? this["Platform.NoApiName"], link.Author ?? this["Shell.Unknown"],
                    link.InstalledVersion ?? this["Shell.Unknown"], link.LatestVersion ?? this["Shell.Unknown"],
                    link.InstalledFileVersionId ?? link.InstalledWebsiteFileId ?? this["Shell.Unknown"],
                    link.RetrievedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? this["Shell.Unknown"])
                : this["Metadata.AuthorOnly"];
        var source = value.ToString() ?? string.Empty;
        if (OptionKeys.TryGetValue(source, out var optionKey)) return this[optionKey];
        if (_sources.TryGetValue(source.Replace("\r\n", "\n"), out var key)) return this[key];
        foreach (var template in _templates)
        {
            if (!template.CanMatch(source)) continue;
            var match = template.Pattern.Match(source);
            if (!match.Success) continue;
            var arguments = Enumerable.Range(0, template.ArgumentCount)
                .Select(index => (object?)match.Groups[$"p{index}"].Value).ToArray();
            // 只重译已知的业务选项参数，模组名、配置名、路径和外部诊断参数保持原值。
            if (template.Key == "UI.ProfileEnabledModsMethodTargetCleanUpThePreviousDeploymentAndDeployInP")
                arguments[2] = Translate(arguments[2]);
            return string.Format(Culture, this[template.Key], arguments);
        }
        // 用户数据和未收录的外部诊断原样显示。
        return source;
    }

    public string Format(string key, params object?[] values) => string.Format(Culture, this[key], values);

    public string TranslateResourceNames(object? value)
    {
        var source = Translate(value);
        return _resourceNamePattern.Replace(source, match => this[_resourceNames[match.Value]]);
    }

    public BindingBase TextBinding(string text) => new MultiBinding
    {
        Mode = BindingMode.OneWay,
        Converter = new LocalizedTextConverter(this),
        Bindings = { new Binding { Source = text }, new Binding(nameof(Revision)) { Source = this } }
    };

    public void Dispose()
    {
        if (_service != null) _service.LanguageChanged -= OnLanguageChanged;
        _service = null;
    }

    private static readonly IReadOnlyDictionary<string, string> OptionKeys = new Dictionary<string, string>
    {
        ["lua"] = "IAS.LuaRoute", ["native"] = "IAS.NativeRoute",
        ["Dark"] = "Options.Dark", ["Light"] = "Options.Light", ["System"] = "Options.System",
        ["Comfortable"] = "Options.Comfortable", ["Compact"] = "Options.Compact",
        ["Copy"] = "Options.Copy", ["HardLink"] = "Options.HardLink", ["SymbolicLink"] = "Options.SymbolicLink",
        ["Found"] = "Game.Found", ["NotFound"] = "Game.NotFound", ["Invalid"] = "Game.Invalid",
        ["Detecting"] = "Game.Detecting", ["Running"] = "Task.Running", ["Pending"] = "Task.Pending",
        ["Completed"] = "Task.Completed", ["Failed"] = "Task.Failed", ["Cancelled"] = "Task.Cancelled",
        ["ForceTerminated"] = "Task.ForceTerminated", ["FullAccess"] = "Plugin.FullAccess",
        ["Full Access"] = "Plugin.FullAccess", ["Sandbox"] = "Plugin.Sandboxed",
        ["Sandboxed"] = "Plugin.Sandboxed", ["Success"] = "Notification.Success",
        ["Warning"] = "Notification.Warning", ["Error"] = "Notification.Error",
        ["Info"] = "Notification.Info"
    };

    private sealed class MessageTemplate
    {
        public string Key { get; }
        public Regex Pattern { get; }
        public int LiteralLength { get; }
        public int ArgumentCount { get; }
        private readonly string _prefix;

        public MessageTemplate(string key, string source)
        {
            Key = key;
            var parts = Regex.Matches(source, @"\{(\d+)(?:[^}]*)\}");
            ArgumentCount = parts.Select(part => int.Parse(part.Groups[1].Value)).Max() + 1;
            _prefix = source[..parts[0].Index].Split('\r', '\n')[0];
            var previous = 0;
            var pattern = @"\A";
            var seen = new HashSet<int>();
            foreach (Match part in parts)
            {
                var literal = source[previous..part.Index];
                LiteralLength += literal.Length;
                pattern += EscapeLiteral(literal);
                var index = int.Parse(part.Groups[1].Value);
                pattern += seen.Add(index) ? $"(?<p{index}>.*?)" : $"\\k<p{index}>";
                previous = part.Index + part.Length;
            }
            LiteralLength += source.Length - previous;
            pattern += EscapeLiteral(source[previous..]) + @"\z";
            Pattern = new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        public bool CanMatch(string source) => source.StartsWith(_prefix, StringComparison.Ordinal);

        // ResGen 在 Windows 上可能保留 CRLF，而业务消息使用 LF；参数内容不做替换。
        private static string EscapeLiteral(string literal) => string.Join(@"\r?\n",
            literal.Replace("\r\n", "\n").Split('\n').Select(Regex.Escape));
    }
}
