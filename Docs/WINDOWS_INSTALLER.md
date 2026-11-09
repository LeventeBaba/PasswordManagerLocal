# Windows publishing and installer

## Build prerequisites

Publish on Windows x64 with the .NET SDK pinned in [global.json](../global.json). Native AOT also requires the Visual Studio C++ build tools and a Windows SDK.

The build renders [PasswordManagerLocal.svg](../PasswordManagerLocal.svg) into a multi-resolution ICO under `artifacts\branding`. The app, tray, and MSI share that icon. The generator uses Inkscape 1.4 or newer from `PATH` or its standard installation directory. Set `PML_INKSCAPE_PATH` to an absolute executable path for a portable installation. [Icon generation script](../Tools/Windows/GenerateWindowsIcon.ps1)

The installer uses the pinned WiX SDK and its UI and Util extensions. After reviewing the [WiX Toolset EULA](https://docs.firegiant.com/wix/osmf/), accept it for the build account from the solution root.

```powershell
dotnet build .\Windows\Installer\PasswordManagerLocal.Windows.Installer.wixproj -t:AcceptEula -p:EulaId=wix7
```

## Publish modes and outputs

Run the publish command from the solution root.

| Command | Output under `artifacts\publish` | Executable layout |
| --- | --- | --- |
| `publish.cmd -W` | `PasswordManagerLocal.Windows` | Managed UI and Agent executables |
| `publish.cmd -W -ReadyToRun` | `PasswordManagerLocal.Windows-ReadyToRun` | ReadyToRun UI and Agent executables |
| `publish.cmd -W -NativeAOT` | `PasswordManagerLocal.Windows-NativeAot\win-x64` | One executable with separate UI and Agent process roles |

Each output root contains `product\PasswordManagerLocal`, `PasswordManagerLocal-win-x64.zip`, `PasswordManagerLocal.msi`, and `reports`. The ZIP contains the product files. The MSI uses the product folder as input and adds the license, attribution, and installer integration. MSI versioning comes from `Version` in [Directory.Build.props](../Directory.Build.props), using three numeric fields within Windows Installer's limits. Add `-SignWindows` for [Authenticode signing](RELEASE_SIGNING.md#windows-authenticode).

The Native AOT executable stays beside its native DLLs. It starts the UI with no arguments. The UI launches the Agent role with `--agent --ui-requested`, and background sync uses `--agent --background` for sign-in startup. See [Windows runtime ownership](Architecture/APPLICATION.md#windows-runtime).

## Installer behavior

The MSI installs per machine under `%ProgramFiles%\PasswordManagerLocal` by default. First install can use a different protected folder on a fixed NTFS drive. Setup rejects user-writable paths, junctions, and symbolic links. The vault and preferences live in the user's Local AppData, separate from program files.

Setup includes Start menu shortcuts and an optional desktop shortcut, selected by default. An interactive install offers an unchecked option to open the app as the desktop user. That option is skipped when setup requires a restart.

English is the default installer language. The same MSI embeds Hungarian dialogs and shortcut labels. Its language is independent of the app setting. Select the Hungarian transform from the directory containing the MSI.

```powershell
msiexec.exe /i ".\PasswordManagerLocal.msi" TRANSFORMS=:1038
```

The publish modes share one installed product. An MSI of the same or a newer version replaces the existing program files in the installed folder. To move the program files, uninstall while keeping local data and choose the new folder on reinstall. Restart Manager tracks both executable paths for files-in-use handling. A successful upgrade updates matching sign-in entries and removes app-owned firewall rules for obsolete executable paths.

## Uninstall and diagnostics

- **Installed apps** removes the program and shortcuts and keeps local data. After uninstall commits, cleanup removes matching startup entries from loaded user hives and known app-owned firewall rules for that installation.
- **Start menu uninstall shortcut** offers to delete the current user's local app data. Keeping it is the default. Choosing deletion removes `%LOCALAPPDATA%\PasswordManagerLocal` before the MSI uninstall opens. Canceling that dialog cannot recover the data. Active instance locks or links in the data tree block deletion.

The publish root's `reports` folder contains `installer-build.binlog` and signing results. Native AOT also records `publish.binlog`. Use `msiexec.exe /i PasswordManagerLocal.msi /L*V install.log` or `msiexec.exe /x PasswordManagerLocal.msi /L*V uninstall.log` to log installation, removal, and integration cleanup. On a silent first install, `INSTALLFOLDER` selects a protected custom directory and `REMOVE=DesktopShortcut` omits the desktop shortcut.
