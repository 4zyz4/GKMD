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
- 上游版本：`v1.5.1`
- 上游许可：MIT License（见 `LICENSE`，GKMD 保留原始版权声明）
- 命名空间：`HIDMaestro` / `HIDMaestro.Internal` / `HIDMaestro.Internal.Usbip`
  → `GKMD` / `GKMD.Internal` / `GKMD.Internal.Usbip`
- 类型 / 文件前缀：`HM`（HIDMaestro 的缩写）→ `GK`，例如 `HMContext` → `GKContext`、
  `HMController` → `GKController`、`HMContext.cs` → `GKContext.cs`、`HMLayout.cs` → `GKLayout.cs`
- 运行时标识：`Global\HIDMaestro*`、`HIDMAESTRO_TIMEOUT_SCALE`、`HKLM\SOFTWARE\HIDMaestro*`
  → `Global\GKMD*`、`GKMD_TIMEOUT_SCALE`、`HKLM\SOFTWARE\GKMD*`

> 注意：GKMD 是 GKME 项目内长期演进后的驱动分支的独立化产物，**不是** HIDMaestro 1.5.1 的
> 逐行精简单文件拷贝。下表的「差异」即相对 1.5.1 的全部已知改动。

## 差异总表

### 1. 传输 / 驱动架构

| 维度 | HIDMaestro 1.5.1 | GKMD |
| --- | --- | --- |
| 控制器创建路径 | 按 `RequiresUsbipBackend` 分支：普通 profile 走 UMDF2 + PnP，复合 persona 走 USB/IP | **无分支，全部走 USB/IP**（`CreateController` / `CreateControllerAt` 均调用 `CreateUsbipController`） |
| 驱动模型 | UMDF2 内核态用户模式驱动 `HIDMaestro.dll` + XUSB companion + INF / PnP | 纯 USB/IP：usbip-win2 虚拟主机控制器 + vHCI，**无内核驱动、无 INF、无设备节点** |
| 设备节点 | 创建 root-enumerated / SWD PnP devnode，写入 friendly name / ControllerIndex | 无 PnP devnode，设备字符串直接来自 USB 描述符 |
| 传输安装 | `DriverBuilder.FullDeploy()`：自签证书 → 签名 → inf2cat → pnputil | `UsbipDriverInstaller` 部署 / 升级 usbip-win2 |
| usbip-win2 版本 | 0.9.7.7 | **0.9.8.0**（IOCTL ABI、结构体同步更新） |
| USB/IP 接收模式 | 仅零拷贝（zero-copy MDL） | 新增 `UsbipReceiveMode.LowLatency`（默认）/ `ZeroCopy` |
| 安装策略 | 首次创建时静默自动安装 | 仅**探测**，缺失/过旧时抛 `UsbipInstallRequiredException` / `UsbipRebootRequiredException`，由宿主 App 显式安装 |
| 音频引擎 | `Audio` 恒非空 | `UsbAudioEngine?`，仅当 profile 声明了 USB Audio 流接口时才创建 |
| 控制器索引释放 | 先移除设备再释放索引 | 先拆 USB/IP 设备再释放索引（避免快速切换时端口复用竞争） |
| 诊断日志 | `DeviceOrchestrator.LogDiag` | 改为 `Debug.WriteLine` |

### 2. 新增功能

