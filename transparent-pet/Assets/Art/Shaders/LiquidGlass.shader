// ================================================================
// ██████  TransparentPet/LiquidGlass —— 液态玻璃史莱姆主合成着色器
// ================================================================
// 移植自姊妹 Godot 项目 modules/effects/shaders/liquid_glass_main.gdshader
// （其源头是参考 liquid-glass-studio 的 WebGL 实现）。管线结构一致：
// SDF 形状 → 折射（斯涅尔定律简化模型）→ 色散（RGB 三通道分离采样）
// → 菲涅尔边缘增亮 → 眩光（内部反射亮斑）→ 色调 → 抗锯齿。
//
// 【与 Godot 版的三处有意差异】
//   1. 去掉 DPR：量纲统一为物理像素，偏移/厚度全部按像素换算。
//   2. 折射偏移用归一化法线方向：Godot 版 getNormal 返回放大 1414 倍的
//      梯度，偏移量纲实际失效；此处 offset = -n̂ · edgeFactor · _RefThickness
//      （像素），行为可控可调。
//   3. alpha 契约（透明窗口）：玻璃轮廓内 alpha=1（可交互命中阈值 0.35 之上），
//      轮廓外只保留一圈淡阴影（alpha≈0.3 以下，鼠标穿透），其余完全透明——
//      桌宠窗口是覆盖桌面的全屏透明层，玻璃外不能挡桌面。
//      折射采样的"背景"由 LiquidGlassBg 生成的程序化素材提供（棋盘格等），
//      只在玻璃内部被"看见"：像一块封着花纹世界的透镜浮在桌面上。
//
// 形状 SDF（sdSlime）与 CPU 侧命中判定 LiquidGlassSlimeSdf.cs 同源：
// 贝塞尔控制点两处共用同一组常量，改形状必须两处同步改。
//
// 调试视图：_Step 0=SDF 白色梯度 1=SDF 等高线 2=法线彩虹图 9=主渲染。
// ================================================================

