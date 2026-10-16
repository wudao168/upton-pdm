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

        public Guid Id { get; }
        public string Revision { get; }
        private readonly string sha256;
        private readonly string sourceSha256;

        public bool MatchesFile(string hash) => !string.IsNullOrWhiteSpace(hash)
            && ((!string.IsNullOrWhiteSpace(sha256) && string.Equals(sha256, hash, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(sourceSha256) && string.Equals(sourceSha256, hash, StringComparison.OrdinalIgnoreCase)));
    }
}
