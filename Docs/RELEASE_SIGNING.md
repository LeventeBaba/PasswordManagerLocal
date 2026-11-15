# Release signing

[Tools/Publish.ps1](../Tools/Publish.ps1), invoked through `publish.cmd`, owns release publication. Commands run from the repository root on Windows.

| Mode | Command | Signing |
| --- | --- | --- |
| Android APK | `publish.cmd -A [10-16]` | Required release keystore; Android `apksigner` |
| Google Play App Bundle | `publish.cmd -G [10-16]` | Required release keystore; JDK bundle signing |
| Windows | `publish.cmd -W` | Unsigned by default; `-SignWindows` enables Authenticode |
| Windows and Android APK | `publish.cmd -F [10-16]` | Required Android signing; optional Windows signing |

## Android signing configuration

Android publication requires the Android SDK Build Tools and a JDK. `ANDROID_SDK_ROOT` and `JAVA_HOME` resolve nonstandard installations. The default keystore is `PublishKeys/PasswordManagerLocal-release.jks`, alias `passwordmanagerlocal`.

[Signing.local.example.json](../Tools/Signing/Signing.local.example.json) defines supported overrides in `PublishKeys/Signing.local.json`. Keystores, local settings, and signing secrets remain outside version control.

The publisher prompts privately for the store/key passwords; an empty key-password response reuses the store password. Unattended publishing reads `PML_ANDROID_STORE_PASSWORD` and `PML_ANDROID_KEY_PASSWORD` from the process environment. Direct Release packaging accepts `AndroidSigningStorePass` / `AndroidSigningKeyPass` as `env:` or `file:` references and does not prompt.

Preflight checks private-key access. Release validation rejects debug signing and verifies the finished package against the selected release certificate. `AndroidCertificateSha256` optionally pins the certificate fingerprint. Verification failure stops publication.

## Android APK output

All APK variants target Android 16 / API 36 and contain ARM64 (`arm64-v8a`) native code. The optional number selects the minimum supported Android version; default is Android 10 / API 29.

```powershell
.\publish.cmd -A
.\publish.cmd -A 16
```

Final APK path:

```text
artifacts\publish\PasswordManagerLocal.Android.Frontend\android-<minimum>-plus\PasswordManagerLocal-android-<minimum>-plus-arm64-v8a-signed.apk
```

An in-place update requires the installed signing identity. Changing from debug signing to a different release identity requires uninstall, which removes that installation's local vault and protected keys.

## Google Play App Bundle

```powershell
.\publish.cmd -G
.\publish.cmd -G 16
```

Bundles use the same keystore/password settings, target API 36, contain ARM64 native libraries, and request 16 KB ZIP alignment for generated APKs. The JDK signs the bundle; certificate/signature checks precede final output.

Default output:

```text
artifacts\publish\PasswordManagerLocal.Android.Frontend\google-play\android-10-plus\PasswordManagerLocal-google-play-arm64-v8a-signed.aab
```

`ApplicationVersion` in the [Android frontend project](../Android/Frontend/PasswordManagerLocal.Android.Frontend.csproj) is the Android version code. A Play release requires a higher version code than its predecessor. `ApplicationDisplayVersion` derives from the central product version.

## Windows Authenticode

Signed Windows publication requires a code-signing certificate with an accessible private key and Windows SDK Signing Tools. Certificate-provider software supplies any hardware/provider integration.

| Setting in `PublishKeys/Signing.local.json` | Value |
| --- | --- |
| `WindowsCertificateThumbprint` | 40 hexadecimal characters |
| `WindowsCertificateStoreLocation` | `CurrentUser` by default, or `LocalMachine` |
| `WindowsTimestampUrl` | RFC 3161 timestamp endpoint |
| `WindowsSignToolPath` | Optional `signtool.exe` path; default lookup uses `PATH` and Windows SDK locations |

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey

.\publish.cmd -W -SignWindows
.\publish.cmd -W -NativeAOT -SignWindows
.\publish.cmd -F -SignWindows
```

Managed builds sign app-owned executables and managed assemblies; Native AOT signs the unified executable. Third-party signatures remain intact. The signed product feeds ZIP/MSI packaging; the MSI is signed after its language transform is embedded. Signing failure stops publication.

`reports\windows-signing-report.json` records signing mode and verified product signatures. [Windows publishing](WINDOWS_INSTALLER.md#publish-modes-and-outputs) defines output locations and installer behavior.
