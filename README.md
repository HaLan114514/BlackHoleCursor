# BlackHoleCursor — 黑洞光标

一个 Windows 11 / Windows 10 桌面特效：在鼠标位置实时渲染一颗**引力透镜黑洞**，把其身后的真实桌面像星际穿越里的 "Gargantua" 那样弯曲、扭曲。

它把一个全屏透明的 DirectComposition 覆盖层贴在桌面上，通过 Windows.Graphics.Capture (WGC) 实时复制桌面，再用 Direct3D11 逐像素做**史瓦西 / 克尔测地线光线追踪**，把吸积盘、爱因斯坦环、镜像二次像、事件视界阴影、背后的星云与星空全部合成出来。整屏由覆盖层自绘以消除抓屏带来的一帧延迟差。

> 警告：本项目为个人兴趣 / 学习性质，使用大量低层 Windows 图形 API（DXGI / DirectComposition / WinRT WGC / GDI）。仅在桌面会话下工作，受 DRM 保护的画面（全黑）会自动回退。

> [!IMPORTANT]
> **来源声明 / Credits**
> 本项目的创意、视觉方向、着色器算法与部分实现**参考并移植自**开源项目
> **[GreenScreen410/singularity](https://github.com/GreenScreen410/singularity)**（MIT License）。
> 原项目的 Windows x64 构建与许可证保存在 [`copy/`](copy/)，完整致谢见
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。
> 本项目并非原创，代码在 AI 辅助下完成；在此**特别感谢原作者 GreenScreen410**。

## 特性

- **真实光线追踪**：逐像素沿 Binet 形式的光子测地线做 leapfrog 积分，可选 Kerr（自旋）精确度量。
- **吸积盘**：薄盘多次穿越着色，含多普勒频移 / 集束效应与角向 wrapping 噪声条纹。
- **引力透镜**：解析弱偏折远场 + 近场积分无缝拼接；爱因斯坦环、高阶镜像像按转角衰减；轻微色差。
- **帧拖拽**：可选自旋下的漩涡拖拽。
- **程序化天空**：可被透镜弯折的星云与星空，随鼠标视差、缓慢流动与呼吸。
- **全屏预乘 alpha 覆盖层**：DirectComposition + 翻转模型交换链，保证桌面内容年龄一致。
- **稳定抓屏**：WGC 为主 + GDI BitBlt 后台线程兜底；自适应节流；受保护内容（全黑帧）自动降级。
- **跟随手感**：惯性跟随、静止游荡、出现 / 消失动画、预设交叉淡入。
- **截图保护**：可选在截图软件（微信 / Snipaste 等）工作时临时收起黑洞，避免被抓走。
- **单实例 + 托盘 UI**：WPF 主界面带实时预览，托盘图标常驻。

## 下载 / 直接运行

免安装版（Windows x64，自包含单文件，无需安装 .NET 运行时）：

- [**Releases → BlackHoleCursor v1.0.0**](https://github.com/HaLan114514/BlackHoleCursor/releases/latest)
- 下载 `BlackHoleCursor-win-x64.zip`（约 69 MB），解压后双击 `BlackHoleCursor.exe`。

程序未做代码签名，首次启动若被 SmartScreen 拦截，选择“更多信息 → 仍要运行”即可。

## 技术栈

| 层 | 技术 |
| --- | --- |
| 语言 / 运行时 | C# 12 / .NET 8（`net8.0-windows10.0.19041.0`, x64） |
| UI | WPF + WinForms（托盘 `Hardcodet.NotifyIcon.Wpf`，MVVM `CommunityToolkit.Mvvm`） |
| 渲染 | Direct3D 11 + DirectComposition + DXGI（`Vortice.*` 3.8.3），HLSL 蓝色空间嵌入式着色器 |
| 抓屏 | Windows.Graphics.Capture (WinRT) 为主，GDI `BitBlt` 后台线程兜底 |
| 测试 | xUnit + Microsoft.NET.Test.Sdk，含对着色器行为的探针（probe）断言 |

## 环境要求

- Windows 10 2004 (build 19041) 或更高（WGC 需要）。
- 已安装 [.NET 8 SDK](https://dotnet.microsoft.com/download)。
- x64 架构；一张支持 DirectX 11 的显卡（无独显时自动回退 WARP 软件渲染，速度很慢）。

## 目录结构

```
.
├── BlackHoleCursor.sln        # 解决方案（Core / WPF / Tests）
├── src/
│   ├── BlackHoleCursor.Core/  # 核心库：渲染、抓屏、跟随物理、设置、着色器
│   │   ├── Rendering/         #   OverlayController、SceneRenderer、交换链、合成
│   │   ├── Capture/           #   WGC / GDI 抓屏协调、后台线程、全黑帧检测
│   │   ├── Screenshots/       #   截图保护
│   │   ├── Shaders/           #   HLSL：common / disk / kerr / lens / composite
│   │   ├── CursorManager.cs   #   系统光标显隐
│   │   ├── FollowPhysics.cs   #   惯性跟随
│   │   └── SettingsModel.cs   #   设置模型与预设
│   └── BlackHoleCursor/       # WPF 宿主：托盘、主界面、实时预览
│       ├── UI/                #   主界面 (XAML + ViewModel)
│       └── Resources/         #   应用图标
├── tests/BlackHoleCursor.Tests/  # 单元 + 着色器探针测试
├── tools/                     # 辅助工具（不参与主程序编译）
│   ├── ApiProbe/              #   探测系统图形 API 行为
│   └── IconGen/               #   生成应用图标
├── installer/BlackHoleCursor.iss  # Inno Setup 安装脚本
├── copy/                      # 原项目 singularity 的发布包与许可证（来源对照 / 致谢）
├── dist/                      # 可执行文件说明（exe 实际放在 Releases）
├── THIRD_PARTY_NOTICES.md     # 第三方来源与 MIT 许可证全文
└── docs/                      # 设计文档（PRD / 实现计划 / 迭代修复记录）
```

## 构建

```bash
# 还原并构建
dotnet build BlackHoleCursor.sln -c Release

# 运行测试
dotnet test -c Release
```

## 运行 / 发布

开发时直接跑 WPF 宿主：

```bash
dotnet run --project src/BlackHoleCursor -c Release
```

发布为自包含单文件（win-x64，目标机无需安装 .NET 运行时）：

```bash
dotnet publish src/BlackHoleCursor -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist
# 产物：dist/BlackHoleCursor.exe
```

启动后黑洞出现在光标处。右键托盘图标打开主界面，可调黑洞大小 / 吸积盘 / 星云 / 透镜开关 / 截图保护等，改动会在主界面预览里实时呈现。

## 测试

测试覆盖跟随物理、抓屏节流与降级、设置迁移、以及大量**着色器行为探针**（把透镜位移、高阶像衰减、星云接缝等直接量化成可回归断言）。

```bash
dotnet test
```

## 相关文档

迭代设计记录见 [`docs/`](docs/)（`prd-*` 产品需求、`plan-*` 实现计划、`fixes-*` 问题修复记录）。

## 特别感谢 / Special Thanks

**特别感谢 [GreenScreen410](https://github.com/GreenScreen410)** ——
[singularity](https://github.com/GreenScreen410/singularity) 的作者。

本项目的创意、视觉目标、引力透镜 / 吸积盘渲染思路与部分着色器算法都来自
这个项目；没有它，就不会有 BlackHoleCursor。原项目采用 MIT 许可证，
其版权声明与许可证全文保留在 [`copy/LICENSE`](copy/LICENSE) 和
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) 中。

如果喜欢这个效果，请也去给原项目点一个 Star：
<https://github.com/GreenScreen410/singularity>

## 许可

本项目采用 MIT License，见 [LICENSE](LICENSE)。

本项目参考 / 移植了 [singularity](https://github.com/GreenScreen410/singularity)
的部分实现，原项目同样采用 MIT License；其版权声明与许可证全文保留在
[`copy/LICENSE`](copy/LICENSE) 与 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。
