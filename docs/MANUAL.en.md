# FC27 Pad Lock manual

This unofficial Windows x64 application guards ordinary keyboard/mouse input while FC27 is foreground. The observed setup is Steam FC27 with a USB DualSense Edge in menus. Other controllers and real match conditions remain unverified. Raw Input and injected input are not fully blocked.

## Install and start

Run the Setup executable from the distribution. It installs per user without administrator rights. The startup and desktop shortcuts are optional. Alternatively, extract the complete portable ZIP and run `Start.cmd`; keep all files together.

Open the app from its tray icon. Enable protection, or use the 30-second trial first in a menu. Use your controller from the first game screen. For the Steam game launch button, the app confirms the guard is ready before opening FC27.

Alt+Tab remains available for other applications. Ctrl+Alt+F12 pauses/resumes protection and stays available as an emergency shortcut. A custom shortcut is configured under Input settings. Cursor motion or an Esc label alone does not prove input reached the game; check actual game actions.

## Settings and reports

See [Application settings](SETTINGS.en.md) for themes, overlay controls, startup, notifications and shortcuts. The close button hides the panel. Right-click the tray icon for the quick panel. Exit all stops protection and helpers.

Save a diagnostic ZIP from Support. The ZIP includes selected status/settings fields and at most 200 event timestamps/kinds. Typed content, key numbers, cursor coordinates, device IDs and personal paths are excluded. Report on GitHub opens an issue page; attach the ZIP and submit it yourself. The app does not automatically send reports.

## Update and remove

Exit all before installing an update. The setup refuses to overwrite an existing version folder. Run the installed uninstaller through Windows Installed apps, or run `Uninstall.ps1` for the portable package. User preferences and game files are preserved. Executables are unsigned; published hashes identify the exact distribution files.
