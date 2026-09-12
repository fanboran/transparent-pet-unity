// ============================================================================
// SlimeSnapshot.cs — 无头渲染快照：PBF 史莱姆关键帧 vs 原版烘焙图对比
// ============================================================================
// 用途（视觉锚定验收）：batchmode 下搭建"只有底部一个地面"的极简测试世界，
// 重力常开，把 PBF 史莱姆从空中落到地面；同场景同底色同比例渲染 git 历史
// 里的原版烘焙贴图（PetSlime_ref.png，需先放到输出目录），产出对比 PNG。
// 运行：-executeMethod TransparentPet.EditorTools.SlimeSnapshot.CaptureHeadless
// 输出：C:/Users/fanbo/AppData/Local/Temp/pet-snapshot/*.png
// ============================================================================
using System.IO;
using System.Collections.Generic;
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
            Random.InitState(42); // 撒点抖动可复现

            // ── 相机与极简世界：纯色背景 + 一条地面线 ──
            var camGo = new GameObject("SnapCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = H * 0.5f / PPU;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f, 1f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(ground.GetComponent<Collider>());
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
                // 路径底边在画布 y=121，画布中心 y=66 → 路径底距中心 55px，
                // 摆到"路径底贴地"：中心 y = 地面 + 0.55 世界单位。
                refGo.transform.position = new Vector3(-2.0f, (H * 0.5f - GroundYpx) / PPU + 0.55f, 0f);
                refGo.transform.localScale = Vector3.one * 0.25f;
                refGo.SetActive(false); // 单独镜头再开
            }
            else
            {
                Debug.LogWarning($"[SlimeSnapshot] 未找到原版参考图 {refPath}，跳过对比渲染");
            }

            // ── PBF 史莱姆：重力常开，从空中落到地面 ──
            var mat = new Material(Shader.Find("TransparentPet/SlimeLiquid"));
            var mf = new GameObject("PbfSlime").AddComponent<MeshFilter>();
            var mr = mf.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.sortingOrder = 10;

            var sim = new SlimePbf(new Vector2(560f, 150f), 80f); // 半宽 80 = 原版路径宽 160px
            var env = new PbfEnvironment
            {
                BoundsWidth = W,
                GroundY = GroundYpx,
                TopY = 0f,
                GravityOn = true,   // 重力常开（用户拍板的新语义）
                Gravity = 800f,
            };

            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;

            var restSize = sim.BoundsSize();
            var impactCaptured = false;
            for (var frame = 0; frame <= 360; frame++)
            {
                if (frame > 0)
                    sim.StepFrame(1f / 60f, env);
                Rebuild(mf, sim);

                if (frame == 1)
                    Snap(cam, rt, "pbf_initial.png");
                if (!impactCaptured && sim.SquashPulse > 0.35f)
                {
                    impactCaptured = true;
                    Snap(cam, rt, "pbf_impact.png");
                }
                if (frame == 240) // 落定趴姿（重力常开平衡态）
                    Snap(cam, rt, "pbf_settled.png");
                if (frame == 360 && refGo != null)
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
            Object.DestroyImmediate(tex);
            Debug.Log("[SlimeSnapshot] 已保存 " + file);
        }

        static void Rebuild(MeshFilter mf, SlimePbf sim)
        {
            var verts = new List<Vector3>(1024);
            var colors = new List<Color>(1024);
            var tris = new List<int>(3072);
            if (DensitySurface.Build(sim.Positions, sim.EffectiveH, sim.Rho0,
                    p => new Vector3((p.x - W * 0.5f) / PPU, (H * 0.5f - p.y) / PPU, 0f),
                    verts, colors, tris))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null)
                {
                    mesh = new Mesh { name = "SnapshotSlime" };
                    mesh.MarkDynamic();
                    mf.sharedMesh = mesh;
                }
                mesh.Clear(false);
                mesh.SetVertices(verts);
                mesh.SetColors(colors);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
            }
        }
    }
}
