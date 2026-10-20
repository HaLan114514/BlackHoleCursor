// ---------------------------------------------------------------------------
// M1.3：倾斜薄盘的「单次穿越」着色。
//
// 一条光线可能多次穿过薄盘（远侧盘面会从阴影上方/下方绕过去），
// 每次穿越都按下列物理累积一次发光：
//   * 开普勒角速度  kep = (rin/rc)^1.5      —— 内圈转得明显更快
//   * 引力时间膨胀  gloc = sqrt(1 - 1.5/r)  —— 内圈时间流逝更慢
//   * 相对论多普勒  g = gloc/(1 + β·cos)    —— 迎面而来的一侧偏蓝且更亮
//   * 相对论集束    boost = g^beam          —— 亮度随 g 的幂次放大
//   * Shakura-Sunyaev 温度剖面 tprof        —— 决定黑体色温
// 返回值 rgb 为「已乘透射率」的发光，a 为局部密度（调用方据此衰减背景）。
// ---------------------------------------------------------------------------

float4 ShadeCrossing(
    float3 xc,      // 穿越点（世界坐标；黑洞在原点，r_s = 1）
    float3 vdir,    // 光线方向（已归一化）
    float3 n,       // 盘法线
    float3 e2,      // 盘面内与 x̂ 正交的基向量
    float rin,      // 盘内径（r_s）
    float rout,     // 盘外径（r_s）
    float t,        // 时间（秒）
    float sdir,     // 公转方向（+1 / -1）
    float spd,      // 条纹速度（取绝对值后传入）
    float trans)    // 当前透射率
{
    float rc = length(xc);
    if (rc <= rin || rc >= rout)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // 内外缘柔化，避免出现硬边
    float band = smoothstep(rin, rin * 1.25, rc)
               * (1.0 - smoothstep(rout * 0.70, rout, rc));

    // 盘面极坐标
    float phi = atan2(dot(xc, e2), xc.x);
    float turns = phi / 6.2831853;

    float kep = pow(rin / rc, 1.5);
    float gloc = sqrt(max(1.0 - 1.5 / rc, 0.02));
    float swirl = rc * Look2.w * 0.12 - t * kep * spd * gloc * sdir;

    float streaks = vnoiseWrapY(float2(rc * 2.8, turns * 19.0 + swirl * 3.0), 19.0) * 0.65
                  + vnoiseWrapY(float2(rc * 1.0, turns * 9.0 + swirl * 1.5 + 7.0), 9.0) * 0.35;
    streaks = 0.35 + Look2.z * streaks * streaks;

    // 气体切向速度方向（顺时针 / 逆时针由 sdir 决定）
    float3 gasdir = normalize(cross(n, xc)) * sdir;
    float beta = clamp(rsqrt(max(2.0 * (rc - 1.0), 0.2)), 0.0, 0.99);
    float g = gloc / max(1.0 + beta * dot(gasdir, vdir), 0.05);
    g = lerp(1.0, g, Look1.w);

    // Shakura-Sunyaev 温度剖面，峰值归一化到 1
    float xpr = max(1.0 - sqrt(rin / rc), 0.0);
    float tprof = pow(rin / rc, 0.75) * pow(xpr, 0.25) / 0.488;

    float3 cbb = blackbody(Look0.x * tprof * g);
    // 自定义染色：乘性叠加，保留黑体的亮度梯度（TintStrength = 0 时完全不影响）
    cbb *= lerp(float3(1.0, 1.0, 1.0), Tint.rgb, Look0.w);

    float boost = pow(g, Look2.x);
    float density = band * streaks;

    return float4(trans * cbb * (Look2.y * 2.2 * density * tprof * tprof * boost), density);
}

// ---- 单元验证入口：把一次合成穿越的结果画出来 ----
// uv.x → 穿越半径（rin*0.5 … rout*1.5）；uv.y → 方位角；Debug.w → 观察者方向角
float4 DiskProbePS(VSOut input) : SV_Target
{
    float incl = Look0.y;
    float3 n = float3(0.0, sin(incl), cos(incl));
    float3 e2 = float3(0.0, cos(incl), -sin(incl));

    float rin = Look1.x;
    float rout = Look1.y;

    float rc = lerp(rin * 0.5, rout * 1.5, input.uv.x);
    float phi = input.uv.y * 6.2831853;
    float3 xc = rc * (cos(phi) * float3(1.0, 0.0, 0.0) + sin(phi) * e2);

    float a = Debug.w;
    float3 vdir = normalize(float3(sin(a), 0.0, -cos(a)));

    float speed = Look3.x;
    float sdir = speed < 0.0 ? -1.0 : 1.0;
    float spd = abs(speed);

    float4 sh = ShadeCrossing(xc, vdir, n, e2, rin, rout, Timing.x, sdir, spd, 1.0);
    return float4(sh.rgb, sh.a);
}
