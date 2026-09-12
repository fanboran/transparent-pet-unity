// ============================================================================
// SlimeSnapshot.cs — 无头渲染快照：PBF 史莱姆关键状态 vs 原版烘焙图对比
// ============================================================================
// batchmode 下搭建"只有底部一个地面"的极简测试世界（重力常开），渲染
// PBF 史莱姆的：出生 / 落地趴姿 / **真实拖拽**（抓偏心点绕圈拖动，途中
// 抓拍——检验受力点是否真的是点击处、身体是否垂坠而非绕鼠标成正圆）。
// 运行：-executeMethod TransparentPet.EditorTools.SlimeSnapshot.CaptureHeadless
// 输出：C:/Users/fanbo/AppData/Local/Temp/pet-snapshot/*.png
// ============================================================================
using System; // 注：Random/Object 用 UnityEngine 限定，避免与 System 二义
using System.IO;
using TransparentPet.Pet;
using UnityEditor;
using UnityEngine;

namespace TransparentPet.EditorTools
{
    public static class SlimeSnapshot
    {
        const int W = 800, H = 560;
        const float PPU = 100f;
        const float GroundYpx = 460f;                     // 地面（屏幕像素，Y 向下）
        const string OutDir = "C:/Users/fanbo/AppData/Local/Temp/pet-snapshot";

        [MenuItem("TransparentPet/快照：PBF 与原版对比")]
        public static void CaptureFromMenu() => CaptureHeadless();

        public static void CaptureHeadless()
        {
            Directory.CreateDirectory(OutDir);
            UnityEngine.Random.InitState(42); // 撒点抖动可复现

            // ── 相机与极简世界：纯色背景 + 一条地面线 ──
            var camGo = new GameObject("SnapCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = H * 0.5f / PPU;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f, 1f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Unlit/Color")) { color = new Color(0.07f, 0.08f, 0.10f) };
            ground.transform.localScale = new Vector3(W / PPU + 2f, 0.04f, 1f);
            ground.transform.position = new Vector3(0f, (H * 0.5f - GroundYpx) / PPU, 0.5f);

            // ── 原版烘焙图（PetSlime_ref.png：git 历史提取，先落 OutDir）──
            GameObject refGo = null;
            var refPath = Path.Combine(OutDir, "PetSlime_ref.png");
            if (File.Exists(refPath))
            {
                var refTex = new Texture2D(2, 2);
                refTex.LoadImage(File.ReadAllBytes(refPath));
                refGo = new GameObject("RefSprite");
                var sr = refGo.AddComponent<SpriteRenderer>();
                sr.sprite = Sprite.Create(refTex,
                    new Rect(0, 0, refTex.width, refTex.height), new Vector2(0.5f, 0.5f), PPU);
                sr.sortingOrder = 5;
                // 贴图 800×528 = 4× 画布 200×132；缩放 0.25 → 画布 200px 宽。
                // 路径底边在画布 y=121 → 路径底距画布中心 55px → 中心抬高 0.55 贴地
                refGo.transform.position = new Vector3(-2.0f, (H * 0.5f - GroundYpx) / PPU + 0.55f, 0f);
                refGo.transform.localScale = Vector3.one * 0.25f;
                refGo.SetActive(false);
            }
            else
                Debug.LogWarning($"[SlimeSnapshot] 未找到原版参考图 {refPath}，跳过对比渲染");

