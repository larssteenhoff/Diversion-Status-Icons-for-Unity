using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System;

[InitializeOnLoad]
public static class DiversionStatusOverlay
{
	static Dictionary<string, Texture2D> statusIcons = new();
	static Dictionary<string, string> fileStatus = new();
	static Dictionary<string, string> folderStatus = new();

	// ✅ Set your full path to Diversion CLI here
	static readonly string DiversionCLIPath = "/Users/macstudio/.diversion/bin/dv";

	static double nextRefreshTime = 0;
	static bool refreshScheduled = false;

	const string DiversionRefreshDelayKey = "DiversionOverlay.RefreshDelay"; // EditorPrefs key for refresh delay
	static float defaultRefreshDelay = 10.0f; // Default refresh delay in seconds

	const string ShowRootFolderIconKey = "DiversionOverlay.ShowRootFolderIcon"; // EditorPrefs key for showing icon on Assets folder
	static bool defaultShowRootFolderIcon = false; // Default state for showing icon on Assets folder

	private static Process statusProcess;
	private static System.Threading.Thread statusProcessThread; // Keep track of the thread if needed, though not strictly used here
	private static string statusProcessOutput = "";
	private static string statusProcessError = "";
	private static bool statusProcessCompleted = false;

	static DiversionStatusOverlay()
	{
		LoadIcons();
		UpdateStatus();
		EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
		EditorApplication.update += OnEditorUpdate;
	}

	static void LoadIcons()
	{
		// Use Unity built-in icons for status overlays
		statusIcons["A"] = EditorGUIUtility.FindTexture("PackageBadgeNew");
		statusIcons["M"] = EditorGUIUtility.FindTexture("d_CollabEdit Icon");
		statusIcons["D"] = EditorGUIUtility.FindTexture("Collab.FileDeleted");
		statusIcons["C"] = EditorGUIUtility.FindTexture("d_CollabConflict Icon");
		statusIcons["U"] = EditorGUIUtility.FindTexture("Collab");

		// Add icon for moved/renamed files
		statusIcons["moved"] = EditorGUIUtility.FindTexture("CollabMoved Icon");

		// Support new Diversion status codes
		statusIcons["added"] = statusIcons["A"];
		statusIcons["deleted"] = statusIcons["D"];
		statusIcons["modified"] = statusIcons["M"];
		statusIcons["conflicted"] = statusIcons["C"];
		statusIcons["uptodate"] = statusIcons["U"];
	}

	static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
	{
		string path = AssetDatabase.GUIDToAssetPath(guid);
		Texture2D iconToDraw = null;

		string fileStat = null;

		// Prioritize status of the associated asset if it's a .meta file
		if (path.EndsWith(".meta"))
		{
			string assetPath = path.Substring(0, path.Length - ".meta".Length);
			if (fileStatus.TryGetValue(assetPath, out string assetStat))
			{
				fileStat = assetStat; // Use the status of the associated asset
			}
		}

		// If fileStat is still null, check the status of the current path
		if (fileStat == null)
		{
			fileStatus.TryGetValue(path, out fileStat);
		}

		// Check if it's a folder and has a status in folderStatus
		if (AssetDatabase.IsValidFolder(path))
		{
			bool showRootIcon = EditorPrefs.GetBool(ShowRootFolderIconKey, defaultShowRootFolderIcon);
			// Display folder icon if it has status and either it's not the root or showing root is enabled
			if (folderStatus.ContainsKey(path) && (path != "Assets" || showRootIcon))
			{
				// Use the specific folder icon
				iconToDraw = EditorGUIUtility.FindTexture("d_CollabChanges Icon");
			}
		}
		else // It's a file (or we are drawing the meta file item, but want the asset icon)
		{
			if (fileStat != null)
			{
				// Use the specific file status icon
				if (statusIcons.TryGetValue(fileStat, out Texture2D icon))
				{
					iconToDraw = icon;
				}
			}
		}

		if (iconToDraw != null)
		{
			// Allow up to 32px width and 16px height, but never scale larger than original, preserving aspect ratio
			float maxHeight = 16f;
			float maxWidth = 32f;
			float width = iconToDraw.width;
			float height = iconToDraw.height;
			float aspect = width / height;
			float drawHeight = Mathf.Min(maxHeight, height);
			float drawWidth = Mathf.Min(maxWidth, drawHeight * aspect, width);
			Rect iconRect = new(
				selectionRect.xMax - drawWidth,
				selectionRect.yMin + (selectionRect.height - drawHeight) * 0.5f,
				drawWidth,
				drawHeight
			);
			GUI.DrawTexture(iconRect, iconToDraw, ScaleMode.ScaleToFit);
		}
	}

