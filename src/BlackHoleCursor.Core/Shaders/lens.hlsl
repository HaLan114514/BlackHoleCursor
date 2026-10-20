// ---------------------------------------------------------------------------
// 引力透镜取样：把真实抓取到的桌面画面按弱场近似做光线偏折。
// 偏折量 defl = strength * Rh^2 / r，并钳制在 0.9r 以内（禁止采样越过中心，避免画面撕裂）。
// 返回值是「预乘 alpha」的背景：影响半径外 alpha 归零，让真实桌面透出来，避免整块区域延迟一帧。
// ---------------------------------------------------------------------------

Texture2D BackgroundTex : register(t0);
SamplerState LinearClampSampler : register(s0);

// 超出 [0,1] 多远就完全淡出（uv 单位）。太小会露出硬边，太大会让窗口外的重复内容又冒出来。
// fixes-v2 问题 5：0.08 时这个淡出在半径方向上只跨 1~2 px，配合「alpha 跟着内容走」
// 会在阴影外侧留下一圈很陡的凹陷；放宽到 0.18 并改用 smoothstep 后过渡是渐变的。
static const float BACKGROUND_FADE = 0.18;

// fixes-v2 问题 2：高阶像衰减的两个标定常量（弧度）。
// HIGH_ORDER_FREE_TURN：这个转角以内不衰减（主像与第一次返回的镜像二次像都落在这里）；
// HIGH_ORDER_DECAY_TURN：超过之后每多转这么多弧度，亮度乘 1/e。
// 标定依据见 `tests/RingArtifactTests.cs`（`OrderProbePS` 把转角与衰减系数画出来量化）。
static const float HIGH_ORDER_FREE_TURN = 4.2;
static const float HIGH_ORDER_DECAY_TURN = 1.1;

// 桌面「天空」取样。
//
// fixes-v2 问题 2：**不再镜像平铺**。
// 原来越界会被 `mirrorUV` 折回来，于是同一块桌面内容在阴影周围被反复复制，
// 表现为「一圈圈等间距同心亮环」，一直铺到窗口边缘（bugs/1.jpg 里连界面文字
// 「Gargantua」「Face-on ember」都被复制到了不同半径上）。
//
// 抓屏只覆盖覆盖层窗口那一块，窗口之外我们**并不知道**是什么，
// 所以正确做法是**淡出**，把那里交给程序化背景光（fixes-v2 问题 1 的星云）。
//
// 注意：淡出只吃掉「取样点跑到窗口之外」的那些光线 ——
// 阴影外侧的镜像二次像（F1-2）取样点仍在窗口内，因此不受影响。
float3 background(float2 uvWindow)
{
    // fixes-v2 问题 5：抓屏纹理是**整块显示器**（固定区域，与光标无关），
    // 所以先把窗口局部 uv 换算到显示器 uv 再取样。
    // 恒等映射（1,1,0,0）下与旧行为完全一致 —— 预览与单元测试走的就是这条。
    float2 uvin = uvWindow * CaptureMap.xy + CaptureMap.zw;

    float2 outside = max(float2(0.0, 0.0) - uvin, uvin - float2(1.0, 1.0));
    float d = saturate(max(outside.x, outside.y) / BACKGROUND_FADE);

    // 与 backgroundCoverage 用同一条 smoothstep：颜色与 alpha 必须一起淡，否则会留脏边
    float fade = 1.0 - (d * d * (3.0 - 2.0 * d));

    return BackgroundTex.SampleLevel(LinearClampSampler, clamp(uvin, 0.0, 1.0), 0.0).rgb * fade;
}

