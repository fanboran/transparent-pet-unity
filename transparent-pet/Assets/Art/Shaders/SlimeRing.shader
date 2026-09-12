// ================================================================
// ██████  TransparentPet/SlimeLiquid —— 程序化液态玻璃史莱姆
// ██████  （全程序化 · 零贴图 · 动态 Mesh · fwidth 屏幕空间抗锯齿）
// ================================================================
// 【为什么重写】
//   旧方案 Slime.shader 用 800x528 精灵贴图的 alpha 通道当形状遮罩，
//   分辨率固定 → 边缘出现像素阶梯（alpha 硬边 + 无抗锯齿），缩放后
//   不可接受。本方案彻底去贴图：
//     1. 形状来自 C# 侧每帧重建的动态 Mesh（29 顶点中心扇形），
//        轮廓顶点就是最终屏幕位置，形状精度 = 物理模拟精度；
//     2. 颜色 / 打光 / 折射 / 眼睛全部由数学公式现场计算，不采样
//        任何贴图（shader 里连 sampler 都没有）；
//     3. 边缘用 fwidth（屏幕空间导数）做自适应抗锯齿：抗锯齿带宽
//        自动等于 1~2 个真实像素，任意缩放都平滑。
//   目标平台：Unity 内置渲染管线（Built-in RP）/ D3D11 / SM3.0。
//
// 【Mesh 输入契约（C# 侧每帧更新）】
//   · 顶点 29 = 28 个轮廓点 + 1 个中心点，中心扇形三角化；
//   · uv0 = (t, angle / 2π)：t 为径向参数（中心 0 → 边缘 1，按到
//     质心距离 / 当前平均半径插值）；angle 为轮廓角度（本版未使用，
//     保留给未来的角向图案，如条纹/斑纹）；
//   · uv1 = (nx, ny) =（粒子位置 - 质心）/ 当前半径，中心 (0,0)、
//     边缘落在单位圆附近。一切"形状内固定位置"的效果（2.5D 法线、
//     眼睛、折射）都挂在 uv1 上 —— 形状怎么变形，效果就怎么跟着走；
//   · 顶点世界位置即最终渲染位置（正交相机，1 unit ≈ 1 屏幕像素
//     量级），vertex 阶段不做任何形变，只做标准 MVP 变换。
//
// 【整体渲染管线（fragment 分步，正文各步骤对应）】
//   步骤1  fwidth 边缘抗锯齿（见下方 alpha 契约）
//   步骤2  2.5D 球面法线：n = (uv1, sqrt(1-|uv1|²))，半球参数化
//   步骤3  Blinn-Phong：固定屏幕左上方向光 → 漫反射渐变 + 锐利高光
//   步骤4  菲涅尔 rim：视角固定 -Z，用 n.z 反推掠射角
//   步骤5  伪折射 + RGB 色散：n.xy 扭动程序化假环境，三通道异强度采样
//   步骤6  内部流动：fbm(uv1*3 + 时间相位) 明度微扰，_VelocityW 加速
//   步骤7  颜色合成：漫反射底色 + 透射环境 + rim + 高光
//   步骤8  眼睛（最后叠加，保证清晰不被 rim/高光盖住）
//   步骤9  挤压脉冲：整体亮度 + rim 增强
//   步骤10 alpha 组装（主体 ≥0.35，仅边缘 1~2px 渐隐）
//
// 【alpha 契约（透明窗口 & 鼠标命中，硬性约束）】
//   输出 alpha 会被 Win32 分层窗口层拿去做鼠标命中检测（阈值 0.1）：
//   · 史莱姆主体内部 alpha 必须 ≥ 0.35 —— 本 shader：中心 0.55
//     （玻璃通透感），随径向参数 t 线性增到 0.92（边缘"更实"），
//     rim 再加权，全程远高于 0.35；
//   · 只允许最边缘 1~2px 由 fwidth 抗锯齿带渐变到 0。
//
// 【伪折射说明（面试常问，先说清楚）】
//   透明窗口拿不到桌面真实背景像素 —— 桌面合成发生在 DWM，GPU 侧
//   无法回读屏幕。因此这里的"折射"是纯艺术近似，不是物理折射：
//     · "环境" = 竖直亮暗渐变 + 两层流动 fbm（假想房间光，标量
//       亮度场 envLum）；
//     · 折射 = 用 n.xy（球面法线的水平分量）扭动环境坐标 —— 法线
//       越偏离视线（越靠边缘）环境坐标扭得越狠，近似凸透镜边缘的
//       光线弯折；中心直视、边缘强扭，天然的透镜畸变分布；
//     · 色散 = 三个颜色通道用三个略微不同的折射强度采样同一环境，
//       模拟折射率随波长变化（正常色散：蓝光折射率 > 红光），产生
//       程序化 chromatic aberration。
//
// 【_VelocityW 加速的相位跳变说明】
//   流场相位 = 速度 × 时间。若让"速度"随 _VelocityW 实时变化，历史
//   相位会被整体重缩放 → 画面瞬间跳一下。本 shader 故意把加速系数
//   做小（最多 ×1.6），且 _VelocityW 来自物理速度模长（连续量），
//   跳变不可察觉。彻底消除需要 C# 侧积分相位后传入，当前契约只给
//   标量速度，故采用此折中。
// ================================================================

