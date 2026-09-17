// ============================================================================
// FourSpeciesSnapshot.cs — 四物种合影截图：黑白格测试舞台 × 四宫格排版
// ============================================================================
// 用途：宣传图/README 门面。每个格子独立建一个 BuildFourSpeciesStage 场景
//（onlyId 隔离，杜绝互相入镜），该物种定格在黑白格正中，取景 776×576，
// 四格合成 2×2 四宫格 PNG。
//
// 各物种的"定格"方式（编辑器非 Play，控制器 Start/Update 不跑，渲染手动驱动）：
//   液态玻璃：LiquidGlassController.Tick()（自家管线，BgType=2 折射同款黑白格）——
//             quad 满屏、SDF 逻辑坐标即"当前取景"的屏幕像素，格心即取景中心；
//   贴图史莱姆：静态精灵（0.4 缩放 = 全宽 320px）；
//   果冻软体：SlimePbf 静息姿态（无重力松弛 30 步）+ SlimeBody.Push，蓝；
//   分裂软体：SlimeSimulation 静息环 + SlimeRingBody.Push，蓝（用户拍板）。
// ============================================================================
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using TransparentPet.Core;
using TransparentPet.Pet;

namespace TransparentPet.EditorTools
{
    public static class FourSpeciesSnapshot
    {
        const int TileW = 776, TileH = 576, Gutter = 16;
        const int GridW = Gutter * 3 + TileW * 2; // 1600
        const int GridH = Gutter * 3 + TileH * 2; // 1200
        const float Ppu = 100f;

        [MenuItem("TransparentPet/快照：四物种合影（黑白格四宫格）")]
        public static void CaptureFromMenu() => CaptureHeadless();

        public static void CaptureHeadless()
        {
            var tiles = new System.Collections.Generic.List<UnityEngine.Color[]>();
            foreach (var id in new[] { "glass", "textured", "softbody", "mesh" })
                tiles.Add(RenderTilePixels(id));

            // 合成四宫格：白底 + 2×2 瓦片（tiles 顺序 = 左上/右上/左下/右下）
            var canvas = new Texture2D(GridW, GridH, TextureFormat.RGBA32, false);
            var fill = new UnityEngine.Color[GridW * GridH];
            for (var i = 0; i < fill.Length; i++)
                fill[i] = UnityEngine.Color.white;
            canvas.SetPixels(fill);
            var origins = new (int x, int y)[]
            {
                (Gutter, GridH - Gutter - TileH),             // 左上
                (Gutter * 2 + TileW, GridH - Gutter - TileH), // 右上
                (Gutter, Gutter),                             // 左下
                (Gutter * 2 + TileW, Gutter),                 // 右下
            };
            for (var t = 0; t < 4; t++)
                canvas.SetPixels(origins[t].x, origins[t].y, TileW, TileH, tiles[t]);
            canvas.Apply();

            var outPath = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "docs", "images", "four_species.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllBytes(outPath, canvas.EncodeToPNG());
            Debug.Log($"[FourSpeciesSnapshot] 四宫格合影输出: {outPath}");
        }

        /// <summary>
        /// 单格：独立场景只建该物种，定格在黑白格正中，渲染 776×576。
        /// 返回像素数组而非 Texture2D——NewScene 卸载会把跨场景持有的贴图当
        /// 未使用资源销毁（MissingReferenceException，实测踩坑），只有像素数组
        /// 能安全穿越场景边界。
        /// </summary>
        static UnityEngine.Color[] RenderTilePixels(string id)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            // withControllers=false：截图手动驱动渲染，不挂控制器（其构造期 Random 在编辑器被禁）
            // 贴图格传"none"：场景里只有黑白格（贴图由软件合成，不进引擎渲染）
            var buildId = id == "textured" ? "none" : id;
            var pets = SceneGenerator.BuildFourSpeciesStage(scene, buildId, withControllers: false);
            var camera = UnityEngine.Camera.main;

