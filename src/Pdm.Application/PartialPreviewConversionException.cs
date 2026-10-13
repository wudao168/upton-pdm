using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>单项转换失败时仍交付已验证的成功产物，由发布服务保存并分别记录结果。</summary>
public sealed class PartialPreviewConversionException(
    IReadOnlyDictionary<Guid, DocumentPreviewArtifact> artifacts,
    IReadOnlyDictionary<Guid, string> errors,
    string message) : Exception(ItemErrorPrefix + message)
{
    public const string ItemErrorPrefix = "逐项转图结果：\n";
    public IReadOnlyDictionary<Guid, DocumentPreviewArtifact> Artifacts { get; } = artifacts;
    public IReadOnlyDictionary<Guid, string> Errors { get; } = errors;
}