Shader "TransparentPet/SlimeRing"
{
    Properties
    {
        // 主体玻璃色（RGB 染色；A 通道本 shader 不使用——透明度由
        // "厚度公式 × 抗锯齿"决定，不受颜色 alpha 影响）
        _BodyColor ("主体玻璃色", Color) = (0.1, 0.3, 0.6, 1)
        // Blinn-Phong 高光幂指数：越大光斑越小越锐
        _SpecularPower ("高光锐度", Float) = 28
        // 镜面高光强度
        _SpecularIntensity ("高光强度", Float) = 1.1
        // 菲涅尔 rim 幂指数：越大边缘亮带越窄
        _RimPower ("边缘光锐度", Float) = 3.0
        // 菲涅尔 rim 强度
        _RimIntensity ("边缘光强度", Float) = 0.85
        // RGB 三通道折射率差 → 色散（chromatic aberration）强度
        _Dispersion ("色散强度", Range(0, 1)) = 0.35
        // 伪折射整体强度（环境坐标被 n.xy 扭动的幅度）
        _RefractStrength ("伪折射强度", Range(0, 2)) = 1.0
        // 内部流动基础流速（乘 _Time.y 得相位）
        _FlowSpeed ("流速", Float) = 0.6
        // 挤压脉冲（C# 传合并吸收时的脉冲并自行衰减）——提亮 + 增强 rim
        _Squash ("挤压脉冲", Range(0, 1)) = 0
        // 速度模长（C# 传）——稍微加速内部流动
        _VelocityW ("速度模长", Float) = 0
        // 预留：整体不透明度增益（默认 0 不生效；正值更实更醒目，
        // 用于临时调亮整体观感）
        _TransparencyBoost ("透明度增益（预留）", Float) = 0
    }

    SubShader
    {
        // Transparent+10：排在默认透明队列之后（桌宠需要压在其他
        // 半透明物之上绘制）；RenderType 供替换/相机深度纹理参考。
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        ZWrite Off
        // 双面渲染：动态网格的扇形绕向随轮廓生成顺序可能翻转，关剔除最稳妥
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        // ------------------------------------------------------------
        // 公共代码块（CGINCLUDE）：内容会被下面唯一的 Pass 原样拼接。
        // 噪声 / 环境 / 眼睛等数学函数集中放在这里并逐个注释数学含义，
        // Pass 内只剩 vert/frag 两个入口，结构一目了然。
        // ------------------------------------------------------------
        CGINCLUDE
        #include "UnityCG.cginc"

        // ==== uniforms（与 Properties 一一对应，MaterialPropertyBlock 每帧驱动）====
        float4 _BodyColor;
        float  _SpecularPower;
        float  _SpecularIntensity;
        float  _RimPower;
        float  _RimIntensity;
        float  _Dispersion;
        float  _RefractStrength;
        float  _FlowSpeed;
        float  _Squash;
        float  _Squash;
        float  _VelocityW;
        float  _TransparencyBoost;

        // ============================================================
        // 噪声函数族 —— 程序化纹理的根基（零贴图，全部现场计算）
        // ============================================================

        // ----------------------------------------------------------------
        // random —— 2D 伪随机哈希（经典 sin-hash）
        // ----------------------------------------------------------------
        // 公式来源：GPU 社区流传已久的"正弦-点积哈希"（sine-dot hash，
        // GLSL 论坛时代诞生、Shadertoy 时代普及的老牌哈希），本项目的
        // Godot 原版与旧 Slime.shader 用的同一套，保持血统。
        //
        // 数学：h(p) = frac( sin( p·d ) · C )
        //   1. dot(p, d)：把 2D 坐标投影到"魔法方向" d=(12.9898, 78.233)
        //      —— 两个精心挑选的大常数，让相邻整数格点的投影值尽量错开；
        //   2. sin()：利用正弦的混沌放大特性，输入的微小差异被放大成
        //      剧烈的输出差异；
        //   3. · 43758.5453 后 frac()：再放大一个数量级后取小数部分。
        //      大数的低位小数对输入极端敏感（浮点精度噪声），等效于
        //      把结果均匀打散到 [0,1)。
        // 性质：确定（同输入同输出）、无状态、只要 3 次基本运算。
        // 已知缺陷：坐标极大（|p| > 数万）时 float 精度下降会出现相关
        // 性条纹；本 shader 坐标范围 |uv1| ≤ ~1.5，绝对安全。
        float random(float2 st)
        {
            return frac(sin(dot(st, float2(12.9898, 78.233))) * 43758.5453);
        }

        // ----------------------------------------------------------------
        // valueNoise —— 2D 值噪声（晶格值噪声：四角哈希 + Hermite 插值）
        // ----------------------------------------------------------------
        // 数学：把连续坐标 st 拆成
        //   i = floor(st)  晶格整数坐标（当前格子左下角）
        //   f = frac(st)   格内相对位置
        // 四个格角各自用 random() 哈希出一个随机"高度" a,b,c,d，再用
        // 平滑权重 u = f²(3-2f) 做双线性插值：
        //   n(st) = lerp(a,b,u.x) + (c-a)·u.y·(1-u.x) + (d-b)·u.x·u.y
        // u = f²(3-2f) 是 Hermite 三次曲线（即 smoothstep 的核），C1
        // 连续 —— 消除晶格边界处线性插值的一阶导突兀折痕。
        // 与 Perlin 噪声的区别：插值对象是"格点随机值"（值噪声）而非
        // "格点梯度"（梯度噪声）。值噪声实现更省、无方向纹理感，正好
        // 符合液体内部柔和团絮的观感。
        // 品质备注：Perlin 推荐的五次权重 u = f³(f(f·6-15)+10) 是 C2
        // 连续，可进一步消频闪；桌宠像素级尺寸下三次权重足够。
        float valueNoise(float2 st)
        {
            float2 i = floor(st);
            float2 f = frac(st);
            float2 u = f * f * (3.0 - 2.0 * f);        // Hermite 平滑权重

            float a = random(i);                         // 格角 (0,0)
            float b = random(i + float2(1.0, 0.0));      // 格角 (1,0)
            float c = random(i + float2(0.0, 1.0));      // 格角 (0,1)
            float d = random(i + float2(1.0, 1.0));      // 格角 (1,1)

            return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
        }

        // ----------------------------------------------------------------
        // fbm —— 分形布朗运动（4 倍频程值噪声叠加，纯空间场）
        // ----------------------------------------------------------------
        // 数学：fbm(p) = Σ_{i=0..3} gain^i · noise(2^i · p)
        //   即 0.5·noise(p) + 0.25·noise(2p) + 0.125·noise(4p) + 0.0625·noise(8p)
        //   lacunarity = 2（每层频率翻倍），gain = 0.5（每层振幅减半）
        //   → 频谱 ≈ 1/f，与云、水面、大理石等自然纹理的统计特征一致
        //   （自相似 + 越细的细节幅度越小）。
        // 设计：本函数是"纯空间场"，时间相位由调用方加在坐标上注入
        // —— 这样不同调用点可以各自用不同方向/速度流动，互不干扰
        // （旧版把时间焊死在 fbm 内部，所有调用点只能同步滚动）。
        // 值域约 [0, 0.94]（振幅和 0.5+0.25+0.125+0.0625），均值 ~0.47。
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

        // ----------------------------------------------------------------
        // envLum —— 伪环境亮度场（假想"房间光"的标量亮度，供折射采样）
        // ----------------------------------------------------------------
        // 组成（不是真实背景！原因见文件头【伪折射说明】）：
        //   1. 竖直渐变：g = saturate(p.y·0.5 + 0.5) 把 p.y ∈ [-1,1]
        //      映射到 [0,1]，上亮下暗 —— 想象房间上方挂了主灯；
        //   2. 大尺度流动层：p×2.2，相位向 -y 漂移（噪声图案向上卷动，
        //      "热对流"感），幅度 ±0.25；
        //   3. 细尺度流动层：p×5.0，斜向漂移（对角流向更自然），幅度
        //      ±0.125。
        // 两层都吃同一个"液体时钟" flowTime —— 与内部流动（步骤6）
        // 共用节奏，整只史莱姆的呼吸感一致。返回值约 [0.05, 1.3]。
        float envLum(float2 p, float flowTime)
        {
            float grad = lerp(0.30, 0.85, saturate(p.y * 0.5 + 0.5));
            float bigFlow = fbm(p * 2.2 + float2(0.0, -flowTime * 0.7));
            float fineFlow = fbm(p * 5.0 + float2(flowTime * 0.9, flowTime * 0.5));
            return grad + (bigFlow - 0.5) * 0.50 + (fineFlow - 0.5) * 0.25;
        }

        // ----------------------------------------------------------------
        // （程序化眼睛已在 2026-09 按用户要求彻底移除：史莱姆只做液态玻璃
        //   本体，不绘制瞳孔/眨眼/视线——历史版本的 eyeMasks 函数与
        //   步骤8 应用代码均已删除，uniforms 与 Properties 同步清理）
        // ----------------------------------------------------------------

        // ============================================================
        // 顶点着色器 —— 只做标准 MVP 变换 + 透传两组 UV
        // ============================================================
        // 形状（顶点位置）完全由 C# 侧每帧重建，GPU 不做顶点动画；
        // 所有视觉效果都在片元阶段基于 uv0/uv1 推导。
        struct appdata
        {
            float4 vertex : POSITION;   // 本地空间顶点 = 最终渲染位置（正交相机）
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
            // SM3.0：fwidth/ddx/ddy 等屏幕空间导数指令需要着色器模型
            // 3.0（D3D11 下原生支持，无兼容性问题）
            #pragma target 3.0

            // ============================================================
            // 片元着色器 —— 全程序化液态玻璃
            // ============================================================
            // 分步注释对应文件头【整体渲染管线】，逐步可对着讲数学。
            float4 frag(v2f i) : SV_Target
            {
                float t = i.uv0.x;      // 径向参数：0 中心 → 1 轮廓
                float2 uv1 = i.uv1;     // 归一化形状坐标（法线/眼睛/折射的基准）

                // ===== 步骤0：液体时钟 =====
                // 流速被 _VelocityW 稍微加速（最多 ×1.6）；saturate 防
                // C# 侧传入异常负值。相位跳变问题见文件头说明。
                float flowSpeed = _FlowSpeed * (1.0 + saturate(_VelocityW) * 0.6);
                float flowTime = _Time.y * flowSpeed;

                // ===== 步骤1：边缘 fwidth 抗锯齿 =====
                // fwidth(t) = |∂t/∂x| + |∂t/∂y|：GPU 在 2×2 像素 quad
                // 上差分得到的"相邻像素间 t 的变化量"。轮廓附近 t 沿
                // 径向近似线性增长（每像素约 1/半径），所以
                //   fwidth(t) ≈ 1 / 半径像素数
                // → 抗锯齿带宽自动锁定为 ~1.5 个真实像素，与缩放、
                // 分辨率无关（放大 = 半径像素数变多 = fwidth 变小）。
                // alpha = 1 - smoothstep(1-aa, 1+aa, t)：t < 1-aa 实心、
                // t > 1+aa 全透，2aa 宽的过渡带骑在轮廓 t=1 上；t=1 处
                // smoothstep 恰为 0.5 → 边缘像素 50% 覆盖率，标准 AA。
                // max(...,1e-4) 防退化：fwidth 为 0 时 smoothstep 除零。
                float aaT = max(fwidth(t) * 1.5, 1e-4);
                float edgeAA = 1.0 - smoothstep(1.0 - aaT, 1.0 + aaT, t);

                // ===== 步骤2：2.5D 球面法线（半球参数化） =====
                // 假设形状是单位圆盘上方的一块球冠：水平分量直接取
                // uv1，竖直分量由单位球方程补全：
                //   n = (uv1.x, uv1.y, sqrt(1 - |uv1|²))
                // 中心 n=(0,0,1) 正对镜头；轮廓 |uv1|→1 时 n.z→0（切向
                // 掠射）。saturate 防变形瞬间 |uv1| 略超 1 时根号内为负。
                float r2 = dot(uv1, uv1);
                float3 n = float3(uv1.x, uv1.y, sqrt(saturate(1.0 - r2)));

                // ===== 步骤3：Blinn-Phong 打光（固定屏幕左上方向光） =====
                // 光源在屏幕左上并稍微朝向观察者（L.z>0 才照得亮正对
                // 镜头的面）。正交相机 + 视角固定 -Z → 视线向量恒 (0,0,1)。
                //   diffuse = saturate(N·L)：左上亮、右下暗的体积渐变；
                //   halfVec = normalize(L+V)：Blinn-Phong 半程向量
                //   （Phong 反射向量 R·V 的廉价近似），spec = (N·H)^p
                //   —— 法线恰好平分"光线-视线"夹角处高光最强，随法线
                //   自然落在左上，不需要手工摆高光位置。
                float3 lightDir = normalize(float3(-0.55, 0.65, 0.55));
                float3 viewDir = float3(0.0, 0.0, 1.0);
                float diffuse = saturate(dot(n, lightDir));
                float3 halfVec = normalize(lightDir + viewDir);
                float specular = pow(saturate(dot(n, halfVec)), _SpecularPower) * _SpecularIntensity;

                // ===== 步骤4：菲涅尔 rim（视角固定，用 n.z 反推） =====
                // 真实菲涅尔 ≈ pow(1 - cosθ, p)，θ 为视线与法线夹角。
                // 视角固定 (0,0,1) → cosθ = N·V = n.z：中心 1（正对，
                // 无 rim）→ 轮廓 0（掠射，rim 最强）。
                // rim = (1 - n.z)^_RimPower × 强度；挤压脉冲时短暂增强。
                float rim = pow(1.0 - n.z, _RimPower) * _RimIntensity;
                rim *= 1.0 + _Squash * 0.8;

                // ===== 步骤5：伪折射 + RGB 色散 =====
                // 折射 = 用 n.xy 扭动环境坐标：中心 n.xy≈0 直视不扭，
                // 越靠边缘 n.xy 越大扭得越狠 —— 天然的凸透镜畸变分布。
                // 色散 = 三通道用不同折射强度采样（蓝折最多、红最少，
                // 对应正常色散 dn/dλ < 0），通道间错位即程序化
                // chromatic aberration。环境本身是假的（envLum：竖直
                // 渐变 + 两层流动 fbm），因为透明窗口拿不到真实桌面
                // 像素 —— 详见文件头【伪折射说明】。
                float2 bend = n.xy * _RefractStrength * 0.45;
                float channelSpread = _Dispersion * 0.25;   // 通道间折射差
                float lumR = envLum(uv1 + bend * (1.0 - channelSpread), flowTime);
                float lumG = envLum(uv1 + bend, flowTime);
                float lumB = envLum(uv1 + bend * (1.0 + channelSpread), flowTime);
                float3 refracted = float3(lumR, lumG, lumB);

                // ===== 步骤6：内部流动（保留 Godot 版的"液体感"） =====
                // fbm(uv1×3 + 时间相位)：×3 控制细节密度；相位沿对角
                // 漂移（x 正 y 负 → 图案向右上卷）。输出约 [0,0.94]，
                // 下方映射成 ±14% 的明度扰动。
                float flow = fbm(uv1 * 3.0 + float2(flowTime * 0.55, -flowTime * 0.40));

                // ===== 步骤7：颜色合成 =====
                // ① 漫反射底色：环境底光 0.5 + 方向光 0.7·N·L
                //    （左上 1.2 倍亮、右下 0.5 倍暗的体积渐变）
                float3 col = _BodyColor.rgb * (0.50 + 0.70 * diffuse);
                // ② 折射透射：环境光穿过玻璃被体色部分染色。体色偏暗，
                //    先 ×2 提回亮度再与白插值，避免内部死黑
                float3 transmitTint = lerp(float3(1.0, 1.0, 1.0), _BodyColor.rgb * 2.0, 0.55);
                col += refracted * transmitTint * 0.55;
                // ③ 内部流动明度微扰：0.86 ~ 1.14 倍，"液体内部涌动"
                col *= 0.86 + 0.28 * flow;
                // ④ rim：向冷白提亮的边缘光带（玻璃掠射聚光的观感）
                float3 rimColor = lerp(_BodyColor.rgb, float3(1.0, 1.0, 1.0), 0.65);
                col += rim * rimColor;
                // ⑤ 镜面高光：纯白锐利光斑，最后直接叠加
                col += specular;

                // ===== 步骤8：挤压脉冲 =====
                // 合并吸收瞬间整体提亮（配合步骤4的 rim增强，构成
                // 一闪而过的"果冻受激"反馈；C# 侧自行把 _Squash 衰减回 0）
                col *= 1.0 + _Squash * 0.18;

                // ===== 步骤9：alpha 组装（窗口命中契约，见文件头） =====
                // · 玻璃厚度感：中心 0.55（通透）→ 轮廓 0.92（更实：
                //   掠射方向光在体内路径更长，真实玻璃边缘本就更实）；
                // · rim 加权 0.22：亮环处稍微更实，轮廓读得更清；
                // · ×edgeAA：只在最外 ~1.5px 渐隐到 0；
                // · _TransparencyBoost：预留旋钮（默认 0，不改变行为）。
                // 结果：主体内部全程 ≥0.55，只有最边缘 1~2px 掉到 0
                // ——满足"命中阈值 0.1、主体 ≥0.35"的硬性契约。
                float glassAlpha = lerp(0.55, 0.92, t);
                glassAlpha = saturate(glassAlpha + rim * 0.22);
                float alpha = glassAlpha * edgeAA;
                alpha = saturate(alpha + _TransparencyBoost);

                return float4(col, alpha);
            }
            ENDCG
        }
    }

    Fallback "Transparent/Diffuse"
}
