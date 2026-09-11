using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;

namespace AgentForUnity.Editor.Application
{
    internal static class AgentClipboardVideoCapture
    {
        private const long MaxVideoBytes = 512L * 1024 * 1024;

        // Only the managed copy is owned by the draft; the clipboard source is never removed.
        internal static bool TryCapture(string projectRoot, out string path, out string label,
            out bool foundVideo, out string error)
        {
            path = null;
            label = null;
            foundVideo = false;
            error = null;
            try
            {
                var source = ReadClipboardFile();
                if (source == null)
                    source = VideoPath(EditorGUIUtility.systemCopyBuffer);
                if (source != null)
                {
                    foundVideo = true;
                    var file = new FileInfo(source);
                    EnsureSize(file.Length);
                    path = CreateDestination(projectRoot, file.Extension);
                    File.Copy(source, path, false);
                    EnsureSize(new FileInfo(path).Length);
                    label = file.Name;
                    return true;
                }
#if UNITY_EDITOR_OSX
                var types = new[] { "public.mpeg-4", "com.apple.quicktime-movie", "public.movie" };
                var extensions = new[] { ".mp4", ".mov", ".mov" };
                for (var i = 0; i < types.Length; i++)
                {
                    var board = Send(objc_getClass("NSPasteboard"), Selector("generalPasteboard"));
                    var data = SendPointer(board, Selector("dataForType:"), NativeString(types[i]));
                    if (data == IntPtr.Zero)
                        continue;
                    foundVideo = true;
                    var length = SendUnsigned(data, Selector("length")).ToUInt64();
                    if (length > (ulong)MaxVideoBytes)
                        throw new InvalidOperationException("The video exceeds the 512 MiB attachment limit.");
                    EnsureSize((long)length);
                    var bytes = Send(data, Selector("bytes"));
                    if (bytes == IntPtr.Zero)
                        throw new InvalidOperationException("Clipboard video data is unavailable.");
                    path = CreateDestination(projectRoot, extensions[i]);
                    var buffer = new byte[1024 * 1024];
                    using (var stream = File.Create(path))
                    {
                        for (long offset = 0; offset < (long)length; offset += buffer.Length)
                        {
                            var count = (int)Math.Min(buffer.Length, (long)length - offset);
                            Marshal.Copy(new IntPtr(bytes.ToInt64() + offset), buffer, 0, count);
                            stream.Write(buffer, 0, count);
                        }
                    }
                    label = "Recording " + DateTime.Now.ToString("HH:mm:ss") + extensions[i];
                    return true;
                }
#endif
                error = "No video found in the clipboard. Copy a local recording file, then paste it here.";
                return false;
            }
            catch (Exception exception)
            {
                if (path != null)
                {
                    try { File.Delete(path); }
                    catch (Exception) { /* Preserve the original capture error. */ }
                }
                path = null;
                error = "Could not attach the clipboard recording: " + exception.Message;
                return false;
            }
        }

        private static void EnsureSize(long length)
        {
            if (length <= 0 || length > MaxVideoBytes)
                throw new InvalidOperationException(length <= 0
                    ? "The recording is empty."
                    : "The video exceeds the 512 MiB attachment limit.");
        }

        private static string CreateDestination(string projectRoot, string extension)
        {
            var directory = Path.Combine(Path.GetFullPath(projectRoot), "Library", "AgentForUnity", "Attachments");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "recording-" + Guid.NewGuid().ToString("N") + extension.ToLowerInvariant());
        }

        private static string VideoPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var candidate = value.Trim().Trim('"');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
                candidate = uri.LocalPath;
            if (!Path.IsPathRooted(candidate))
                return null;
            var extension = Path.GetExtension(candidate).ToLowerInvariant();
            switch (extension)
            {
                case ".mp4": case ".mov": case ".m4v": case ".webm": case ".mkv": case ".avi":
                    return candidate;
                default:
                    return null;
            }
        }

        private static string ReadClipboardFile()
        {
#if UNITY_EDITOR_OSX
            var board = Send(objc_getClass("NSPasteboard"), Selector("generalPasteboard"));
            var items = Send(board, Selector("pasteboardItems"));
            var count = Math.Min(SendUnsigned(items, Selector("count")).ToUInt64(), 64UL);
            for (ulong i = 0; i < count; i++)
            {
                var item = SendIndex(items, Selector("objectAtIndex:"), new UIntPtr(i));
                var url = SendPointer(item, Selector("stringForType:"), NativeString("public.file-url"));
                if (url == IntPtr.Zero)
                    continue;
                var pointer = Send(url, Selector("UTF8String"));
                if (pointer == IntPtr.Zero)
                    continue;
                var length = 0;
                while (Marshal.ReadByte(pointer, length) != 0)
                    length++;
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                var path = VideoPath(Encoding.UTF8.GetString(bytes));
                if (path != null)
                    return path;
            }
#elif UNITY_EDITOR_WIN
            const uint fileDrop = 15;
            if (!IsClipboardFormatAvailable(fileDrop))
                return null;
            if (!OpenClipboard(IntPtr.Zero))
                throw new InvalidOperationException("Windows denied access to the clipboard.");
            try
            {
                var handle = GetClipboardData(fileDrop);
                var count = Math.Min(DragQueryFile(handle, uint.MaxValue, null, 0), 64u);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFile(handle, i, null, 0);
                    var buffer = new StringBuilder((int)length + 1);
                    DragQueryFile(handle, i, buffer, (uint)buffer.Capacity);
                    var path = VideoPath(buffer.ToString());
                    if (path != null)
                        return path;
                }
            }
            finally { CloseClipboard(); }
#endif
            return null;
        }

#if UNITY_EDITOR_OSX
        private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
        private static IntPtr Selector(string name) => sel_registerName(name);
        private static IntPtr NativeString(string value)
        {
            var pointer = Marshal.StringToHGlobalAnsi(value);
            try { return SendPointer(objc_getClass("NSString"), Selector("stringWithUTF8String:"), pointer); }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        [DllImport(ObjectiveCLibrary)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjectiveCLibrary)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr value);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, UIntPtr index);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern UIntPtr SendUnsigned(IntPtr receiver, IntPtr selector);
#endif
#if UNITY_EDITOR_WIN
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder file, uint size);
#endif
    }
}
