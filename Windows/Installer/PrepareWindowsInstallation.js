// Runs in the immediate MSI session, where the resolved directory and the
// installed component registration are still available.
function prepareValidation() {
    var reusingFolder = Session.Property("Installed") || Session.Property("WIX_UPGRADE_DETECTED");
    Session.Property("WixQuietExecCmdLine") = command(reusingFolder ? "validateInstalled" : "validate",
        Session.TargetPath("INSTALLFOLDER"));
    return 1;
}

function prepareCleanup() {
    var removing = Session.Property("REMOVE") === "ALL";
    var directory = Session.TargetPath("INSTALLFOLDER");
    if (removing) {
        var executable = String(Session.Installer.ComponentPath(Session.Property("ProductCode"),
            "{49247B48-4201-4F9A-80CC-F6494FE50601}") || "");
        var separator = executable.lastIndexOf("\\");
        if (separator < 0 || executable.substring(separator + 1).toLowerCase() !== "passwordmanagerlocaldemo.exe") {
            return 0;
        }
        directory = executable.substring(0, separator);
    }
    Session.Property("CommitWindowsIntegration") = command(removing ? "remove" : "upgrade", directory);
    return 1;
}

function command(operation, directory) {
    var systemDirectory = Session.Property("System64Folder");
    if (!/^[A-Za-z]:\\[^"\r\n]*\\$/.test(systemDirectory)) {
        throw new Error("Windows Installer did not provide the 64-bit system directory.");
    }

    // Encode the directory independently so even quotes and PowerShell syntax
    // in a folder name can only arrive as data in the maintenance script.
    var bootstrap = "$pmlOperation = '" + operation + "'\n" +
        "$pmlInstallDirectory = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" +
        encodeUnicode(directory) + "'))\n";
    // Pad with newlines so the two UTF-16LE Base64
    // segments join at a three-byte boundary without padding inside the result.
    while (bootstrap.length % 3 !== 0) {
        bootstrap += "\n";
    }
    return '"' + systemDirectory + 'WindowsPowerShell\\v1.0\\powershell.exe"' +
        " -NoProfile -NonInteractive -EncodedCommand " +
        encodeUnicode(bootstrap) + Session.Property("WindowsInstallationScript");
}

function encodeUnicode(value) {
    var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    var bytes = [];
    var result = "";
    for (var i = 0; i < value.length; i++) {
        var unit = value.charCodeAt(i);
        bytes.push(unit & 255, unit >> 8);
    }
    for (var offset = 0; offset < bytes.length; offset += 3) {
        var first = bytes[offset];
        var second = bytes[offset + 1] || 0;
        var third = bytes[offset + 2] || 0;
        result += alphabet.charAt(first >> 2) +
            alphabet.charAt(((first & 3) << 4) | (second >> 4)) +
            (offset + 1 < bytes.length ? alphabet.charAt(((second & 15) << 2) | (third >> 6)) : "=") +
            (offset + 2 < bytes.length ? alphabet.charAt(third & 63) : "=");
    }
    return result;
}

function checkLaunch() {
    // MSIRUNMODE_REBOOTATEND and MSIRUNMODE_REBOOTNOW
    if (!Session.Mode(6) && !Session.Mode(7)) {
        Session.Property("PmlCanLaunchApplication") = "1";
    }
    return 1;
}