/// <summary>
/// fixes-v2 问题 5：这个取样点**到底有没有折射内容**（1 = 有，0 = 没有）。
/// 与 <see cref="background"/> 用的是同一套淡出公式，只是返回系数而不是颜色。
///
/// 为什么需要它：alpha 必须跟着「内容」走。以前 alpha 用的是 <c>window</c>（折射层的
/// 覆盖范围），于是在 3~7 个阴影半径处 alpha 只有 0.8~0.37 —— 真实桌面按 (1−alpha)
/// 透出来，屏幕上是「折射过的」和「没折射过的」两层叠在一起（折射那层太浅、没折射那层又明显）。
/// 现在 alpha 只由「这里确实画了折射内容」决定，没内容的地方才让桌面透出来。
/// </summary>
float backgroundCoverage(float2 uvWindow)
{
    float2 uvin = uvWindow * CaptureMap.xy + CaptureMap.zw;
    float2 outside = max(float2(0.0, 0.0) - uvin, uvin - float2(1.0, 1.0));
    float d = saturate(max(outside.x, outside.y) / BACKGROUND_FADE);

    // smoothstep 而不是线性：线性淡出在边界处斜率突变，半径方向上会出现可见的坎。
    return 1.0 - (d * d * (3.0 - 2.0 * d));
}

// ---------------------------------------------------------------------------
// M1.4.1：Schwarzschild 测地线积分骨架。
// 每像素一条光线，从远处相机 (+z) 出发，按 Binet 形式的光子加速度
//   a = -(3/2) h² x / r⁵
// 用 leapfrog(kick-drift-kick) 积分。落入视界 → 阴影；逃出去 → 背景。
// 本步只做「落入 / 逃逸」判定，背景仍按原位置取样；
// 反投影（爱因斯坦环 / 镜像二次像）在 M1.4.2，薄盘穿越在 M1.4.3。
// ---------------------------------------------------------------------------
/// <summary>
/// M1.4.1：Schwarzschild（不自旋）测地线积分。
/// 每像素一条光线，从远处相机 (+z) 出发，按 Binet 形式的光子加速度
///   a = -(3/2) h² x / r⁵
/// 用 leapfrog(kick-drift-kick) 积分。落入视界 → 阴影；逃出去 → 背景。
/// M1.4.2 起输出终态供反投影使用；M1.4.3 起在积分中累积薄盘穿越。
/// </summary>
bool TraceSchwarzschild(
    inout float3 x,
    inout float3 v,
    float h2,
    float z0,
    int steps,
    DiskGeom d,
    out bool escaped,
    inout float3 emit,
    inout float trans,
    inout float turn)
{
    escaped = false;

    float sPrev = dot(x, d.n);
    float3 xPrev = x;

    for (int i = 0; i < steps; i++)
    {
        float3 vPrev = v;

        float r2 = dot(x, x);

        if (r2 < 1.0) { break; }                                // 穿过视界 → 落入
        if (x.z < -z0 && v.z < 0.0) { escaped = true; break; }  // 从背面逃出
        if (r2 > 4.0 * z0 * z0) { escaped = true; break; }      // 被甩到很远

        float r = sqrt(r2);
        float dt = clamp(0.16 * r, 0.03, 1.5);                  // 步长随半径：光子球附近更细

        float3 acc = -1.5 * h2 * x / (r2 * r2 * r);
        v += acc * (0.5 * dt);
        x += v * dt;

        r2 = dot(x, x);
        r = sqrt(r2);
        acc = -1.5 * h2 * x / (r2 * r2 * r);
        v += acc * (0.5 * dt);

        // fixes-v2 问题 2：累计转角 —— 每绕黑洞多转一圈，就会多复制一份背景内容。
        // 高阶像必须衰减，否则同一块桌面会在阴影周围被反复复制成「一圈圈亮环」。
        turn += acos(clamp(dot(normalize(vPrev), normalize(v)), -1.0, 1.0));

        // 薄盘穿越：一条光线可能穿过盘面多次（远侧盘面会从阴影上方/下方绕过去）
        float s = dot(x, d.n);
        if (d.withDisk && trans > 0.02 && s * sPrev < 0.0)
        {
            float tc = sPrev / (sPrev - s);
            float3 xc = lerp(xPrev, x, tc);
            float4 sh = ShadeCrossing(xc, normalize(v), d.n, d.e2, d.rin, d.rout, d.t, d.sdir, d.spd, trans);
            emit += sh.rgb;
            trans *= (1.0 - clamp(d.opac * sh.a, 0.0, 1.0));
        }

        sPrev = s;
        xPrev = x;
    }

    // 步数用尽仍在缠绕 → 视同落入（参考实现的处理）
    return !escaped && dot(x, x) < 4.0;
}

