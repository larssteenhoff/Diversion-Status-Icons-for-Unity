using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;
using System.Linq;
#if UNITY_EDITOR
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class DiversionStatusOverlay
{
	internal static Dictionary<string, Texture2D> statusIcons = new();
	static Dictionary<string, string> fileStatus = new();
	static Dictionary<string, string> folderStatus = new();

	public const string DiversionRepoIdKey = "DiversionOverlay.RepoId";
	public const string DiversionWorkspaceIdKey = "DiversionOverlay.WorkspaceId";
	public const string DiversionAPIKey = "DiversionOverlay.APIKey";
	public const string DiversionRefreshTokenKey = "DiversionOverlay.RefreshToken";
	public const string DiversionAccessTokenKey = "DiversionOverlay.AccessToken";
	public const string DiversionCLIPathKey = "DiversionOverlay.CLIPath";
	public const string DiversionRefreshDelayKey = "DiversionOverlay.RefreshDelay";
	public const string DiversionMaxFilesKey = "DiversionOverlay.MaxFiles";
	public const string DiversionAccessTokenLastRefreshKey = "DiversionOverlay.AccessTokenLastRefresh";
	private const double AccessTokenRefreshIntervalSeconds = 59 * 60; // 59 minutes

	private static bool pendingRefresh = false;
	private static double lastAssetChangeTime = 0;
	private static float refreshDelay = 1.0f; // default 1 second
	private static int maxFilesSetting;

	static DiversionStatusOverlay()
	{
		LoadIcons();
		EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
		EditorApplication.update += OnEditorUpdate;
		UpdateStatusAsync();
		refreshDelay = EditorPrefs.GetFloat(DiversionRefreshDelayKey, 1.0f);
		maxFilesSetting = EditorPrefs.GetInt(DiversionMaxFilesKey, 1000);
		// On startup, if token is old, refresh it
		CheckAndRefreshAccessTokenIfNeeded();
	}

	static void LoadIcons()
	{
		statusIcons["A"] = EditorGUIUtility.FindTexture("PackageBadgeNew");
		statusIcons["M"] = EditorGUIUtility.FindTexture("d_CollabEdit Icon");
		statusIcons["D"] = EditorGUIUtility.FindTexture("CollabDeleted Icon");
		statusIcons["C"] = EditorGUIUtility.FindTexture("d_CollabConflict Icon");
		statusIcons["U"] = EditorGUIUtility.FindTexture("Collab");
		statusIcons["moved"] = EditorGUIUtility.FindTexture("CollabMoved Icon");
		statusIcons["added"] = statusIcons["A"];
		statusIcons["deleted"] = statusIcons["D"];
		statusIcons["modified"] = statusIcons["M"];
		statusIcons["conflicted"] = statusIcons["C"];
		// No icon for 'uptodate' status
	}

	public static async Task ExchangeRefreshTokenForAccessToken(string refreshToken)
	{
		if (string.IsNullOrEmpty(refreshToken))
			return;
		string url = "https://auth.diversion.dev/oauth2/token";
		WWWForm form = new WWWForm();
		form.AddField("grant_type", "refresh_token");
		form.AddField("refresh_token", refreshToken);
		form.AddField("client_id", "j084768v4hd6j1pf8df4h4c47");
		using (UnityWebRequest www = UnityWebRequest.Post(url, form))
		{
			www.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
			await www.SendWebRequest();
			if (www.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError($"Diversion Overlay: Failed to exchange refresh token: {www.error}\nResponse: {www.downloadHandler.text}");
				return;
			}
			try
			{
				var json = JObject.Parse(www.downloadHandler.text);
				string accessToken = json["access_token"]?.Value<string>();
				if (!string.IsNullOrEmpty(accessToken) && accessToken.Count(c => c == '.') == 2)
				{
					EditorPrefs.SetString(DiversionAccessTokenKey, accessToken);
					Debug.Log("Diversion Overlay: Access token updated.");
					EditorPrefs.SetFloat(DiversionAccessTokenLastRefreshKey, (float)EditorApplication.timeSinceStartup);
				}
				else
				{
					Debug.LogError($"Diversion Overlay: No valid access_token in response. Raw: {json["access_token"]}");
				}
			}
			catch (System.Exception ex)
			{
				Debug.LogError("Diversion Overlay: Error parsing access token: " + ex.Message);
			}
		}
	}

	static async void UpdateStatusAsync()
	{
		string accessToken = EditorPrefs.GetString(DiversionAccessTokenKey, "");
		string repoId = EditorPrefs.GetString(DiversionRepoIdKey, "");
		string workspaceId = EditorPrefs.GetString(DiversionWorkspaceIdKey, "");

		// Ensure Diversion prefixes are present
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;

		if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(workspaceId))
		{
			Debug.LogWarning("Diversion Overlay: Missing access token, repo ID, or workspace ID.");
			return;
		}

		int limit = EditorPrefs.GetInt(DiversionMaxFilesKey, 1000);
		int skip = 0;
		bool more = true;
		JObject combinedItems = new JObject();
		string[] categories = new[] { "new", "modified", "deleted", "conflicted", "moved" };
		foreach (var cat in categories) combinedItems[cat] = new JArray();

		while (more)
		{
			string apiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/status?detail_items=true&recurse=true&limit={limit}&skip={skip}";
			Debug.Log($"Diversion Overlay: Fetching status from API (skip={skip})...");
			using (UnityWebRequest webRequest = UnityWebRequest.Get(apiUrl))
			{
				webRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
				await webRequest.SendWebRequest();

				if (webRequest.result != UnityWebRequest.Result.Success)
				{
					Debug.LogError("Diversion Overlay: API Request Failed: " + webRequest.error);
					return;
				}

				JObject json = JObject.Parse(webRequest.downloadHandler.text);
				var items = json["items"] as JObject;
				if (items != null)
				{
					int itemsAddedThisPage = 0;
					foreach (var cat in categories)
					{
						var arr = items[cat] as JArray;
						if (arr != null && arr.Count > 0)
						{
							((JArray)combinedItems[cat]).Merge(arr);
							itemsAddedThisPage += arr.Count;
						}
					}
					if (itemsAddedThisPage < limit)
					{
						more = false;
					}
					else
					{
						skip += limit;
					}
				}
				else
				{
					more = false;
				}
			}
		}

		// Build a fake status JSON to pass to ParseDiversionAPIOutput
		JObject fakeStatusJson = new JObject { ["items"] = combinedItems };
		ParseDiversionAPIOutput(fakeStatusJson.ToString());
		EditorApplication.RepaintProjectWindow();
	}

	static void ParseDiversionAPIOutput(string jsonOutput)
	{
		fileStatus.Clear();
		folderStatus.Clear();
		JObject json = JObject.Parse(jsonOutput);
		// New
		foreach (var item in json["items"]?["new"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			string path = item["path"]?.Value<string>();
			if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
				fileStatus[path] = "added";
		}
		// Modified
		foreach (var item in json["items"]?["modified"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			string path = item["path"]?.Value<string>();
			string prevPath = item["prev_path"]?.Value<string>();
			string status = string.IsNullOrEmpty(prevPath) ? "modified" : "moved";
			if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
				fileStatus[path] = status;
		}
		// Deleted
		foreach (var item in json["items"]?["deleted"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			string path = item["path"]?.Value<string>();
			if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
				fileStatus[path] = "deleted";
		}
		Debug.Log($"Diversion Overlay: Parsed {fileStatus.Count} file statuses from API.");
		PropagateStatusToFolders();
	}

	static void PropagateStatusToFolders()
	{
		foreach (var kvp in fileStatus)
		{
			string path = kvp.Key;
			string status = kvp.Value;
			if (status != "uptodate" && status != "U")
			{
				string folder = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
				while (!string.IsNullOrEmpty(folder) && folder.StartsWith("Assets"))
				{
					folderStatus[folder] = "changed";
					folder = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
				}
			}
		}
	}

	static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
	{
		string path = AssetDatabase.GUIDToAssetPath(guid);
		Texture2D iconToDraw = null;
		string fileStat = null;
		if (path.EndsWith(".meta"))
		{
			string assetPath = path.Substring(0, path.Length - ".meta".Length);
			if (fileStatus.TryGetValue(assetPath, out string assetStat))
				fileStat = assetStat;
		}
		if (fileStat == null)
			fileStatus.TryGetValue(path, out fileStat);
		if (AssetDatabase.IsValidFolder(path))
		{
			if (folderStatus.ContainsKey(path) && path != "Assets")
				iconToDraw = EditorGUIUtility.FindTexture("d_CollabChanges Icon");
		}
		else
		{
			if (fileStat != null && statusIcons.TryGetValue(fileStat, out Texture2D icon))
				iconToDraw = icon;
		}
		if (iconToDraw != null)
		{
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

	[MenuItem("Tools/Diversion/Refresh Status")]
	public static void ManualRefreshStatus()
	{
		UpdateStatusAsync();
		EditorApplication.RepaintProjectWindow();
	}

	public static void FetchRepoAndWorkspaceIds()
	{
		string diversionCLIPath = EditorPrefs.GetString(DiversionCLIPathKey, "");
		if (string.IsNullOrEmpty(diversionCLIPath) || !System.IO.File.Exists(diversionCLIPath))
		{
			diversionCLIPath = AutoDetectDiversionCLIPath();
			if (!string.IsNullOrEmpty(diversionCLIPath))
				EditorPrefs.SetString(DiversionCLIPathKey, diversionCLIPath);
		}
		if (string.IsNullOrEmpty(diversionCLIPath) || !System.IO.File.Exists(diversionCLIPath))
		{
			Debug.LogError("Diversion Overlay: Could not find Diversion CLI (dv). Please set the path in Project Settings.");
			return;
		}
		var psi = new System.Diagnostics.ProcessStartInfo
		{
			FileName = diversionCLIPath,
			Arguments = "status --porcelain",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		try
		{
			using (var process = System.Diagnostics.Process.Start(psi))
			{
				string output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				string repoId = null;
				string workspaceId = null;
				using (var reader = new System.IO.StringReader(output))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						if (line.Contains("dv.repo."))
						{
							int idx = line.IndexOf("dv.repo.");
							if (idx != -1)
								repoId = line.Substring(idx + "dv.repo.".Length).Trim();
						}
						if (line.Contains("dv.ws."))
						{
							int idx = line.IndexOf("dv.ws.");
							if (idx != -1)
							{
								workspaceId = line.Substring(idx + "dv.ws.".Length);
								int end = workspaceId.IndexOf(')');
								if (end != -1)
									workspaceId = workspaceId.Substring(0, end);
								workspaceId = workspaceId.Trim();
							}
						}
					}
				}
				if (!string.IsNullOrEmpty(repoId) && !string.IsNullOrEmpty(workspaceId))
				{
					EditorPrefs.SetString(DiversionRepoIdKey, repoId);
					EditorPrefs.SetString(DiversionWorkspaceIdKey, workspaceId);
					Debug.Log($"Diversion Overlay: Auto-fetched Repo ID: {repoId}, Workspace ID: {workspaceId}");
				}
				else
				{
					Debug.LogWarning("Diversion Overlay: Could not auto-detect Repo ID or Workspace ID.");
				}
			}
		}
		catch (System.Exception ex)
		{
			Debug.LogError("Diversion Overlay: Error fetching repo/workspace IDs: " + ex.Message);
		}
	}

	public static string AutoDetectDiversionCLIPath()
	{
		string userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
		string[] possiblePaths;
		if (Application.platform == RuntimePlatform.WindowsEditor)
		{
			possiblePaths = new[] {
				userProfile + "\\.diversion\\bin\\dv.exe",
				"C:\\Program Files\\Diversion\\dv.exe"
			};
			foreach (var path in possiblePaths)
			{
				if (System.IO.File.Exists(path))
					return path;
			}
			// Try PATH (Windows)
			try
			{
				var psi = new System.Diagnostics.ProcessStartInfo
				{
					FileName = "where",
					Arguments = "dv",
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				using (var process = System.Diagnostics.Process.Start(psi))
				{
					string output = process.StandardOutput.ReadToEnd();
					process.WaitForExit();
					if (!string.IsNullOrEmpty(output))
					{
						string cliPath = output.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)[0];
						if (System.IO.File.Exists(cliPath))
							return cliPath;
					}
				}
			}
			catch { }
		}
		else // macOS/Linux
		{
			possiblePaths = new[] {
				userProfile + "/.diversion/bin/dv",
				"/usr/local/bin/dv",
				"/opt/homebrew/bin/dv",
				"/usr/bin/dv"
			};
			foreach (var path in possiblePaths)
			{
				if (System.IO.File.Exists(path))
					return path;
			}
			// Try PATH (Unix)
			try
			{
				var psi = new System.Diagnostics.ProcessStartInfo
				{
					FileName = "which",
					Arguments = "dv",
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				using (var process = System.Diagnostics.Process.Start(psi))
				{
					string output = process.StandardOutput.ReadToEnd();
					process.WaitForExit();
					if (!string.IsNullOrEmpty(output))
					{
						string cliPath = output.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)[0];
						if (System.IO.File.Exists(cliPath))
							return cliPath;
					}
				}
			}
			catch { }
		}
		return null;
	}

	[MenuItem("Assets/Diversion/Reset", true, 2000)]
	private static bool ValidateResetSelectedMulti()
	{
		var selected = Selection.objects;
		if (selected == null || selected.Length == 0) return false;
		foreach (var obj in selected)
		{
			string path = AssetDatabase.GetAssetPath(obj);
			if (!string.IsNullOrEmpty(path)) return true;
		}
		return false;
	}

	[MenuItem("Assets/Diversion/Reset", false, 2000)]
	public static async void ResetSelectedMulti()
	{
		var selected = Selection.objects;
		if (selected == null || selected.Length == 0) return;

		string accessToken = EditorPrefs.GetString(DiversionAccessTokenKey, "");
		string repoId = EditorPrefs.GetString(DiversionRepoIdKey, "");
		string workspaceId = EditorPrefs.GetString(DiversionWorkspaceIdKey, "");
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;
		if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(workspaceId))
		{
			EditorUtility.DisplayDialog("Diversion Reset", "Missing access token, repo ID, or workspace ID.", "OK");
			return;
		}

		// Gather all selected files and folders (no need to enumerate inside folders)
		HashSet<string> selectedPaths = new HashSet<string>();
		foreach (var obj in selected)
		{
			string path = AssetDatabase.GetAssetPath(obj);
			if (!string.IsNullOrEmpty(path))
				selectedPaths.Add(path);
		}
		if (selectedPaths.Count == 0)
		{
			EditorUtility.DisplayDialog("Diversion Reset", "No valid files or folders selected.", "OK");
			return;
		}

		// Query Diversion status to check for 'new' files in the selection (for delete prompt)
		string statusApiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/status?detail_items=true&recurse=true&limit=1000";
		List<string> newFiles = new List<string>();
		using (UnityWebRequest statusRequest = UnityWebRequest.Get(statusApiUrl))
		{
			statusRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			await statusRequest.SendWebRequest();
			if (statusRequest.result == UnityWebRequest.Result.Success)
			{
				var json = JObject.Parse(statusRequest.downloadHandler.text);
				var items = json["items"] as JObject;
				if (items != null)
				{
					var arr = items["new"] as JArray;
					if (arr != null)
					{
						foreach (var entry in arr.OfType<JObject>())
						{
							string path = entry["path"]?.ToString();
							if (string.IsNullOrEmpty(path)) continue;
							// If the new file is directly selected or under a selected folder
							bool isUnderSelected = false;
							foreach (var selPath in selectedPaths)
							{
								if (path == selPath || (AssetDatabase.IsValidFolder(selPath) && path.StartsWith(selPath + "/")))
								{
									isUnderSelected = true;
									break;
								}
							}
							if (isUnderSelected)
								newFiles.Add(path);
						}
					}
				}
			}
		}

		// If all changes are selected, offer to use theall for efficiency
		bool canUseTheAll = false;
		{
			// If the user selected the root Assets folder, treat as 'reset all'
			if (selectedPaths.Contains("Assets"))
				canUseTheAll = true;
		}

		// Confirmation dialog
		bool deleteNewFiles = false;
		string message = $"You are about to discard changes on the selected files and folders.";
		if (newFiles.Count > 0)
			message += $"\n\n{newFiles.Count} new file(s) will be deleted locally if you choose to delete new files.";
		if (canUseTheAll)
			message += "\n\nYou have selected the root folder. All changes will be reset.";
		message += "\n\nDo you want to proceed?";

		if (newFiles.Count > 0)
		{
			deleteNewFiles = EditorUtility.DisplayDialogComplex(
				"Discard",
				message,
				"Proceed and Delete New Files",
				"Cancel",
				"Proceed Without Deleting New Files"
			) == 0;
			if (!deleteNewFiles && EditorUtility.DisplayDialogComplex(
				"Discard",
				message,
				"Proceed Without Deleting New Files",
				"Cancel",
				"Proceed and Delete New Files"
			) != 0)
			{
				// User cancelled
				return;
			}
		}
		else
		{
			if (!EditorUtility.DisplayDialog("Discard", message, "Proceed", "Cancel"))
				return;
		}

		// Prepare API call
		string apiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/reset";
		JObject payload;
		if (canUseTheAll)
		{
			payload = new JObject { ["theall"] = true };
		}
		else
		{
			payload = new JObject { ["paths"] = new JArray(selectedPaths) };
		}
		if (deleteNewFiles)
		{
			payload["delete_added"] = true;
		}
		using (UnityWebRequest webRequest = new UnityWebRequest(apiUrl, "POST"))
		{
			string json = payload.ToString();
			byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
			webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
			webRequest.downloadHandler = new DownloadHandlerBuffer();
			webRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			webRequest.SetRequestHeader("Content-Type", "application/json");
			await webRequest.SendWebRequest();
			if (webRequest.result == UnityWebRequest.Result.Success)
			{
				EditorUtility.DisplayDialog("Diversion Reset", $"Successfully reset changes in the selection.", "OK");
				AssetDatabase.Refresh();
				UpdateStatusAsync();
			}
			else
			{
				EditorUtility.DisplayDialog("Diversion Reset Failed", $"Error: {webRequest.error}\n{webRequest.downloadHandler.text}", "OK");
			}
		}
	}

	static void OnEditorUpdate()
	{
		refreshDelay = EditorPrefs.GetFloat(DiversionRefreshDelayKey, 1.0f);
		if (pendingRefresh && (EditorApplication.timeSinceStartup - lastAssetChangeTime > refreshDelay))
		{
			pendingRefresh = false;
			UpdateStatusAsync();
		}
		// Check if access token needs to be refreshed
		CheckAndRefreshAccessTokenIfNeeded();
	}

	static void CheckAndRefreshAccessTokenIfNeeded()
	{
		double lastRefresh = EditorPrefs.GetFloat(DiversionAccessTokenLastRefreshKey, 0f);
		double now = EditorApplication.timeSinceStartup;
		if (now - lastRefresh > AccessTokenRefreshIntervalSeconds)
		{
			string refreshToken = EditorPrefs.GetString(DiversionRefreshTokenKey, "");
			if (!string.IsNullOrEmpty(refreshToken))
			{
				_ = ExchangeRefreshTokenForAccessToken(refreshToken);
				EditorPrefs.SetFloat(DiversionAccessTokenLastRefreshKey, (float)now);
			}
		}
	}

	// AssetPostprocessor to trigger status update on asset changes
	class DiversionAssetPostprocessor : AssetPostprocessor
	{
		static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
		{
			pendingRefresh = true;
			lastAssetChangeTime = EditorApplication.timeSinceStartup;
		}
	}
}

public class DiversionOverlaySettingsProvider : SettingsProvider
{
	private string refreshToken;
	private string accessToken;
	private string apiKey; // deprecated, but kept for backward compatibility
	private string repoId;
	private string workspaceId;
	private bool accessTokenDirty = false;
	private string cliPath;
	private float refreshIntervalSetting;
	private int maxFilesSetting;

	public DiversionOverlaySettingsProvider(string path, SettingsScope scope = SettingsScope.Project)
		: base(path, scope) { }

	[SettingsProvider]
	public static SettingsProvider CreateSettingsProvider()
	{
		return new DiversionOverlaySettingsProvider("Project/Diversion", SettingsScope.Project);
	}

	public override void OnActivate(string searchContext, VisualElement rootElement)
	{
		ReloadFields();
	}

	private void ReloadFields()
	{
		refreshToken = EditorPrefs.GetString(DiversionStatusOverlay.DiversionRefreshTokenKey, "");
		accessToken = EditorPrefs.GetString(DiversionStatusOverlay.DiversionAccessTokenKey, "");
		apiKey = EditorPrefs.GetString(DiversionStatusOverlay.DiversionAPIKey, "");
		repoId = EditorPrefs.GetString(DiversionStatusOverlay.DiversionRepoIdKey, "");
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		workspaceId = EditorPrefs.GetString(DiversionStatusOverlay.DiversionWorkspaceIdKey, "");
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;
		cliPath = EditorPrefs.GetString(DiversionStatusOverlay.DiversionCLIPathKey, "");
		if (string.IsNullOrEmpty(cliPath) || !System.IO.File.Exists(cliPath))
			cliPath = DiversionStatusOverlay.AutoDetectDiversionCLIPath() ?? "";
		if (!string.IsNullOrEmpty(cliPath))
			EditorPrefs.SetString(DiversionStatusOverlay.DiversionCLIPathKey, cliPath);
		maxFilesSetting = EditorPrefs.GetInt(DiversionStatusOverlay.DiversionMaxFilesKey, 1000);
	}

	public override void OnGUI(string searchContext)
	{
		EditorGUILayout.Space();
		EditorGUILayout.Space();
		GUILayout.BeginHorizontal();
		GUILayout.Space(20); // Left margin
		GUILayout.BeginVertical();

		EditorGUILayout.LabelField("Diversion Settings", EditorStyles.boldLabel);
		EditorGUILayout.Space();

		// CLI Path at the top
		EditorGUILayout.LabelField("Diversion CLI Path");
		EditorGUILayout.SelectableLabel(cliPath, EditorStyles.textField, GUILayout.Height(18));
		if (GUILayout.Button("Auto-detect Diversion CLI Path"))
		{
			cliPath = DiversionStatusOverlay.AutoDetectDiversionCLIPath() ?? "";
			if (!string.IsNullOrEmpty(cliPath))
				EditorPrefs.SetString(DiversionStatusOverlay.DiversionCLIPathKey, cliPath);
			ReloadFields();
		}
		EditorGUILayout.Space();

		EditorGUI.BeginChangeCheck();
		refreshToken = EditorGUILayout.TextField("Refresh Token", refreshToken);
		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Repo ID");
		EditorGUILayout.SelectableLabel(repoId, EditorStyles.textField, GUILayout.Height(18));
		EditorGUILayout.LabelField("Workspace ID");
		EditorGUILayout.SelectableLabel(workspaceId, EditorStyles.textField, GUILayout.Height(18));

		EditorGUILayout.Space();
		if (GUILayout.Button("Auto-detect IDs from CLI"))
		{
			DiversionStatusOverlay.FetchRepoAndWorkspaceIds();
			ReloadFields();
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Access Token (read-only):");
		EditorGUILayout.SelectableLabel(EditorPrefs.GetString(DiversionStatusOverlay.DiversionAccessTokenKey, ""), EditorStyles.textField, GUILayout.Height(40));

		EditorGUILayout.Space();
		if (GUILayout.Button("Refresh Access Token"))
		{
			if (!string.IsNullOrEmpty(refreshToken))
			{
				_ = DiversionStatusOverlay.ExchangeRefreshTokenForAccessToken(refreshToken);
			}
			else
			{
				Debug.LogWarning("Diversion Overlay: Please enter a refresh token first.");
			}
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Status Refresh Delay After Change (seconds)");
		EditorGUI.BeginChangeCheck();
		float delaySetting = EditorPrefs.GetFloat(DiversionStatusOverlay.DiversionRefreshDelayKey, 1.0f);
		delaySetting = EditorGUILayout.Slider(delaySetting, 0.1f, 10.0f);
		if (EditorGUI.EndChangeCheck())
		{
			EditorPrefs.SetFloat(DiversionStatusOverlay.DiversionRefreshDelayKey, delaySetting);
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Max Files to Check per API Call");
		EditorGUI.BeginChangeCheck();
		maxFilesSetting = EditorGUILayout.IntSlider(maxFilesSetting, 100, 5000);
		if (EditorGUI.EndChangeCheck())
		{
			EditorPrefs.SetInt(DiversionStatusOverlay.DiversionMaxFilesKey, maxFilesSetting);
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Status Icon Legend", EditorStyles.boldLabel);
		var legend = new (string label, string key)[] {
			("Added", "added"),
			("Modified", "modified"),
			("Deleted", "deleted"),
			("Conflicted", "conflicted"),
			("Moved", "moved")
			// No icon for 'Up to date'
		};
		foreach (var (label, key) in legend)
		{
			Texture2D icon = null;
			DiversionStatusOverlay.statusIcons.TryGetValue(key, out icon);
			EditorGUILayout.BeginHorizontal();
			GUILayout.Space(10);
			if (icon != null)
			{
				float maxHeight = 20f;
				float maxWidth = 32f;
				float width = icon.width;
				float height = icon.height;
				float aspect = width / height;
				float drawHeight = Mathf.Min(maxHeight, height);
				float drawWidth = Mathf.Min(maxWidth, drawHeight * aspect, width);
				GUILayout.Label(icon, GUILayout.Width(drawWidth), GUILayout.Height(drawHeight));
			}
			else
			{
				GUILayout.Label("");
			}
			EditorGUILayout.LabelField(label, GUILayout.Width(100));
			EditorGUILayout.EndHorizontal();
		}

		GUILayout.EndVertical();
		GUILayout.Space(20); // Right margin
		GUILayout.EndHorizontal();
	}
}
#endif
