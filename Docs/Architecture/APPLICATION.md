# Application architecture

PasswordManagerLocal maintains an encrypted local vault on each installation and synchronizes approved profiles directly between enrolled devices. Shared backend code owns account operations, persistence, cryptography, and synchronization. Platform code owns processes, operating-system key protection, and frontend connectivity.

## Project boundaries

| Project area | Responsibility |
| --- | --- |
| [Common/Contracts](../../Common/Contracts) | Endpoint interfaces, DTOs, notifications, and shared boundary types |
| [Common/Backend](../../Common/Backend) | Domain services, direct SQLite persistence, security, peer protocols, and runtime hosting |
| [Common/Frontend](../../Common/Frontend) | Avalonia views/view models, UI sessions, localization, themes, and refresh coordination |
| [Common/Preferences](../../Common/Preferences) | Application preference storage |
| [Windows](../../Windows) | Frontend, Agent, IPC, Endpoint RPC, OS integration, and packaging |
| [Android](../../Android) | Activity, private bound/foreground service, runtime owner, and OS integration |

The [project map](../SOLUTION_PROJECT_ARCHITECTURE.html) shows default project references and runtime boundaries.

## Shared runtime

[Hosting](../../Common/Backend/Hosting) and its [abstractions](../../Common/Backend/Abstractions/Hosting) coordinate backend startup, ownership leases, interactive sessions, and shutdown. Backend lifetime and synchronization lifetime are separate: a ready backend or active lease does not establish healthy synchronization.

