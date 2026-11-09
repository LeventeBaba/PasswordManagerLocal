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
- [Release signing](RELEASE_SIGNING.md) covers Android APK publishing and Windows Authenticode.
- [Third-party notices](THIRD_PARTY_NOTICES.md) contains attribution shipped with the Windows installer.
- [Privacy policy](Privacy/PrivacyPolicy.en.md) · [Hungarian policy](Privacy/PrivacyPolicy.hu.md) · [Public privacy and account-deletion pages](Privacy/Public/index.html).

Keep architecture docs focused on responsibilities, boundaries, and the reasons for them. Link to source for identifiers, limits, and detailed behavior.