/// <summary>
/// 测地线积分的统一入口。M1.6：spin &lt; 0.005 走 Schwarzschild 快速路径，
/// 否则走 Kerr（精确自旋度量，代价高得多）。
/// </summary>
/// <param name="withDisk">true 时在积分中累积薄盘穿越（M1.4.3）。</param>
bool TraceGeodesic(
    float2 pr,
    float z0,
    int steps,
    bool withDisk,
    out bool escaped,
    out float3 outX,
    out float3 outV,
    out float3 emit,
    out float trans,
    out float turn)
{
    float3 x = float3(pr, z0);
    float3 v = float3(0.0, 0.0, -1.0);
    float h2 = dot(pr, pr);

    escaped = false;
    emit = float3(0.0, 0.0, 0.0);
    trans = 1.0;
    turn = 0.0;

    // 盘几何：法线绕屏幕 x 轴倾斜 incl，(x̂, e2, n) 构成盘面正交基
    float incl = Look0.y;
    float speed = Look3.x;

    DiskGeom d;
    d.n = float3(0.0, sin(incl), cos(incl));
    d.e2 = float3(0.0, cos(incl), -sin(incl));
    d.rin = Look1.x;
    d.rout = Look1.y;
    d.opac = Look1.z;
    d.sdir = speed < 0.0 ? -1.0 : 1.0;
    d.spd = abs(speed);
    d.t = Timing.x;
    d.withDisk = withDisk;

    float spin = Look3.w;
    bool captured;

    if (spin < 0.005)
    {
        captured = TraceSchwarzschild(x, v, h2, z0, steps, d, escaped, emit, trans, turn);
    }
    else
    {
        captured = TraceKerr(x, v, z0, steps, d, spin, escaped, emit, trans, turn);
    }

    outX = x;
    outV = v;
    return captured;
}

/// <summary>
/// M1.4.2：把逃逸光线的出射方向反投影到桌面「天空平面」z = -LENS_DEPTH，
/// 得到它究竟来自画面的哪一处。爱因斯坦环与镜像二次像都由这一步产生。
/// </summary>
/// <remarks>
/// M1.5：改为返回**屏幕 p 空间**（黑洞中心为原点、y 以屏高为单位）的源位置，
/// 而不是最终的 uv —— 因为远场淡出必须作用在「位移」上（与参考实现一致），
/// 直接淡出颜色会在解析远场与积分区的交界圆上留下接缝。
/// </remarks>
float2 ReprojectToSky(float3 x, float3 v, float2 p, float roll, float worldScale, out float toward)
{
    float3 d = normalize(v);
    toward = 0.0;

    // 弯折过度（几乎侧向飞出）的光线到不了天空平面，回退到像素自身位置并淡出
    if (d.z >= -0.05)
    {
        return p;
    }

    float tpl = (-LENS_DEPTH - x.z) / d.z;
    float3 hp = x + d * tpl;
    float2 q = rot(hp.xy, -roll) / worldScale;
    float2 sp = float2(q.x, -q.y);

    toward = smoothstep(0.05, 0.35, -d.z);
    return sp;
}

// ===========================================================================
// M1.5：远场解析弱偏折
//
// b >= bmax 的光线不可能碰到吸积盘（bmax = rout + 3），不必再花 48~88 步积分。
// 这里用一条按有限相机距离拟合的解析映射，与积分区在交界圆上的位移差 < 1%，
// 肉眼看不到接缝；同时它覆盖的面积远大于积分区，是主要的性能来源。
//
// 无缝的关键（参考实现的注释原话）：fade the *displacement*, never the color。
// 两侧都在位移上乘同一个 window，交界圆上位移连续，因此颜色也连续。
// ===========================================================================

