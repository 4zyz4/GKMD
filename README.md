# GKMD

**English** | [简体中文](README.zh-CN.md)

GKMD is a standalone Windows virtual USB game controller engine (class library) that emulates USB devices in user mode through the virtual host controller (vHCI) of [usbip-win2](https://github.com/vadimgrn/usbip-win2), presenting a real hardware identity to the kernel-facing HID / XInput / DirectInput / SDL / Gamepad APIs.

## Attribution

GKMD **is modified from [hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) 1.5.1** (tag `v1.5.1`, MIT License). It was extracted from HIDMaestro's `sdk/HIDMaestro.Core` and extensively trimmed, rewritten, and extended toward a "pure USB/IP backend + fixed built-in profiles" design.

- Upstream project: https://github.com/hifihedgehog/HIDMaestro
- Upstream version: `v1.5.1`
- Upstream license: MIT License (see `LICENSE`; GKMD retains the original copyright notice)
- Namespaces: `HIDMaestro` / `HIDMaestro.Internal` / `HIDMaestro.Internal.Usbip`
  → `GKMD` / `GKMD.Internal` / `GKMD.Internal.Usbip`
- Type / file prefix: `HM` (short for HIDMaestro) → `GK`, e.g. `HMContext` → `GKContext`,
  `HMController` → `GKController`, `HMContext.cs` → `GKContext.cs`, `HMLayout.cs` → `GKLayout.cs`
- Runtime identifiers: `Global\HIDMaestro*`, `HIDMAESTRO_TIMEOUT_SCALE`, `HKLM\SOFTWARE\HIDMaestro*`
  → `Global\GKMD*`, `GKMD_TIMEOUT_SCALE`, `HKLM\SOFTWARE\GKMD*`

> Note: GKMD is the standalone product of a driver branch that evolved over the long term within the GKME project. It is **not** a line-by-line simplified single-file copy of HIDMaestro 1.5.1. The "differences" in the table below constitute all known changes relative to 1.5.1.

## Differences summary

### 1. Transport / driver architecture

| Dimension | HIDMaestro 1.5.1 | GKMD |
| --- | --- | --- |
| Controller creation path | Branches on `RequiresUsbipBackend`: normal profiles use UMDF2 + PnP, composite personas use USB/IP | **No branching; everything goes through USB/IP** (`CreateController` / `CreateControllerAt` both call `CreateUsbipController`) |
| Driver model | UMDF2 user-mode driver `HIDMaestro.dll` + XUSB companion + INF / PnP | Pure USB/IP: usbip-win2 virtual host controller + vHCI, **no kernel driver, no INF, no device node** |
| Device node | Creates a root-enumerated / SWD PnP devnode and writes friendly name / ControllerIndex | No PnP devnode; device strings come directly from USB descriptors |
| Transport installation | `DriverBuilder.FullDeploy()`: self-signed cert → sign → inf2cat → pnputil | `UsbipDriverInstaller` deploys / upgrades usbip-win2 |
| usbip-win2 version | 0.9.7.7 | **0.9.8.1** (IOCTL ABI and structs updated in sync) |
| USB/IP receive mode | Zero-copy (zero-copy MDL) only | Adds `UsbipReceiveMode.LowLatency` (default) / `ZeroCopy` |
| Installation policy | Silently auto-installs on first creation | **Probe only**; throws `UsbipInstallRequiredException` / `UsbipRebootRequiredException` when missing/outdated, leaving installation to the host app |
| Audio engine | `Audio` is always non-null | `UsbAudioEngine?`, created only when the profile declares a USB Audio streaming interface |
| Controller index release | Removes the device first, then releases the index | Tears down the USB/IP device first, then releases the index (avoids port reuse races during fast switching) |
| Diagnostic log | `DeviceOrchestrator.LogDiag` | Replaced with `Debug.WriteLine` |

### 2. New features

| Feature | Related files | Description |
| --- | --- | --- |
| Xbox One GIP protocol | `Usbip/GipProtocol.cs`, `Usbip/GipResponder.cs`, `Usbip/GipLog.cs`, `Usbip/UsbipEmulatedDevice.cs` | Vendor class `045E:02EA` implements Microsoft Gaming Input Protocol: HELLO/announce/identify/status handshake, 14-byte `gip_gamepad_pkt_input` input, guide virtual key, host command (power/LED/rumble/identify) decoding |
| Switch Pro / Joy-Con responder | `Usbip/UsbipEmulatedDevice.cs`, `StaticProfileRegistry.cs` | `057E:2009/2006/2007`: 0x80 USB init, 0x01 subcommands, 0x30/0x21 frames, fake SPI mirror (IMU/stick calibration, colors, serial number, device type), 15 ms streaming, IMU toggle, rumble enable |
| Vendor-class (non-HID) device channel | `UsbDescriptorSet.cs`, `UsbipEmulatedDevice.cs`, `UsbConfigurationSpec.cs` | Passes opaque input reports through verbatim; the `vendorRequests` table answers EP0 vendor requests (e.g. Xbox 360 `0xC1/0x01/0x0100` capability report); only answers `InputEndpoint` and STALLs the remaining INT-IN endpoints (avoids xusb22 creating a phantom audio device) |
| Xbox 360 persona | `StaticProfileRegistry.BuildXbox360()` | 20-byte native input report + `VendorRequests` capability report, no HID descriptor path |
| DualShock 4 authentication / serial | `Usbip/UsbipEmulatedDevice.cs` | Adds `Ds4FeatureReport20` (JDM-050 serial number) and `Ds4FeatureReport81` (`0x03030301` challenge response); recognizes the `SET_FEATURE 0x80` subcommand |
| DualSense test commands | `Usbip/SonyTestCommandHandler.cs` | torch/telemetry, Type2 tracability, BT MAC/patch, battery voltage and other device_id/action_id families, with multi-page/single-page responses and header injection |
| DualSense firmware info update | `Usbip/UsbipEmulatedDevice.cs` | Firmware string `202510:10:32` → `202510:38`, version/series field adjustments, adds `GetDs5FirmwareInfo()` |
| High-resolution wheel | `UsbConfigurationSpec.ResolutionMultiplier` | Supports the Resolution Multiplier for `GET/SET_REPORT(Feature, 0)`, defaulting to high resolution during enumeration |
| Device serial number string | `ControllerProfile.SerialNumberString`, `UsbDescriptorSet.cs` | iSerial returns the real serial number (previously always returned null) |
| Install/reboot exception types | `Usbip/UsbipInstallRequiredException.cs`, `Usbip/UsbipRebootRequiredException.cs` | Lets the host UI turn "needs install / needs reboot" into a prompt rather than a failure |
| Static profile registry | `StaticProfileRegistry.cs` | Builds built-in profiles in code, replacing the upstream JSON catalog/embedded resources |
| Keyboard / mouse HID persona | `StaticProfileRegistry.cs` | Built-in keyboard / mouse profiles (used by GKME for virtual keyboard/mouse) |

### 3. Removed components / features

| File / feature (present in 1.5.1) | Original responsibility |
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

### 4. Modified files (functional differences)

| File | Main changes |
| --- | --- |
| `GKContext.cs` | Removes background warm-up from the constructor; `IsDriverInstalled` → `UsbipBackend.IsAvailable`; `InstallUsbipBackend` changed to `Install(interactive:false)` and throws on failure; `InstallDriver` → `EnsureInstalled()` (no longer auto-installs); `RemoveAllVirtualControllers` → `UsbipBackend.DetachAllOwned`; `LoadDefaultProfiles` now uses `StaticProfileRegistry`; removes `LoadProfilesFromDirectory`; `CreateController/CreateControllerAt` always go through USB/IP and allow a USB-only configuration (no HID descriptor); `FinalizeNames` becomes a no-op |
| `GKController.cs` | `_reportBuilder` is nullable, adds `_rawReportBuffer`; `SubmitState` returns immediately for vendor profiles without HID; removes all PID publishing APIs; removes Switch Pro controller-side logic (moved to the device side); `UsbAudio` is created only when there is an audio streaming interface |
| `ControllerProfile.cs` | Adds `SerialNumberString`; removes `Backend` / `RequiresUsbipBackend`; `UsbConfiguration` becomes the descriptor set carried by every profile; `GetOrBuildReportBuilder()` can return null; **removes the entire `ProfileDatabase` class** |
| `SharedMemoryIO.cs` | **Removes the whole PID state**: PID_STATE size/offset constants, `EnsurePidStateMapping`, `WritePidPool/BlockLoad/State`, `ReadPidBlockLoad`, `WritePidReportIds` and related handles and disposal logic |
| `UsbConfigurationSpec.cs` | Adds `InputReportSize`, `VendorRequests` (including the `VendorControlRequest` type), `Gip`, `ResolutionMultiplier`; comments changed to describe a USB/IP persona descriptor set |
| `UsbDescriptorSet.cs` | `ReportDescriptor` is nullable, adds `HasHidInterface` / `InputEndpoint`; parses the vendor 0x21 class descriptor; the HID interface becomes optional; adds `GetClassDescriptor`; iSerial returns the real serial number |
| `UsbipBackend.cs` | `Attach` passes `UsbipReceiveMode.LowLatency`; `UsbipBackendHandle.Dispose` adds `WaitForPortDetached` (with one retry) to avoid reusing an uncleaned port during fast mode switching |
| `UsbipDriverInstaller.cs` | Bumps version/SHA to 0.9.8.0; `EnsureInstalled` now validates the ABI and throws "needs install / needs reboot" exceptions; adds `Install(interactive)`, Inno exit code parsing (0/3010), `NeedsRebootThisBoot` flag, `WaitForCurrentAbi`, `IsCurrentAbi`; `StampOwnerHardwareId` now enumerates `ROOT\USB\0000..000F` and stamps each |
| `UsbipEmulatedDevice.cs` | Adds the GIP / Switch input pump branches, vendor-class opaque reporting, `vendorRequests`, Resolution Multiplier, DS4 0x20/0x81, DualSense test commands; only answers `InputEndpoint`; `Audio` is nullable; reconnect/reset cleanup; GIP error logging |
| `VhciClient.cs` | `plugin_hardware` struct expanded to 1120 bytes (adds serial / wsk_events), `imported_device` line 1128 bytes; `Attach` adds a `UsbipReceiveMode` parameter; `Detach` normalizes ≤0 to `PORT_ALL(-1)`; adds `WaitForPortDetached`, `IsCurrentAbi`, `UsbipReceiveMode` enum |
| `UsbipServer.cs` | `Unregister` adds identity validation (prevents an old device from mistakenly deleting a new one); comment version bumped to 0.9.8.0 |
| `GKProfile.cs` | Removes `Backend` / `RequiresUsbipBackend`; `Connection` uses `IsNullOrEmpty` for null checks; docs changed from "profile JSON" to "catalog / built-in" |
| `GKLayoutLoader.cs` | Adds `IL3050` / `IL2026` suppressions and `System.Diagnostics.CodeAnalysis`, adapting to AOT / trimming |
| `OemNameOverrideStore.cs` | Adds the `CA1416` (Windows-only) platform compatibility suppression |
| `GKUsbAudio.cs` | Comment update; adds `!` to `device.Audio` now that it is nullable |
| `UsbAudioEngine.cs` | Version semantics bumped to 0.9.8.0; `DeviceOrchestrator.LogDiag` → `Debug.WriteLine` |
| `VendorBlobCodec.cs`, `HidReportBuilder.cs`, `HidDescriptorBuilder.cs`, `GKOutputDecodedEventArgs.cs`, `GKOutputEncoder.cs`, `GKProfileBuilder.cs` | Wording / minor null-check syntax only, no functional change |
| `GKGamepadState.cs`, `GKLayout.cs`, `GKOemNameOverride.cs`, `GKOutputPacket.cs`, `TimeoutScale.cs`, `VendorBlobProgram.cs`, `UsbipProtocol.cs` | **No differences** other than the namespace |

### 5. Build / project differences

| Dimension | HIDMaestro 1.5.1 | GKMD |
| --- | --- | --- |
| Output | Class library `HIDMaestro.Core.dll` | Class library **`GKMD.dll`** |
| TargetFramework | `net10.0-windows10.0.26100.0` | `net10.0-windows` |
| Platform | x64 (`PlatformTarget`) | AnyCPU |
| Namespaces | `HIDMaestro` / `.Internal` / `.Internal.Usbip` | `GKMD` / `.Internal` / `.Internal.Usbip` |
| Directory structure | `HIDMaestro.Core/` + `Internal/` + `Internal/Usbip/` | Flattened to the root + the `Usbip/` subdirectory |
| Driver resources | The `PackResources` target collects `HIDMaestro.dll`, two INFs, `hmswd.exe`, and the signtool/inf2cat dependency tree from `build/` and the local WDK | **Embeds no kernel driver/signing/catalog tools**; embeds only `Resources/USBip-0.9.8.1-x64.exe` and `THIRD-PARTY-NOTICES.txt` |
| usbip-win2 acquisition | Build-time `DownloadFile` of 0.9.7.7 with SHA256 verification (fails on mismatch) | Embeds 0.9.8.1 directly, no download target (SHA256 still verified at runtime) |
| Profile source | Linked and embedded from `profiles/**/*.json` (logical name `HIDMaestro.Profiles.*`) | No JSON resources, constructed in code by `StaticProfileRegistry.cs` |
| Resource logical names | `HIDMaestro.Resources.*` | `GKMD.Resources.*` |
| Runtime identifiers | `Global\HIDMaestro*`, `HIDMAESTRO_TIMEOUT_SCALE`, `HKLM\SOFTWARE\HIDMaestro*` | `Global\GKMD*`, `GKMD_TIMEOUT_SCALE`, `HKLM\SOFTWARE\GKMD*` |
| Two-phase build | Required (build the native driver first to populate `Resources/`, then `dotnet build` twice to embed) | Not required; resources are fixed files |
| Version | Upstream version number (1.x) | `4.5.2.0` (follows GKME) |
| AOT / trimming | Not annotated for AOT | `IsAotCompatible=true` (trim / AOT analyzers, 0 warnings); the host publishes with `PublishAot` |
| Host integration | Generic SDK | Used by [GKME](../GKME-Windows) via `ProjectReference`; `InternalsVisibleTo("GKME")` exposes the transport installer |

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
│   ├── UsbipBackend.cs / UsbipServer.cs / VhciClient.cs
│   ├── UsbipEmulatedDevice.cs  # Device-side behavior (HID / GIP / Switch / vendor)
│   ├── UsbDescriptorSet.cs / UsbConfigurationSpec.cs
│   ├── UsbipDriverInstaller.cs # Deploy/upgrade usbip-win2
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

Prebuilt `GKMD.dll` (Release, IL, RID-neutral AnyCPU) is attached to each [GitHub Release](../../releases); the release changelog lives in [RELEASE_NOTES.md](RELEASE_NOTES.md).

## License

MIT License, see [LICENSE](LICENSE). The original copyright belongs to the HIDMaestro Contributors; GKMD retains that notice and marks its derivation. The bundled usbip-win2 (BSD-2-Clause) license is in [Resources/THIRD-PARTY-NOTICES.txt](Resources/THIRD-PARTY-NOTICES.txt).
