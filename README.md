# jsync Drive for Windows

A tray app (C# / WPF, .NET 8) for the jsync Drive: the desktop counterpart of the Android app (`../jsyncandroid`).

First run asks **how this PC is used**:

- **As client**: Hub and Drive address, the folder for the Drive, and a sign-in (the PC shows a code that is approved once in
  the Drive web app). The Drive then shows as a normal folder in Explorer (`jsync mount drive`, Windows Cloud Filter API),
  can be browsed in the app, and can send/receive files to the account's other devices.
- **As hub**: where the Drive files are kept, optional account-service settings, ports. It runs `jsyncd` (in the app, or as a
  Windows service) and shows the addresses other devices must use. It can also be a client of itself.

## What is in it

| Page | What it does |
|---|---|
| My Drive | Browse (grid with image thumbnails / list), search as you type, upload (button or drop, folders too), download, open, rename, delete, new folder, folder colour, pin, share with people, send to a device, unlock end-to-end encrypted folders |
| Devices | The account's other devices (send files, drop onto a card), what is happening now (accept / always / decline / cancel), history of shared and received files, receive folder, "show a window", accept from all my devices, trusted devices ("always accept") |
| Drive folder | The Cloud Filter folder: status, start/stop, folder location, read/write, free space after N days, start at sign-in, log, remove from Explorer |
| Server (hub) | Hub status, run in the app or as a Windows service (install/start/stop/uninstall), addresses for the devices, configuration (`jsyncd init`), log |
| Settings | Account and connection, language (English / Español), light / dark, start with Windows, change the role, about |

A file sent to this PC opens a small window (picture or icon of its kind, Accept / Always / Decline; later Open / Show in folder /
Delete), like the Android app's transfer window.

## How it is built

The app does not reimplement the protocol. It runs the Go engine from `../filesharer` as child processes:

- `jsyncnode.exe`: login, Drive files, folder colours / pins / shares, device-to-device transfers, behind a loopback HTTP API
  (the same node the Android app runs). Wrapped by `Engine/NodeApi.cs` and `Engine/NodeHost.cs`.
- `jsync.exe mount drive`: the Cloud Filter folder, with the node's identity and login (`Engine/MountService.cs`).
- `jsyncd.exe`: the hub (`init` writes its config without questions, `install --no-wizard` registers the service).

`JSYNC_CREDENTIALS=file` keeps the app's Drive token in its own folder instead of the OS credential store, which is keyed by
server alone and would otherwise be overwritten by (and overwrite) the `jsync` CLI's login.

"Run in the background" is a Task Scheduler task for the current user (start at sign-in, restart on failure; no administrator).
A Cloud Filter sync root belongs to a user session, so the mount is not a SYSTEM service; the hub is a real Windows service.

State lives in `%LOCALAPPDATA%\jsync Drive\` (`settings.json`, `history.json`, `node\`, `logs\`); the hub in
`%ProgramData%\jsync Drive\hub\`. `JSYNC_APP_HOME` moves all of it (tests).

## Build and run

    .\build-engine.ps1          # Go engine -> engine\   (needs Go and ../filesharer)
    dotnet build src\JsyncDrive
    .\publish.ps1               # dist\jsync-drive-win-x64\ + zip (self-contained)
    .\install.ps1               # (from the published folder) per-user install + Start menu shortcut

`python tools\check-strings.py` lists English texts without a Spanish translation.

## Tests

`tests/EngineSmoke` drives the engine layer (login, upload/download, colours, pins, search) against a hub you run locally
(`jsyncd init` + `jsyncd`, see "As hub"). The UI was checked by hand and with UI Automation.

Not tested: the UAC-elevated service install and a real phone as the other device.

## Engine

`build-engine.ps1` builds `jsyncnode`, `jsync` and `jsyncd` from the jsync Go repository (`-Source <path>`, default: the `filesharer` folder next to this one).

## Credits

Icons are Material Design Icons (Apache-2.0), see NOTICE.
