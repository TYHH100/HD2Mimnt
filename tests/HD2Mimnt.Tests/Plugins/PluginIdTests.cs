using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Tests.Plugins;

/// <summary>
/// 插件 Id 白名单：Id 会拼进宿主进程的数据目录路径（Plugins/&lt;id&gt;），
/// 路径分隔符、盘符与遍历序列必须在安装与宿主加载两处都被拒绝。
/// </summary>
public sealed class PluginIdTests
{
    [Theory]
    [InlineData("com.example.analysis", true)]
    [InlineData("0E5F3A1B-6D4C-4E3A-9B2F-1A2B3C4D5E6F", true)]
    [InlineData("mdl3.helper_2", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("..\\..\\..\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu", false)]
    [InlineData("C:\\Users\\example", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("a b", false)]
    [InlineData("插件", false)]
    [InlineData("a..b", true)] // 词中双点只是普通字符，Path.Combine 后仍在 Plugins/ 内
    public void IsValid_RejectsTraversalAndUnsafeCharacters(string? id, bool expected)
    {
        Assert.Equal(expected, PluginId.IsValid(id));
    }

    [Fact]
    public void IsValid_RejectsOverlongId()
    {
        Assert.False(PluginId.IsValid(new string('a', 129)));
        Assert.True(PluginId.IsValid(new string('a', 128)));
    }
}
