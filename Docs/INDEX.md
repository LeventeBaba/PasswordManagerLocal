# Developer documentation

## Architecture

| Document | Scope |
| --- | --- |
| [Application architecture](Architecture/APPLICATION.md) | Project boundaries, backend ownership, Windows IPC, Android service hosting, persistence, and frontend state |
| [Synchronization](Architecture/SYNCHRONIZATION.md) | Eligibility, runtime supervision, discovery, transport, snapshot relay/merge, and causal tombstone collection |
| [Account and device lifecycle](Architecture/ACCOUNT_LIFECYCLE.md) | Enrollment, removal, key rotation, login projection, account deletion, and authenticated revocation delivery |
| [Integrity and recovery](Architecture/RECOVERY.md) | Canonical checkpoints, fault isolation, automatic reconstruction, and confirmed device-history repair |
| [Interactive project map](SOLUTION_PROJECT_ARCHITECTURE.html) | Default project-reference graph and platform process boundaries |

## Build and test

The full solution targets .NET 10 and includes Windows and Android projects. [global.json](../global.json) selects SDK 10.0.100 with `major` roll-forward; a newer installed SDK can be selected when the requested SDK range is unavailable. Android builds require compatible Android workloads. [Directory.Packages.props](../Directory.Packages.props) owns NuGet versions; project references omit inline package versions.

Commands below use PowerShell from the repository root on Windows. [Windows prerequisites](WINDOWS_INSTALLER.md#build-prerequisites) cover icon rendering, packaging, and Native AOT tooling.

```powershell
dotnet restore .\PasswordManagerLocal.sln
dotnet build .\PasswordManagerLocal.sln -c Debug --no-restore
dotnet test .\PasswordManagerLocal.sln -c Debug --no-build --no-restore --filter 'TestCategory!=SyncStress&TestCategory!=SyncStressExtended'
```

[Synchronization testing](Testing/MULTI_DEVICE_SYNC_STRESS.md) documents opt-in multi-device scenarios, focused regression filters, storage measurements, fixture ownership, and physical-device coverage.

## Publishing and maintenance

| Document | Scope |
| --- | --- |
| [Windows publishing and installer](WINDOWS_INSTALLER.md) | Managed/ReadyToRun/Native AOT output, SVG-derived branding, MSI integration, upgrades, and uninstall |
| [Release signing](RELEASE_SIGNING.md) | Android APK/App Bundle signing and optional Windows Authenticode |
| [Third-party notices](THIRD_PARTY_NOTICES.md) | Dependency attribution |

[PasswordManagerLocal.svg](../PasswordManagerLocal.svg) is the branding source. Windows generates the ICO through [GenerateWindowsIcon.ps1](../Tools/Windows/GenerateWindowsIcon.ps1). Android generates its adaptive icon through [GenerateAdaptiveIcon.xslt](../Tools/Android/GenerateAdaptiveIcon.xslt); the common frontend generates its About icon through [GenerateAboutIcon.xslt](../Tools/Branding/GenerateAboutIcon.xslt).

Protocol bounds and versions are defined in [SyncConstants](../Common/Backend/Constants/SyncConstants.cs), port defaults in [SyncProtocolDefaults](../Common/Contracts/Sync/SyncProtocolDefaults.cs), database compatibility in [DatabaseConstants](../Common/Backend/Constants/DatabaseConstants.cs), and tombstone/evidence limits in [TombstoneConstants](../Common/Backend/Constants/TombstoneConstants.cs).

## Privacy documents

- [English privacy policy](Privacy/PrivacyPolicy.en.md)
- [Hungarian privacy policy](Privacy/PrivacyPolicy.hu.md)
- [Published policy and account-deletion pages](Privacy/Public/index.html)
- [Hosted policy pages](https://leventebaba.github.io/PasswordManagerLocal/)
