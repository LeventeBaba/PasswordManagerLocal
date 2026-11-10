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
| `publish.cmd -W` | `PasswordManagerLocalDemo.Windows` | Managed UI and Agent executables |
| `publish.cmd -W -ReadyToRun` | `PasswordManagerLocalDemo.Windows-ReadyToRun` | ReadyToRun UI and Agent executables |
| `publish.cmd -W -NativeAOT` | `PasswordManagerLocalDemo.Windows-NativeAot\win-x64` | One executable with separate UI and Agent process roles |

Each output root contains `product\PasswordManagerLocalDemo`, `PasswordManagerLocalDemo-win-x64.zip`, `PasswordManagerLocalDemo.msi`, and `reports`. The ZIP contains the product files. The MSI uses the product folder as input and adds the license, attribution, and installer integration. MSI versioning comes from `Version` in [Directory.Build.props](../Directory.Build.props), using three numeric fields within Windows Installer's limits. Add `-SignWindows` for [Authenticode signing](RELEASE_SIGNING.md#windows-authenticode).

The Windows launcher is `PasswordManagerLocalDemo.exe`. The Native AOT executable stays beside its native DLLs. It starts the UI with no arguments. The UI launches the Agent role with `--agent --ui-requested`, and background sync uses `--agent --background` for sign-in startup. See [Windows runtime ownership](Architecture/APPLICATION.md#windows-runtime).

## Installer behavior

The Demo MSI installs per machine under `%ProgramFiles%\PasswordManagerLocalDemo` by default. The full edition uses `%ProgramFiles%\PasswordManagerLocal`. Both editions can be installed side by side. First install can use a different protected folder on a fixed NTFS drive. Setup rejects user-writable paths, junctions, symbolic links, and folders that overlap the registered full edition's folder.

Setup includes Start menu shortcuts and an optional desktop shortcut, selected by default. An interactive install offers an unchecked option to open the app as the desktop user. That option is skipped when setup requires a restart.

English is the default installer language. The same MSI embeds Hungarian dialogs and shortcut labels. Its language is independent of the app setting. Select the Hungarian transform from the directory containing the MSI.

```powershell
msiexec.exe /i ".\PasswordManagerLocalDemo.msi" TRANSFORMS=:1038
```

The Demo has a separate MSI upgrade identity, registry entry, and firewall rule group. A Demo MSI of the same or a newer version replaces only the installed Demo program files. Restart Manager tracks the Demo executable paths for files-in-use handling. A successful upgrade updates sign-in entries that point to the Demo and removes its firewall rules for obsolete executable paths.

If you installed the earlier Demo draft that used the full edition's MSI registration, remove that draft through Installed apps before installing this MSI. Installed apps keeps the vault.

The Windows editions share the encrypted vault, settings, and instance locks under `%LOCALAPPDATA%\PasswordManagerLocal`. Each UI connects to its own edition's Agent. To switch editions, exit the active app completely from the notification-area icon, then launch the other edition. If background sync is enabled, both editions share one sign-in startup entry and the Agent that runs updates it to point to its own edition.

## Uninstall and diagnostics

- **Installed apps** removes the program and shortcuts and keeps local data. After uninstall commits, cleanup removes matching startup entries from loaded user hives and known app-owned firewall rules for that installation.
- **Start menu uninstall shortcut** offers to delete the current user's local app data. Keeping it is the default. Choosing deletion removes the vault and settings shared by both editions in `%LOCALAPPDATA%\PasswordManagerLocal` before the MSI uninstall opens. Canceling that dialog cannot recover the data. Active instance locks or links in the data tree block deletion.

The publish root's `reports` folder contains `installer-build.binlog` and signing results. Native AOT also records `publish.binlog`. Use `msiexec.exe /i PasswordManagerLocalDemo.msi /L*V install.log` or `msiexec.exe /x PasswordManagerLocalDemo.msi /L*V uninstall.log` to log installation, removal, and integration cleanup. On a silent first install, `INSTALLFOLDER` selects a protected custom directory and `REMOVE=DesktopShortcut` omits the desktop shortcut.