| 功能 | 相关文件 | 说明 |
| --- | --- | --- |
| Xbox One GIP 协议 | `Usbip/GipProtocol.cs`、`Usbip/GipResponder.cs`、`Usbip/GipLog.cs`、`Usbip/UsbipEmulatedDevice.cs` | 厂商类 `045E:02EA` 实现 Microsoft Gaming Input Protocol：HELLO/announce/identify/status 握手、14 字节 `gip_gamepad_pkt_input` 输入、guide 虚拟键、主机命令（power/LED/rumble/identify）解码 |
| Switch Pro / Joy-Con 响应器 | `Usbip/UsbipEmulatedDevice.cs`、`StaticProfileRegistry.cs` | `057E:2009/2006/2007`：0x80 USB init、0x01 子命令、0x30/0x21 帧、伪 SPI 镜像（IMU/摇杆校准、颜色、序列号、device type）、15 ms 流、IMU 开关、震动使能 |
| 厂商类（非 HID）设备通道 | `UsbDescriptorSet.cs`、`UsbipEmulatedDevice.cs`、`UsbConfigurationSpec.cs` | 不透明 input report 原样透传；`vendorRequests` 表应答 EP0 vendor 请求（如 Xbox 360 `0xC1/0x01/0x0100` 能力报告）；只应答 `InputEndpoint`，其余 INT-IN 端点 STALL（避免 xusb22 生成幻影音频设备） |
| Xbox 360 persona | `StaticProfileRegistry.BuildXbox360()` | 20 字节原生输入报告 + `VendorRequests` 能力报告，无 HID 描述符路径 |
| DualShock 4 认证 / 序列号 | `Usbip/UsbipEmulatedDevice.cs` | 新增 `Ds4FeatureReport20`（JDM-050 序列号）与 `Ds4FeatureReport81`（`0x03030301` challenge response）；识别 `SET_FEATURE 0x80` 子命令 |
| DualSense 测试指令 | `Usbip/SonyTestCommandHandler.cs` | torch/telemetry、Type2 tracability、BT MAC/patch、电池电压等 device_id/action_id 家族，支持多页/单页响应与头部注入 |
| DualSense 固件信息更新 | `Usbip/UsbipEmulatedDevice.cs` | 固件串 `202510:10:32` → `202510:38`，版本/系列字段调整，新增 `GetDs5FirmwareInfo()` |
| 高分辨率滚轮 | `UsbConfigurationSpec.ResolutionMultiplier` | 支持 `GET/SET_REPORT(Feature, 0)` 的 Resolution Multiplier，枚举时默认高分辨率 |
| 设备序列号字符串 | `ControllerProfile.SerialNumberString`、`UsbDescriptorSet.cs` | iSerial 返回真实序列号（此前固定返回 null） |
| 安装/重启异常类型 | `Usbip/UsbipInstallRequiredException.cs`、`Usbip/UsbipRebootRequiredException.cs` | 宿主 UI 可将「需安装 / 需重启」转成提示而非失败 |
| 静态 profile 注册表 | `StaticProfileRegistry.cs` | 以代码构造内建 profile，替代上游的 JSON 目录/嵌入资源 |
| 键盘 / 鼠标 HID persona | `StaticProfileRegistry.cs` | 内建 keyboard / mouse profile（GKME 用于虚拟键鼠） |

### 3. 移除的组件 / 功能

| 文件 / 功能（1.5.1 中存在） | 原有职责 |
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

### 4. 修改的文件（功能差异）

