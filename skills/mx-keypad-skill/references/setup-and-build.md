# Setup and build

## Check what the service really runs

```bash
ls /Applications/Utilities/LogiPluginService.app/Contents/MonoBundle | grep -i "System.Runtime.dll"
dotnet --list-sdks
```

Logi Plugin Service 6.4.2 ships with .NET 10 even though Logitech's documentation says .NET 8. Its `PluginApi.dll` references `System.Runtime` version 10, so a project targeting `net8.0` fails to build. Install the .NET 10 SDK (a user install in `~/.dotnet` is fine) and set:

```xml
<TargetFramework>net10.0</TargetFramework>
```

If `dotnet` is killed with SIGKILL right after installing over an older SDK, the macOS code-signature cache is stale. In the SDK folder run `cp dotnet dotnet.fresh && mv -f dotnet.fresh dotnet`.

## Create the project

Install LogiPluginTool (`dotnet tool install --global LogiPluginTool`) and generate a skeleton with `logiplugintool generate <Name>`. The generated project:

- builds to `bin/Debug/bin/<Name>Plugin.dll`;
- writes `<Name>Plugin.link` into `~/Library/Application Support/Logi/LogiPluginService/Plugins/`, containing the path of the build folder, so the service loads the plugin straight from your build;
- asks the service to reload the plugin with `open loupedeck:plugin/<Name>/reload`.

The link file is ideal while developing. `dotnet clean` removes it. To ship, pack with `logiplugintool pack ./bin/Release ./<Name>.lplug4`.

To compile a second copy without installing it (for example a sample), override the folder: `dotnet build -p:PluginDir=/tmp/fake-plugins/`.

## Package metadata

In `package/metadata/LoupedeckPackage.yaml` set `supportedDevices: [LogitechCreativeFamily]` for the MX Keypad and MX Creative Console, and `pluginFolderMac: bin`. Add `DefaultIconTemplate.ict` in the same folder (see `drawing-keys.md`).

## Inspect the SDK when documentation runs out

`ilspycmd` may not install for the right runtime. A small console program using `System.Reflection.MetadataLoadContext` can list every public type, method and signature in `PluginApi.dll`, which is how the useful members below were found:

- `PluginDynamicCommand.AddParameter`, `ActionImageChanged`, `ProcessButtonEvent2`, `GetCommandImage`, `GetCommandDisplayName`
- `BitmapBuilder`, `BitmapImage.TryCreateFromSvg`
- `Plugin.ExecuteGenericAction(name, parameter, diff)` and `GenericActions.GetActionNames(true)` for page actions
- `Plugin.GetPluginDataDirectory()` for a writable data folder

List generic actions once at load and log them; the names on one install were `@NextTouchPage`, `@PreviousTouchPage`, `@FirstTouchPage`, `@LastTouchPage`, `@ChangeTouchPage` and `@ChangeWorkspace`.

## Lifecycle

- `Load()` runs on every reload. Start timers there or in the constructor.
- `Unload()` must stop every timer, file watcher and background task. The service does not stop them for you. If you skip this, each reload leaves the old copy running: old timers keep firing, old code keeps sending requests, and you see behaviour from builds you replaced hours ago. Restarting Logi Plugin Service clears old copies once.
- Plugin log: `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/<Name>.log`. It restarts on each load.
