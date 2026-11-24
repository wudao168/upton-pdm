using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static async Task CompactStagingBindingScenario(string output)
    {
        string instance = Guid.NewGuid().ToString("N");
        string workspace = Path.Combine(Path.GetFullPath(output), "workspace");
        string external = Path.Combine(Path.GetFullPath(output), "source");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(external);
        var identity = Type("PdmDocumentIdentityStore");
        var pathRules = Type("ManagedCadPaths");
        var managerType = Type("ControlledWorkspaceManager");
        var binding = identity.GetField("bindingDirectory", Flags);
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        object originalBinding = binding.GetValue(null), originalResolver = resolver.GetValue(null);
        binding.SetValue(null, Path.Combine(output, "bindings"));
        resolver.SetValue(null, new Func<string>(() => workspace));

        object Invoke(MethodInfo method, object target, params object[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        object Id(string method, params object[] args) => Invoke(identity.GetMethod(method, Flags), null, args);
        object Read(string path)
        {
            var args = new object[] { path, null };
            return (bool)Id("TryReadLocalMetadata", args) ? args[1] : null;
        }
        void Expect(bool passed, string text)
        {
            if (!passed) throw new InvalidOperationException(text);
            Console.WriteLine("PASS: " + text);
        }
        string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        // The stage is chosen by the installed path policy. Only this newly allocated
        // token directory may be removed; all document bindings live in the fixture.
        string compactRoot = (string)pathRules.GetProperty("CompactWorkspaceRoot", Flags).GetValue(null);
        string stage = (string)Invoke(pathRules.GetMethod("ChooseStagingDirectory", Flags), null,
            Path.Combine(workspace, new string('X', 240)), instance, new[] { "PART.SLDASM" });
        string expectedStage = (string)Invoke(pathRules.GetMethod("CompactDirectory", Flags), null, compactRoot, "s", instance, null);
        if (!string.Equals(stage, expectedStage, StringComparison.OrdinalIgnoreCase) || Directory.Exists(stage))
            throw new InvalidOperationException("Fixture requires a fresh, generated, machine-compact staging directory.");
        Directory.CreateDirectory(stage);
        try
        {
            var project = Guid.NewGuid();
            var doc = Guid.NewGuid();
            var ver = Guid.NewGuid();
            byte[] archive = Encoding.UTF8.GetBytes("immutable source body");
            string archiveHash = Digest(archive);
            string shared = Path.Combine(external, "COMMON.SLDASM");
            File.WriteAllBytes(shared, archive);
            var otherProject = Guid.NewGuid();
            var otherDoc = Guid.NewGuid();
            Expect((bool)Id("TryWriteControlledVersion", shared, doc, project, ver, "W1", archiveHash, (long)archive.Length)
                && (bool)Id("TryWriteControlledVersion", shared, otherDoc, otherProject, ver, "W1", archiveHash, (long)archive.Length),
                "Two selected projects can store their own receipts for the same external source");
            object ArchiveReceipt(Guid projectId)
            {
                var args = new object[] { shared, projectId, null };
                return (bool)Id("TryReadArchiveMetadata", args) ? args[2] : null;
            }
            Expect(Read(shared) == null && (Guid)Get(ArchiveReceipt(project), "DocumentId") == doc
                && (Guid)Get(ArchiveReceipt(otherProject), "DocumentId") == otherDoc && ArchiveReceipt(Guid.NewGuid()) == null,
                "External source provenance stays separate by project and does not confer a local working identity");
            foreach (var mode in new[] { "resolved", "native", "archive" })
            {
                byte[] actual = mode == "archive" ? archive : Encoding.UTF8.GetBytes("projection-" + mode);
                string actualHash = Digest(actual), name = mode + ".SLDASM";
                string donor = Path.Combine(workspace, "donor-" + mode), target = Path.Combine(workspace, "target-" + mode);
                Directory.CreateDirectory(donor);
                string sourcePath = Path.Combine(donor, name), stagePath = Path.Combine(stage, name);
                File.WriteAllBytes(sourcePath, actual);
                var manifest = New("ControlledOpenManifestDto");
                Set(manifest, "Id", Guid.NewGuid()); Set(manifest, "ProjectId", project); Set(manifest, "ProjectCode", "QA-STAGING-" + instance);
                Set(manifest, "RootDocumentId", doc); Set(manifest, "RootVersionId", ver); Set(manifest, "RootRelativePath", name);
                var file = New("ControlledOpenFileDto");
                Set(file, "DocumentId", doc); Set(file, "VersionId", ver); Set(file, "FileName", name); Set(file, "RelativePath", name);
                Set(file, "Revision", "W1"); Set(file, "FileLength", (long)archive.Length); Set(file, "Sha256", archiveHash); Set(file, "IsRoot", true);
                var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
                files.Add(file); Set(manifest, "Files", files);
                var snapshot = Guid.NewGuid();
                if (!(bool)Id("TryWriteControlledVersion", sourcePath, doc, project, ver, "W1", archiveHash, (long)archive.Length))
                    throw new IOException("Cannot prepare fixture donor.");
                if (mode == "native")
                {
                    var isolation = New("NativeFileIsolationDto");
                    Set(isolation, "SourceVersionId", ver); Set(isolation, "SourceSha256", archiveHash);
                    Set(isolation, "Sha256", actualHash); Set(isolation, "Length", (long)actual.Length); Set(isolation, "ReferenceSnapshotId", snapshot);
                    Set(file, "NativeIsolation", isolation); Set(file, "IsNativeIsolated", true);
                    if (!(bool)Id("TryRecordNativeIsolation", sourcePath, Read(sourcePath), snapshot, actualHash)) throw new IOException("Fixture isolation record failed.");
                }
                if (mode == "resolved" && !(bool)Id("TryRecordResolvedReferences", sourcePath, Read(sourcePath), actualHash))
                    throw new IOException("Fixture reference record failed.");
                using (var client = (IDisposable)New("PdmApiClient", "http://regression.invalid/"))
                using (var http = new HttpClient(new CompactBindingOfflineHandler()) { BaseAddress = new Uri("http://regression.invalid/") })
                {
                    SetField(client, "httpClient", http);
                    var manager = New("ControlledWorkspaceManager", client, workspace);
                    var verified = (Dictionary<Guid, string>)managerType.GetField("verifiedSnapshotDirectories", Flags).GetValue(manager);
                    verified[doc] = donor;
                    await (Task)Invoke(managerType.GetMethod("DownloadManifestFilesAsync", Flags), manager, manifest, stage, CancellationToken.None, null);
                    var stamp = Read(stagePath);
                    Expect(stamp != null && (Guid)Get(stamp, "ProjectId") == project && (Guid)Get(stamp, "DocumentId") == doc
                        && (Guid)Get(stamp, "VersionId") == ver && (string)Get(stamp, "Sha256") == archiveHash
                        && (mode == "archive" || (string)Get(stamp, "ResolvedReferenceSha256") == actualHash)
                        && (mode != "native" || (Guid)Get(stamp, "NativeIsolationSnapshotId") == snapshot),
                        mode + " cache copied to the exact compact stage retains its complete controlled origin");
                    Expect(!(bool)Id("IsProjectWorkspacePath", workspace, (string)Get(manifest, "ProjectCode"), stagePath),
                        mode + " compact staging identity cannot become an editable project workspace");
                    // Only this stage is now offered as a source to the real update.
                    verified[doc] = stage;
                    string root = await (Task<string>)Invoke(managerType.GetMethod("UpdateWorkingCacheAsync", Flags),
                        manager, manifest, target, Array.Empty<Guid>(), CancellationToken.None, null);
                    var finalStamp = Read(root);
                    var matches = Type("ControlledReferenceMaterializer").GetMethod("Matches", Flags);
                    Expect(root == Path.Combine(target, name) && finalStamp != null && (Guid)Get(finalStamp, "VersionId") == ver
                        && (Guid)Get(finalStamp, "ProjectId") == project && (bool)Invoke(matches, null, root, file)
                        && File.ReadAllBytes(root).SequenceEqual(actual),
                        mode + " complete workspace update recovers the verified bytes and exact version from the compact stage");
                    File.SetAttributes(stagePath, FileAttributes.Normal);
                    File.WriteAllBytes(stagePath, Encoding.UTF8.GetBytes("changed after verification"));
                    Expect(!(bool)Invoke(matches, null, stagePath, file), mode + " changed stage bytes cannot reuse a previous proof");
                }
            }
        }
        finally
        {
            binding.SetValue(null, originalBinding); resolver.SetValue(null, originalResolver);
            foreach (var path in Directory.GetFiles(stage)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(stage, true);
        }
    }

    sealed class CompactBindingOfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Unexpected download: the fixture must use only its proven local cache.");
    }
}
