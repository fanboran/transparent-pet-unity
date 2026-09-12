// ================================================================
// ██████  TransparentPet/SlimeLiquid —— metaball 场着色器（逐像素密度场）
// ================================================================
// 输入：粒子位置 StructuredBuffer（世界坐标 XY）+ 罩住粒子包围盒的四边形。
// 片元对每个像素累加全部粒子的平滑核 w = (1-r²/h²)³（C² 连续），
// alpha = smoothstep(iso±band, w)：边缘是数学级连续的等值面——
// 任何分辨率、任何缩放都不可能出现锯齿/块状（mesh 等值线的顶点
// 天然卡在网格上，这是其"马赛克感"无法根除的原因）。
// 颜色：纯色半透明 + 挤压脉冲轻微提亮（物理反馈）。
// alpha 契约：主体内部 w >> iso → alpha = _BodyAlpha(0.78)，远高于
// 透明窗口命中阈值 0.35 下限；仅边缘 ~1.5px 渐隐。
// ================================================================

Shader "TransparentPet/SlimeLiquid"
{
    Properties
    {
        _BodyColor ("主体色", Color) = (0.16, 0.48, 0.92, 1)
        _BodyAlpha ("主体不透明度", Range(0.35, 1)) = 0.78
        _Squash ("挤压脉冲", Range(0, 1)) = 0
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
            #pragma target 4.5   // StructuredBuffer 需要 SM4.5（D3D11）
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Particles;   // xy = 世界坐标（每帧 SetData）
            int _ParticleCount;
            float _KernelH;   // 核半径（世界单位）
            float _Iso;       // 等值阈值（每帧按质心核总和自标定）
            float _Band;      // 边缘过渡带宽度（核单位）

            float4 _BodyColor;
            float _BodyAlpha;
            float _Squash;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 world : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, v.vertex);
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.world = world.xy;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // 逐像素密度场：累加全部粒子的平滑核（紧支撑，C² 连续）
                float h2 = _KernelH * _KernelH;
                float w = 0.0;
                for (int k = 0; k < _ParticleCount; k++)
                {
                    float2 d = i.world - _Particles[k].xy;
                    float r2 = dot(d, d);
                    if (r2 < h2)
                    {
                        float t = 1.0 - r2 / h2;
                        w += t * t * t;
                    }
                }

                // 等值面 + 平滑过渡带 = 天然逐像素抗锯齿
                float alpha = _BodyAlpha * smoothstep(_Iso - _Band, _Iso + _Band, w);
                float3 col = _BodyColor.rgb * (1.0 + _Squash * 0.18);
                return float4(col, alpha);
            }
            ENDCG
        }
    }

    Fallback Off
}
