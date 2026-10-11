using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class LibraryViewModelSelectionTests
{
    [Fact]
    public void FocusMod_ShouldOnlyFocusAndAnchorSubsequentShiftRanges()
    {
        var vm = CreateViewModelWithMods(out var first, out var second, out var third);
        foreach (var mod in new[] { first, second, third }) vm.FilteredMods.Add(mod);
        vm.ToggleSelected(third);
        vm.FocusMod(first);
        Assert.Same(first, vm.FocusedMod);
        Assert.Empty(vm.SelectedMods);
        Assert.False(first.IsSelected);
        vm.SelectRange(third);
        Assert.Equal(new[] { first, second, third }, vm.SelectedMods);
        vm.SelectRange(second);
        Assert.Equal(new[] { first, second }, vm.SelectedMods);
        vm.SelectRange(third);
        Assert.Equal(new[] { first, second, third }, vm.SelectedMods);
    }

    [Fact]
    public void CtrlShift_ShouldAppendRangeAndCtrlDeselect_ShouldMoveAnchor()
    {
        var vm = CreateViewModelWithMods(out var first, out var second, out var third);
        foreach (var mod in new[] { first, second, third }) vm.FilteredMods.Add(mod);
        vm.ToggleSelected(first);
        vm.ToggleSelected(third);
        vm.SelectRange(second, additive: true);
        Assert.Equal(3, vm.SelectedCount);
        vm.ToggleSelected(third);
        Assert.False(third.IsSelected);
        vm.SelectRange(second);
        Assert.Equal(new[] { second, third }, vm.SelectedMods);
        Assert.Same(second, vm.FocusedMod);
    }

    [Fact]
    public void SelectAllVisible_ShouldRemainSelectedOnRepeatedShortcutAndClearShouldResetAnchor()
    {
        var vm = CreateViewModelWithMods(out var first, out var second, out var third);
        foreach (var mod in new[] { first, second, third }) vm.FilteredMods.Add(mod);
        vm.FocusMod(first);
        vm.SelectAllVisibleCommand.Execute(null);
        vm.SelectAllVisibleCommand.Execute(null);
        Assert.Equal(3, vm.SelectedCount);
        vm.ClearSelectionCommand.Execute(null);
        vm.FocusedMod = null;
        vm.SelectRange(third);
        Assert.Equal(third, Assert.Single(vm.SelectedMods));
    }

    [Fact]
    public void SelectRange_ShouldUseCurrentVisibleOrderAndRecoverWhenAnchorIsFilteredOut()
    {
        var vm = CreateViewModelWithMods(out var first, out var second, out var third);
        foreach (var mod in new[] { third, first, second }) vm.FilteredMods.Add(mod);
        vm.FocusMod(third);
        vm.SelectRange(first);
        Assert.Equal(new[] { third, first }, vm.SelectedMods);
        vm.FilteredMods.Remove(third);
        vm.SelectRange(second);
        Assert.Equal(second, Assert.Single(vm.SelectedMods));
    }

    [Fact]
    public void SyncListSelection_ShouldKeepCheckboxAndBatchSelectionInSync()
    {
        var viewModel = new LibraryViewModel(
            null!, null!, null!, null!, null!, null!, null!);
        var first = new ModItemViewModel(new HD2Mimnt.Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "First",
            Path = "first"
        });
        var second = new ModItemViewModel(new HD2Mimnt.Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = "Second",
            Path = "second"
        });
        viewModel.FilteredMods.Add(first);
        viewModel.FilteredMods.Add(second);

        viewModel.SyncListSelection(new[] { first, second }, Array.Empty<ModItemViewModel>());

        Assert.True(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.Equal(2, viewModel.SelectedCount);
        Assert.True(viewModel.IsAllSelected);

        viewModel.SyncListSelection(Array.Empty<ModItemViewModel>(), new[] { first });

        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.Single(viewModel.SelectedMods);
        Assert.Equal(second, viewModel.SelectedMods[0]);
        Assert.False(viewModel.IsAllSelected);
    }

    [Fact]
    public void SelectAllCommand_ShouldToggleOnlyFilteredSelection()
    {
        var viewModel = CreateViewModelWithMods(out var first, out var second, out var hidden);
        viewModel.FilteredMods.Add(first);
        viewModel.FilteredMods.Add(second);
        viewModel.SyncListSelection(new[] { hidden }, Array.Empty<ModItemViewModel>());

        viewModel.SelectAllCommand.Execute(null);

        Assert.True(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.True(hidden.IsSelected);
        Assert.Equal(3, viewModel.SelectedCount);

        viewModel.SelectAllCommand.Execute(null);

        Assert.False(first.IsSelected);
        Assert.False(second.IsSelected);
        Assert.True(hidden.IsSelected);
        Assert.Single(viewModel.SelectedMods);
        Assert.Equal(hidden, viewModel.SelectedMods[0]);
    }

    [Fact]
    public void InvertSelectionCommand_ShouldInvertOnlyFilteredSelection()
    {
        var viewModel = CreateViewModelWithMods(out var first, out var second, out var hidden);
        viewModel.FilteredMods.Add(first);
        viewModel.FilteredMods.Add(second);
        viewModel.SyncListSelection(new[] { first, hidden }, Array.Empty<ModItemViewModel>());

        viewModel.InvertSelectionCommand.Execute(null);

        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.True(hidden.IsSelected);
        Assert.Equal(2, viewModel.SelectedCount);
    }

    private static LibraryViewModel CreateViewModelWithMods(
        out ModItemViewModel first,
        out ModItemViewModel second,
        out ModItemViewModel hidden)
    {
        var viewModel = new LibraryViewModel(null!, null!, null!, null!, null!, null!, null!);
        first = CreateMod("First");
        second = CreateMod("Second");
        hidden = CreateMod("Hidden");
        return viewModel;
    }

    private static ModItemViewModel CreateMod(string name)
        => new(new HD2Mimnt.Core.Entities.Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = name
        });
}
