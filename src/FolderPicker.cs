using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// The real Windows Explorer "Select folder" window (IFileOpenDialog with FOS_PICKFOLDERS).
    public static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCo { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr spec);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr pfde, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem si);
            void AddPlace(IShellItem si, int where);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid g);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem si, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        /// Returns the chosen folder, or null if cancelled.
        public static string Pick(IWin32Window owner, string title, string startFolder)
        {
            try
            {
                IFileDialog dlg = (IFileDialog)new FileOpenDialogCo();
                try
                {
                    dlg.SetOptions(0x20 | 0x40 | 0x800); // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                    dlg.SetTitle(title);
                    dlg.SetOkButtonLabel(L.T("select_folder"));
                    if (!string.IsNullOrEmpty(startFolder) && System.IO.Directory.Exists(startFolder))
                    {
                        IShellItem start;
                        SHCreateItemFromParsingName(startFolder, IntPtr.Zero, typeof(IShellItem).GUID, out start);
                        if (start != null) dlg.SetFolder(start);
                    }
                    int hr = dlg.Show(owner != null ? owner.Handle : IntPtr.Zero);
                    if (hr != 0) return null;
                    IShellItem result;
                    dlg.GetResult(out result);
                    string path;
                    result.GetDisplayName(0x80058000, out path); // SIGDN_FILESYSPATH
                    return path;
                }
                finally { Marshal.ReleaseComObject(dlg); }
            }
            catch
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = title;
                    d.SelectedPath = startFolder;
                    return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
                }
            }
        }
    }
}
