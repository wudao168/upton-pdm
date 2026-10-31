using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

// Local work only: never part of the server manifest or an immutable version.
internal sealed class RetainedLocalReference
{
    internal string SourcePath, WorkspacePath, Sha256;
    internal long FileLength;
    internal bool Managed, Editable;
    internal string FileName => Path.GetFileName(SourcePath);
    internal IEnumerable<string> Paths => new[] { SourcePath, WorkspacePath }.Where(path => !string.IsNullOrWhiteSpace(path));
    internal bool Allows(string path) => Paths.Any(candidate => string.Equals(Path.GetFullPath(path), candidate, StringComparison.OrdinalIgnoreCase));
    internal bool Matches(string path) => Allows(path) && File.Exists(path) && new FileInfo(path).Length == FileLength
        && string.Equals(ArchiveFileFingerprint.Read(path), Sha256, StringComparison.OrdinalIgnoreCase);
}
