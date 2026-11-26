using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

public sealed record ModBinarySearchCandidate(Guid ModId, string Name, int Order);

public sealed record ModBinarySearchStartInfo(
    Guid SourceProfileId,
    string SourceProfileName,
    Guid TemporaryProfileId,
    string TemporaryProfileName,
    int TotalModCount,
    int CandidateCount,
    IReadOnlyList<string> ExcludedModNames);

public sealed record ModBinarySearchTest(
    Guid Id,
    int Round,
    bool IsFinalVerification,
    IReadOnlyList<ModBinarySearchCandidate> DisabledCandidates,
    IReadOnlyList<ModBinarySearchCandidate> EnabledCandidates);

public sealed record ModBinarySearchHistory(
    int Round,
    bool IsFinalVerification,
    bool ProblemRemains,
    IReadOnlyList<string> DisabledNames,
    IReadOnlyList<string> RemainingSuspectNames);

public sealed record ModBinarySearchResult(
    bool Confirmed,
    bool Cancelled,
    ModBinarySearchCandidate? Suspect,
    IReadOnlyList<ModBinarySearchHistory> History);

public sealed record ModBinarySearchStep(ModBinarySearchTest? Test, ModBinarySearchResult? Result,
    IReadOnlyList<ModBinarySearchHistory> History);

