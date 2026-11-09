# Application architecture

PasswordManagerLocal stores a vault on each installation. The shared backend owns access to that vault and synchronization with enrolled peers. Each platform supplies its runtime host, key protection, and frontend connection. This keeps account, storage, and sync behavior in the same code on Windows and Android while allowing each platform to manage its own process and service lifetime.

## Project boundaries

| Area | Responsibility |
| --- | --- |
| [Common/Contracts](../../Common/Contracts) | Endpoint contracts, DTOs, and other types shared across the frontend and backend boundary |
| [Common/Frontend](../../Common/Frontend) | Avalonia views, view models, UI sessions, and refresh coordination |
| [Common/Backend](../../Common/Backend) | Account and vault services, persistence, cryptography, peer synchronization, and shared runtime hosting |
| [Common/Preferences](../../Common/Preferences) | Shared application preference persistence |
| [Windows](../../Windows) | Desktop frontend, Agent, IPC, Endpoint RPC, platform integration, and packaging |
| [Android](../../Android) | Activity, bound service, Android runtime hosting, and platform integration |

The [interactive project map](../SOLUTION_PROJECT_ARCHITECTURE.html) shows the default project-reference graph and the runtime process boundaries.

## Shared backend runtime

[Hosting](../../Common/Backend/Hosting) composes services and coordinates runtime leases, interactive session authority, and startup and shutdown. The Windows and Android runtime owners use [hosting abstractions](../../Common/Backend/Abstractions/Hosting). [Persistence](../../Common/Backend/Persistence) owns vault file operations, including reset cleanup, while [background sync configuration](../../Common/Backend/Configuration/BackgroundSync) can be read before the vault opens.

## Windows runtime

The frontend owns the desktop UI. The Agent owns the backend, the vault, and peer network operations. The frontend sends requests over named pipes. With background sync enabled, the Agent can continue after the UI exits and start at sign-in.

Managed and ReadyToRun builds ship separate frontend and Agent executables. Native AOT ships one executable with a UI entry point and an `--agent` entry point. The two roles still run in separate processes with separate instance locks and the same IPC boundary. The app's fixed elevated firewall setup targets the executable that runs the Agent role.

### IPC and endpoint outcomes

[Windows/IPC](../../Windows/IPC) owns pipe transport, framing, handshake and authorization, instance coordination, and UI registration. [Windows/EndpointRpc](../../Windows/EndpointRpc) owns the endpoint client, contracts, and server dispatch. Generic transport and framing coverage lives in [Tests.IPC](../../Windows/Tests.IPC). Endpoint-specific behavior lives in [Tests.EndpointRpc](../../Windows/Tests.EndpointRpc).

The [operation manifest](../../Windows/EndpointRpc/Contracts/Metadata/EndpointOperationManifest.cs) assigns stable IDs and commit models. The [dispatcher](../../Windows/EndpointRpc/Server/EndpointRpcDispatcher.cs) tracks invocation and reports the observable outcome.

| Commit model | Meaning |
| --- | --- |
| `NoDurableMutation` | A read-only operation |
| `SingleAtomicCommit` | One authoritative commit or indivisible local mutation |
| `CommitThenFollowUp` | A commit can succeed before required session, cache, or runtime work fails |
| `MultiStageRecoverableCommit` | A later stage can fail after a known durable subset |
| `ExternallyObservableSideEffect` | A process, listener, network, or session change can happen independently of a database commit |

A rejection known to precede execution can return `NotCommitted`. A known durable subset can return `PartiallyCommittedRecoveryRequired`. An operation that may have taken effect can return `OutcomeUnknown`. A restart requirement is separate from the outcome. Resolve uncertainty by reading authoritative state or using the operation's recovery flow. For example, [enrollment transfer](ACCOUNT_LIFECYCLE.md#enrollment) can be retried after the signed addition commits.

## Android runtime

[MainActivity](../../Android/Frontend/MainActivity.cs) owns the frontend session. It binds to a private [PasswordManagerBackgroundService](../../Android/Frontend/Runtime/PasswordManagerBackgroundService.cs) in the same app process. The service hosts the backend through [AndroidRuntimeServiceHost](../../Android/Runtime/AndroidRuntimeServiceHost.cs). Each activity attachment has a generation. Detachment or replacement revokes the old generation's endpoint authority and notification access.

| Active owners | Runtime profile |
| --- | --- |
| Interactive UI | Interactive, through the bound service |
| Interactive UI and background sync | Interactive, with the background-sync foreground service and notification |
| Background sync | Background, for regular sync with enrolled peers |
| None | Runtime stops after the last owner releases it |

Enrollment and database reset require the current interactive attachment. When the activity closes, work is canceled or drained according to its commit boundary. The [manifest](../../Android/Frontend/Properties/AndroidManifest.xml) declares the non-exported `connectedDevice` foreground service and boot and package-replacement restoration. Restoration depends on the saved preference, the device's unlock state, and Android's service-start rules.

## Vault persistence

The backend accesses SQLite directly through SQLite3MC. The [database initializer](../../Common/Backend/Persistence/AppDatabaseInitializer.cs) creates the [baseline schema](../../Common/Backend/Persistence/Schema/InitialSchema.sql) or opens an existing vault and checks database integrity. The [encryption migrator](../../Common/Backend/Persistence/DatabaseEncryptionMigrator.cs) handles supported legacy SQLCipher storage. Windows DPAPI and Android Keystore protect the local database key. The vault file on its own cannot be opened on another installation.

[Repositories](../../Common/Backend/Repositories) read and write persisted data. Backend services own validation, lifecycle decisions, and transactions across repositories. A canonical change commits with its durable sync state and queue work. Transport activation follows the commit so peers cannot observe work for a rolled-back mutation.

## Frontend state

The backend sends scoped, sequenced change notifications. Windows carries them over IPC. Android delivers them through the active attachment. The [frontend refresh coordinator](../../Common/Frontend/Services/FrontendRefreshCoordinator.cs) coalesces work in the UI session, handles missed events or runtime replacement, and preserves in-progress edits. Device views read cached presence. The backend owns discovery and presence refresh.

[Application preferences](../../Common/Contracts/Preferences/ApplicationPreferences.cs) hold language, theme, and presentation settings. The UI applies its active localization and theme within the frontend session. The Windows Agent reads language independently for the tray. Background runtime configuration and vault state are stored separately from presentation preferences.
