using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WhopClipper.Api.App.Services;

/// <summary>
/// Sends a file to the Windows Recycle Bin through the shell. Every dialog is switched off: this runs in
/// the windowless backend, where a confirmation or error box would hang the request unseen.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    /// <summary>True once the file is in the Recycle Bin; false leaves it where it was.</summary>
    public static bool TrySend(string fullPath)
    {
        if (!File.Exists(fullPath)) return false;
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(fullPath) + "\0\0", // the shell reads a double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        return SHFileOperation(ref operation) == 0 && !operation.fAnyOperationsAborted && !File.Exists(fullPath);
    }
}
