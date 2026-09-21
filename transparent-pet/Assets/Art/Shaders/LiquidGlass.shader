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
//
// 【绘制范围收敛（性能）】
// 本 shader 逐像素很重（每像素 16 槽贝塞尔 SDF，法线再×4），全屏绘制在核显上要
// 几十毫秒。玻璃本体只占屏幕一小块，其余像素算完 alpha≈0——于是 CPU 端
// （GlassRenderRect）只让 quad 覆盖"轮廓 + 阴影可见半径"，并用 _ScreenUvRect 把
// quad 的本地 uv 换算回屏幕 uv：片元里的 uv / pixel 与全屏绘制逐像素等价，
// 画面（含轮廓外那圈淡阴影）逐字节不变。
// 多只分散时并集矩形可能覆盖大半个屏幕，故主渲染路径再加两级跳过（均为
// 「本来就不改变结果」的剪枝，见 allItemsFar / mainSDF）：
//   · 整屏提前退出：离所有物品都远到阴影已不可见 → 直接输出全透明；
//   · 逐项跳过：某物品的下界距离已超过当前 smin 结果 + 融合宽度 → 该项对结果
//     无影响（smin 在 |a-b|≥k 时恒等于 min，逐位等价）。
// 调试视图（_Step ≤ 2）保持全屏 + 不剪枝：SDF/法线图是形状锚定工具，需要看到
// 轮廓外的数值分布。
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
        _Tint ("色调(RGBA, A=强度)", Color) = (1, 1, 1, 0.08)
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
            float4 _Resolution;
            int _Step;

            // 绘制矩形（左上原点像素 → 见 CPU 端 GlassRenderRect）。
            // _ScreenUvRect：矩形在屏幕 uv（左下原点）的位置尺寸；_RectUvScale：
            // 屏幕 uv → 矩形本地 uv 的缩放（= 屏幕尺寸 / 矩形尺寸，矩形与屏幕同像素密度）。
            float4 _ScreenUvRect;
            float4 _RectUvScale;
            // 清晰背景 _Bg 的采样映射：xy = 该纹理左上角在屏幕 uv 的原点，
            // zw = 屏幕 uv → 纹理 uv 的缩放。用于「桌面帧（区域随绘制矩形变化）」
            // 与「素材 RT（与绘制矩形同尺寸）」两种来源，两者只是参数不同。
            float4 _BgRemap;
            // 主渲染的整体提前退出阈值（px，已含安全余量）——超过它就没有任何可见输出
            float _EarlyOutPx;

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
            float4 _Tint;
            float _BlurEdge;
            float _ShadowExpand;
            float _ShadowFactor;

            // 物品数组（与 CPU 端 LiquidGlassController 一一对应）：
            // xy = 中心（GL 像素，左下原点）；目前只装配史莱姆形状一种，
            // 保留 3 槽位 + smin 融合，多只玻璃史莱姆融合零成本可加。
            #define MAX_ITEMS 16
            float4 _ItemPositions[MAX_ITEMS];
            float _ItemWidths[MAX_ITEMS];
            float _ItemScales[MAX_ITEMS];
            float _ItemEnabled[MAX_ITEMS];
            // 变换级生命感（每槽）：xy = 非等比缩放（呼吸/挤压），z = 绕中心的旋转弧度。
            // 全 1 / 0 时与旧行为逐像素一致（见 getItemSDF 的归一化因子）。
            // 只影响渲染；CPU 命中判定与物理边界仍用未变形的静息轮廓（视觉装饰不污染模拟）
            float4 _ItemShape[MAX_ITEMS];

            #define PI 3.14159265359

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;      // 屏幕 uv（与全屏绘制逐像素等价）
                float2 uvLocal : TEXCOORD1; // 矩形本地 uv（模糊 RT / 素材 RT 用）
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // quad 本地 uv → 屏幕 uv：绘制矩形只覆盖画面一角时，片元拿到的
                // uv 仍是"整屏坐标系"里的位置，SDF/阴影/折射的量纲全部不变
                o.uv = _ScreenUvRect.xy + v.texcoord.xy * _ScreenUvRect.zw;
                o.uvLocal = v.texcoord.xy;
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
                for (int iter = 0; iter < 6; iter++)
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
                    for (int step = 0; step < 20; step++)
                    {
                        float mid = (lo + hi) * 0.5;
                        if (bezier3(RU_A, RU_B, RU_C, RU_D, mid).y < y) lo = mid; else hi = mid;
                    }
                    return bezier3(RU_A, RU_B, RU_C, RU_D, (lo + hi) * 0.5).x;
                }
                for (int step = 0; step < 20; step++)
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

                float2 d = pixelTopDown - _ItemPositions[index].xy;

                // 变换级生命感：绕中心旋转（倾斜）→ 各轴独立缩放（呼吸/挤压）。
                // 形状 = {pos + R(-rot)·shape_rest}，即旋转采样点等价于反向旋转形状。
                float rot = _ItemShape[index].z;
                if (rot != 0.0)
                {
                    float cr = cos(rot);
                    float sr = sin(rot);
                    d = float2(cr * d.x - sr * d.y, sr * d.x + cr * d.y);
                }

                float span = _ItemWidths[index] * _ItemScales[index];
                float2 span2 = span * _ItemShape[index].xy;
                float slimeD = sdSlime(float2(d.x / span2.x, d.y / span2.y));

                // 距离还原回归一化单位（与旧写法等价：shape=(1,1) 时均值因子恒为 1）。
                // 非等比缩放会让距离度量失真，smin 融合半径取两轴均值即可（软参数）
                float spanMean = span * 0.5 * (_ItemShape[index].x + _ItemShape[index].y);
                return slimeD * spanMean / _Resolution.y;
            }

            // smin 平滑融合（多物品 metaball 式合并；单物品时退化为 min）
            float smin(float a, float b, float k)
            {
                float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            // 物品保守 AABB 的像素距离下界（点在 AABB 内为 0）。
            // 形状 ⊂ AABB ⇒ 返回值 ≤ 真实 SDF 像素距离，但注意本 shader 的 SDF 是
            // 「归一化距离 × spanMean / 分辨率」，非等比缩放（生命感的呼吸/挤压）会
            // 让两者差一个 ≤ max(sx,sy)/mean 的因子；调用方按 2 倍安全系数折算。
            float itemAabbDistPx(int index, float2 pixelTopDown)
            {
                float2 d = pixelTopDown - _ItemPositions[index].xy;
                float rot = _ItemShape[index].z;
                if (rot != 0.0)
                {
                    float cr = cos(rot);
                    float sr = sin(rot);
                    d = float2(cr * d.x - sr * d.y, sr * d.x + cr * d.y);
                }
                float span = _ItemWidths[index] * _ItemScales[index];
                // y 取上下界中绝对值较大者（-0.231 / +0.275），对称外包属安全侧
                float2 halfExt = float2(0.4, 0.275) * span * abs(_ItemShape[index].xy);
                float2 q = max(abs(d) - halfExt, 0.0);
                return length(q);
            }

            // 提前退出的判定点必须"整格一致"：同一 2×2 quad 内若一部分像素提前返回、
            // 另一部分继续，继续的那些像素的 ddx/ddy 会取到已返回 lane 的未定义值
            //（aaWidth 被污染 → 轮廓外浮出一圈假阴影，实测差异全部集中在阈值边界）。
            // 把判定点吸附到 quad 原点（2×2 块的偶数坐标）即整格同进退。
            // 吸附点与真实像素最远差 √2 px，CPU 端给阈值时已含这个余量。
            float2 snapToQuad(float2 pixelBottomUp, float2 resolution)
            {
                float2 q = floor(pixelBottomUp * 0.5) * 2.0;
                return float2(q.x, resolution.y - q.y); // → 与 SDF 同系的 y 向下
            }

            // 是否所有物品都远到"输出必然全透明"。阈值由 CPU 端按 AA 带宽给
            //（GlassRenderRect.EarlyOutPx）——轮廓外 alpha 会被抗锯齿项乘成 0，
            // 真正决定"多远就没输出"的是 aaWidth 而不是阴影尾巴（见 CPU 端注释）。
            bool allItemsFar(float2 pixelTopDown)
            {
                for (int i = 0; i < MAX_ITEMS; i++)
                {
                    if (_ItemEnabled[i] < 0.5)
                        continue;
                    if (itemAabbDistPx(i, pixelTopDown) <= _EarlyOutPx)
                        return false;
                }
                return true;
            }

            float mainSDF(float2 pixelTopDown, bool allowSkip)
            {
                float result = 1.0;
                for (int i = 0; i < MAX_ITEMS; i++)
                {
                    if (_ItemEnabled[i] < 0.5)
                        continue;
                    // 剪枝（仅在主渲染路径开启）：|d - result| ≥ k 时 smin 恒等于
                    // min(result, d)，该项对 result 无影响，跳过与不跳过逐位等价
                    if (allowSkip && itemAabbDistPx(i, pixelTopDown) / _Resolution.y * 0.5 - result >= _MergeRate)
                        continue;
                    result = smin(result, getItemSDF(i, pixelTopDown), _MergeRate);
                }
                return result;
            }

            // SDF 数值梯度 = 表面法线。返回像素空间单位梯度（merged 是归一化距离，
            // 乘回分辨率）；Godot 原版此处乘 1414 的放大系数只为可视化，这里语义化。
            //
            // **前向差分**而非中心差分：轮廓曲率半径是几十像素量级（轮廓半宽 128px
            // 的圆顶上 κ≈1/128），步长 1px 的前向差分方向偏差 ≈ h·κ/2 ≈ 0.4%；
            // 而中心差分要多跑 2 次 mainSDF——mainSDF 是这条管线最贵的单项（每项
            // 贝塞尔 SDF + 20 次二分反解），原来每像素 5 次求值里 4 次都出自法线。
            // center 由调用方传入（它已经算过本像素的 SDF），总次数 5 → 3。
            float2 getNormal(float2 pixelTopDown, bool allowSkip, float center)
            {
                float2 h = float2(max(abs(ddx(pixelTopDown.x)), 0.0001), max(abs(ddy(pixelTopDown.y)), 0.0001));
                float2 grad = float2(
                    mainSDF(pixelTopDown + float2(h.x, 0.0), allowSkip) - center,
                    mainSDF(pixelTopDown + float2(0.0, h.y), allowSkip) - center
                ) / h;
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

            // 清晰背景 _Bg 的采样映射：屏幕 uv → 该纹理自己的 uv。
            // 桌面帧的矩形随绘制矩形移动、素材 RT 与绘制矩形同尺寸，两者只是参数不同。
            float2 bgUV(float2 screenUv)
            {
                return (screenUv - _BgRemap.xy) * _BgRemap.zw;
            }

            // RGB 三通道按各自折射率分别偏移采样，再与模糊版逐通道混合。
            // uv = 屏幕 uv；uvLocal = 矩形本地 uv（模糊 RT 的坐标系）——
            // 同一个像素位移在两者间的换算就是 _RectUvScale
            float4 getTextureDispersion(float mixRate, float2 offset, float factor, float2 uv, float2 uvLocal)
            {
                float2 offR = offset * (1.0 - (N_R - 1.0) * factor);
                float2 offG = offset * (1.0 - (N_G - 1.0) * factor);
                float2 offB = offset * (1.0 - (N_B - 1.0) * factor);

                float bgR = tex2D(_Bg, bgUV(uv + offR)).r;
                float bgG = tex2D(_Bg, bgUV(uv + offG)).g;
                float bgB = tex2D(_Bg, bgUV(uv + offB)).b;

                float blurR = tex2D(_BlurredBg, uvLocal + offR * _RectUvScale.xy).r;
                float blurG = tex2D(_BlurredBg, uvLocal + offG * _RectUvScale.xy).g;
                float blurB = tex2D(_BlurredBg, uvLocal + offB * _RectUvScale.xy).b;

                return float4(lerp(bgR, blurR, mixRate), lerp(bgG, blurG, mixRate), lerp(bgB, blurB, mixRate), 1.0);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 resolution = _Resolution.xy;
                float2 pixel = i.uv * resolution;              // GL 语义（左下原点），用于 UV 采样换算
                float2 pixelTD = float2(pixel.x, resolution.y - pixel.y); // y 向下（top-origin），SDF/法线/眩光统一在此空间计算

                // 剪枝只在主渲染路径开启（调试视图要全屏数值）
                bool skip = _Step > 2;

                // 离所有物品都远到必然全透明 → 输出与"逐像素算完得到 alpha=0"完全一致
                //（判定点吸附到 quad 原点，避免混合 quad 污染邻居的屏幕空间导数）
                if (skip && allItemsFar(snapToQuad(pixel, resolution)))
                    return float4(0, 0, 0, 0);

                float merged = mainSDF(pixelTD, skip);

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
                        float2 normal = getNormal(pixelTD, skip, merged);
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
                        float2 normal = normalize(getNormal(pixelTD, skip, merged) + float2(1e-6, 1e-6));

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
                            // 无偏移 → 直接模糊底 + 色调
                            outColor = tex2D(_BlurredBg, i.uvLocal);
                            outColor.rgb = lerp(outColor.rgb, _Tint.rgb, _Tint.a * 0.8);
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
                                i.uv,
                                i.uvLocal);

                            outColor = float4(lerp(refracted.rgb, _Tint.rgb, _Tint.a * 0.8), 1.0);

                            // 菲涅尔：掠射边缘增亮（LCH 空间提 L，色相不漂）
                            float fresnelFactor = clamp(
                                pow(1.0 + merged * resolution.y / 1500.0 * pow(500.0 / _RefFresnelRange, 2.0) + _RefFresnelHardness, 5.0),
                                0.0, 1.0);
                            float3 fresnelTintLCH = SRGB_TO_LAB(lerp(float3(1.0, 1.0, 1.0), _Tint.rgb, _Tint.a * 0.5));
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

                            float3 glareTintLCH = SRGB_TO_LAB(lerp(refracted.rgb, _Tint.rgb, _Tint.a * 0.5));
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
                }

                return outColor;
            }
            ENDCG
        }
    }

    Fallback Off
}
