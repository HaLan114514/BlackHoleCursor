// ---------------------------------------------------------------------------
// v2 主流程：M1.4 测地线积分 + 薄盘多次穿越 + 背景反投影 + tonemap，
//            M1.5 远场改为解析弱偏折（交界圆无缝）并加入轻微色差，
//            M1.6 Kerr 自旋，M1.7 透镜星场。
// 取代 v1 的「位移近似 + 手绘椭圆盘 + 高斯光子环」（已在 M1.8.2 删除）。
// 输出仍是预乘 alpha，可直接交给 DirectComposition 的预乘交换链。
// ---------------------------------------------------------------------------

// fixes-v2 问题 5：星云亮度 → alpha 的增益。星云本身是偏暗的环境光（亮度多在 0.1~0.4），
// 直接拿亮度当 alpha 会让它在桌面上只剩一层几乎看不见的薄膜；乘这个增益后，
// 星云浓的地方能真正盖住桌面（作为「黑洞背后的天」），稀的地方仍然透出桌面。
static const float NEBULA_ALPHA_GAIN = 2.5;

float4 CompositePS(VSOut input) : SV_Target
{
    float aspect = Viewport.x / max(Viewport.y, 1.0);
    float2 center = float2(CenterRadius.x / max(Viewport.x, 1.0),
                           CenterRadius.y / max(Viewport.y, 1.0));

    // M1.5：背景取样统一走 ResolveLens —— 近场积分、远场解析，交界圆上位移连续。
    LensSample s = ResolveLens(input.uv, center, aspect);
    float expo = Look3.y;

    float3 bg = float3(0.0, 0.0, 0.0);

    // fixes-v2 问题 5：折射层**确实画了内容**的覆盖率（0~1）。
    // alpha 只由它决定 —— 有内容就把未折射的桌面完全压在下面，没内容才让桌面透出来。
    float bgCoverage = 0.0;

    if (!s.captured && Timing.w > 0.5)
    {
        // 色差只在远场分支生效（红/绿/蓝取样位置不同）；近场三个位置相同，
        // 这里分开取是为了让同一个函数同时服务两种分支，近场分支只做一次取样。
        if (s.far)
        {
            bg = float3(background(s.uvR).r, background(s.uvG).g, background(s.uvB).b);
        }
        else
        {
            bg = background(s.uvG);
        }

        // fixes-v2 问题 5：有没有内容取决于两件事 ——
        // 1) 取样点落在抓屏区域内（越界 → 淡出，等于没内容）；
        // 2) 光线没被弯折过度（过度弯折时取样点会退回像素自身位置，
        //    那样画出来的就是**没折射过的**桌面 —— 正是要被压在下面的那一层，不能算内容）。
        bgCoverage = backgroundCoverage(s.uvG) * s.toward;

        // fixes-v2 问题 2：高阶像按转角衰减（只压亮度，不动取样位置）
        bg *= s.toward * s.bgGain;

        // M1.7：透镜星场。落进阴影的光线不产生星点（与参考实现一致），
        // 星场与背景一起被盘的透射率衰减 —— 盘前面的星星会被盘挡住。
        bg += s.star;
    }

    // fixes-v2 问题 1：程序化背景光（星云 / 辉光）—— **常开**，且刻意放在
    // `Timing.w` 门之外：它是黑洞自带的光，不该被「是否抓屏」/「抓屏判全黑」掐掉，
    // 否则在最需要它的暗背景下反而失效。
    // 三条约束：不乘 s.toward（那是给真实桌面过度弯折做淡出的）；
    // 乘 s.trans（盘前面的星云要被盘挡住）；落入视界时 ResolveLens 里就是 0（阴影保持纯黑）。
    float3 ambient = s.nebula;

    // HDR → tonemap：盘光经常远超 1，直接相加会糊成一团白
    float3 col = (bg + ambient) * s.trans + (1.0 - exp(-s.emit * expo));

    // fixes-v2 问题 4 + 问题 5：折射层的**覆盖范围**必须与它的**内容**同生共死。
    // 关掉透镜（或抓屏被判全黑）时 bg 为 0，若还拿 s.window 去占 alpha，
    // 覆盖层就会用一个「没有内容的窗口」把桌面涂黑 —— 阴影外围那**一大圈黑色**就是这么来的。
    //
    // 而开了透镜的地方必须**完全盖住**未折射的桌面（问题 5）：
    // 只要这里确实画了折射内容，alpha 就是 1，桌面按 (1−alpha)=0 完全被压在下面，
    // 不会再出现「折射过的 + 没折射过的」两层重影。
    float bgAlpha = bgCoverage;

    // fixes-v2 问题 5：星云（问题 1 那层程序化背景光）同样是我们画出来的内容，
    // 也要参与占 alpha —— 否则折射层取不到内容的那一圈（取样点跑到抓屏区外）
    // alpha 会掉到 0，既露出桌面、又把星云自己抹掉了，在阴影外侧留一圈凹陷。
    // 按星云自身亮度加权：亮处盖住桌面，暗处仍让桌面透出来（而不是整片糊住）。
    float ambientAlpha = saturate(dot(ambient, float3(0.299, 0.587, 0.114)) * NEBULA_ALPHA_GAIN);

    // 透镜开着、星云也启用时，星云就是「黑洞背后的天」：
    // 折射层取不到桌面内容的那一圈由它顶上，alpha 拉满 —— 桌面不会从那圈里透出来。
    float nebulaEnabled = saturate(Hole.w * 100.0);           // Hole.w = NebulaGain
    ambientAlpha = max(ambientAlpha, nebulaEnabled * (Timing.w > 0.5 ? 1.0 : 0.0));

    // 阴影属于黑洞本体（不是折射层），无论开不开透镜都不透明。
    // 以前它靠 s.window 间接变黑，于是黑的不止阴影，而是整个 7 倍半径的圆盘。
    float shadowAlpha = s.captured ? 1.0 : 0.0;

    // alpha：阴影不透明；盘遮挡处也要有 alpha，否则盘会被背景冲淡；
    // 折射层只在真的画了背景时才计入。
    float diskAlpha = saturate(1.0 - s.trans);
    float alpha = saturate(max(max(max(shadowAlpha, diskAlpha), bgAlpha), ambientAlpha));

    // 窗口边缘羽化：覆盖层是**有限的小窗口**（上限 1024），远场淡出未必能在窗口内走完，
    // 这里在最外圈再收一次尾，保证永远不会被窗口矩形裁出一条硬边。
    float fadeR = 0.5 / max(Hole.x, 1e-4);            // 窗口半径，以阴影半径为单位
    float feather = 1.0 - smoothstep(fadeR * 0.70, fadeR, s.plen);

    // ---------------------------------------------------------------------------
    // W7（抄参考实现 singularity）：**抓屏可用时整屏不透明**。
    //
    // 为什么要它（fixes-v3 问题 4）：折射区是我们用抓屏内容画的（天生老一帧），
    // 而周围若仍是**透传**的真实桌面（DWM 实时合成），两者并排就会被看出「慢半拍」。
    // 整屏都由我们画 ⇒ 全屏内容年龄一致 ⇒ 没有参照物可对比，也就看不出滞后。
    // 参考实现正是这么做的：`singularity.wgsl` 里 alpha 恒为 1，全文件没有 alpha / blend 逻辑。
    //
    // ⚠️ 兜底：抓屏不可用（受保护内容 / HDR / 锁屏 / 抓挂了）时必须退回透明，
    //    否则会整屏死黑 —— 参考实现是拿「程序化图案」顶着，我们退回透明更干净。
    // ---------------------------------------------------------------------------
    float desktopReady = saturate(Extra.z);
    alpha = max(alpha * feather, desktopReady);

    // N4 出现 / 消失：预乘 alpha 下 rgb 与 a 必须一起乘，否则淡出会留脏边
    return float4(col * alpha, alpha) * saturate(Extra.w);
}

// ---------------------------------------------------------------------------
// v1 旧流程（位移近似 + 手绘椭圆盘 + 高斯光子环）已在 M1.8.2 随
// blackhole.hlsl / SampleLensed 一并删除。
// ---------------------------------------------------------------------------