/// <summary>一个像素的背景取样结果：近场（积分）与远场（解析）共用的返回结构。</summary>
struct LensSample
{
    float2 uvR;      // 红通道取样 uv（色差：红弯得少一点）
    float2 uvG;      // 绿通道取样 uv（无色差，作为基准）
    float2 uvB;      // 蓝通道取样 uv（蓝弯得多一点）
    float toward;    // 近场：弯折过度的淡出；远场恒为 1
    float3 emit;     // 薄盘累积发光（HDR，尚未 tonemap）
    float3 star;     // M1.7：透镜星场（已乘 star 增益与远场淡出窗口）
    float3 nebula;   // fixes-v2 问题 1：程序化背景光（已乘星云强度与远场淡出窗口）
    float turn;      // fixes-v2 问题 2：光线累计转角（弧度）
    float bgGain;    // fixes-v2 问题 2：高阶像衰减（只作用于背景，不影响取样位置）
    float trans;     // 到背景的透射率（被盘遮挡后剩余）
    float window;    // 远场淡出窗口 exp(-(plen/7)^2)：星云 / 星空用
    float windowDisp;// fixes-v3 问题 1：**位移**专用淡出窗口，必须在 alpha 羽化之前就归零
    float plen;      // 到黑洞的距离，以**阴影半径**为单位
    bool captured;   // 落入视界
    bool far;        // 走了远场解析分支
};

/// <summary>
/// 解析远场的**天球方向**：按弱偏折角 α = 2/b 反推弯折后的方向（不做积分）。
///
/// fixes-v3 问题 6：这个方向同时被近场用到（见 `ResolveLens` 里的 blend）——
/// 交界圆两侧原本各算各的（近场用积分终态 `finalV`、远场用这里的解析式），
/// 而星云 / 星空是**按方向索引**的 ⇒ 那一圈上会取到两片互不相干的值，
/// 表现为一条跟着黑洞走的硬缝。抽成同一个函数，两侧才有得可 blend。
/// </summary>
float3 FarSkyDirection(float2 pr, float b)
{
    float binv = 1.0 / max(b, 1e-4);
    return normalize(float3(-pr * (2.0 * binv * binv), -1.0));
}

