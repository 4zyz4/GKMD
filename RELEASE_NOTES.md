# Release notes

## v4.5.2 — 2026-10-10 — usbip-win2 0.9.8.1 support

**English** | [简体中文](#v452--2026-10-10--支持-usbip-win2-0981)

GKMD 4.5.2 upgrades the embedded USB/IP transport to usbip-win2 **0.9.8.1** and
teaches the engine to speak the 0.9.8.1 vHCI request layout, so an installed
0.9.8.1 driver is no longer misdetected as missing. It follows GKME 4.5.2.

### Highlights

- **usbip-win2 0.9.8.1 payload** — the embedded installer is upgraded from
  `USBip-0.9.8.0-x64.exe` to `USBip-0.9.8.1-x64.exe`; `UsbipDriverInstaller`
  carries the new version string and the upstream release SHA-256
  (`38cad6…2a8518a`), which is still verified after extraction.
  `THIRD-PARTY-NOTICES.txt` and both READMEs follow.
- **Multi-layout vHCI probing** — `VhciClient` now probes the installed driver
  for the ABI it actually supports across three layouts (**0.9.8.1 / 0.9.8.0 /
  0.9.7.x**). In 0.9.8.1 a new `location_hash` follows `port`, growing
  `plugin_hardware`, `stop_attach_attempts` and `imported_device` by 4 bytes
  each; `Attach`, `StopAttachAttempts` and `GetImportedDevices` are all built
  for the detected layout.
- **Fix — false "not installed"** — a hard-coded 0.9.8.0-sized
  `GET_IMPORTED_DEVICES` probe was rejected by 0.9.8.1 with an ABI error, so
  `IsCurrentAbi()` returned `false` and an installed 0.9.8.1 was treated as
  missing. The per-layout probe fixes this.
- **0.9.7.x preserved** — `UsbipReceiveMode` is retained; 0.9.7.x has no
  `wsk_events` field.
- **Git LFS** — `Resources/*.exe` is now tracked through Git LFS.

### Compatibility

- Controller personas are unchanged (Xbox One / Series X|S, Xbox 360,
  Switch Pro / Joy-Con, DualShock 4, DualSense, wheel, keyboard, mouse).
- Works against an installed usbip-win2 **0.9.7.x, 0.9.8.0 or 0.9.8.1**.

### Artifact

| File | Size | Notes |
| --- | --- | --- |
| `GKMD.dll` | 26,392,576 bytes | Release build, IL, `net10.0-windows`, AnyCPU, embedded usbip-win2 0.9.8.1 |

SHA-256: `6816340ED6574938A9A70E074CA73C21765843B50E1BCB34CB2587E8EA18F1FD`

Produced by `dotnet build -c Release`; Native AOT compilation happens in the
host application (GKME), not in this library.

### Build / verify

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # from a host project referencing GKMD.csproj
```

### Known limitations

- The bundled usbip-win2 payload is x64-only.
- Windows only (`net10.0-windows`).
- Layout (de)serialization uses reflection-based `System.Text.Json`; under a
  trimmed/AOT host the `GKLayout*` types must be preserved by the host.

### Credits & license

Derived from HIDMaestro 1.5.1 (MIT); the original copyright notice is retained
in [LICENSE](LICENSE). Bundled usbip-win2 is BSD-2-Clause, see
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt).

---

## v4.5.2 — 2026-10-10 — 支持 usbip-win2 0.9.8.1

[English](#v452--2026-10-10--usbip-win2-0981-support) | **简体中文**

GKMD 4.5.2 将内嵌的 USB/IP 传输升级到 usbip-win2 **0.9.8.1**，并让引擎按
0.9.8.1 的 vHCI 请求布局通信，已安装的 0.9.8.1 驱动不再被误判为未安装。跟随
GKME 4.5.2 发布。

### 亮点

- **usbip-win2 0.9.8.1 载荷** — 内嵌安装程序由 `USBip-0.9.8.0-x64.exe` 升级至
  `USBip-0.9.8.1-x64.exe`；`UsbipDriverInstaller` 同步新的版本串与上游 release
  SHA-256（`38cad6…2a8518a`），解压后仍会校验。`THIRD-PARTY-NOTICES.txt` 与两份
  README 一并更新。
- **多布局 vHCI 探测** — `VhciClient` 改为探测已安装驱动实际支持的 ABI，覆盖
  **0.9.8.1 / 0.9.8.0 / 0.9.7.x** 三种布局。0.9.8.1 在 `port` 之后新增
  `location_hash`，使 `plugin_hardware`、`stop_attach_attempts` 与
  `imported_device` 各增大 4 字节；`Attach`、`StopAttachAttempts` 与
  `GetImportedDevices` 均按探测结果构造。
- **修复：误报“未安装”** — 硬编码 0.9.8.0 尺寸的 `GET_IMPORTED_DEVICES` 探测被
  0.9.8.1 以 ABI 错误拒绝，导致 `IsCurrentAbi()` 返回 `false`，已安装的 0.9.8.1
  被误判为缺失；按布局探测后修复。
- **保留 0.9.7.x** — 保留 `UsbipReceiveMode`；0.9.7.x 无 `wsk_events` 字段。
- **Git LFS** — `Resources/*.exe` 改用 Git LFS 追踪。

### 兼容性

- 各手柄人格不变（Xbox One / Series X|S、Xbox 360、Switch Pro / Joy-Con、
  DualShock 4、DualSense、方向盘、键盘、鼠标）。
- 兼容已安装的 usbip-win2 **0.9.7.x、0.9.8.0、0.9.8.1**。

### 产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `GKMD.dll` | 26,392,576 字节 | Release 构建、IL、`net10.0-windows`、AnyCPU，内嵌 usbip-win2 0.9.8.1 |

SHA-256：`6816340ED6574938A9A70E074CA73C21765843B50E1BCB34CB2587E8EA18F1FD`

由 `dotnet build -c Release` 生成；Native AOT 编译在宿主应用（GKME）中完成，
而非本类库。

### 构建 / 验证

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # 在引用 GKMD.csproj 的宿主工程中执行
```

### 已知限制

- 内嵌的 usbip-win2 载荷仅支持 x64。
- 仅支持 Windows（`net10.0-windows`）。
- layout 序列化/反序列化使用基于反射的 `System.Text.Json`；在被裁剪的 AOT 宿主中，
  `GKLayout*` 类型需由宿主保留。

### 致谢与许可

派生自 HIDMaestro 1.5.1（MIT），原始版权声明保留在 [LICENSE](LICENSE)；
捆绑的 usbip-win2 为 BSD-2-Clause，见
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt)。

---

## v4.3.0 — 2026-10-01 — first release

**English** | [简体中文](#v430--2026-10-01--首个版本)

GKMD 4.3.0 is the first public release of GKMD, a standalone Windows virtual USB
game controller engine extracted from the GKME driver branch and derived from
[hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) 1.5.1 (MIT).

### Highlights

- **Pure USB/IP transport** — every controller is created through the
  usbip-win2 virtual host controller (vHCI). No UMDF2 kernel driver, no INF,
  no PnP device node; usbip-win2 **0.9.8.0** is embedded and deployed on demand.
- **Probe-only installation** — the engine never installs silently: it throws
  `UsbipInstallRequiredException` / `UsbipRebootRequiredException` so the host
  app can turn them into a prompt.
- **Device personas** — Xbox One `045E:02EA` (GIP handshake, 14-byte input,
  guide key, rumble/LED), Xbox 360 native report + capability query,
  Switch Pro / Joy-Con (`057E:2009/2006/2007`) subcommand & 0x30/0x21 responder
  with fake SPI mirror, DualShock 4 serial + authentication feature reports,
  DualSense test-command families and updated firmware info, high-resolution
  wheel (Resolution Multiplier), plus keyboard / mouse HID personas.
- **Vendor-class channel** — non-HID devices pass opaque input reports through
  verbatim and answer EP0 vendor requests (e.g. Xbox 360 capability report).
- **USB audio** — `UsbAudioEngine` is created only when the profile declares a
  USB Audio streaming interface.
- **Low-latency USB/IP** — `UsbipReceiveMode.LowLatency` is the default, with
  `ZeroCopy` still available; teardown waits for port detach to avoid reuse
  races during fast mode switching.
- **Static profile catalog** — built-in profiles are constructed in code by
  `StaticProfileRegistry.cs`; no JSON resources are loaded from disk.

### AOT (Native Ahead-of-Time) support

- `GKMD.csproj` sets `<IsAotCompatible>true</IsAotCompatible>`: the trim / AOT /
  single-file analyzers run on every build and both Debug and Release builds are
  **0 warnings / 0 errors**.
- The only reflection-based JSON paths (`GKLayoutLoader.cs`) are annotated
  locally with `#pragma warning disable IL2026, IL3050`; hosts that serialize
  layouts at runtime should keep the `GKLayout*` types alive
  (`JsonSerializerContext` or a trimmer root descriptor).
- Verified end-to-end: a host app referencing `GKMD.csproj` with
  `<PublishAot>true</PublishAot>` + `<PublishTrimmed>true</PublishTrimmed>`
  publishes with `dotnet publish -c Release -r win-x64 -p:PublishAot=true` with
  no warnings, and at runtime both embedded manifest resources are read back at
  full length (`USBip-0.9.8.0-x64.exe` = 26,390,744 bytes,
  `THIRD-PARTY-NOTICES.txt` = 2,269 bytes).
- GKME already publishes with `<PublishAot>true</PublishAot>` +
  `<PublishTrimmed>true</PublishTrimmed>` and links GKMD that way.

### Artifact

| File | Size | Notes |
| --- | --- | --- |
| `GKMD.dll` | 26,693,632 bytes | Release build, IL, `net10.0-windows`, AnyCPU, embedded usbip-win2 0.9.8.0 |

SHA-256: `3F71F78359C6A66781973DBBBBE74EA06EAAB1A9F9E799294A813CA8FC9CD41E`

The DLL embeds `USBip-0.9.8.0-x64.exe` and `THIRD-PARTY-NOTICES.txt`; it is
produced by `dotnet build -c Release`. Native AOT compilation happens in the
host application, not in this library.

### Build / verify

```powershell
dotnet build -c Release                       # library, trim + AOT analyzers on
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # from a host project referencing GKMD.csproj
```

### Known limitations

- Layout (de)serialization uses reflection-based `System.Text.Json`; under a
  trimmed/AOT host the `GKLayout*` types must be preserved by the host.
- The bundled usbip-win2 payload is x64-only.
- Windows only (`net10.0-windows`).

### Credits & license

Derived from HIDMaestro 1.5.1 (MIT); the original copyright notice is retained
in [LICENSE](LICENSE). Bundled usbip-win2 is BSD-2-Clause, see
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt).

---

## v4.3.0 — 2026-10-01 — 首个版本

[English](#v430--2026-10-01--first-release) | **简体中文**

GKMD 4.3.0 是 GKMD 的首个公开版本：一个独立的 Windows 虚拟 USB 手柄引擎，
从 GKME 驱动分支中抽取，派生自 [hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro)
1.5.1（MIT）。

### 亮点

- **纯 USB/IP 传输** — 所有虚拟手柄均经 usbip-win2 虚拟主机控制器（vHCI）创建，
  无 UMDF2 内核驱动、无 INF、无 PnP 设备节点；内嵌 usbip-win2 **0.9.8.0** 并按需部署。
- **仅探测、不擅自安装** — 缺失/过期时抛出 `UsbipInstallRequiredException` /
  `UsbipRebootRequiredException`，由宿主应用转成提示。
- **设备人格** — Xbox One `045E:02EA`（GIP 握手、14 字节输入、Home 键、震动/LED）、
  Xbox 360 原生报告 + 能力查询、Switch Pro / Joy-Con（`057E:2009/2006/2007`）
  子命令与 0x30/0x21 帧 + 伪造 SPI 镜像、DualShock 4 序列号与认证特征报告、
  DualSense 测试指令族与新版固件信息、高分辨率方向盘（Resolution Multiplier），
  以及键盘 / 鼠标 HID 人格。
- **Vendor class 通道** — 非 HID 设备原样透传输入报告并响应 EP0 vendor 请求
  （如 Xbox 360 能力报告）。
- **USB 音频** — 仅当 profile 声明 USB Audio streaming 接口时才创建 `UsbAudioEngine`。
- **低延迟 USB/IP** — 默认 `UsbipReceiveMode.LowLatency`，保留 `ZeroCopy`；
  拆除时等待端口脱离，避免快速切换时的端口复用竞态。
- **静态 profile 目录** — 内建 profile 由 `StaticProfileRegistry.cs` 代码构造，
  不再从磁盘加载 JSON 资源。

### AOT（Native Ahead-of-Time）支持

- `GKMD.csproj` 设置 `<IsAotCompatible>true</IsAotCompatible>`：每次构建都会运行
  trim / AOT / single-file 分析器，Debug 与 Release 均为 **0 警告 / 0 错误**。
- 唯一基于反射的 JSON 路径（`GKLayoutLoader.cs`）在代码中就地标注
  `#pragma warning disable IL2026, IL3050`；运行时序列化 layout 的宿主应保留
  `GKLayout*` 类型（`JsonSerializerContext` 或 trimmer 根描述符）。
- 端到端验证：引用 `GKMD.csproj`、开启 `<PublishAot>true</PublishAot>` +
  `<PublishTrimmed>true</PublishTrimmed>` 的宿主执行
  `dotnet publish -c Release -r win-x64 -p:PublishAot=true` 无任何警告，
  运行时两个内嵌 manifest resource 均可按完整长度读出
  （`USBip-0.9.8.0-x64.exe` = 26,390,744 字节，`THIRD-PARTY-NOTICES.txt` = 2,269 字节）。
- GKME 已使用 `<PublishAot>true</PublishAot>` + `<PublishTrimmed>true</PublishTrimmed>`
  发布，并以该方式链接 GKMD。

### 产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `GKMD.dll` | 26,693,632 字节 | Release 构建、IL、`net10.0-windows`、AnyCPU，内嵌 usbip-win2 0.9.8.0 |

SHA-256：`3F71F78359C6A66781973DBBBBE74EA06EAAB1A9F9E799294A813CA8FC9CD41E`

DLL 内嵌 `USBip-0.9.8.0-x64.exe` 与 `THIRD-PARTY-NOTICES.txt`，由
`dotnet build -c Release` 生成；Native AOT 编译在宿主应用中完成，而非本类库。

### 构建 / 验证

```powershell
dotnet build -c Release                                   # 类库，开启 trim + AOT 分析器
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # 在引用 GKMD.csproj 的宿主工程中执行
```

### 已知限制

- layout 序列化/反序列化使用基于反射的 `System.Text.Json`；在被裁剪的 AOT 宿主中，
  `GKLayout*` 类型需由宿主保留。
- 内嵌的 usbip-win2 载荷仅支持 x64。
- 仅支持 Windows（`net10.0-windows`）。

### 致谢与许可

派生自 HIDMaestro 1.5.1（MIT），原始版权声明保留在 [LICENSE](LICENSE)；
捆绑的 usbip-win2 为 BSD-2-Clause，见
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt)。
