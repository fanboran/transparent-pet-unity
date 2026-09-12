// ================================================================
// ██████  TransparentPet/SlimeLiquid —— 程序化液态玻璃史莱姆
// ██████  （全程序化 · 零贴图 · 动态 Mesh · fwidth 屏幕空间抗锯齿）
// ================================================================
// 【为什么这样设计】
//   旧贴图方案 alpha 硬边 → 像素阶梯（分辨率固定，缩放必出锯齿）。
//   本方案彻底去贴图：
//     1. 形状来自 C# 侧每帧重建的动态 Mesh（29 顶点中心扇形），
//        轮廓顶点就是最终屏幕位置，形状精度 = 物理模拟精度；
//     2. 观感全部由数学公式现场计算：不采样任何贴图（连 sampler
//        都没有），也没有传统意义的"打光"——玻璃的立体感来自
//        厚度渐变（中心薄边缘厚）、伪折射（法线扭动环境）与 RGB
//        色散，这三者本身就是"液态玻璃"的同层实现；
//     3. 边缘用 fwidth（屏幕空间导数）做自适应抗锯齿：带宽自动
//        等于 ~1.5 个真实像素，任意缩放都平滑。
//   目标平台：Unity 内置渲染管线（Built-in RP）/ D3D11 / SM3.0。
//
// 【Mesh 输入契约（C# 侧每帧更新）】
//   · 顶点 29 = 28 个轮廓点 + 1 个中心点，中心扇形三角化；
//   · uv0 = (t, angle/2π)：t 为径向参数（中心 0 → 边缘 1）；
//   · uv1 = (nx, ny) =（粒子位置 - 质心）/ 当前静息半径：中心
//     (0,0)，边缘落在单位圆上；形变时偏离单位圆（拉伸>1/压缩<1），
//     把软体形变信息直接喂给折射 —— 形状怎么变形，玻璃观感就
//     怎么跟着走；
//   · 顶点世界位置即最终渲染位置（正交相机），vertex 只做 MVP。
//
// 【整体渲染管线（fragment 分步）】
//   步骤1  fwidth 边缘抗锯齿
//   步骤2  2.5D 球面法线：n = (uv1, sqrt(1-|uv1|²))——仅供折射用
//   步骤3  伪折射 + RGB 色散：n.xy 扭动程序化假环境，三通道异强度
//   步骤4  内部流动：fbm(uv1*3 + 时间相位) 明度微扰
//   步骤5  颜色合成：厚度渐变体色 + 折射透射 + 内部流动
//   步骤6  挤压脉冲：整体提亮 + 折射短暂增强（受激反馈）
//   步骤7  alpha 组装（厚度渐变，主体 ≥0.55，仅边缘 1~2px 渐隐）
//
// 【alpha 契约（透明窗口 & 鼠标命中，硬性约束）】
//   输出 alpha 会被 Win32 分层窗口层拿去做鼠标命中检测（阈值 0.1）：
//   主体内部 alpha 必须 ≥ 0.35 —— 本 shader 中心 0.55（通透）随
//   径向参数 t 增到 0.92（边缘厚更实），全程远高于下限；只允许
//   最边缘 1~2px 由抗锯齿带渐变到 0。
//
// 【伪折射说明（面试常问，先说清楚）】
//   透明窗口拿不到桌面真实背景像素——桌面合成发生在 DWM，GPU 侧
//   无法回读屏幕。因此"折射"是纯艺术近似，不是物理折射：
//     · "环境" = 竖直亮暗渐变 + 两层流动 fbm（假想房间光的标量
//       亮度场 envLum）；
//     · 折射 = 用 n.xy（球面法线水平分量）扭动环境坐标——中心
//       n.xy≈0 直视不扭，越靠边缘扭得越狠，天然的凸透镜畸变分布；
//     · 色散 = 三个颜色通道用略微不同的折射强度采样同一环境，
//       模拟折射率随波长变化，产生程序化 chromatic aberration。
// ================================================================