| 文件 | 主要变更 |
| --- | --- |
| `GKContext.cs` | 删除构造函数后台预热；`IsDriverInstalled` → `UsbipBackend.IsAvailable`；`InstallUsbipBackend` 改为 `Install(interactive:false)` 并失败抛异常；`InstallDriver` → `EnsureInstalled()`（不再自动安装）；`RemoveAllVirtualControllers` → `UsbipBackend.DetachAllOwned`；`LoadDefaultProfiles` 改用 `StaticProfileRegistry`；删除 `LoadProfilesFromDirectory`；`CreateController/CreateControllerAt` 一律走 USB/IP 且允许仅有 USB 配置（无 HID 描述符）；`FinalizeNames` 变为 no-op |
| `GKController.cs` | `_reportBuilder` 可空、新增 `_rawReportBuffer`；`SubmitState` 对无 HID 的厂商 profile 直接返回；删除全部 PID 发布 API；删除 Switch Pro 控制器侧逻辑（移至设备端）；`UsbAudio` 仅在有音频流接口时创建 |
| `ControllerProfile.cs` | 新增 `SerialNumberString`；删除 `Backend` / `RequiresUsbipBackend`；`UsbConfiguration` 变为每个 profile 都携带的描述符集；`GetOrBuildReportBuilder()` 可返回 null；**删除整个 `ProfileDatabase` 类** |
| `SharedMemoryIO.cs` | **删除 PID 状态整套**：PID_STATE 尺寸/偏移常量、`EnsurePidStateMapping`、`WritePidPool/BlockLoad/State`、`ReadPidBlockLoad`、`WritePidReportIds` 及相关句柄与释放逻辑 |
| `UsbConfigurationSpec.cs` | 新增 `InputReportSize`、`VendorRequests`（含 `VendorControlRequest` 类型）、`Gip`、`ResolutionMultiplier`；注释改为 USB/IP persona 描述符集 |
| `UsbDescriptorSet.cs` | `ReportDescriptor` 可空、新增 `HasHidInterface` / `InputEndpoint`；解析厂商 0x21 class descriptor；HID 接口改为可选；新增 `GetClassDescriptor`；iSerial 返回真实序列号 |
| `UsbipBackend.cs` | `Attach` 传 `UsbipReceiveMode.LowLatency`；`UsbipBackendHandle.Dispose` 增加 `WaitForPortDetached`（含一次重试），避免快速模式切换复用未清理端口 |
| `UsbipDriverInstaller.cs` | 版本/SHA 升 0.9.8.0；`EnsureInstalled` 改为校验 ABI 并抛「需安装/需重启」异常；新增 `Install(interactive)`、Inno 退出码解析（0/3010）、`NeedsRebootThisBoot` 标记、`WaitForCurrentAbi`、`IsCurrentAbi`；`StampOwnerHardwareId` 改为枚举 `ROOT\USB\0000..000F` 并逐个打标 |
| `UsbipEmulatedDevice.cs` | 新增 GIP / Switch 两条 input pump 分支、厂商类不透明上报、`vendorRequests`、Resolution Multiplier、DS4 0x20/0x81、DualSense 测试指令；仅应答 `InputEndpoint`；`Audio` 可空；重连/重置清理；GIP 错误日志 |
| `VhciClient.cs` | `plugin_hardware` 结构扩至 1120 字节（新增 serial / wsk_events）、`imported_device` 行 1128 字节；`Attach` 新增 `UsbipReceiveMode` 参数；`Detach` 将 ≤0 归一化为 `PORT_ALL(-1)`；新增 `WaitForPortDetached`、`IsCurrentAbi`、`UsbipReceiveMode` 枚举 |
| `UsbipServer.cs` | `Unregister` 增加身份校验（防止旧设备误删新设备）；注释版本升 0.9.8.0 |
| `GKProfile.cs` | 删除 `Backend` / `RequiresUsbipBackend`；`Connection` 改用 `IsNullOrEmpty` 判空；文档由「profile JSON」改为「catalog / 内建」 |
| `GKLayoutLoader.cs` | 增加 `IL3050` / `IL2026` 抑制与 `System.Diagnostics.CodeAnalysis`，适配 AOT / 裁剪 |
| `OemNameOverrideStore.cs` | 增加 `CA1416`（仅 Windows）平台兼容抑制 |
| `GKUsbAudio.cs` | 注释更新；`device.Audio` 改为可空后加 `!` |
| `UsbAudioEngine.cs` | 版本语义提到 0.9.8.0；`DeviceOrchestrator.LogDiag` → `Debug.WriteLine` |
| `VendorBlobCodec.cs`、`HidReportBuilder.cs`、`HidDescriptorBuilder.cs`、`GKOutputDecodedEventArgs.cs`、`GKOutputEncoder.cs`、`GKProfileBuilder.cs` | 仅措辞/微小空判断语法，无功能变化 |
| `GKGamepadState.cs`、`GKLayout.cs`、`GKOemNameOverride.cs`、`GKOutputPacket.cs`、`TimeoutScale.cs`、`VendorBlobProgram.cs`、`UsbipProtocol.cs` | 除命名空间外**无差异** |