Shader "TransparentPet/LiquidGlass"
{
    Properties
    {
        _Bg ("清晰背景素材", 2D) = "white" {}
        _BlurredBg ("模糊背景素材", 2D) = "white" {}
        _Resolution ("渲染分辨率", Vector) = (1920, 1080, 0, 0)
        _Step ("调试 STEP", Float) = 9

        [Header(Refraction)]
        _RefThickness ("折射厚度(px)", Float) = 45
        _RefFactor ("折射率 n", Float) = 1.45
        _RefDispersion ("色散强度", Float) = 7
        [Header(Fresnel)]
        _RefFresnelRange ("菲涅尔范围(px)", Float) = 30
        _RefFresnelHardness ("菲涅尔硬度", Float) = 0.2
        _RefFresnelFactor ("菲涅尔强度", Range(0, 1)) = 0.35
        [Header(Glare)]
        _GlareRange ("眩光范围(px)", Float) = 30
        _GlareHardness ("眩光硬度", Float) = 0.2
        _GlareConvergence ("眩光汇聚", Range(0, 1)) = 0.5
        _GlareOppositeFactor ("背面眩光因子", Range(0, 1)) = 0.8
        _GlareFactor ("眩光强度", Range(0, 1)) = 0.9
        _GlareAngle ("眩光角度(度)", Float) = -45
        [Header(Shape)]
        _MergeRate ("融合宽度(SDF空间)", Range(0.001, 0.5)) = 0.05
        _BlurEdge ("边缘模糊(0=渐进 1=全模糊)", Float) = 1
        [Header(Shadow)]
        _ShadowExpand ("阴影扩散(px)", Float) = 26
        _ShadowFactor ("阴影强度", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            sampler2D _Bg;
            sampler2D _BlurredBg;
            sampler2D _PetRTTex;   // PetRefract 层的画面(其他史莱姆),PetRefractLayer 每帧更新
            float4 _Resolution;
            int _Step;

            float _RefThickness;
            float _RefFactor;
            float _RefDispersion;
            float _RefFresnelRange;
            float _RefFresnelHardness;
            float _RefFresnelFactor;
            float _GlareRange;
            float _GlareHardness;
            float _GlareConvergence;
            float _GlareOppositeFactor;
            float _GlareFactor;
            float _GlareAngle; // 已换算为弧度
            float _MergeRate;
            float _BlurEdge;
            float _ShadowExpand;
            float _ShadowFactor;

            // 物品数组（与 CPU 端 LiquidGlassController 一一对应）：
            // xy = 中心（GL 像素，左下原点）；rgb = 该只的种类基色（CharacterRegistry 预设），
            // a = 着色强度。多只相邻时 smin 融合，颜色按同一权重过渡——两色玻璃融出渐变色。
            #define MAX_ITEMS 3
            float4 _ItemPositions[MAX_ITEMS];
            float _ItemWidths[MAX_ITEMS];
            float _ItemScales[MAX_ITEMS];
            float _ItemEnabled[MAX_ITEMS];
            float4 _ItemTints[MAX_ITEMS];

            #define PI 3.14159265359

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }

            // ── 色散常数：三通道相对折射率（蓝光偏折多、红光偏折少）──
            static const float N_R = 0.98;
            static const float N_G = 1.0;
            static const float N_B = 1.02;

            // ================================================================
            // 史莱姆形状 SDF —— 与 LiquidGlassSlimeSdf.cs 同源（控制点勿单边修改）
            // SVG 空间：x∈[-0.4,0.4]、y∈[-0.231,+0.275]，y 向下（Godot 原生语义）：
            // -0.231 端是圆穹顶、+0.275 端是平底边（趴姿布丁轮廓）。
            // 宽高比 0.8:0.506 = 160:101（与原版烘焙图关键数值同源：静息半宽 80）
            // ================================================================
            float2 bezier3(float2 a, float2 b, float2 c, float2 d, float t)
            {
                float u = 1.0 - t;
                return u*u*u * a + 3.0*u*u*t * b + 3.0*u*t*t * c + t*t*t * d;
            }

            float sdBezier(float2 p, float2 a, float2 b, float2 c, float2 d)
            {
                float best_t = 0.0;
                float best_d2 = 1e10;

                const int SAMPLES = 12;
                for (int i = 0; i <= SAMPLES; i++)
                {
                    float t = float(i) / float(SAMPLES);
                    float2 q = bezier3(a, b, c, d, t);
                    float d2 = dot(q - p, q - p);
                    if (d2 < best_d2) { best_d2 = d2; best_t = t; }
                }

                float t = clamp(best_t, 0.0, 1.0);
                for (int i = 0; i < 6; i++)
                {
                    float2 B = bezier3(a, b, c, d, t);
                    float u = 1.0 - t;
                    float2 dB = 3.0*u*u*(b-a) + 6.0*u*t*(c-b) + 3.0*t*t*(d-c);
                    float2 ddB = 6.0*u*(c - 2.0*b + a) + 6.0*t*(d - 2.0*c + b);
                    float f = dot(B - p, dB);
                    float df = dot(dB, dB) + dot(B - p, ddB);
                    if (abs(df) < 1e-9) break;
                    t -= f / df;
                    t = clamp(t, 0.0, 1.0);
                }
                return length(bezier3(a, b, c, d, t) - p);
            }

            float sdSegment(float2 p, float2 a, float2 b)
            {
                float2 pa = p - a, ba = b - a;
                float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
                return length(pa - ba * h);
            }

            // 给定高度 y 反解轮廓半宽（对上/下段贝塞尔二分求逆）
            float halfWidth(float y)
            {
                // 与 sdSlime 相同的控制点
                #define RU_A float2(0.0, -0.231)
                #define RU_B float2(0.25, -0.231)
                #define RU_C float2(0.4, -0.055)
                #define RU_D float2(0.4, 0.11)
                #define RL_A float2(0.4, 0.11)
                #define RL_B float2(0.4, 0.22)
                #define RL_C float2(0.325, 0.275)
                #define RL_D float2(0.25, 0.275)

                float lo = 0.0, hi = 1.0;
                if (y <= 0.11)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        float mid = (lo + hi) * 0.5;
                        if (bezier3(RU_A, RU_B, RU_C, RU_D, mid).y < y) lo = mid; else hi = mid;
                    }
                    return bezier3(RU_A, RU_B, RU_C, RU_D, (lo + hi) * 0.5).x;
                }
                for (int i = 0; i < 20; i++)
                {
                    float mid = (lo + hi) * 0.5;
                    if (bezier3(RL_A, RL_B, RL_C, RL_D, mid).y < y) lo = mid; else hi = mid;
                }
                return bezier3(RL_A, RL_B, RL_C, RL_D, (lo + hi) * 0.5).x;
            }

            float sdSlime(float2 p)
            {
                float ax = abs(p.x);

                float d_ru = sdBezier(float2(ax, p.y),
                    float2(0.0, -0.231), float2(0.25, -0.231), float2(0.4, -0.055), float2(0.4, 0.11));
                float d_rl = sdBezier(float2(ax, p.y),
                    float2(0.4, 0.11), float2(0.4, 0.22), float2(0.325, 0.275), float2(0.25, 0.275));
                float d_bt = sdSegment(float2(ax, p.y), float2(0.25, 0.275), float2(-0.25, 0.275));

                float d = min(min(d_ru, d_rl), d_bt);

                bool inside = p.y > -0.232 && p.y < 0.276 && ax <= halfWidth(p.y);
                return inside ? -d : d;
            }

            // 归一化空间 SDF。坐标系：y 向下（top-origin）——Godot 4 屏幕空间的原生
            // 语义，sdSlime 的平底（+0.275 端）因此落在下方；与 CPU 侧
            // LiquidGlassSlimeSdf.Hits 的输入语义一致（"所见即所点"的数学同源）。
            // _ItemPositions.xy 直接用逻辑坐标（左上原点），无需翻转。
            float getItemSDF(int index, float2 pixelTopDown)
            {
                if (_ItemEnabled[index] < 0.5)
                    return 1.0; // 禁用槽位 = 一个屏高之外

                float2 pn = (pixelTopDown - _ItemPositions[index].xy) / _Resolution.y;
                float span = _ItemWidths[index] * _ItemScales[index];
                float slimeD = sdSlime(pn * _Resolution.y / span);
                return slimeD * span / _Resolution.y;
            }

            // smin 平滑融合（多物品 metaball 式合并；单物品时退化为 min）。
            // 颜色按同一混合权重 h 同步过渡：两色玻璃相邻时融出平滑渐变色。
            // colA 为 inout：融合链上逐物品累积。
            float sminTinted(float a, float b, float k, inout float4 colA, float4 colB)
            {
                float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
                colA = lerp(colB, colA, h);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            float mainSDF(float2 pixelTopDown, out float4 tintOut)
            {
                float result = 1.0;
                float4 col = float4(1.0, 1.0, 1.0, 0.0);
                for (int i = 0; i < MAX_ITEMS; i++)
                {
                    float d = getItemSDF(i, pixelTopDown);
                    float4 itemCol = _ItemTints[i];
                    result = sminTinted(result, d, _MergeRate, col, itemCol);
                }
                tintOut = col;
                return result;
            }

            // SDF 数值梯度 = 表面法线。返回像素空间单位梯度（merged 是归一化距离，
            // 乘回分辨率）；Godot 原版此处乘 1414 的放大系数只为可视化，这里语义化
            float2 getNormal(float2 pixelTopDown)
            {
                float4 tintIgnore; // mainSDF 同时输出种类色，法线计算只关心梯度
                float2 h = float2(max(abs(ddx(pixelTopDown.x)), 0.0001), max(abs(ddy(pixelTopDown.y)), 0.0001));
                float2 grad = float2(
                    mainSDF(pixelTopDown + float2(h.x, 0.0), tintIgnore) - mainSDF(pixelTopDown - float2(h.x, 0.0), tintIgnore),
                    mainSDF(pixelTopDown + float2(0.0, h.y), tintIgnore) - mainSDF(pixelTopDown - float2(0.0, h.y), tintIgnore)
                ) / (2.0 * h);
                return grad * _Resolution.y;
            }

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            // ── 颜色科学：sRGB → XYZ → LAB → LCH 往返（菲涅尔/眩光在 LCH
            //    空间提明度/色度，色相不漂移；矩阵与白点为 CIE 标准值）──
            static const float3 D65_WHITE = float3(0.95045592705, 1.0, 1.08905775076);
            static const float3x3 RGB_TO_XYZ_M = float3x3(
                float3(0.4124, 0.2126, 0.0193),
                float3(0.3576, 0.7152, 0.1192),
                float3(0.1805, 0.0722, 0.9505));
            static const float3x3 XYZ_TO_RGB_M = float3x3(
                float3(3.2406255, -0.9689307, 0.0557101),
                float3(-1.537208, 1.8757561, -0.2040211),
                float3(-0.4986286, 0.0415175, 1.0569959));

            float UNCOMPAND_SRGB(float a)
            {
                return a > 0.04045 ? pow((a + 0.055) / 1.055, 2.4) : a / 12.92;
            }

            float COMPAND_RGB(float a)
            {
                return a <= 0.0031308 ? 12.92 * a : 1.055 * pow(a, 0.41666666666) - 0.055;
            }

            float3 SRGB_TO_RGB(float3 srgb)
            {
                return float3(UNCOMPAND_SRGB(srgb.x), UNCOMPAND_SRGB(srgb.y), UNCOMPAND_SRGB(srgb.z));
            }

            float3 RGB_TO_SRGB(float3 rgb)
            {
                return float3(COMPAND_RGB(rgb.x), COMPAND_RGB(rgb.y), COMPAND_RGB(rgb.z));
            }

            float XYZ_TO_LAB_F(float x)
            {
                return x > 0.00885645167 ? pow(x, 0.333333333) : 7.78703703704 * x + 0.13793103448;
            }

            float LAB_TO_XYZ_F(float x)
            {
                return x > 0.206897 ? x * x * x : 0.12841854934 * (x - 0.137931034);
            }

            float3 XYZ_TO_LAB(float3 xyz)
            {
                float3 s = xyz / D65_WHITE;
                s = float3(XYZ_TO_LAB_F(s.x), XYZ_TO_LAB_F(s.y), XYZ_TO_LAB_F(s.z));
                return float3(116.0 * s.y - 16.0, 500.0 * (s.x - s.y), 200.0 * (s.y - s.z));
            }

            float3 LAB_TO_XYZ(float3 lab)
            {
                float w = (lab.x + 16.0) / 116.0;
                return D65_WHITE * float3(LAB_TO_XYZ_F(w + lab.y / 500.0), LAB_TO_XYZ_F(w), LAB_TO_XYZ_F(w - lab.z / 200.0));
            }

            float3 SRGB_TO_LAB(float3 srgb)
            {
                return XYZ_TO_LAB(mul(SRGB_TO_RGB(srgb), RGB_TO_XYZ_M));
            }

            float3 LAB_TO_LCH(float3 lab)
            {
                return float3(lab.x, sqrt(dot(lab.yz, lab.yz)), atan2(lab.z, lab.y) * 57.2957795131);
            }

            float3 LCH_TO_LAB(float3 lch)
            {
                return float3(lch.x, lch.y * cos(lch.z * 0.01745329251), lch.y * sin(lch.z * 0.01745329251));
            }

            float3 LCH_TO_SRGB(float3 lch)
            {
                return RGB_TO_SRGB(mul(LAB_TO_XYZ(LCH_TO_LAB(lch)), XYZ_TO_RGB_M));
            }

            float vec2ToAngle(float2 v)
            {
                float angle = atan2(v.y, v.x);
                return angle < 0.0 ? angle + 2.0 * PI : angle;
            }

            // RGB 三通道按各自折射率分别偏移采样，再与模糊版逐通道混合
            float4 getTextureDispersion(float mixRate, float2 offset, float factor, float2 uv)
            {
                float bgR = tex2D(_Bg, uv + offset * (1.0 - (N_R - 1.0) * factor)).r;
                float bgG = tex2D(_Bg, uv + offset * (1.0 - (N_G - 1.0) * factor)).g;
                float bgB = tex2D(_Bg, uv + offset * (1.0 - (N_B - 1.0) * factor)).b;

                float blurR = tex2D(_BlurredBg, uv + offset * (1.0 - (N_R - 1.0) * factor)).r;
                float blurG = tex2D(_BlurredBg, uv + offset * (1.0 - (N_G - 1.0) * factor)).g;
                float blurB = tex2D(_BlurredBg, uv + offset * (1.0 - (N_B - 1.0) * factor)).b;

                return float4(lerp(bgR, blurR, mixRate), lerp(bgG, blurG, mixRate), lerp(bgB, blurB, mixRate), 1.0);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 resolution = _Resolution.xy;
                float2 pixel = i.uv * resolution;              // GL 语义（左下原点），用于 UV 采样换算
                float2 pixelTD = float2(pixel.x, resolution.y - pixel.y); // y 向下（top-origin），SDF/法线/眩光统一在此空间计算

                float4 kindTint; // rgb = 融合后的种类基色，a = 着色强度（原味档 0）
                float merged = mainSDF(pixelTD, kindTint);

                float4 outColor;

                if (_Step <= 0)
                {
                    // 调试：SDF 白色梯度
                    float raw = merged > 0.0 ? merged : -merged * 2.0;
                    float3 col = float3(raw, raw, raw) * 3.0;
                    col = lerp(col, float3(1.0, 1.0, 1.0), 1.0 - smoothstep(0.0, 2.0 / resolution.y, abs(merged)));
                    outColor = float4(col, 1.0);
                }
                else if (_Step <= 1)
                {
                    // 调试：SDF 等高线（内外双色 + 余弦条纹）
                    float px = 2.0 / resolution.y;
                    float3 col = merged > 0.0 ? float3(0.9, 0.6, 0.3) : float3(0.65, 0.85, 1.0);
                    col *= 1.0 - exp(-0.03 * abs(merged) * resolution.y);
                    col *= 0.6 + 0.4 * smoothstep(-0.5, 0.5, cos(0.25 * abs(merged) * resolution.y * 2.0));
                    col = lerp(col, float3(1.0, 1.0, 1.0), 1.0 - smoothstep(1.5 / resolution.y - px, 1.5 / resolution.y + px, abs(merged)));
                    outColor = float4(col, 1.0);
                }
                else if (_Step <= 2)
                {
                    // 调试：法线彩虹图（平滑过渡 = 法线连续；角度在 y 向下空间，hue 与 GL 惯例差 180°，仅诊断用）
                    if (merged < 0.0)
                    {
                        float2 normal = getNormal(pixelTD);
                        float angle = atan2(normal.y, normal.x);
                        float hue = (angle < 0.0 ? angle + 2.0 * PI : angle) / (2.0 * PI);
                        outColor = float4(hsv2rgb(float3(hue, 1.0, 1.0)), length(normal));
                    }
                    else
                        outColor = float4(0.8, 0.8, 0.8, 0.0);
                }
                else
                {
                    // ======== 主渲染：折射/色散/菲涅尔/眩光/色调 ========
                    if (merged < 0.005)
                    {
                        float nmerged = -merged * resolution.y; // 到边缘的深度（px）
                        float2 normal = normalize(getNormal(pixelTD) + float2(1e-6, 1e-6));

                        // 简化斯涅尔模型：深度越深入射角越平，edgeFactor = -tan(θT-θI)
                        // 在轮廓边缘→接近 1（透镜边缘折得最狠）、内部→0
                        float x_R_ratio = 1.0 - nmerged / _RefThickness;
                        float thetaI = asin(clamp(pow(x_R_ratio, 2.0), -1.0, 1.0));
                        float sinThetaT = (1.0 / _RefFactor) * sin(thetaI);

                        float edgeFactor = 0.0;
                        if (sinThetaT >= -1.0 && sinThetaT <= 1.0 && nmerged < _RefThickness)
                        {
                            float thetaT = asin(sinThetaT);
                            edgeFactor = -tan(thetaT - thetaI);
                        }

                        if (edgeFactor <= 0.0)
                        {
                            // 无偏移 → 直接模糊底 + 种类着色
                            outColor = tex2D(_BlurredBg, i.uv);
                            outColor.rgb = lerp(outColor.rgb, kindTint.rgb, kindTint.a);
                        }
                        else
                        {
                            // 折射偏移（px → UV 逐分量换算，像素级等方）+ 色散分离采样。
                            // normal 在 y 向下空间：换回 UV（y 向上）时 y 分量反向
                            float edgeH = nmerged / _RefThickness;
                            float2 offsetUV = float2(normal.x, -normal.y) * edgeFactor * _RefThickness / resolution;

                            float4 refracted = getTextureDispersion(
                                _BlurEdge > 0.5 ? 1.0 : edgeH,
                                offsetUV,
                                _RefDispersion,
                                i.uv);

                            outColor = float4(lerp(refracted.rgb, kindTint.rgb, kindTint.a), 1.0);

                            // 菲涅尔：掠射边缘增亮（LCH 空间提 L，色相不漂）
                            float fresnelFactor = clamp(
                                pow(1.0 + merged * resolution.y / 1500.0 * pow(500.0 / _RefFresnelRange, 2.0) + _RefFresnelHardness, 5.0),
                                0.0, 1.0);
                            float3 fresnelTintLCH = SRGB_TO_LAB(lerp(float3(1.0, 1.0, 1.0), kindTint.rgb, kindTint.a * 0.5));
                            fresnelTintLCH.x = clamp(fresnelTintLCH.x + 20.0 * fresnelFactor * _RefFresnelFactor, 0.0, 100.0);
                            outColor = lerp(outColor, float4(LCH_TO_SRGB(fresnelTintLCH), 1.0),
                                fresnelFactor * _RefFresnelFactor * 0.7);

                            // 眩光：内部多次反射的定向亮斑（左上光源，角度 ×2 半周期）
                            float glareGeoFactor = clamp(
                                pow(1.0 + merged * resolution.y / 1500.0 * pow(500.0 / _GlareRange, 2.0) + _GlareHardness, 5.0),
                                0.0, 1.0);
                            float glareAngleRad = (vec2ToAngle(normal) - PI / 4.0 + _GlareAngle) * 2.0;

                            bool glareFarside =
                                (glareAngleRad > PI * 1.5 && glareAngleRad < PI * 3.5) || glareAngleRad < PI * -0.5;

                            float glareAngleFactor = (0.5 + sin(glareAngleRad) * 0.5)
                                * (glareFarside ? 1.2 * _GlareOppositeFactor : 1.2) * _GlareFactor;
                            glareAngleFactor = clamp(pow(glareAngleFactor, 0.1 + _GlareConvergence * 2.0), 0.0, 1.0);

                            float3 glareTintLCH = SRGB_TO_LAB(lerp(refracted.rgb, kindTint.rgb, kindTint.a * 0.5));
                            glareTintLCH.x = clamp(glareTintLCH.x + 150.0 * glareAngleFactor * glareGeoFactor, 0.0, 120.0);
                            glareTintLCH.y += 30.0 * glareAngleFactor * glareGeoFactor;

                            outColor = lerp(outColor, float4(LCH_TO_SRGB(glareTintLCH), 1.0),
                                glareAngleFactor * glareGeoFactor);
                        }
                    }
                    else
                    {
                        // 轮廓外：淡阴影环带（alpha 低于穿透阈值 0.35，不挡点击）+ 全透明
                        float shadow = exp(-merged * resolution.y / max(_ShadowExpand, 1.0)) * 0.5 * _ShadowFactor;
                        outColor = float4(0, 0, 0, shadow);
                    }

                    // 抗锯齿：SDF 屏幕梯度自适应带宽，边缘平滑归零
                    float aaWidth = sqrt(ddx(merged) * ddx(merged) + ddy(merged) * ddy(merged)) * 2.0;
                    float aa = smoothstep(-aaWidth, aaWidth, merged);
                    outColor.a *= 1.0 - aa;

                    // 其他物种桌宠（PetRefract 层）在玻璃外区域直接可见。必须放在
                    // AA 之后：轮廓外的抗锯齿会把 alpha 一并乘零（实测：宠物只有
                    // 透过玻璃才可见，离开玻璃整个消失）。玻璃覆盖的部分不走这里——
                    // 它们已并入折射源（LiquidGlassCompose），随玻璃一起折射/模糊。
                    // 宠物画进 RT 时 alpha 被标准混合平方衰减（0.78²≈0.61），按 1.25
                    // 回拉，让软体在玻璃外保持果冻般透亮而非过淡。
                    if (merged >= 0.005)
                    {
                        float4 pet = tex2D(_PetRTTex, i.uv);
                        float petA = saturate(pet.a * 1.25);
                        outColor.rgb = lerp(outColor.rgb, pet.rgb, petA);
                        outColor.a = max(outColor.a, petA * 0.9);
                    }
                }

                return outColor;
            }
            ENDCG
        }
    }

    Fallback Off
}