`BackendRuntimeLifetimeCoordinator` tracks interactive and background reasons. The execution profile is interactive while the frontend owns a lease and background-only while only background synchronization owns one. [Synchronization eligibility](SYNCHRONIZATION.md#runtime-eligibility) independently determines active networking, delivery work, enrollment, and passive historical ingress.

[Background settings](../../Common/Backend/Configuration/BackgroundSync) are separate from the vault and UI preferences. The platform owner reads the desired setting before acquiring background ownership. A disabled background setting does not prevent foreground synchronization under an interactive lease.

## Windows runtime

The frontend owns UI. The Agent owns the backend, vault, peer networking, endpoint host, and tray integration. Requests cross named pipes.

| Publish mode | Process layout |
| --- | --- |
| Managed / ReadyToRun | Separate frontend and Agent executables |
| Native AOT | One executable; UI entry point and `--agent` entry point run as separate processes |

Both layouts retain separate instance locks and the same IPC boundary. Windows firewall integration targets the executable running the Agent role. [Publishing](../WINDOWS_INSTALLER.md) defines the product layout, sign-in registration, installation, and removal behavior.

### IPC and commit outcomes

[Windows/IPC](../../Windows/IPC) owns named-pipe transport, framing, handshake/authorization, instance coordination, and UI registration. [Windows/EndpointRpc](../../Windows/EndpointRpc) owns endpoint payloads, client/proxy behavior, and server dispatch. [EndpointOperationManifest](../../Windows/EndpointRpc/Contracts/Metadata/EndpointOperationManifest.cs) assigns stable operation IDs and commit models.

| Commit model | Boundary |
| --- | --- |
| `NoDurableMutation` | Read-only operation |
| `SingleAtomicCommit` | One authoritative commit or indivisible local mutation |
| `CommitThenFollowUp` | Durable commit followed by session, cache, or runtime work |
| `MultiStageRecoverableCommit` | Durable subset can precede a later failure |
| `ExternallyObservableSideEffect` | Process, network, listener, or session effect independent of a database commit |

[Dispatcher](../../Windows/EndpointRpc/Server/EndpointRpcDispatcher.cs) results distinguish `NotCommitted`, `PartiallyCommittedRecoveryRequired`, and `OutcomeUnknown` where applicable. Process restart requirements are separate from commit outcome. Authoritative readback or an operation-specific recovery path resolves uncertain effects; blind mutation replay is unsafe.

## Android runtime

[MainActivity](../../Android/Frontend/MainActivity.cs) binds to the private [PasswordManagerBackgroundService](../../Android/Frontend/Runtime/PasswordManagerBackgroundService.cs) in the app process. [AndroidRuntimeServiceHost](../../Android/Runtime/AndroidRuntimeServiceHost.cs) owns the backend composition and foreground/background leases.

Each activity attachment has a generation. Replacement/detachment revokes the old generation's endpoint and notification authority. Enrollment, database reset, and attachment-owned settings changes require the current authorized attachment. Activity closure cancels or drains work according to its commit boundary.

The [manifest](../../Android/Frontend/Properties/AndroidManifest.xml) declares a non-exported `connectedDevice` foreground service and boot/package-replacement receivers. Restoration respects the saved preference, protected-storage availability, first unlock, and Android service-start restrictions.

## Background ownership and health

| Platform | Required synchronization / retry | Intentional idle |
| --- | --- | --- |
| Windows | The enabled background preference retains the existing Agent/backend owner after frontend closure | The lightweight background lease remains for IPC and justified passive ingress; ordinary discovery is inactive |
| Android | Foreground/background ownership remains while eligible synchronization, actionable revocation delivery, or recoverable retry requires it | Foreground/background ownership is released; the enabled preference remains stored |

An attached Android activity can keep an idle backend alive and reactivate foreground ownership after a committed local change. With no owner, the service/backend can stop; historical traffic is unavailable until a supported local/restoration action starts it again. The app has no periodic WorkManager synchronization mode.

Platform status distinguishes enabled idle, startup, running synchronization, recoverable degradation, unavailability, and restart-required failure. Foreground notification presence and backend readiness are separate from synchronization readiness. Unsafe Android cleanup closes attachment authority and uses the host's process-termination path. Neither platform creates a second backend owner to bypass an unsafe runtime latch.

## Persistence and transactions

[SqliteSession](../../Common/Backend/Persistence/SqliteSession.cs) provides direct SQLite repository/unit-of-work transactions over SQLite3MC. [AppDatabaseInitializer](../../Common/Backend/Persistence/AppDatabaseInitializer.cs) creates the [schema](../../Common/Backend/Persistence/Schema/InitialSchema.sql), checks supported database versions, and performs structural integrity checks. [DatabaseEncryptionMigrator](../../Common/Backend/Persistence/DatabaseEncryptionMigrator.cs) handles supported legacy SQLCipher encryption separately from schema compatibility.

Windows DPAPI and Android Keystore protect the installation's database key. The vault file alone cannot transfer that OS protection to another installation. Enrollment transfers authorized profile data instead of copying protected local keys.

[Repositories](../../Common/Backend/Repositories) implement persisted reads/writes. Services own validation, lifecycle decisions, and multi-repository transactions. Per-user lifecycle ownership serializes canonical and authority-sensitive work. Durable mutation, checkpoint/publication state, and queue work commit together; transport activation follows commit. Post-commit failures preserve the operation's actual durable outcome.

Current storage controls include bounded protocol messages/inventories, latest-per-origin snapshot replacement, causal tombstone collection, and explicit evidence-processing limits. They do not impose a global lifetime bound on the database. User data, origin/epoch namespaces, control history, and retained revocation evidence have distinct growth paths. [Storage baseline tests](../Testing/MULTI_DEVICE_SYNC_STRESS.md#storage-baselines) measure those paths without treating constant file size as proof of bounded storage.

## Frontend state

The backend publishes scoped, sequenced change notifications. Windows carries them over IPC; Android delivers them through the authorized activity attachment. [FrontendRefreshCoordinator](../../Common/Frontend/Services/FrontendRefreshCoordinator.cs) coalesces refreshes, handles missed notifications/runtime replacement, and preserves in-progress edits. Device views consume cached presence; backend discovery/probing owns network work.

[ApplicationPreferences](../../Common/Contracts/Preferences/ApplicationPreferences.cs) stores language, theme, and presentation settings. The frontend applies localization/theme per UI session. The Agent reads its language independently for tray text. Presentation preferences, background configuration, and vault state remain separate stores.