	static void UpdateStatus()
	{
		// Prevent starting a new status process if one is already running		
		if (statusProcess != null && !statusProcess.HasExited)
		{
			//UnityEngine.Debug.Log("Diversion Status Overlay: Status update already in progress.");
			return;
		}

		fileStatus.Clear();
		folderStatus.Clear();

		ProcessStartInfo psi = new()
		{
			FileName = DiversionCLIPath,
			Arguments = "status --wait --porcelain", // Added --wait to wait for sync
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Application.dataPath.Replace("/Assets", "") // Unity project root
		};

		try
		{
			statusProcessOutput = "";
			statusProcessError = "";
			statusProcessCompleted = false;

			statusProcess = new Process();
			statusProcess.StartInfo = psi;
			statusProcess.EnableRaisingEvents = true; // Essential for Exited event
			statusProcess.Exited += (sender, args) => { statusProcessCompleted = true; };

			statusProcess.Start();

			// Start reading output asynchronously
			statusProcess.BeginOutputReadLine();
			statusProcess.BeginErrorReadLine();

			statusProcess.OutputDataReceived += (sender, args) => { if (!string.IsNullOrEmpty(args.Data)) statusProcessOutput += args.Data + "\n"; };
			statusProcess.ErrorDataReceived += (sender, args) => { if (!string.IsNullOrEmpty(args.Data)) statusProcessError += args.Data + "\n"; };

			//UnityEngine.Debug.Log("Diversion Status Overlay: Status update process started in background.");
		}
		catch (System.Exception ex)
		{
			// UnityEngine.Debug.LogWarning("Failed to run Diversion CLI: " + ex.Message);
			UnityEngine.Debug.LogError("Failed to start Diversion CLI status process: " + ex.Message);
			statusProcess = null; // Ensure process is null if starting failed
			statusProcessCompleted = true; // Mark as completed to avoid infinite waiting if starting failed
		}
	}

	static void ParseDiversionOutput(string output)
	{
		using StringReader reader = new(output);
		string line;
		string currentStatus = null;
		int count = 0;
		while ((line = reader.ReadLine()) != null)
		{
			line = line.Trim();
			if (line.EndsWith(":"))
			{
				// Section header, e.g. "New:"
				string section = line.Substring(0, line.Length - 1).ToLowerInvariant();
				switch (section)
				{
					case "new": currentStatus = "added"; break;
					case "deleted": currentStatus = "deleted"; break;
					case "modified":
						// For modified section, we might distinguish moves later
						currentStatus = "modified";
						break;
					case "conflicted": currentStatus = "conflicted"; break;
					case "uptodate": currentStatus = "uptodate"; break;
					default: currentStatus = null; break;
				}
				continue;
			}

			if (!string.IsNullOrEmpty(line) && currentStatus != null)
			{
				string path;

				// Handle renamed/moved files indicated by '->'
				int arrowIndex = line.IndexOf("->");
				if (arrowIndex != -1)
				{
					// Extract the new path (after the ->)
					path = line.Substring(arrowIndex + 2).Trim();
				}
				else
				{
					// Normal path line
					path = line.Trim();
				}

				string statusToApply = currentStatus;

				// If the status is 'modified' and the line contained '->', treat it as 'moved'
				if (currentStatus == "modified" && arrowIndex != -1)
				{
					statusToApply = "moved";
				}

				if (path.StartsWith("Assets"))
				{
					fileStatus[path] = statusToApply;
					count++;
				}
			}
		}
		UnityEngine.Debug.Log($"DiversionStatusOverlay: Parsed {count} file statuses.");
	}

	static void PropagateStatusToFolders()
	{
		// For each file with a status other than uptodate, propagate up the folder hierarchy
		foreach (var kvp in fileStatus)
		{
			string path = kvp.Key;
			string status = kvp.Value;

			// Only propagate if the file status is not uptodate
			if (status != "uptodate" && status != "U")
			{
				string folder = Path.GetDirectoryName(path).Replace('\\', '/');
				// Propagate up to the Assets root
				while (!string.IsNullOrEmpty(folder) && folder.StartsWith("Assets"))
				{
					// Simply mark the folder as having a change
					folderStatus[folder] = "changed"; // Value doesn't matter, just presence
					folder = Path.GetDirectoryName(folder).Replace('\\', '/');
				}
			}
		}
	}

