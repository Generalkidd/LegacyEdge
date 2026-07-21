LEGACY EDGE DEVELOPER INSTALL

This is an unsigned, certificate-free loose development package. Each bundle
contains one architecture-specific Legacy Edge build. The installer reads the
package manifest, verifies that the payload matches this PC, and selects the
matching runtime dependencies automatically.

Windows Developer Mode permits loose registration, but it does not make an
unsigned MSIX installable by double-clicking it.

INSTALL

1. Enable Settings > System > For developers > Developer Mode.
2. Download the x64, ARM64, x86, or ARM bundle that matches the Windows system
   architecture.
3. Extract the ENTIRE ZIP to a normal local folder. Do not run it from the
   compressed-folder view in File Explorer.
4. Close Legacy Edge first if it is open. An update interrupts active tabs and
   downloads while Windows refreshes the package registration.
5. Double-click Install.cmd.

You can instead open PowerShell in this folder and run:

  .\Install.ps1

The script validates the package identity, version, architecture, and payload
before changing the current registration. It installs the matching Microsoft
runtime dependencies when needed, preserves existing Legacy Edge app data
during normal updates, and verifies the exact registered architecture,
location, development mode, and package health before reporting success.

IMPORTANT: This extracted directory becomes the live installed package
location. Keep it in place after installation. Moving, renaming, or deleting it
will break the app. To update, extract the new matching-architecture ZIP to a
permanent folder and run its Install.cmd; the known prior registration is
replaced while favorites, history, settings, sessions, and other local data are
preserved.

To deliberately erase Legacy Edge's local app data during installation, use:

  .\Install.ps1 -ResetApplicationData

To remove the app and its local data for the current user, run Uninstall.ps1.

The standalone unsigned MSIX is included only as a build artifact. App Installer
still requires a package signature chained to a trusted certificate, even when
Developer Mode is enabled.
