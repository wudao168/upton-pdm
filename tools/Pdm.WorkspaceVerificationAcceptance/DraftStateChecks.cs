using Upton.Pdm.SolidWorks;

internal static partial class Program
{
    private static void VerifyDraftState(string root)
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid(); var version = Guid.NewGuid();
        var folder = Path.Combine(root, "View", "DRAFT"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "draft.SLDPRT"); File.WriteAllText(source, "archived");
        var originalHash = Hash(source);
        PdmDocumentIdentityStore.TryWriteControlledVersion(source, id, project, version, "W1", originalHash, new FileInfo(source).Length);
        File.WriteAllText(source, "saved uncommitted edit");
        var journal = new EditDraftJournal(Path.Combine(root, "draft-journal"), "server|company|tester");
        var entry = new LocalEditDraft { DocumentId=id,ProjectId=project,SessionId=Guid.NewGuid(),SourcePath=source,
            BaseVersionId=version,BaseSha256=originalHash };
        journal.Preserve(entry,source,true);
        File.SetAttributes(source,FileAttributes.ReadOnly);
        var request = new WorkspaceDocumentStateRequest { DocumentId=id, FileName="draft.SLDPRT", LatestRevision="W1" };
        WorkspaceLocalFileState Read(EditDraftJournal selected) => WorkspaceLocalStateReader.Read(root,project,"DRAFT","tester",new[]{request},selected).Items.Single();
        var state=Read(journal);
        Check(state.LocalState=="Modified" && state.LocalStateLabel=="待恢复草稿" && state.IsReadOnly,
            "已验证只读恢复草稿显示待恢复，不冒充已存档或损坏");
        PdmDocumentIdentityStore.TryReadControlledMetadata(source,out var metadata);
        Check(metadata.Sha256==originalHash && metadata.VersionId==version,"草稿状态判断保留真实归档基准");
        Check(Read(new EditDraftJournal(Path.Combine(root,"draft-journal"),"other-server|company|tester")).LocalState=="IntegrityMismatch",
            "其他服务器或账号的草稿不能掩盖缓存不一致");
        File.SetAttributes(source,FileAttributes.Normal);File.WriteAllText(source,"archived");
        var unsaved=Path.Combine(root,"unsaved.SLDPRT");File.WriteAllText(unsaved,"unsaved memory snapshot");
        journal.Preserve(entry,unsaved,true);File.SetAttributes(source,FileAttributes.ReadOnly);
        Check(Read(journal).LocalStateLabel=="待恢复草稿","磁盘仍是存档版时也能显示已保留的未保存草稿");
        File.SetAttributes(source,FileAttributes.Normal);File.WriteAllText(source,"saved uncommitted edit");File.SetAttributes(source,FileAttributes.ReadOnly);
        File.WriteAllText(entry.DraftPath,"corrupted backup");
        Check(Read(journal).LocalState=="IntegrityMismatch","损坏的草稿不能被认作有效恢复副本");
        journal.MarkDiscarded(entry);
        Check(Read(journal).LocalState=="NeedsUpdate" && Read(journal).LocalStateLabel=="待恢复存档",
            "明确放弃的修改显示待恢复存档，不再当作可继续草稿");
        File.SetAttributes(source,FileAttributes.Normal);
    }
}