	[MenuItem("Tools/Diversion/Refresh Status")]
	public static void ManualRefreshStatus()
	{
		UpdateStatus();
		EditorApplication.RepaintProjectWindow();
	}

	//[MenuItem("Diversion/Reset")] // Commented out - Keep the main menu item
	//[MenuItem("Assets/Diversion/Revert Selected")] // Commented out - Add the context menu item for files
	// The validation method below will ensure this only shows for files in the Assets folder
	public static void ResetStatus()
	{
		string selectedPath = AssetDatabase.GetAssetPath(Selection.activeObject);

		if (string.IsNullOrEmpty(selectedPath) || AssetDatabase.IsValidFolder(selectedPath))
		{
			UnityEngine.Debug.LogWarning("Diversion Status Overlay: Please select a file to reset.");
			return; // Exit if nothing or a folder is selected
		}

		UnityEngine.Debug.Log($"Diversion Status Overlay: Attempting to reset file: {selectedPath}");

		// --- Check if sync is complete before attempting reset ---
		ProcessStartInfo statusPsi = new ProcessStartInfo()
		{
			FileName = DiversionCLIPath,
			Arguments = "status --porcelain",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Application.dataPath.Replace("/Assets", "") // Unity project root
		};

		try
		{
			using (Process statusProcess = Process.Start(statusPsi))
			{
				using (StreamReader statusOutputReader = statusProcess.StandardOutput)
				using (StreamReader statusErrorReader = statusProcess.StandardError)
				{
					string statusOutput = statusOutputReader.ReadToEnd();
					string statusError = statusErrorReader.ReadToEnd();

					statusProcess.WaitForExit();

					// Check for indication of incomplete sync in the status output or error
					if ((!string.IsNullOrEmpty(statusOutput) && statusOutput.Contains("Sync is incomplete")) ||
						(!string.IsNullOrEmpty(statusError) && statusError.Contains("Sync is incomplete")))
					{
						UnityEngine.Debug.LogWarning("Diversion Status Overlay: Sync is incomplete. Cannot reset file. Please wait.");
						return; // Exit if sync is incomplete
					}
				}
			}
		}
		catch (System.Exception ex)
		{
			UnityEngine.Debug.LogError("Failed to run Diversion CLI status check: " + ex.Message);
			return; // Exit if status check fails
		}
		// --- End sync check ---

		ProcessStartInfo psi = new ProcessStartInfo()
		{
			FileName = DiversionCLIPath,
			Arguments = $"reset \"{selectedPath}\"", // Change command to reset
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Application.dataPath.Replace("/Assets", "") // Unity project root
		};

		try
		{
			using (Process process = Process.Start(psi))
			{
				using (StreamReader outputReader = process.StandardOutput)
				using (StreamReader errorReader = process.StandardError)
				{
					string output = outputReader.ReadToEnd();
					string error = errorReader.ReadToEnd();

					process.WaitForExit();

					if (!string.IsNullOrEmpty(output))
					{
						UnityEngine.Debug.Log($"Diversion CLI Reset Output:\n{output}");
					}

					if (!string.IsNullOrEmpty(error))
					{
						UnityEngine.Debug.LogError($"Diversion CLI Reset Error:\n{error}");
					}
					else if (process.ExitCode != 0)
					{
						UnityEngine.Debug.LogError($"Diversion CLI Reset failed with exit code {process.ExitCode}.");
					}
					else
					{
						UnityEngine.Debug.Log($"Diversion CLI successfully reset {selectedPath}");
					}
				}
			}
		}
		catch (System.Exception ex)
		{
			UnityEngine.Debug.LogError("Failed to run Diversion CLI reset: " + ex.Message);
		}

		// Update status and repaint Project window after reset attempt
		UpdateStatus();
		EditorApplication.RepaintProjectWindow();
	}

	static void OnEditorUpdate()
	{
		if (refreshScheduled && EditorApplication.timeSinceStartup >= nextRefreshTime)
		{
			refreshScheduled = false;
			UpdateStatus(); // This now starts the process asynchronously
			EditorApplication.RepaintProjectWindow(); // Repaint to clear old icons while waiting
		}

		// Check if the background status process has completed
		if (statusProcessCompleted && statusProcess != null)
		{
			// Process the results on the main thread
			try
			{
				//UnityEngine.Debug.Log("Diversion Status Overlay: Background status process completed. Processing results.");

				if (!string.IsNullOrEmpty(statusProcessError))
				{
					UnityEngine.Debug.LogWarning($"Diversion CLI Error (Background):\n{statusProcessError}");
				}

				ParseDiversionOutput(statusProcessOutput);
				PropagateStatusToFolders();

				EditorApplication.RepaintProjectWindow(); // Repaint with updated icons

				UnityEngine.Debug.Log($"Diversion Status Overlay: Status update complete. Parsed {fileStatus.Count} file statuses.");
			}
			catch (System.Exception ex)
			{
				UnityEngine.Debug.LogError("Diversion Status Overlay: Error processing background status results: " + ex.Message);
			}
			finally
			{
				// Clean up the process
				statusProcess.Dispose();
				statusProcess = null;
				statusProcessOutput = "";
				statusProcessError = "";
				statusProcessCompleted = false;
			}
		}
	}

