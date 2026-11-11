# SUNMI Print

[![Windows-Installer herunterladen](https://img.shields.io/badge/Windows-Installer%20herunterladen-17664D?style=for-the-badge)](https://github.com/emiliaplays/sunmi-print/releases/download/v1.0.0/SunmiPrint-Setup-1.0.0.exe)

Windows-App für den eingebauten 58-mm-Bondrucker des SUNMI V2. Sie druckt Texte und Bilder (PNG, JPEG, BMP) und enthält fünf bearbeitbare Vorlagen. Der Installer enthält auch einen fiktiven Verkaufsbeleg für ein Home-AI-Rack.

## Installieren und drucken

1. Lade `SunmiPrint-Setup-1.0.0.exe` unter **Releases** herunter und starte den Installer. Er benötigt Windows mit .NET Framework 4.8 oder neuer und installiert die App für dein Windows-Konto.
2. Wähle im Installer den optionalen Download der Android Platform-Tools, falls ADB fehlt. Du kannst die [Platform-Tools](https://developer.android.com/tools/releases/platform-tools) auch separat installieren.
3. Aktiviere ADB auf dem SUNMI und wähle in der App unter **Einstellungen** WLAN oder USB. Für WLAN braucht die App die IPv4-Adresse des SUNMI und dessen ADB-Port (typischerweise `5555`). Beide Geräte müssen sich erreichen können.
4. Klicke **Verbinden** und bestätige die Debugging-Freigabe auf dem entsperrten SUNMI, falls sie erscheint. Beim ersten Verbinden installiert die App die eingebettete Android-Druckbrücke.
5. Wähle **Testdruck** oder lade eine Vorlage beziehungsweise eine eigene Datei und klicke **Drucken**.

Der Installer ist für das aktuelle Windows-Konto. Du findest die Deinstallation unter **Installierte Apps**. Die Verbindungseinstellungen und separat installierte Platform-Tools bleiben beim Deinstallieren erhalten.

## Build

Installiere [NSIS](https://nsis.sourceforge.io/Download) und führe in PowerShell `./build-installer.ps1` aus. Der Build lädt OpenJDK, die Android-Build-Tools und die SUNMI-Druckbibliothek und legt den Installer in `dist` ab. Mit `./build.ps1` kannst du die App ohne Installer bauen.

Der Build speichert den Android-Signaturschlüssel lokal in `%LOCALAPPDATA%\SunmiPrint\build-tools`. Verwende für Updates der Druckbrücke denselben Schlüssel. Falls eine Druckbrücke mit einem anderen Schlüssel signiert ist, entferne die vorherige Druckbrücke vom SUNMI, bevor du dich mit dem neuen Build verbindest.