            // ── PBF 史莱姆：metaball 场渲染（与运行时同路径）──
            var sim = new SlimePbf(new Vector2(560f, 150f), 80f); // 半宽 80 = 原版路径宽 160px
            var fieldGo = new GameObject("PbfField");
            var mf = fieldGo.AddComponent<MeshFilter>();
            var mr = fieldGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("TransparentPet/SlimeLiquid"));
            mr.sortingOrder = 10;
            var quad = new Mesh { name = "FieldQuad" };
            quad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            };
            quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            quad.bounds = new Bounds(Vector3.zero, Vector3.one);
            mf.sharedMesh = quad;
            using var field = new SlimeFieldRenderer(mr.sharedMaterial, sim.Count);
            mr.sharedMaterial = field.MaterialInstance; // 渲染器换用带 buffer 的实例（与运行时同路径）

            Func<Vector2, Vector3> ToWorld = p => new Vector3((p.x - W * 0.5f) / PPU, (H * 0.5f - p.y) / PPU, 0f);

            var env = new PbfEnvironment
            {
                BoundsWidth = W,
                GroundY = GroundYpx,
                TopY = 0f,
                GravityOn = true,   // 重力常开
                Gravity = 800f,
            };

            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;

            void Push() => field.Render(fieldGo.transform, sim, ToWorld, new Color(0.16f, 0.48f, 0.92f));

            var restSize = sim.BoundsSize();
            var impactCaptured = false;
            for (var frame = 0; frame <= 480; frame++)
            {
                if (frame > 0)
                    sim.StepFrame(1f / 60f, env);
                Push();

                if (frame == 1)
                    Snap(cam, rt, "pbf_initial.png");
                if (!impactCaptured && sim.SquashPulse > 0.35f)
                {
                    impactCaptured = true;
                    Snap(cam, rt, "pbf_impact.png");
                }
                if (frame == 240) // 落定趴姿
                {
                    Snap(cam, rt, "pbf_settled.png");
                    Closeup(cam, rt, sim, "closeup_settled.png");
                }

                // ── 真实拖拽复现（用户核心抱怨的交互）：抓"左上偏心点"，
                //    提起→右移→途中抓拍。若实现退化成正圆贴图，此处立即现形
                if (frame == 260)
                {
                    var grabPoint = sim.Centroid + new Vector2(-40f, -30f); // 偏心：左上角
                    sim.TryGrab(grabPoint);
                }
                if (frame > 260 && frame <= 420)
                {
                    var k = (frame - 260) / 160f; // 0→1
                    // 提起 140px 后水平右移 160px，带一点弧线
                    var target = new Vector2(520f + 160f * k, 240f + 30f * Mathf.Sin(k * Mathf.PI));
                    sim.MoveGrab(target, frame * 16.7f);
                }
                if (frame == 320 || frame == 420) // 拖拽途中 / 结束（悬挂态）
                {
                    Snap(cam, rt, frame == 320 ? "drag_moving.png" : "drag_hold.png");
                    Closeup(cam, rt, sim, frame == 320 ? "closeup_drag_moving.png" : "closeup_drag_hold.png");
                }
                if (frame == 424)
                    sim.Release(350f, 800f, 2f, true);

                if (frame == 480 && refGo != null)
                {
                    refGo.SetActive(true); // 同框对比：左原版 / 右 PBF
                    Snap(cam, rt, "combined_compare.png");
                }
            }

            Debug.Log($"[SlimeSnapshot] restSize={restSize:F0}(期望≈160x101) " +
                      $"settledSize={sim.BoundsSize():F0} centroid={sim.Centroid:F0} " +
                      $"rho={sim.AverageDensity():F4}/{sim.Rho0:F4}");
        }

        static void Snap(Camera cam, RenderTexture rt, string file)
        {
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, file), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[SlimeSnapshot] 已保存 " + file);
        }

        /// <summary>特写镜头：相机推近史莱姆质心（视野 ~300px 高），拍完复原。</summary>
        static void Closeup(Camera cam, RenderTexture rt, SlimePbf sim, string file)
        {
            var c = sim.Centroid;
            var homePos = cam.transform.position;
            var homeSize = cam.orthographicSize;
            cam.transform.position = new Vector3((c.x - W * 0.5f) / PPU, (H * 0.5f - c.y) / PPU, -10f);
            cam.orthographicSize = 1.5f;
            Snap(cam, rt, file);
            cam.transform.position = homePos;
            cam.orthographicSize = homeSize;
        }
    }
}
