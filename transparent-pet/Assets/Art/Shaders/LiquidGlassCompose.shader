// ================================================================
// LiquidGlassCompose —— 折射源合成：桌面抓屏 + PetRefract 层（其他物种桌宠）
// ================================================================
// LiquidGlassController 每帧把折射源（真实桌面抓屏或程序化素材）blit 过本
// Pass：PetRefract 层的画面（软体等其他物种桌宠，PetRefractLayer 相机每帧
// 渲染到全局纹理 _PetRTTex）按 alpha 叠在桌面上，输出作为玻璃的折射源——
// 于是玻璃内的其他桌宠与桌面一起被折射/色散/模糊，而不是浮在玻璃上面。
// 玻璃轮廓外的直接可见性由 LiquidGlass.shader 主合成分支负责（不经过本 Pass）。
// _PetRTTex 未就绪（无 PetRefractLayer / 首帧）时采样为黑且 alpha=0，本 Pass
// 等价于直通拷贝，零副作用。
// ================================================================

Shader "TransparentPet/LiquidGlassCompose"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }

        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;   // 折射源（桌面抓屏或程序化素材），Blit 自动绑定
            sampler2D _PetRTTex;  // PetRefract 层画面（其他物种桌宠）

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float4 pet = tex2D(_PetRTTex, i.uv);
                c.rgb = lerp(c.rgb, pet.rgb, pet.a);
                c.a = max(c.a, pet.a);
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
