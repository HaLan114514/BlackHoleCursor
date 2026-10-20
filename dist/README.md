# dist/ — 可直接运行的版本

`BlackHoleCursor.exe` 是 **Windows x64 自包含单文件**版本（约 180 MB），
目标机无需安装 .NET 8 运行时。

由于 GitHub 对单个 Git 文件有 **100 MB 硬限制**，这个 exe 没有直接提交进
仓库，而是压缩后放在 **Releases** 中：

- **下载页**：<https://github.com/HaLan114514/BlackHoleCursor/releases/latest>
- **文件名**：`BlackHoleCursor-win-x64.zip`（约 69 MB）
- **ZIP SHA-256**：`8a8bd9b1db9996a96005c2ff32c3d1dd28919d677bad9427709dda0db3c821fb`
- **EXE SHA-256**：`9893f4c7144178a830ff31cf541c76c76e80c385c275f27dacc3a29ac8a10433`

下载后解压，直接双击 `BlackHoleCursor.exe` 即可。程序未做代码签名，
首次运行时 Windows SmartScreen 可能提示，选择“更多信息 → 仍要运行”。

## 自行构建

```bash
dotnet publish src/BlackHoleCursor -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o dist
```
