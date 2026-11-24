# Windows publishing and installer

## Build prerequisites

| Tool | Use |
| --- | --- |
| .NET SDK accepted by [global.json](../global.json) | Project target frameworks remain .NET 10; `major` roll-forward permits newer installed SDKs when required |
| Inkscape 1.4 or newer | SVG-to-ICO rendering |
| WiX SDK/extensions pinned in the project | MSI build and UI integration |
| Visual Studio C++ build tools and Windows SDK | Native AOT publishing |

[GenerateWindowsIcon.ps1](../Tools/Windows/GenerateWindowsIcon.ps1) renders [PasswordManagerLocal.svg](../PasswordManagerLocal.svg) into the shared multi-resolution `artifacts\branding\PasswordManagerLocal.ico`. The frontend, Agent, and MSI consume that output. Inkscape resolves through `PATH`, its standard installation directory, or `PML_INKSCAPE_PATH`. Generation is incremental, serialized, and atomic; it runs before Avalonia resource packaging in ordinary and design-time builds.

WiX EULA acceptance is associated with the build account:

```powershell
dotnet build .\Windows\Installer\PasswordManagerLocal.Windows.Installer.wixproj -t:AcceptEula -p:EulaId=wix7
```

[EULA reference](https://docs.firegiant.com/wix/osmf/).

## Publish modes and outputs

Commands run from the repository root on Windows x64.

| Command | Output root under `artifacts\publish` | Executable layout |
| --- | --- | --- |
| `publish.cmd -W` | `PasswordManagerLocal.Windows` | Managed UI and Agent executables |
| `publish.cmd -W -ReadyToRun` | `PasswordManagerLocal.Windows-ReadyToRun` | ReadyToRun UI and Agent executables |
| `publish.cmd -W -NativeAOT` | `PasswordManagerLocal.Windows-NativeAot\win-x64` | One executable with separate UI/Agent process roles |

Each root contains:

| Path | Contents |
| --- | --- |
| `product\PasswordManagerLocal` | Verified executable/native-library product layout |
| `PasswordManagerLocal-win-x64.zip` | Product files |
| `PasswordManagerLocal.msi` | Installer built from the verified product, license, attribution, and integration inputs |
| `reports` | Packaging, installer, and signing diagnostics |

Standalone project publishing is rejected by the build targets. [PublishWindowsProduct.ps1](../Tools/Windows/PublishWindowsProduct.ps1) stages, verifies, optionally signs, archives, and packages the product. `-SignWindows` enables [Authenticode](RELEASE_SIGNING.md#windows-authenticode); default Windows output is unsigned.

[Directory.Build.props](../Directory.Build.props) supplies the product version. MSI version validation requires three numeric fields within `255.255.65535`.

## Native AOT entry points

The unified executable remains beside its native DLLs. No arguments starts the UI. The UI starts the Agent role with `--agent --ui-requested`; sign-in background startup uses `--agent --background`. Roles retain separate process/instance ownership and communicate over IPC. See [Windows runtime](Architecture/APPLICATION.md#windows-runtime).

## Installation and upgrades

The MSI installs per machine under `%ProgramFiles%\PasswordManagerLocal` by default. First installation accepts a protected custom folder on a fixed NTFS drive. User-writable paths, junctions, and symbolic links are rejected. Vault/preferences remain in the user's Local AppData outside program files.

Setup creates Start menu shortcuts and an optional desktop shortcut selected by default. Interactive setup offers an unchecked launch option for the desktop user; launch is skipped when setup requires restart.

English is the default installer language. Hungarian dialogs/shortcut labels are embedded in the same MSI and are independent of the app language:

```powershell
msiexec.exe /i ".\PasswordManagerLocal.msi" TRANSFORMS=:1038
```

Managed, ReadyToRun, and Native AOT modes share one installed product. Same/newer-version installation replaces product files in the installed folder. Moving the folder requires uninstall/reinstall while retaining local data. Restart Manager tracks both executable paths. Successful upgrade updates matching sign-in entries and removes app-owned firewall rules for obsolete executable paths.

## Uninstall

| Entry point | Local-data behavior |
| --- | --- |
| Windows Installed apps | Removes product/shortcuts and retains local data |
| Start menu uninstall shortcut | Offers current-user local-data deletion; keeping data is the default |

Post-commit uninstall cleanup removes matching startup entries from loaded user hives and known app-owned firewall rules for the installation. Optional data deletion removes `%LOCALAPPDATA%\PasswordManagerLocal` before MSI uninstall opens. Canceling the later MSI dialog does not restore that data. Active instance locks or links in the data tree block deletion.

## Diagnostics

`reports\installer-build.binlog` records installer compilation. Native AOT also produces `publish.binlog`; signing status is recorded in `windows-signing-report.json`.

```powershell
msiexec.exe /i ".\PasswordManagerLocal.msi" /L*V install.log
msiexec.exe /x ".\PasswordManagerLocal.msi" /L*V uninstall.log
```

For silent first install, `INSTALLFOLDER` selects a protected custom directory and `REMOVE=DesktopShortcut` excludes that shortcut.