public sealed class ModBinarySearchService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IModRepository _modRepository;
    private readonly IDeploymentRepository _deploymentRepository;
    private readonly ProfileService _profileService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public ModBinarySearchService(IProfileRepository profileRepository, IModRepository modRepository,
        IDeploymentRepository deploymentRepository, ProfileService profileService)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
        _deploymentRepository = deploymentRepository;
        _profileService = profileService;
    }

    public async Task<ModBinarySearchStartInfo> StartAsync(Guid sourceProfileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session != null) throw new InvalidOperationException("已有模组二分排查正在进行。");
            var source = await _profileRepository.GetByIdAsync(sourceProfileId, cancellationToken)
                ?? throw new InvalidOperationException("源配置不存在。");
            var mods = await LoadModsAsync(source, cancellationToken);
            var candidates = source.Mods
                .Where(item => item.Enabled && BslProfilePolicy.Role(mods[item.ModId]) == BslModRole.None)
                .OrderBy(item => item.Order)
                .Select(item => new ModBinarySearchCandidate(item.ModId, mods[item.ModId].Name, item.Order))
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("源配置没有可参与二分的已启用普通模组。加载器、依赖和 SmoothBoot 保持原状态。");

            var temporary = await _profileService.DuplicateEnabledProfileSnapshotAsync(sourceProfileId,
                $"{source.Name} · 二分排查（临时） {DateTime.Now:HHmmss}", cancellationToken);
            if (!SameEnabledProfileContent(source, temporary))
            {
                await _profileService.DeleteProfileAsync(temporary.Id, cancellationToken);
                throw new InvalidOperationException("临时配置未完整保留源配置的启用模组，已放弃本次排查。");
            }

            _session = new Session
            {
                Source = source,
                SourceProfileId = source.Id,
                TemporaryProfileId = temporary.Id,
                AllCandidates = candidates,
                Candidates = candidates,
                ExcludedModNames = source.Mods
                    .Where(item => item.Enabled && BslProfilePolicy.Role(mods[item.ModId]) != BslModRole.None)
                    .OrderBy(item => item.Order)
                    .Select(item => mods[item.ModId].Name)
                    .ToList()
            };
            return new ModBinarySearchStartInfo(source.Id, source.Name, temporary.Id, temporary.Name,
                temporary.Mods.Count, candidates.Count, _session.ExcludedModNames);
        }
        finally { _gate.Release(); }
    }

    public async Task<ModBinarySearchTest> PrepareFirstTestAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            if (session.Stage != SessionStage.Started) throw new InvalidOperationException("当前排查阶段不能准备测试。");
            return await PrepareTestCoreAsync(session, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<ModBinarySearchStep> RecordObservationAsync(Guid testId, bool problemRemains,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            if (session.Stage != SessionStage.AwaitingObservation || session.CurrentTest?.Id != testId)
                throw new InvalidOperationException("当前测试尚未部署完成或已经处理。");

            var test = session.CurrentTest;
            session.History.Add(new ModBinarySearchHistory(test.Round, test.IsFinalVerification, problemRemains,
                test.DisabledCandidates.Select(item => item.Name).ToList(),
                (problemRemains ? test.DisabledCandidates : test.EnabledCandidates).Select(item => item.Name).ToList()));

            if (test.IsFinalVerification)
            {
                var suspect = problemRemains ? null : session.Candidates.Single();
                session.Stage = SessionStage.AwaitingCleanup;
                return new ModBinarySearchStep(null, new ModBinarySearchResult(!problemRemains, false, suspect,
                    session.History), session.History);
            }

            session.Candidates = problemRemains ? test.DisabledCandidates.ToList() : test.EnabledCandidates.ToList();
            if (session.Candidates.Count == 0)
                throw new InvalidOperationException("二分结果没有留下候选模组，请取消排查。");
            return new ModBinarySearchStep(await PrepareTestCoreAsync(session, cancellationToken), null, session.History);
        }
        finally { _gate.Release(); }
    }

    public async Task<ModBinarySearchResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            session.Stage = SessionStage.AwaitingCleanup;
            return new ModBinarySearchResult(false, true, null, session.History);
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureSourceUnchangedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            if (session.Stage != SessionStage.AwaitingCleanup)
                throw new InvalidOperationException("请先完成或取消二分排查。");
            var source = await _profileRepository.GetByIdAsync(session.SourceProfileId, cancellationToken);
            if (source == null || !SameProfileContent(session.Source, source))
                throw new InvalidOperationException("源配置在排查期间已改变。为避免覆盖，临时配置已保留，请先核对源配置。");
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteTemporaryProfileAsync(string? gamePath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = RequireSession();
            if (session.Stage != SessionStage.AwaitingCleanup)
                throw new InvalidOperationException("请先完成或取消二分排查。");
            var source = await _profileRepository.GetByIdAsync(session.SourceProfileId, cancellationToken);
            if (source == null || !SameProfileContent(session.Source, source))
                throw new InvalidOperationException("源配置在排查期间已改变，临时配置已保留。");
            if (!string.IsNullOrWhiteSpace(gamePath))
            {
                var records = await _deploymentRepository.GetCurrentDeploymentsAsync(gamePath, cancellationToken);
                if (records.Any(record => record.ProfileId == session.TemporaryProfileId))
                    throw new InvalidOperationException("临时配置仍有部署记录，已保留临时配置。请重试恢复原配置部署。");
            }
            await _profileService.DeleteProfileAsync(session.TemporaryProfileId, cancellationToken);
            _session = null;
        }
        finally { _gate.Release(); }
    }

    private async Task<ModBinarySearchTest> PrepareTestCoreAsync(Session session, CancellationToken cancellationToken)
    {
        var finalVerification = session.Candidates.Count == 1;
        var disabled = finalVerification ? session.Candidates : session.Candidates.Take(session.Candidates.Count / 2).ToList();
        var enabled = session.Candidates.Except(disabled).ToList();

        await _profileService.SetProfileModsEnabledWithoutNormalizingAsync(session.TemporaryProfileId,
            session.AllCandidates.Select(item => item.ModId).ToList(), true, cancellationToken);
        await _profileService.SetProfileModsEnabledWithoutNormalizingAsync(session.TemporaryProfileId,
            disabled.Select(item => item.ModId).ToList(), false, cancellationToken);

        var temporary = await _profileRepository.GetByIdAsync(session.TemporaryProfileId, cancellationToken)
            ?? throw new InvalidOperationException("临时配置不存在。");
        if (!SameEnabledMembers(session.Source, temporary))
            throw new InvalidOperationException("临时配置的启用模组数量、相对顺序或选项发生变化，已停止排查。");

        session.Round++;
        session.CurrentTest = new ModBinarySearchTest(Guid.NewGuid(), session.Round, finalVerification, disabled, enabled);
        session.Stage = SessionStage.AwaitingObservation;
        return session.CurrentTest;
    }

    private async Task<IReadOnlyDictionary<Guid, Mod>> LoadModsAsync(Profile profile, CancellationToken cancellationToken)
    {
        var ids = profile.Mods.Select(item => item.ModId).Distinct().ToArray();
        var mods = (await _modRepository.GetByIdsAsync(ids, cancellationToken)).ToDictionary(mod => mod.Id);
        if (mods.Count != ids.Length) throw new InvalidOperationException("源配置包含已不存在的模组，无法安全排查。");
        foreach (var mod in mods.Values)
            await BslProfilePolicy.EnsureDetectedAsync(mod, _modRepository, cancellationToken);
        return mods;
    }

    private static bool SameEnabledProfileContent(Profile source, Profile temporary)
        => SameEnabledMembers(source, temporary)
            && temporary.Mods.All(item => item.Enabled)
            && source.IsLuaGroupCollapsed == temporary.IsLuaGroupCollapsed
            && source.LuaSeparatorColor == temporary.LuaSeparatorColor;

    private static bool SameEnabledMembers(Profile source, Profile temporary)
    {
        var expected = source.Mods.Where(item => item.Enabled).OrderBy(item => item.Order).ToList();
        return temporary.Mods.Count == expected.Count
            && temporary.Mods.Select(item => item.Order).SequenceEqual(Enumerable.Range(0, temporary.Mods.Count))
            && expected.Select(item => (item.ModId, item.OptionsJson))
                .SequenceEqual(temporary.Mods.Select(item => (item.ModId, item.OptionsJson)));
    }

    private static bool SameProfileContent(Profile left, Profile right)
        => SameProfileOrderAndOptions(left, right)
            && left.Mods.OrderBy(item => item.Order).Select(item => (item.ModId, item.Enabled))
                .SequenceEqual(right.Mods.OrderBy(item => item.Order).Select(item => (item.ModId, item.Enabled)))
            && left.IsLuaGroupCollapsed == right.IsLuaGroupCollapsed
            && left.LuaSeparatorColor == right.LuaSeparatorColor
            && left.Separators.OrderBy(item => item.Order).Select(item =>
                (item.Name, item.Order, item.MemberCount, item.IsCollapsed, item.Color))
                .SequenceEqual(right.Separators.OrderBy(item => item.Order).Select(item =>
                    (item.Name, item.Order, item.MemberCount, item.IsCollapsed, item.Color)));

    private static bool SameProfileOrderAndOptions(Profile left, Profile right)
        => left.Mods.Count == right.Mods.Count
            && left.Mods.OrderBy(item => item.Order).Select(item => (item.ModId, item.Order, item.OptionsJson))
                .SequenceEqual(right.Mods.OrderBy(item => item.Order).Select(item => (item.ModId, item.Order, item.OptionsJson)));

    private Session RequireSession() => _session ?? throw new InvalidOperationException("当前没有模组二分排查。");

    private enum SessionStage { Started, AwaitingObservation, AwaitingCleanup }

    private sealed class Session
    {
        public required Profile Source { get; init; }
        public required Guid SourceProfileId { get; init; }
        public required Guid TemporaryProfileId { get; init; }
        public required List<ModBinarySearchCandidate> AllCandidates { get; init; }
        public required List<ModBinarySearchCandidate> Candidates { get; set; }
        public required List<string> ExcludedModNames { get; init; }
        public List<ModBinarySearchHistory> History { get; } = new();
        public ModBinarySearchTest? CurrentTest { get; set; }
        public int Round { get; set; }
        public SessionStage Stage { get; set; }
    }
}
