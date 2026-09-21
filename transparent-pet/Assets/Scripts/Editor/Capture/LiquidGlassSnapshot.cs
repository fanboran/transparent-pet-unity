// ============================================================================
// LiquidGlassSnapshot.cs — 液态玻璃版本的视觉锚定快照（batchmode 人工看图）
// ============================================================================
// 搭极简世界（纯色背景模拟桌面），以非 Play 方式手动驱动 LiquidGlassController
// 的渲染管线，输出诊断图到临时目录供人工验收：
//
//   【画布 800×560】表现与形状锚定（文件头历史用例，文件名保持稳定）
//   liquidglass_main.png       主渲染（棋盘格素材，折射/色散/菲涅尔/眩光）
//   liquidglass_gradient.png   主渲染（垂直渐变素材，观察折射形变更直观）
//   liquidglass_sdf.png        STEP 0：SDF 梯度（核对轮廓形状/比例）
//   liquidglass_normal.png     STEP 2：法线彩虹图（核对法线连续性）
//   liquidglass_life_{identity,squash,tilt}.png
//                              变换级生命感定点图（渐变底，三者同底可 A/B）
//
//   【画布 1600×1000】渲染范围（"只画玻璃包围盒"性能改造）的等价性锚定
//   liquidglass_rect_single.png  单只偏心：绘制矩形远小于画布
//   liquidglass_rect_multi.png   三只（两只相邻走 smin 融合 + 一只远处）
//   liquidglass_rect_sdf.png     STEP 0 调试视图：必须仍覆盖全屏（调试视图不收敛）
//   改造后这三张必须与改造前逐字节一致——收敛绘制范围是纯性能改动，
//   画面（含轮廓外那圈淡阴影）不允许有任何像素级变化。
//
// 确定性：生命感呼吸相位取自 Time.time，不锁定则两次运行的图本不可比。
// 故所有 Snap 之前统一调用 PinLife 把每只的变换锁成 identity（OffsetY=0）。
//
// 运行：-executeMethod TransparentPet.EditorTools.LiquidGlassSnapshot.CaptureHeadless
// 输出：%TEMP%/pet-snapshot/（Path.GetTempPath() 派生，不写死个人路径）
// ============================================================================
using System.IO;
using TransparentPet.Pet;
using UnityEditor;
using UnityEngine;
using TransparentPet.Pet.Glass;

namespace TransparentPet.EditorTools
{
    public static class LiquidGlassSnapshot
    {
        const int W = 800, H = 560;
        const float PPU = 100f;
        // 输出目录：系统临时目录派生——写死个人用户名的绝对路径，换台机器一跑就指向不存在的位置
        static readonly string OutDir = Path.Combine(Path.GetTempPath(), "pet-snapshot");

        [MenuItem("TransparentPet/快照：液态玻璃")]
        public static void CaptureFromMenu() => CaptureHeadless();

        /// <summary>一个临时世界：正交相机 + 玻璃控制器 + 离屏 RT，用完必须 Destroy。</summary>
        sealed class World
        {
            public Camera Cam;
            public LiquidGlassController Controller;
            public RenderTexture Rt;

            /// <summary>驱动一帧管线（Blit + 上屏）并存图。</summary>
            public void Snap(string fileName)
            {
                Controller.Tick();  // Blit 管线（素材 → 模糊 → 主合成参数）
                Cam.Render();       // quad 携主合成材质上屏
                SaveRtPng(Rt, fileName);
                Debug.Log($"[LiquidGlassSnapshot] 输出 {fileName} 来源矩形={Controller.CurrentRenderRect} " +
                          $"quad矩形={Controller.CurrentQuadRect}");
            }

            public void Dispose()
            {
                Cam.targetTexture = null;
                Object.DestroyImmediate(Cam.gameObject);
                Object.DestroyImmediate(Controller.gameObject);
                Rt.Release();
                Object.DestroyImmediate(Rt);
            }
        }

