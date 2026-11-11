using System.IO.Compression;
using System.Xml;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProjectFileFormService(IPdmRepository repository, IProjectFileFormRepository forms,
    ProjectFileService fileService, IProjectFileRepository files, IProjectFileFormWorkbook workbook, TimeProvider clock)
{
    public async Task<ProjectFileFormIndex> ListAsync(Guid projectId, string kind, string actor, UserRole role, CancellationToken ct)
    {
        var access = await RequireAsync(projectId, kind, "view", actor, role, ct);
        var project = access.Context.Project;
        var companyId = project.OrganizationId ?? access.Context.Root.OrganizationId;
        var receiving = kind == ProjectFileFormPermissions.Sample && access.CanEdit && companyId is Guid id
            ? (await repository.ReadCompanyBusinessDetailsAsync(id, "receiving", ct)).Receiving : null;
        return new(access.Folder.Id, project.Code, project.Name, project.OrganizationName ?? access.Context.Root.OrganizationName ?? "UPTON 阿普顿",
            project.CustomerName ?? access.Context.Root.CustomerName ?? "", access.CanEdit, access.CanArchive,
            await forms.ListAsync(projectId, kind, ct), receiving, access.CanExport);
    }

    public async Task<ProjectFileForm> ReadAsync(Guid projectId, string kind, Guid id, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(projectId, kind, "view", actor, role, ct);
        return ForView(await FindAsync(projectId, kind, id, ct));
    }

    public async Task<ProjectFileForm> SaveAsync(Guid projectId, string kind, Guid id, SaveProjectFileForm input,
        string actor, UserRole role, CancellationToken ct)
    {
        var access = await RequireAsync(projectId, kind, "edit", actor, role, ct);
        if (id == Guid.Empty || input.ExpectedRowVersion < 0 || input.Data is null) throw new PdmRuleException("单据资料无效，请刷新后重试。");
        var previous = await forms.FindAsync(projectId, kind, id, ct);
        var data = Normalize(kind, input.Data, previous?.Data);
        return ForView(await forms.SaveAsync(access.Context.Project, kind, id, input.ExpectedRowVersion, data, actor, ct));
    }

    public async Task<(byte[] Content, string FileName)> ExportAcceptanceAsync(Guid projectId, string kind, Guid id, string actor, UserRole role, CancellationToken ct)
    {
        if (!ProjectFileFormPermissions.IsAcceptance(kind)) throw new PdmNotFoundException("验收单类型不存在。");
        var access = await RequireAsync(projectId, kind, "view", actor, role, ct);
        if (!access.CanExport) throw new UnauthorizedAccessException("当前项目未授权导出验收单。");
        var form = await FindAsync(projectId, kind, id, ct);
        ValidateComplete(kind, form.Data);
        var draft = form with { Summary = form.Summary with { FileVersion = 0 } };
        var content = workbook.Build(kind, draft, access.Context.Project, access.Context.Root, access.Context.Directory, actor, clock.GetUtcNow(), null);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "project.file.form.export", nameof(ProjectFileForm),
            form.Summary.Id.ToString(), $"{form.Summary.Number} · 导出验收 Excel"), ct);
        return (content, FileName($"{form.Summary.Number}_{ProjectFileFormPermissions.Title(kind)}", ".xlsx"));
    }

    public async Task<ProjectFileFormSummary> ArchiveAsync(Guid projectId, string kind, Guid id, long revision,
        string actor, UserRole role, CancellationToken ct)
    {
        if (ProjectFileFormPermissions.IsAcceptance(kind))
            throw new PdmRuleException("请在质量页签的预/终验收中上传记录，原文件会自动归入验收资料。");
        var access = await RequireAsync(projectId, kind, "archive", actor, role, ct);
        var form = await FindAsync(projectId, kind, id, ct);
        if (form.Summary.RowVersion != revision) throw new PdmConflictException("单据已更新，请重新打开后生成存档。");
        var currentFile = await RequireActiveFileAsync(form.Summary.FileId, access.Folder, ct);
        var layoutVersion = ProjectFileFormIdentity.LayoutVersion(kind);
        if (form.Summary.ArchivedRevision == revision && form.Summary.ArchivedLayoutVersion >= layoutVersion && currentFile is not null) return form.Summary;
        ValidateComplete(kind, form.Data);
        var currentEvidence = await RequireActiveFileAsync(form.Summary.EvidenceFileId, access.Folder, ct);
        var projectCode = access.Context.Project.Code;
        var archiveNumber = kind == ProjectFileFormPermissions.Sample && form.Summary.Number == $"{projectCode}-YPXQ"
            ? projectCode : form.Summary.Number;
        var basename = $"{archiveNumber}_{ProjectFileFormPermissions.Title(kind)}";
        var name = currentFile?.FileName ?? FileName(basename, ".xlsx");
        var evidenceName = (form.Data.Evidence?.Count ?? 0) > 0 || currentEvidence is not null
            ? currentEvidence?.FileName ?? FileName(form.Summary.Number + "_确认附件", ".zip") : null;
        // The shared file repository increments the version from the locked current file.
        var export = form with { Summary = form.Summary with { FileVersion = (currentFile?.CurrentVersion?.VersionNumber ?? 0) + 1 } };
        var content = workbook.Build(kind, export, access.Context.Project, access.Context.Root, access.Context.Directory, actor, clock.GetUtcNow(), evidenceName);
        var output = await fileService.PrepareGeneratedFileAsync(projectId, access.Folder.Id, name, content, actor, role, ct);
        StoredProjectFileUpload? evidence = null;
        var attemptedCommit = false;
        try
        {
            if (evidenceName is not null)
                evidence = await fileService.PrepareGeneratedFileAsync(projectId, access.Folder.Id, evidenceName,
                    EvidenceZip(form.Data.Evidence ?? []), actor, role, ct);
            attemptedCommit = true;
            var result = await forms.ArchiveAsync(access.Context.Project, kind, id, revision, export.Summary.FileVersion, layoutVersion, output, evidence, actor, ct);
            if (!result.Added) await DiscardAsync();
            return result.Form;
        }
        catch (Exception error) when (!attemptedCommit || error is PdmConflictException or PdmNotFoundException or UnauthorizedAccessException)
        {
            // These failures are known to have occurred before the database commit.
            await DiscardAsync();
            throw;
        }

        async Task DiscardAsync()
        {
            await fileService.DiscardGeneratedFileAsync(output);
            if (evidence is not null) await fileService.DiscardGeneratedFileAsync(evidence);
        }
    }

    private async Task<ProjectFile?> RequireActiveFileAsync(Guid? id, ProjectFolder folder, CancellationToken ct)
    {
        if (id is null) return null;
        var file = await files.FindAsync(id.Value, ct);
        if (file is null || file.DeletedAt is not null || file.FolderId != folder.Id || file.RootProjectId != folder.RootProjectId)
            throw new PdmConflictException("原存档文件不在当前项目文件目录，请恢复或移回原文件后重试。");
        return file;
    }

    private async Task<ProjectFileForm> FindAsync(Guid projectId, string kind, Guid id, CancellationToken ct) =>
        await forms.FindAsync(projectId, kind, id, ct) ?? throw new PdmNotFoundException("当前项目中没有这张单据。");

    private async Task<FormAccess> RequireAsync(Guid projectId, string kind, string action, string actor, UserRole role, CancellationToken ct)
    {
        if (!ProjectFileFormPermissions.Kinds.Contains(kind)) throw new PdmNotFoundException("单据类型不存在。");
        var context = await AuthorizationPolicy.CreateContextAsync(repository, projectId, actor, ct)
            ?? throw new UnauthorizedAccessException("无权访问该项目的单据。");
        var decisions = AuthorizationPolicy.Decisions(await repository.GetAuthorizationSettingsAsync(ct), context);
        bool Allows(string operation) => decisions.Any(d => d.Permission == ProjectFileFormPermissions.Code(kind, operation) && d.Allowed);
        if (!Allows(action) || !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("当前账号没有该项目的单据操作权限。");
        var folder = (await repository.ListProjectFoldersAsync(projectId, actor, role, ct))
            .Where(f => f.TemplateKey == ProjectFileFormPermissions.FolderKey && f.Purpose == ProjectFolderPurpose.Standard
                && (f.TargetProjectId is null || f.TargetProjectId == projectId))
            .OrderByDescending(f => f.TargetProjectId == projectId).FirstOrDefault();
        if (folder is null || !folder.EffectiveAccess.HasFlag(FolderAccess.View))
            throw new UnauthorizedAccessException("当前项目的项目文件目录不可查看，请核对目录权限及模板。");
        var canArchive = !ProjectFileFormPermissions.IsAcceptance(kind) && Allows("archive") && folder.EffectiveAccess.HasFlag(FolderAccess.Upload);
        if (action == "archive" && !canArchive) throw new UnauthorizedAccessException("生成存档需要项目文件目录的上传权限。");
        var canExport = ProjectFileFormPermissions.IsAcceptance(kind) && decisions.Any(d => d.Permission == ProjectCenterPermissions.Export && d.Allowed);
        return new(context, folder, Allows("edit"), canArchive, canExport);
    }

    private sealed record FormAccess(AuthorizationPolicy.Context Context, ProjectFolder Folder, bool CanEdit, bool CanArchive, bool CanExport);

    private static ProjectFileForm ForView(ProjectFileForm form) => form with
    {
        Data = form.Data with { Evidence = form.Data.Evidence?.Select(file => file with { Content = null }).ToArray() }
    };

    private static ProjectFileFormData Normalize(string kind, ProjectFileFormData data, ProjectFileFormData? previous)
    {
        var bytes = 0L;
        var ids = new HashSet<Guid>();
        var savedFiles = (previous?.Evidence ?? []).Concat((previous?.Items ?? []).Select(item => item.Image).OfType<ProjectFormAttachment>())
            .ToDictionary(file => file.Id);
        ProjectFormAttachment Attachment(ProjectFormAttachment input, bool imageOnly)
        {
            if (input.Id == Guid.Empty || !ids.Add(input.Id)) throw new PdmRuleException("附件标识重复，请重新选择附件。");
            var file = input.Content is not null ? input : savedFiles.GetValueOrDefault(input.Id)
                ?? throw new PdmRuleException("原附件不存在，请重新选择文件。");
            var name = Field(file.FileName, "附件名称", 160);
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new PdmRuleException("附件名称无效，请重命名后重新选择。");
            if (file.Content is not { Length: > 0 and <= 2 * 1024 * 1024 } content)
                throw new PdmRuleException("单个图片或附件必须大于0且不超过2MB。");
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var valid = extension switch
            {
                ".png" => content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                ".jpg" or ".jpeg" => content.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
                ".pdf" => !imageOnly && content.AsSpan().StartsWith("%PDF-"u8),
                ".eml" => !imageOnly && !content.AsSpan().Contains((byte)0),
                _ => false
            };
            if (!valid) throw new PdmRuleException(imageOnly ? "样件图片须为有效的 PNG 或 JPEG。" : "确认附件须为 PNG、JPEG、PDF 或 EML 邮件。");
            bytes += content.Length;
            if (bytes > 8 * 1024 * 1024) throw new PdmRuleException("同一单据的附件总大小不能超过8MB。");
            return file with { FileName = name };
        }
        if (kind == ProjectFileFormPermissions.Sample)
        {
            if ((data.Items?.Count ?? 0) > 100) throw new PdmRuleException("样件需求最多100行。");
            return new(Recipient: Field(data.Recipient, "收货人", 80), Contact: Field(data.Contact, "联系方式", 100), Address: Field(data.Address, "收货地址", 300),
                Items: (data.Items ?? []).Select(item =>
                {
                    if (item is null) throw new PdmRuleException("样件明细无效，请重新编辑。");
                    var unit = Field(item.Unit, "单位", 12);
                    if (unit.Length == 0) unit = "件";
                    if (item.Quantity is decimal count && (count <= 0 || count > 1_000_000 || decimal.Round(count, 3) != count || IsCounted(unit) && decimal.Truncate(count) != count))
                        throw new PdmRuleException("样件数量须大于0且不超过100万。按件、台、套等计件时须填写整数，可拆分单位最多保留3位小数。");
                    var phase = Field(item.Phase, "适用阶段", 80);
                    if (phase is not ("" or "设计" or "装配" or "调试") && previous?.Items?.Any(saved => saved.Phase == phase) != true)
                        throw new PdmRuleException("适用阶段请选择设计、装配或调试。");
                    return new ProjectSampleItem(Field(item.MaterialName, "物料名称", 200), item.Quantity, unit, item.RequiredOn,
                        phase, Field(item.Note, "备注", 500), item.Image is null ? null : Attachment(item.Image, true));
                }).ToArray());
        }
        if (ProjectFileFormPermissions.IsAcceptance(kind))
        {
            var acceptance = data.Acceptance ?? new ProjectAcceptanceDetails();
            return new(Supplier: Field(data.Supplier, "供方", 200), Customer: Field(data.Customer, "需方", 200), ChangedOn: data.ChangedOn,
                Acceptance: new(Field(acceptance.ContractNumber, "合同编号", 120), Field(acceptance.Location, "验收地点", 300),
                    Field(acceptance.Attendees, "验收人员", 300), Field(acceptance.Standard, "验收标准", 2000),
                    Field(acceptance.Explanation, "验收说明", 2000), Field(acceptance.Corrections, "整改要求", 2000), acceptance.Passed,
                    Field(acceptance.Comments, "验收人意见", 3000), Field(acceptance.Terms, "验收约定", 3000)));
        }
        if ((data.Evidence?.Count ?? 0) > 8) throw new PdmRuleException("每张变更单最多添加8个确认附件。");
        return new(Supplier: Field(data.Supplier, "供方", 200), Customer: Field(data.Customer, "需方", 200), ChangedOn: data.ChangedOn,
            Reason: Field(data.Reason, "变更原因", 2000), Before: Field(data.Before, "变更前内容", 3000), After: Field(data.After, "变更后内容", 3000),
            Paid: data.Paid, Note: Field(data.Note, "备注", 2000),
            Evidence: (data.Evidence ?? []).Select(file => Attachment(file ?? throw new PdmRuleException("确认附件无效。"), false)).ToArray());
    }

    private static bool IsCounted(string unit) => unit is not ("米" or "毫米" or "厘米" or "千克" or "克" or "公斤" or "吨" or "升" or "毫升" or "平方米" or "立方米" or "m" or "mm" or "cm" or "kg" or "g" or "L");

    private static string Field(string? value, string label, int length)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > length) throw new PdmRuleException($"{label}最多{length}个字符。");
        try { return XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw new PdmRuleException($"{label}含有无法导出的字符，请修改后重试。"); }
    }

    private static void ValidateComplete(string kind, ProjectFileFormData data)
    {
        static bool Missing(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
        if (kind == ProjectFileFormPermissions.Sample)
        {
            if (Missing(data.Recipient, data.Contact, data.Address)) throw new PdmRuleException("请填写收货人、联系方式和收货地址。");
            if (data.Items is not { Count: > 0 } || data.Items.Any(item => Missing(item.MaterialName, item.Phase) || item.Quantity is null || item.RequiredOn is null))
                throw new PdmRuleException("请至少填写一条完整的样件需求，每行需有物料名称、数量、需求日期及适用阶段。");
        }
        else if (ProjectFileFormPermissions.IsAcceptance(kind))
        {
            if (Missing(data.Supplier, data.Customer)) throw new PdmRuleException("请填写供方和需方。");
        }
        else if (Missing(data.Supplier, data.Customer, data.Reason, data.Before, data.After) || data.ChangedOn is null || data.Paid is null)
            throw new PdmRuleException("请填写供方、需方、变更日期、变更原因、变更前后内容并选择是否付费。");
    }

    private static string FileName(string value, string extension) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' ? '_' : character)) + extension;

    private static byte[] EvidenceZip(IReadOnlyList<ProjectFormAttachment> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                using var entry = archive.CreateEntry($"{index + 1:D2}_{file.FileName}").Open();
                entry.Write(file.Content!);
            }
        }
        return output.ToArray();
    }
}
