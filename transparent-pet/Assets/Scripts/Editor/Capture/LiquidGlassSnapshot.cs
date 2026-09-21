// ============================================================================
// LiquidGlassSnapshot.cs — 液态玻璃版本的视觉锚定快照（batchmode 人工看图）
// ============================================================================
// 搭 800×560 极简世界（纯色背景模拟桌面），以非 Play 方式手动驱动
// LiquidGlassController 的渲染管线，输出多张诊断图到临时目录供人工验收：
//   liquidglass_main.png       主渲染（棋盘格素材，折射/色散/菲涅尔/眩光）
//   liquidglass_gradient.png   主渲染（垂直渐变素材，观察折射形变更直观）
//   liquidglass_sdf.png        STEP 0：SDF 梯度（核对轮廓形状/比例）
//   liquidglass_normal.png     STEP 2：法线彩虹图（核对法线连续性）
//   liquidglass_life_{identity,squash,tilt}.png
//                              变换级生命感定点图（渐变底，三者同底可 A/B）：
//                              identity = 无变形、squash = 挤压、tilt = 向右拖的倾角
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

        public static void CaptureHeadless()
        {
            Directory.CreateDirectory(OutDir);

            var camGo = new GameObject("SnapCam");
            camGo.tag = "MainCamera"; // controller 的 Camera.main 依赖
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = H * 0.5f / PPU;
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
            controller.SetSlimeWidthForCapture(320f);
            controller.SetLogicPositionForCapture(new Vector2(W * 0.5f, H * 0.5f));

            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;

            void Snap(string fileName)
            {
                controller.Tick();     // Blit 管线（素材 → 模糊 → 主合成参数）
                cam.Render();          // quad 携主合成材质上屏
                SaveRtPng(rt, fileName);
                Debug.Log($"[LiquidGlassSnapshot] 输出 {fileName}");
            }

            // 主渲染 × 两种素材 + 两个诊断视图
            controller.Step = 9;
            controller.BgType = 0;
            Snap("liquidglass_main.png");
            DumpRt(controller.BgTarget, "liquidglass_dbg_bg.png");
            DumpRt(controller.HBlurTarget, "liquidglass_dbg_blur.png");
            Debug.Log($"[LiquidGlassSnapshot] 材质状态: {controller.DescribeMaterialState()}");

            controller.BgType = 1;
            Snap("liquidglass_gradient.png");

            controller.BgType = 0;
            controller.Step = 0;
            Snap("liquidglass_sdf.png");

            controller.Step = 2;
            Snap("liquidglass_normal.png");

            // ── 变换级生命感的定点图：人工确认形变方向与幅度 ──
            // 同用渐变素材（形变最直观），与上面的 liquidglass_gradient.png 同底可 A/B：
            //   identity = 无变形（等同于本次改造前的渲染路径）
            //   squash   = 挤压（落地/戳：横向涨、纵向扁）
            //   tilt     = 倾角（向右拖拽 → 顶部朝运动方向倾倒）
            controller.BgType = 1;
            controller.Step = 9;
            controller.SetLifeTransformForCapture(0, 1f, 1f, 0f);
            Snap("liquidglass_life_identity.png");
            controller.SetLifeTransformForCapture(0, 1.15f, 0.85f, 0f);
            Snap("liquidglass_life_squash.png");
            controller.SetLifeTransformForCapture(0, 1f, 1f, -10f * Mathf.Deg2Rad);
            Snap("liquidglass_life_tilt.png");
            controller.SetLifeTransformForCapture(0, 1f, 1f, 0f); // 复原，避免影响后续/重复运行

            cam.targetTexture = null;

            // ── 清理：camGo/glassGo/RT 都是一次性临时产物，同一编辑器会话内
            //    反复运行（菜单点多次/batchmode 多次 executeMethod）不销毁就累积泄漏 ──
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(glassGo);
            rt.Release();
            Object.DestroyImmediate(rt);
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