### 5. 构建 / 工程差异

| 维度 | HIDMaestro 1.5.1 | GKMD |
| --- | --- | --- |
| 产物 | 类库 `HIDMaestro.Core.dll` | 类库 **`GKMD.dll`** |
| TargetFramework | `net10.0-windows10.0.26100.0` | `net10.0-windows` |
| Platform | x64（`PlatformTarget`） | AnyCPU |
| 命名空间 | `HIDMaestro` / `.Internal` / `.Internal.Usbip` | `GKMD` / `.Internal` / `.Internal.Usbip` |
| 目录结构 | `HIDMaestro.Core/` + `Internal/` + `Internal/Usbip/` | 扁平化到根目录 + `Usbip/` 子目录 |
| 驱动资源 | `PackResources` 目标从 `build/` 与本地 WDK 收集 `HIDMaestro.dll`、两个 INF、`hmswd.exe`、signtool/inf2cat 依赖树 | **不嵌入任何内核驱动/签名/编目工具**，仅嵌入 `Resources/USBip-0.9.8.0-x64.exe` 与 `THIRD-PARTY-NOTICES.txt` |
| usbip-win2 获取 | 构建时 `DownloadFile` 拉取 0.9.7.7 并 SHA256 校验（不匹配则失败） | 直接嵌入 0.9.8.0，无下载目标（运行时仍校验 SHA256） |
| Profile 来源 | 从 `profiles/**/*.json` 链接嵌入（逻辑名 `HIDMaestro.Profiles.*`） | 无 JSON 资源，由 `StaticProfileRegistry.cs` 代码构造 |
| 资源逻辑名 | `HIDMaestro.Resources.*` | `GKMD.Resources.*` |
| 运行时标识 | `Global\HIDMaestro*`、`HIDMAESTRO_TIMEOUT_SCALE`、`HKLM\SOFTWARE\HIDMaestro*` | `Global\GKMD*`、`GKMD_TIMEOUT_SCALE`、`HKLM\SOFTWARE\GKMD*` |
| 双阶段构建 | 需要（先构建原生驱动填充 `Resources/`，再两次 `dotnet build` 嵌入） | 不需要，资源为固定文件 |
| 版本 | 上游版本号（1.x） | `4.3.0.0`（跟随 GKME） |
| AOT / trimming | 未做 AOT 标注 | `IsAotCompatible=true`（trim / AOT 分析器，0 警告）；由宿主以 `PublishAot` 发布 |
| 宿主集成 | 通用 SDK | 供 [GKME](../GKME-Windows) 通过 `ProjectReference` 使用；`InternalsVisibleTo("GKME")` 暴露传输安装器 |

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
│   ├── UsbipBackend.cs / UsbipServer.cs / VhciClient.cs
│   ├── UsbipEmulatedDevice.cs  # 设备端行为（HID / GIP / Switch / vendor）
│   ├── UsbDescriptorSet.cs / UsbConfigurationSpec.cs
│   ├── UsbipDriverInstaller.cs # 部署/升级 usbip-win2
│   ├── GipProtocol.cs / GipResponder.cs / GipLog.cs
│   └── SonyTestCommandHandler.cs
└── Resources/
    ├── USBip-0.9.8.0-x64.exe
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
- 内嵌的 usbip-win2 载荷（`Resources/USBip-0.9.8.0-x64.exe`）是 manifest
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

每个 [GitHub Release](../../releases) 附带预构建的 `GKMD.dll`（Release、IL、AnyCPU，
RID 无关），变更日志见 [RELEASE_NOTES.md](RELEASE_NOTES.md)。

## 许可

MIT License，见 [LICENSE](LICENSE)。原始版权归 HIDMaestro Contributors，
GKMD 保留该声明并标注其派生关系。捆绑的 usbip-win2（BSD-2-Clause）许可见
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt)。
