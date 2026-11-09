# Release signing

Run `publish.cmd` from the solution root on Windows. `-A` publishes Android, `-W` publishes Windows, and `-F` publishes both. Android release APKs require a release keystore. Windows output is unsigned by default and uses Authenticode when requested with `-SignWindows`.

## Android APKs

Install the Android SDK Build Tools and a JDK. Set `ANDROID_SDK_ROOT` and `JAVA_HOME` if the tools are outside the usual locations. The default release keystore is `PublishKeys/PasswordManagerLocal-release.jks` with alias `passwordmanagerlocal`. For a different path or alias, copy the [signing settings template](../Tools/Signing/Signing.local.example.json) to `PublishKeys/Signing.local.json` and edit the local file. Keep the signing keys and secrets outside version control.

```powershell
.\publish.cmd -A
.\publish.cmd -A 16
```

Both commands target Android 16 (API 36). The first sets Android 10 as the minimum and publishes separate ARM64 and ARM32 APKs. The second sets Android 16 as the minimum and publishes ARM64 only. Output goes to `artifacts\publish\PasswordManagerLocal.Android.Frontend\android-<minimum>-plus`.

The publisher prompts privately for the keystore password and key password. Press Enter at the second prompt if both are the same. For unattended publishing, supply `PML_ANDROID_STORE_PASSWORD` and `PML_ANDROID_KEY_PASSWORD` through the process environment. Direct Release packaging uses `AndroidSigningStorePass` and `AndroidSigningKeyPass` as `env:` or `file:` references and does not prompt. The release workflow produces APKs per architecture.

The publisher checks private-key access before building. It rejects debug signing and uses the selected Android SDK `apksigner` to match the final APK's signer to the selected release certificate. `AndroidCertificateSha256` optionally pins the expected fingerprint.

An installed package requires a matching signing identity for an in-place update. Switching from a debug-signed installation to a release-signed installation requires uninstalling the debug package, which deletes that installation's local vault and secure-storage keys.

## Windows Authenticode

For signed output, use a trusted code-signing certificate with an accessible private key in the Windows certificate store. Install the certificate provider's software and Windows SDK Signing Tools. Copy the [settings template](../Tools/Signing/Signing.local.example.json) to `PublishKeys/Signing.local.json`.

| Setting | Value |
| --- | --- |
| `WindowsCertificateThumbprint` | Certificate thumbprint as 40 hexadecimal characters |
| `WindowsCertificateStoreLocation` | `CurrentUser` (default) or `LocalMachine` |
| `WindowsTimestampUrl` | Provider's RFC 3161 timestamp URL |
| `WindowsSignToolPath` | Optional absolute path to `signtool.exe`. The default search uses `PATH` and the Windows SDK. |

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey

.\publish.cmd -W -SignWindows
.\publish.cmd -W -NativeAOT -SignWindows
.\publish.cmd -F -SignWindows
```

Signing covers app-owned executables and managed assemblies in managed builds, or the unified executable in Native AOT. Third-party binaries retain their existing signatures. The publisher packages the signed files into the ZIP and signs the MSI after adding its language transform. Signing failures stop publication.

`reports\windows-signing-report.json` records the signing mode and the verified product signatures for a signed build. See [Windows publish output](WINDOWS_INSTALLER.md#publish-modes-and-outputs).
