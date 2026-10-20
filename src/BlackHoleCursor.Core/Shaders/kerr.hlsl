// ---------------------------------------------------------------------------
// M1.6：Kerr（自旋黑洞）的精确测地线。
//
// 在 Kerr-Schild 坐标下用哈密顿量积分：
//   H = 1/2(|p|² − E²) − 1/2·f·(l·p + E)²，   f = 2 M r³ / (r⁴ + a²z²)
// 取 M = 0.5，于是 r_s = 2M = 1，与 Schwarzschild 路径的单位完全一致。
// dx/dλ 有解析式；dp/dλ 直接对 H 做中心差分 —— 既不用手推 Christoffel 符号，
// 积分的又是精确度量（而不是某种弱场近似）。
//
// 与参考实现 singularity.wgsl 逐行对应；唯一的有意偏离见 TraceKerr 里 rinK 的注释。
// ---------------------------------------------------------------------------

#ifndef BHC_KERR_HLSL
#define BHC_KERR_HLSL

/// <summary>
/// 顺行 ISCO 半径（Bardeen 1972），单位 r_s。spin = a/M ∈ [0,1)。
/// spin = 0 → 3 r_s（= 6M）；spin → 1 时收缩到约 0.6 r_s ——
/// 转得快的黑洞允许吸积盘贴住阴影，这是自旋最直观的后果。
/// </summary>
float KerrIscoRs(float spin)
{
    float a = clamp(spin, 0.0, 0.999);
    float z1 = 1.0 + pow(1.0 - a * a, 1.0 / 3.0)
                   * (pow(1.0 + a, 1.0 / 3.0) + pow(1.0 - a, 1.0 / 3.0));
    float z2 = sqrt(3.0 * a * a + z1 * z1);
    float rM = 3.0 + z2 - sqrt(max((3.0 - z1) * (3.0 + z1 + 2.0 * z2), 0.0));
    return rM * 0.5;   // M 单位 → r_s 单位（r_s = 2M）
}

/// <summary>Kerr-Schild 坐标下的 Boyer-Lindquist 半径 r。</summary>
float KerrRadius(float3 pos, float a)
{
    float rho2 = dot(pos, pos);
    float bq = rho2 - a * a;
    float r2 = 0.5 * (bq + sqrt(bq * bq + 4.0 * a * a * pos.z * pos.z));
    return sqrt(max(r2, 1e-8));
}

/// <summary>
/// 哈密顿量 H（E = 1，外向 Kerr-Schild；视界外精确）。
/// 中心差分 dp/dλ 与解析 dx/dλ 必须取到同一个 lp，两处公式务必保持一致。
/// </summary>
float KerrHamiltonian(float3 pos, float3 pm, float a)
{
    float r = KerrRadius(pos, a);
    float r2 = r * r;
    float den = r2 + a * a;

    float3 l = float3((r * pos.x + a * pos.y) / den,
                      (r * pos.y - a * pos.x) / den,
                      pos.z / max(r, 1e-6));

    float f = r2 * r / (r2 * r2 + a * a * pos.z * pos.z);   // = 2 M r³ / (...)，M = 0.5
    float lp = dot(l, pm) + 1.0;

    return 0.5 * (dot(pm, pm) - 1.0) - 0.5 * f * lp * lp;
}

/// <summary>dx/dλ：解析式。</summary>
float3 KerrDxdl(float3 pos, float3 pm, float a)
{
    float r = KerrRadius(pos, a);
    float r2 = r * r;
    float den = r2 + a * a;

    float3 l = float3((r * pos.x + a * pos.y) / den,
                      (r * pos.y - a * pos.x) / den,
                      pos.z / max(r, 1e-6));

    float f = r2 * r / (r2 * r2 + a * a * pos.z * pos.z);
    float lp = dot(l, pm) + 1.0;

    return pm - f * lp * l;
}

/// <summary>dp/dλ：对 H 做中心差分（e = 2e-3），省去手推 Christoffel 符号。</summary>
float3 KerrDpdl(float3 pos, float3 pm, float a)
{
    const float e = 2e-3;

    return float3(
        KerrHamiltonian(pos - float3(e, 0.0, 0.0), pm, a) - KerrHamiltonian(pos + float3(e, 0.0, 0.0), pm, a),
        KerrHamiltonian(pos - float3(0.0, e, 0.0), pm, a) - KerrHamiltonian(pos + float3(0.0, e, 0.0), pm, a),
        KerrHamiltonian(pos - float3(0.0, 0.0, e), pm, a) - KerrHamiltonian(pos + float3(0.0, 0.0, e), pm, a))
        / (2.0 * e);
}

/// <summary>
/// 夸张化的帧拖拽漩涡。Kerr 透镜真实的方位角扭曲确实存在，但在桌面尺度上肉眼不可见，
/// 所以「可见层」把它放大：背景取样绕黑洞旋转，越靠近环越强，方向与盘一致。
/// spin = 0 时严格为空操作（返回 0），不会破坏 M1.5 刚验证过的无缝性。
/// </summary>
float DragTwist(float bRs, float spinSigned)
{
    float u = bRs / B_CRIT;
    return 1.3 * spinSigned / (1.0 + 0.8 * u * u);
}

