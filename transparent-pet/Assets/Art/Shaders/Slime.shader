// ================================================================
// ██████  TransparentPet/Slime —— 史莱姆玻璃材质（Unity 内置管线版）
// ██████
// ================================================================
// 【来源】由 Godot 4 canvas_item 着色器逐段移植：
//   F:/VSCode/game/transparent-pet/modules/display/shaders/slime.gdshader
//   （1号史莱姆：普通蓝色玻璃史莱姆）
// 渲染风格：玻璃质感 + 菲涅尔发光 + FBM 噪声流动扰动 + 顶点果冻晃动。
// 移植原则：所有数学公式与原版完全一致，不做任何改动；
//   差异仅为跨引擎单位/轴向换算（见下方清单）。
//
// 【与 Godot 版的差异清单（均为平台换算，非算法改动）】
// 1. TIME → _Time.y（两者都是"秒"，可直接替换）。
// 2. 顶点晃动幅度：Godot 的 VERTEX 是像素空间（幅度常量 10.0 / 15.0
//    像素）；Unity 本地空间 1 像素 = 0.01 单位（PPU=100），
//    故幅度额外乘 0.01 换算成本地单位。
// 3. 高光位置 y 偏移翻转：Godot 的 UV V 轴向下（v=0 在图像顶部），
//    Unity 贴图 v 轴向上（v=0 在图像底部）。原版为 center+(0.15,-0.15)
//    （视觉右上区域），Unity 中翻转为 center+(0.15,+0.15)，
//    保证两版高光都落在画面右上。
// 4. 顶点晃动的 Y 方向：Godot +Y 向下、Unity +Y 向上。正弦晃动是
//    对称振荡，符号差异仅等效于相位平移，视觉完全一致，故保持同号。
// 5. 贴图只当形状遮罩：仅采样 alpha（<0.05 discard），RGB 不输出，
//    最终颜色全部由 shader 计算（玻璃色+发光+高光）——与原版一致
//    （原版同样不使用纹理 RGB）。副作用：SpriteRenderer.color 的
//    顶点色不会染色，与 Godot 版忽略 modulate 的行为相同。
// 6. 精灵网格建议使用 Full Rect（若用 Tight 网格，UV 不满 0..1，
//    中心 0.5/高光位置会偏移——Godot 版同样假设 UV 覆盖整张贴图）。
//
// 【quad 顶点晃动的局限说明】
//   SpriteRenderer 的网格是一个 quad，只有 4 个顶点（四角）。顶点级
//   正弦晃动只能让整个矩形的四个角做整体偏移，无法产生网格级
//   "果冻波浪"细节——这一点与 Godot 版完全相同（Godot 的 Sprite
//   同样是 quad，晃动效果同样有限）。更细腻的液面形变需细分网格或
//   改用片元级 UV 位移，两版都未做，保持行为一致。
//
// 【原版注释勘误（以数学为准，公式未改）】
//   原版注释称"边缘比中心更亮/中心更透明"，但按其数学：
//   dist_to_edge = 1 - smoothstep(0.25, 0.48, dist_to_center)
//   在中心处为 1、边缘处为 0，实际效果是：中心更亮更白、
//   边缘 alpha 更低（更透）。移植严格照抄数学，注释按实际计算描述。
// ================================================================

