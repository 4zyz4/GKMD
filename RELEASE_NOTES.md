# Release notes

## v4.9.0 — 2026-11-01 — Linux support and Windows on Arm

**English** | [简体中文](#v490--2026-11-01--linux-支持与-windows-on-arm)

GKMD 4.9.0 adds Linux support and Windows on Arm support to the same AnyCPU
assembly. Release packages cover Windows x64, Windows ARM64, Linux x64 and
Linux ARM64. The two Windows builds each embed exactly one official usbip-win2
0.9.8.1 installer; the Linux builds ride mainline's in-kernel `vhci_hcd` and
carry no installer payload.

### Highlights

#### Linux (in-kernel `vhci_hcd`)

- **Multi-targeting** — `GKMD.csproj` now builds `net10.0-windows` (usbip-win2,
  `GKMD_WINDOWS` define, embedded installer) and `net10.0` (Linux). A host picks
  the right build through `ProjectReference`, so no source or API change is
  needed in GKME or any other consumer.
- **Managed attach through sysfs** — `VhciClient.Linux.cs` connects to the
  in-process server, performs the `OP_REQ_IMPORT` handshake itself, and hands
  the connected socket to the kernel by writing `port sockfd devid speed` to
  `/sys/devices/platform/vhci_hcd.0/attach`. Detach, status and the
  `/var/run/vhci_hcd/port<N>` location records match the `usbip` tool, so
  location-based detach and the stale sweep work unchanged. `O_NONBLOCK` is
  cleared with `fcntl` before the handoff because the kernel client expects a
  blocking socket.
- **`modprobe` replaces the installer** — `UsbipDriverInstaller.Linux.cs` keeps
  the same API (`EnsureInstalled`, `Install`, `IsInstalled`,
  `BundledArchitecture`). `EnsureInstalled` only probes and throws
  `UsbipInstallRequiredException` when `vhci_hcd` is absent; `Install` runs
  `modprobe vhci-hcd` directly when root and through `pkexec` for an
  interactive, non-root install.
- **uid-based admission** — Linux imports are user-space initiated, so there is
  no System-process socket to check. `TcpConnectionOwner.Linux.cs` reads the
  peer's uid from `/proc/net/tcp` and the server admits root or this process's
  own uid.
- **Asynchronous detach** — the kernel's hub thread runs the disconnect, so
  detach waits (bounded) for the port to report `VDEV_ST_NULL` (4) instead of
  expecting a synchronous ioctl. There are no re-attach attempts to cancel, so
  `StopAttachAttempts` is a no-op.
- **Port 0 is a real port** — Linux's global vhci numbering starts at 0
  (Windows starts at 1), so `VhciClient.Detach` treats only a negative value as
  "every port", and `FindLocation` returns -1 when nothing matches.
- **Portable in-process channel** — `SharedMemoryIO` no longer uses named
  sections or named Win32 events: both ends are always in one process now, so
  it is a block of `NativeMemory.AllocZeroed` plus managed `AutoResetEvent`s.
  This removes the last Win32 dependency from the frame path and works
  identically on Windows and Linux.
- **Verified on WSL2** — on Ubuntu with kernel
  `6.6.87.2-microsoft-standard-WSL2`, the DualShock 4 and Switch Pro personas
  enumerated through `vhci_hcd`, `usbhid` bound their HID interface (and
  `snd-usb-audio` the DualShock 4 audio interfaces), submitted state produced
  evdev axis and button events, detach returned every port to `VDEV_ST_NULL`,
  and two concurrent processes attached on ports 0 and 1 without interfering.
  `tools/LinuxSmokeTest` reproduces this test.

#### Windows on Arm

- **ARM64 payload** — `Resources/USBip-0.9.8.1-arm64.exe` is the upstream
  release asset, unmodified (19,060,408 bytes, SHA-256
  `CAB7FF97F79275EEB5C5B8BB2EB3111FF6B6C4EEAD07D9BEC9BE5BD1E3A35800`, matching
  the digest the GitHub release API publishes). Like the x64 asset it is a
  32-bit Inno Setup binary; what differs is the driver package it carries
  (ARM64 `usbip2_ude.sys`).
- **One payload per build** — `GKMD.csproj` selects the installer from
  `UsbipPayloadArch`, which follows `RuntimeIdentifier` when one is given
  (`dotnet build -r win-arm64`) and defaults to x64. The two builds therefore
  land in `bin\Release\net10.0-windows\` (x64) and `...\win-arm64\` (ARM64),
  and a host that sets its RID in its own project file forwards
  `-p:UsbipPayloadArch=arm64`. Any other value fails the build.
- **OS-architecture selection** — at run time `UsbipDriverInstaller` still
  chooses by `RuntimeInformation.OSArchitecture`, not the process
  architecture: an x64 host process under emulation on Windows on Arm has to
  install the ARM64 driver, the only kind ARM64 Windows loads. When the
  shipped build carries the other payload, extraction throws a message naming
  the build to ship instead of running an installer that cannot work.
  `UsbipDriverInstaller.BundledArchitecture` reports what a build carries.
- **Per-payload verification** — each architecture carries its own SHA-256 from
  the release API and is checked after extraction; a mismatch still deletes the
  file and throws instead of running it. Extraction uses a per-architecture
  temp directory (`%TEMP%\GKMD_usbip_0.9.8.1_arm64`).
- **vHCI layouts unchanged** — the IOCTL structs contain no pointer-sized
  fields, so the 0.9.8.1 ARM64 driver speaks the same
  `0.9.8.1 / 0.9.8.0 / 0.9.7.x` request layouts and the existing probe still
  discovers the installed one. `pnputil`, CfgMgr and the IPHLPAPI owner check
  are architecture-neutral (the TCP owner row is 24 bytes on every 64-bit
  target).

### Compatibility

- No public API or profile format change; controller personas are unchanged.
- Windows: works against an installed usbip-win2 **0.9.7.x, 0.9.8.0 or
  0.9.8.1**, on x64 and on ARM64.
- Linux: any mainline kernel with `vhci_hcd`; attach requires root, and the
  distribution's `usbip` tool can attach by hand with
  `--tcp-port <server port>`.
- The bytes on the wire and in the shared input section are unchanged.
- `GKOemNameOverride` is a no-op on Linux: joystick OEM labels come from a
  Windows registry database with no Linux equivalent.

### Artifacts

| File | Size | Notes |
| --- | --- | --- |
| `GKMD-v4.9.0-windows-x64.zip` | 25,426,898 bytes | Windows x64: `GKMD.dll` (26,413,056 bytes, embeds `USBip-0.9.8.1-x64.exe`), `LICENSE`, `THIRD-PARTY-NOTICES.txt` |
| `GKMD-v4.9.0-windows-arm64.zip` | 18,355,046 bytes | Windows ARM64: `GKMD.dll` (19,389,952 bytes, embeds `USBip-0.9.8.1-arm64.exe`), `LICENSE`, `THIRD-PARTY-NOTICES.txt` |
| `GKMD-v4.9.0-linux-x64.zip` | 131,657 bytes | Linux x64 (`net10.0`, `linux-x64`): `GKMD.dll` (313,856 bytes, AnyCPU IL, no installer payload), `LICENSE`, `THIRD-PARTY-NOTICES.txt` |
| `GKMD-v4.9.0-linux-arm64.zip` | 131,666 bytes | Linux ARM64 (`net10.0`, `linux-arm64`): `GKMD.dll` (313,856 bytes, AnyCPU IL, no installer payload), `LICENSE`, `THIRD-PARTY-NOTICES.txt` |

SHA-256:

- `GKMD-v4.9.0-windows-x64.zip`: `DAA07017C13CADC68B346057449626B3DA0790D40C684FA4D9CE9675F541640C`
- `GKMD-v4.9.0-windows-arm64.zip`: `13B7CA46BCE1E9B214A99EBCDC6F9CF49457F93D7A10B95AD222661E52DA348F`
- `GKMD-v4.9.0-linux-x64.zip`: `B91FDEB740EF223E6393F03D434DBD4CFFDA07787CF1E77B4A6F5AA0BFABCD06`
- `GKMD-v4.9.0-linux-arm64.zip`: `BAE4C34D45C7A089E4A430E36867B89013F9F15DCAB6A514CB3A5625E869E55A`

Produced by the four `PackageRelease` commands above; Native AOT compilation
happens in the host application (GKME), not in this library.

### Build / verify

```powershell
dotnet build -c Release                          # both TFMs, 0 warnings
dotnet build -c Release -f net10.0 -r linux-x64
dotnet build -c Release -f net10.0 -r linux-arm64
dotnet build -c Release -f net10.0-windows -r win-x64
dotnet build -c Release -f net10.0-windows -r win-arm64
```

### Known limitations

- Linux attach requires root; there is no unprivileged helper.
- `UsbipReceiveMode` is Windows-only and ignored by the Linux build.
- The Linux zips contain the AnyCPU `GKMD.dll`, not a self-contained runtime;
  the host publishes its application with its own RID.
- One payload per Windows build: shipping the x64 DLL on Windows on Arm, or the
  ARM64 DLL on x64, is reported at install time rather than silently worked
  around.
- Layout (de)serialization uses reflection-based `System.Text.Json`; under a
  trimmed/AOT host the `GKLayout*` types must be preserved by the host.
- Owner records and transport-index leases, direct usbip-win2 host-controller
  installation and `HMUninstall` from 2.0.x remain unsynced (not applicable to
  GKMD's transport); the ARM64 driver files come from the upstream installer,
  not from embedded `usbip2_ude` files.

---

## v4.9.0 — 2026-11-01 — Linux 支持与 Windows on Arm

[English](#v490--2026-11-01--linux-support-and-windows-on-arm) | **简体中文**

GKMD 4.9.0 在同一个 AnyCPU 程序集上加入 Linux 支持与 Windows on Arm 支持。
发布包覆盖 Windows x64、Windows ARM64、Linux x64 与 Linux ARM64。两个 Windows
构建各自内嵌一份 usbip-win2 0.9.8.1 官方安装器；Linux 构建使用主线内核自带的
`vhci_hcd`，不携带安装器载荷。

### 亮点

#### Linux（内核 `vhci_hcd`）

- **多目标构建** —— `GKMD.csproj` 现在同时构建 `net10.0-windows`（usbip-win2、
  `GKMD_WINDOWS` 宏、内嵌安装器）与 `net10.0`（Linux）。宿主通过
  `ProjectReference` 自动选择对应构建，GKME 或其它调用方无需改源码或 API。
- **通过 sysfs 的托管 attach** —— `VhciClient.Linux.cs` 连接进程内 server，
  自己在托管代码中完成 `OP_REQ_IMPORT` 握手，再把已连接的 socket 以
  `port sockfd devid speed` 写入
  `/sys/devices/platform/vhci_hcd.0/attach` 交给内核。detach、status 以及
  `/var/run/vhci_hcd/port<N>` location 记录都与 `usbip` 工具一致，因此按
  location 拆除与死设备清扫无需改动。交棒前用 `fcntl` 清除 `O_NONBLOCK`，
  因为内核客户端要求阻塞 socket。
- **用 `modprobe` 取代安装器** —— `UsbipDriverInstaller.Linux.cs` 保持相同
  API（`EnsureInstalled`、`Install`、`IsInstalled`、`BundledArchitecture`）。
  `EnsureInstalled` 只探测，`vhci_hcd` 缺失时抛
  `UsbipInstallRequiredException`；`Install` 在已是 root 时直接执行
  `modprobe vhci-hcd`，非 root 的交互式安装则经 `pkexec` 提权。
- **按 uid 准入** —— Linux 的导入由用户态发起，不存在 System 进程的 socket。
  `TcpConnectionOwner.Linux.cs` 从 `/proc/net/tcp` 读取对端 uid，server 放行
  root 或本进程自身。
- **异步 detach** —— 断开由内核 hub 线程执行，因此 detach 会（有界地）等待
  端口回到 `VDEV_ST_NULL`（4），而不是期待同步 ioctl。Linux 没有 re-attach
  attempts 需要取消，`StopAttachAttempts` 为空操作。
- **端口 0 是真实端口** —— Linux 的 vhci 全局端口号从 0 开始（Windows 从 1
  开始），因此 `VhciClient.Detach` 只在参数为负时表示「全部端口」，
  `FindLocation` 在未找到时返回 -1。
- **可移植的进程内通道** —— `SharedMemoryIO` 不再使用命名 section 或命名
  Win32 event：两端现在总在同一进程内，因此改为 `NativeMemory.AllocZeroed`
  加托管 `AutoResetEvent`。帧路径上的最后一处 Win32 依赖被移除，Windows 与
  Linux 行为一致。
- **已在 WSL2 上验证** —— 在内核
  `6.6.87.2-microsoft-standard-WSL2` 的 Ubuntu 上，DualShock 4 与 Switch Pro
  人格经 `vhci_hcd` 完成枚举，`usbhid` 绑定其 HID 接口（DualShock 4 的音频
  接口由 `snd-usb-audio` 绑定），提交的状态产生了 evdev 轴 / 按键事件，
  detach 后所有端口回到 `VDEV_ST_NULL`，两个进程并发 attach 分别落在端口 0
  和 1 且互不干扰。测试可用 `tools/LinuxSmokeTest` 复现。

#### Windows on Arm

- **ARM64 载荷** —— `Resources/USBip-0.9.8.1-arm64.exe` 是上游 release 原始
  资源，未做任何修改（19,060,408 字节，SHA-256
  `CAB7FF97F79275EEB5C5B8BB2EB3111FF6B6C4EEAD07D9BEC9BE5BD1E3A35800`，与
  GitHub release API 公布的摘要一致）。与 x64 载荷一样，它本身是一个 32 位
  Inno Setup 程序，区别在于内部携带的驱动包是 ARM64 的 `usbip2_ude.sys`。
- **每个构建一份载荷** —— `GKMD.csproj` 用 `UsbipPayloadArch` 选择安装器：
  该属性在给出 `RuntimeIdentifier` 时跟随它（`dotnet build -r win-arm64`），
  否则默认为 x64。因此两个构建分别落在
  `bin\Release\net10.0-windows\`（x64）与 `...\win-arm64\`（ARM64）；若宿主
  在自己的工程文件里设置 RID，则需转发 `-p:UsbipPayloadArch=arm64`。取值
  只允许 `x64` / `arm64`，其他值会让构建直接失败。
- **按 OS 架构选择** —— 运行时 `UsbipDriverInstaller` 仍依据
  `RuntimeInformation.OSArchitecture` 而不是进程架构选择载荷：宿主 App 的
  x64 版本在 Windows on Arm 上以模拟方式运行，但内核只加载 ARM64 驱动，因此
  必须安装 ARM64 安装器。若随包的构建带的是另一份载荷，解包时会抛出明确
  异常，指出应该分发哪个构建，而不是运行一个注定失败的安装器。
  `UsbipDriverInstaller.BundledArchitecture` 可查询当前构建携带的架构。
- **逐载荷校验** —— 每个架构各自携带 release API 公布的 SHA-256，解包后校验；
  不匹配时仍会删除文件并抛异常，而不会执行。解包目录按架构区分
  （`%TEMP%\GKMD_usbip_0.9.8.1_arm64`）。
- **vHCI 布局不变** —— IOCTL 结构中没有指针宽度字段，0.9.8.1 ARM64 驱动同样
  支持 **0.9.8.1 / 0.9.8.0 / 0.9.7.x** 三套请求布局，原有探测逻辑照常识别
  已安装驱动的布局。`pnputil`、CfgMgr 与 IPHLPAPI 的 owner 检查均与架构无关
  （TCP owner 行在所有 64 位目标上都是 24 字节）。

### 兼容性

- 无公开 API 或 profile 格式变化；各手柄人格不变。
- Windows：兼容已安装的 usbip-win2 **0.9.7.x、0.9.8.0、0.9.8.1**，x64 与
  ARM64 均可。
- Linux：任意带 `vhci_hcd` 的主线内核；attach 需要 root，发行版自带的
  `usbip` 工具可用 `--tcp-port <server 端口>` 手动连接。
- 线上字节与共享输入 section 的字节不变。
- `GKOemNameOverride` 在 Linux 上为空操作：手柄 OEM 名称来自 Windows 注册表
  数据库，Linux 没有对应物。

### 产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `GKMD-v4.9.0-windows-x64.zip` | 25,426,898 字节 | Windows x64：`GKMD.dll`（26,413,056 字节，内嵌 `USBip-0.9.8.1-x64.exe`）、`LICENSE`、`THIRD-PARTY-NOTICES.txt` |
| `GKMD-v4.9.0-windows-arm64.zip` | 18,355,046 字节 | Windows ARM64：`GKMD.dll`（19,389,952 字节，内嵌 `USBip-0.9.8.1-arm64.exe`）、`LICENSE`、`THIRD-PARTY-NOTICES.txt` |
| `GKMD-v4.9.0-linux-x64.zip` | 131,657 字节 | Linux x64（`net10.0`、`linux-x64`）：`GKMD.dll`（313,856 字节，AnyCPU IL，无安装器载荷）、`LICENSE`、`THIRD-PARTY-NOTICES.txt` |
| `GKMD-v4.9.0-linux-arm64.zip` | 131,666 字节 | Linux ARM64（`net10.0`、`linux-arm64`）：`GKMD.dll`（313,856 字节，AnyCPU IL，无安装器载荷）、`LICENSE`、`THIRD-PARTY-NOTICES.txt` |

SHA-256：

- `GKMD-v4.9.0-windows-x64.zip`：`DAA07017C13CADC68B346057449626B3DA0790D40C684FA4D9CE9675F541640C`
- `GKMD-v4.9.0-windows-arm64.zip`：`13B7CA46BCE1E9B214A99EBCDC6F9CF49457F93D7A10B95AD222661E52DA348F`
- `GKMD-v4.9.0-linux-x64.zip`：`B91FDEB740EF223E6393F03D434DBD4CFFDA07787CF1E77B4A6F5AA0BFABCD06`
- `GKMD-v4.9.0-linux-arm64.zip`：`BAE4C34D45C7A089E4A430E36867B89013F9F15DCAB6A514CB3A5625E869E55A`

分别由上文四条 `PackageRelease` 命令生成；Native AOT 编译在宿主应用（GKME）
中完成，而非本类库。

### 构建 / 验证

```powershell
dotnet build -c Release                          # 两个目标，0 警告
dotnet build -c Release -f net10.0 -r linux-x64
dotnet build -c Release -f net10.0 -r linux-arm64
dotnet build -c Release -f net10.0-windows -r win-x64
dotnet build -c Release -f net10.0-windows -r win-arm64
```

### 已知限制

- Linux 的 attach 需要 root，没有非特权 helper。
- `UsbipReceiveMode` 仅适用于 Windows，Linux 构建忽略它。
- Linux zip 内是 AnyCPU `GKMD.dll`，不是自包含运行时；宿主需按自己的 RID
  发布应用。
- 每个 Windows 构建只有一份载荷：把 x64 DLL 发到 Windows on Arm（或把 ARM64
  DLL 发到 x64）会在安装时被明确报错，而不是被静默绕过。
- layout 序列化 / 反序列化使用基于反射的 `System.Text.Json`；在被裁剪的 AOT
  宿主中，`GKLayout*` 类型须由宿主保留。
- 2.0.x 的 owner record 与 transport index lease、直接安装 usbip-win2 host
  controller 与 `HMUninstall` 仍未同步（不适用于 GKMD 的传输层）；ARM64 驱动
  文件来自上游安装器，而不是内嵌的 `usbip2_ude` 文件。

---

## v4.8.0 — 2026-10-31 — HIDMaestro 2.0.x allocation-free USB/IP and submit paths

**English** | [简体中文](#v480--2026-10-31--同步-hidmaestro-20x-零分配-usbip-与-submit-路径)

GKMD 4.8.0 takes the performance-only work issue #1 deferred: the USB/IP
transfer, frame and control paths no longer allocate per transfer, audio URBs
and frame buffers are pooled, and the input-section write and the submit paths
do less work per frame.

### Highlights

- **Frames** — the input pump builds each report in its own buffer; queued
  frames come from a pool and go back as soon as a read answers them or the
  queue drops them; Switch and GIP frames are built in place,
  `GET_REPORT(Input)` copies the latest report from under the HID lock, and a
  non-byte-aligned report field of up to 32 bits goes in with one masked
  read-modify-write instead of one pass per bit.
- **Transfers** — the command reader owns a 4 KB receive buffer and reuses the
  arrays for a submit's OUT data, isochronous descriptors and packets; audio
  URBs are pooled with their packet and PCM arrays reused, and the microphone
  completion fills one buffer the engine keeps.
- **Control replies** — descriptors, status, configuration, interface, UAC
  values, test-command pages, the resolution multiplier and the Sony feature
  stubs are answered from the stack or a device-owned buffer; the vendor
  request table's hex answers are decoded once at construction.
- **Submit path** — `WriteInputFrame` stores through the section's pointer,
  takes spans, orders the seqlock with one write barrier plus a release store,
  and keeps the `SetEvent` doorbells out of line; `SubmitRawReport` writes a
  frame with no fixed bytes straight from the caller's span; `SubmitState`
  resolves the six simple slots only for the extended codec and the XUSB GIP
  block, letting the descriptor path read the axes itself.

### Compatibility

- No public API or profile format change; controller personas are unchanged.
- Works against an installed usbip-win2 **0.9.7.x, 0.9.8.0 or 0.9.8.1**.
- The bytes on the wire and in the shared input section are unchanged.

### Artifact

| File | Size | Notes |
| --- | --- | --- |
| `GKMD-v4.8.0.zip` | 25,427,246 bytes | Contains `GKMD.dll` (Release build, IL, `net10.0-windows`, AnyCPU, embedded usbip-win2 0.9.8.1), `LICENSE` and `THIRD-PARTY-NOTICES.txt` |

SHA-256: `458620BD08D48C4B3702F9298F58342585788E6E8D01354BCD03CE149306BA9E`

Produced by `dotnet build -c Release -t:PackageRelease`; Native AOT compilation
happens in the host application (GKME), not in this library.

### Known limitations

- The bundled usbip-win2 payload is x64-only.
- Windows only (`net10.0-windows`).
- Layout (de)serialization uses reflection-based `System.Text.Json`; under a
  trimmed/AOT host the `GKLayout*` types must be preserved by the host.
- Owner records and transport-index leases, direct usbip-win2 host-controller
  installation and ARM64 payloads from 2.0.x remain unsynced (not applicable to
  GKMD's transport).

---

## v4.8.0 — 2026-10-31 — 同步 HIDMaestro 2.0.x 零分配 USB/IP 与 submit 路径

[English](#v480--2026-10-31--hidmaestro-20x-allocation-free-usbip-and-submit-paths) | **简体中文**

GKMD 4.8.0 接过 issue #1 中暂缓的纯性能工作：USB/IP 的传输、帧与 control
路径不再逐次分配，audio URB 与帧缓冲改为池化，输入 section 的写入与 submit
路径每帧做的工作也更少。

### 亮点

- **帧** — 输入泵在自己的缓冲里构建每一份报文；排队帧来自缓冲池，被读取应答
  或队列淘汰后立即归还；Switch 与 GIP 帧就地构建；`GET_REPORT(Input)` 在 HID
  锁内复制最新报文；不足一字节对齐、最多 32 位的报文字段改为一次掩码读改写，
  而不是逐位循环。
- **传输** — 命令 reader 自带 4 KB 接收缓冲，并复用 OUT 数据、等时描述符与包
  数组；audio URB 池化，包与 PCM 数组复用；麦克风完成时填充引擎保留的一个
  缓冲。
- **control 应答** — 描述符、状态、配置、接口、UAC 值、test command 分页、
  resolution multiplier 与 Sony feature stub 均从栈或设备自有缓冲发出；vendor
  请求表的十六进制应答在构造时解码一次。
- **submit 路径** — `WriteInputFrame` 通过 section 指针写入、接受 span，seqlock
  只用一个 write barrier 加一次 release store，`SetEvent` 门铃移出内联；
  `SubmitRawReport` 对没有固定字节的帧直接从调用方 span 写入；`SubmitState`
  只在扩展 codec 与 XUSB GIP 块真正使用时才解析六个 simple slot，描述符路径由
  `BuildReportInto` 自己读取轴值。

### 兼容性

- 无公开 API 或 profile 格式变化；各手柄人格不变。
- 兼容已安装的 usbip-win2 **0.9.7.x、0.9.8.0、0.9.8.1**。
- 线上字节与共享输入 section 的字节不变。

### 产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `GKMD-v4.8.0.zip` | 25,427,246 字节 | 内含 `GKMD.dll`（Release 构建、IL、`net10.0-windows`、AnyCPU，内嵌 usbip-win2 0.9.8.1）、`LICENSE` 与 `THIRD-PARTY-NOTICES.txt` |

SHA-256：`458620BD08D48C4B3702F9298F58342585788E6E8D01354BCD03CE149306BA9E`

由 `dotnet build -c Release -t:PackageRelease` 生成；Native AOT 编译在宿主应用
（GKME）中完成，而非本类库。

### 已知限制

- 内嵌的 usbip-win2 载荷仅支持 x64。
- 仅支持 Windows（`net10.0-windows`）。
- layout 序列化/反序列化使用基于反射的 `System.Text.Json`；在被裁剪的 AOT 宿主中，
  `GKLayout*` 类型须由宿主保留。
- 2.0.x 的 owner record 与 transport index lease、直接安装 usbip-win2 host
  controller、ARM64 载荷仍未同步（不适用于 GKMD 的传输层）。

---

## v4.7.0 — 2026-10-24 — HIDMaestro 2.0.3 runtime fixes

**English** | [简体中文](#v470--2026-10-24--同步-hidmaestro-203-运行时修复)

GKMD 4.7.0 syncs the USB/IP runtime fixes that landed in HIDMaestro 2.0.0–2.0.3,
gives the server its own listen-port range so GKMD and HIDMaestro can run side
by side, and fixes a bugcheck when creating an Xbox One persona.

### Highlights

- **HIDMaestro 2.0.3 transport sync** — `Dispose` wakes the input pump instead
  of waiting out its 500 ms frame wait; only the host controller's connection
  (System, PID 4, found via `GetExtendedTcpTable`) may import a device; a
  connection has 5 s to finish its handshake and at most 16 handshakes run at
  once; `GET_REPORT(Input)` STALLs a report ID the descriptor does not declare;
  the claim is identity-checked on release and teardown survives a race with
  the host's detach; detach is by location, since a vhci port may be reused;
  and audio control/stream events are raised on a dedicated thread with a
  throwing handler logged and dropped.
- **Coexistence** — the server now listens on **37286..37301**, clear of
  HIDMaestro's `18509..18524` / `28509..28524` and FlexInput's `18008`. The
  stale sweep inspects only its own range and detaches only imports whose
  server is gone, so neither program takes the other's imports or cancels its
  re-attach attempts. When the range is full it falls back to an OS-assigned
  loopback port.
- **Xbox One persona bugcheck** — the GIP metadata blob is rebuilt at 230 bytes
  from the real 214-byte gamepad metadata, appending the PC security opt-out as
  a fourth interface and recomputing the device / total / client-command
  offsets; `InputReportSize` is corrected from 18 to 14. The previous 182-byte
  sample metadata made `xboxgip` bugcheck on create.

### Compatibility

- Controller personas are unchanged (Xbox One / Series X|S, Xbox 360,
  Switch Pro / Joy-Con, DualShock 4, DualSense, wheel, keyboard, mouse).
- Works against an installed usbip-win2 **0.9.7.x, 0.9.8.0 or 0.9.8.1**.
- Runs alongside HIDMaestro 2.0.x and FlexInput; the port ranges do not overlap.

### Artifact

| File | Size | Notes |
| --- | --- | --- |
| `GKMD-v4.7.0.zip` | 25,424,258 bytes | Contains `GKMD.dll` (Release build, IL, `net10.0-windows`, AnyCPU, embedded usbip-win2 0.9.8.1), `LICENSE` and `THIRD-PARTY-NOTICES.txt` |

SHA-256: `C223A37D51A703046AD72D07016B185FFE4A1EB18A0ABAAB7CF4451654364460`

Produced by `dotnet build -c Release -t:PackageRelease`; Native AOT compilation
happens in the host application (GKME), not in this library.

### Build / verify

```powershell
dotnet build -c Release
dotnet build -c Release -t:PackageRelease
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # from a host project referencing GKMD.csproj
```

### Known limitations

- The bundled usbip-win2 payload is x64-only.
- Windows only (`net10.0-windows`).
- Layout (de)serialization uses reflection-based `System.Text.Json`; under a
  trimmed/AOT host the `GKLayout*` types must be preserved by the host.
- The zero-allocation frame / output / control paths, owner records and
  transport-index leases, direct usbip-win2 host-controller installation and
  ARM64 payloads from 2.0.x are not synced (performance-only or not applicable
  to GKMD's transport).

### Credits & license

Derived from HIDMaestro 1.5.1 (MIT); the original copyright notice is retained
in [LICENSE](LICENSE). Bundled usbip-win2 is BSD-2-Clause, see
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt).

---

## v4.7.0 — 2026-10-24 — 同步 HIDMaestro 2.0.3 运行时修复

[English](#v470--2026-10-24--hidmaestro-203-runtime-fixes) | **简体中文**

GKMD 4.7.0 同步 HIDMaestro 2.0.0–2.0.3 中适用于 USB/IP 架构的运行时修复，为 server
分配独立的监听端口段，使 GKMD 与 HIDMaestro 可以并存，并修复创建 Xbox One 人格时的
蓝屏。

### 亮点

- **HIDMaestro 2.0.3 传输层同步** — `Dispose` 唤醒输入泵，不再等满 500 ms 的帧等待；
  只有宿主控制器连接（System 进程 PID 4，经 `GetExtendedTcpTable` 判定）才能导入
  设备；连接须在 5 秒内完成握手，同时最多 16 个握手；`GET_REPORT(Input)` 对描述符
  未声明的 report ID 返回 STALL；claim 在释放时做身份校验，teardown 与宿主 detach
  竞态不再结束进程；按 location 拆除（vhci 端口会被复用）；音频 control/stream 事件
  在专用线程上触发，handler 抛异常只记录并丢弃。
- **并存** — server 监听 **37286..37301**，避开 HIDMaestro 的 `18509..18524` /
  `28509..28524` 与 FlexInput 的 `18008`。死服务清扫只检查自己的端口段，只拆除
  server 已消失的 import，因此两者不会互相接管 import 或取消对方的 re-attach
  attempts。该段用满时回退到 OS 分配的 loopback 端口。
- **Xbox One 人格蓝屏** — GIP 元数据 blob 以真实手柄的 214 字节 gamepad 元数据为基础
  重建为 230 字节，把 PC security opt-out 追加为第 4 个 interface 并重算 device /
  total / client-command 偏移；`InputReportSize` 从 18 修正为 14。此前的 182 字节
  示例元数据会让 `xboxgip` 在创建设备时 bugcheck。

### 兼容性

- 各手柄人格不变（Xbox One / Series X|S、Xbox 360、Switch Pro / Joy-Con、
  DualShock 4、DualSense、方向盘、键盘、鼠标）。
- 兼容已安装的 usbip-win2 **0.9.7.x、0.9.8.0、0.9.8.1**。
- 可与 HIDMaestro 2.0.x 及 FlexInput 并存；端口段不重叠。

### 产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `GKMD-v4.7.0.zip` | 25,424,258 字节 | 内含 `GKMD.dll`（Release 构建、IL、`net10.0-windows`、AnyCPU，内嵌 usbip-win2 0.9.8.1）、`LICENSE` 与 `THIRD-PARTY-NOTICES.txt` |

SHA-256：`C223A37D51A703046AD72D07016B185FFE4A1EB18A0ABAAB7CF4451654364460`

由 `dotnet build -c Release -t:PackageRelease` 生成；Native AOT 编译在宿主应用
（GKME）中完成，而非本类库。

### 构建 / 验证

```powershell
dotnet build -c Release
dotnet build -c Release -t:PackageRelease
dotnet publish -c Release -r win-x64 -p:PublishAot=true   # 在引用 GKMD.csproj 的宿主工程中执行
```

### 已知限制

- 内嵌的 usbip-win2 载荷仅支持 x64。
- 仅支持 Windows（`net10.0-windows`）。
- layout 序列化/反序列化使用基于反射的 `System.Text.Json`；在被裁剪的 AOT 宿主中，
  `GKLayout*` 类型需由宿主保留。
- 2.0.x 中的零分配 frame / output / control 路径、owner record 与 transport-index
  lease、直接安装 usbip-win2 host controller 以及 ARM64 载荷未同步（仅性能优化或不
  适用于 GKMD 的传输层）。

### 致谢与许可

派生自 HIDMaestro 1.5.1（MIT），原始版权声明保留在 [LICENSE](LICENSE)；
捆绑的 usbip-win2 为 BSD-2-Clause，见
[Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt)。

---

## v4.6.1 — 2026-10-17 — HIDMaestro 1.12.0 fixes

**English** | [简体中文](#v461--2026-10-17--同步-hidmaestro-1120-修复)

GKMD 4.6.1 syncs the USB/IP-relevant fixes that landed in HIDMaestro after
1.5.1, up to 1.12.0, and follows GKME 4.6.1.

### Highlights

- **Audio stream inference** — a usbip-win2 0.9.8.1 host controller driver
  paired with an older filter driver never forwards `SET_INTERFACE`, while the
  audio itself flows. An interface that has never received `SET_INTERFACE` is
  now opened by its first isochronous transfer and closed about 200 ms after
  the transfers stop; once the host delivers a `SET_INTERFACE`, only that
  decides the stream state.
- **Stable per-controller serials** — a captured serial varies by controller
  index, and personas without one get a stable synthetic serial. Windows keys
  the USB instance id on the serial instead of the vhci port, so a device comes
  back at the same path across dispose/recreate, process restart and reboot.
- **DualShock 4 reports** — report `0x01` fields sit at the SDL/Linux offsets,
  the sensor timestamp advances in 16/3 µs ticks, battery/full/cable and touch
  fields follow the real pad, `LT_DIGITAL` / `RT_DIGITAL` follow the 8-bit
  trigger value, and calibration is the identity (8192/g, 16/°/s) with the DS4
  `0x05` ordering.

### Compatibility

- Controller personas are unchanged (Xbox One / Series X|S, Xbox 360,
  Switch Pro / Joy-Con, DualShock 4, DualSense, wheel, keyboard, mouse).
- Works against an installed usbip-win2 **0.9.7.x, 0.9.8.0 or 0.9.8.1**.

### Artifact

| File | Size | Notes |
| --- | --- | --- |
| `GKMD-v4.6.1.zip` | 25,420,883 bytes | Contains `GKMD.dll` (Release build, IL, `net10.0-windows`, AnyCPU, embedded usbip-win2 0.9.8.1), `LICENSE` and `THIRD-PARTY-NOTICES.txt` |

SHA-256: `D306D60EDDA7F43822A2590F3476AD80E6D766D39EAC747956D054A0979A09E8`

Produced by `dotnet build -c Release -t:PackageRelease`; Native AOT compilation
happens in the host application (GKME), not in this library.

### Build / verify

```powershell
dotnet build -c Release
dotnet build -c Release -t:PackageRelease
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

## v4.6.1 — 2026-10-17 — 同步 HIDMaestro 1.12.0 修复

[English](#v461--2026-10-17--hidmaestro-1120-fixes) | **简体中文**

GKMD 4.6.1 同步 HIDMaestro 在 1.5.1 之后至 1.12.0 中适用于 USB/IP 架构的修复，
跟随 GKME 4.6.1 发布。

### 亮点

- **音频流推断** — usbip-win2 0.9.8.1 主控驱动搭配旧 filter 驱动时收不到
  `SET_INTERFACE`，但音频本身仍在传输。从未收到 `SET_INTERFACE` 的接口现在由首次
  等时传输开启，并在传输停止约 200 ms 后关闭；一旦宿主送达 `SET_INTERFACE`，流状态
  只由它决定。
- **每控制器稳定 serial** — 已捕获的 serial 按控制器索引变化，无 serial 的 persona
  生成稳定的合成 serial。Windows 以 serial 而非 vhci 端口作为 USB 实例 ID，设备在
  销毁重建、进程重启与重启系统后回到同一路径。
- **DualShock 4 报文** — `0x01` 各字段落在 SDL/Linux 读取的偏移，传感器时间戳按
  16/3 µs 递增，电量/充满/cable 与触摸字段符合真实手柄，`LT_DIGITAL` /
  `RT_DIGITAL` 按 8 位触发值判定，校准为单位校准（8192/g、16/°/s）并提供 DS4
  `0x05` 顺序。

### 兼容性

- 各手柄人格不变（Xbox One / Series X|S、Xbox 360、Switch Pro / Joy-Con、
  DualShock 4、DualSense、方向盘、键盘、鼠标）。
- 兼容已安装的 usbip-win2 **0.9.7.x、0.9.8.0、0.9.8.1**。

### 产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `GKMD-v4.6.1.zip` | 25,420,883 字节 | 内含 `GKMD.dll`（Release 构建、IL、`net10.0-windows`、AnyCPU，内嵌 usbip-win2 0.9.8.1）、`LICENSE` 与 `THIRD-PARTY-NOTICES.txt` |

SHA-256：`D306D60EDDA7F43822A2590F3476AD80E6D766D39EAC747956D054A0979A09E8`

由 `dotnet build -c Release -t:PackageRelease` 生成；Native AOT 编译在宿主应用
（GKME）中完成，而非本类库。

### 构建 / 验证

```powershell
dotnet build -c Release
dotnet build -c Release -t:PackageRelease
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
| `GKMD-v4.5.2.zip` | 25,419,331 bytes | Contains `GKMD.dll` (Release build, IL, `net10.0-windows`, AnyCPU, embedded usbip-win2 0.9.8.1), `LICENSE` and `THIRD-PARTY-NOTICES.txt` |

SHA-256: `60EF842F3DDEC9F13327E47CDCE675485017BD6940AAFDC6CD63B86494A1B8A7`

Produced by `dotnet build -c Release -t:PackageRelease`; Native AOT compilation
happens in the host application (GKME), not in this library.

### Build / verify

```powershell
dotnet build -c Release
dotnet build -c Release -t:PackageRelease
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
| `GKMD-v4.5.2.zip` | 25,419,331 字节 | 内含 `GKMD.dll`（Release 构建、IL、`net10.0-windows`、AnyCPU，内嵌 usbip-win2 0.9.8.1）、`LICENSE` 与 `THIRD-PARTY-NOTICES.txt` |

SHA-256：`60EF842F3DDEC9F13327E47CDCE675485017BD6940AAFDC6CD63B86494A1B8A7`

由 `dotnet build -c Release -t:PackageRelease` 生成；Native AOT 编译在宿主应用
（GKME）中完成，而非本类库。

### 构建 / 验证

```powershell
dotnet build -c Release
dotnet build -c Release -t:PackageRelease
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
