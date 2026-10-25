using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

internal static partial class Program
{
    private static void LiveWorkflow(Assembly plugin, SldWorks app, IModelDoc2 initialRoot, string copies, string qa)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(copies, "qa-live.json")));
        var project = Guid.Parse((string)config["projectId"]); var rootId = Guid.Parse((string)config["rootDocumentId"]);
        var url = (string)config["apiBaseUrl"]; var token = (string)config["token"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback) throw new IOException("联调只允许隔离回环API。");
        object New(string name, params object[] args) => Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks." + name, true), Members & ~BindingFlags.Static, null, args, null);
        void Set(object value, string name, object data) => value.GetType().GetProperty(name, Members).SetValue(value, data);
        void Field(object value, string name, object data) => value.GetType().GetField(name, Members).SetValue(value, data);
        object Call(object value, string name, params object[] args)
        {
            var method = value.GetType().GetMethod(name, Members);
            return method.Invoke(value, args.Concat(method.GetParameters().Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
        }
        object Wait(object value) { var task = (Task)value; Pump(task); return task.GetType().GetProperty("Result")?.GetValue(task); }
        var addin = New("PdmAddin"); var client = New("PdmApiClient", url);
        foreach (var name in new[] { "httpClient", "controlledOpenHttpClient" })
            ((HttpClient)client.GetType().GetField(name, Members).GetValue(client)).DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var inspect = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(10) };
        inspect.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Dictionary<string, object> Snapshot(string label)
        {
            var text = inspect.GetStringAsync("/qa/snapshot").GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(qa, label + ".json"), text);
            return json.Deserialize<Dictionary<string, object>>(text);
        }
        Dictionary<Guid, int> Counts(Dictionary<string, object> snapshot) => ((IEnumerable)snapshot["versions"]).Cast<Dictionary<string, object>>()
            .GroupBy(v => Guid.Parse((string)v["documentId"])).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, object>[] ReviewRows(string label)
        {
            var text = inspect.GetStringAsync("/api/projects/" + project + "/drawing-review-candidates").GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(qa, label + "-review-candidates.json"), text);
            return json.Deserialize<Dictionary<string, object>[]>(text);
        }
        var workspace = New("ControlledWorkspaceManager", client, Path.Combine(qa, "workspace"));
        using var host = new Form { Opacity = 0, ShowInTaskbar = false };
        using var pane = (Control)New("PdmTaskPaneControl", "真实装配与隔离API联调");
        host.Controls.Add(pane); var handle = host.Handle; pane.CreateControl();
        var previous = SynchronizationContext.Current;
        var settings = plugin.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore", true).GetField("SettingsPath", Members);
        var previousSettings = settings.GetValue(null);
        var settingsPath = Path.Combine(qa, "workspace-settings.json");
        File.WriteAllText(settingsPath, json.Serialize(new { WorkspaceRoot = Path.Combine(qa, "workspace") })); settings.SetValue(null, settingsPath);
        var identities = plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore", true);
        identities.GetField("bindingDirectory", Members).SetValue(null, Path.Combine(qa, "bindings"));
        identities.GetField("workspaceRootResolver", Members).SetValue(null, new Func<string>(() => Path.Combine(qa, "workspace")));
        var journalScope = (string)Get(client, "EditJournalScope") + "|admin";
        Field(addin, "application", app); Field(addin, "apiClient", client); Field(addin, "controlledWorkspace", workspace);
        Field(addin, "authenticatedUsername", "admin"); Field(addin, "currentProjectId", project); Field(addin, "taskPaneControl", pane);
        Field(addin, "lifetime", new CancellationTokenSource()); Field(addin, "scanner", New("SolidWorksReferenceTreeScanner", app));
        Field(addin, "editJournalScope", journalScope); Field(addin, "editJournal", New("EditDraftJournal", Path.Combine(qa, "drafts"), journalScope));
        plugin.GetType("Upton.Pdm.SolidWorks.ArchiveUploadJournal", true).GetField("directory", Members).SetValue(null, Path.Combine(qa, "uploads"));
        plugin.GetType("Upton.Pdm.SolidWorks.ArchiveBatchJournal", true).GetField("directory", Members).SetValue(null, Path.Combine(qa, "batches"));
        var results = new List<object>();
        app.UserControlBackground = true;
        Console.WriteLine("LIVE COM userControl=" + app.UserControl + " background=" + app.UserControlBackground + " visible=" + app.Visible);
        try
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if (config.TryGetValue("readback", out var readback))
            {
                var proof = (Dictionary<string, object>)readback;
                var modelId = Guid.Parse((string)proof["modelId"]);
                var proofDrawingId = Guid.Parse((string)proof["drawingId"]);
                var mode = Enum.Parse(plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenMode", true), "LatestReadOnly");
                if (!(bool)Wait(Call(addin, "ExecuteControlledOpenAsync", project, proofDrawingId, mode, null, null, null, false)))
                    throw new IOException("冷启动后从PLM重新获取最新工程图失败。");
                var finalManifest = Wait(Call(client, "CreateControlledOpenManifestAsync", proofDrawingId, null, false, false, CancellationToken.None));
                var readDirectory = (string)Call(workspace, "GetReadOnlyDirectory", finalManifest, false);
                var readFiles = ((IEnumerable)Get(finalManifest, "Files")).Cast<object>().ToArray();
                var drawingPath = Path.Combine(readDirectory, (string)Get(readFiles.Single(file => Equals(Get(file, "DocumentId"), proofDrawingId)), "RelativePath"));
                var modelPath = Path.Combine(readDirectory, (string)Get(readFiles.Single(file => Equals(Get(file, "DocumentId"), modelId)), "RelativePath"));
                var drawing = app.GetOpenDocumentByName(drawingPath) as IDrawingDoc;
                if (drawing == null) throw new IOException("未找到刚从PLM打开的只读工程图窗口：" + drawingPath);
                var views = ((object[])drawing.GetViews()).OfType<object[]>().SelectMany(sheet => sheet.Skip(1)).OfType<IView>()
                    .Where(view => view.ReferencedDocument != null).ToArray();
                if (views.Length == 0 || views.Any(view => !string.Equals(((IModelDoc2)view.ReferencedDocument).GetPathName(), modelPath, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("冷启动取回的2D未引用已存档的目标零件。");
                var model = app.GetOpenDocumentByName(modelPath) as IModelDoc2;
                var dimension = EditableDimensions(model).Single(value => value.FullName == (string)proof["dimensionName"]);
                if (Math.Abs(dimension.SystemValue - Convert.ToDouble(proof["expectedValue"])) > 1e-9)
                    throw new IOException("冷启动后PLM工程图引用的模型尺寸不是最终存档值。");
                Report["liveReadback"] = new { passed = true, modelId, drawingId = proofDrawingId, actualDimension = dimension.SystemValue,
                    expectedDimension = proof["expectedValue"], dimensionName = dimension.FullName,
                    modelConfiguration = LiveModelConfigurationEvidence(model), drawingViewConfigurations = LiveDrawingConfigurationEvidence(drawing),
                    views = views.Length, latestDrawingFetchedThroughPlm = true };
                Console.WriteLine("LIVE READBACK PASS：新进程从PLM重新获取2D，所有视图均引用最终存档尺寸。");
                return;
            }
            var manifest = Wait(Call(client, "CreateControlledOpenManifestAsync", rootId, null, false, false, CancellationToken.None));
            Call(addin, "RememberControlledOpenManifest", manifest, copies);
            var files = ((IEnumerable)Get(manifest, "Files")).Cast<object>().ToArray();
            var relations = ((IEnumerable)Wait(Call(client, "GetDocumentRelationsAsync", project, CancellationToken.None))).Cast<object>().ToArray();
            var candidates = json.Deserialize<Dictionary<string, object>[]>(inspect.GetStringAsync("/api/projects/" + project + "/drawing-review-candidates").GetAwaiter().GetResult());
            var reviewable = new HashSet<Guid>(candidates.Where(candidate => Equals(candidate["selectable"], true) && candidate["modelDocumentId"] != null)
                .Select(candidate => Guid.Parse((string)candidate["modelDocumentId"])));
            object targetFile = null, drawingFile = null, selectedModelConfiguration = null; string dimensionName = null;
            var requestedTarget = config.TryGetValue("targetModelFileName", out var targetName) ? targetName as string : null;
            foreach (var relation in relations)
            {
                if (!reviewable.Contains((Guid)Get(relation, "ModelDocumentId"))) continue;
                var candidate = files.FirstOrDefault(f => Equals(Get(f, "DocumentId"), Get(relation, "ModelDocumentId"))
                    && ((string)Get(f, "FileName")).EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase));
                if (candidate == null || relations.Count(r => Equals(Get(r, "ModelDocumentId"), Get(relation, "ModelDocumentId"))) != 1) continue;
                if (!string.IsNullOrWhiteSpace(requestedTarget)
                    && !string.Equals((string)Get(candidate, "FileName"), requestedTarget, StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(copies, (string)Get(candidate, "RelativePath"));
                var errors = 0; var warnings = 0;
                var model = app.GetOpenDocumentByName(path) as IModelDoc2 ?? app.OpenDoc6(path, 1, 3, "", ref errors, ref warnings);
                if (model == null || errors != 0) continue;
                var dimension = EditableDimensions(model).FirstOrDefault(d => d.SystemValue > 0 && d.SystemValue < 10);
                if (dimension == null) continue;
                targetFile = candidate; dimensionName = dimension.FullName;
                selectedModelConfiguration = LiveModelConfigurationEvidence(model);
                drawingFile = files.Single(f => Equals(Get(f, "DocumentId"), Get(relation, "DrawingDocumentId")));
                break;
            }
            if (targetFile == null) throw new IOException("大装配中没有找到带唯一工程图及可修改真实尺寸的零件。");
            var targetId = (Guid)Get(targetFile, "DocumentId"); var drawingId = (Guid)Get(drawingFile, "DocumentId");
            var expected = new HashSet<Guid> { targetId, drawingId };
            double? previousDimensionValue = null;
            Report["liveTarget"] = new { model = Get(targetFile, "FileName"), drawing = Get(drawingFile, "FileName"), targetId, drawingId, dimensionName,
                modelConfigurationAtSelection = selectedModelConfiguration };
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var before = Snapshot("cycle" + cycle + "-before");
                var beforeCounts = Counts(before);
                var beforeReview = ReviewRows("cycle" + cycle + "-before");
                Console.WriteLine("LIVE cycle=" + cycle + " 获取真实PLM编辑副本：" + Get(targetFile, "FileName"));
                var mode = Enum.Parse(plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenMode", true), "LatestEdit");
                if (!(bool)Wait(Call(addin, "ExecuteControlledOpenAsync", project, rootId, mode, null, targetId, new[] { targetId }, false)))
                    throw new IOException("真实API获取编辑副本失败：" + plugin.GetType("Upton.Pdm.SolidWorks.PluginDiagnosticLog", true).GetMethod("Snapshot", Members).Invoke(null, null));
                manifest = Wait(Call(client, "CreateControlledOpenManifestAsync", rootId, null, false, false, CancellationToken.None));
                var rootPath = (string)Call(workspace, "GetWorkingFilePath", manifest, rootId);
                var partPath = (string)Call(workspace, "GetWorkingFilePath", manifest, targetId);
                var drawingPath = (string)Call(workspace, "GetWorkingFilePath", manifest, drawingId);
                var originalDrawingHash = Hash(drawingPath);
                var errors = 0; var warnings = 0;
                var part = app.GetOpenDocumentByName(partPath) as IModelDoc2 ?? app.OpenDoc6(partPath, 1, 1, "", ref errors, ref warnings);
                var parent = app.GetOpenDocumentByName(rootPath) as IModelDoc2;
                if (part == null || part.IsOpenedReadOnly() || parent == null || !parent.IsOpenedReadOnly()) throw new IOException("编辑权限未限定到零件；父装配须保持只读。");
                var dimension = EditableDimensions(part).Single(d => d.FullName == dimensionName);
                var modelConfigurationBeforeChange = LiveModelConfigurationEvidence(part);
                var oldValue = dimension.SystemValue;
                if (previousDimensionValue.HasValue && Math.Abs(oldValue - previousDimensionValue.Value) > 1e-9)
                    throw new IOException("再次从PLM获取后，模型没有保留上一轮已存档的真实尺寸。");
                var oldVolume = (part.Extension.CreateMassProperty() as IMassProperty)?.Volume;
                Console.WriteLine("LIVE GEOMETRY cycle=" + cycle + " dimension=" + dimensionName + " old=" + oldValue);
                dimension.SystemValue = oldValue * 1.03;
                var newValue = dimension.SystemValue;
                var modelConfigurationAfterChange = LiveModelConfigurationEvidence(part);
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-dimension-configuration.json"), json.Serialize(new {
                    cycle, dimensionName, oldValue, newValue, modelConfigurationBeforeChange, modelConfigurationAfterChange }));
                if (Math.Abs(newValue - oldValue) < 1e-9) throw new IOException("实际尺寸没有改变，不能以属性变更代替几何验收。");
                part.ForceRebuild3(false);
                var newVolume = (part.Extension.CreateMassProperty() as IMassProperty)?.Volume;
                if (!part.Save3(1, ref errors, ref warnings) || errors != 0) throw new IOException("真实零件保存失败：" + errors);
                Console.WriteLine("LIVE GEOMETRY SAVED cycle=" + cycle + " new=" + newValue + " volume=" + oldVolume + " -> " + newVolume);
                var scanner = New("SolidWorksReferenceTreeScanner", app);
                Console.WriteLine("LIVE SCAN cycle=" + cycle);
                var root = Call(scanner, "ScanDocument", parent, true);
                plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true).GetMethod("RestorePersistedDocumentIdentities", Members).Invoke(null, new[] { root });
                var inventory = Call(scanner, "AddNativeAssociatedDrawings", parent, root);
                plugin.GetType("Upton.Pdm.SolidWorks.NativeArchiveManifest", true).GetMethod("ConfirmFileScope", Members).Invoke(null, new[] { root, inventory });
                plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true).GetMethod("RestorePersistedDocumentIdentities", Members).Invoke(null, new[] { root });
                Field(addin, "currentTree", root);
                var documents = ((IEnumerable)Wait(Call(client, "GetDocumentsAsync", project, CancellationToken.None))).Cast<object>().ToArray();
                foreach (var node in Flatten(root))
                {
                    var stored = documents.FirstOrDefault(d => Equals(Get(d, "Id"), Get(node, "DocumentId")));
                    if (stored == null) continue;
                    Call(addin, "ApplyCheckoutDocument", node, stored);
                    Set(node, "StoredVersionStateKnown", true); Set(node, "HasStoredVersion", true);
                }
                Field(addin, "currentTree", root); Call(pane, "SetTree", root);
                var allItems = plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true).GetMethod("BuildBatchOperationItems", Members).Invoke(null, new[] { root });
                Console.WriteLine("LIVE SCOPE cycle=" + cycle);
                allItems = Wait(Call(addin, "PrepareArchiveScopeAsync", allItems, project, CancellationToken.None));
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-scope.json"), json.Serialize(((IEnumerable)allItems).Cast<object>().Select(item => Get(item, "Node"))
                    .Select(node => new { name = Get(node, "FileName"), path = Get(node, "FullPath"), id = Get(node, "DocumentId"), dirty = Get(node, "IsModifiedInSolidWorks"), role = Get(node, "Role").ToString() })));
                Wait(Call(addin, "PreloadBatchCheckInDocumentsAsync", allItems, null, CancellationToken.None));
                if ((bool)Wait(Call(addin, "EnsureBatchCheckInSavedStateAsync", allItems, project, CancellationToken.None)))
                    allItems = Wait(Call(addin, "PrepareArchiveScopeAsync", allItems, project, CancellationToken.None));
                var plan = Wait(Call(addin, "BuildBatchCheckInPlanAsync", allItems, (Action<string>)(stage => Console.WriteLine("LIVE PLAN " + stage)), CancellationToken.None));
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-plan-before-drawing-update.json"), json.Serialize(((IEnumerable)Get(plan, "Items")).Cast<object>().Select(item => Get(item, "Node"))
                    .Select(node => new { name = Get(node, "FileName"), id = Get(node, "DocumentId"), role = Get(node, "Role").ToString(),
                        currentRevision = Get(node, "CurrentRevision"), revision = Get(node, "Revision"), requiresModelUpdate = Get(node, "RequiresModelUpdate"),
                        structureIndexPending = Get(node, "StructureIndexPending"), archiveReadError = Get(node, "ArchiveReadError"),
                        scope = Get(node, "DrawingScopePaths"), references = ((IEnumerable)Get(node, "ReferencedModels")).Cast<object>().Select(reference => new {
                            name = Get(reference, "FileName"), id = Get(reference, "DocumentId"), path = Get(reference, "FullPath"), role = Get(reference, "Role").ToString(),
                            configuration = Get(reference, "Configuration"), revision = Get(reference, "Revision"), currentRevision = Get(reference, "CurrentRevision"),
                            openedRevision = Get(reference, "OpenedRevision"), openedVersionId = Get(reference, "OpenedVersionId") }) })));
                plan = Wait(Call(addin, "UpdateLinkedDrawingsAsync", plan, project, CancellationToken.None));
                var planned = ((IEnumerable)Get(plan, "Items")).Cast<object>().Select(item => Get(item, "Node")).ToArray();
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-plan.json"), json.Serialize(planned.Select(node => new { name = Get(node, "FileName"), id = Get(node, "DocumentId"),
                    path = Get(node, "FullPath"),
                    revision = Get(node, "Revision"), currentRevision = Get(node, "CurrentRevision"), openedVersionId = Get(node, "OpenedVersionId"), openedRevision = Get(node, "OpenedRevision"),
                    pending = Get(node, "DrawingUpdatePendingReason") })));
                if (!new HashSet<Guid>(planned.Select(node => (Guid)Get(node, "DocumentId"))).SetEquals(expected)) throw new IOException("整套存档计划遗漏配套2D或误包含无关图档；见plan.json。");
                if (Hash(drawingPath) == originalDrawingHash) throw new IOException("配套2D没有实际更新并保存。");
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-drawing-bytes.json"), json.Serialize(new {
                    cycle, drawingPath, originalDrawingHash, updatedDrawingHash = Hash(drawingPath),
                    sourceDrawingPath = Path.Combine(copies, (string)Get(drawingFile, "RelativePath")),
                    sourceDrawingHash = Hash(Path.Combine(copies, (string)Get(drawingFile, "RelativePath"))) }));
                var result = Wait(Call(addin, "CheckInBatchAsync", Get(plan, "Items"), project, "隔离验收第" + cycle + "轮：真实尺寸与配套2D更新", null, CancellationToken.None,
                    null, null, null, Get(plan, "Preflights")));
                if (((IEnumerable)Get(result, "Failures")).Cast<object>().Any()) throw new IOException("真实API存档失败：" + json.Serialize(Get(result, "Failures")));
                var after = Snapshot("cycle" + cycle + "-after"); var afterCounts = Counts(after);
                var advanced = new HashSet<Guid>(afterCounts.Where(pair => pair.Value != beforeCounts[pair.Key]).Select(pair => pair.Key));
                if (!advanced.SetEquals(expected) || expected.Any(id => afterCounts[id] != beforeCounts[id] + 1)) throw new IOException("服务器版本有遗漏或无关文件误升版。");
                var afterReview = ReviewRows("cycle" + cycle + "-after");
                var changedReview = afterReview.First(row => (string)row["modelDocumentId"] == targetId.ToString());
                if (config.TryGetValue("expectFormalChange", out var formal) && Equals(formal, true)
                    && !Equals(changedReview["hasUnreleasedDrawingChanges"], true)) throw new IOException("正式版修改后，PLM没有标记图纸修改状态。");
                foreach (var row in beforeReview.Where(row => (string)row["modelDocumentId"] != targetId.ToString()))
                {
                    var next = afterReview.Single(candidate => Equals(candidate["modelDocumentId"], row["modelDocumentId"])
                        && Equals(candidate["drawingDocumentId"], row["drawingDocumentId"]) && Equals(candidate["configuration"], row["configuration"])
                        && Equals(candidate["drawingNumber"], row["drawingNumber"]));
                    foreach (var key in new[] { "modelRevision", "drawingRevision", "hasUnreleasedDrawingChanges" })
                        if (!Equals(row[key], next[key])) throw new IOException("无关图纸被误标为变更：" + row["drawingNumber"] + "；" + key);
                }
                // A new UI operation rebuilds its selection from the current tree and
                // prepares native state again; do not reuse a previous operation's nodes.
                var repeatItems = plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true).GetMethod("BuildBatchOperationItems", Members)
                    .Invoke(null, new[] { addin.GetType().GetField("currentTree", Members).GetValue(addin) });
                repeatItems = Wait(Call(addin, "PrepareArchiveScopeAsync", repeatItems, project, CancellationToken.None));
                Wait(Call(addin, "PreloadBatchCheckInDocumentsAsync", repeatItems, null, CancellationToken.None));
                if ((bool)Wait(Call(addin, "EnsureBatchCheckInSavedStateAsync", repeatItems, project, CancellationToken.None)))
                    repeatItems = Wait(Call(addin, "PrepareArchiveScopeAsync", repeatItems, project, CancellationToken.None));
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-repeat-scope.json"), json.Serialize(((IEnumerable)repeatItems).Cast<object>()
                    .Select(item => Get(item, "Node")).Select(node => new { name = Get(node, "FileName"), path = Get(node, "FullPath"), id = Get(node, "DocumentId") })));
                var repeatPlan = Wait(Call(addin, "BuildBatchCheckInPlanAsync", repeatItems, null, CancellationToken.None));
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-repeat-plan.json"), json.Serialize(((IEnumerable)Get(repeatPlan, "Items")).Cast<object>().Select(item => Get(item, "Node"))
                    .Select(node => new { name = Get(node, "FileName"), id = Get(node, "DocumentId"), revision = Get(node, "CurrentRevision"),
                        path = Get(node, "FullPath"),
                        dirty = Get(node, "IsModifiedInSolidWorks"), workState = Get(node, "WorkState").ToString(), checkout = Get(node, "CheckedOutBy"),
                        requiresModelUpdate = Get(node, "RequiresModelUpdate"), references = ((IEnumerable)Get(node, "ReferencedModels")).Cast<object>()
                            .Select(reference => new { id = Get(reference, "DocumentId"), revision = Get(reference, "CurrentRevision"), configuration = Get(reference, "Configuration") }) })));
                repeatPlan = Wait(Call(addin, "UpdateLinkedDrawingsAsync", repeatPlan, project, CancellationToken.None));
                var repeatResult = Wait(Call(addin, "CheckInBatchAsync", Get(repeatPlan, "Items"), project, "隔离验收无变化重复存档", null, CancellationToken.None,
                    null, null, null, Get(repeatPlan, "Preflights")));
                File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-repeat-result.json"), json.Serialize(repeatResult));
                var repeatedCounts = Counts(Snapshot("cycle" + cycle + "-unchanged-repeat"));
                if (Convert.ToInt32(Get(repeatResult, "CreatedVersions")) != 0 || ((IEnumerable)Get(repeatResult, "Failures")).Cast<object>().Any())
                    throw new IOException("模型和2D更新后再次存档，出现重复升版或失败。");
                if (afterCounts.Any(pair => repeatedCounts[pair.Key] != pair.Value)) throw new IOException("无变化重复存档推进了服务器版本。");
                var drawing = app.GetOpenDocumentByName(drawingPath) as IModelDoc2 ?? app.OpenDoc6(drawingPath, 3, 3, "", ref errors, ref warnings);
                var views = ((IDrawingDoc)drawing).GetViews() as object[];
                var loaded = views.OfType<object[]>().SelectMany(sheet => sheet.Skip(1)).OfType<IView>().Where(view => view.ReferencedDocument != null).ToArray();
                if (loaded.Length == 0 || loaded.Any(view => !string.Equals(((IModelDoc2)view.ReferencedDocument).GetPathName(), partPath, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("重新打开2D时没有引用本次修改的模型。");
                using (var response = inspect.PostAsync("/qa/check-change", new StringContent(json.Serialize(new { modelId = targetId, drawingId, cycle }), System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult())
                {
                    var verification = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(qa, "cycle" + cycle + "-review.json"), verification);
                    if (!response.IsSuccessStatusCode) throw new IOException("修改状态或真实发起审批校验失败：" + verification);
                }
                previousDimensionValue = newValue;
                results.Add(new { cycle, dimensionName, oldValue, newValue, oldVolume, newVolume,
                    modelConfigurationBeforeChange, modelConfigurationAfterChange, modelConfigurationAfterDrawingUpdate = LiveModelConfigurationEvidence(part),
                    drawingViewConfigurations = LiveDrawingConfigurationEvidence((IDrawingDoc)drawing),
                    drawingChanged = true, drawingViews = loaded.Length, advancedDocuments = advanced.ToArray(), unrelatedVersionsUnchanged = true,
                    unchangedRepeatCreatesNoVersions = true, plmModifiedState = changedReview["hasUnreleasedDrawingChanges"], unrelatedChangeFlagsUnchanged = true,
                    CreatedVersions = Get(result, "CreatedVersions"), skipped = Get(plan, "SkippedFiles") });
                File.WriteAllText(Path.Combine(qa, "live-cycles.json"), json.Serialize(results));
                Console.WriteLine("LIVE PASS cycle=" + cycle + "：真实尺寸变化，2D已更新，仅目标模型和2D新增存档。");
            }
            Report["liveCycles"] = results; Report["realIsolatedApi"] = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(qa, "plugin-diagnostics.txt"), Convert.ToString(plugin.GetType("Upton.Pdm.SolidWorks.PluginDiagnosticLog", true)
                .GetMethod("Snapshot", Members).Invoke(null, null)));
            SynchronizationContext.SetSynchronizationContext(previous); settings.SetValue(null, previousSettings);
            ((IDisposable)client).Dispose();
        }
    }

    // Read-only diagnostics. A failed evidence read is recorded without changing
    // the existing native workflow, target selection or acceptance assertions.
    private static object LiveModelConfigurationEvidence(IModelDoc2 model)
    {
        try { return new { documentPath = model?.GetPathName(), activeConfiguration = model?.ConfigurationManager?.ActiveConfiguration?.Name, error = (string)null }; }
        catch (Exception error) { return new { documentPath = (string)null, activeConfiguration = (string)null, error = error.ToString() }; }
    }

    private static object LiveDrawingConfigurationEvidence(IDrawingDoc drawing)
    {
        var records = new List<object>();
        try
        {
            var sheets = drawing.GetViews() as object[] ?? Array.Empty<object>();
            for (var sheetIndex = 0; sheetIndex < sheets.Length; sheetIndex++)
            {
                var views = sheets[sheetIndex] as object[] ?? Array.Empty<object>();
                for (var viewIndex = 1; viewIndex < views.Length; viewIndex++)
                {
                    if (!(views[viewIndex] is IView view)) continue;
                    try { records.Add(new { sheetIndex, viewIndex, viewName = view.GetName2(), referencedConfiguration = view.ReferencedConfiguration,
                        referencedModel = LiveModelConfigurationEvidence(view.ReferencedDocument as IModelDoc2), error = (string)null }); }
                    catch (Exception error) { records.Add(new { sheetIndex, viewIndex, error = error.ToString() }); }
                }
            }
            return new { views = records, error = (string)null };
        }
        catch (Exception error) { return new { views = records, error = error.ToString() }; }
    }

    private static IEnumerable<IDimension> EditableDimensions(IModelDoc2 model)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var feature = model.FirstFeature() as IFeature; feature != null; feature = feature.GetNextFeature() as IFeature)
            for (var display = feature.GetFirstDisplayDimension() as IDisplayDimension; display != null; display = feature.GetNextDisplayDimension(display) as IDisplayDimension)
                if (display.GetDimension2(0) is IDimension dimension && seen.Add(dimension.FullName)) yield return dimension;
    }
}
