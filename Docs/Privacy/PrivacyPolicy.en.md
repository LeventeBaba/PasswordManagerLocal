# Privacy Policy — Password Manager Local Demo

Version 1.1 · Effective from: 2026-11-07

> Privacy questions and requests can be sent to leventebaba.dev@gmail.com.

Your profiles and passwords are stored on your devices. Enrolled devices exchange data directly, including in the background when enabled. The app has no developer-operated vault server or automatic telemetry. If you email the developer, the information you choose to send is handled separately.

## 1. About this policy and privacy contact

Password Manager Local Demo is developed by Levente Baba in Hungary. This policy covers the installed Windows and Android app and the developer email channel for feedback, bug reports and privacy requests. You can reach the developer at leventebaba.dev@gmail.com.

Do not include master passwords, stored passwords, connection codes, key files or vault databases in an email. App stores, operating systems and external websites have their own privacy practices.

## 2. Profile information and required fields

The current registration form requires a username, first name, last name, email address, master password and password confirmation. Remember me on this device is optional. Without the required fields, registration cannot complete; Settings and the information pages remain available without registration.

The app uses the username and master password for local sign-in and account protection. It stores the profile’s username, names, email, registration time, registration time zone, registration device type and update information on your devices. The first name is used in the app’s account menu. The first name, last name and email address appear in the profile details. The app does not send registration or verification emails, use that address for marketing, or offer an email-based master-password reset. The master password is used to derive authentication/key material; it is not a message sent to a developer account service.

Keep your username private. Manual sign-in requires both your username and master password. The app has no forgotten-master-password reset, and the developer cannot look up your username, reset your password, or restore your vault. If you can still open your profile on one of your devices, its Account overview shows your username.

## 3. Passwords and other content you enter

The app stores password entries, including their names, descriptions, password values, tags, colors and creation/update information, so that you can view, edit and organize them. A description may contain personal information you choose to enter. Data comes from your input, actions in the app and devices enrolled into the same profile; it is not obtained from public data brokers.

The feature called Export copies or moves selected entries and related tags/colors to another eligible local profile. It is not a general plaintext-file export. A copy in the destination becomes that profile’s data and may synchronize with its devices. Deleting the source does not delete a destination copy.

## 4. Device and synchronization information

To enroll devices, authenticate peers and reconcile changes, the app processes app-generated account/device identifiers, device names and types, public keys and certificate fingerprints, network addresses/ports, membership history, timestamps, version information, and trust/block/error status. It also stores pending encrypted updates and records of deletions so that changes are not silently lost or resurrected. For each device, the app records the two most recent sign-in times for the profile. It displays them in device details and can synchronize them as encrypted device data.

These identifiers are synchronization identifiers, not an advertising profile. The app does not request an advertising ID, contacts, microphone recordings or GPS location. Network interfaces and local addresses are used to discover and connect devices, not to provide a location-tracking feature.

## 5. Local storage, encryption and remembered sign-in

Profile and vault data are held in a local encrypted database, with additional encrypted account payloads and key material. Windows uses protection tied to the current Windows user for protected keys; Android uses Android Keystore. App preferences such as language, theme and animations are stored locally outside the vault. Vault encryption does not cover settings stored separately, operating-system diagnostics or observable network metadata.

A copy of the encrypted database file cannot be opened by itself. The app also needs the original database key, protected by Windows data protection under the Windows account that created it or by the Android Keystore for the original app installation. Copying app files to another device does not transfer that OS protection. Use in-app enrollment to bring your profile to another device while you still have access.

Remember me on this device saves protected local key material so the app can restore access without you re-entering the master password. A person who can use your unlocked device or operating-system account may therefore be able to open your passwords. Turn it off and sign out when appropriate; protect the device itself. Usable data must be decrypted in memory while the relevant operation runs. Encryption cannot prevent every loss, malware attack, screenshot or disclosure on an unlocked device.

## 6. Connections and who receives data

