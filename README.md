# FiveM Manager

A small Windows app for switching which Rockstar account FiveM is linked to, and
launching servers that need a specific game build or pure mode.

## Install

Download **FiveMManager-win-Setup.exe** from the
[latest release](https://github.com/zuhmoon/fivem-manager/releases/latest) and run it.

Windows will show *"Windows protected your PC"* the first time — the installer isn't
code-signed. Click **More info** then **Run anyway**. You only see this once; the app
updates itself after that.

No prerequisites: the .NET runtime is bundled. It installs per-user, so there's no
admin prompt, and it puts shortcuts on your desktop and Start menu.

## What it does

**Accounts** — Save the Rockstar account FiveM is currently linked to under a name, then
switch between saved accounts without signing in again each time. It snapshots the
`ros_*` files in `FiveM.app\data\game-storage`. Close FiveM before switching; those
files are locked while it runs.

**Servers** — Keep a list of servers with their join link, pure mode and game build.
Launch and join in one click. Servers that need a build or pure mode open FiveM first,
then join once it's up — there's a countdown you can skip or cancel.

**Clear cache** — Deletes `cache`, `server-cache`, `server-cache-priv` and `nui-storage`,
the usual fix for stuck loading screens and missing textures. Leaves `game-storage`
alone, so your Rockstar link survives.

Settings live in `%APPDATA%\FiveMManager\config.json`.

## Building it yourself

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```
dotnet run --project FiveMManager.csproj
```

`--selftest` runs the built-in checks and prints `SELFTEST OK`. `--updatecheck` prints
what the updater sees, for when updates aren't arriving.

`make-icon.ps1` regenerates `logo.ico` from the same geometry the in-app logo uses.
