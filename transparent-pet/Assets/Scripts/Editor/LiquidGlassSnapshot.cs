// ============================================================================
// LiquidGlassSnapshot.cs — 液态玻璃版本的视觉锚定快照（batchmode 人工看图）
// ============================================================================
// 搭 800×560 极简世界（纯色背景模拟桌面），以非 Play 方式手动驱动
// LiquidGlassController 的渲染管线，输出多张诊断图到临时目录供人工验收：
//   liquidglass_main.png       主渲染（棋盘格素材，折射/色散/菲涅尔/眩光）
//   liquidglass_gradient.png   主渲染（垂直渐变素材，观察折射形变更直观）
//   liquidglass_sdf.png        STEP 0：SDF 梯度（核对轮廓形状/比例）
//   liquidglass_normal.png     STEP 2：法线彩虹图（核对法线连续性）
// 运行：-executeMethod TransparentPet.EditorTools.LiquidGlassSnapshot.CaptureHeadless
// 输出：C:/Users/fanbo/AppData/Local/Temp/pet-snapshot/
// ============================================================================
using System.IO;
using TransparentPet.Pet;
using UnityEditor;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    public static class LiquidGlassSnapshot
    {
        const int W = 800, H = 560;
        const float PPU = 100f;
        const string OutDir = "C:/Users/fanbo/AppData/Local/Temp/pet-snapshot";

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
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(OutDir, fileName), tex.EncodeToPNG());
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

            cam.targetTexture = null;
        }

        static void DumpRt(RenderTexture rt, string fileName)
        {
            if (rt == null)
            {
                Debug.LogWarning($"[LiquidGlassSnapshot] {fileName}: RT 为 null，跳过");
                return;
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(OutDir, fileName), tex.EncodeToPNG());
        }
    }
}