The app exchanges profile and vault data directly with devices you approve through enrollment. During a Demo-to-Demo enrollment, the receiving device uses the app-generated account ID over the enrollment connection to check whether it has room for the account. It has no developer-operated account, vault, or relay server. The app does not automatically send your profile or vault data to the developer or to devices you have not approved. Enrolled devices hold their own copies and can relay authenticated information about known peers. Enroll only devices you own or control.

Peer synchronization uses TLS and verifies the other device. To find peers, the app sends signed or authenticated UDP discovery messages on the local network. Other devices on that network, including ones you have not enrolled, can observe discovery metadata such as IP addresses, app-generated device IDs or enrollment-session IDs, and traffic timing. Discovery messages do not contain vault contents.

## 7. Background operation and platform permissions

When background operation is enabled and available, the Windows agent or Android service can continue discovery, peer connections and exchanging encrypted updates while the main window is closed. Encrypted updates may wait locally until an authorized session can apply them. This is device-to-device synchronization, not a telemetry upload to the publisher. Manage background operation in Settings; stopping it does not erase previously stored data or copies on peers.

Android declares Internet, network/Wi-Fi state and multicast permissions for connectivity/discovery; camera permission for QR scanning; and foreground connected-device service, notification and boot-completed permissions for background service operation and restoration. A service notification can indicate background activity. The camera requires runtime permission. Denying camera access leaves manual code entry or image selection available. OS restrictions can limit background operation.

On Windows, if the required firewall rules are missing, the app can offer to configure them. Changes require your approval in the app and administrator approval in Windows. Setup adds or replaces rules for the Password Manager Local Demo Agent, allowing inbound TCP 26688 for enrollment and synchronization and UDP 26689 for discovery. These inbound rules cover Windows network profiles and do not filter by remote IP address. Outbound rules are added only for enabled profiles that block outbound traffic by default. The app changes only its own rules and does not change the firewall’s enabled state. Declining setup can prevent peer connections, while your local vault remains available.

## 8. Camera, QR images and selected files

On Android, QR scanning reads camera preview frames locally to decode an enrollment code. The scanner does not implement a recording or developer upload service. Choose QR image reads the selected file and decodes it on your device. Manual code entry is another option. An enrollment code authorizes a security-sensitive connection; keep it and any image containing it private.

If you capture the QR code in a screenshot or save a copy outside the app, it is separate from the vault and follows the storage and backup rules of the location you choose. A system file picker or file provider may itself access a cloud-backed location if you select one. Deleting a profile does not automatically remove images you saved elsewhere.

## 9. Clipboard and visible information

Copying a password or other text writes it to the operating-system clipboard in readable form. Other applications, clipboard history or clipboard synchronization can retain or expose it according to your system settings. The current app does not implement a timed clipboard-clear guarantee. Clear sensitive clipboard content and review your system’s clipboard settings yourself.

Information you reveal on screen can be seen by people nearby or captured by system tools. Hiding a password or avoiding a username in the shell header does not hide all account information throughout the app.

## 10. Retention, deletion and other copies

Profile and vault data stay on your devices until you change or delete them or remove the relevant app storage. There is no account-inactivity expiry in the app. Sign out ends a session; it does not delete the profile. To delete an account, sign in to that profile, open Profile → Security → Delete account, read the warning, enter the master password and confirm. This removes the local account’s mutable profile/vault and pending state and creates a signed deletion instruction for its other devices.

Deletion is not an instant remote wipe. Offline or unreachable devices apply the deletion only when they receive and accept it. Disconnecting a device is also not a guarantee of erasing its existing copy. Previously copied passwords, destination profiles, saved images, operating-system records and backups need separate attention.

The app deliberately retains account/device identifiers, signed deletion/control records and historical membership evidence to verify deletion and prevent resurrection. Permanent account-deletion barriers have no automatic expiry in the current implementation. Other synchronization deletion markers may be removed when the protocol’s acknowledgement conditions are met, rather than after a fixed number of days. Database reset removes local database/key files; it does not promise deletion of operating-system records or external copies. Logical deletion does not guarantee forensic overwriting of every storage block.

## 11. Self-diagnostics and error information

