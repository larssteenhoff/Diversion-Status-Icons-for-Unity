# Diversion-Status-Icons-for-Unity
Diversion Status Icons for Unity

## 📷 Screenshot

![Diversion Status Icons for Unity](https://github.com/larssteenhoff/Diversion-Status-Icons-for-Unity/blob/main/Screenshot%202025-05-30%20at%2018.36.29.png?raw=true)

# Unity Diversion Status Overlay

This Unity Editor script provides visual overlays in the Project window to show the Diversion status of your assets (files and folders).

It uses the Diversion command-line interface (`dv`) to fetch the status of files in your project and displays corresponding icons on the items in the Project window, similar to how other version control integrations might work.

## Features

*   Displays icons for files with "added", "modified", "deleted", "conflicted", and "uptodate" statuses.
*   Displays a generic "changed" icon on folders that contain files with any status other than "uptodate".
*   Includes a manual refresh option in the Unity Editor menu (`Tools/Diversion/Refresh Status`).
*   Includes a Project Settings page to adjust the automatic refresh delay (`Project Settings/Diversion Overlay Icons`).
*   Includes context menu items in the Project window under `Assets/Diversion` for:
    *   `Refresh Asset icon`: Forces a Unity asset re-import for the selected item(s).
    *   `Revert Selected`: Attempts to reset local changes for the selected file using `dv reset`. (Note: This menu item is currently commented out in the script).

## Installation

1.  Make sure you have the Diversion CLI installed and configured on your system.
2.  Place the `DiversionStatusOverlay.cs` script inside an `Editor` folder within your Unity project's `Assets` directory (e.g., `Assets/Editor/DiversionStatusOverlay.cs` or `Assets/Diversion/Editor/DiversionStatusOverlay.cs`). If you put it in `Assets/Diversion/Editor/`, you might need to create these folders.
3.  Ensure the `DiversionCLIPath` static variable in the script is set to the correct full path of your `dv` executable. The default path is `/Users/macstudio/.diversion/bin/dv`, but you should verify this for your setup.
4.  Restart the Unity Editor to ensure the script is compiled and the menu items are registered.

## Usage

*   Status icons should appear automatically on assets in the Project window based on their Diversion status.
*   The icons will refresh automatically after asset changes are detected by Unity or based on the configured refresh delay.
*   You can force a manual refresh via `Tools/Diversion/Refresh Status`.
*   Adjust the refresh delay in `Project Settings/Diversion Overlay Icons`.
*   Use the context menu items under `Assets/Diversion` as needed (if not commented out in the script).

## Requirements

*   Unity Editor (tested with Unity 6, but should work with recent versions supporting `EditorApplication.projectWindowItemOnGUI` and `SettingsProvider`).
*   Diversion CLI (`dv`) installed and configured.

## Configuration

*   **Diversion CLI Path**: Edit the `DiversionCLIPath` string in the script to match the absolute path of your `dv` executable.
*   **Refresh Delay**: Adjust the delay for automatic status refreshes in `Project Settings/Diversion Overlay Icons`.

## Troubleshooting

*   If menu items or icons don't appear, check the Unity console for compilation errors in `DiversionStatusOverlay.cs`. Resolve any errors and restart Unity.
*   Ensure the `DiversionCLIPath` is correct and the `dv status --porcelain` command works correctly in your terminal from the project root directory.
*   If the "Revert Selected" command fails, check the Unity console for output from the Diversion CLI. The error message may provide clues (e.g., incomplete sync).
![Diversion Status Icons for Unity](https://github.com/larssteenhoff/Diversion-Status-Icons-for-Unity/blob/main/settings.png?raw=true)
