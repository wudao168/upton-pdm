using System;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    internal async Task RequireUnverifiedReferencesSupportAsync(CancellationToken token)
    {
        var capabilities = await GetJsonAsync<System.Collections.Generic.Dictionary<string, bool>>("api/archive-capabilities", token);
        if (capabilities == null || !capabilities.TryGetValue("unverifiedReferences", out var supported) || !supported)
            throw new InvalidOperationException("服务器尚不支持保留待核对引用，请先更新服务器；原文件和引用已保留，尚未上传。");
    }
    internal async Task RefreshArchiveWarningPolicyAsync(CancellationToken token)
    {
        var scope = EditJournalScope;
        try
        {
            var policy = await GetJsonAsync<ArchiveWarningPolicy>("api/archive-warning-policy", token);
            if (scope != EditJournalScope) throw new InvalidOperationException("登录范围已变化，请重新开始存档。");
            ArchiveWarningGate.Configure(policy);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("存档警告策略读取失败", error.ToString());
            throw new InvalidOperationException("未能读取管理员存档警告策略，请检查服务器连接或插件版本后重试；原图档和修改均已保留。", error);
        }
    }
}
