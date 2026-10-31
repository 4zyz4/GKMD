# GKMD

[English](README.md) | **简体中文**

GKMD 是一个独立的 Windows 虚拟 USB 游戏控制器引擎（class library），通过
[usbip-win2](https://github.com/vadimgrn/usbip-win2) 的虚拟主机控制器（vHCI）在用户态
模拟 USB 设备，向内核上层的 HID / XInput / DirectInput / SDL / Gamepad API 呈现真实硬件身份。

## 来源声明

GKMD **修改自 [hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) 1.5.1**
（tag `v1.5.1`，MIT License），提取自 HIDMaestro 的 `sdk/HIDMaestro.Core`，并在其上做了大量
面向「纯 USB/IP 后端 + 固定内建 profile」的删减、改写与新增。

- 上游项目：https://github.com/hifihedgehog/HIDMaestro
- 来源版本：`v1.5.1`
- **对比的上游基线：`v2.0.3`**（2026-10-22），API 与行为的对比以其为准。该版本已把设备行为迁到 JSON profile +
  编译程序，并带有 identity key、传输自动安装、卸载、VR 与 ARM64 载荷；同时改动了公开 API，
  1.x 程序必须迁移到 2.0。2.1.0 的零分配 submit 路径另行同步。
- 上游许可：MIT License（见 `LICENSE`，GKMD 保留原始版权声明）
- 命名空间：`HIDMaestro` / `HIDMaestro.Internal` / `HIDMaestro.Internal.Usbip`
  → `GKMD` / `GKMD.Internal` / `GKMD.Internal.Usbip`
- 类型 / 文件前缀：`HM`（HIDMaestro 的缩写）→ `GK`，例如 `HMContext` → `GKContext`、
  `HMController` → `GKController`、`HMContext.cs` → `GKContext.cs`、`HMLayout.cs` → `GKLayout.cs`
- 运行时标识：`Global\HIDMaestro*`、`HIDMAESTRO_TIMEOUT_SCALE`、`HKLM\SOFTWARE\HIDMaestro*`
  → `Global\GKMD*`、`GKMD_TIMEOUT_SCALE`、`HKLM\SOFTWARE\GKMD*`

> 注意：GKMD 是 GKME 项目内长期演进后的驱动分支的独立化产物，**不是** HIDMaestro 1.5.1 的
> 逐行精简单文件拷贝。本文档除特别标注「1.5.1 基线」的行外，均以**最新上游 v2.0.3** 为对比对象。
> 由于 GKMD 停留在 1.5.1 派生 API 上，它同样缺少上游 1.6.0 至 2.0.3 中依赖 2.0 数据模型的改动；
> 其中 USB/IP 运行时代码的修复已同步到 2.0.3。

## 与 HIDMaestro 2.0.3 的差异

### 1. 传输 / 驱动架构

| 维度 | HIDMaestro 2.0.3 | GKMD |
| --- | --- | --- |
| 控制器创建路径 | 按 `RequiresUsbipBackend` 分支：普通 profile 走 UMDF2 + PnP，复合 persona 走 USB/IP | **无分支，全部走 USB/IP**（`CreateController` / `CreateControllerAt` 均调用 `CreateUsbipController`） |
| 驱动模型 | 普通 profile 使用 UMDF2 用户模式驱动 `HIDMaestro.dll` + XUSB companion + INF / PnP；复合 persona 使用 usbip-win2 虚拟主机控制器。无内核态驱动 | 纯 USB/IP：usbip-win2 虚拟主机控制器 + vHCI，**无 UMDF2 驱动、无 INF、无 PnP 设备节点** |
| 设备节点 | UMDF2 路径创建 root-enumerated / SWD PnP devnode，写入 friendly name / ControllerIndex；复合路径是 vhci USB 设备 | 无 PnP devnode，设备字符串直接来自 USB 描述符 |
| 传输安装 | 2.0.1 起直接嵌入未修改的 x64 / ARM64 `usbip2_ude.inf` / `.sys` / `.cat`，由 SDK 自己注册 host controller devnode（`InstallHostController`）；不运行 usbip-win2 安装器，不安装其 root-hub filter，并记录安装内容 | `UsbipDriverInstaller` 嵌入并运行官方 x64 `USBip-0.9.8.1-x64.exe`（Inno）安装器 |
| usbip-win2 版本 | 0.9.8.1，x64 + ARM64 载荷 | 0.9.8.1 安装器载荷（x64）；`VhciClient` 支持 **0.9.8.1 / 0.9.8.0 / 0.9.7.x** 三套请求布局，并探测已安装驱动实际支持的布局 |
| USB/IP 接收模式 | 始终走 IRP 接收路径（`wsk_events` 保持 false），无可选模式 | 0.9.8.x 上新增 `UsbipReceiveMode.LowLatency`（默认）/ `ZeroCopy`；0.9.7.x 始终走 MDL 路径 |
| 安装策略 | 首次创建复合 persona 时静默安装内嵌传输；`InstallDriver()` 对 UMDF2 做同样的事 | 仅**探测**，缺失/过旧时抛 `UsbipInstallRequiredException` / `UsbipRebootRequiredException`，由宿主 App 显式安装 |
| 音频引擎 | 复合 persona 通过 USB/IP 承载 USB Audio，SDK 自动安装传输 | `UsbAudioEngine?`，仅当 profile 声明了 USB Audio 流接口时才创建 |
| 控制器索引 / 身份释放 | lease + reservation 系统、identity key、多程序并存（无 key 的 1.12.0 程序落在 transport index 64 以上） | 先拆 USB/IP 设备再释放索引（避免快速切换时端口复用竞争） |
| 卸载 | `HMUninstall` / `HIDMaestroUninstall.exe` 移除控制器、驱动包、证书、注册表与文件；2.0.3 还可移除 usbip-win2 及其 root-hub filter | 无卸载 API / 工具；由宿主 App 自行清理（`UsbipBackend.DetachAllOwned`） |
| 架构 | 单个 AnyCPU DLL 内含 x64 与 ARM64 的驱动和传输载荷 | AnyCPU 托管 DLL；内嵌 usbip-win2 载荷仅 x64 |
| 诊断日志 | `DeviceOrchestrator.LogDiag` | 改为 `Debug.WriteLine` |

### 2. Profile 与设备行为

| 维度 | HIDMaestro 2.0.3 | GKMD |
| --- | --- | --- |
| Profile 来源 | 337 个内建 profile（其中 103 个键盘/鼠标），从 `profiles/**/*.json` 链接；支持运行时自定义 / 提取 profile；严格校验的 source-generated JSON | 由 `StaticProfileRegistry.cs` 以代码构造的 10 个 persona；**无 JSON 目录、无 profile 提取** |
| Profile 校验 | 严格 schema；被拒文件记入 `HMContext.ProfileLoadIssues` 并给出 JSON 路径；代码构造的 profile 抛出带 `ProfileId` / `JsonPath` 的 `HMProfileException` | C# 编译期构造；无 schema / JSON 路径诊断 |
| 设备行为 | 设备即数据：report 字段、feature 应答、协议与 report 程序都是 profile 键，`deviceModelFrom` 提供默认模型；目录内程序编译为代码，目录外 profile 由解释器运行同一套程序 | 固定的 C# builder（`HidDescriptorBuilder`、`HidReportBuilder`、`VendorBlobCodec`）与手写 responder |
| 多控制器设备 | `HMPort` / `HMPortSet` 支持一个 report 内承载多个控制器的适配器、分插器和接收器 | 无 |
| 设备身份 | `identityKey` + lease 让 parent / child / 接口路径 / DirectInput GUID / SDL 路径 / ContainerId / serial 在 dispose、重启、重启系统与驱动升级后保持稳定 | serial 按控制器索引变化；无捕获 serial 的 profile 生成稳定的合成 serial |
| Persona 覆盖 | 337 个 profile：Xbox 360 家族、Xbox One / Series（UMDF2）、DualShock 3 / 4、DualSense、Switch Pro / Joy-Con / Switch 2 Pro、Valve Steam Controller / Steam Deck、VR 控制器、方向盘、键盘鼠标、Wii U GameCube 适配器、PS Move 等 | 10 个 persona：Xbox 360、Xbox One、Xbox Series X\|S、DualShock 4 v2、DualSense、Switch Pro、Joy-Con L/R、键盘、鼠标 |
| Xbox One / Series 传输 | 通过 UMDF2 呈现为 Windows `xinputhid` 视角；USB 线上协议不是 GIP | 以 **GIP 厂商类设备**跑在 USB/IP 上（`045E:02EA` / `045E:0B12`），包含 `[MS-GIPUSB]` 信封 |
| Switch | Switch Pro / Joy-Con / Switch 2 Pro 的蓝牙与 USB 描述符，协议数据在 profile 中 | Switch Pro / Joy-Con 走 USB，responder 在代码侧 |
| 键盘 / 鼠标 | `SubmitKeyboard` / `SubmitMouse` / `SubmitConsumer`、LED 状态、绝对鼠标，以及 103 个真实设备 profile | GKME 使用的两个简单内建 persona |
| VR | `HMVR` / `HMVRController` 与 OpenVR 驱动包 | 无 |
| Sony 设备应答 | 由 profile 数据提供（firmware info、pairing info、calibration），蓝牙带 CRC-32 | 代码侧 DualShock 4 认证 / 序列号报文与 DualSense 测试指令处理器 |

### 3. GKMD 特有功能

| 功能 | 相关文件 | 说明 |
| --- | --- | --- |
| USB/IP 上的 GIP | `Usbip/GipProtocol.cs`、`Usbip/GipResponder.cs`、`Usbip/GipLog.cs` | Xbox One（`045E:02EA`）与 Xbox Series X\|S（`045E:0B12`）作为厂商类 USB 设备响应完整 GIP 握手（HELLO / announce / identify / status）、14 字节 `gip_gamepad_pkt_input`、guide 虚拟键，以及主机命令（power / LED / rumble / identify）。Xbox One 以真实手柄的 214 字节 gamepad 元数据为基础，把 PC security opt-out 追加为第 4 个 interface（保留 `INavigationController`），形成 230 字节 blob；此前的 182 字节示例元数据会让 `xboxgip` 在创建设备时 bugcheck |
| 可选的 USB/IP 接收模式 | `Usbip/VhciClient.cs`、`Usbip/UsbipBackend.cs` | 0.9.8.x 上支持 `UsbipReceiveMode.LowLatency`（默认，`wsk_events`）/ `ZeroCopy`（MDL） |
| 仅探测的安装策略 | `Usbip/UsbipInstallRequiredException.cs`、`Usbip/UsbipRebootRequiredException.cs` | 库不会在宿主意料之外安装驱动；宿主 UI 可将「需安装 / 需重启」转成提示 |
| 代码定义的 profile 注册表 | `StaticProfileRegistry.cs` | 内建 persona 就是普通 C# 代码，不依赖 JSON 或反射 |
| 代码侧 Sony 处理器 | `Usbip/SonyTestCommandHandler.cs`、`Usbip/UsbipEmulatedDevice.cs` | DualSense 测试指令（torch/telemetry、Type2 tracability、BT MAC/patch、电池电压）与 DualShock 4 `0x20` / `0x81` feature report（`0x03030301` challenge response） |
| 高分辨率滚轮 | `UsbConfigurationSpec.ResolutionMultiplier` | 应答 `GET/SET_REPORT(Feature, 0)`，枚举时默认高分辨率 |
| 对 AOT 友好的类库 | `GKMD.csproj`、`GKLayoutLoader.cs` | `IsAotCompatible=true`；仅对基于反射的 layout JSON 路径做局部抑制 |
| GKME 集成 | `AssemblyInfo.cs` | 由 [GKME](../GKME-Windows) 通过 `ProjectReference` 引用；`InternalsVisibleTo("GKME")` 暴露传输安装器 |

### 4. GKMD 未采用的上游工作（1.6.0 → 2.0.3）

只同步了适用于纯 USB/IP 架构、且截至 1.12.0 已存在的修复。以下上游工作没有进入 GKMD：

- **1.6.0 – 1.7.3**：虚拟 VR 控制器与 OpenVR 驱动、Valve persona（Steam Controller、Steam Deck）、Steam Controller 2 raw report、Xbox 360 家族的 WGI gamepad 呈现、legacy `GetControllerState`、原生二进制版本标记。
- **1.8.0 – 1.12.0**：身份 / 音频 / DualShock 4 修复（其中与 USB/IP 相关的**已同步**，见下文）、带 motion 的 Switch 2 Pro（1.11.0）、103 个键盘/鼠标 profile 与键盘鼠标 API（1.12.0），以及其它 catalog 与 persona 工作。
- **2.0.0**：devices-as-data 的 report 字段、feature 应答、协议与 report 程序；`ProgramForm` 编译器 / 解释器；`deviceModelFrom` 模型；带 JSON 路径的严格 profile 校验；`HMPort`；identity key 与 lease；`HMBuildInfo`；NativeAOT 支持；ARM64 驱动/传输载荷；Wii U GameCube 适配器；PS Move 与 DualShock 3 的数据化形态；337 个 profile 目录。
- **2.0.1 – 2.0.3**：不运行 usbip-win2 安装器而直接安装其 host controller 文件、`HMUninstall` / `HIDMaestroUninstall.exe`、移除 usbip-win2 host controller 与 root-hub filter，以及 Valve persona 修正。

2.0.0 的 **USB/IP 运行时**修复**已同步**（仅接受 host controller 导入、握手超时与并发上限、死服务清扫、按 location 拆除、释放与音频事件加固；见第 7 节同步清单），2.0.x 的零分配帧、输出、control 与 submit 路径也已移植。GKMD 未采用的是 2.0 数据模型、identity / lease 系统，以及 2.0.1–2.0.3 的安装/卸载器。

GKMD 仍然保留官方 usbip-win2 安装器、仅探测策略、代码定义的静态注册表和 1.x 派生的控制器 API。

### 5. 构建 / 工程差异

| 维度 | HIDMaestro 2.0.3 | GKMD |
| --- | --- | --- |
| 产物 | 类库 `HIDMaestro.Core.dll` | 类库 **`GKMD.dll`** |
| TargetFramework | `net10.0-windows10.0.26100.0` | `net10.0-windows` |
| Platform | AnyCPU | AnyCPU |
| 命名空间 | `HIDMaestro` / `.Internal` / `.Internal.Usbip` | `GKMD` / `.Internal` / `.Internal.Usbip` |
| 目录结构 | `sdk/HIDMaestro.Core/` + `Internal/` + `Internal/Usbip/` + `ProgramForm/` + `Internal/Uninstall/` | 扁平化到根目录 + `Usbip/` 子目录 |
| 驱动资源 | 嵌入 UMDF2 驱动 + XUSB companion + SWD helper + INF + 签名/编目工具 + OpenVR 驱动 + `usbip2_ude` 文件，x64 与 ARM64 各一套 | **不嵌入任何内核 / UMDF2 驱动及签名/编目工具**，仅嵌入 `Resources/USBip-0.9.8.1-x64.exe` 与 `THIRD-PARTY-NOTICES.txt` |
| usbip-win2 获取 | 构建时由 `prepare_usbip.ps1` 解包固定版本的 0.9.8.1 安装器，生成各架构 host controller 文件 | 直接嵌入 0.9.8.1，无下载目标；运行时校验 SHA-256 |
| Profile 来源 | `profiles/` 下 348 个 JSON 文件；2.0.3 目录暴露 337 个内建 profile，以 source-generated JSON 编译进程序集 | 无 JSON 资源，由 `StaticProfileRegistry.cs` 代码构造 |
| 资源逻辑名 | `HIDMaestro.*` | `GKMD.Resources.*` |
| 运行时标识 | `Global\HIDMaestro*`、`HIDMAESTRO_TIMEOUT_SCALE`、`HKLM\SOFTWARE\HIDMaestro*` | `Global\GKMD*`、`GKMD_TIMEOUT_SCALE`、`HKLM\SOFTWARE\GKMD*` |
| 双阶段构建 | 需要（先为 x64/ARM64 构建 UMDF2 驱动、companion、SWD helper 与 OpenVR 驱动，再构建 SDK） | 不需要，资源为固定文件 |
| 版本 | `2.0.3` | `4.8.0.0`（跟随 GKME） |
| AOT / trimming | `IsAotCompatible=true`；source-generated JSON、`LibraryImport`，AOT 分析器把警告当构建失败；可直接 NativeAOT 发布 | `IsAotCompatible=true`（trim / AOT 分析器，0 警告）；`GKLayoutLoader.cs` 中基于反射的 `System.Text.Json` 局部抑制；由宿主以 `PublishAot` 发布 |
| ARM64 | 单个 DLL 内含 x64 与 ARM64 驱动/传输载荷 | 托管 AnyCPU 程序集；捆绑的 usbip-win2 载荷仅 x64 |
| 宿主集成 | 通用 SDK | 供 [GKME](../GKME-Windows) 通过 `ProjectReference` 使用；`InternalsVisibleTo("GKME")` 暴露传输安装器 |

### 6. 未从 1.5.1 基线继承的组件（附录）

这些组件存在于 1.5.1 基线。上游 2.0.3 仍保留其中大部分（若干已扩展；`SwitchProPacker` 已被 profile state block 取代）；GKMD 均不包含。

| 文件 / 功能（上游存在） | 原有职责 |
| --- | --- |
| `HMDeviceExtractor.cs` | 从已连接物理 HID 设备提取完整 profile（枚举 + 重建描述符） |
| `HMHidDeviceInfo.cs` | `ListDevices()` 的轻量 HID 设备信息 DTO |
| `HMPidState.cs` | PID FFB 公共类型（`PidLoadStatus`、`PidStateFlags`、`HMPidBlockLoad`） |
| `Internal/DeviceManager.cs` | 基于 `CM_Register_Notification` 的事件驱动 PnP 设备管理 |
| `Internal/DeviceNodeCreator.cs` | 创建 root-enumerated 虚拟 PnP 设备节点（含 xinputhid / Xbox legacy / 普通 HID 三条枚举路径） |
| `Internal/DeviceOrchestrator.cs` | controller 从 profile 到活设备的 setup/teardown 编排、companion 创建、orphan sweep、GameInputService 预热 |
| `Internal/DeviceProperties.cs` | 通过 `CM_Set_DevNode_PropertyW` 设置 FriendlyName / DeviceDesc / BusReportedDeviceDesc |
| `Internal/DriverBuilder.cs` | 自包含 UMDF2 驱动安装器（解压、自签证书、签名、编目、pnputil 部署、same-version 快速路径） |
| `Internal/EmbeddedManifest.cs` | 嵌入驱动安装载荷的稳定 SHA-256，供 `FullDeploy` 快速短路 |
| `Internal/HidDescriptorReconstructor.cs` | 从 Windows preparsed-data 重建 HID report descriptor |
| `Internal/HidDeviceEnumerator.cs` | SetupAPI + `Hid_*` 枚举已连接 HID 设备 |
| `Internal/HidPreparsedData.cs` | Windows HID preparsed data 二进制布局镜像 |
| `Internal/PidReportIdExtractor.cs` | 遍历 report descriptor 找出 PID Pool/State/BlockLoad 的 Report ID |
| `Internal/PnputilHelper.cs` | `pnputil /enum-drivers`、`/delete-driver` 的结构化封装 |
| `Internal/SwdDeviceFactory.cs` | 通过 `hmswd.exe`（`SwDeviceCreate`）创建 SWD 设备以获得真实 ContainerId |
| `Internal/SwitchProPacker.cs` | SDK 侧 Switch Pro 0x30 body 打包 + HD 震动幅度解码（逻辑已迁入设备模拟器端） |
| 功能：PID FFB 共享段 | `SharedMemoryIO` 中整套 `GKMD_PidState<N>` section、`PublishPidPool/BlockLoad/State`、`GetCurrentPidBlockLoad`、`WritePidReportIds` |
| 功能：profile 磁盘/嵌入加载 | `GKContext.LoadProfilesFromDirectory`、`ProfileDatabase.Load/LoadEmbedded` |
| 功能：UMDF2 生命周期 | `InstallDriver` 的 ghost sweep / `FullDeploy`、`_batchDisposing`、`RemoveOrphanHidChildrenBatch`、后台预热任务 |
| `HIDMaestro.Core.csproj` | 上游 SDK 工程文件，由 `GKMD.csproj` 取代 |

### 7. 相对 1.5.1 基线的文件级改动（附录）

以下是 GKMD 对 1.5.1 文件所做的改动。上游 2.0.3 此后已重写其中许多文件，因此本表记录的是 GKMD 的派生过程，而非 2.0.3 的当前实现。

| 文件 | 主要变更 |
| --- | --- |
| `GKContext.cs` | 删除构造函数后台预热；`IsDriverInstalled` → `UsbipBackend.IsAvailable`；`InstallUsbipBackend` 改为 `Install(interactive:false)` 并失败抛异常；`InstallDriver` → `EnsureInstalled()`（不再自动安装）；`RemoveAllVirtualControllers` → `UsbipBackend.DetachAllOwned`；`LoadDefaultProfiles` 改用 `StaticProfileRegistry`；删除 `LoadProfilesFromDirectory`；`CreateController/CreateControllerAt` 一律走 USB/IP 且允许仅有 USB 配置（无 HID 描述符）；`FinalizeNames` 变为 no-op |
| `GKController.cs` | `_reportBuilder` 可空、新增 `_rawReportBuffer`；`SubmitState` 对无 HID 的厂商 profile 直接返回；删除全部 PID 发布 API；删除 Switch Pro 控制器侧逻辑（移至设备端）；`UsbAudio` 仅在有音频流接口时创建 |
| `ControllerProfile.cs` | 新增 `SerialNumberString`；删除 `Backend` / `RequiresUsbipBackend`；`UsbConfiguration` 变为每个 profile 都携带的描述符集；`GetOrBuildReportBuilder()` 可返回 null；**删除整个 `ProfileDatabase` 类**；`FieldSpec` 新增 `Divisor` / `FullValue`，用于同步自 1.10.0 的 DualShock 4 修复 |
| `SharedMemoryIO.cs` | **删除 PID 状态整套**：PID_STATE 尺寸/偏移常量、`EnsurePidStateMapping`、`WritePidPool/BlockLoad/State`、`ReadPidBlockLoad`、`WritePidReportIds` 及相关句柄与释放逻辑 |
| `UsbConfigurationSpec.cs` | 新增 `InputReportSize`、`VendorRequests`（含 `VendorControlRequest` 类型）、`Gip`、`ResolutionMultiplier`；注释改为 USB/IP persona 描述符集 |
| `UsbDescriptorSet.cs` | `ReportDescriptor` 可空、新增 `HasHidInterface` / `InputEndpoint`；解析厂商 0x21 class descriptor；HID 接口改为可选；新增 `GetClassDescriptor`；iSerial 返回真实序列号；按控制器索引变化已捕获的 serial，并为无 serial 的 profile 提供稳定的合成 serial（上游 1.8.0 身份修复） |
| `UsbipBackend.cs` | `Attach` 传 `UsbipReceiveMode.LowLatency`；死服务清扫只拆除没有监听者的 import、只取消死 location 的 attempts；attach 失败会清理抢先进入的 import；`UsbipBackendHandle.Dispose` 按 location 拆除、等待其离开列表（含一次重试）并取消其 attempts |
| `UsbipDriverInstaller.cs` | 固定 **0.9.8.1** 并校验该 release 的 SHA-256；`EnsureInstalled` 校验 ABI 并抛「需安装/需重启」异常；新增 `Install(interactive)`、Inno 退出码解析（0/3010）、`NeedsRebootThisBoot` 标记、`WaitForCurrentAbi`、`IsCurrentAbi`；调用 `VhciClient.IsCurrentAbi()`，使 0.9.8.1 / 0.9.8.0 / 0.9.7.x 都能被识别；`StampOwnerHardwareId` 枚举 `ROOT\USB\0000..000F` 并逐个打标 |
| `VhciClient.cs` | 支持**三套 vHCI 布局**并探测已安装驱动的那一套：0.9.8.1（`plugin_hardware` 1124、stop 1108、行 1132，`port` 后新增 `location_hash`）、0.9.8.0（1120 / 1104 / 1128）、0.9.7.x（1100 / 1104 / 1108，无 `wsk_events`）；`Attach` 新增 `UsbipReceiveMode` 参数；`Detach` 将 ≤0 归一化为 `PORT_ALL(-1)`；新增 `WaitForPortDetached`、`DetachLocation`、`FindLocation`、`SamePort`、`WaitForLocationDetached`、批量 `StopAttachAttempts`、`IsCurrentAbi`、`InstalledLayout`、`UsbipReceiveMode` |
| `UsbipServer.cs` | `Unregister` 增加身份校验（防止旧设备误删新设备）；新增仅接受 host controller 的导入、握手超时与并发上限、GKMD 独立端口段与 OS 分配回退、未知 bus id 的 re-attach 取消、应答失败时的 claim 清理，以及设备 Dispose 后结束 import |
| `Usbip/TcpConnectionOwner.cs` | 新增：通过 `GetExtendedTcpTable` 把已接受的 loopback 连接映射到所属 PID，只有内核 WSK 连接（System，PID 4）可以导入 |
| `UsbipEmulatedDevice.cs` | 新增 GIP / Switch 两条 input pump 分支、厂商类不透明上报、`vendorRequests`、Resolution Multiplier、DS4 0x20/0x81、DualSense 测试指令；仅应答 `InputEndpoint`；`Audio` 可空；重连/重置清理；GIP 错误日志；同步上游 1.10.0 的 DualShock 4 `0x01` 偏移与 `0x05` 校准；把接口 / alt setting 传给音频引擎；新增 `IsDisposed` / claim 检查、会唤醒输入泵的幂等 `Dispose`，以及描述符未声明 input report ID 时的 STALL |
| `Usbip/GipResponder.cs` | Xbox One 以真实手柄的 214 字节 gamepad 元数据为基础，把 PC security opt-out 追加为第 4 个 interface（保留 `INavigationController`），重算 device / total / client-command 偏移后形成 230 字节 blob；Xbox One 的 `InputReportSize` 从 18 修正为 14。此前的 182 字节示例元数据会让 `xboxgip` 在创建设备时 bugcheck |
| `Usbip/UsbAudioEngine.cs` | 版本语义跟随 0.9.8.x；`DeviceOrchestrator.LogDiag` → `Debug.WriteLine`；旧 filter 不转发 `SET_INTERFACE` 时，根据等时传输推断流的开启/关闭（上游 1.10.0 修复）；针对 0.9.8.1 filter 使用 bmRequestType 0x80；control 与 stream 变化在专用 event thread 上按主机顺序触发，handler 异常会被丢弃 |
| `GKProfile.cs` | 删除 `Backend` / `RequiresUsbipBackend`；`Connection` 改用 `IsNullOrEmpty` 判空；文档由「profile JSON」改为「catalog / 内建」 |
| `GKLayoutLoader.cs` | 增加 `IL3050` / `IL2026` 抑制与 `System.Diagnostics.CodeAnalysis`，适配 AOT / 裁剪 |
| `OemNameOverrideStore.cs` | 增加 `CA1416`（仅 Windows）平台兼容抑制 |
| `GKUsbAudio.cs` | 注释更新；`device.Audio` 改为可空后加 `!`；公开音频事件按 handler 逐个触发，异常被丢弃而不是结束音频线程或连接 |
| `VendorBlobCodec.cs`、`VendorBlobProgram.cs` | 同步上游 1.10.0 的 DualShock 4 修复：`uint16-le` 传感器时间戳、触摸包计数器、电量 `fullValue`、cable 位，以及按 8 位触发值判定 `LT_DIGITAL` / `RT_DIGITAL` |
| `HidReportBuilder.cs`、`HidDescriptorBuilder.cs`、`GKOutputDecodedEventArgs.cs`、`GKOutputEncoder.cs`、`GKProfileBuilder.cs` | 仅措辞/微小空判断语法，无功能变化 |
| `GKGamepadState.cs`、`GKLayout.cs`、`GKOemNameOverride.cs`、`GKOutputPacket.cs`、`TimeoutScale.cs`、`UsbipProtocol.cs` | 除命名空间外**无差异** |

#### 已同步的上游修复（1.5.1 → 1.12.0）

只采纳适用于纯 USB/IP 架构的修复：

- **音频流推断**（`9306fc3`）：usbip-win2 0.9.8.1 搭配旧 filter 驱动时收不到 `SET_INTERFACE`；引擎改为在首次等时传输时开启流，并在传输停止约 200 ms 后关闭；已经收到过 `SET_INTERFACE` 的接口仍只由它决定状态。
- **每控制器稳定 serial**（`46054b8`、`a396741`）：USB serial 按控制器索引变化；无捕获 serial 的 profile 生成稳定的合成 serial。Windows 以它作为 USB 实例 ID，而不再依赖 vhci 端口。
- **DualShock 4 报文**（`fd86c1a`）：`0x01` 各字段落在 SDL/Linux 读取的偏移；传感器时间戳按 16/3 µs 递增；电量/充满/cable 与触摸字段符合真实手柄；`LT_DIGITAL` / `RT_DIGITAL` 按 8 位触发值判定；校准改为单位校准（8192/g、16/°/s），并提供 DS4 的 `0x05` 顺序。

#### 已同步的上游 USB/IP 修复（1.12.0 → 2.1.0）

以下 2.0.x 与 2.1.0 改动完全位于 USB/IP 传输与设备层，不依赖 2.0 数据模型，因此已移植：

- **仅接受 host controller 导入**（`TcpConnectionOwner`）：只有连接客户端属于 System 进程（PID 4，内核 WSK 连接的表现）时才接受 `OP_REQ_IMPORT`。本机其它程序不能再从另一个进程导入 persona。
- **握手限制**：连接必须在 5 秒内完成导入握手；同时最多 16 个握手，超出且不是 host controller 打开的连接会在不创建线程的情况下关闭。
- **死服务清扫**：只拆除端口上没有监听者（或本进程自己的端口上未导出的 bus id）的 import，并且只取消这些死 location 的 re-attach attempts。其它正在运行程序的 server 和 import 不会被触碰。
- **按 location 拆除**：`DetachLocation` / `FindLocation` / `WaitForLocationDetached` 按 `(host, server 端口, bus id)` 拆除与等待，而不是按可能已被其它设备复用的 vhci 端口。
- **注册闸门**：清扫期间新的注册会等待，避免新设备 attach 到清扫即将拆除的位置。
- **attach / import 清理**：attach 失败会拆除抢先进入的 import 并取消其 attempt；server 在发送 import 应答前就持有 device claim，因此应答发送失败也不会让 bus id 永久停在 `ST_DEV_BUSY`。
- **释放**：被 Dispose 的设备会结束其打开的 import；handle 按 location 拆除并取消该 location 的 attempts；`Dispose` 会唤醒输入泵，而不再等满 500 ms 的帧等待；第二次 `Dispose` 直接返回。
- **音频事件隔离**：control 与 stream 变化在专用 audio event thread 上按主机顺序触发，慢 handler 不会阻塞连接 reader 或音频泵；handler 抛出的异常会被丢弃，而不是结束连接或进程。
- **GET_REPORT(Input)**：HID 描述符未声明的 report ID 返回 STALL，而不是最后一份报文。
- **USB/IP 传输零分配**：命令 reader 自带接收缓冲，并复用 OUT 数据、等时描述符与包数组；输入泵在自己的缓冲里构建每一帧，队列中的帧在被读取应答或淘汰后归还缓冲池；Switch 与 GIP 帧就地构建；control 应答（描述符、状态、配置、接口、UAC 值、`GET_REPORT` 与 Sony feature stub）从设备自有缓冲或栈上发出，vendor 表的十六进制应答在构造时解码一次；audio URB 池化，包与 PCM 数组复用。
- **submit 路径零分配**：`WriteInputFrame` 通过 section 指针写入、接受 span，seqlock 只用一个 write barrier 加一次 release store，`SetEvent` 门铃移出内联；`SubmitRawReport` 对没有固定字节的帧直接从调用方 span 写入；`SubmitState` 只在扩展 codec 与 XUSB GIP 块真正使用时才解析六个 simple slot，描述符路径由 `BuildReportInto` 自己读取轴值；`HidReportBuilder.WriteBits` 用一次掩码读改写写入任意 1–32 位字段，而不是逐位循环。

另有一项 GKMD 自身的调整：server 监听自己的 `37286..37301`，避开 HIDMaestro 的 `18509..18524` / `28509..28524` 与 FlexInput 的 `18008`，两者可并存而不会互相接管对方的 import。该段用完后回退到 OS 分配的 loopback 端口。

以下 2.0.x 改动未同步（不适用或不属于 USB/IP 运行时）：owner record 与 transport index lease（身份/多程序系统）、直接安装 usbip-win2 host controller 与 `HMUninstall`（GKMD 保留官方安装器与仅探测策略）、ARM64 载荷，以及 2.0 数据模型。

#### 1.12.0 之后未同步的上游工作（1.12.0 → 2.0.3）

- **2.0.0** 把设备层换成 JSON 数据 + 编译程序，新增带 JSON 路径的严格 profile 校验、`HMPort`、identity key / lease、NativeAOT、ARM64 载荷、Wii U GameCube 适配器与 337 个 profile。这些是架构性改动，无法直接移植进 GKMD 的静态 C# 注册表；其中 USB/IP 运行时的修复已列在上方并已同步。
- **2.0.1 – 2.0.3** 直接安装 usbip-win2 host controller 文件，新增 `HMUninstall` 与 usbip-win2 host controller / root-hub filter 移除，并修正 Valve persona。GKMD 有意保留官方 usbip-win2 安装器与仅探测的安装策略，也没有 Valve persona 需要修正。
- 1.6.0 – 1.12.0 之间的 VR、Valve persona、Switch 2 Pro、键盘/鼠标 profile、PS Move、DualShock 3 压力等属于新 persona 或新 API，而非可移植修复；见第 4 节。

## 目录结构

```
GKMD/
├── GKMD.csproj                 # 类库工程（net10.0-windows）
├── AssemblyInfo.cs             # AssemblyVersion / InternalsVisibleTo
├── GKContext.cs                # 顶层入口：profile 目录 + 控制器生命周期
├── GKController.cs             # 单个虚拟控制器
├── GKProfile.cs / GKProfileBuilder.cs / ControllerProfile.cs
├── HidDescriptorBuilder.cs / HidReportBuilder.cs
├── SharedMemoryIO.cs           # 与设备端共享的 pagefile section / event
├── StaticProfileRegistry.cs    # 内建 profile（代码构造）
├── TimeoutScale.cs / GKLayout.cs / GKLayoutLoader.cs / ...
├── Usbip/                      # USB/IP 后端
│   ├── UsbipBackend.cs / UsbipServer.cs / VhciClient.cs / TcpConnectionOwner.cs
│   ├── UsbipEmulatedDevice.cs  # 设备端行为（HID / GIP / Switch / vendor）
│   ├── UsbDescriptorSet.cs / UsbConfigurationSpec.cs
│   ├── UsbipDriverInstaller.cs # 部署/升级 usbip-win2
│   ├── UsbipInstallRequiredException.cs / UsbipRebootRequiredException.cs
│   ├── GipProtocol.cs / GipResponder.cs / GipLog.cs
│   └── SonyTestCommandHandler.cs
└── Resources/
    ├── USBip-0.9.8.1-x64.exe
    └── THIRD-PARTY-NOTICES.txt
```

## 构建

```powershell
dotnet build -c Release
```

GKME 集成方式：把本仓库与 GKME 仓库放在同一父目录下，GKME 通过
`<ProjectReference Include="..\GKMD\GKMD.csproj" />` 引用本工程。

## AOT 支持

GKMD **兼容 AOT / trimming**，可被链接进 Native AOT 宿主程序：

- `GKMD.csproj` 设置了 `<IsAotCompatible>true</IsAotCompatible>`，每次构建都会运行
  trim / AOT / single-file 分析器，Debug 与 Release 构建均为 0 警告 / 0 错误。
- 唯一基于反射的 JSON 路径（`GKLayoutLoader.cs`，`System.Text.Json` 的 layout
  读写）在代码里用 `#pragma warning disable IL2026, IL3050` 就地标注；
  运行时会序列化 layout 的宿主应保留 `GKLayout*` 类型（如 `JsonSerializerContext`
  或 trimmer 根描述符）。
- 内嵌的 usbip-win2 载荷（`Resources/USBip-0.9.8.1-x64.exe`）是 manifest
  resource，**在 Native AOT 发布后依然存在**：引用本工程的宿主执行
  `dotnet publish -r win-x64 -p:PublishAot=true` 后，可按完整长度读回该资源。

自行验证：

```powershell
# 1) 打开 trim / AOT 分析器的类库构建（必须 0 警告）
dotnet build -c Release

# 2) 在引用 GKMD.csproj 的宿主工程里做完整的 Native AOT 发布
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```

GKME 已使用 `<PublishAot>true</PublishAot>` + `<PublishTrimmed>true</PublishTrimmed>`
发布，并以这种方式链接 GKMD。

## 发布（Releases）

自 v4.5.2 起，每个 [GitHub Release](../../releases) 附带一个 `GKMD-v<版本>.zip`
（Release、IL、AnyCPU，RID 无关），内含 `GKMD.dll`、`LICENSE` 与
`THIRD-PARTY-NOTICES.txt`，确保许可声明随每份二进制副本分发。使用
`dotnet build -c Release -t:PackageRelease` 生成，变更日志见
[RELEASE_NOTES.md](RELEASE_NOTES.md)。

## 许可

MIT License，见 [LICENSE](LICENSE)。原始版权归 HIDMaestro Contributors，
GKMD 保留该声明并标注其派生关系。捆绑的 usbip-win2（BSD-2-Clause）许可见
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt)。
