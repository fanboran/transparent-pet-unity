// ============================================================================
// ProductShots.cs — 作品集门面图渲染（batchmode）：静帧 hero + 呼吸/抛掷 GIF 帧序列
// ============================================================================
// 复用真实物理（ThrowPhysics）与真实表现数学（PetLifeMath + LifeTuning），
// 渲染出的每一帧即运行时行为的离线复现——"图 = 真实行为"。
// 运行：-executeMethod TransparentPet.EditorTools.ProductShots.CaptureHeadless
// 输出：docs/images/hero.png + docs/images/_frames_{breathe,throw}/frame_*.png
//       帧序列随后由 tools/make_gif.py 合成 GIF（并清理帧目录）
// ============================================================================
using System.IO;
using TransparentPet.Pet;
using UnityEditor;
using UnityEngine;
using TransparentPet.Pet.Common;
using TransparentPet.Pet.Textured;

namespace TransparentPet.EditorTools
{
    public static class ProductShots
    {
        const float PPU = 100f;
        const float BaseScale = 0.25f; // 与 SvgPetController 基准缩放一致

        // ── 静帧 hero（作品集主图）──
        const int HeroW = 760, HeroH = 420;

        // ── 呼吸 GIF：整周期无缝循环（3.6s @20fps = 72 帧）──
        const int BreatheW = 300, BreatheH = 200;
        const float BreatheFps = 20f;
        const int BreatheFrames = 72;

        // ── 抛掷 GIF：静息 → 抓起拖拽 → 抛出 → 落地挤压 → 回弹落定 ──
        // 画面高度留足纵向行程（拖起 80px + 抛物线上冲 ~40px 都在画内）
        const int ThrowW = 420, ThrowH = 300;
        const float ThrowFps = 30f;
        const int RestFrames = 18;  // 0.6s 静息
        const int DragFrames = 20;  // 0.67s 拖拽（170px → 约 510px/s 抛速）
        const int ThrowTotalFrames = 108;

        const float GroundMargin = 30f; // 地面线离画面底部距离

        // 抛掷跟拍：平滑跟随半衰期（秒，越小越紧跟；滞后带来的"宠物领先"即运动感来源）
        const float CameraFollowHalfLife = 0.12f;

        static readonly Color BgColor = new Color(0.12f, 0.13f, 0.17f, 1f);
        static readonly Color GroundColor = new Color(0.25f, 0.27f, 0.34f, 1f);

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string OutDir => Path.Combine(RepoRoot, "docs", "images");

        [MenuItem("TransparentPet/渲染作品集门面图")]
        public static void CaptureFromMenu() => CaptureHeadless();

        public static void CaptureHeadless()
        {
            Directory.CreateDirectory(OutDir);

            var petSprite = LoadPetSprite();
            if (petSprite == null)
            {
                Debug.LogError("[ProductShots] 找不到宠物贴图，渲染中止: " + SceneGeneratorTexturePath);
                return;
            }

            var shadowSprite = CreateRadialSprite(128, new Color(0f, 0f, 0f, 1f), 0.9f);
            var cursorSprite = CreateRadialSprite(64, new Color(1f, 1f, 1f, 1f), 1f);

            CaptureHero(petSprite, shadowSprite);
            CaptureBreathe(petSprite, shadowSprite);
            CaptureThrow(petSprite, shadowSprite, cursorSprite);

            Debug.Log("[ProductShots] 全部帧渲染完成 → " + OutDir);
        }

        const string SceneGeneratorTexturePath = "Assets/Art/Pet/PetSlime.png";

