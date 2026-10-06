# Application settings

The default overlay uses the compact layout with 50% opacity. Saved opacity and layout preferences are retained. Resetting display settings restores these defaults.

Double-click the tray icon to open the dashboard. Right-click it for the custom quick panel with current protection status, release shortcut and common actions. Closing the main window hides it; protection keeps running. Use Exit all in the quick panel to stop the application and release protection.

Settings are grouped by appearance, input, startup/notifications and support. Japanese and English can be changed immediately. Choose Dark, Light or System theme. The startup switch registers a shortcut for this application at Windows sign-in. The separate startup panel option controls whether the main window also opens. Warning notifications can be disabled without disabling protection or observation. The warning interval ranges from 5 to 300 seconds.

The overlay supports four corners, compact/detailed layouts, 25–100% opacity, 75–150% size and an 8–80 pixel margin. Choose the information you want to see. Opacity means 100% is fully opaque. The overlay stays click-through and does not activate the game or take focus. Reset appearance restores visual preferences while preserving your release shortcut and protection state.

Use at least two distinct modifiers from Ctrl, Alt, Shift and Win plus F1–F24 for a custom release shortcut. Emergency Ctrl+Alt+F12 always remains available. The dashboard and overlay show the actual shortcut reported by the guard; a requested shortcut that has not been applied is marked pending.

Preferences live in `%LOCALAPPDATA%\FC27PadLock\app-settings.json` and overlay visibility/corner/layout in `overlay-settings.json`. Updates preserve unknown fields. The example and JSON schema are in `config/`. `extensionSettings` can hold namespaced configuration for source extensions; it does not execute plugins or scripts.

Automatic updates are enabled by default. The app checks GitHub Releases on startup and every six hours; Check for updates runs a manual check. Only a published Release with its distribution ZIP and SHA256SUMS.txt is considered, rather than source commits. Preview versions can follow newer preview or stable releases; stable versions follow stable releases. Updates wait until FC27 exits, verify the archive checksum and package paths, back up existing files, and restore them if applying the update fails. Preferences are retained. Updates restart the app and enable protection. Development checkouts are excluded; use an installed or portable distribution.
