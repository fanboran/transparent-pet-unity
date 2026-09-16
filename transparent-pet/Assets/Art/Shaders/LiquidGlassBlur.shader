// ================================================================
// ██████  TransparentPet/LiquidGlassBlur —— 分离式一维高斯模糊
// ================================================================
// 移植自 Godot 版 liquid_glass_blur.gdshader。高斯核可分离：
// G(x,y) = G(x)·G(y)，二维模糊拆成 竖直 + 水平 两次一维卷积，
// O(N²) → O(2N)。同一个 shader 用 _Vertical 开关跑两遍
//（CPU 端 Graphics.Blit 串联：bgRT → 竖直 → 水平 → 主合成）。
//
// 高斯权重由 CPU 端（LiquidGlassController）按 σ = radius/3 预计算并
// 归一化后经 SetFloatArray 推入（避免逐像素算 exp），shader 内只做
// 保护性归一化。数组上限 64 = radius 上限 31，超出部分截断。
// ================================================================

Shader "TransparentPet/LiquidGlassBlur"
{
    Properties
    {
        _MainTex ("上一遍输出", 2D) = "white" {}
        _BlurRadius ("模糊半径(px)", Float) = 6
        _Vertical ("方向(1=竖直 0=水平)", Float) = 1
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

            sampler2D _MainTex;
            float _BlurRadius;
            float _Vertical;
            float4 _Resolution;

            #define MAX_KERNEL_SIZE 64
            float _BlurWeights[MAX_KERNEL_SIZE];

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

            float4 frag(v2f i) : SV_Target
            {
                float2 texelSize = 1.0 / _Resolution.xy;

                int kernelSize = min(int(_BlurRadius * 2.0 + 1.0), MAX_KERNEL_SIZE);
                int halfKernel = kernelSize / 2;

                float4 color = float4(0, 0, 0, 0);
                float totalWeight = 0.0;

                for (int k = 0; k < kernelSize; k++)
                {
                    int offset = k - halfKernel;
                    float2 uvOffset = (_Vertical > 0.5 ? float2(0.0, offset) : float2(offset, 0.0)) * texelSize;
                    float weight = _BlurWeights[k];
                    color += tex2D(_MainTex, i.uv + uvOffset) * weight;
                    totalWeight += weight;
                }

                if (totalWeight > 0.0)
                    color /= totalWeight;
                return color;
            }
            ENDCG
        }
    }

    Fallback Off
}