/// 解析该像素的背景取样位置：b ≥ bmax 走解析远场，否则走测地线积分。
/// </summary>
LensSample ResolveLens(float2 uv, float2 center, float aspect)
{
    float rh = max(Hole.x, 1e-4);            // 阴影半径（视口高度单位）
    float worldScale = max(Hole.y, 1e-4);    // W = B_CRIT / rh
    float roll = Look0.z;
    float rout = Look1.y;
    float z0 = max(14.0, rout + 5.0);

    float2 p = (uv - center) * float2(aspect, 1.0);
    float plenPx = length(p);                // 屏幕单位（屏高 = 1）
    float plen = plenPx / rh;                // 以阴影半径为单位

    float w = plen / 7.0;
    float window = exp(-w * w);

    // fixes-v3 问题 1：位移必须有**自己**的淡出窗口，且要比 alpha 的羽化**先归零**。
    //
    // 原来位移和星云共用 `window`（7 rs 衰减），而 alpha 的羽化区间是
    // [0.70·fadeR, fadeR]（全屏下 fadeR = 屏幕半高 ≈ 8.33 rs，即 5.83 ~ 8.33 rs）。
    // 于是羽化刚开始（5.83 rs）时位移还有约 50%，到屏幕边缘也还有约 24% ——
    // 半透明的「偏移画面」叠在真实桌面上，就是 `bugs/边缘bug.jpg` 里那种重影模糊。
    //
    // 这里让位移在 0.45 ~ 0.68 个 fadeR 之间平滑归零（早于羽化起点 0.70·fadeR），
    // 于是进入半透明区时画面已经没有偏移，重影消失。
    //
    // ⚠️ 近场与远场**必须共用同一个** `windowDisp`：交界圆上两条分支的位移各乘一份，
    // 只有两者是同一个数，交界圆才仍然无缝（否则会冒出一圈新的接缝）。
    // 星云 / 星空仍用原来的 `window`（问题 3 已决定保留星云观感）。
    float fadeR = 0.5 / max(Hole.x, 1e-4);
    float windowDisp = 1.0 - smoothstep(fadeR * 0.45, fadeR * 0.68, plen);

    float2 pr = rot(float2(p.x, -p.y), roll) * worldScale;
    float b = length(pr);                    // 撞击参数，单位 r_s
    float bmax = rout + 3.0;                 // 超过它就不可能碰到盘

    LensSample s;
    s.uvR = uv;
    s.uvG = uv;
    s.uvB = uv;
    s.toward = 1.0;
    s.emit = float3(0.0, 0.0, 0.0);
    s.star = float3(0.0, 0.0, 0.0);
    s.nebula = float3(0.0, 0.0, 0.0);
    s.turn = 0.0;
    s.bgGain = 1.0;
    s.trans = 1.0;
    s.window = window;
    s.windowDisp = windowDisp;
    s.plen = plen;
    s.captured = false;
    s.far = b >= bmax;

    if (s.far)
    {
        // ---- 远场：解析弱偏折（有限相机距离拟合式）----
        float uu = z0 * rsqrt(z0 * z0 + b * b);
        // fixes-v3 问题 1：偏折量也乘 `windowDisp`（不是 `window`）⇒ 位移先于 alpha 归零
        float defl = (2.0 / (worldScale * worldScale)) / max(plenPx, 1e-4)
                   * (1.29 * uu + 0.07)
                   * max(LENS_DEPTH - 2.14 * uu + 0.75, 0.0)
                   * windowDisp;
        float2 dir = p / max(plenPx, 1e-5);

        // 轻微色差：蓝比红弯得多一点。在交界圆上 smoothstep = 0，不破坏无缝。
        float ab = 0.035 * smoothstep(1.0, 2.0, b / max(bmax, 1e-4));

        // M1.6：帧拖拽漩涡 —— 背景取样绕黑洞旋转，越靠近环越强，方向与盘一致
        float tw = DragTwist(b, Look3.w * (Look3.x < 0.0 ? -1.0 : 1.0));

        float2 spR = rot(p - dir * defl * (1.0 - ab), tw);
        float2 spG = rot(p - dir * defl, tw);
        float2 spB = rot(p - dir * defl * (1.0 + ab), tw);

        s.uvR = center + spR / float2(aspect, 1.0);
        s.uvG = center + spG / float2(aspect, 1.0);
        s.uvB = center + spB / float2(aspect, 1.0);

        // M1.7：透镜星场。远场不积分，直接用解析弱偏折角 α = 2/b 反推弯折后的方向。
        float3 dirFar = FarSkyDirection(pr, b);
        s.star = stars(dirFar) * Look3.z * window;

        // fixes-v2 问题 1：远场同样用解析弱偏折角反推方向，省一次积分
        s.nebula = nebula(dirFar) * Hole.w * window;
        return s;
    }

    // ---- 近场：测地线积分 + 薄盘多次穿越 ----
    // 用局部变量接收 out 参数再回填结构，避免把结构成员直接当 out 实参。
    bool escaped;
    float3 finalX;
    float3 finalV;
    float3 emit;
    float trans;
    float turn;
    bool captured = TraceGeodesic(pr, z0, (int)Hole.z, true, escaped, finalX, finalV, emit, trans, turn);

    s.captured = captured;
    s.emit = emit;
    s.trans = trans;
    s.turn = turn;

    // fixes-v2 问题 2：高阶像衰减。
    // 一条光线绕着黑洞多转一圈，就会把同一块背景内容再复制一份到另一个半径上 ——
    // 照片里的「很多圈亮环」就是这么来的（bugs/1.jpg、2.jpg 里连界面文字都被复制了多次）。
    // 真实的引力透镜里，高阶像本来就应该暗得多，这里把它按转角指数压下去：
    // 只衰减**亮度**、不动取样位置 —— 这样 F1-2 的「镜像二次像」依旧存在，只是更暗。
    s.bgGain = exp(-max(turn - HIGH_ORDER_FREE_TURN, 0.0) / HIGH_ORDER_DECAY_TURN);

    if (!captured)
    {
        float toward;
        float2 sp = ReprojectToSky(finalX, finalV, p, roll, worldScale, toward);
        s.toward = toward;

        // M1.6：帧拖拽漩涡（spin=0 时 DragTwist 恒为 0，是严格的空操作）
        sp = rot(sp, DragTwist(b, Look3.w * (Look3.x < 0.0 ? -1.0 : 1.0)));

        // 淡出的是位移，不是颜色：从「真实源位置」平滑回到「像素自身位置」。
        // fixes-v3 问题 1：这里同样改用 `windowDisp`（与远场分支同一个数，交界圆才无缝）
        float2 spw = p + (sp - p) * windowDisp;
        float2 suv = center + spw / float2(aspect, 1.0);

        s.uvR = suv;
        s.uvG = suv;
        s.uvB = suv;

        // M1.7：透镜星场按**弯折后**的光线方向索引（近场用积分终态）
        float3 dirNear = normalize(finalV);

        // fixes-v3 问题 6：交界圆（b = bmax）两侧的**方向**是两套算法
        // （近场 = 积分终态，远场 = 解析弱偏折反推）。位移当年已经校准到连续，
        // 但**方向**没管 —— 星云 / 星空按方向索引，于是那一圈上取到两片不相干的值
        // ⇒ 一条跟着黑洞走的硬缝（实测：亮度在 r ≈ 3.84 rs 处一步掉 13/255）。
        //
        // 这里在 b 接近 bmax 时把方向平滑地让给解析式 ⇒ 交界处两侧接得上。
        // 只影响 b ∈ [0.75·bmax, bmax] 这一圈（远场解析式在那一带本来就够准），
        // 光子环附近（b 小）仍然是积分终态，观感不变。
        float blend = smoothstep(bmax * 0.75, bmax, b);
        float3 dirSky = normalize(lerp(dirNear, FarSkyDirection(pr, b), blend));

        s.star = stars(dirSky) * Look3.z * window;

        // fixes-v2 问题 1：星云走同一套方向索引
        s.nebula = nebula(dirSky) * Hole.w * window;
    }

    return s;
}

