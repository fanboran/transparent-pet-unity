// ============================================================================
// PerfProbe.cs — 液态玻璃渲染耗时的可复现探针（batchmode，无窗口无干扰）
// ============================================================================
// 用途：给"绘制范围收敛"这类性能改动提供可对比的数字——同一套世界、同一套驱动
// 代码（LiquidGlassController.Tick + Camera.Render），只改被测实现，跑两遍比耗时。
//
// 世界参数刻意取"本机最坏形态"：2560×1440（主显示器分辨率）+ 核显（Intel UHD）
// + SlimeWidthPx=320。用例刻意覆盖两种量级：
//   · 单只居中：收敛后绘制矩形远小于屏幕（本改动的主要收益场景）
//   · 两只分散：并集矩形较大（用户实机配置的形态）
//
// 计时口径：预热 30 帧后计 60 帧，每 10 帧用 1 像素 ReadPixels 强制一次 GPU 同步
//（ReadPixels 是同步读回，等 GPU 排空；隔 10 帧一次让读回本身的开销摊薄到 1/10）。
// 锁死生命感变换（呼吸相位取自 Time.time，不锁则每次运行的形变不同）。
//
// 运行：-batchmode -quit -projectPath ... -executeMethod TransparentPet.EditorTools.PerfProbe.RunHeadless
// 输出：控制台 [PerfProbe] 行（ms/帧）
// ============================================================================
using System.Diagnostics;
using TransparentPet.Pet.Glass;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TransparentPet.EditorTools
{
    public static class PerfProbe
    {
        const int ScreenW = 2560, ScreenH = 1440;
        const float PPU = 100f;

        [MenuItem("TransparentPet/性能探针：玻璃单帧耗时")]
        public static void RunFromMenu() => RunHeadless();

        public static void RunHeadless()
        {
            Probe("单只居中", new[] { new Vector2(ScreenW * 0.5f, ScreenH * 0.5f) });
            Probe("两只分散", new[] { new Vector2(1939f, 573f), new Vector2(1794f, 793f) });
        }

        static void Probe(string label, Vector2[] positions)
        {
            var camGo = new GameObject("ProbeCam");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = ScreenH * 0.5f / PPU;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f, 1f);
            cam.allowHDR = false;

            var glassGo = new GameObject("LiquidGlass");
            glassGo.AddComponent<MeshFilter>();
            glassGo.AddComponent<MeshRenderer>();
            var controller = glassGo.AddComponent<LiquidGlassController>();
            controller.MainShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/LiquidGlass.shader");
            controller.BgShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/LiquidGlassBg.shader");
            controller.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/LiquidGlassBlur.shader");
            controller.BgType = 1; // 渐变素材：与无头快照同底，便于同时看图
            for (var i = 0; i < positions.Length; i++)
            {
                controller.SetLogicPositionForCapture(i, positions[i]);
                controller.SetLifeTransformForCapture(i, 1f, 1f, 0f); // 锁呼吸 → 可复现
            }

            var rt = new RenderTexture(ScreenW, ScreenH, 24);
            cam.targetTexture = rt;

            const int warmup = 30, frames = 60, syncEvery = 10;
            for (var i = 0; i < warmup; i++)
            {
                controller.Tick();
                cam.Render();
            }
            SyncGpu(rt);

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < frames; i++)
            {
                controller.Tick();
                cam.Render();
                if ((i + 1) % syncEvery == 0)
                    SyncGpu(rt);
            }
            sw.Stop();

            Debug.Log($"[PerfProbe] {label}（{ScreenW}x{ScreenH}）: " +
                      $"{sw.Elapsed.TotalMilliseconds / frames:F2} ms/帧" +
                      $"（{frames} 帧，每 {syncEvery} 帧一次 GPU 同步）");

            cam.targetTexture = null;
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(glassGo);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        /// <summary>强制 GPU 同步：1 像素同步读回（等 GPU 把已有命令跑完）。</summary>
        static void SyncGpu(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0);
            RenderTexture.active = prev;
            Object.DestroyImmediate(tex);
        }
    }
}
