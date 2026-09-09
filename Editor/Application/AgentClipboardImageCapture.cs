using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentForUnity.Editor.Application
{
    internal static class AgentClipboardImageCapture
    {
        private const int MaxImageBytes = 25 * 1024 * 1024;
        private const string AttachmentDirectoryName = "Attachments";

        internal static bool TryCapture(string projectRoot, out string path, out string error)
        {
            path = null;
            error = null;

#if UNITY_EDITOR_OSX
            try
            {
                var data = ReadPasteboardData("public.png");
                if (data == IntPtr.Zero)
                {
                    var tiffData = ReadPasteboardData("public.tiff");
                    data = ConvertTiffToPng(tiffData);
                }

                if (data == IntPtr.Zero)
                {
                    error = "The clipboard does not contain a screenshot or image.";
                    return false;
                }

                var bytes = CopyData(data);
                var directory = GetAttachmentDirectory(projectRoot);
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, "screenshot-" + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // Preserve the original clipboard capture error.
                    }
                }

                path = null;
                error = "Could not attach the clipboard screenshot: " + exception.Message;
                return false;
            }
#else
            error = "Clipboard screenshots are currently supported on macOS only.";
            return false;
#endif
        }

        internal static bool IsManagedAttachmentPath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var directory = Path.GetFullPath(GetAttachmentDirectory(projectRoot))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(path);
                return candidate.StartsWith(directory, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetAttachmentDirectory(string projectRoot)
        {
            return Path.Combine(Path.GetFullPath(projectRoot), "Library", "AgentForUnity", AttachmentDirectoryName);
        }

#if UNITY_EDITOR_OSX
        private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
        private const ulong PngBitmapFileType = 4;

        private static IntPtr ReadPasteboardData(string dataType)
        {
            var pasteboardClass = objc_getClass("NSPasteboard");
            var pasteboard = Send(pasteboardClass, Selector("generalPasteboard"));
            var dataTypePointer = Marshal.StringToHGlobalAnsi(dataType);
            try
            {
                var type = SendPointer(
                    objc_getClass("NSString"),
                    Selector("stringWithUTF8String:"),
                    dataTypePointer);
                return SendPointer(pasteboard, Selector("dataForType:"), type);
            }
            finally
            {
                Marshal.FreeHGlobal(dataTypePointer);
            }
        }

        private static IntPtr ConvertTiffToPng(IntPtr tiffData)
        {
            if (tiffData == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var representation = Send(objc_getClass("NSBitmapImageRep"), Selector("alloc"));
            representation = SendPointer(representation, Selector("initWithData:"), tiffData);
            if (representation == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                var properties = Send(objc_getClass("NSDictionary"), Selector("dictionary"));
                return SendUnsignedPointer(
                    representation,
                    Selector("representationUsingType:properties:"),
                    new UIntPtr(PngBitmapFileType),
                    properties);
            }
            finally
            {
                SendVoid(representation, Selector("release"));
            }
        }

        private static byte[] CopyData(IntPtr data)
        {
            var length = SendUnsigned(data, Selector("length")).ToUInt64();
            if (length == 0 || length > MaxImageBytes)
            {
                throw new InvalidOperationException(
                    length == 0
                        ? "The clipboard image is empty."
                        : "The clipboard image exceeds the 25 MiB attachment limit.");
            }

            var bytesPointer = Send(data, Selector("bytes"));
            if (bytesPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("The clipboard image data is unavailable.");
            }

            var bytes = new byte[(int)length];
            Marshal.Copy(bytesPointer, bytes, 0, bytes.Length);
            return bytes;
        }

        private static IntPtr Selector(string name)
        {
            return sel_registerName(name);
        }

        [DllImport(ObjectiveCLibrary)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjectiveCLibrary)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr value);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern UIntPtr SendUnsigned(IntPtr receiver, IntPtr selector);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendUnsignedPointer(
            IntPtr receiver,
            IntPtr selector,
            UIntPtr unsignedValue,
            IntPtr pointerValue);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr receiver, IntPtr selector);
#endif
    }
}
