using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AgentForUnity.Editor.Application
{
    internal static class AgentClipboardImageCapture
    {
        private const int MaxImageBytes = 25 * 1024 * 1024;
        private const int MaxClipboardDataBytes = 100 * 1024 * 1024;
        private const int MaxDecodedPixels = 16 * 1024 * 1024;
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
#elif UNITY_EDITOR_WIN
            try
            {
                var bytes = ReadWindowsClipboardImage();
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
            error = "Clipboard screenshots are currently supported on macOS and Windows only.";
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

#if UNITY_EDITOR_WIN
        private const uint ClipboardFormatDib = 8;
        private const uint ClipboardFormatDibV5 = 17;
        private const uint CompressionRgb = 0;
        private const uint CompressionBitfields = 3;

        private static byte[] ReadWindowsClipboardImage()
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                throw new InvalidOperationException("Windows denied access to the clipboard.");
            }

            try
            {
                var pngFormat = RegisterClipboardFormat("PNG");
                if (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat))
                {
                    var png = CopyGlobalMemory(GetClipboardData(pngFormat));
                    if (IsPng(png))
                    {
                        EnsureAttachmentSize(png);
                        return png;
                    }
                }

                var format = IsClipboardFormatAvailable(ClipboardFormatDibV5)
                    ? ClipboardFormatDibV5
                    : IsClipboardFormatAvailable(ClipboardFormatDib) ? ClipboardFormatDib : 0;
                if (format == 0)
                {
                    throw new InvalidOperationException("The clipboard does not contain a screenshot or image.");
                }

                return ConvertDibToPng(CopyGlobalMemory(GetClipboardData(format)));
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static byte[] CopyGlobalMemory(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The clipboard image data is unavailable.");
            }

            var length = GlobalSize(handle).ToUInt64();
            if (length == 0 || length > MaxClipboardDataBytes)
            {
                throw new InvalidOperationException(length == 0
                    ? "The clipboard image is empty."
                    : "The clipboard image exceeds the supported size.");
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not read the clipboard image data.");
            }

            try
            {
                var bytes = new byte[(int)length];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        private static byte[] ConvertDibToPng(byte[] dib)
        {
            if (dib == null || dib.Length < 40)
            {
                throw new InvalidOperationException("The clipboard image uses an unsupported DIB format.");
            }

            var headerSize = ReadUInt32(dib, 0);
            var width = ReadInt32(dib, 4);
            var signedHeight = ReadInt32(dib, 8);
            var bitCount = ReadUInt16(dib, 14);
            var compression = ReadUInt32(dib, 16);
            var height = signedHeight < 0 ? -(long)signedHeight : signedHeight;
            if (headerSize < 40 || headerSize > dib.Length || width <= 0 || height <= 0 ||
                height > int.MaxValue || bitCount != 24 && bitCount != 32 ||
                compression != CompressionRgb && compression != CompressionBitfields ||
                (long)width * height > MaxDecodedPixels)
            {
                throw new InvalidOperationException("The clipboard image uses an unsupported size or pixel format.");
            }

            var pixelOffset = (int)headerSize;
            uint redMask = 0x00ff0000;
            uint greenMask = 0x0000ff00;
            uint blueMask = 0x000000ff;
            uint alphaMask = 0;
            if (compression == CompressionBitfields)
            {
                var maskOffset = headerSize >= 52 ? 40 : pixelOffset;
                if (maskOffset + 12 > dib.Length)
                {
                    throw new InvalidOperationException("The clipboard image is missing its color masks.");
                }

                redMask = ReadUInt32(dib, maskOffset);
                greenMask = ReadUInt32(dib, maskOffset + 4);
                blueMask = ReadUInt32(dib, maskOffset + 8);
                if (headerSize >= 56 && maskOffset + 16 <= dib.Length)
                {
                    alphaMask = ReadUInt32(dib, maskOffset + 12);
                }
                else if (headerSize == 40)
                {
                    pixelOffset += 12;
                }
            }

            var bytesPerPixel = bitCount / 8;
            var stride = ((long)width * bitCount + 31) / 32 * 4;
            var dataLength = stride * height;
            if (pixelOffset > dib.Length || dataLength > dib.Length - pixelOffset)
            {
                throw new InvalidOperationException("The clipboard image pixel data is incomplete.");
            }

            var texture = new Texture2D(width, (int)height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[width * (int)height];
                for (var y = 0; y < (int)height; y++)
                {
                    var sourceY = signedHeight < 0 ? (int)height - y - 1 : y;
                    var rowOffset = pixelOffset + (int)(sourceY * stride);
                    for (var x = 0; x < width; x++)
                    {
                        var offset = rowOffset + x * bytesPerPixel;
                        var value = (uint)(dib[offset] | dib[offset + 1] << 8 | dib[offset + 2] << 16);
                        if (bytesPerPixel == 4)
                        {
                            value |= (uint)dib[offset + 3] << 24;
                        }

                        pixels[y * width + x] = new Color32(
                            ExtractComponent(value, redMask),
                            ExtractComponent(value, greenMask),
                            ExtractComponent(value, blueMask),
                            alphaMask == 0 ? (byte)255 : ExtractComponent(value, alphaMask));
                    }
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                var png = texture.EncodeToPNG();
                EnsureAttachmentSize(png);
                return png;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static byte ExtractComponent(uint value, uint mask)
        {
            if (mask == 0)
            {
                return 0;
            }

            var shift = 0;
            while ((mask & (1u << shift)) == 0)
            {
                shift++;
            }

            var maximum = mask >> shift;
            return (byte)((((ulong)(value & mask) >> shift) * 255) / maximum);
        }

        private static void EnsureAttachmentSize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                throw new InvalidOperationException(bytes == null || bytes.Length == 0
                    ? "The clipboard image is empty."
                    : "The clipboard image exceeds the 25 MiB attachment limit.");
            }
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 &&
                   bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 &&
                   bytes[6] == 26 && bytes[7] == 10;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | bytes[offset + 1] << 8);
        }

        private static int ReadInt32(byte[] bytes, int offset)
        {
            return bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return unchecked((uint)ReadInt32(bytes, offset));
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr GlobalSize(IntPtr memory);
#endif
    }
}
