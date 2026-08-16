using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Upton.Pdm.SolidWorks;

internal static class PdmDocumentIdentityStore
{
    private const string StreamName = ":UptonPdm.DocumentId";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;

    public static bool TryRead(string filePath, out Guid documentId)
    {
        documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using (var handle = CreateFile(
                filePath + StreamName,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    return false;
                }

                using (var reader = new StreamReader(new FileStream(handle, FileAccess.Read), Encoding.ASCII, false))
                {
                    return Guid.TryParse(reader.ReadToEnd()?.Trim(), out documentId) && documentId != Guid.Empty;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryWrite(string filePath, Guid documentId)
    {
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        FileAttributes? originalAttributes = null;
        try
        {
            originalAttributes = File.GetAttributes(filePath);
            if ((originalAttributes.Value & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(filePath, originalAttributes.Value & ~FileAttributes.ReadOnly);
            }

            using (var handle = CreateFile(
                filePath + StreamName,
                GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                CreateAlways,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    return false;
                }

                using (var writer = new StreamWriter(new FileStream(handle, FileAccess.Write), Encoding.ASCII))
                {
                    writer.Write(documentId.ToString("D"));
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (originalAttributes.HasValue && File.Exists(filePath))
            {
                try
                {
                    File.SetAttributes(filePath, originalAttributes.Value);
                }
                catch
                {
                    // The identity stream is optional on file systems without writable attributes.
                }
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
