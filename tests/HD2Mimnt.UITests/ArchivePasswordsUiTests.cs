using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ArchivePasswords_ShouldSaveSelectDefaultExportPromptRetryAndCancelImport()
    {
        await using var scope = await UiScope.CreateAsync();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var passwords = new ArchivePasswordsViewModel(settings);
        var editor = new ArchivePasswordsControl { DataContext = passwords };
        var window = new Window { Content = editor, Width = 560, Height = 600 };
        try
        {
            window.Show();
            passwords.Name = "常用密码";
            passwords.Password = "test password!";
            passwords.MakeDefault = true;
            await passwords.SaveCommand.ExecuteAsync(null);
            Assert.Empty(passwords.Error);
            Assert.Single(passwords.Entries);
            Assert.Equal('●', editor.FindControl<TextBox>("ArchivePasswordInput")!.PasswordChar);

            var creation = scope.Services.GetRequiredService<ModCreationViewModel>();
            Assert.True(creation.Passwords.UsePassword);
            Assert.Equal("test password!", creation.Passwords.GetExportPassword());
            var change = await new ArchivePasswordService(settings).SaveAsync("另一个默认", "changed", true);
            Assert.Equal("changed", creation.Passwords.GetExportPassword());
            await new ArchivePasswordService(settings).DeleteAsync(change.Id);
            Assert.Null(creation.Passwords.GetExportPassword());
            passwords.MakeDefault = true;
            await passwords.SaveCommand.ExecuteAsync(null);
            Assert.Equal("test password!", creation.Passwords.GetExportPassword());
            var source = Path.Combine(scope.Root, "encrypted-source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "aaaaaaaaaaaaaaaa.patch_0"), "test patch");
            creation.SourceDirectory = source;
            await creation.InitializeSourceAsync();
            creation.ModName = "Password UI mod";
            var archive = Path.Combine(scope.Root, "mod.zip");
            await creation.ExportAsync(archive);
            Assert.True(File.Exists(archive), creation.StatusMessage);

            // 清空库后用真实 UI ViewModel 等待密码，错误密码后仍可重试。
            var service = new ArchivePasswordService(settings);
            await service.DeleteAsync(Assert.Single(service.Read().Entries).Id);
            var library = scope.Services.GetRequiredService<LibraryViewModel>();
            var import = library.Import;
            window.Content = new ModImportOverlay { DataContext = library };
            var importing = import.ImportAsync([archive]);
            await WaitForPasswordAsync(import);
            var overlay = Assert.IsType<ModImportOverlay>(window.Content);
            var input = overlay.FindControl<TextBox>("ImportPasswordInput")!;
            input.Text = "wrong";
            import.SubmitPasswordCommand.Execute(null);
            for (var i = 0; i < 100 && !import.HasPasswordRequest; i++) await Task.Delay(10);
            // 等待前一轮请求完成，并允许下一轮请求出现。
            await Task.Delay(150);
            await WaitForPasswordAsync(import);
            input.Text = "test password!";
            import.SubmitPasswordCommand.Execute(null);
            await importing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, import.SuccessCount);
            Assert.Empty(import.Errors);

            var cancelled = import.ImportAsync([archive]);
            await WaitForPasswordAsync(import);
            import.CancelCommand.Execute(null);
            await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(import.IsImporting);
            Assert.False(import.HasPasswordRequest);
            Assert.Equal(0, import.FailedCount);
            Assert.Equal(0, import.SuccessCount);
        }
        finally { window.Close(); }

        static async Task WaitForPasswordAsync(ModImportViewModel import)
        {
            for (var i = 0; i < 500 && !import.HasPasswordRequest && import.IsImporting; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.True(import.HasPasswordRequest, string.Join("; ", import.Errors));
        }
    }
}
