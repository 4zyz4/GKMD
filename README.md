# GKMD

**English** | [简体中文](README.zh-CN.md)

GKMD is a standalone Windows virtual USB game controller engine (class library) that emulates USB devices in user mode through the virtual host controller (vHCI) of [usbip-win2](https://github.com/vadimgrn/usbip-win2), presenting a real hardware identity to the kernel-facing HID / XInput / DirectInput / SDL / Gamepad APIs.

## Attribution

GKMD **is modified from [hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) 1.5.1** (tag `v1.5.1`, MIT License). It was extracted from HIDMaestro's `sdk/HIDMaestro.Core` and extensively trimmed, rewritten, and extended toward a "pure USB/IP backend + fixed built-in profiles" design.

- Upstream project: https://github.com/hifihedgehog/HIDMaestro
- Origin version: `v1.5.1`
- **Latest upstream compared: `v2.0.3`** (2026-10-22), the current release. It has moved device behavior into JSON profiles with compiled programs, and ships identity keys, automatic transport installation, uninstall, VR and ARM64 payloads; it also changed the public API, so 1.x programs must migrate to 2.0.
- Upstream license: MIT License (see `LICENSE`; GKMD retains the original copyright notice)
- Namespaces: `HIDMaestro` / `HIDMaestro.Internal` / `HIDMaestro.Internal.Usbip`
  → `GKMD` / `GKMD.Internal` / `GKMD.Internal.Usbip`
- Type / file prefix: `HM` (short for HIDMaestro) → `GK`, e.g. `HMContext` → `GKContext`,
  `HMController` → `GKController`, `HMContext.cs` → `GKContext.cs`, `HMLayout.cs` → `GKLayout.cs`
- Runtime identifiers: `Global\HIDMaestro*`, `HIDMAESTRO_TIMEOUT_SCALE`, `HKLM\SOFTWARE\HIDMaestro*`
  → `Global\GKMD*`, `GKMD_TIMEOUT_SCALE`, `HKLM\SOFTWARE\GKMD*`

> Note: GKMD is the standalone product of a driver branch that evolved over the long term within the GKME project. It is **not** a line-by-line simplified single-file copy of HIDMaestro 1.5.1. This README compares GKMD against the **latest upstream, v2.0.3**, unless a row explicitly says "1.5.1 base". Because GKMD stays on the 1.5.1-derived API, it lacks the upstream work from 1.6.0 through 2.0.3 that builds on the 2.0 data model; its USB/IP runtime fixes have been synced through 2.0.3.

## Differences from HIDMaestro 2.0.3

### 1. Transport / driver architecture

| Dimension | HIDMaestro 2.0.3 | GKMD |
| --- | --- | --- |
| Controller creation path | Branches on `RequiresUsbipBackend`: ordinary profiles use UMDF2 + PnP, composite personas use USB/IP | **No branching; everything goes through USB/IP** (`CreateController` / `CreateControllerAt` both call `CreateUsbipController`) |
| Driver model | UMDF2 user-mode driver `HIDMaestro.dll` + XUSB companion + INF / PnP for ordinary profiles; usbip-win2's host controller for composite personas. No kernel-mode driver | Pure USB/IP: usbip-win2 virtual host controller + vHCI, **no UMDF2 driver, no INF, no PnP device node** |
| Device node | UMDF2 path registers a root-enumerated / SWD PnP devnode and writes friendly name / ControllerIndex; composite path is a vhci USB device | No PnP devnode; device strings come directly from USB descriptors |
| Transport installation | Since 2.0.1 the SDK embeds the unmodified `usbip2_ude.inf` / `.sys` / `.cat` for x64 and ARM64 and registers the host controller devnode itself (`InstallHostController`); it never runs usbip-win2's installer and never installs its root-hub filter, and it records what it installed | `UsbipDriverInstaller` embeds and runs the official x64 `USBip-0.9.8.1-x64.exe` (Inno) installer |
| usbip-win2 version | 0.9.8.1, x64 + ARM64 payloads | 0.9.8.1 installer payload (x64); `VhciClient` speaks the **0.9.8.1 / 0.9.8.0 / 0.9.7.x** request layouts and probes the installed driver for the one it has |
| USB/IP receive mode | Always the IRP receive path (`wsk_events` left false); no selectable mode | Adds `UsbipReceiveMode.LowLatency` (default) / `ZeroCopy` on 0.9.8.x; 0.9.7.x always uses the MDL path |
| Installation policy | Silently installs the bundled transport on the first composite create; `InstallDriver()` does the same for UMDF2 | **Probe only**; throws `UsbipInstallRequiredException` / `UsbipRebootRequiredException` when missing/outdated, leaving installation to the host app |
| Audio engine | Composite personas carry USB Audio over USB/IP; the SDK installs the transport for them | `UsbAudioEngine?`, created only when the profile declares a USB Audio streaming interface |
| Controller index / identity release | Lease + reservation system, identity keys, multi-program coexistence (a keyless 1.12.0 program lands at transport index 64+) | Tears down the USB/IP device first, then releases the index (avoids port reuse races during fast switching) |
| Uninstall | `HMUninstall` / `HIDMaestroUninstall.exe` removes controllers, driver packages, certificate, registry keys and files; 2.0.3 can also remove usbip-win2 and its root-hub filter | No uninstall API or tool; the host app handles teardown (`UsbipBackend.DetachAllOwned`) |
| Architecture | One AnyCPU DLL carrying driver and transport payloads for x64 and ARM64 | AnyCPU managed DLL; the embedded usbip-win2 payload is x64-only |
| Diagnostic log | `DeviceOrchestrator.LogDiag` | Replaced with `Debug.WriteLine` |

### 2. Profiles and device behavior

| Dimension | HIDMaestro 2.0.3 | GKMD |
| --- | --- | --- |
| Profile source | 337 built-in profiles (103 keyboards and mice) linked from `profiles/**/*.json`; custom and extracted profiles load at runtime; strict source-generated JSON | 10 personas constructed in code by `StaticProfileRegistry.cs`; **no JSON catalog, no profile extraction** |
| Profile validation | Strict schema; a refused file is reported in `HMContext.ProfileLoadIssues` with its JSON path; code-built profiles throw `HMProfileException` with `ProfileId` / `JsonPath` | C# compile-time construction; no schema or JSON-path diagnostics |
| Device behavior | Devices are data: report fields, feature answers, protocols and report programs are profile keys, with `deviceModelFrom` supplying defaults; catalog programs compile to code, other profiles run the same programs on an interpreter | Fixed C# builders (`HidDescriptorBuilder`, `HidReportBuilder`, `VendorBlobCodec`) and hand-written responders |
| Multi-controller devices | `HMPort` / `HMPortSet` for adapters, multitaps and receivers that carry several controllers in one report | None |
| Device identity | `identityKey` + leases keep parent / child / interface path / DirectInput GUID / SDL path / container id / serial across dispose, restart, reboot and driver upgrade | A serial varies by controller index; profiles without one get a stable synthetic serial |
| Persona coverage | 337 profiles: Xbox 360 family, Xbox One / Series (UMDF2), DualShock 3 / 4, DualSense, Switch Pro / Joy-Con / Switch 2 Pro, Valve Steam Controller / Steam Deck, VR controllers, wheels, keyboards and mice, Wii U GameCube adapter, PS Move, etc. | 10 personas: Xbox 360, Xbox One, Xbox Series X\|S, DualShock 4 v2, DualSense, Switch Pro, Joy-Con L/R, keyboard, mouse |
| Xbox One / Series transport | Presented through UMDF2 with the Windows `xinputhid` view; the USB wire protocol is not GIP | Implemented as a **GIP vendor-class device** over USB/IP (`045E:02EA` / `045E:0B12`), including the `[MS-GIPUSB]` envelope |
| Switch | Switch Pro / Joy-Con / Switch 2 Pro over Bluetooth and USB descriptors, protocol data in the profile | Switch Pro / Joy-Con over USB with a code-side responder |
| Keyboards / mice | `SubmitKeyboard` / `SubmitMouse` / `SubmitConsumer`, LED state, absolute mice, 103 real-device profiles | Two simple built-in personas used by GKME |
| VR | `HMVR` / `HMVRController` and an OpenVR driver package | None |
| Sony device answers | Served from profile data (firmware info, pairing info, calibration), with Bluetooth CRC-32 | Code-side DS4 authentication / serial reports and DualSense test-command handler |

### 3. GKMD-specific capabilities

| Capability | Related files | Description |
| --- | --- | --- |
| GIP over USB/IP | `Usbip/GipProtocol.cs`, `Usbip/GipResponder.cs`, `Usbip/GipLog.cs` | Xbox One (`045E:02EA`) and Xbox Series X\|S (`045E:0B12`) are vendor-class USB devices answering the full GIP handshake (HELLO / announce / identify / status), 14-byte `gip_gamepad_pkt_input`, guide virtual key, and host commands (power / LED / rumble / identify). Xbox One starts from the real 214-byte gamepad metadata and appends the PC security opt-out as a fourth interface (keeping `INavigationController`), producing a 230-byte blob; the previous 182-byte sample metadata made `xboxgip` bugcheck on create |
| Selectable USB/IP receive mode | `Usbip/VhciClient.cs`, `Usbip/UsbipBackend.cs` | `UsbipReceiveMode.LowLatency` (default, `wsk_events`) / `ZeroCopy` (MDL) on 0.9.8.x |
| Probe-only installation | `Usbip/UsbipInstallRequiredException.cs`, `Usbip/UsbipRebootRequiredException.cs` | The library never installs a driver behind the host's back; the host UI turns "needs install / needs reboot" into a prompt |
| Code-defined profile registry | `StaticProfileRegistry.cs` | Built-in personas are ordinary C# and carry no JSON or reflection dependency |
| Code-side Sony handlers | `Usbip/SonyTestCommandHandler.cs`, `Usbip/UsbipEmulatedDevice.cs` | DualSense test commands (torch/telemetry, Type2 tracability, BT MAC/patch, battery voltage) and DualShock 4 `0x20` / `0x81` feature reports (`0x03030301` challenge response) |
| High-resolution wheel | `UsbConfigurationSpec.ResolutionMultiplier` | Answers `GET/SET_REPORT(Feature, 0)` and enumerates at high resolution |
| AOT-friendly library | `GKMD.csproj`, `GKLayoutLoader.cs` | `IsAotCompatible=true`; only the reflection-based layout JSON path is locally suppressed |
| GKME integration | `AssemblyInfo.cs` | Linked by [GKME](../GKME-Windows) through `ProjectReference`; `InternalsVisibleTo("GKME")` exposes the transport installer |

### 4. Upstream work GKMD has not adopted (1.6.0 → 2.0.3)

Only fixes that apply to the pure USB/IP design and that existed through 1.12.0 were synced. The following upstream work is not present:

- **1.6.0 – 1.7.3**: virtual VR controllers and the OpenVR driver, Valve personas (Steam Controller, Steam Deck), the Steam Controller 2 raw report, WGI gamepad presentation for the Xbox 360 family, legacy `GetControllerState`, native binary stamping.
- **1.8.0 – 1.12.0**: the identity / audio / DualShock 4 fixes (the USB/IP-relevant ones **are** synced; see below), the Switch 2 Pro with motion (1.11.0), 103 keyboard/mouse profiles and the keyboard/mouse API (1.12.0), and other catalog and persona work.
- **2.0.0**: devices-as-data report fields, feature answers, protocols and report programs; the `ProgramForm` compiler / interpreter; `deviceModelFrom` models; strict profile validation with JSON paths; `HMPort`; identity keys and leases; `HMBuildInfo`; NativeAOT support; ARM64 driver/transport payloads; the Wii U GameCube adapter; the PS Move and DualShock 3 data forms; the 337-profile catalog.
- **2.0.1 – 2.0.3**: direct installation of usbip-win2's host controller files without running its installer, `HMUninstall` / `HIDMaestroUninstall.exe`, removal of the usbip-win2 host controller and root-hub filter, and the Valve persona corrections.

The **USB/IP runtime** fixes from 2.0.0 *are* synced (host-controller-only imports, handshake limits, dead-server sweeps, location-based detach, disposal and audio-event hardening; see the sync list in section 7). What GKMD does not carry is the 2.0 data model, the identity/lease system, the 2.0.1–2.0.3 installer/uninstaller, and the perf-only allocation work.

GKMD instead keeps the official usbip-win2 installer, the probe-only policy, the code-defined static registry and the 1.x-derived controller API.

### 5. Build / project differences

| Dimension | HIDMaestro 2.0.3 | GKMD |
| --- | --- | --- |
| Output | Class library `HIDMaestro.Core.dll` | Class library **`GKMD.dll`** |
| TargetFramework | `net10.0-windows10.0.26100.0` | `net10.0-windows` |
| Platform | AnyCPU | AnyCPU |
| Namespaces | `HIDMaestro` / `.Internal` / `.Internal.Usbip` | `GKMD` / `.Internal` / `.Internal.Usbip` |
| Directory structure | `sdk/HIDMaestro.Core/` + `Internal/` + `Internal/Usbip/` + `ProgramForm/` + `Internal/Uninstall/` | Flattened to the root + the `Usbip/` subdirectory |
| Driver resources | Embeds UMDF2 driver + XUSB companion + SWD helper + INFs + signing/catalog tools + OpenVR driver + `usbip2_ude` files, for x64 and ARM64 | **Embeds no kernel or UMDF2 driver and no signing/catalog tools**; embeds only `Resources/USBip-0.9.8.1-x64.exe` and `THIRD-PARTY-NOTICES.txt` |
| usbip-win2 acquisition | Build-time `prepare_usbip.ps1` unpacks the pinned 0.9.8.1 installers into per-architecture host-controller files | Embeds 0.9.8.1 directly, no download target; SHA-256 verified at runtime |
| Profile source | 348 JSON files under `profiles/`; the 2.0.3 catalog exposes 337 built-in profiles, compiled into the assembly with source-generated JSON | No JSON resources, constructed in code by `StaticProfileRegistry.cs` |
| Resource logical names | `HIDMaestro.*` | `GKMD.Resources.*` |
| Runtime identifiers | `Global\HIDMaestro*`, `HIDMAESTRO_TIMEOUT_SCALE`, `HKLM\SOFTWARE\HIDMaestro*` | `Global\GKMD*`, `GKMD_TIMEOUT_SCALE`, `HKLM\SOFTWARE\GKMD*` |
| Two-phase build | Required (build the native UMDF2 driver, companion, SWD helper and OpenVR driver for x64/ARM64 first, then the SDK) | Not required; resources are fixed files |
| Version | `2.0.3` | `4.6.1.0` (follows GKME) |
| AOT / trimming | `IsAotCompatible=true`; source-generated JSON, `LibraryImport`, AOT analyzers fail the build on warnings; ships NativeAOT-ready | `IsAotCompatible=true` (trim / AOT analyzers, 0 warnings); reflection-based `System.Text.Json` in `GKLayoutLoader.cs` is suppressed locally; the host publishes with `PublishAot` |
| ARM64 | Driver and usbip payloads for x64 and ARM64 in one DLL | Managed AnyCPU assembly; bundled usbip-win2 payload is x64-only |
| Host integration | Generic SDK | Used by [GKME](../GKME-Windows) via `ProjectReference`; `InternalsVisibleTo("GKME")` exposes the transport installer |

### 6. Removed from the 1.5.1 base (appendix)

These components exist in the 1.5.1 base. Upstream 2.0.3 still carries most of them (several have grown since; `SwitchProPacker` has been replaced by profile state blocks); GKMD carries none of them.

| File / feature (present upstream) | Original responsibility |
| --- | --- |
| `HMDeviceExtractor.cs` | Extracts a full profile from a connected physical HID device (enumeration + descriptor reconstruction) |
| `HMHidDeviceInfo.cs` | Lightweight HID device info DTO for `ListDevices()` |
| `HMPidState.cs` | PID FFB public types (`PidLoadStatus`, `PidStateFlags`, `HMPidBlockLoad`) |
| `Internal/DeviceManager.cs` | Event-driven PnP device management based on `CM_Register_Notification` |
| `Internal/DeviceNodeCreator.cs` | Creates root-enumerated virtual PnP device nodes (including the xinputhid / Xbox legacy / normal HID enumeration paths) |
| `Internal/DeviceOrchestrator.cs` | Controller setup/teardown orchestration from profile to live device, companion creation, orphan sweep, GameInputService warm-up |
| `Internal/DeviceProperties.cs` | Sets FriendlyName / DeviceDesc / BusReportedDeviceDesc via `CM_Set_DevNode_PropertyW` |
| `Internal/DriverBuilder.cs` | Self-contained UMDF2 driver installer (extract, self-sign cert, sign, catalog, pnputil deploy, same-version fast path) |
| `Internal/EmbeddedManifest.cs` | Stable SHA-256 of the embedded driver install payload for the `FullDeploy` fast path |
| `Internal/HidDescriptorReconstructor.cs` | Rebuilds a HID report descriptor from Windows preparsed data |
| `Internal/HidDeviceEnumerator.cs` | SetupAPI + `Hid_*` enumeration of connected HID devices |
| `Internal/HidPreparsedData.cs` | Binary layout mirror of Windows HID preparsed data |
| `Internal/PidReportIdExtractor.cs` | Walks the report descriptor to find the PID Pool/State/BlockLoad Report IDs |
| `Internal/PnputilHelper.cs` | Structured wrapper for `pnputil /enum-drivers`, `/delete-driver` |
| `Internal/SwdDeviceFactory.cs` | Creates an SWD device via `hmswd.exe` (`SwDeviceCreate`) to obtain a real ContainerId |
| `Internal/SwitchProPacker.cs` | SDK-side Switch Pro 0x30 body packing + HD rumble amplitude decoding (logic moved to the device-emulator side) |
| Feature: PID FFB shared section | The entire `GKMD_PidState<N>` section in `SharedMemoryIO`, `PublishPidPool/BlockLoad/State`, `GetCurrentPidBlockLoad`, `WritePidReportIds` |
| Feature: profile disk/embedded loading | `GKContext.LoadProfilesFromDirectory`, `ProfileDatabase.Load/LoadEmbedded` |
| Feature: UMDF2 lifecycle | `InstallDriver`'s ghost sweep / `FullDeploy`, `_batchDisposing`, `RemoveOrphanHidChildrenBatch`, background warm-up tasks |
| `HIDMaestro.Core.csproj` | Upstream SDK project file, superseded by `GKMD.csproj` |

### 7. File-level changes relative to the 1.5.1 base (appendix)

These are the changes GKMD made to the 1.5.1 files. Upstream 2.0.3 has since rewritten many of them, so this table documents GKMD's derivation, not the current 2.0.3 implementation.

| File | Main changes |
| --- | --- |
| `GKContext.cs` | Removes background warm-up from the constructor; `IsDriverInstalled` → `UsbipBackend.IsAvailable`; `InstallUsbipBackend` changed to `Install(interactive:false)` and throws on failure; `InstallDriver` → `EnsureInstalled()` (no longer auto-installs); `RemoveAllVirtualControllers` → `UsbipBackend.DetachAllOwned`; `LoadDefaultProfiles` now uses `StaticProfileRegistry`; removes `LoadProfilesFromDirectory`; `CreateController/CreateControllerAt` always go through USB/IP and allow a USB-only configuration (no HID descriptor); `FinalizeNames` becomes a no-op |
| `GKController.cs` | `_reportBuilder` is nullable, adds `_rawReportBuffer`; `SubmitState` returns immediately for vendor profiles without HID; removes all PID publishing APIs; removes Switch Pro controller-side logic (moved to the device side); `UsbAudio` is created only when there is an audio streaming interface |
| `ControllerProfile.cs` | Adds `SerialNumberString`; removes `Backend` / `RequiresUsbipBackend`; `UsbConfiguration` becomes the descriptor set carried by every profile; `GetOrBuildReportBuilder()` can return null; **removes the entire `ProfileDatabase` class**; `FieldSpec` gains `Divisor` / `FullValue` for the DualShock 4 fixes synced from 1.10.0 |
| `SharedMemoryIO.cs` | **Removes the whole PID state**: PID_STATE size/offset constants, `EnsurePidStateMapping`, `WritePidPool/BlockLoad/State`, `ReadPidBlockLoad`, `WritePidReportIds` and related handles and disposal logic |
| `UsbConfigurationSpec.cs` | Adds `InputReportSize`, `VendorRequests` (including the `VendorControlRequest` type), `Gip`, `ResolutionMultiplier`; comments changed to describe a USB/IP persona descriptor set |
| `UsbDescriptorSet.cs` | `ReportDescriptor` is nullable, adds `HasHidInterface` / `InputEndpoint`; parses the vendor 0x21 class descriptor; the HID interface becomes optional; adds `GetClassDescriptor`; iSerial returns the real serial number; varies a captured serial per controller index and serves a stable synthetic serial for profiles that carry none (upstream 1.8.0 identity fix) |
| `UsbipBackend.cs` | `Attach` passes `UsbipReceiveMode.LowLatency`; the stale sweep detaches only imports whose server is gone and cancels only dead locations' attempts; a failed attach detaches a raced import; `UsbipBackendHandle.Dispose` detaches by location, waits for it to leave the list (with one retry) and cancels its attempts |
| `UsbipDriverInstaller.cs` | Pins **0.9.8.1** and verifies that release's SHA-256; `EnsureInstalled` validates the ABI and throws "needs install / needs reboot" exceptions; adds `Install(interactive)`, Inno exit code parsing (0/3010), `NeedsRebootThisBoot` flag, `WaitForCurrentAbi`, `IsCurrentAbi`; calls `VhciClient.IsCurrentAbi()` so 0.9.8.1 / 0.9.8.0 / 0.9.7.x are all recognized; `StampOwnerHardwareId` enumerates `ROOT\USB\0000..000F` and stamps each |
| `VhciClient.cs` | Speaks **three vHCI layouts** and probes for the installed one: 0.9.8.1 (`plugin_hardware` 1124, stop 1108, row 1132, `location_hash` after `port`), 0.9.8.0 (1120 / 1104 / 1128) and 0.9.7.x (1100 / 1104 / 1108, no `wsk_events`); `Attach` adds a `UsbipReceiveMode` parameter; `Detach` normalizes ≤0 to `PORT_ALL(-1)`; adds `WaitForPortDetached`, `DetachLocation`, `FindLocation`, `SamePort`, `WaitForLocationDetached`, batched `StopAttachAttempts`, `IsCurrentAbi`, `InstalledLayout`, `UsbipReceiveMode` |
| `UsbipServer.cs` | `Unregister` adds identity validation (prevents an old device from mistakenly deleting a new one); adds host-controller-only import admission, handshake deadline and cap, GKMD's own port range with an OS-assigned fallback, re-attach cancellation for an unknown bus id, claim cleanup on a failed reply, and an import that ends when its device is disposed |
| `Usbip/TcpConnectionOwner.cs` | New: maps an accepted loopback connection to its owning PID through `GetExtendedTcpTable`, so only the kernel WSK connection (System, PID 4) may import |
| `UsbipEmulatedDevice.cs` | Adds the GIP / Switch input pump branches, vendor-class opaque reporting, `vendorRequests`, Resolution Multiplier, DS4 0x20/0x81, DualSense test commands; only answers `InputEndpoint`; `Audio` is nullable; reconnect/reset cleanup; GIP error logging; DualShock 4 0x01 offsets and 0x05 calibration from upstream 1.10.0; passes the interface / alternate setting to the audio engine; `IsDisposed` / claim checks, an idempotent `Dispose` that wakes the input pump, and a STALL for an input report ID the descriptor does not declare |
| `Usbip/GipResponder.cs` | Xbox One now builds a 230-byte metadata blob from the real 214-byte gamepad metadata by appending the PC security opt-out as a fourth interface (keeping `INavigationController`) and recomputing the device / total / client-command offsets; Xbox One `InputReportSize` corrected from 18 to 14. The previous 182-byte sample metadata made `xboxgip` bugcheck on create |
| `Usbip/UsbAudioEngine.cs` | Version semantics follow 0.9.8.x; `DeviceOrchestrator.LogDiag` → `Debug.WriteLine`; infers stream state from isochronous traffic when an older filter never forwards `SET_INTERFACE` (upstream 1.10.0 fix); uses bmRequestType 0x80 for the 0.9.8.1 filter; control and stream changes run on a dedicated event thread in host order, and handler exceptions are dropped |
| `GKProfile.cs` | Removes `Backend` / `RequiresUsbipBackend`; `Connection` uses `IsNullOrEmpty` for null checks; docs changed from "profile JSON" to "catalog / built-in" |
| `GKLayoutLoader.cs` | Adds `IL3050` / `IL2026` suppressions and `System.Diagnostics.CodeAnalysis`, adapting to AOT / trimming |
| `OemNameOverrideStore.cs` | Adds the `CA1416` (Windows-only) platform compatibility suppression |
| `GKUsbAudio.cs` | Comment update; adds `!` to `device.Audio` now that it is nullable; raises public audio events per handler, dropping an exception instead of ending the audio thread or connection |
| `VendorBlobCodec.cs`, `VendorBlobProgram.cs` | DualShock 4 fixes synced from upstream 1.10.0: `uint16-le` sensor timestamp, touch packet counter, battery `fullValue`, cable bit, and exact 8-bit trigger digital bits |
| `HidReportBuilder.cs`, `HidDescriptorBuilder.cs`, `GKOutputDecodedEventArgs.cs`, `GKOutputEncoder.cs`, `GKProfileBuilder.cs` | Wording / minor null-check syntax only, no functional change |
| `GKGamepadState.cs`, `GKLayout.cs`, `GKOemNameOverride.cs`, `GKOutputPacket.cs`, `TimeoutScale.cs`, `UsbipProtocol.cs` | **No differences** other than the namespace |

#### Upstream fixes synced (1.5.1 → 1.12.0)

Only fixes that apply to the pure USB/IP design were taken:

- **Audio stream inference** (`9306fc3`): usbip-win2 0.9.8.1 with an older filter driver never forwards `SET_INTERFACE`; the engine now opens a stream on its first isochronous transfer and closes it about 200 ms after the transfers stop, while an interface that has received `SET_INTERFACE` still follows it alone.
- **Stable per-controller serials** (`46054b8`, `a396741`): the USB serial varies by controller index, and profiles without a captured serial get a stable synthetic one. Windows keys the USB instance id on it instead of the vhci port.
- **DualShock 4 reports** (`fd86c1a`): report `0x01` fields sit at SDL/Linux offsets, the sensor timestamp advances in 16/3 µs ticks, battery/full/cable and touch fields follow the real pad, `LT_DIGITAL` / `RT_DIGITAL` follow the 8-bit trigger value, and calibration is the identity (8192/g, 16/°/s) with the DS4 `0x05` ordering.

#### Upstream USB/IP fixes synced (1.12.0 → 2.0.3)

These are the 2.0.0 changes that live entirely in the USB/IP transport and device layer, so they port without the 2.0 data model:

- **Host-controller-only imports** (`TcpConnectionOwner`): the server admits an `OP_REQ_IMPORT` only when the client end belongs to the System process (PID 4), which is how the kernel WSK connection appears. A local program can no longer import a persona from another process.
- **Handshake limits**: a connection must finish its import handshake within 5 s, and at most 16 handshakes run at once; connections beyond that are closed without a thread unless the host controller opened them.
- **Dead-server sweeps**: a sweep detaches only imports on a port with no listener (or on this process's own port whose bus id it does not export), and cancels re-attach attempts only for those dead locations. A live program's server and imports are left alone.
- **Location-based detach**: `DetachLocation` / `FindLocation` / `WaitForLocationDetached` detach and wait by `(host, server port, bus id)` rather than by a vhci port that may have been reused by another device.
- **Registration gate**: registrations wait while the stale sweep runs, so a new device cannot attach at a location the sweep is about to detach.
- **Attach/import cleanup**: a failed attach detaches a raced import and cancels its attempt; the server holds the device claim from before the import reply, so a failed reply cannot leave the bus id stuck `ST_DEV_BUSY`.
- **Disposal**: a disposed device ends its open import, the handle detaches by location and cancels the location's attempts, `Dispose` wakes the input pump instead of waiting out its 500 ms frame wait, and a second `Dispose` returns.
- **Audio event isolation**: control and stream changes are raised on a dedicated audio event thread in host order, so a slow handler cannot block the connection reader or the audio pump; an exception a handler throws is dropped instead of ending the connection or the process.
- **GET_REPORT(Input)**: a report ID the HID descriptor does not declare is answered with a STALL instead of the last report.

One GKMD-specific change accompanies these: the server listens on its own `37286..37301`, clear of HIDMaestro's `18509..18524` / `28509..28524` and FlexInput's `18008`, so the two can run side by side without either taking the other's imports. If the range is full, it falls back to an OS-assigned loopback port.

Not synced from 2.0.x because they do not apply or are outside the USB/IP runtime: the zero-allocation control/frame paths (performance only), owner records and transport-index leases (identity/coexistence system), direct installation of usbip-win2's host controller and `HMUninstall` (GKMD keeps the official installer and the probe-only policy), ARM64 payloads, and the 2.0 data model.

#### Upstream work after 1.12.0 not synced (1.12.0 → 2.0.3)

- **2.0.0** replaces the device layer with JSON data and compiled programs, adds strict profile validation with JSON paths, `HMPort`, identity keys/leases, NativeAOT, ARM64 payloads, the Wii U GameCube adapter and 337 profiles. These are architectural changes, not fixes that can be ported into GKMD's static C# registry. Its USB/IP runtime fixes are listed above and are synced.
- **2.0.1 – 2.0.3** install usbip-win2's host controller files directly, add `HMUninstall` and removal of usbip-win2's host controller and root-hub filter, and correct the Valve personas. GKMD deliberately keeps the official usbip-win2 installer and the probe-only install policy, and has no Valve personas to correct.
- 1.6.0 – 1.12.0 features such as VR, Valve personas, Switch 2 Pro, keyboard/mouse profiles, PS Move and DualShock 3 pressure are new personas or APIs rather than fixes; see section 4.

## Directory structure

```
GKMD/
├── GKMD.csproj                 # Class library project (net10.0-windows)
├── AssemblyInfo.cs             # AssemblyVersion / InternalsVisibleTo
├── GKContext.cs                # Top-level entry: profile catalog + controller lifecycle
├── GKController.cs             # A single virtual controller
├── GKProfile.cs / GKProfileBuilder.cs / ControllerProfile.cs
├── HidDescriptorBuilder.cs / HidReportBuilder.cs
├── SharedMemoryIO.cs           # Pagefile section / event shared with the device side
├── StaticProfileRegistry.cs    # Built-in profiles (constructed in code)
├── TimeoutScale.cs / GKLayout.cs / GKLayoutLoader.cs / ...
├── Usbip/                      # USB/IP backend
│   ├── UsbipBackend.cs / UsbipServer.cs / VhciClient.cs / TcpConnectionOwner.cs
│   ├── UsbipEmulatedDevice.cs  # Device-side behavior (HID / GIP / Switch / vendor)
│   ├── UsbDescriptorSet.cs / UsbConfigurationSpec.cs
│   ├── UsbipDriverInstaller.cs # Deploy/upgrade usbip-win2
│   ├── UsbipInstallRequiredException.cs / UsbipRebootRequiredException.cs
│   ├── GipProtocol.cs / GipResponder.cs / GipLog.cs
│   └── SonyTestCommandHandler.cs
└── Resources/
    ├── USBip-0.9.8.1-x64.exe
    └── THIRD-PARTY-NOTICES.txt
```

## Build

```powershell
dotnet build -c Release
```

GKME integration: place this repository and the GKME repository under the same parent directory; GKME references this project via `<ProjectReference Include="..\GKMD\GKMD.csproj" />`.

## AOT support

GKMD is **AOT / trimming compatible** and is meant to be linked into a Native AOT host:

- `GKMD.csproj` sets `<IsAotCompatible>true</IsAotCompatible>`, so the trim / AOT / single-file analyzers run on every build. Both Debug and Release builds are warning-free (0 warnings / 0 errors).
- The only reflection-based JSON paths (`GKLayoutLoader.cs`, `System.Text.Json` layout load/save) are annotated locally with `#pragma warning disable IL2026, IL3050`. A host that serializes layouts at runtime should keep the `GKLayout*` types alive (e.g. `JsonSerializerContext` or a trimmer root descriptor).
- The embedded usbip-win2 payload (`Resources/USBip-0.9.8.1-x64.exe`) is a manifest resource and **survives Native AOT publishing**: a host app referencing this project reads it back at full length after `dotnet publish -r win-x64 -p:PublishAot=true`.

Verify it yourself:

```powershell
# 1) library build with the trim / AOT analyzers on (must stay warning-free)
dotnet build -c Release

# 2) full Native AOT publish from a host project that references GKMD.csproj
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```

GKME already publishes with `<PublishAot>true</PublishAot>` + `<PublishTrimmed>true</PublishTrimmed>` and links GKMD that way.

## Releases

Starting with v4.5.2, each [GitHub Release](../../releases) attaches a single `GKMD-v<version>.zip` (Release, IL, RID-neutral AnyCPU) containing `GKMD.dll`, `LICENSE` and `THIRD-PARTY-NOTICES.txt`, so the notices travel with every copy of the binary. Build it with `dotnet build -c Release -t:PackageRelease`; the release changelog lives in [RELEASE_NOTES.md](RELEASE_NOTES.md).

## License

MIT License, see [LICENSE](LICENSE). The original copyright belongs to the HIDMaestro Contributors; GKMD retains that notice and marks its derivation. The bundled usbip-win2 (BSD-2-Clause) license is in [Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt).
