// ================================================================
// ██████  TransparentPet/SlimeLiquid —— 纯色半透明史莱姆（基础观感版）
// ================================================================
// 观感决策（用户拍板）：先锚定最基础的"纯色半透明"形态，折射/色散/
// 流动/打光等高级效果以后再说。形状与平滑度全部由 C# 侧的密度场
// Marching Squares 表面提供：
//   · 顶点位置 = 密度等值面（轮廓天然光滑，无多边形棱角）；
//   · 顶点色 alpha = 密度覆盖度（边缘 0 → 内部 1），片元插值后即得
//     1~2px 的密度渐变边缘——天然抗锯齿，与缩放/分辨率无关；
//   · alpha 契约（透明窗口鼠标命中，阈值 0.1）：主体内部 coverage=1
//     → alpha=_BodyAlpha(0.75)，远高于 0.35 下限；仅边缘渐隐。
// 挤压脉冲（_Squash，撞地/受激时 C# 置 1 并指数衰减）只做轻微整体
// 提亮——物理反馈，不是装饰配色。
// ================================================================

Shader "TransparentPet/SlimeLiquid"
{
    Properties
    {
        _BodyColor ("主体色", Color) = (0.16, 0.48, 0.92, 1)
        _BodyAlpha ("主体不透明度", Range(0.35, 1)) = 0.75
        _Squash ("挤压脉冲", Range(0, 1)) = 0
        _VelocityW ("速度模长", Float) = 0
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
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4 _BodyColor;
            float  _BodyAlpha;
            float  _Squash;
            float  _VelocityW;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;   // 顶点色：a = 密度覆盖度（C# 侧写入）
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // 覆盖度（顶点插值）→ smoothstep 软化后再作为透明系数：
                // 顶点色网格量化出的 alpha 台阶被 S 曲线抹平（马赛克感来源之一），
                // 边缘密度低于 iso 的部分平滑渐隐，内部为 1
                float coverage = smoothstep(0.0, 1.0, i.color.a);
                float alpha = _BodyAlpha * coverage;
                // 挤压脉冲轻微提亮（果冻受激反馈）
                float3 col = _BodyColor.rgb * (1.0 + _Squash * 0.18);
                return float4(col, alpha);
            }
            ENDCG
        }
    }

    Fallback "Transparent/Diffuse"
}
