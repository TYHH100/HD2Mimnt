namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>IAS 模型组件中的一个文件：源为模组库内绝对路径，RelativePath 为 bin/HD2IndependentRig 下的相对路径。</summary>
public sealed record IasModelFile(string SourcePath, string RelativePath);

/// <summary>模组库中的一个 IAS 模型组件（workflow 为 HD2IAS 的模组）及其 native 文件清单。</summary>
public sealed record IasModelComponent(Guid ModId, string ModName, IReadOnlyList<IasModelFile> Files);
