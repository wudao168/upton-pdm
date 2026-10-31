using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // This cache belongs to one check-in attempt. Only immutable, committed version
    // identities/content hashes are retained; current document/permission state is not.
    private sealed class BatchReferenceVersionCache
    {
        private readonly Guid projectId;
        private readonly Guid sessionId;
        private readonly string scope;
        private readonly string username;
        private readonly Dictionary<Guid, IReadOnlyList<ArchivedReferenceVersion>> histories =
            new Dictionary<Guid, IReadOnlyList<ArchivedReferenceVersion>>();
        private readonly Dictionary<Guid, IReadOnlyList<ArchivedReferenceVersion>> summaries =
            new Dictionary<Guid, IReadOnlyList<ArchivedReferenceVersion>>();
        private readonly HashSet<string> verifiedBindings = new HashSet<string>(StringComparer.Ordinal);
        private bool legacyServer;

        public BatchReferenceVersionCache(Guid projectId, Guid sessionId, string scope, string username)
        {
            this.projectId = projectId;
            this.sessionId = sessionId;
            this.scope = scope;
            this.username = username;
        }

        public void ValidateScope(Guid project, Guid session, string serverScope, string user)
        {
            if (projectId != project || sessionId != session
                || !string.Equals(scope, serverScope, StringComparison.Ordinal)
                || !string.Equals(username, user, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("存档项目、账号或编辑会话已变化，请重新检查后再存档。");
        }

        public async Task<IReadOnlyList<ArchivedReferenceVersion>> GetHistoryAsync(
            Guid documentId, PdmApiClient client, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (histories)
                if (histories.TryGetValue(documentId, out var cached)) return cached;

            var versions = await client.GetVersionsAsync(documentId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Do not retain mutable API DTOs or a notion of the "latest" revision.
            var snapshot = Array.AsReadOnly(versions.Select(version => new ArchivedReferenceVersion(version)).ToArray());
            lock (histories) histories[documentId] = snapshot;
            return snapshot;
        }

        public IReadOnlyList<ArchivedReferenceVersion> KnownVersions(Guid id) =>
            histories.TryGetValue(id, out var full) ? full : summaries.TryGetValue(id, out var known) ? known : Array.Empty<ArchivedReferenceVersion>();

        public async Task PrimeReferencesAsync(IReadOnlyList<CadTreeNode> nodes, PdmApiClient client,
            CancellationToken token, Action<int, int, string, string> report)
        {
            var requests = nodes.GroupBy(node => node.DocumentId.Value).SelectMany(group => group
                .Select(node => node.OpenedVersionId == Guid.Empty ? null : node.OpenedVersionId).Distinct()
                .Select(bound => new DocumentVersionVerificationRequestDto { DocumentId = group.Key, BoundVersionId = bound }))
                .Where(request => !histories.ContainsKey(request.DocumentId) && !verifiedBindings.Contains(Key(request))).ToList();
            var total = requests.Count; var completed = 0;
            while (requests.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                // Each server request has distinct document IDs. A second bound
                // version of the same file is checked in a subsequent batch.
                var batch = requests.GroupBy(request => request.DocumentId).Select(group => group.First()).Take(100).ToArray();
                report?.Invoke(completed, total, string.Empty, "正在批量核对已存引用版本（不上传引用文件）…");
                var result = legacyServer ? null : await client.VerifyReferenceVersionsAsync(batch, token);
                if (result == null)
                {
                    legacyServer = true;
                    foreach (var request in batch) await GetHistoryAsync(request.DocumentId, client, token);
                }
                else
                {
                    if (result.Count != batch.Length || result.Any(row => row == null) || result.Select(row => row.DocumentId).Distinct().Count() != batch.Length
                        || result.Any(row => row == null || !batch.Any(request => request.DocumentId == row.DocumentId)
                            || !row.Accessible || new[] { row.Latest, row.Bound }.Where(version => version != null)
                                .Any(version => version.DocumentId != row.DocumentId || version.Id == Guid.Empty || string.IsNullOrWhiteSpace(version.Sha256)
                                    || string.IsNullOrWhiteSpace(version.Revision))
                            || row.Bound != null && row.Bound.Id != batch.First(request => request.DocumentId == row.DocumentId).BoundVersionId))
                        throw new InvalidOperationException("装配引用的版本或查看权限未通过批量核对，尚未上传，请刷新项目后重试。");
                    foreach (var row in result)
                    {
                        var known = KnownVersions(row.DocumentId);
                        summaries[row.DocumentId] = Array.AsReadOnly(known.Concat(new[] { row.Latest, row.Bound }
                            .Where(version => version != null).Select(version => new ArchivedReferenceVersion(version)))
                            .GroupBy(version => version.Id).Select(group => group.First()).ToArray());
                        foreach (var version in new[] { row.Latest, row.Bound }.Where(version => version != null))
                            verifiedBindings.Add(row.DocumentId + "|" + version.Id);
                    }
                }
                foreach (var request in batch) { verifiedBindings.Add(Key(request)); requests.Remove(request); }
                completed += batch.Length;
            }
            if (total > 0) LogOperation("引用版本批量核对完成 bindings=" + total + " completeHistories=" + histories.Count + " legacy=" + legacyServer);
        }

        private static string Key(DocumentVersionVerificationRequestDto request) => request.DocumentId + "|" + request.BoundVersionId;
    }

    private sealed class ArchivedReferenceVersion
    {
        public ArchivedReferenceVersion(DocumentVersionDto version)
        {
            Id = version.Id;
            Revision = version.Revision?.Display;
            sha256 = version.Sha256;
            sourceSha256 = VersionSourceSha256(version);
        }

        public ArchivedReferenceVersion(DocumentVersionVerificationDto version)
        {
            Id = version.Id; Revision = version.Revision; sha256 = version.Sha256; sourceSha256 = version.SourceFileSha256;
        }

        public Guid Id { get; }
        public string Revision { get; }
        private readonly string sha256;
        private readonly string sourceSha256;

        public bool MatchesFile(string hash, string path = null) => !string.IsNullOrWhiteSpace(hash)
            && ((!string.IsNullOrWhiteSpace(sha256) && string.Equals(sha256, hash, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(sourceSha256) && string.Equals(sourceSha256, hash, StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrWhiteSpace(path) && PdmDocumentIdentityStore.MatchesResolvedVersion(path, Id, sha256, hash));
    }
}
