#ifndef BHC_COMMON_HLSL
#define BHC_COMMON_HLSL

// ---------------------------------------------------------------------------
// 常量缓冲区：由 C# 端 SceneRenderer 填充（每行 float4，符合 16 字节对齐）
// 所有长度单位均为物理像素，坐标原点为覆盖层左上角，y 轴向下。
// ---------------------------------------------------------------------------
// ⚠️ 与 C# 侧 SceneConstants 是**按字节顺序**隐式对应的（不是按名字）：
// 增删行必须同时改 SceneConstants.cs 的字段与 FloatCount，否则编译不报错、
// 运行时静默错位。每行固定 float4，未用分量填 0（对齐 + 预留）。
cbuffer SceneParams : register(b0)
{
    float4 Viewport;      // xy = 宽高(px)      zw = 1/宽, 1/高
    float4 CenterRadius;  // xy = 黑洞中心(px)  zw = 保留
    float4 Timing;        // x  = 时间(秒)      w  = 透镜开关        y,z = 保留
    float4 Extra;         // w  = 淡入淡出(N4)  x,y = 视差（光标位置，归一化到屏高；fixes-v2 问题 8）  z = 保留
    float4 Debug;         // 验证入口用：x = 色温(K) / spin   y = 位移放大 / 噪声缩放
                          //             z = rin / 角向周期    w = 旋转角(rad)

    // ---- v2 测地线渲染专用（M1.2 骨架；M1.4 起真正使用）----
    float4 Hole;          // x = 阴影半径(屏高单位)  y = W = B_CRIT/半径  z = 积分步数  w = 星云强度(fixes-v2 问题 1)
    float4 Look0;         // x = 色温K  y = 倾角rad  z = roll rad  w = 染色强度
    float4 Look1;         // x = 内径r_s  y = 外径r_s  z = 盘不透明度  w = 多普勒混合
    float4 Look2;         // x = beam  y = gain  z = contrast  w = winding
    float4 Look3;         // x = speed  y = exposure  z = star  w = spin
    float4 Tint;          // 自定义染色 rgba(0..1)
    float4 CaptureMap;    // fixes-v2 问题 5：窗口 uv → 抓屏纹理 uv（xy = 缩放，zw = 偏移）
    float4 NebulaTint;    // fixes-v2 问题 6：星云亮部配色 rgb（预设可带）
};

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

// 全屏三角形：无需顶点缓冲，3 个顶点覆盖整个视口。
VSOut FullscreenVS(uint vertexId : SV_VertexID)
{
    float2 p = float2(float((vertexId << 1) & 2), float(vertexId & 2));
    VSOut o;
    o.pos = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
    o.uv = p;
    return o;
}

// 哈希：v2 的盘条纹噪声与程序化星场都基于它
float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// ---------------------------------------------------------------------------
// v2 测地线渲染常量。必须与 C# 侧 LensMath 的同名常量保持一致
// （LensDepth = 13.0、CriticalImpactParameter = 2.5980762）。
// ---------------------------------------------------------------------------
static const float LENS_DEPTH = 13.0;      // 桌面「天空平面」到黑洞的距离，单位 r_s
static const float B_CRIT     = 2.5980762; // 临界撞击参数 (3√3/2)·r_s

// ---------------------------------------------------------------------------
// fixes-v2 问题 8：星空 / 星云的「随位置变化」。
// 单位换算：Extra.xy 是光标位置除以屏高（0..~1.8），乘下面系数后
// 直接加到各自的采样坐标上 —— 于是鼠标一动，图案就跟着漂。
// ⚠️ 两者尺度差 18 倍：星云 p = sph*2.2，星空格 = sph*40，
// 所以同一个系数给星空会让它疯狂跳格，必须分开调。
// ---------------------------------------------------------------------------
static const float NEBULA_PARALLAX = 0.35;   // 横穿全屏 ≈ 星云采样位移 0.6（约 1/4 个云絮周期）
static const float STAR_PARALLAX   = 0.50;   // 横穿全屏 ≈ 星空跨过约 1 个格子（星星缓缓掠过）

