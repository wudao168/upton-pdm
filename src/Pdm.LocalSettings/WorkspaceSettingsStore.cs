using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.LocalSettings
{
    internal sealed class WorkspaceSettings
    {
        public string WorkspaceRoot { get; set; } = string.Empty;
    }

    internal static class WorkspaceSettingsStore
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UPTON PDM");

        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "workspace-settings.json");

        public static string DefaultWorkspaceRoot => Path.Combine(SettingsDirectory, "Workspace");

        public static string GetWorkspaceRoot()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return DefaultWorkspaceRoot;
                }

                var settings = new JavaScriptSerializer().Deserialize<WorkspaceSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                return NormalizeWorkspaceRoot(settings == null ? string.Empty : settings.WorkspaceRoot);
            }
            catch
            {
                return DefaultWorkspaceRoot;
            }
        }

        public static string SaveWorkspaceRoot(string workspaceRoot)
        {
            var normalized = NormalizeWorkspaceRoot(workspaceRoot);
            Directory.CreateDirectory(normalized);
            VerifyWritable(normalized);
            Directory.CreateDirectory(SettingsDirectory);

            var temporaryPath = string.Concat(SettingsPath, ".new");
            try
            {
                var json = new JavaScriptSerializer().Serialize(new WorkspaceSettings { WorkspaceRoot = normalized });
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporaryPath, SettingsPath, null, true);
                }
                else
                {
                    File.Move(temporaryPath, SettingsPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return normalized;
        }

        private static string NormalizeWorkspaceRoot(string workspaceRoot)
        {
            var expanded = Environment.ExpandEnvironmentVariables((workspaceRoot ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return DefaultWorkspaceRoot;
            }
            if (!Path.IsPathRooted(expanded))
            {
                throw new InvalidDataException("本地工作区必须使用完整的绝对路径。");
            }

            var normalized = Path.GetFullPath(expanded);
            if (File.Exists(normalized))
            {
                throw new InvalidDataException("本地工作区路径指向了文件，请选择文件夹。");
            }
            var pathRoot = Path.GetPathRoot(normalized);
            return string.Equals(normalized, pathRoot, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void VerifyWritable(string workspaceRoot)
        {
            var probePath = Path.Combine(workspaceRoot, string.Concat(".upton-pdm-write-test-", Guid.NewGuid().ToString("N")));
            try
            {
                using (File.Create(probePath))
                {
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException || exception is IOException)
            {
                throw new IOException("所选本地工作区不可写，请选择有写入权限的文件夹。", exception);
            }
            finally
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
        }
    }
}