        public static void CaptureHeadless()
        {
            Directory.CreateDirectory(OutDir);

            // ── 画布 1：既有 800×560 表现锚定用例 ──
            var world = BuildWorld(W, H);
            var controller = world.Controller;
            controller.SetSlimeWidthForCapture(256f);
            controller.SetLogicPositionForCapture(new Vector2(W * 0.5f, H * 0.5f));
            PinLife(controller, controller.SlimeCount);

            // 主渲染 × 两种素材 + 两个诊断视图
            controller.Step = 9;
            controller.BgType = 0;
            world.Snap("liquidglass_main.png");
            DumpRt(controller.BgTarget, "liquidglass_dbg_bg.png");
            DumpRt(controller.HBlurTarget, "liquidglass_dbg_blur.png");
            Debug.Log($"[LiquidGlassSnapshot] 材质状态: {controller.DescribeMaterialState()}");

            controller.BgType = 1;
            world.Snap("liquidglass_gradient.png");

            controller.BgType = 0;
            controller.Step = 0;
            world.Snap("liquidglass_sdf.png");

            controller.Step = 2;
            world.Snap("liquidglass_normal.png");

            // ── 变换级生命感的定点图：人工确认形变方向与幅度 ──
            // 同用渐变素材（形变最直观），与上面的 liquidglass_gradient.png 同底可 A/B：
            //   identity = 无变形（等同于本次改造前的渲染路径）
            //   squash   = 挤压（落地/戳：横向涨、纵向扁）
            //   tilt     = 倾角（向右拖拽 → 顶部朝运动方向倾倒）
            controller.BgType = 1;
            controller.Step = 9;
            controller.SetLifeTransformForCapture(0, 1f, 1f, 0f);
            world.Snap("liquidglass_life_identity.png");
            controller.SetLifeTransformForCapture(0, 1.15f, 0.85f, 0f);
            world.Snap("liquidglass_life_squash.png");
            controller.SetLifeTransformForCapture(0, 1f, 1f, -10f * Mathf.Deg2Rad);
            world.Snap("liquidglass_life_tilt.png");
            controller.SetLifeTransformForCapture(0, 1f, 1f, 0f); // 复原，避免影响后续/重复运行
            world.Dispose();

            // ── 画布 2：渲染范围收敛的等价性用例（大画布 + 偏心/多只）──
            CaptureRenderRectCases();
        }

        /// <summary>
        /// 渲染范围用例：画布 1600×1000 上放偏心单只与"融合 + 远方"三只，
        /// 让绘制矩形明显小于画布，逐像素验证收敛后与全屏绘制等价。
        /// </summary>
        static void CaptureRenderRectCases()
        {
            const int W2 = 1600, H2 = 1000;
            var world = BuildWorld(W2, H2);
            var controller = world.Controller;
            controller.BgType = 1; // 渐变素材：折射形变比棋盘格更直观

            // 单只贴角：绘制矩形只覆盖画面一角
            controller.SetSlimeWidthForCapture(192f);
            controller.SetLogicPositionForCapture(new Vector2(1380f, 130f));
            PinLife(controller, controller.SlimeCount);
            world.Snap("liquidglass_rect_single.png");

            // 三只：两只相邻（间距 260 < 半宽×2 + 融合半径 → smin 融合可见），一只远处
            controller.SetLogicPositionForCapture(0, new Vector2(760f, 520f));
            controller.SetLogicPositionForCapture(1, new Vector2(1020f, 530f));
            controller.SetLogicPositionForCapture(2, new Vector2(300f, 800f));
            PinLife(controller, controller.SlimeCount);
            world.Snap("liquidglass_rect_multi.png");

            // 调试视图（STEP ≤ 2）必须仍绘制全画布——SDF/法线图是形状锚定工具，
            // 收敛到包围盒就看不到轮廓外围的数值分布了
            controller.Step = 0;
            world.Snap("liquidglass_rect_sdf.png");

            world.Dispose();
        }

        /// <summary>锁定每只的生命感变换为 identity（呼吸相位依赖 Time.time，不锁则不可比）。</summary>
        static void PinLife(LiquidGlassController controller, int count)
        {
            for (var i = 0; i < count; i++)
                controller.SetLifeTransformForCapture(i, 1f, 1f, 0f);
        }

        /// <summary>搭一个临时世界（相机 + 玻璃对象 + 离屏 RT）。</summary>
        static World BuildWorld(int w, int h)
        {
            var camGo = new GameObject("SnapCam");
            camGo.tag = "MainCamera"; // controller 的 Camera.main 依赖
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = h * 0.5f / PPU;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f, 1f); // 模拟桌面深灰

            // ── 液态玻璃对象：与运行时同组件路径 ──
            var glassGo = new GameObject("LiquidGlass");
            glassGo.AddComponent<MeshFilter>();
            glassGo.AddComponent<MeshRenderer>();
            var controller = glassGo.AddComponent<LiquidGlassController>();
            controller.MainShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/LiquidGlass.shader");
            controller.BgShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/LiquidGlassBg.shader");
            controller.BlurShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/LiquidGlassBlur.shader");

            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;

            return new World { Cam = cam, Controller = controller, Rt = rt };
        }

        static void DumpRt(RenderTexture rt, string fileName)
        {
            if (rt == null)
            {
                Debug.LogWarning($"[LiquidGlassSnapshot] {fileName}: RT 为 null，跳过");
                return;
            }
            SaveRtPng(rt, fileName);
        }

        // Snap 与 DumpRt 的公共出口：读 RT → 存 PNG。此前是两份几乎相同的实现且
        // active 处理不一致（一处置 null、一处恢复 prev），抽到一处统一为
        // "保存 prev → 用完恢复"；读屏用的 Texture2D 用完即销毁（多次运行会累积泄漏）。
        static void SaveRtPng(RenderTexture rt, string fileName)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(OutDir, fileName), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