The app can check and, where supported, repair account and synchronization state locally and show the result or an error on screen. It does not write routine diagnostic logs or automatically send telemetry, error reports, or crash reports to the developer. The app does not notify the developer when something goes wrong. Your operating system or app store may handle its own crash or diagnostic records under its own policies.

If you find a bug, please report it to leventebaba.dev@gmail.com. Include the app version, platform, what you were doing, and steps to reproduce the issue. Leave out sensitive information such as usernames, passwords, connection codes, database files, and keys. If you attach a screenshot, cover or remove sensitive details first. The developer will investigate reports and fix issues where possible.

## 12. System backups and loss of access

The app does not provide a developer-hosted cloud backup or master-password recovery service. The Android build opts out of automatic app-data cloud backups and configures exclusions for automatic device-to-device transfers because its database key is protected by an installation-specific Android Keystore key. A normal full uninstall removes the private local database and the Keystore key; reinstalling starts a new installation. Operating-system or backup tools on other platforms may copy app files according to their own settings. Protected keys may not be usable in another installation or OS account.

Uninstalling, clearing app data, resetting the database or losing keys may permanently remove access. On Windows, uninstall behavior depends on the installer and may leave user data behind. These backup exclusions do not erase previously created backups or independently copied files. Copies on other devices are not automatically erased by a local uninstall. Keep control of those copies and do not rely on another device or a backup as a guaranteed recovery method.

## 13. External links, email and international transfers

The app contains no integrated ads, usage analytics, or automatic developer telemetry. Reading this policy or the bundled help does not contact a website. Opening a web link passes the address to your browser or system launcher. The site can receive connection information such as your IP address and browser details. App stores, backup providers, cloud file pickers and clipboard sync services have their own privacy practices.

If you send an email to the developer, your email provider and Google, which provides the developer’s Gmail inbox, handle delivery and storage. Email data can be processed outside the EEA. Google describes its transfer safeguards, including applicable adequacy decisions and standard contractual clauses, at https://policies.google.com/privacy/frameworks. You can ask the developer about safeguards applicable to your correspondence. Vault synchronization takes place directly between the devices you enroll.

## 14. How the developer handles email

Levente Baba is responsible for the personal data received in the developer inbox. This can include your email address, display name, the content and date of your message, and any attachments you choose to send. The developer uses it to answer questions, investigate bug reports and handle privacy requests.

Where the GDPR applies, handling ordinary support and feedback is based on the developer’s legitimate interest in responding and maintaining the app. The developer handles requests required by data protection law to comply with those legal obligations. Correspondence is kept as long as needed to resolve the matter and any follow-up, or longer where a legal obligation requires it. Sending a support email is optional. Your local profile email does not send a message to the developer.

## 15. Your controls, rights and complaints

You can read and change profile and password information through the app, choose Remember me on this device, manage background operation, enroll/disconnect devices and delete an account. Camera and notification permissions can be managed in Android settings. The current cross-profile Export feature is not a general GDPR data-portability export or a developer request portal. A developer cannot remotely read, recover or erase copies held only on your devices through a central service that does not exist.

Where the GDPR applies to a controller’s processing of your data, rights may include access, rectification, erasure, restriction, portability and objection, subject to their legal conditions. Where processing relies on consent, you may withdraw it without affecting prior lawful processing. You may complain to a supervisory authority, particularly where you live or work or where an alleged infringement occurred; in Hungary this is the Nemzeti Adatvédelmi és Információszabadság Hatóság (NAIH), https://www.naih.hu/.

For a question or to exercise rights over data handled by the developer, email leventebaba.dev@gmail.com. Describe what the request is about and what you would like the developer to do. You can manage data held only on your devices through the local controls described above.

## 16. Automated decisions and intended use

The app is designed for people managing their own credentials. It does not ask for a date of birth or verify age.

The app does not use advertising profiling or make automated decisions with legal or similarly significant effects. Local password-strength estimates, sync conflict handling and device security checks help the app operate.

## 17. Policy updates and availability

You can read this policy offline in Settings → Privacy Policy and from the registration screen before creating a profile.

The effective date and version at the top identify this policy. If data practices change, the developer will update the notice and communicate material changes as required.
