using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.ViewModels;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

public class ModItemBadgeTests
{
    [Fact]
    public void ProductTag_ShowsHd2ClBadgeOnly()
    {
        var item = new ModItemViewModel(new Mod { Name = "角色库成品", Tags = [Hd2ClProductTag.Value] });

        Assert.True(item.IsHd2Cl);
        Assert.False(item.IsHd2Ir);
    }

    [Fact]
    public void IasWorkflow_ShowsHd2IrBadgeOnly()
    {
        var item = new ModItemViewModel(new Mod { Name = "源模组", WorkflowType = "HD2IAS" });

        Assert.True(item.IsHd2Ir);
        Assert.False(item.IsHd2Cl);
    }
}
