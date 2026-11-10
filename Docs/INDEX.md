# Developer documentation

## Architecture

| Topic | What it covers |
| --- | --- |
| [Application architecture](Architecture/APPLICATION.md) | Project boundaries, Windows and Android runtime ownership, vault persistence, frontend state, and Windows IPC |
| [Synchronization](Architecture/SYNCHRONIZATION.md) | Peer discovery, authenticated exchange, snapshot relay, deterministic merging, and tombstone collection |
| [Account and device lifecycle](Architecture/ACCOUNT_LIFECYCLE.md) | Enrollment, removal cutoffs, key rotation, username resolution, and account deletion |
| [Integrity and recovery](Architecture/RECOVERY.md) | Signed checkpoints, fault isolation, canonical reconstruction, and confirmed device-history repair |
| [Interactive project map](SOLUTION_PROJECT_ARCHITECTURE.html) | Default solution project references and runtime process diagrams |

## Publishing and maintenance

- [Windows publishing and installer](WINDOWS_INSTALLER.md) covers build modes, output paths, icon generation, MSI behavior, and installation diagnostics.
- Edit [PasswordManagerLocal.svg](../PasswordManagerLocal.svg) to update the app icon. Windows renders the full SVG into an ICO. The [Android build transform](../Tools/Android/GenerateAdaptiveIcon.xslt) uses the SVG's vault group and case colors to generate the adaptive icon shared by the launcher and splash screen.
- [Release signing](RELEASE_SIGNING.md) covers Android APK publishing and Windows Authenticode.
- [Third-party notices](THIRD_PARTY_NOTICES.md) contains attribution shipped with the Windows installer.
- [Published privacy and account-deletion pages](https://leventebaba.github.io/PasswordManagerLocal/) · [English privacy policy](https://leventebaba.github.io/PasswordManagerLocal/privacy-policy.en.html) · [Hungarian privacy policy](https://leventebaba.github.io/PasswordManagerLocal/privacy-policy.hu.html).
- Privacy policy files in this repository include the [English document](Privacy/PrivacyPolicy.en.md), the [Hungarian document](Privacy/PrivacyPolicy.hu.md), and the [generated site](Privacy/Public/index.html).

Keep architecture docs focused on responsibilities, boundaries, and the reasons for them. Link to source for identifiers, limits, and detailed behavior.
