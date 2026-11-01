# Linux smoke test

Manual end-to-end check for GKMD's `net10.0` build. It is not part of the
library build and is not shipped.

It creates a controller over mainline Linux's in-kernel `vhci_hcd` and checks
the Linux-only pieces that unit tests cannot cover on Windows:

- `VhciClient`'s managed `OP_REQ_IMPORT` handshake and sysfs socket handoff
- kernel enumeration of the emulated device
- `usbhid` binding the HID interface
- evdev axis/button events from submitted state
- location-based detach and the port returning to `VDEV_ST_NULL`
- `TcpConnectionOwner`'s uid admission (the import is refused otherwise)
- `UsbipDriverInstaller`'s probe path (`EnsureInstalled`)

## Run

Publish from Windows (cross-publishes a self-contained `linux-x64` build, so
WSL needs no .NET SDK):

```powershell
dotnet publish tools/LinuxSmokeTest/GkmdLinuxSmoke.csproj -c Release -r linux-x64 --self-contained true
```

Then, in WSL:

```bash
bash tools/LinuxSmokeTest/run-linux-smoke.sh
```

The script re-executes itself through `sudo` (it prompts for your WSL
password), loads `vhci-hcd`, `usbhid` and `evdev`, runs the test as root, and
prints the vhci status plus the kernel log.

Optional arguments:

```bash
bash tools/LinuxSmokeTest/run-linux-smoke.sh <profile-id> <seconds>
```

`profile-id` defaults to `dualshock-4-v2` (a standard HID gamepad, so
`usbhid` and evdev map it predictably). Other useful values: `switch-pro`,
`xbox360`, `dualsense`.

Requirements:

- a kernel with `CONFIG_USBIP_VHCI_HCD` (the WSL2 kernel ships it as a module)
- root for `modprobe` and for the vhci sysfs `attach` write

## Verified

WSL2 Ubuntu, kernel `6.6.87.2-microsoft-standard-WSL2`:

- `dualshock-4-v2`: enumerated as `054C:09CC`, `snd-usb-audio` bound the
  audio interfaces, `usbhid` bound the HID interface, ~1800 `EV_ABS` and
  ~40 `EV_KEY` events in 5 s, detach freed port 0.
- `switch-pro`: enumerated as `057E:2009`, `usbhid` bound, evdev events
  received, detach freed port 0. (The kernel logs a harmless warning that
  the profile's captured configuration descriptor declares one endpoint but
  carries two; Windows accepts the same descriptor.)
- Two concurrent instances: attached on ports 0 and 1, both passed, and every
  port was back to `VDEV_ST_NULL` afterwards.
