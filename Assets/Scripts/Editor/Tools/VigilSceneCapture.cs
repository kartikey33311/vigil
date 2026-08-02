// -----------------------------------------------------------------------------
// Vigil — offscreen level capture.
//
// Renders the facility to a PNG from a fixed vantage point, so rendering can be
// verified without a human looking at a window. Built after a magenta-materials
// bug shipped into a player build unnoticed: "it compiles" and "all tests pass"
// say nothing about whether the thing actually draws.
//
// Run WITHOUT -nographics (batch mode still needs a graphics device to render):
//   Unity.exe -batchmode -quit -projectPath . -logFile - \
//     -executeMethod Vigil.Editor.Tools.VigilSceneCapture.CaptureLevel
// -----------------------------------------------------------------------------

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vigil.Core.Diagnostics;

namespace Vigil.Editor.Tools
{
    public static class VigilSceneCapture
    {
        const string LevelScenePath = "Assets/Scenes/Level_Facility.unity";
        const int Width = 1280;
        const int Height = 720;

        [MenuItem("Vigil/Capture Level Preview", priority = 130)]
        public static void CaptureLevel()
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(outDir);

            Scene scene = EditorSceneManager.OpenScene(LevelScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                VLog.Error(LogCat.Core, $"Could not open {LevelScenePath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            // Three angles: an overview, a corridor at eye height, and a close look
            // at a generator. One shot can easily miss a material that only appears
            // on one prop.
            Capture(Path.Combine(outDir, "preview_overview.png"),
                new Vector3(0f, 34f, -30f), Quaternion.Euler(46f, 0f, 0f), 60f);

            Capture(Path.Combine(outDir, "preview_eyelevel.png"),
                new Vector3(0f, 1.7f, -6f), Quaternion.Euler(4f, 0f, 0f), 68f);

            Capture(Path.Combine(outDir, "preview_generator.png"),
                new Vector3(-11f, 2.4f, -12f), Quaternion.Euler(10f, -140f, 0f), 68f);

            VLog.Info(LogCat.Core, $"Captured level previews to {outDir}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static void Capture(string path, Vector3 position, Quaternion rotation, float fieldOfView)
        {
            GameObject camGo = new GameObject("~VigilCaptureCamera");
            camGo.transform.SetPositionAndRotation(position, rotation);

            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 400f;

            // Without an explicit clear the target texture keeps whatever was in it,
            // which makes a failed render look like a successful dark one.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);

            RenderTexture rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            rt.Create();

            cam.targetTexture = rt;
            cam.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D shot = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            shot.Apply();

            RenderTexture.active = previous;

            File.WriteAllBytes(path, shot.EncodeToPNG());

            // Report the mean colour: a magenta level is instantly obvious as a huge
            // red+blue / no-green signature, and it is checkable without human eyes.
            Color32[] pixels = shot.GetPixels32();
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < pixels.Length; i++) { r += pixels[i].r; g += pixels[i].g; b += pixels[i].b; }

            float n = pixels.Length;
            float mr = r / n, mg = g / n, mb = b / n;

            bool looksMagenta = mr > 60f && mb > 60f && mg < (mr * 0.5f) && mg < (mb * 0.5f);

            VLog.Info(LogCat.Core,
                $"{Path.GetFileName(path)}  meanRGB=({mr:F0},{mg:F0},{mb:F0})  {(looksMagenta ? "*** MAGENTA - SHADERS MISSING ***" : "ok")}");

            cam.targetTexture = null;
            RenderTexture.active = null;
            rt.Release();

            Object.DestroyImmediate(shot);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
        }
    }
}