        static Sprite LoadPetSprite()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(SceneGeneratorTexturePath);
            if (texture == null)
                return null;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), PPU);
        }

        // ── 静帧 hero：静息 + 呼吸峰值相位 + 贴地柔影 ──

        static void CaptureHero(Sprite petSprite, Sprite shadowSprite)
        {
            var root = new GameObject("HeroShot");
            var cam = BuildScene(root, HeroW, HeroH);
            var groundY = HeroH - 60f;
            AddGroundLine(root, HeroW, HeroH, groundY);

            var pet = CreatePet(root, petSprite);
            var shadow = CreateShadow(root, shadowSprite);

            // 呼吸相位取峰值（吸气最饱满的一刻）
            const float phaseTime = LifeTuning.BreathePeriod * 0.25f;
            var breathe = PetLifeMath.BreatheScale(phaseTime, LifeTuning.BreatheAmplitude, LifeTuning.BreathePeriod);
            var bobPx = PetLifeMath.FloatOffset(phaseTime, LifeTuning.FloatAmplitudePx, LifeTuning.BreathePeriod, 0f);

            var petLogic = new Vector2(HeroW * 0.5f, groundY - HalfHeightPx(petSprite));
            var world = ToWorld(petLogic, HeroW, HeroH);
            var groundWorldY = ToWorld(new Vector2(0f, groundY), HeroW, HeroH).y;
            pet.transform.localScale = Vector3.one * (BaseScale * breathe);
            pet.transform.position = new Vector3(world.x, world.y + bobPx / PPU, 0f);

            shadow.transform.position = new Vector3(world.x, groundWorldY, 0.1f);
            shadow.transform.localScale = new Vector3(1.5f, 1.5f * 0.36f, 1f);
            shadow.color = new Color(0f, 0f, 0f, 0.4f);

            var rt = new RenderTexture(HeroW, HeroH, 24);
            SaveFrame(cam, rt, Path.Combine(OutDir, "hero.png"));
            rt.Release();

            Object.DestroyImmediate(root);
            Object.DestroyImmediate(rt);
            Debug.Log("[ProductShots] hero.png 完成");
        }

        // ── 呼吸 GIF：整周期无缝循环 ──

        static void CaptureBreathe(Sprite petSprite, Sprite shadowSprite)
        {
            var root = new GameObject("BreatheShot");
            var cam = BuildScene(root, BreatheW, BreatheH);
            var groundY = BreatheH - GroundMargin;
            AddGroundLine(root, BreatheW, BreatheH, groundY);

            var pet = CreatePet(root, petSprite);
            var shadow = CreateShadow(root, shadowSprite);

            var petLogic = new Vector2(BreatheW * 0.5f, groundY - HalfHeightPx(petSprite));
            var world = ToWorld(petLogic, BreatheW, BreatheH);
            var groundWorldY = ToWorld(new Vector2(0f, groundY), BreatheW, BreatheH).y;
            shadow.transform.position = new Vector3(world.x, groundWorldY, 0.1f);
            shadow.transform.localScale = new Vector3(1.35f, 1.35f * 0.36f, 1f);
            shadow.color = new Color(0f, 0f, 0f, 0.35f);

            var dir = Path.Combine(OutDir, "_frames_breathe");
            Directory.CreateDirectory(dir);
            ClearFrames(dir);
            var rt = new RenderTexture(BreatheW, BreatheH, 24);

            var dt = 1f / BreatheFps;
            for (var frame = 0; frame < BreatheFrames; frame++)
            {
                var time = frame * dt;
                var breathe = PetLifeMath.BreatheScale(time, LifeTuning.BreatheAmplitude, LifeTuning.BreathePeriod);
                var bobPx = PetLifeMath.FloatOffset(time, LifeTuning.FloatAmplitudePx, LifeTuning.BreathePeriod, 0f);

                pet.transform.localScale = Vector3.one * (BaseScale * breathe);
                pet.transform.position = new Vector3(world.x, world.y + bobPx / PPU, 0f);

                SaveFrame(cam, rt, Path.Combine(dir, $"frame_{frame:000}.png"));
            }

            rt.Release();
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(rt);
            Debug.Log($"[ProductShots] breathe 帧序列完成（{BreatheFrames} 帧）");
        }

        // ── 抛掷 GIF：与运行时同源的物理 + 表现 ──

        static void CaptureThrow(Sprite petSprite, Sprite shadowSprite, Sprite cursorSprite)
        {
            var root = new GameObject("ThrowShot");
            var cam = BuildScene(root, ThrowW, ThrowH);
            var groundY = ThrowH - GroundMargin;
            AddGroundLine(root, ThrowW, ThrowH, groundY);

            var pet = CreatePet(root, petSprite);
            var shadow = CreateShadow(root, shadowSprite);
            var cursorGo = new GameObject("Cursor");
            cursorGo.transform.SetParent(root.transform);
            var cursor = cursorGo.AddComponent<SpriteRenderer>();
            cursor.sprite = cursorSprite;
            cursor.sortingOrder = 20;
            cursor.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            cursor.transform.localScale = Vector3.one * 0.22f;
            cursor.color = new Color(1f, 1f, 1f, 1f);
            cursorGo.SetActive(false);

            var shadowWorldY = ToWorld(new Vector2(0f, groundY), ThrowW, ThrowH).y;

            // 物理与表现状态（运行时同款）
            var physics = new ThrowPhysics();
            var petLogic = new Vector2(ThrowW * 0.5f, groundY - HalfHeightPx(petSprite));
            var squash = new PetLifeMath.SquashState();
            var tilt = 0f;
            var prevVerticalVelocity = 0f;
            var onGround = true;
            var time = 0f;
            var cursorPos = Vector2.zero;
            var grabStart = Vector2.zero;
            var camX = ToWorld(petLogic, ThrowW, ThrowH).x;

            var spriteSize = new Vector2(petSprite.texture.width, petSprite.texture.height);
            // 左右墙推到画面外很远：演示"全屏桌面上的自由飞行"（相机跟拍保证不出画）
            var screenSize = new Vector2(2000f, groundY);

            var dir = Path.Combine(OutDir, "_frames_throw");
            Directory.CreateDirectory(dir);
            ClearFrames(dir);
            var rt = new RenderTexture(ThrowW, ThrowH, 24);
            var dt = 1f / ThrowFps;

            for (var frame = 0; frame < ThrowTotalFrames; frame++)
            {
                // ── 输入脚本（复现一次真实交互：抓偏心点 → 右上快拖 → 松手抛出）──
                if (frame == RestFrames)
                {
                    cursorPos = petLogic + new Vector2(26f, -6f); // 偏心抓取点
                    grabStart = cursorPos;
                    physics.DragBegin(cursorPos, petLogic, time * 1000f);
                }
                else if (frame > RestFrames && frame < RestFrames + DragFrames)
                {
                    var k = (frame - RestFrames) / (float)DragFrames;
                    cursorPos = Vector2.Lerp(grabStart, grabStart + new Vector2(170f, -80f), k);
                    petLogic = physics.DragMove(cursorPos, time * 1000f);
                }
                else if (frame == RestFrames + DragFrames)
                {
                    physics.DragEnd(); // 初速 ≈ 拖拽速度 × 倍率
                }
                else if (physics.IsThrowing)
                {
                    var wasOnGround = onGround;
                    var result = physics.Step(petLogic, dt, screenSize, spriteSize, BaseScale);
                    petLogic = result.Position;
                    onGround = result.HitGround;

                    if (result.HitGround && !wasOnGround)
                        squash.Value += PetLifeMath.ImpactImpulse(Mathf.Abs(prevVerticalVelocity),
                            LifeTuning.ImpactDeadZone, LifeTuning.ImpactReferenceSpeed, LifeTuning.ImpactMaxSquash);
                    prevVerticalVelocity = result.Velocity.y;
                }

                // ── 生命感表现（与 PetLifeVisual 同一套数学与常量）──
                var active = physics.IsDragging || physics.IsThrowing;
                var lifeGain = active ? LifeTuning.ActiveLifeGain : 1f;
                var breathe = PetLifeMath.BreatheScale(time, LifeTuning.BreatheAmplitude * lifeGain, LifeTuning.BreathePeriod);
                var bobPx = PetLifeMath.FloatOffset(time, LifeTuning.FloatAmplitudePx * lifeGain, LifeTuning.BreathePeriod, 0f);
                squash = PetLifeMath.StepSquash(squash, dt, LifeTuning.SquashStiffness, LifeTuning.SquashDamping);
                squash.Value = Mathf.Clamp(squash.Value, -0.6f, 0.6f);
                var targetTilt = physics.IsDragging
                    ? PetLifeMath.TiltAngle(physics.LastFrameVelocity.x, LifeTuning.TiltSpeedForMax, LifeTuning.TiltMaxDegrees)
                    : 0f;
                tilt = PetLifeMath.Approach(tilt, targetTilt, dt, LifeTuning.TiltHalfLife);

                // ── 应用变换（与 PetLifeVisual 一致）──
                pet.transform.localScale = new Vector3(
                    BaseScale * breathe * (1f + squash.Value),
                    BaseScale * breathe * (1f - squash.Value),
                    1f);
                pet.transform.rotation = Quaternion.Euler(0f, 0f, tilt);
                var world = ToWorld(petLogic, ThrowW, ThrowH);
                var spriteWorldHeight = petSprite.bounds.size.y * BaseScale;
                var groundCompPx = -squash.Value * spriteWorldHeight * 0.5f * LifeTuning.GroundSquashComp * PPU;
                pet.transform.position = new Vector3(world.x, world.y + (bobPx + groundCompPx) / PPU, 0f);

                // ── 软阴影：贴地最实，离地越高越小越淡 ──
                var bottomPx = petLogic.y + spriteWorldHeight * 0.5f * PPU;
                var heightPx = Mathf.Max(0f, groundY - bottomPx);
                var shadowK = Mathf.Clamp01(1f - heightPx / 320f);
                var shadowWidth = 1.25f * (0.55f + 0.45f * shadowK);
                shadow.transform.position = new Vector3(world.x, shadowWorldY, 0.1f);
                shadow.transform.localScale = new Vector3(shadowWidth, shadowWidth * 0.36f, 1f);
                shadow.color = new Color(0f, 0f, 0f, 0.35f * shadowK);

                // ── 光标：仅拖拽期间可见（表达"人在拖"）──
                if (physics.IsDragging)
                {
                    cursorGo.SetActive(true);
                    var cw = ToWorld(cursorPos, ThrowW, ThrowH);
                    cursorGo.transform.position = new Vector3(cw.x, cw.y, -0.1f);
                }
                else
                {
                    cursorGo.SetActive(false);
                }

                // ── 相机：平滑跟随宠物 x（滞后量随速度自然增大，快速飞行时即"跟拍"感）──
                camX = Mathf.Lerp(camX, world.x, 1f - Mathf.Pow(0.5f, dt / CameraFollowHalfLife));
                cam.transform.position = new Vector3(camX, 0f, -10f);

                SaveFrame(cam, rt, Path.Combine(dir, $"frame_{frame:000}.png"));
                time += dt;
            }

            rt.Release();
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(rt);
            Debug.Log($"[ProductShots] throw 帧序列完成（{ThrowTotalFrames} 帧）");
        }

        // ── 场景搭建 ──

        static Camera BuildScene(GameObject root, int w, int h)
        {
            var camGo = new GameObject("ShotCam");
            camGo.transform.SetParent(root.transform);
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = h * 0.5f / PPU;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BgColor;
            cam.allowHDR = false;
            return cam;
        }

        static void AddGroundLine(GameObject root, int w, int h, float groundY)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "GroundLine";
            go.transform.SetParent(root.transform);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Unlit/Color")) { color = GroundColor };
            // 足够宽以覆盖抛掷跟拍的相机行程（±100 世界单位）
            go.transform.localScale = new Vector3(220f, 0.02f, 1f);
            go.transform.position = new Vector3(0f, ToWorld(new Vector2(0f, groundY), w, h).y, 0.5f);
        }

        static SpriteRenderer CreatePet(GameObject root, Sprite sprite)
        {
            var go = new GameObject("ShopPet");
            go.transform.SetParent(root.transform);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            return sr;
        }

        static SpriteRenderer CreateShadow(GameObject root, Sprite sprite)
        {
            var go = new GameObject("Shadow");
            go.transform.SetParent(root.transform);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 1;
            sr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            sr.color = new Color(0f, 0f, 0f, 0.45f);
            return sr;
        }

        /// <summary>程序化径向渐变圆形精灵（柔软阴影 / 光标圆点），PPU=尺寸 → 世界尺寸 1</summary>
        static Sprite CreateRadialSprite(int size, Color color, float power)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var center = (size - 1) * 0.5f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - center) / center;
                    var dy = (y - center) / center;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(1f - d);
                    a = Mathf.Pow(a, power);
                    pixels[y * size + x] = new Color32(
                        (byte)(color.r * 255f), (byte)(color.g * 255f), (byte)(color.b * 255f), (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // ── 工具 ──

        static float HalfHeightPx(Sprite sprite) => sprite.bounds.size.y * BaseScale * 0.5f * PPU;

        static void ClearFrames(string dir)
        {
            foreach (var file in Directory.GetFiles(dir, "frame_*.png"))
                File.Delete(file);
        }

        static void SaveFrame(Camera cam, RenderTexture rt, string path)
        {
            // 必须把相机输出绑到同一张 RT 再 Render：否则渲染进了屏幕、读出的是一张空 RT（全白）
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        static Vector2 ToWorld(Vector2 screenPos, int w, int h) => new Vector2(
            (screenPos.x - w * 0.5f) / PPU,
            (h * 0.5f - screenPos.y) / PPU);
    }
}