            // 相机固定世界原点：视野 ±3.88×±2.88 世界单位 = 776×576px，格心即原点
            camera.orthographicSize = TileH * 0.5f / Ppu;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            Vector3 ToWorld(Vector2 px) => new Vector3(
                (px.x - TileW * 0.5f) / Ppu, (TileH * 0.5f - px.y) / Ppu, 0f);
            var center = new Vector2(TileW * 0.5f, TileH * 0.5f);

            switch (id)
            {
                case "glass":
                {
                    var glass = pets["glass"].GetComponent<LiquidGlassController>();
                    glass.EnsureInitialized();
                    glass.SetSlimeWidthForCapture(PetMetrics.BaseFullWidthPx);
                    glass.SetLogicPositionForCapture(center);
                    glass.Tick(); // 管线：折射源（黑白格）→ 模糊 → 主合成参数
                    break;
                }
                case "softbody":
                {
                    var body = pets["softbody"].GetComponent<SlimeBody>();
                    body.Initialize(pets["softbody"].GetComponent<MeshRenderer>().sharedMaterial);
                    var sim = new SlimePbf(center, PetMetrics.BaseFullWidthPx * 0.5f);
                    var env = new PbfEnvironment
                    {
                        BoundsWidth = TileW, GroundY = TileH, TopY = 0f,
                        GravityOn = false, Gravity = 800f,
                    };
                    for (var i = 0; i < 30; i++)
                        sim.StepFrame(1f / 60f, env); // 无重力松弛：静息趴姿
                    body.Push(sim, ToWorld, new Color(0.16f, 0.48f, 0.92f));
                    break;
                }
                case "mesh":
                {
                    // V2 碎裂软体：PBF + 等值线渲染，静息姿态定格
                    var body = pets["mesh"].GetComponent<SlimeMeshBody>();
                    body.Initialize(pets["mesh"].GetComponent<MeshRenderer>().sharedMaterial);
                    var sim = new SlimePbfMesh(center, PetMetrics.BaseFullWidthPx * 0.5f);
                    body.Push(sim, ToWorld, new Color(0.16f, 0.48f, 0.92f)); // 构造即静息姿态
                    break;
                }
            }

            var rt = new RenderTexture(TileW, TileH, 24);
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            var tile = new Texture2D(TileW, TileH, TextureFormat.RGBA32, false);
            tile.ReadPixels(new Rect(0, 0, TileW, TileH), 0, 0);
            tile.Apply();
            RenderTexture.active = null;
            camera.targetTexture = null;
            var pixels = tile.GetPixels();
            Object.DestroyImmediate(tile);
            rt.Release();
            Object.DestroyImmediate(rt);

            // 贴图格：PetSlime.png 按 alpha 软件混合到正中（绕开编辑模式渲染的不稳定）
            if (id == "textured")
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/PetSlime.png");
                var sp = tex.GetPixels();
                var sw = tex.width;
                var sh = tex.height;
                var c = sp[(sh / 2) * sw + (sw / 2)];
                var corner = sp[0];
                Debug.Log($"[FourSpeciesSnapshot] 贴图混合诊断 中心RGBA=({c.r:0.00},{c.g:0.00},{c.b:0.00},{c.a:0.00}) 角RGBA=({corner.r:0.00},{corner.g:0.00},{corner.b:0.00},{corner.a:0.00}) sp={sp.Length}");
                var scale = 2f * Ppu / tex.width; // 物种平等：显示全宽 200px（原版基准）
                var dw = Mathf.RoundToInt(sw * scale);
                var dh = Mathf.RoundToInt(tex.height * scale);
                var x0 = Mathf.RoundToInt(TileW / 2f - dw / 2f);
                var y0 = Mathf.RoundToInt(TileH / 2f - dh / 2f);

                for (var y = 0; y < dh; y++)
                {
                    for (var x = 0; x < dw; x++)
                    {
                        var src = sp[(int)(y / scale) * sw + (int)(x / scale)];
                        var di = (y0 + y) * TileW + (x0 + x);
                        var dst = pixels[di];
                        pixels[di] = src * src.a + dst * (1f - src.a);
                    }
                }
            }

            return pixels;
        }

    }
}