// ---------------------------------------------------------------------------
// fixes-v3 问题 6（星云的割裂缝）：方位角方向**每整圈必须取整数格**。
//
// 原来 `p = sph * 2.2` ⇒ 一整圈是 2π × 2.2 = **13.82 格**（非整数）。
// 跨过 atan2 的分支切口（±π）时，方位角从 +π 跳到 −π ⇒ 噪声的格子索引整块跳 ~13.8 格
// ⇒ 切口两侧是两片**互不相干**的云 ⇒ 画面上就是一条硬缝（跟着黑洞走，因为它是方向空间里的曲线）。
//
// 解法与 `vnoiseWrapY`（吸积盘条纹当年踩的同一个坑）同源，但那里只 wrap 了 y，
// 而星云的切口在 **x（方位角）** 上 ⇒ 这里改用 `vnoiseWrapXY`，x / y 都 wrap。
//
// 三个八度各自的「每整圈格子数」都必须是**整数**，且尽量贴近原来的 2.07 倍频比：
//   14 → 29 → 60   ⇒ freq = 1.0 / 2.071 / 4.286（原来是 1.0 / 2.07 / 4.285，密度几乎不变）
// 方位角缩放也随之从 2.2 变成 14 / 2π = 2.2282（云的密度看不出差别）。
// ---------------------------------------------------------------------------
static const float NEBULA_CELLS_PER_TURN = 14.0;                      // 第 1 个八度每整圈的格子数
static const float NEBULA_OCTAVE_CELLS[3] = { 14.0, 29.0, 60.0 };     // 三个八度各自的每整圈格子数

// ---------------------------------------------------------------------------
// fixes-v3 问题 2：氛围（星云流动 / 星空闪烁）的**流速**。
//
// 原来这些系数全是写死的常量，快慢没法调（你觉得「变化太慢」）。
// 倍率由 CPU 通过 `Timing.z` 传入（默认 1 = 原速，0 = 完全冻结，最大 3 倍速），
// 统一走这一个入口 —— 否则星云与星空各乘一次，以后改一处就会漏另一处。
//
// ⚠️ 只乘**时间项**：位置项（Extra.xy 的视差）不乘，否则「移动鼠标时图案跟着漂」
// 会被改成「移动鼠标时图案乱跳」，那是另一个维度的事。
// ---------------------------------------------------------------------------
float AmbientTime()
{
    return Timing.y * Timing.z;
}

/// <summary>
/// 薄盘的几何与着色参数。M1.6：Schwarzschild 与 Kerr 两条积分路径共用，
/// 保证「盘怎么着色」只有一份实现。
/// </summary>
struct DiskGeom
{
    float3 n;        // 盘法线（Kerr 路径里它就是自旋轴）
    float3 e2;       // 盘面内基向量，与 x̂、n 构成正交基
    float rin;       // 盘内径（r_s）
    float rout;      // 盘外径（r_s）
    float opac;      // 盘不透明度
    float sdir;      // 公转方向（±1）
    float spd;       // 条纹速度（绝对值）
    float t;         // 时间（秒）
    bool withDisk;   // 是否在积分中累积盘穿越
};

// ===========================================================================
// M1.1：v2 测地线渲染所需的工具函数
// 与参考实现 singularity(singularity.wgsl) 逐行对应，仅把 WGSL 语义翻译成 HLSL。
// 目前尚未接入主流程（M1.4 接入），先用下方两个验证入口做单元测试。
// ===========================================================================

float gmod(float x, float y)
{
    return x - y * floor(x / y);
}

// 把 uv 折回 [0,1]，用于重复平铺取样
float2 mirrorUV(float2 uvin)
{
    float2 m = uvin - 2.0 * floor(uvin / 2.0);
    return 1.0 - abs(1.0 - m);
}

// 屏幕平面内旋转
float2 rot(float2 v, float a)
{
    float c = cos(a);
    float s = sin(a);
    return float2(c * v.x - s * v.y, s * v.x + c * v.y);
}