/// <summary>
/// M1.6：Kerr 路径的测地线积分（88 步档位下与参考实现一致）。
///
/// 在以自旋轴（= 盘法线）为 +z 的**黑洞坐标系**里积分：
/// 世界 → 黑洞的旋转矩阵的三行就是 (x̂, e2, n)，盘赤道面即该坐标系的 z = 0。
/// </summary>
/// <param name="x">入：相机处起点（世界坐标）；出：终点（世界坐标）。</param>
/// <param name="v">入：初始方向；出：终点切向（世界坐标）。</param>
bool TraceKerr(
    inout float3 x,
    inout float3 v,
    float z0,
    int steps,
    DiskGeom d,
    float spin,
    out bool escaped,
    inout float3 emit,
    inout float trans,
    inout float turn)
{
    escaped = false;

    float a = 0.5 * spin * d.sdir;                       // a = M·spin，与盘同向
    float rhor = 0.5 + sqrt(max(0.25 - a * a, 0.0));      // 事件视界半径（r_s）

    // 自旋让 ISCO 收缩，盘可以更贴住阴影。
    // ⚠️ 与参考实现的有意偏离：参考写的是 max(rin·ISCO/3, ISCO)，
    // 但 ISCO(spin→0) = 3 r_s，而 Schwarzschild 路径用的是 rin（Gargantua = 2.2）——
    // 照搬会让滑块从 0 推到 0.006 时盘内径从 2.2 突跳到 3.0，肉眼可见地"跳一下"。
    // 改成 min(ISCO, rin) 后：spin→0 时连续等于 rin，spin 大时仍收缩到 ISCO。
    float isco = KerrIscoRs(spin);
    float rinK = max(d.rin * isco / 3.0, min(isco, d.rin));

    // 世界 → 黑洞坐标
    float3 xb = float3(x.x, dot(x, d.e2), dot(x, d.n));
    float3 pb = float3(v.x, dot(v, d.e2), dot(v, d.n));
    float sPrev = xb.z;
    float3 xPrev = xb;

    bool captured = false;

    for (int i = 0; i < steps; i++)
    {
        float r = KerrRadius(xb, a);
        if (r < rhor + 0.02) { captured = true; break; }                          // 落入视界
        if (r > 1.4 * z0 && dot(xb, KerrDxdl(xb, pb, a)) > 0.0) { escaped = true; break; }

        // leapfrog（哈密顿形式）：dl 随半径变化，光子轨道附近更细
        float dl = clamp(0.16 * r, 0.02, 1.1);
        float3 pbPrev = pb;
        pb += KerrDpdl(xb, pb, a) * (0.5 * dl);
        xb += KerrDxdl(xb, pb, a) * dl;
        pb += KerrDpdl(xb, pb, a) * (0.5 * dl);

        // fixes-v2 问题 2：累计转角（与 Schwarzschild 路径同一套语义）
        turn += acos(clamp(dot(normalize(pbPrev), normalize(pb)), -1.0, 1.0));

        // 薄盘穿越：黑洞坐标系里就是 z = 0
        if (d.withDisk && trans > 0.02 && xb.z * sPrev < 0.0)
        {
            float tc = sPrev / (sPrev - xb.z);
            float3 xcb = lerp(xPrev, xb, tc);

            // 交点与切向都要回到世界坐标，才能复用同一份盘着色
            float3 xc = xcb.x * float3(1.0, 0.0, 0.0) + xcb.y * d.e2 + xcb.z * d.n;
            float3 vb = KerrDxdl(xb, pb, a);
            float3 vw = vb.x * float3(1.0, 0.0, 0.0) + vb.y * d.e2 + vb.z * d.n;

            float4 sh = ShadeCrossing(xc, normalize(vw), d.n, d.e2, rinK, d.rout, d.t, d.sdir, d.spd, trans);
            emit += sh.rgb;
            trans *= (1.0 - clamp(d.opac * sh.a, 0.0, 1.0));
        }

        sPrev = xb.z;
        xPrev = xb;
    }

    // 步数耗尽仍在绕光子轨道（Kerr 逆行光子轨道可达 r = 4M = 2.0，留些余量）
    if (!captured && !escaped && KerrRadius(xb, a) < 2.4) { captured = true; }

    // 回到世界坐标，交给统一的背景反投影
    float3 vb = KerrDxdl(xb, pb, a);
    x = xb.x * float3(1.0, 0.0, 0.0) + xb.y * d.e2 + xb.z * d.n;
    v = vb.x * float3(1.0, 0.0, 0.0) + vb.y * d.e2 + vb.z * d.n;

    return captured;
}

// ---------------------------------------------------------------------------
// M1.6 验证入口：把 Kerr 的几个标量直接画出来，供测试断言。
// R = ISCO / 4（ISCO 取值 0.6~3.0），G = 视界半径 / 2，B = 盘内径 rinK / 8
// ---------------------------------------------------------------------------
float4 KerrProbePS(VSOut input) : SV_Target
{
    float spin = clamp(Debug.x, 0.0, 0.999);
    float rin = max(Debug.y, 0.0);

    float isco = KerrIscoRs(spin);
    float a = 0.5 * spin;
    float rhor = 0.5 + sqrt(max(0.25 - a * a, 0.0));
    float rinK = max(rin * isco / 3.0, min(isco, rin));

    return float4(isco / 4.0, rhor / 2.0, rinK / 8.0, 1.0);
}

#endif