/// <summary>
/// M1.5 验证入口：输出该像素的**背景取样位移**，供测试量化透镜强度。
/// rg = 绿通道（无色差基准），ba = 蓝通道（色差最强）。
///
/// 为什么不直接输出 uv：uv 存进 8 位 UNORM 会变成 round(255·uv)/255，
/// 相当于把坐标整体按 256/255 缩放 —— 测出的位移会带上一个 r/255 px 的**外向系统偏差**
/// （r = 到中心的距离），远处真实位移接近 0 时，这个偏差比信号还大。
/// 改成输出「取样位置 − 像素自身位置」并放大居中后，8 位分辨率全用在位移本身，
/// 只剩 1/255 的**相对**误差。
/// </summary>
// ---------------------------------------------------------------------------
// fixes-v2 问题 2 的验证入口：把「光线累计转角」与「高阶像衰减」直接画出来。
//   r = turn / 2π（绕了几圈）
//   g = bgGain（高阶像衰减系数）
//   b = 是否落入视界
// 有了它，标定 HIGH_ORDER_FREE_TURN / HIGH_ORDER_DECAY_TURN 才有依据，
// 也能把「只有一圈主环」这件事变成可回归的断言（而不是靠肉眼看照片）。
// ---------------------------------------------------------------------------
float4 OrderProbePS(VSOut input) : SV_Target
{
    float aspect = Viewport.x / max(Viewport.y, 1.0);
    float2 center = float2(CenterRadius.x / max(Viewport.x, 1.0),
                           CenterRadius.y / max(Viewport.y, 1.0));

    LensSample s = ResolveLens(input.uv, center, aspect);

    return float4(s.turn / 6.2831853, s.bgGain, s.captured ? 1.0 : 0.0, 1.0);
}

float4 FarFieldProbePS(VSOut input) : SV_Target
{
    float aspect = Viewport.x / max(Viewport.y, 1.0);
    float2 center = float2(CenterRadius.x / max(Viewport.x, 1.0),
                           CenterRadius.y / max(Viewport.y, 1.0));

    LensSample s = ResolveLens(input.uv, center, aspect);

    float gain = max(Debug.y, 0.01);   // 位移放大倍数（测试通过 ShaderProbe.NoiseScale 传入）
    float2 dG = (s.uvG - input.uv) * gain + 0.5;
    float2 dB = (s.uvB - input.uv) * gain + 0.5;

    return float4(dG, dB);
}