// 角向 wrap 噪声：y 方向每 perY 格回绕一次，
// 这样吸积盘的条纹跨过 atan2 的分支切口时不会出现接缝。
float vnoiseWrapY(float2 p, float perY)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float y0 = gmod(i.y, perY);
    float y1 = gmod(i.y + 1.0, perY);

    return lerp(
        lerp(Hash21(float2(i.x, y0)), Hash21(float2(i.x + 1.0, y0)), f.x),
        lerp(Hash21(float2(i.x, y1)), Hash21(float2(i.x + 1.0, y1)), f.x),
        f.y);
}

// fixes-v3 问题 6：x / y **两个方向**都 wrap 的版本。
//
// 与 `vnoiseWrapY` 的差别只在 x：方位角跨过 `atan2` 的分支切口（±π）时会整块跳变，
// 只有把 x 的格子索引也按整数周期绕回来，切口两侧才是同一片噪声。
// ⚠️ 必须同时 wrap `i.x` 与 `i.x + 1`（`vnoiseWrapY` 对 y 就是这么做的）——
//    只 wrap 一个的话，**边界那一格**仍然会断。
float vnoiseWrapXY(float2 p, float2 per)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float x0 = gmod(i.x, per.x);
    float x1 = gmod(i.x + 1.0, per.x);
    float y0 = gmod(i.y, per.y);
    float y1 = gmod(i.y + 1.0, per.y);

    return lerp(
        lerp(Hash21(float2(x0, y0)), Hash21(float2(x1, y0)), f.x),
        lerp(Hash21(float2(x0, y1)), Hash21(float2(x1, y1)), f.x),
        f.y);
}

// 黑体色（Tanner Helland 拟合）：输入开尔文，输出归一化 rgb。
// 注意 t <= 66 与 t > 66 走不同分支，且 pow 的底数必须为正，分支条件不能改。
float3 blackbody(float T)
{
    float t = clamp(T, 1500.0, 40000.0) / 100.0;

    float r = 1.0;
    if (t > 66.0) { r = clamp(1.292936 * pow(t - 60.0, -0.1332047), 0.0, 1.0); }

    float g = 0.0;
    if (t <= 66.0) { g = clamp(0.3900816 * log(t) - 0.6318414, 0.0, 1.0); }
    else { g = clamp(1.1298909 * pow(t - 60.0, -0.0755148), 0.0, 1.0); }

    float b = 1.0;
    if (t < 66.0)
    {
        if (t <= 19.0) { b = 0.0; }
        else { b = clamp(0.5432068 * log(t - 10.0) - 1.1962540, 0.0, 1.0); }
    }

    return float3(r, g, b);
}

// 稀疏程序化星场：按（已被弯折的）光线方向索引
float3 stars(float3 d)
{
    float2 sph = float2(atan2(d.x, -d.z), asin(clamp(d.y, -1.0, 1.0)));

    // fixes-v2 问题 8：位置项 —— 鼠标一动，星星缓缓掠过（而不是钉在天球上不动）
    float2 g = sph * 40.0 + Extra.xy * STAR_PARALLAX;
    float2 id = floor(g);
    float h = Hash21(id);

    // HLSL 的数值构造器不接受「标量展开」（WGSL 可以），必须写满分量
    if (h < 0.92) { return float3(0.0, 0.0, 0.0); }

    float2 f = frac(g) - 0.5;
    float2 off = (float2(Hash21(id + 17.3), Hash21(id + 31.7)) - 0.5) * 0.7;
    float spark = smoothstep(0.10, 0.0, length(f - off));

    // fixes-v2 问题 8：闪烁改用 `Timing.y`（**不受盘速度门控**的时间）。
    // 原来用 `Timing.x`，盘速设 0 时时间被冻结 → 星星全都不闪了，
    // 恰好违反「静止时也要随时间变化」。
    // fixes-v3 问题 2：闪烁速度也归「氛围流速」管（`Timing.z`）
    float tw = 0.7 + 0.3 * sin(AmbientTime() * (0.5 + 2.0 * Hash21(id + 5.1)) + 40.0 * h);
    float3 tint = lerp(float3(1.0, 0.82, 0.60), float3(0.75, 0.85, 1.0), Hash21(id + 2.9));

    return tint * spark * tw * ((h - 0.92) / 0.08);
}

