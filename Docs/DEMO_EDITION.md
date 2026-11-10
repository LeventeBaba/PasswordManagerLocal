# Password Manager Local Demo

The `Demo` branch carries the restricted edition.

## Implementation map

|Step|Location|
|-|-|
|Define edition quotas separately from integrity and request limits|[DemoLimits](../Common/Contracts/Constants/DemoLimits.cs)|
|Check vault growth on create and on the destination of copy or move|[Backend services](../Common/Backend/Services/)|
|Count local accounts during registration and incoming enrollment, and count active device authorizations before signing an addition|[Registration](../Common/Backend/Services/UserRegistrationService.cs) and [enrollment](../Common/Backend/Services/DeviceEnrollmentService.cs)|
|Apply the same vault quotas to normal incoming snapshot merges|[Snapshot merge](../Common/Backend/Services/UserDataBundleSyncService.cs)|
|Brand the installed apps, package outputs, About page and policy|[Android](../Android/Frontend/), [Windows publishing](WINDOWS_INSTALLER.md), and [privacy](Privacy/Public/index.html)|

## Quota behavior

|Quota|Limit|
|-|-:|
|Saved passwords per account|20|
|Tags per account|10|
|Custom colors per account|10|
|Local accounts per installation|3|
|Active devices per account, including the current device|3|

* Existing passwords, tags and colors above the Demo limits remain usable. A user can read, edit, or delete them and can copy or move items to another local profile with sufficient room. New saves and transfers that exceed the receiving quota are rejected as a whole. The seven starter colors on new UI-created profiles count toward the ten saved custom colors.
* Both registration and enrollment import check capacity inside the database write transaction. Demo senders also check the target's account capacity before committing a new device link. If capacity changes after that check, the target rejects the import and the sender can retry the same committed enrollment when there is room. A retry of a previously committed addition uses the existing device slot.
* Live items count toward vault limits. Deleted items and synchronization history use their existing retention rules. Removed device authorizations free a device slot. A device already authorized for an enrollment awaiting transfer occupies a slot.
* Enrollment transfers an existing vault, and integrity recovery can restore an existing vault. Regular synchronization defers an incoming snapshot if applying it would introduce live items above a Demo limit. That snapshot stays pending for a later merge, so devices that add entries independently while offline can temporarily show different entries until there is room to synchronize them.
* The Demo enforces its device quota when it creates a membership addition or imports enrollment. Existing signed membership history continues to replicate. Use Demo builds on all linked devices to apply the same quotas throughout a profile.

## Installation and publishing

* Android uses `com.leventebaba.passwordmanagerlocaldemo` and a separate app sandbox from the earlier Android package. Enroll the Demo on a device if you want to transfer an existing vault through the app.
* The Windows Demo installs in `%ProgramFiles%\\PasswordManagerLocalDemo` alongside the full edition. Both share the same per-user vault, preferences, and instance locks. Exit the running edition from the notification area before opening the other one.
* The Windows launcher is `PasswordManagerLocalDemo.exe`. The MSI and portable ZIP are `PasswordManagerLocalDemo.msi` and `PasswordManagerLocalDemo-win-x64.zip`. The Demo icon is generated from the shared SVG.
* The app's online policy links open the localized files on the `Demo` branch. For a store listing, publish the generated HTML under `Docs/Privacy/Public` at the Demo's public policy URL.

## Verification checklist

* On both platforms, fill each vault quota, confirm one more item fails, and try both copy and move into a profile with insufficient room. Check that failed moves leave the source intact.
* On Windows, open an existing vault above the quota and verify that existing items can be opened, edited and removed. Verify that new items can be saved after the count falls below the quota.
* Register three accounts and try a fourth registration and incoming enrollment. Enroll a single account on three devices and confirm that adding a fourth fails. Disconnect one device and retry.
* Add records offline on different devices so the union would exceed a quota, then reconnect and confirm that a device keeps its local records and the incoming update stays pending.
* Verify Windows managed and Native AOT publishing, shortcuts, MSI branding, and preserved data on upgrade. Install both Windows editions, switch between them, and verify that removing the Demo through Installed apps leaves the full edition and the shared vault available.
* Verify Android launcher name, app ID, service startup and the localized About and privacy pages.