	public class DiversionAssetChangeWatcher : AssetPostprocessor
	{
		static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
		{
			if (importedAssets.Length > 0 || deletedAssets.Length > 0 || movedAssets.Length > 0 || movedFromAssetPaths.Length > 0)
			{
				ScheduleRefresh();
			}
		}
	}

	static void ScheduleRefresh()
	{
		float delay = EditorPrefs.GetFloat(DiversionRefreshDelayKey, defaultRefreshDelay);
		nextRefreshTime = EditorApplication.timeSinceStartup + delay;
		refreshScheduled = true; // Ensure refresh is scheduled
	}

	// Settings Provider for Diversion Overlay Icons
	[InitializeOnLoad]
	class DiversionOverlaySettingsProvider : SettingsProvider
	{
		const string DiversionRefreshDelayKey = "DiversionOverlay.RefreshDelay"; // Must match the key in DiversionStatusOverlay
		static float refreshDelay;

		const string ShowRootFolderIconKey = "DiversionOverlay.ShowRootFolderIcon"; // Must match the key in DiversionStatusOverlay
		static bool showRootFolderIcon;

		// Constructor
		public DiversionOverlaySettingsProvider(string path, SettingsScope scope = SettingsScope.Project)
			: base(path, scope)
		{
			// Load the setting when the provider is created
			refreshDelay = EditorPrefs.GetFloat(DiversionRefreshDelayKey, DiversionStatusOverlay.defaultRefreshDelay);
			showRootFolderIcon = EditorPrefs.GetBool(ShowRootFolderIconKey, DiversionStatusOverlay.defaultShowRootFolderIcon);
		}

		// Method to create the SettingsProvider instance
		[SettingsProvider]
		public static SettingsProvider CreateSettingsProvider()
		{
			// Provide a path and a scope. The path should be unique.
			var provider = new DiversionOverlaySettingsProvider("Project/Diversion Overlay Icons");

			// Optionally add keywords to the search index for the settings
			provider.keywords = new HashSet<string>(new[] { "Diversion", "Overlay", "Icons", "Refresh", "Delay", "Status" });

			return provider;
		}

		// Draw the settings UI
		public override void OnGUI(string searchContext)
		{
			// Display the setting using a FloatField
			refreshDelay = EditorGUILayout.FloatField("Refresh Delay (seconds)", refreshDelay);

			// Validate input to be non-negative
			if (refreshDelay < 0) refreshDelay = 0;

			EditorGUILayout.Space(); // Add some spacing

			// Display toggle for root folder icon
			showRootFolderIcon = EditorGUILayout.Toggle("Show Icon on Assets Folder", showRootFolderIcon);

			// Save the setting when the GUI changes
			if (GUI.changed)
			{
				EditorPrefs.SetFloat(DiversionRefreshDelayKey, refreshDelay);
				EditorPrefs.SetBool(ShowRootFolderIconKey, showRootFolderIcon);
				// Optionally, immediately schedule a refresh with the new delay
				ScheduleRefresh();
			}
		}
	}

	//[MenuItem("Assets/Diversion/Reset Asset icon")] // Commented out - Changed menu item text
	public static void RefreshSelectedAsset()
	{
		string selectedPath = AssetDatabase.GetAssetPath(Selection.activeObject);

		if (string.IsNullOrEmpty(selectedPath))
		{
			UnityEngine.Debug.LogWarning("Diversion Status Overlay: Please select an asset (file or folder) to refresh.");
			return;
		}

		UnityEngine.Debug.Log($"Diversion Status Overlay: Refreshing asset: {selectedPath}");

		// Use ForceUpdate to ensure it's re-imported even if Unity thinks it's up to date
		AssetDatabase.ImportAsset(selectedPath, ImportAssetOptions.ForceUpdate);

		// Optional: Trigger a status refresh after asset import is complete
		// ScheduleRefresh(); // This is already handled by OnPostprocessAllAssets normally
	}
}