// ---------------------------------------------------------------------------
// fixes-v2 问题 1：程序化背景光（星云 / 辉光）。
//
// 为什么需要它：v2 的观赏性过度依赖「桌面上正好有东西可以折射」——
// 暗色桌面（甚至抓屏被判全黑）时，`bg * trans`（被弯折的桌面）整层归零，
// 于是只剩盘本体和一圈细环，观感从「成片光环 + 亮带」塌成「一个孤零零的盘」。
//
// 这一层是**自发光**：不依赖抓屏内容，任何背景下都在，
// 但仍然按弯折后的光线方向索引 —— 所以它同样会被透镜弯折、被盘遮挡，
// 看起来就是「黑洞背后有片星云被吸进来」，而不是贴在屏幕上的一层雾。
// ---------------------------------------------------------------------------
float3 nebula(float3 d)
{
    // 与 stars() 同一套方向参数化（方位角 + 仰角），保证两者在同一片天球上。
    // fixes-v3 问题 6：方位角改为按「每整圈 NEBULA_CELLS_PER_TURN 格」缩放
    // （= 14 / 2π ≈ 2.228，原来是 2.2），这样跨切口时格子索引正好绕回同一格。
    float2 sph = float2(atan2(d.x, -d.z), asin(clamp(d.y, -1.0, 1.0)));
    float2 p = float2(sph.x * (NEBULA_CELLS_PER_TURN / 6.2831853), sph.y * 2.2);

    // fixes-v2 问题 4「炫彩加变化」：时间上的流动。
    // 用 `Timing.y`（**不受盘速度门控**的时间）而不是 `Timing.x` ——
    // 这样即使把盘冻结（DiskSpeed = 0），星云依旧缓慢演化，鼠标不动时画面也在动。
    // 幅度刻意很小：让云"在流动"，而不是让画面闪。
    // fixes-v3 问题 2：`AmbientTime()` 已乘上流速倍率 —— 平移与下面的呼吸一起被加速 / 冻结
    float t = AmbientTime();
    p += float2(t * 0.015, t * 0.006);          // 场缓慢平移（沿方位角方向漂移）

    // fixes-v2 问题 8：位置项 —— 鼠标一动，云絮跟着漂（静止时只有上面的时间项在动）
    p += Extra.xy * NEBULA_PARALLAX;

    // 3 个八度的 value noise。perY 给得足够大，保证在 ±π 的仰角范围内不会提前回绕
    float n = 0.0;
    float amp = 0.55;
    for (int i = 0; i < 3; i++)
    {
        // 每个八度各自的「每整圈格子数」都是整数 ⇒ 跨切口时格子索引正好绕回同一格
        float perX = NEBULA_OCTAVE_CELLS[i];
        float freq = perX / NEBULA_CELLS_PER_TURN;   // ≈ 1.0 / 2.071 / 4.286
        n += amp * vnoiseWrapXY(p * freq, float2(perX, 64.0));
        amp *= 0.5;
    }

    // 先提对比（pow）再压一个软阈值，把噪声拉成有层次的云絮而不是均匀灰雾。
    // ⚠️ 系数是量出来的，不是拍的：三层八度的均值约 0.48，
    // pow(0.48, 2.2) + smoothstep(0.12, 0.85) 会把均值压到 0.05 —— 画面上等于没有光。
    float cloud = pow(saturate(n), 1.35);
    cloud = smoothstep(0.08, 0.75, cloud);

    // 配色：亮的地方偏暖（像被黑洞加热），暗的地方偏冷蓝紫。
    // fixes-v2 问题 6：亮端由 `NebulaTint` 决定 —— 预设可以带自己的星云色调。
    float3 warm = NebulaTint.rgb;

    // fixes-v3 问题 3：暗端**不再是写死的冷蓝**。
    //
    // 原来暗端是 float3(0.26, 0.44, 1.00)（饱和蓝，亮度约 0.45）。全屏之后
    // 整屏 alpha = 1（plan-v3 P1.4），而这层云有 0.12 的亮度地板 ⇒
    // 那圈冷蓝的稀薄边缘变成**一大圈**、非常显眼 —— 你要的是「去掉蓝色，星云照常可见」。
    //
    // 做法：暗端取**亮端色调的去饱和暗部版本** ——
    //   ① 先按亮度把 NebulaTint 压成灰（去掉饱和的蓝）；
    //   ② 掺回 25% 原色调（暖色预设的暗部仍带一点暖，不至于死灰）；
    //   ③ 归一到与旧冷蓝**相当的亮度**（0.42 ≈ 旧值 0.45）——
    //      亮度基本不变、只去掉蓝，这样「星云照常可见」才守得住。
    float3 luma = float3(0.299, 0.587, 0.114);
    float grayLevel = dot(warm, luma);
    float3 gray = float3(grayLevel, grayLevel, grayLevel);
    float3 desaturated = lerp(gray, warm, 0.25);
    float3 cool = desaturated * (0.42 / max(dot(desaturated, luma), 1e-3));

    float3 tint = lerp(cool, warm, saturate(cloud * 1.3 - 0.25));

    // fixes-v2 问题 4：缓慢的明暗呼吸（第二种「时间上的流动」）。
    // 与上面的平移叠加：整体既在漂也在明暗起伏，周期约 25 秒，看不出重复感。
    float breath = 0.85 + 0.15 * sin(t * 0.25 + sph.x * 1.7);

    // fixes-v2 问题 5：留一个亮度地板。
    // 折射层取不到桌面内容的那一圈（取样点跑到抓屏区外）现在由星云接管、
    // alpha 会拉到 1 —— 若星云允许取到 0，那些像素就会变成纯黑斑点。
    cloud = max(cloud, 0.12);

    return tint * cloud * breath;
}

