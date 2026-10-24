using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class LogViewModelTests
{
    [Fact]
    public async Task Logs_ShouldGroupMultilineFilterByLevelAndFormatApiDetailsWithoutChangingCopy()
    {
        var service = new Mock<IFileLogService>();
        const string debug = "2026-10-15 13:00:00.000 +08:00 [Debug] Test.Nexus: Nexus API response: files; HTTP 403; Headers=[{\"Name\":\"Content-Type\",\"Value\":\"application/json\"}]; Body={\"code\":403,\"message\":\"Mod not available: 17373\"}";
        service.Setup(item => item.ReadRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            debug,
            "2026-10-15 13:01:00.000 +08:00 [Warning] Test.Service: 多行错误",
            "stack trace continuation",
            "2026-10-15 13:02:00.000 +08:00 [Critical] Test.Service: fatal"
        });
        var vm = new LogViewModel(service.Object);
        await vm.InitializeAsync();
        Assert.Equal(3, vm.EntryItems.Count);
        Assert.Equal("Critical", vm.EntryItems[0].LevelText);
        Assert.Contains("stack trace continuation", vm.EntryItems[1].Raw);
        var api = vm.EntryItems[2];
        Assert.DoesNotContain("Headers=", api.Summary);
        Assert.Contains("\n  \"code\": 403", api.Detail.Replace("\r\n", "\n"));
        Assert.Equal(debug, api.Raw);
        vm.SearchText = "17373";
        Assert.Single(vm.EntryItems);
        Assert.Equal(debug, vm.GetRecentText());
        vm.DisplayLevel = "Warning";
        Assert.Empty(vm.EntryItems);
        vm.SearchText = "";
        Assert.Equal(2, vm.EntryItems.Count);
        vm.ErrorsOnly = true;
        Assert.Single(vm.EntryItems);
        Assert.Equal("Critical", vm.EntryItems[0].LevelText);
    }
}