Shader "TransparentPet/Slime"
{
    Properties
    {
        // 精灵贴图（800x528 RGBA），仅 alpha 作为形状遮罩使用
        _MainTex ("Sprite Texture", 2D) = "white" {}
        // 基础玻璃色（RGBA；alpha 通道 = 基础不透明度），默认蓝色调
        _GlassColor ("Glass Color", Color) = (0.1, 0.3, 0.6, 1.0)
        // 发光强度（0~2），与 _FresnelPower 配合控制发光效果
        _EdgeGlow ("Edge Glow", Range(0,2)) = 0.8
        // 高光/镜面反射强度（0~2），值越大高光越刺眼
        _HighlightIntensity ("Highlight Intensity", Range(0,2)) = 1.0
        // 液体晃动幅度（0~1）：控制顶点形变与噪声扰动强度，0=固体玻璃
        _LiquidWobble ("Liquid Wobble", Range(0,1)) = 0.3
        // 晃动速度（0.5~5）：正弦波的时间频率，值越大晃动越快
        _WobbleSpeed ("Wobble Speed", Range(0.5,5)) = 2.0
        // 菲涅尔幂指数（1~8）：越小发光带越宽，越大发光带越窄
        _FresnelPower ("Fresnel Power", Range(1,8)) = 3.0
        // 呼吸效果开关：1=顶点正弦形变（果冻晃动），0=静止
        _EnableBreathing ("Enable Breathing", Float) = 1
        // 动效开关：1=噪声扰动+流动颜色微变化，0=材质完全静态
        _EnableMotion ("Enable Motion", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 目标平台：Windows D3D11；默认编译目标（SM2.5）即可，
            // 无需更高 target（4 倍频程循环常量边界可自动展开）。
            #include "UnityCG.cginc"

            // ============================================================
            // 属性 uniforms（与 Properties 一一对应）
            // ============================================================
            sampler2D _MainTex;
            float4 _GlassColor;
            float _EdgeGlow;
            float _HighlightIntensity;
            float _LiquidWobble;
            float _WobbleSpeed;
            float _FresnelPower;
            float _EnableBreathing;
            float _EnableMotion;

            struct appdata
            {
                float4 vertex : POSITION;  // 本地空间顶点（PPU=100：1 像素 = 0.01 单位）
                float2 uv     : TEXCOORD0; // 贴图 UV（0..1 覆盖整张精灵贴图）
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
            };

            // ============================================================
            // 噪声函数（Noise Functions）—— 与 Godot 版逐行对应
            // 构成 FBM（分形布朗运动）噪声系统，为史莱姆表面添加有机纹理扰动
            // ============================================================

            // random —— 伪随机数生成器（正弦-点积哈希 sine-dot hash）
            // 将 2D 坐标投影到"魔法方向"(12.9898, 78.233)，经 sin 的混沌
            // 特性放大后取小数部分，映射到 [0,1]。快速、确定性、看似随机。
            float random(float2 st)
            {
                return frac(sin(dot(st, float2(12.9898, 78.233))) * 43758.5453);
            }

            // noise —— 2D 值噪声（四角双线性插值 + Hermite 平滑）
            // floor/fract 得到晶格坐标与格内相对位置，四角 random 后
            // 用 u = f*f*(3-2f) 消除晶格边界的 C1 不连续性再插值。
            float noise(float2 st)
            {
                float2 i = floor(st);
                float2 f = frac(st);
                float2 u = f * f * (3.0 - 2.0 * f); // Hermite 平滑

                float a = random(i);
                float b = random(i + float2(1.0, 0.0));
                float c = random(i + float2(0.0, 1.0));
                float d = random(i + float2(1.0, 1.0));

                return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
            }

            // fbm —— 分形布朗运动（4 倍频程叠加：频率 ×2，振幅 ×0.5）
            // 1/f 频谱分布模拟云/水面等自然现象；内部注入时间项
            // （TIME → _Time.y）产生流动动画。
            float fbm(float2 st)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;

                for (int i = 0; i < 4; i++)
                {
                    value += amplitude * noise(st * frequency + _Time.y * _WobbleSpeed * 0.5);
                    frequency *= 2.0;
                    amplitude *= 0.5;
                }

                return value;
            }

            // ============================================================
            // 顶点着色器 —— 正弦晃动形变
            // ============================================================
            // edge_factor = |UV.x - 0.5| * 2.0：
            //   UV.x=0.5（中心）→ 0（不变形）；UV.x=0/1（左右边缘）→ 1（最大形变）。
            //   形变中心为零、向边缘线性增强，产生"果冻晃动"感。
            // Y 轴 sin（幅度 10 像素）、X 轴 cos（幅度 15 像素、频率 ×0.7）：
            //   X/Y 不同步产生更自然的弹性感。
            // 注：SpriteRenderer 是 quad（仅 4 顶点），晃动只有整体偏移
            // 效果，与 Godot 版（同为 quad）行为一致，详见文件头说明。
            v2f vert(appdata v)
            {
                v2f o;

                // 边缘因子：UV.x 离 0.5 越远（越靠近左右边缘），形变越大
                float edge_factor = abs(v.uv.x - 0.5) * 2.0;
                if (_EnableBreathing > 0.5)
                {
                    // Y 轴正弦形变：上下晃动。
                    // Godot 像素幅度 10.0 → ×0.01 换算成本地单位（PPU=100）。
                    // Godot +Y 向下、Unity +Y 向上；正弦为对称振荡，
                    // 符号差异仅等效于相位平移，视觉一致，故不翻转符号。
                    float wobble = sin(_Time.y * _WobbleSpeed) * _LiquidWobble * 10.0 * 0.01;
                    v.vertex.y += wobble * edge_factor;
                    // X 轴余弦形变：频率 ×0.7 与 Y 轴不同步（像素幅度 15.0 → ×0.01）
                    v.vertex.x += cos(_Time.y * _WobbleSpeed * 0.7) * _LiquidWobble * 15.0 * 0.01 * edge_factor;
                }

                o.vertex = UnityObjectToClipPos(v.vertex);
                // 精灵网格 UV 已覆盖整张贴图，不做 ST 变换（与 Godot 原始 UV 一致）
                o.uv = v.uv;
                return o;
            }

            // ============================================================
            // 片元着色器 —— 像素渲染
            // ============================================================
            // 步骤1：FBM 噪声扰动 UV（模拟玻璃内部不均匀折射/流动）
            // 步骤2：扰动 UV 采样贴图 alpha 作形状遮罩（<0.05 discard）
            // 步骤3：菲涅尔幂函数计算发光强度
            // 步骤4：高光/镜面反射（固定位置圆形高光）
            // 步骤5：颜色混合（按权重向亮色偏移）
            // 步骤6：动态微扰动（高频 FBM 颜色微变化）
            // 步骤7：alpha 遮罩 × 厚度模拟
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // ====== 步骤1：计算 FBM 噪声扰动 ======
                // uv*3.0 控制噪声细节密度，_LiquidWobble 控制扰动强度
                float distortion = 0.0;
                if (_EnableMotion > 0.5)
                {
                    distortion = fbm(uv * 3.0) * _LiquidWobble;
                }

                // ====== 步骤2：扰动 UV 并采样形状遮罩 ======
                // 对 UV 施加微小偏移（distortion*0.1），模拟液体折射
                float2 distorted_uv = uv + float2(distortion * 0.1, distortion * 0.1);

                float4 tex_color = tex2D(_MainTex, distorted_uv);
                float shape_mask = tex_color.a; // alpha 通道作为形状遮罩（贴图 RGB 不使用）

                // 形状完全透明（mask<0.05）的区域直接丢弃像素，
                // 确保史莱姆之外的区域不渲染任何内容
                if (shape_mask < 0.05)
                {
                    discard;
                }

                // ====== 步骤3：菲涅尔发光计算 ======
                // 当前像素到贴图中心的距离（UV 空间）
                float2 center = float2(0.5, 0.5);
                float dist_to_center = length(uv - center);

                // smoothstep(0.25, 0.48, d)：0.25 以内输出 0，0.48 以上输出 1
                // dist_to_edge：中心处为 1、边缘处为 0（变量名沿用原版）
                float dist_to_edge = 1.0 - smoothstep(0.25, 0.48, dist_to_center);

                // 发光强度 = 幂函数 × 强度；
                // _FresnelPower=1 线性衰减（宽发光带），=3 三次方（默认），
                // =8 极窄发光带（仅中心区域极亮）
                float rim_light = pow(dist_to_edge, _FresnelPower) * _EdgeGlow;

                // ====== 步骤4：高光/镜面反射计算 ======
                // 高光位置：Godot 原版为 center + (0.15, -0.15)。Godot V 轴
                // 向下（-0.15 即视觉上方偏移），Unity 贴图 v 轴向上，故
                // y 偏移翻转为 +0.15，两版高光均落在画面右上区域。
                float2 highlight_pos = center + float2(0.15, 0.15);
                float highlight_dist = length(uv - highlight_pos);

                // 0~0.35 范围内圆形渐变；1 - smoothstep 反转使高光中心最亮；
                // 四次方衰减产生尖锐高光点；×强度缩放
                float specular = pow(1.0 - smoothstep(0.0, 0.35, highlight_dist), 4.0) * _HighlightIntensity;

                // ====== 步骤5：颜色计算与混合 ======
                float4 final_color = _GlassColor;

                // 叠加发光（强度 ×0.8）
                final_color.rgb += float3(rim_light, rim_light, rim_light) * 0.8;

                // 叠加镜面高光（纯白，强度 ×1.2）
                final_color.rgb += float3(specular, specular, specular) * 1.2;

                // 按权重向亮色混合（dist_to_edge 中心为 1，混白集中在中心区域）：
                // R → 0.9（权重 ×0.4）、G → 0.95（×0.4）、B → 1.0（×0.6），
                // 三个不同目标值产生偏白微蓝的色调（按原版数学照抄）
                final_color.r = lerp(final_color.r, 0.9, dist_to_edge * 0.4);
                final_color.g = lerp(final_color.g, 0.95, dist_to_edge * 0.4);
                final_color.b = lerp(final_color.b, 1.0, dist_to_edge * 0.6);

                // ====== 步骤6：动态微扰动（如果启用动效） ======
                // 高频 FBM（uv*10）+ 快速流动（时间 ×2），×0.05 的颜色微变化
                if (_EnableMotion > 0.5)
                {
                    // Godot: final_color.rgb += fbm(...) * 0.05（标量广播到 rgb 三通道）
                    float motion_noise = fbm(uv * 10.0 + _Time.y * 2.0);
                    final_color.rgb += float3(motion_noise, motion_noise, motion_noise) * 0.05;
                }

                // ====== 步骤7：alpha 遮罩与厚度模拟 ======
                // 基础遮罩：乘以贴图 alpha
                final_color.a *= shape_mask;

                // 厚度模拟：alpha × (1 - dist_to_center * 0.6)
                // 中心（d=0）→ ×1.0；d=1 → ×0.4（按原版数学照抄）
                final_color.a *= (1.0 - dist_to_center * 0.6);

                // 输出最终颜色
                return final_color;
            }
            ENDCG
        }
    }
}