// ---- 单元验证入口（仅供测试渲染一次，不参与正常绘制） ----
// rgb = blackbody(Debug.x)，a = vnoiseWrapY(...)
float4 ToolsColorProbePS(VSOut input) : SV_Target
{
    return float4(blackbody(Debug.x), vnoiseWrapY(input.uv * Debug.y, Debug.z));
}

// ---------------------------------------------------------------------------
// fixes-v3 问题 6 的验证入口：把「方位角扫一整圈」时的星云值直接画出来。
//   x（uv.x 0→1）= 方位角 −π → +π
//   y（uv.y 0→1）= 仰角 −1.2 → +1.2
//
// 判据在 **uv.x 的 0 与 1 接缝处**：那里方位角从 +π 跳到 −π，
// 但它们在空间里是**相邻的两个方向**（只是跨了 atan2 的分支切口），
// 所以星云的值必须连续 —— 不连续就是画面上那条割裂缝。
// ---------------------------------------------------------------------------
float4 NebulaSeamProbePS(VSOut input) : SV_Target
{
    float azimuth = (input.uv.x * 2.0 - 1.0) * 3.14159265;
    float elevation = (input.uv.y * 2.0 - 1.0) * 1.2;

    // 反解 nebula() 的 sph：sph.x = atan2(d.x, -d.z) = azimuth，sph.y = asin(d.y) = elevation
    float ce = cos(elevation);
    float3 d = normalize(float3(sin(azimuth) * ce, sin(elevation), -cos(azimuth) * ce));

    return float4(nebula(d), 1.0);
}

// rg = rot(v, Debug.w) + 0.5（旋转应保持长度），ba = mirrorUV(...)
float4 ToolsTransformProbePS(VSOut input) : SV_Target
{
    float2 v = input.uv - 0.5;
    float2 r = rot(v, Debug.w);
    float2 m = mirrorUV(input.uv * 3.0);
    return float4(r + 0.5, m);
}

#endif