Shader "TransparentPet/SlimeLiquid"
{
    Properties
    {
        // 主体玻璃色（RGB 染色；A 通道不使用——透明度由"厚度公式 ×
        // 抗锯齿"决定，不受颜色 alpha 影响）
        _BodyColor ("主体玻璃色", Color) = (0.1, 0.3, 0.6, 1)
        // RGB 三通道折射率差 → 色散强度
        _Dispersion ("色散强度", Range(0, 1)) = 0.35
        // 伪折射整体强度（环境坐标被 n.xy 扭动的幅度）
        _RefractStrength ("伪折射强度", Range(0, 2)) = 1.0
        // 内部流动基础流速（乘 _Time.y 得相位）
        _FlowSpeed ("流速", Float) = 0.6
        // 挤压脉冲（C# 传分裂/合并/撞地脉冲并自行衰减）——提亮 + 折射增强
        _Squash ("挤压脉冲", Range(0, 1)) = 0
        // 速度模长（C# 传）——稍微加速内部流动
        _VelocityW ("速度模长", Float) = 0
        // 预留：整体不透明度增益（默认 0 不生效）
        _TransparencyBoost ("透明度增益（预留）", Float) = 0
    }

    SubShader
    {
        // Transparent+10：排在默认透明队列之后；RenderType 供替换参考
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        ZWrite Off
        // 双面渲染：动态网格扇形绕向可能随轮廓翻转，关剔除最稳妥
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        CGINCLUDE
        #include "UnityCG.cginc"

        // ==== uniforms（与 Properties 一一对应，MaterialPropertyBlock 每帧驱动）====
        float4 _BodyColor;
        float  _Dispersion;
        float  _RefractStrength;
        float  _FlowSpeed;
        float  _Squash;
        float  _VelocityW;
        float  _TransparencyBoost;

        // ============================================================
        // 噪声函数族 —— 程序化纹理的根基（零贴图，全部现场计算）
        // ============================================================

        // random —— 2D 伪随机哈希（经典 sin-hash）
        // h(p) = frac(sin(p·d)·C)：dot 投影到魔法方向 d，sin 放大微
        // 小差异，大数取小数打散到 [0,1)。确定性、无状态、3 次运算。
        float random(float2 st)
        {
            return frac(sin(dot(st, float2(12.9898, 78.233))) * 43758.5453);
        }

        // valueNoise —— 2D 值噪声（四角哈希 + Hermite 双线性插值）
        // u = f²(3-2f) 是 Hermite 三次曲线（smoothstep 核），C1 连续，
        // 消除晶格边界的折痕；插值对象是格点随机值（值噪声），柔和
        // 无方向感，正符合液体内部团絮的观感。
        float valueNoise(float2 st)
        {
            float2 i = floor(st);
            float2 f = frac(st);
            float2 u = f * f * (3.0 - 2.0 * f);

            float a = random(i);
            float b = random(i + float2(1.0, 0.0));
            float c = random(i + float2(0.0, 1.0));
            float d = random(i + float2(1.0, 1.0));

            return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
        }

        // fbm —— 分形布朗运动（4 倍频程叠加，纯空间场）
        // fbm(p) = Σ gain^i · noise(2^i·p)，频谱 ≈ 1/f，与云/水面等
        // 自然纹理统计特征一致。时间相位由调用方加在坐标上注入，
        // 各调用点可用不同方向/速度流动，互不干扰。值域约 [0, 0.94]。
        float fbm(float2 st)
        {
            float value = 0.0;
            float amplitude = 0.5;
            float frequency = 1.0;

            [unroll]
            for (int octave = 0; octave < 4; octave++)
            {
                value += amplitude * valueNoise(st * frequency);
                frequency *= 2.0;
                amplitude *= 0.5;
            }
            return value;
        }

        // envLum —— 伪环境亮度场（假想"房间光"的标量亮度，供折射采样）
        // 组成：竖直渐变（上亮下暗，想象房间上方挂灯）+ 大尺度流动层
        // （向上卷动，热对流感）+ 细尺度斜向流动层。两层共用"液体
        // 时钟" flowTime，与内部流动同节奏。返回值约 [0.05, 1.3]。
        float envLum(float2 p, float flowTime)
        {
            float grad = lerp(0.30, 0.85, saturate(p.y * 0.5 + 0.5));
            float bigFlow = fbm(p * 2.2 + float2(0.0, -flowTime * 0.7));
            float fineFlow = fbm(p * 5.0 + float2(flowTime * 0.9, flowTime * 0.5));
            return grad + (bigFlow - 0.5) * 0.50 + (fineFlow - 0.5) * 0.25;
        }

        // ============================================================
        // 顶点着色器 —— 标准 MVP 变换 + 透传两组 UV
        // ============================================================
        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv0    : TEXCOORD0;  // (t, angle/2π)：t 径向 0中心→1边缘
            float2 uv1    : TEXCOORD1;  // (nx, ny)：归一化形状坐标
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv0 : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv0 = v.uv0;
            o.uv1 = v.uv1;
            return o;
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // SM3.0：fwidth/ddx/ddy 屏幕空间导数需要着色器模型 3.0
            #pragma target 3.0

            // ============================================================
            // 片元着色器 —— 全程序化液态玻璃（无打光、无 rim、无贴图）
            // ============================================================
            float4 frag(v2f i) : SV_Target
            {
                float t = i.uv0.x;      // 径向参数：0 中心 → 1 轮廓
                float2 uv1 = i.uv1;     // 归一化形状坐标（法线/折射基准）

                // ===== 步骤0：液体时钟 =====
                // 流速被 _VelocityW 稍微加速（最多 ×1.6）；来自物理速度
                // 模长（连续量），小系数下相位重缩放不可察觉。
                float flowSpeed = _FlowSpeed * (1.0 + saturate(_VelocityW) * 0.6);
                float flowTime = _Time.y * flowSpeed;

                // ===== 步骤1：边缘 fwidth 抗锯齿 =====
                // fwidth(t) ≈ 1/半径像素数 → 抗锯齿带宽自动锁定 ~1.5
                // 真实像素，与缩放、分辨率无关。alpha = 1-smoothstep
                // 骑在轮廓 t=1 上，边缘像素 50% 覆盖率，标准 AA。
                float aaT = max(fwidth(t) * 1.5, 1e-4);
                float edgeAA = 1.0 - smoothstep(1.0 - aaT, 1.0 + aaT, t);

                // ===== 步骤2：法线水平分量（仅供折射使用） =====
                // 球面法线 n = (uv1, sqrt(1-|uv1|²)) 的水平分量就是
                // uv1 本身；竖直分量本版不需要（无打光），不计算。
                // saturate 语义由 |uv1|≤1 的形状坐标天然保证。
                float2 normalXY = uv1;

                // ===== 步骤3：伪折射 + RGB 色散 =====
                // 折射 = n.xy 扭动环境坐标：中心直视不扭、边缘强扭，
                // 凸透镜畸变分布。色散 = 三通道异强度采样（蓝折最多
                // 红最少），程序化 chromatic aberration。环境是假的
                // （envLum），原因见文件头【伪折射说明】。
                // 挤压脉冲时折射短暂增强——受激的玻璃"晃"得更明显。
                float bendScale = _RefractStrength * (1.0 + _Squash * 0.5);
                float2 bend = normalXY * bendScale * 0.45;
                float channelSpread = _Dispersion * 0.25;
                float lumR = envLum(uv1 + bend * (1.0 - channelSpread), flowTime);
                float lumG = envLum(uv1 + bend, flowTime);
                float lumB = envLum(uv1 + bend * (1.0 + channelSpread), flowTime);
                float3 refracted = float3(lumR, lumG, lumB);

                // ===== 步骤4：内部流动（液体的"涌动"） =====
                // fbm(uv1×3 + 对角相位) → ±14% 明度扰动
                float flow = fbm(uv1 * 3.0 + float2(flowTime * 0.55, -flowTime * 0.40));

                // ===== 步骤5：颜色合成 =====
                // ① 厚度渐变体色：中心薄（t=0，色浅通透）→ 边缘厚
                //    （t=1，色浓更实）。这是玻璃自身的观感，不是打光
                //    ——真实玻璃杯的边缘本来就比中心深。
                float3 col = _BodyColor.rgb * (0.62 + 0.38 * t);
                // ② 折射透射：环境光穿过玻璃被体色部分染色。体色偏暗，
                //    ×2 提回亮度再与白插值，避免内部死黑
                float3 transmitTint = lerp(float3(1.0, 1.0, 1.0), _BodyColor.rgb * 2.0, 0.55);
                col += refracted * transmitTint * 0.60;
                // ③ 内部流动明度微扰："液体内部涌动"
                col *= 0.86 + 0.28 * flow;

                // ===== 步骤6：挤压脉冲 =====
                // 分裂/合并/撞地瞬间整体提亮一闪（C# 侧把 _Squash 衰减回 0）
                col *= 1.0 + _Squash * 0.18;

                // ===== 步骤7：alpha 组装（窗口命中契约，见文件头） =====
                // 厚度渐变：中心 0.55（通透）→ 轮廓 0.92（厚实）；
                // ×edgeAA 仅在最外 ~1.5px 渐隐到 0；主体全程 ≥0.55。
                float glassAlpha = lerp(0.55, 0.92, t);
                float alpha = glassAlpha * edgeAA;
                alpha = saturate(alpha + _TransparencyBoost);

                return float4(col, alpha);
            }
            ENDCG
        }
    }

    Fallback "Transparent/Diffuse"
}
