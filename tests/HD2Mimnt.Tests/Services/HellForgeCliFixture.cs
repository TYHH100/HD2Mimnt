using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 伪造一个 HellForge 安装：安装标记、game 目录校验所需的文件，以及 app/cli 下的 CLI 替身
/// （HD2Mimnt.HellForgeCliStub，程序集名 HellForge.Cli）。替身按命令输出预置 JSON，
/// 并把收到的参数追加到 cli-args.txt；cli-fail.txt 存在时以 stderr 失败。
/// </summary>
public sealed class HellForgeCliFixture : IDisposable
{
    public string Root { get; }
    public string ArgsFile => Path.Combine(Root, "cli-args.txt");

    private HellForgeCliFixture(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hellforge-install.json"),
            """{"schemaVersion": 1, "buildId": "25480438"}""");
        foreach (var folder in new[] { "data", "tools", "bin" })
            Directory.CreateDirectory(Path.Combine(root, "game", folder));
        File.WriteAllText(Path.Combine(root, "game", "bin", "helldivers2.exe"), "stub");

        var cli = Path.Combine(root, "app", "cli");
        Directory.CreateDirectory(cli);
        foreach (var file in new[] { "HellForge.Cli.exe", "HellForge.Cli.dll", "HellForge.Cli.runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(cli, file));

        File.WriteAllText(Path.Combine(root, "cli-status.json"), """
            {
              "install": "stub", "game": "stub/game", "gameBuild": "25480438",
              "hellforge": "0.4.0-beta", "engine": "1.2.3", "modSdk": 16,
              "ready": { "gameCopy": true, "sessionRunner": true, "linuxNamespace": false },
              "networkBlock": "wfp",
              "settings": { "profile": "default", "war": "live", "localPlayFab": true, "multiplayer": true,
                            "engineLog": true, "crashDump": false, "sandbox": true, "steamUdp": true, "gameOptions": "" },
              "session": { "running": false, "lastRun": null, "lastStatus": "observed", "lastFailure": null },
              "languages": [ "English" ], "logs": "stub/logs", "mods": "stub/mods"
            }
            """);
        File.WriteAllText(Path.Combine(root, "cli-profiles.json"), """
            {
              "profiles": [
                { "name": "default", "template": "starter", "folder": "stub/profiles/default", "used": true, "selected": true },
                { "name": "alt", "template": "starter", "folder": "stub/profiles/alt", "used": false, "selected": false }
              ],
              "templates": [ { "name": "starter", "description": "Fresh player after basic training", "items": 3 } ]
            }
            """);
    }

    public static HellForgeCliFixture Create()
        => new(Path.Combine(Path.GetTempPath(), $"HD2Mimnt-HellForge-{Guid.NewGuid():N}"));

    public string ReadArgs() => File.Exists(ArgsFile) ? File.ReadAllText(ArgsFile) : string.Empty;

    public void SetFailure(string message) => File.WriteAllText(Path.Combine(Root, "cli-fail.txt"), message);

    public void OverwriteResponse(string command, string json)
        => File.WriteAllText(Path.Combine(Root, $"cli-{command}.json"), json);

    /// <summary>预置 play 事件流；hold 为 true 时 play 输出完后等待 stop 信号才结束。</summary>
    public void SetPlayStream(string jsonl, bool hold = false, int exitCode = 0)
    {
        File.WriteAllText(Path.Combine(Root, "cli-play.jsonl"), jsonl);
        if (hold) File.WriteAllText(Path.Combine(Root, "cli-play-hold.txt"), "hold");
        if (exitCode != 0) File.WriteAllText(Path.Combine(Root, "cli-play-exit.txt"), exitCode.ToString());
    }

    /// <summary>把安装目录写入设置，供 HellForgeService / GameDetectionService 解析。</summary>
    public async Task ConfigureAsync(SettingsService settings)
        => await settings.SetSettingAsync(SettingKeys.HellForgePath, Root);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}