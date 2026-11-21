using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static void ArchiveEarlyMetadataScenario()
    {
        var addinType = Type("PdmAddin");
        var addin = New("PdmAddin");
        var validate = addinType.GetMethod("ValidateArchiveMetadataBeforePreparation", Flags);
        var validateScope = addinType.GetMethod("ValidateArchiveScopeMetadataAsync", Flags);
        var checkBeforePreparation = addinType.GetMethod("PrepareArchiveScopeCoreAsync", Flags);
        var archiveAttempt = addinType.GetMethod("CheckInBatchAttemptAsync", Flags);
        if (validate == null || validateScope == null || checkBeforePreparation == null || archiveAttempt == null)
            throw new InvalidOperationException("找不到存档前字段校验入口。");

        const string supplementary = "\U00020000";
        var drawingAtLimit = new string('图', 254) + supplementary;
        var nameAtLimit = new string('名', 299) + supplementary;
        var nameOverLimit = nameAtLimit + "长";
        var validNew = ArchiveMetadataNode("VALID.SLDPRT", nameAtLimit);
        var existing = ArchiveMetadataNode("LEGACY.SLDPRT", nameOverLimit);
        Set(existing, "DocumentId", Guid.NewGuid());
        Set(existing, "DrawingNumber", drawingAtLimit + "长");
        var virtualFile = ArchiveMetadataNode("VIRTUAL.SLDPRT", nameOverLimit, "Virtual");
        var identities = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>)
            .MakeGenericType(typeof(string), Type("BatchDocumentIdentity")));
        identities.Add(Get(existing, "FullPath"), New("BatchDocumentIdentity", drawingAtLimit, nameAtLimit));

        var valid = LinkedItems(validNew, existing, virtualFile);
        validate.Invoke(addin, new object[] { valid, null, true });
        validate.Invoke(addin, new object[] { valid, identities, true });
        var project = Guid.NewGuid();
        ((Task)validateScope.Invoke(addin, new object[] { valid, project, CancellationToken.None })).GetAwaiter().GetResult();

        var longStem = "过长图号" + new string('图', 252) + ".STP";
        const string badDisplayNameFile = "LONG-NAME.SLDPRT";
        var invalidDrawing = ArchiveMetadataNode(longStem, "有效名称");
        Set(invalidDrawing, "Kind", Enum.Parse(Type("CadDocumentKind"), "Other"));
        Set(invalidDrawing, "Role", Enum.Parse(Type("CadReferenceRole"), "ExternalFileDependency"));
        var invalidName = ArchiveMetadataNode(badDisplayNameFile, nameOverLimit);
        var invalid = LinkedItems(invalidDrawing, invalidName, existing, virtualFile);
        var failure = MetadataFailure(() => validate.Invoke(addin, new object[] { invalid, null, true }));
        RequireEarlyMetadataFailure(failure, longStem, badDisplayNameFile);

        var longFileName = "文件名" + new string('件', 251) + ".SLDPRT";
        var invalidFile = ArchiveMetadataNode(longFileName, "有效名称");
        Set(invalidFile, "DocumentId", Guid.NewGuid());
        var invalidFileSelection = LinkedItems(invalidFile);
        var fileNameFailure = MetadataFailure(() => validate.Invoke(addin, new object[] { invalidFileSelection, null, true }));
        RequireArchiveFileNameFailure(fileNameFailure, longFileName);

        // A file-name error proves the flow rejected the batch before accessing either provider.
        var beforePreparation = MetadataFailure(() =>
            ((Task)checkBeforePreparation.Invoke(addin, new object[] { invalidFileSelection, project, CancellationToken.None, true }))
                .GetAwaiter().GetResult());
        RequireArchiveFileNameFailure(beforePreparation, longFileName);

        var attempt = MetadataFailure(() => ((Task)archiveAttempt.Invoke(addin, new object[]
        {
            invalidFileSelection, project, "隔离存档字符检查", null, CancellationToken.None, invalidFileSelection,
            null, null, null, null, null, false, null
        })).GetAwaiter().GetResult());
        RequireArchiveFileNameFailure(attempt, longFileName);

        identities[Get(existing, "FullPath")] = New("BatchDocumentIdentity", drawingAtLimit + "长", "有效名称");
        var explicitIdentity = MetadataFailure(() => validate.Invoke(addin, new object[] { valid, identities, true }));
        if (!(explicitIdentity is InvalidOperationException) || !explicitIdentity.Message.Contains("LEGACY.SLDPRT")
            || !explicitIdentity.Message.Contains("图号") || !explicitIdentity.Message.Contains("255"))
            throw new InvalidOperationException("提交的新图号超过长度时，已登记图档也必须提前指出该文件。", explicitIdentity);

        VerifyArchiveMetadataIdentityLookup(addin, validateScope, project, invalidName, badDisplayNameFile);
        Console.WriteLine("PASS: early archive metadata aggregates field failures, preserves existing identities and Unicode, and avoids native preparation");
    }

    static void VerifyArchiveMetadataIdentityLookup(object addin, MethodInfo validateScope, Guid project, object node, string fileName)
    {
        using (var handler = new ArchiveMetadataDirectoryHandler(project, fileName))
        using (var client = (IDisposable)New("PdmApiClient", "http://archive-metadata.invalid/"))
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://archive-metadata.invalid/") })
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", http);
            SetField(addin, "apiClient", client);

            var selected = LinkedItems(node);
            void ValidateScope() => ((Task)validateScope.Invoke(addin, new object[] { selected, project, CancellationToken.None }))
                .GetAwaiter().GetResult();

            ValidateScope();
            if (handler.DocumentRequests != 1 || Get(node, "DocumentId") != null)
                throw new InvalidOperationException("同名候选应保留给原有重复检查，不得绕过内容校验绑定身份。");

            handler.IncludeExisting = false;
            var absent = MetadataFailure(ValidateScope);
            if (!(absent is InvalidOperationException) || !absent.Message.Contains("尚未上传")
                || !absent.Message.Contains(fileName) || !absent.Message.Contains("名称")
                || !absent.Message.Contains("300") || !absent.Message.Contains("301") || handler.DocumentRequests != 2)
                throw new InvalidOperationException("目标项目没有同名图档时，首次登记的名称长度问题应在存档准备前阻止。", absent);
        }
    }

    sealed class ArchiveMetadataDirectoryHandler : HttpMessageHandler
    {
        readonly Guid project;
        readonly string fileName;
        public int DocumentRequests { get; private set; }
        public bool IncludeExisting { get; set; } = true;

        public ArchiveMetadataDirectoryHandler(Guid project, string fileName)
        {
            this.project = project;
            this.fileName = fileName;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException("存档字符预检不得写入服务器。");

            object response;
            if (request.RequestUri.AbsolutePath == "/api/projects/" + project + "/documents")
            {
                DocumentRequests++;
                response = IncludeExisting
                    ? new object[] { new { Id = Guid.NewGuid(), ProjectId = project, FileName = fileName.ToLowerInvariant(), Kind = 1 } }
                    : Array.Empty<object>();
            }
            else if (request.RequestUri.AbsolutePath == "/api/document-operation-rules")
                response = new { DisabledRuleIds = Array.Empty<string>() };
            else
                throw new InvalidOperationException("字符预检访问了额外接口。");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JavaScriptSerializer().Serialize(response))
            });
        }
    }

    static object ArchiveMetadataNode(string fileName, string displayName, string status = "Normal")
    {
        var node = New("CadTreeNode");
        Set(node, "FullPath", @"C:\ArchiveMetadataQa\" + fileName);
        Set(node, "FileName", fileName);
        Set(node, "DisplayName", displayName);
        Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
        Set(node, "Status", Enum.Parse(Type("CadReferenceStatus"), status));
        return node;
    }

    static Exception MetadataFailure(Action action)
    {
        try { action(); }
        catch (TargetInvocationException error) { return error.InnerException ?? error; }
        catch (Exception error) { return error; }
        return null;
    }

    static void RequireEarlyMetadataFailure(Exception error, string drawingFile, string nameFile)
    {
        if (!(error is InvalidOperationException) || !error.Message.Contains("尚未上传")
            || !error.Message.Contains(drawingFile) || !error.Message.Contains("图号") || !error.Message.Contains("255")
            || !error.Message.Contains(nameFile) || !error.Message.Contains("名称") || !error.Message.Contains("300")
            || error.Message.Contains("VIRTUAL.SLDPRT") || error.Message.Contains("LEGACY.SLDPRT"))
            throw new InvalidOperationException("存档前检查未汇总待登记图档的字段长度问题。", error);
    }

    static void RequireArchiveFileNameFailure(Exception error, string fileName)
    {
        if (!(error is InvalidOperationException) || !error.Message.Contains("尚未上传")
            || !error.Message.Contains(fileName) || !error.Message.Contains("文件名")
            || !error.Message.Contains("260") || !error.Message.Contains("261"))
            throw new InvalidOperationException("文件名超过长度时必须在连服务器及访问 SolidWorks 前指出。", error);
    }
}
