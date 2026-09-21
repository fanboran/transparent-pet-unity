// ============================================================================
// PerfProbe.cs — 液态玻璃渲染耗时的可复现探针（batchmode，无窗口无干扰）
// ============================================================================
// 用途：给"绘制范围收敛 / 主合成剪枝 / 法线求法"这类性能改动提供可对比的数字。
//
// 世界参数刻意取"本机最坏形态"：2560×1440（主显示器分辨率）+ 核显（Intel UHD）
// + SlimeWidthPx=320。用例覆盖两种量级：单只居中（收敛后绘制矩形远小于屏幕）
// 与两只分散（用户实机配置的形态）。
//
// **计时口径（关键，别退回成"跑 60 帧取平均"）**：核显是共享显存 + 睿频漂移的
// 环境——同一配置放在不同位置实测能差 40%，单次均值完全不可比。故：
//   1. 每批 10 帧后强制一次 GPU 排空（1 像素同步读回），逐批计时；
//   2. 每批取**最小值**（受干扰最少的那批）作为该配置的速度，另报中位数看离散度；
//   3. 要比多个配置时，必须在同一进程里**交替**测（时序漂移对所有配置同等作用），
//      各跑一遍的旧做法会把漂移当成改动效果——本轮的"改前/改后"就是这么测的；
//   4. 同一份代码连跑两次应当吻合（实测 5.06 / 5.01 ms）；某次整轮偏高说明有外部
//      进程在抢 GPU，重跑即可，别据此下结论。
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

            const int warmup = 30, batchFrames = 10, batches = 12;
            for (var i = 0; i < warmup; i++)
            {
                controller.Tick();
                cam.Render();
            }
            SyncGpu(rt);

            var samples = new double[batches];
            for (var b = 0; b < batches; b++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < batchFrames; i++)
                {
                    controller.Tick();
                    cam.Render();
                }
                SyncGpu(rt);
                sw.Stop();
                samples[b] = sw.Elapsed.TotalMilliseconds / batchFrames;
            }

            var sorted = (double[])samples.Clone();
            System.Array.Sort(sorted);

            Debug.Log($"[PerfProbe] {label}（{ScreenW}x{ScreenH}）: " +
                      $"最快 {sorted[0]:F2} ms/帧，中位 {sorted[batches / 2]:F2} ms/帧" +
                      $"（{batches} 批 × {batchFrames} 帧，每批一次 GPU 同步，取最小批）");

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
