using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentForUnity.Editor.Application
{
    internal static class AgentUnityViewImageCapture
    {
        private const int MaxImageBytes = 25 * 1024 * 1024;
        private const string AttachmentDirectoryName = "Attachments";
        private const BindingFlags InstanceFieldFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        internal static bool TryCapture(
            string projectRoot,
            out string path,
            out string error)
        {
            path = null;
            error = null;
            Texture2D texture = null;
            try
            {
                texture = CopyRenderTexture(GetGameViewTexture());
                var bytes = texture.EncodeToPNG();
                if (bytes == null || bytes.Length == 0)
                {
                    throw new InvalidOperationException("Unity returned an empty image.");
                }

                if (bytes.Length > MaxImageBytes)
                {
                    throw new InvalidOperationException("The captured image exceeds the 25 MiB attachment limit.");
                }

                var directory = Path.Combine(
                    Path.GetFullPath(projectRoot),
                    "Library",
                    "AgentForUnity",
                    AttachmentDirectoryName);
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, "game-view-" + Guid.NewGuid().ToString("N") + ".png");
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
                        // Preserve the original capture error.
                    }
                }

                path = null;
                error = "Could not capture the Game view: " + exception.Message;
                return false;
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        private static RenderTexture GetGameViewTexture()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                throw new InvalidOperationException("This Unity version does not expose the Game view.");
            }

            var focusedWindow = EditorWindow.focusedWindow;
            var gameView = focusedWindow != null && gameViewType.IsInstanceOfType(focusedWindow)
                ? focusedWindow
                : Resources.FindObjectsOfTypeAll(gameViewType).OfType<EditorWindow>().FirstOrDefault();
            if (gameView == null)
            {
                throw new InvalidOperationException("Open the Game view once, then try again.");
            }

            return ReadRenderTextureField(gameView, "m_RenderTexture", "Game view");
        }

        private static RenderTexture ReadRenderTextureField(object view, string fieldName, string displayName)
        {
            var field = view.GetType().GetField(fieldName, InstanceFieldFlags);
            var texture = field?.GetValue(view) as RenderTexture;
            if (texture == null || !texture.IsCreated() || texture.width <= 0 || texture.height <= 0)
            {
                throw new InvalidOperationException(displayName + " has not rendered an image yet.");
            }

            return texture;
        }

        private static Texture2D CopyRenderTexture(RenderTexture source)
        {
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            var copy = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            try
            {
                Graphics.Blit(source, copy, new Vector2(1f, -1f), new Vector2(0f, 1f));
                RenderTexture.active = copy;
                texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            catch (Exception)
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(copy);
            }
        }
    }
}
