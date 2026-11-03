using System.Runtime.InteropServices;

namespace OfflineReShade.WinUI.Services;

// The in-process Common Item Dialog: the same Explorer-style dialog the WinRT pickers show through
// PickerHost.exe, but usable from elevated processes, where the brokered pickers fail with E_FAIL.
internal static class Win32FileDialog
{
    private const uint FosOverwritePrompt = 0x2, FosNoChangeDir = 0x8, FosPickFolders = 0x20, FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800, FosFileMustExist = 0x1000;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    // Returns the chosen file-system path, or null when the user cancels. Must run on an STA thread.
    public static string? Show(IntPtr owner, bool save, bool folder, string filterName, IReadOnlyList<string> extensions, string? suggestedName)
    {
        // The coclasses only gain IFileDialog through QueryInterface, so cast via object.
        var dialog = (IFileDialog)(save ? (object)new FileSaveDialogClass() : new FileOpenDialogClass());
        try
        {
            dialog.GetOptions(out var options);
            options |= FosForceFileSystem | FosNoChangeDir | FosPathMustExist;
            options |= folder ? FosPickFolders : save ? FosOverwritePrompt : FosFileMustExist;
            dialog.SetOptions(options);
            if (!folder && extensions.Count != 0)
            {
                var spec = string.Join(";", extensions.Select(static extension => "*" + extension));
                dialog.SetFileTypes(1, [new FilterSpec { Name = $"{filterName} ({spec})", Spec = spec }]);
                if (save) dialog.SetDefaultExtension(extensions[0].TrimStart('.'));
            }
            if (suggestedName != null) dialog.SetFileName(suggestedName);

            var result = dialog.Show(owner);
            if (result == ErrorCancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SigdnFileSysPath, out var path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass { }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogClass { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    // Declared in vtable order; only Show, SetFileTypes, SetOptions/GetOptions, SetFileName,
    // SetDefaultExtension and GetResult are called.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
