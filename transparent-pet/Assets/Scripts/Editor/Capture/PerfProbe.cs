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
//   3. 要比多个配置时，必须在同一进程里**交替**测（时序漂移对所有配置同等作用）；
//      分段版更进一步：轮间配置顺序**正反交替** + 报**配对中位差**（同轮内两配置
//      相减，慢漂移在差里抵消），并同时报最小批差作旁证，两种口径同号才算数；
//   4. 同一份代码连跑两次**未必**吻合：实测有差到 2 倍的（4.23 vs 8.48 ms），
//      且两个因素叠加——①机器上可能另有一个 Unity 实例（别的工程的 batchmode
//      测试跑了 35 分钟、吃掉 6000+ 秒 CPU）；②**长时间热态漂移**：那个进程退出、
//      机器空闲后复测仍比会话开始时高 65%，同一份代码连着两次还能差 22%。
//      故：**绝对数字只在同一进程内互相比较**，别拿跨进程/跨小时的数字算倍数；
//      要报"改动省了多少"必须用第 3 条的交替配对。测量前先确认没有别的 Unity
//      实例：tasklist | grep -i unity。
// 锁死生命感变换（呼吸相位取自 Time.time，不锁则每次运行的形变不同）。
//
// 运行：-batchmode -quit -projectPath ... -executeMethod TransparentPet.EditorTools.PerfProbe.RunHeadless
//      分段版：-executeMethod TransparentPet.EditorTools.PerfProbe.RunBreakdownHeadless
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
        const int Warmup = 30, BatchFrames = 10, Batches = 12;

        [MenuItem("TransparentPet/性能探针：玻璃单帧耗时")]
        public static void RunFromMenu() => RunHeadless();

        public static void RunHeadless()
        {
            Probe("单只居中", new[] { new Vector2(ScreenW * 0.5f, ScreenH * 0.5f) });
            Probe("两只分散", new[] { new Vector2(1939f, 573f), new Vector2(1794f, 793f) });
        }

        /// <summary>探针世界：相机 + 玻璃对象 + 离屏 RT；用完必须 Dispose。</summary>
        sealed class World
        {
            public Camera Cam;
            public GameObject GlassGo;
            public LiquidGlassController Controller;
            public RenderTexture Rt;

            public void Dispose()
            {
                Cam.targetTexture = null;
                Object.DestroyImmediate(Cam.gameObject);
                Object.DestroyImmediate(GlassGo);
                Rt.Release();
                Object.DestroyImmediate(Rt);
            }
        }

        static World BuildWorld(Vector2[] positions)
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

            return new World { Cam = cam, GlassGo = glassGo, Controller = controller, Rt = rt };
        }

        static void Probe(string label, Vector2[] positions)
        {
            var world = BuildWorld(positions);

            for (var i = 0; i < Warmup; i++)
            {
                world.Controller.Tick();
                world.Cam.Render();
            }
            SyncGpu(world.Rt);

            var samples = new double[Batches];
            for (var b = 0; b < Batches; b++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < BatchFrames; i++)
                {
                    world.Controller.Tick();
                    world.Cam.Render();
                }
                SyncGpu(world.Rt);
                sw.Stop();
                samples[b] = sw.Elapsed.TotalMilliseconds / BatchFrames;
            }

            var sorted = (double[])samples.Clone();
            System.Array.Sort(sorted);

            Debug.Log($"[PerfProbe] {label}（{ScreenW}x{ScreenH}）: " +
                      $"最快 {sorted[0]:F2} ms/帧，中位 {sorted[Batches / 2]:F2} ms/帧" +
                      $"（{Batches} 批 × {BatchFrames} 帧，每批一次 GPU 同步，取最小批）");

            world.Dispose();
        }

        [MenuItem("TransparentPet/性能探针：分段耗时（定位瓶颈）")]
        public static void RunBreakdownFromMenu() => RunBreakdownHeadless();

        /// <summary>
        /// 分段耗时：把"整帧"拆成五段，交替测（时序漂移对各段同等作用）。
        ///   ①整帧             = 清屏 + bg/模糊 Blit + 主合成上屏
        ///   ②掉主合成         = 禁用 quad 的 MeshRenderer（Tick 照跑：清屏 + Blit 仍在）
        ///   ③只清屏           = 不调 Tick（无任何 Blit）+ 禁用 renderer
        ///   ④无提前退出       = 同①，但每帧 Tick 后把 `_EarlyOutPx` 顶到 500px（阈值失效）
        ///   ⑤quad=来源矩形    = 同①，但每帧 Tick 后把 quad 撑回来源矩形（解耦前行为）
        /// 于是 ③ = 清屏、②−③ = Blit 段、①−② = 主合成段；④−① 是**提前退出这一级剪枝
        /// 在绘制矩形里省下的时间**；⑤−① 是**quad 与来源矩形解耦**省下的时间。
        /// ④⑤ 都不改代码，只把 Uniform / 变换推回旧行为，所以能在同一进程里交替对比。
        ///
        /// **统计口径（本轮补）**：本机核显的功率状态会让同一配置在几十秒尺度上漂 2×，
        /// 光看"最小批"曾出现"提前退出省 6ms / 1.9ms / 0.1ms"三种互斥结论。故：
        ///   · 每轮的配置顺序**正反交替**（漂移在轮内是单调的，交替后不再系统性偏向某个配置）；
        ///   · 主结论取**配对差的中位数**（同一轮内两配置相减，慢漂移被差分抵消）；
        ///   · 同时报"最小批差"作旁证，两者一致才算数。
        /// </summary>
        public static void RunBreakdownHeadless()
        {
            Breakdown("单只居中", new[] { new Vector2(ScreenW * 0.5f, ScreenH * 0.5f) });
            Breakdown("两只分散", new[] { new Vector2(1939f, 573f), new Vector2(1794f, 793f) });
        }

        static void Breakdown(string label, Vector2[] positions)
        {
            // 预热与轮数按"轮"计（一轮 = 每个配置各一批）：4 轮预热足够让着色器/RT 稳定，
            // 16 轮供配对差取中位（轮数是统计功效的来源，比延长每批帧数更有效）
            const int WarmupRounds = 4, Rounds = 16;
            var world = BuildWorld(positions);
            var renderer = world.GlassGo.GetComponent<MeshRenderer>();
            var names = new[] { "整帧", "掉主合成", "只清屏", "无提前退出", "quad=来源矩形" };
            var samples = new double[names.Length, Rounds];

            // 先跑一帧取两个矩形（配置 ⑤ 会把 quad 撑大，循环结束后读到的是被改过的状态）
            world.Controller.Tick();
            var rects = $"quad {world.Controller.CurrentQuadRect} / 来源 {world.Controller.CurrentRenderRect}";

            for (var b = -WarmupRounds; b < Rounds; b++)
            {
                var forward = (b & 1) == 0;
                for (var k = 0; k < names.Length; k++)
                {
                    var cfg = forward ? k : names.Length - 1 - k;
                    renderer.enabled = cfg != 1 && cfg != 2;
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < BatchFrames; i++)
                    {
                        if (cfg != 2)
                        {
                            world.Controller.Tick();
                            if (cfg == 3)
                                renderer.sharedMaterial.SetFloat("_EarlyOutPx", 500f);
                            if (cfg == 4)
                                WidenQuadToSourceRect(world);
                        }
                        world.Cam.Render();
                    }
                    SyncGpu(world.Rt);
                    sw.Stop();
                    if (b >= 0)
                        samples[cfg, b] = sw.Elapsed.TotalMilliseconds / BatchFrames;
                }
            }

            var best = new double[names.Length];
            var paired = new double[names.Length];
            for (var cfg = 0; cfg < names.Length; cfg++)
            {
                var col = new double[Rounds];
                var diff = new double[Rounds];
                for (var b = 0; b < Rounds; b++)
                {
                    col[b] = samples[cfg, b];
                    diff[b] = samples[cfg, b] - samples[0, b];
                }
                System.Array.Sort(col);
                System.Array.Sort(diff);
                best[cfg] = col[0];
                paired[cfg] = diff[Rounds / 2];
                Debug.Log($"[PerfProbe] 分段·{label}·{names[cfg]}: 最快 {col[0]:F2} ms/帧，中位 {col[Rounds / 2]:F2} ms/帧" +
                          $"（配对中位差 {paired[cfg]:+0.00;-0.00;0.00}）");
            }

            Debug.Log($"[PerfProbe] 分段结论·{label}（{ScreenW}x{ScreenH}）：" +
                      $"清屏 {best[2]:F2} + Blit {best[1] - best[2]:F2} + 主合成 {best[0] - best[1]:F2} = 整帧 {best[0]:F2} ms；" +
                      $"提前退出省 {paired[3]:+0.00;-0.00;0.00}（最小批差 {best[3] - best[0]:+0.00;-0.00;0.00}）ms；" +
                      $"quad 解耦省 {paired[4]:+0.00;-0.00;0.00}（最小批差 {best[4] - best[0]:+0.00;-0.00;0.00}）ms；" +
                      rects);

            world.Dispose();
        }

        /// <summary>
        /// 把主合成 quad 撑回"来源矩形"（解耦前的形态）：等价于改动前的 _ScreenUvRect +
        /// 变换写法——必须与 LiquidGlassController.SyncQuadToCamera 的算式逐字一致，
        /// 否则测的就不是同一件事。用来在同一进程里 A/B 解耦前后的整帧耗时。
        /// </summary>
        static void WidenQuadToSourceRect(World world)
        {
            var r = world.Controller.CurrentRenderRect;
            var t = world.GlassGo.transform;
            t.localScale = new Vector3(r.W / PPU, r.H / PPU, 1f);
            t.position = new Vector3(
                (r.X + r.W * 0.5f - ScreenW * 0.5f) / PPU,
                (ScreenH * 0.5f - (r.Y + r.H * 0.5f)) / PPU,
                0f);
            world.GlassGo.GetComponent<MeshRenderer>().sharedMaterial.SetVector("_ScreenUvRect",
                new Vector4(r.X / (float)ScreenW, 1f - (r.Y + r.H) / (float)ScreenH,
                            r.W / (float)ScreenW, r.H / (float)ScreenH));
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
