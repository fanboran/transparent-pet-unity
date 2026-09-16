// ============================================================================
// PetRefractLayer.cs — 折射捕获相机：PetRefract 层 → 全局纹理 _PetRTTex
// ============================================================================
// 需求：窗口里还有"其他史莱姆"（贴图桌宠，PetRefract 层），液态玻璃要把它们
// 也折射进画面。本组件维护一台手动驱动的正交子相机，只渲染 PetRefract 层，
// 结果写进与主相机等大的 RenderTexture，并以全局纹理名 _PetRTTex 暴露给
// LiquidGlass.shader——玻璃片元在主渲染分支里 tex2D(_PetRTTex, i.uv) 采样，
// 与全屏渲染目标 1:1 对齐（同为 y 向上 UV，无需翻转）。
//
// 契约：
// - PetRefract 层未在 TagManager 配置（NameToLayer 返回 -1）时：告警一次、
//   禁用自身逻辑、不做任何事（不建相机、不建 RT）。
// - 捕获相机 enabled=false + 每帧手动 Render()，绝不自行出图；cullingMask 仅
//   PetRefract 一层——液态玻璃 quad（Default）与 HUD（UI）不会进 RT。
// - RT 尺寸每帧与主相机 pixelWidth/Height 同步（ARGB32 + Bilinear），窗口
//   分辨率变化时自动重建。
// ============================================================================
using UnityEngine;

namespace TransparentPet.Pet
{
    public class PetRefractLayer : MonoBehaviour
    {
        /// <summary>被折射的贴图桌宠所在层名（TagManager 用户层，槽位 8）。</summary>
        public const string RefractLayerName = "PetRefract";

        /// <summary>玻璃 shader 的采样名（LiquidGlass.shader: sampler2D _PetRTTex）。</summary>
        public const string GlobalTexName = "_PetRTTex";

        /// <summary>辅助相机 depth 压到最低（仅文档化身份：enabled=false + 手动 Render，不参与自动渲染）。</summary>
        const int PetCamDepth = -100;

        Camera petCam;           // 折射捕获相机（子物体 PetRefractCam）
        RenderTexture petRT;     // 捕获目标，尺寸随主相机逐帧同步
        bool layerMissingWarned; // 层未配置只告警一次

        void Start()
        {
            // 层尚未配置：告警一次、禁用自身逻辑、不做任何事
            if (LayerMask.NameToLayer(RefractLayerName) < 0)
            {
                WarnLayerMissing();
                enabled = false;
                return;
            }
            EnsurePetCamera();
        }

        void LateUpdate()
        {
            // 运行期兜底：若被外部重新启用而层仍缺失，同样不做任何事（告警只发一次）
            if (LayerMask.NameToLayer(RefractLayerName) < 0)
            {
                WarnLayerMissing();
                return;
            }

            var mainCam = Camera.main;
            if (mainCam == null)
                return; // 场景未装配主相机：无处同步位姿/尺寸

            EnsurePetCamera();

            // 1) 同步位姿与正交尺寸：与主相机同一视野 → RT 与全屏画面 1:1 对齐
            petCam.orthographicSize = mainCam.orthographicSize;
            petCam.transform.SetPositionAndRotation(
                mainCam.transform.position, mainCam.transform.rotation);

            // 2) RT 尺寸跟随主相机像素尺寸（变了才重建），随后手动渲染一帧
            var w = mainCam.pixelWidth;
            var h = mainCam.pixelHeight;
            if (w <= 0 || h <= 0)
                return;
            if (petRT == null || petRT.width != w || petRT.height != h)
                RecreateTarget(w, h);

            petCam.Render();

            // 3) 以全局名喂给玻璃 shader（每帧刷新，域重载/新材质也始终有效）
            Shader.SetGlobalTexture(GlobalTexName, petRT);
        }

        /// <summary>
        /// 创建 "PetRefractCam" 子相机：enabled=false（手动渲染）、正交、透明纯色清屏、
        /// cullingMask 仅 PetRefract 层（玻璃 quad 与 HUD 不进 RT）；orthographicSize
        /// 与位姿随后每帧从主相机复制。
        /// </summary>
        void EnsurePetCamera()
        {
            if (petCam != null)
                return;

            var go = new GameObject("PetRefractCam");
            go.transform.SetParent(transform, false);
            petCam = go.AddComponent<Camera>();

            // 手动渲染：绝不自行出图（depth 仅文档化辅助相机身份）
            petCam.enabled = false;
            petCam.depth = PetCamDepth;
            petCam.orthographic = true;

            // 只看得见 PetRefract 层：液态玻璃 quad（Default）、HUD（UI）一律不进 RT
            petCam.cullingMask = 1 << LayerMask.NameToLayer(RefractLayerName);

            // 清屏恒为透明纯色：无宠物处 pet.a=0，玻璃 shader 端零影响
            petCam.clearFlags = CameraClearFlags.SolidColor;
            petCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            petCam.useOcclusionCulling = false;
        }

        /// <summary>按主相机像素尺寸（重）建捕获 RT：ARGB32 + Bilinear，带 24 位深度供精灵排序。</summary>
        void RecreateTarget(int w, int h)
        {
            if (petRT != null)
            {
                petRT.Release();
                Destroy(petRT);
            }

            petRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                name = "PetRefractRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            petRT.Create();
            petCam.targetTexture = petRT;
        }

        void WarnLayerMissing()
        {
            if (layerMissingWarned)
                return;
            layerMissingWarned = true;
            Debug.LogWarning(
                $"[PetRefractLayer] 层 {RefractLayerName} 未在 TagManager 配置" +
                "（Project Settings → Tags and Layers），玻璃折射其他史莱姆的功能不工作。");
        }

        void OnDestroy()
        {
            // 清理捕获相机（连同子 GO）与 RT；全局纹理引用随 RT 销毁自然失效
            if (petCam != null)
                Destroy(petCam.gameObject);
            if (petRT != null)
            {
                petRT.Release();
                Destroy(petRT);
            }
            petCam = null;
            petRT = null;
        }
    }
}