// ---------------------------------------------------------------------------
// fixes-v3 问题 1 的验证入口：把「位移淡出」与「alpha 羽化」两条曲线同时画出来。
//   r = windowDisp（位移专用窗口：1 = 位移完整，0 = 位移已归零）
//   g = alpha 的羽化系数 1 − smoothstep(0.70·fadeR, fadeR, plen)（1 = 不透明，0 = 透明）
//   b = |uvG − uv| × gain + 0.5（位移幅度读数，量程做法见 FarFieldProbePS 的说明）
//
// 重影的判据正是「g < 1（半透明）而位移 ≠ 0（画面偏移）」的环带。
// 有它就能把问题 1 变成可回归的断言，而不是靠肉眼看照片。
// ---------------------------------------------------------------------------
float4 LensFadeProbePS(VSOut input) : SV_Target
{
    float aspect = Viewport.x / max(Viewport.y, 1.0);
    float2 center = float2(CenterRadius.x / max(Viewport.x, 1.0),
                           CenterRadius.y / max(Viewport.y, 1.0));

    LensSample s = ResolveLens(input.uv, center, aspect);

    // 与 composite.hlsl 的收尾羽化**同一条公式**，两边必须一起改
    float fadeR = 0.5 / max(Hole.x, 1e-4);
    float feather = 1.0 - smoothstep(fadeR * 0.70, fadeR, s.plen);

    float gain = max(Debug.y, 0.01);
    float magnitude = length(s.uvG - input.uv) * gain + 0.5;

    return float4(s.windowDisp, feather, magnitude, 1.0);
}

// 验证入口：落入 → 纯黑；逃逸 → 原样取背景
float4 GeodesicProbePS(VSOut input) : SV_Target
{
    float aspect = Viewport.x / max(Viewport.y, 1.0);
    float2 center = float2(CenterRadius.x / max(Viewport.x, 1.0),
                           CenterRadius.y / max(Viewport.y, 1.0));

    float rh = Hole.x;        // 阴影半径（屏高单位）
    float worldScale = Hole.y; // W = B_CRIT / rh
    float roll = Look0.z;
    float rout = Look1.y;
    float z0 = max(14.0, rout + 5.0);

    float2 p = (input.uv - center) * float2(aspect, 1.0);
    float2 pr = rot(float2(p.x, -p.y), roll) * worldScale;

    bool escaped;
    float3 finalX;
    float3 finalV;
    float3 emit;
    float trans;
    float turn;
    bool captured = TraceGeodesic(pr, z0, (int)Hole.z, false, escaped, finalX, finalV, emit, trans, turn);

    if (captured)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    // Debug.x < 0 → 关闭反投影（供测试做「有/无反投影」对照）
    if (Debug.x < 0.0)
    {
        return float4(background(input.uv), 1.0);
    }

    float toward;
    float2 sp = ReprojectToSky(finalX, finalV, p, roll, worldScale, toward);

    return float4(background(center + sp / float2(aspect, 1.0)) * toward, 1.0);
}

// ---------------------------------------------------------------------------
// M1.4.3：完整版验证入口 —— 测地线 + 薄盘多次穿越 + 背景反投影。
// 输出 col = 背景 × 透射率 + 盘的累积发光（tonemap 在 M1.4.4）。
// ---------------------------------------------------------------------------
float4 GeodesicDiskProbePS(VSOut input) : SV_Target
{
    float aspect = Viewport.x / max(Viewport.y, 1.0);
    float2 center = float2(CenterRadius.x / max(Viewport.x, 1.0),
                           CenterRadius.y / max(Viewport.y, 1.0));

    float rh = Hole.x;
    float worldScale = Hole.y;
    float roll = Look0.z;
    float rout = Look1.y;
    float z0 = max(14.0, rout + 5.0);

    float2 p = (input.uv - center) * float2(aspect, 1.0);
    float2 pr = rot(float2(p.x, -p.y), roll) * worldScale;

    bool escaped;
    float3 finalX;
    float3 finalV;
    float3 emit;
    float trans;
    float turn;
    bool captured = TraceGeodesic(pr, z0, (int)Hole.z, true, escaped, finalX, finalV, emit, trans, turn);

    float3 background3 = float3(0.0, 0.0, 0.0);
    if (!captured)
    {
        float toward;
        float2 sp = ReprojectToSky(finalX, finalV, p, roll, worldScale, toward);
        background3 = background(center + sp / float2(aspect, 1.0)) * toward;
    }

    return float4(background3 * trans + emit, 1.0);
}
