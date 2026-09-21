// ================================================================
// ██████  TransparentPet/LiquidGlassBg —— 液态玻璃折射素材生成器
// ================================================================
// 移植自 Godot 版 liquid_glass_bg.gdshader 的背景部分（简化）。
// 它不直接上屏：渲染到一张与屏幕等大的 RenderTexture，作为主合成
// 着色器（TransparentPet/LiquidGlass）折射采样的"玻璃后面的世界"。
//
// 只保留三种素材（Godot 版的调试四宫格与"全透明"模式对本项目无意义）：
//   0 = 经典棋盘格（观察折射形变的标准素材）
//   1 = 垂直渐变（浅灰→深灰）
//   2 = 自定义纹理（cover 模式铺满，宽高比不匹配时裁边不留黑框）
//
// Godot 版在此 pass 里按形状纹理画阴影——其接线（读主 pass 输出当 SDF）
// 在原项目中本就存疑；Unity 版阴影改由主着色器在轮廓外环带直接绘制，
// 本 pass 保持纯素材，避免与主 pass 形成 RT 依赖。
//
// 【绘制范围收敛（性能）】本 pass 与主合成同矩形（见 GlassRenderRect）：
// _ScreenUvRect 把 quad 本地 uv 换算回屏幕 uv，素材图案仍是"整屏坐标系"里的
// 图案——矩形只决定"渲染哪一块"，不改变图案本身，逐像素与全屏绘制等价。
// ================================================================

Shader "TransparentPet/LiquidGlassBg"
{
    Properties
    {
        _BgType ("素材类型(0棋盘 1渐变 2纹理)", Float) = 0
        _BgTexture ("自定义素材", 2D) = "white" {}
        _BgTextureRatio ("素材宽高比", Float) = 1
        _BgTextureReady ("素材就绪", Float) = 0
        _Resolution ("渲染分辨率", Vector) = (1920, 1080, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Opaque" }

        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            float _BgType;
            sampler2D _BgTexture;
            float _BgTextureRatio;
            float _BgTextureReady;
            float4 _Resolution;
            // 绘制矩形在屏幕 uv（xy = 原点，zw = 尺寸）；_Resolution 仍是整屏尺寸，
            // 故图案坐标（uvpx = 屏幕 uv × _Resolution）与全屏绘制逐像素一致
            float4 _ScreenUvRect;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = _ScreenUvRect.xy + v.texcoord.xy * _ScreenUvRect.zw;
                return o;
            }

            // mode: 0=横条 1=竖条 2=棋盘（横竖异或）；坐标恒正，fmod 与 GLSL mod 同效
            float chessboard(float2 uvpx, float size, int mode)
            {
                float yBars = step(size * 2.0, fmod(uvpx.y * 2.0, size * 4.0));
                float xBars = step(size * 2.0, fmod(uvpx.x * 2.0, size * 4.0));
                if (mode == 0) return yBars;
                if (mode == 1) return xBars;
                return abs(yBars - xBars);
            }

            // CSS object-fit: cover 等效——铺满画布、等比、裁边
            float2 getCoverUV(float2 uv, float canvasAspect, float textureAspect)
            {
                if (canvasAspect > textureAspect)
                {
                    float scale = textureAspect / canvasAspect;
                    uv.y = uv.y * scale + 0.5 - 0.5 * scale;
                }
                else
                {
                    float scale = canvasAspect / textureAspect;
                    uv.x = uv.x * scale + 0.5 - 0.5 * scale;
                }
                return uv;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uvpx = i.uv * _Resolution.xy;
                float3 bgColor;

                if (_BgType < 0.5)
                {
                    // 经典棋盘格：20px 格，黑白灰（0.75/1.0）
                    float shade = 1.0 - chessboard(uvpx, 20.0, 2) / 4.0;
                    bgColor = float3(shade, shade, shade);
                }
                else if (_BgType < 1.5)
                {
                    // 垂直渐变：上暗下亮，映射到 [0.3, 0.9]（Unity UV 原点在左下，
                    // 与 Godot 相反，故用 1-uv.y 保持 Godot 原版的方向）
                    float shade = smoothstep(0.35, 0.65, 1.0 - i.uv.y) * 0.6 + 0.3;
                    bgColor = float3(shade, shade, shade);
                }
                else
                {
                    if (_BgTextureReady < 0.5)
                    {
                        float shade = 1.0 - chessboard(uvpx, 20.0, 2) / 4.0;
                        bgColor = float3(shade, shade, shade);
                    }
                    else
                        bgColor = tex2D(_BgTexture, getCoverUV(i.uv, _Resolution.x / _Resolution.y, _BgTextureRatio)).rgb;
                }

                return float4(bgColor, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
