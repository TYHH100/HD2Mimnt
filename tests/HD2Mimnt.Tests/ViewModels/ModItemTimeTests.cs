using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class ModItemTimeTests
{
    [Fact]
    public void MissingImportTime_ShouldDisplayUnknownWithoutGuessingFromModifiedTime()
    {
        var mod = new Mod { ModifiedAt = DateTime.UtcNow };
        var item = new ModItemViewModel(mod);
        Assert.Equal("未记录（旧记录缺失导入时间）", item.ImportedAtDisplay);
        Assert.Equal(DateTime.MinValue, mod.ImportedAt);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TimeDisplay_ShouldConvertUtcAndPreserveAlreadyLocalTime(DateTimeKind kind)
    {
        var utc = new DateTime(2026, 10, 10, 2, 30, 0, DateTimeKind.Utc);
        var stored = kind == DateTimeKind.Local ? utc.ToLocalTime() : DateTime.SpecifyKind(utc, kind);
        var item = new ModItemViewModel(new Mod { ImportedAt = stored, ModifiedAt = stored });
        var expected = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(expected, item.ImportedAtDisplay);
        Assert.Equal(expected, item.ModifiedAtDisplay);
    }
}
