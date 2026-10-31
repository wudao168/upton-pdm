using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private int ownedReferenceResolutionDepth;

    private static void ClassifyReferenceLinks(CadTreeNode owner, NativeDesignDependencies.CheckInReferences evidence)
    {
        foreach (var link in owner.Children.Where(node => node.Role == CadReferenceRole.ExternalFileDependency))
        {
            var context = link.Kind == CadDocumentKind.Assembly
                && (evidence.Contexts.Contains(link.SourceReferencePath) || evidence.Contexts.Contains(link.FullPath));
            var document = evidence.Sources.Any(path => !evidence.UnverifiedSources.Contains(path)
                && (string.Equals(path, link.SourceReferencePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(path, link.FullPath, StringComparison.OrdinalIgnoreCase)));
            if (context && (link.Children.Count != 0 || link.ReferencedModels.Count != 0))
                throw new InvalidOperationException("上下文记录混入装配结构，需要重新读取引用：" + owner.FileName + " → " + link.FileName);
            link.IsContextReference = context;
            link.ReferenceTypeVerified = context || document;
            if (link.ReferenceTypeVerified) link.ReferenceTypeUnverified = false;
        }
    }

    private IModelDoc2 ResolveReferenceComponent(IComponent2 component, CadTreeNode owner)
    {
        var state = component.GetSuppression();
        if (state != 1) return component.GetModelDoc2() as IModelDoc2;
        if (Volatile.Read(ref nativeFileOpenInProgress) != 0)
            throw new InvalidOperationException("仍有文件加载未结束，未开始解析引用：" + owner.FileName);
        application.GetRunningCommandInfo(out var command, out var title, out var active);
        if (active || nativeUiCommandId != 0) throw new InvalidOperationException("当前命令尚未结束，未开始解析引用：" + owner.FileName);
        Interlocked.Increment(ref ownedReferenceResolutionDepth);
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            ArchiveSession.Current?.SetStage("正在按需解析候选引用：" + owner.FileName);
            var result = component.SetSuppression2(3); // Resolve this component, not its entire subtree.
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model == null || !PathsEqual(model.GetPathName(), owner.FullPath))
                throw new InvalidOperationException("候选引用解析未取得匹配模型，未继续存档：" + owner.FileName + "；状态=" + result);
            var epoch = nativeLoadEpoch;
            application.GetRunningCommandInfo(out command, out title, out active);
            var ready = application.StartupProcessCompleted;
            if (active || !ready || nativeUiCommandId != 0 || epoch != nativeLoadEpoch)
                throw new InvalidOperationException("候选引用解析后仍有原生命令或新加载，已保留现场：" + owner.FileName);
            // Only this owned native call can end the load notifications it produced.
            Interlocked.Exchange(ref nativeFileOpenInProgress, 0);
            nativeFileOpenCommandId = 0;
            unknownLoadOwner = null; unknownAddedEpoch = unknownSavedEpoch = 0;
            LogOperation("候选引用解析完成并核对加载状态：" + owner.FullPath + "；epoch=" + epoch);
            return model;
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Decrement(ref ownedReferenceResolutionDepth);
        }
    }

    private async Task CompleteCycleEvidenceAsync(IReadOnlyList<BatchOperationItem> items, CancellationToken token)
    {
        var groups = ArchiveReferenceCycles.Analyze(items);
        if (groups.Count == 0) return;
        var owners = groups.SelectMany(group => group.Edges).Where(edge => !edge.Verified)
            .Select(edge => edge.Owner).Distinct().ToArray();
        var session = ArchiveSession.Current;
        session?.SetStep(session.DisplayStep, "正在补充核对引用归属…", owners.Length);
        var checkedOwners = 0;
        PluginDiagnosticLog.Record("候选引用完整核对", "含环分组=" + groups.Count + "；待核对来源实例=" + owners.Length);
        var assembly = application.ActiveDoc as IAssemblyDoc;
        var components = new Dictionary<string, IComponent2>(StringComparer.OrdinalIgnoreCase);
        IComponent2 Find(string name)
        {
            if (assembly == null || string.IsNullOrWhiteSpace(name)) return null;
            IComponent2 current = null; var prefix = "";
            var segments = name.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[i] : prefix + "/" + segments[i];
                if (components.TryGetValue(prefix, out var cached)) { current = cached; continue; }
                current = i == 0 ? assembly.GetComponentByName(segments[i]) as IComponent2
                    : (current.GetChildren() as Array)?.OfType<IComponent2>().FirstOrDefault(child => string.Equals(child.Name2.Split('/').Last(), segments[i], StringComparison.OrdinalIgnoreCase));
                if (current == null) return null;
                components[prefix] = current;
            }
            return current;
        }
        foreach (var owner in owners)
        {
            session?.ReportFile(checkedOwners++, owners.Length, owner.FileName, "正在核对候选引用归属…");
            token.ThrowIfCancellationRequested();
            var model = application.GetOpenDocumentByName(owner.FullPath) as IModelDoc2;
            if (model == null)
            {
                var component = Find(owner.ComponentSelectionName);
                if (component != null && PathsEqual(component.GetPathName(), owner.FullPath)) model = ResolveReferenceComponent(component, owner);
            }
            if (model != null && PathsEqual(model.GetPathName(), owner.FullPath))
            {
                if (!string.IsNullOrWhiteSpace(owner.Configuration)
                    && !string.Equals(model.ConfigurationManager?.ActiveConfiguration?.Name, owner.Configuration, StringComparison.OrdinalIgnoreCase))
                {
                    PluginDiagnosticLog.Record("引用配置待核对", owner.FullPath + "；目标配置=" + owner.Configuration);
                    continue;
                }
                var evidence = NativeDesignDependencies.ReadCheckIn(model);
                ClassifyReferenceLinks(owner, evidence);
            }
            await Task.Yield();
        }
        ArchiveReferenceCycles.Validate(items);
        session?.ReportFile(owners.Length, owners.Length, null, "候选引用归属已核对");
    }
}
