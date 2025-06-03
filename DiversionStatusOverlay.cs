using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;
using System.Text.Json;
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
	public const string DiversionDebugLogsKey = "DiversionOverlay.DebugLogs";
	public const string DiversionMeldPathKey = "DiversionOverlay.MeldPath";
	public const string DiversionBranchRefKey = "DiversionOverlay.BranchRef";
	public const string DiversionAutoRefreshEnabledKey = "DiversionOverlay.AutoRefreshEnabled";
	public const string DiversionAutoRefreshIntervalKey = "DiversionOverlay.AutoRefreshInterval";
	public const string DiversionDiffToolNameKey = "DiversionOverlay.DiffToolName";
	public const string DiversionDiffToolPathKey = "DiversionOverlay.DiffToolPath";
	private const double AccessTokenRefreshIntervalSeconds = 59 * 60; // 59 minutes

	private static bool pendingRefresh = false;
	private static double lastAssetChangeTime = 0;
	private static float refreshDelay = 1.0f; // default 1 second
	private static int maxFilesSetting;
	private static double lastAutoRefreshTime = 0;

	// Helper to get a project-specific key for EditorPrefs
	public static string ProjectKeyPrefix => Application.dataPath.GetHashCode().ToString();
	public static string ProjectScopedKey(string baseKey) => $"DiversionOverlay.{ProjectKeyPrefix}.{baseKey}";

	// Supported diff tools and their info
	public class DiffToolInfo {
		public string Name;
		public string[] PossiblePaths;
		public string ArgumentsFormat; // e.g. "\"{0}\" \"{1}\""
		public bool IsAvailable;
		public DiffToolInfo(string name, string[] paths, string args) {
			Name = name; PossiblePaths = paths; ArgumentsFormat = args; IsAvailable = false;
		}
	}

	public static List<DiversionStatusOverlay.DiffToolInfo> GetPlatformDiffTools() {
		#if UNITY_EDITOR_WIN
			return new List<DiversionStatusOverlay.DiffToolInfo> {
				new DiversionStatusOverlay.DiffToolInfo("Meld", new[]{
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) + @"\Meld\Meld.exe",
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86) + @"\Meld\Meld.exe"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("Beyond Compare", new[]{
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) + @"\Beyond Compare 4\BCompare.exe",
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86) + @"\Beyond Compare 4\BCompare.exe"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("WinMerge", new[]{
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) + @"\WinMerge\WinMergeU.exe",
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86) + @"\WinMerge\WinMergeU.exe"
				}, "/e /u \"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("KDiff3", new[]{
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) + @"\KDiff3\kdiff3.exe",
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86) + @"\KDiff3\kdiff3.exe"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("Araxis Merge", new[]{
					System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) + @"\Araxis\Araxis Merge\Merge.exe"
				}, "\"{0}\" \"{1}\""),
			};
		#elif UNITY_EDITOR_OSX
			return new List<DiversionStatusOverlay.DiffToolInfo> {
				new DiversionStatusOverlay.DiffToolInfo("Meld", new[]{
					"/Applications/Meld.app/Contents/MacOS/Meld",
					"/usr/local/bin/meld"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("Beyond Compare", new[]{
					"/Applications/Beyond Compare.app/Contents/MacOS/bcomp"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("Kaleidoscope", new[]{
					"/Applications/Kaleidoscope.app/Contents/Resources/bin/ksdiff"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("FileMerge (opendiff)", new[]{
					"/usr/bin/opendiff"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("DiffMerge", new[]{
					"/Applications/DiffMerge.app/Contents/MacOS/DiffMerge"
				}, "\"{0}\" \"{1}\""),
			};
		#else // Linux
			return new List<DiversionStatusOverlay.DiffToolInfo> {
				new DiversionStatusOverlay.DiffToolInfo("Meld", new[]{
					"/usr/bin/meld",
					"/usr/local/bin/meld"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("KDiff3", new[]{
					"/usr/bin/kdiff3",
					"/usr/local/bin/kdiff3"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("Kompare", new[]{
					"/usr/bin/kompare"
				}, "\"{0}\" \"{1}\""),
				new DiversionStatusOverlay.DiffToolInfo("DiffMerge", new[]{
					"/usr/bin/diffmerge",
					"/usr/local/bin/diffmerge"
				}, "\"{0}\" \"{1}\""),
			};
		#endif
	}

	public static void AutoDetectDiffTools(List<DiversionStatusOverlay.DiffToolInfo> tools) {
		foreach (var tool in tools) {
			foreach (var path in tool.PossiblePaths) {
				if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) {
					tool.IsAvailable = true;
					break;
				}
			}
		}
	}

	public static DiversionStatusOverlay.DiffToolInfo GetSelectedDiffTool(List<DiversionStatusOverlay.DiffToolInfo> tools, string selectedName) {
		foreach (var tool in tools) {
			if (tool.Name == selectedName) return tool;
		}
		return null;
	}

	static DiversionStatusOverlay()
	{
		LoadIcons();
		EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
		EditorApplication.update += OnEditorUpdate;
		UpdateStatusAsync();
		refreshDelay = EditorPrefs.GetFloat(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshDelayKey), 1.0f);
		maxFilesSetting = EditorPrefs.GetInt(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionMaxFilesKey), 1000);
		// On startup, refresh access token immediately if refresh token is present
		string refreshToken = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshTokenKey), "");
		if (!string.IsNullOrEmpty(refreshToken))
		{
			_ = ExchangeRefreshTokenForAccessToken(refreshToken);
		}
		// Also check if token is old and needs refresh
		CheckAndRefreshAccessTokenIfNeeded();
		// Refresh status when Unity regains focus
		EditorApplication.focusChanged += OnEditorFocusChanged;
		lastAutoRefreshTime = EditorApplication.timeSinceStartup;
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
				using var jsonDoc = JsonDocument.Parse(www.downloadHandler.text);
				var root = jsonDoc.RootElement;
				string accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
				if (!string.IsNullOrEmpty(accessToken) && accessToken.Count(c => c == '.') == 2)
				{
					EditorPrefs.SetString(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), accessToken);
					if (DebugLogsEnabled) Debug.Log("Diversion Overlay: Access token updated.");
					EditorPrefs.SetFloat(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenLastRefreshKey), (float)EditorApplication.timeSinceStartup);
				}
				else
				{
					Debug.LogError($"Diversion Overlay: No valid access_token in response. Raw: {root.GetRawText()}");
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
		string accessToken = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), "");
		string repoId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), "");
		string workspaceId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionWorkspaceIdKey), "");

		// Ensure Diversion prefixes are present
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;

		if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(workspaceId))
		{
			if (DiversionStatusOverlay.DebugLogsEnabled) Debug.LogWarning("Diversion Overlay: Missing access token, repo ID, or workspace ID.");
			return;
		}

		int limit = EditorPrefs.GetInt(ProjectScopedKey(DiversionStatusOverlay.DiversionMaxFilesKey), 1000);
		int skip = 0;
		bool more = true;
		var combinedItems = new Dictionary<string, List<Dictionary<string, object>>>();
		string[] categories = new[] { "new", "modified", "deleted", "conflicted", "moved" };
		foreach (var cat in categories) combinedItems[cat] = new List<Dictionary<string, object>>();

		while (more)
		{
			string apiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/status?detail_items=true&recurse=true&limit={limit}&skip={skip}";
			if (DiversionStatusOverlay.DebugLogsEnabled) Debug.Log($"Diversion Overlay: Fetching status from API (skip={skip})...");
			using (UnityWebRequest webRequest = UnityWebRequest.Get(apiUrl))
			{
				webRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
				await webRequest.SendWebRequest();

				if (webRequest.result != UnityWebRequest.Result.Success)
				{
					Debug.LogError("Diversion Overlay: API Request Failed: " + webRequest.error);
					return;
				}

				using var jsonDoc = JsonDocument.Parse(webRequest.downloadHandler.text);
				var root = jsonDoc.RootElement;
				if (root.TryGetProperty("items", out var items))
				{
					int itemsAddedThisPage = 0;
					foreach (var cat in categories)
					{
						if (items.TryGetProperty(cat, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
						{
							foreach (var item in arr.EnumerateArray())
							{
								var dict = new Dictionary<string, object>();
								if (item.TryGetProperty("path", out var pathProp)) dict["path"] = pathProp.GetString();
								if (item.TryGetProperty("prev_path", out var prevPathProp)) dict["prev_path"] = prevPathProp.GetString();
								// Add more properties as needed
								combinedItems[cat].Add(dict);
								itemsAddedThisPage++;
							}
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
		var fakeStatus = new Dictionary<string, object> { ["items"] = combinedItems };
		ParseDiversionAPIOutput(JsonSerializer.Serialize(fakeStatus));
		EditorApplication.RepaintProjectWindow();
	}

	static void ParseDiversionAPIOutput(string jsonOutput)
	{
		fileStatus.Clear();
		folderStatus.Clear();
		using var jsonDoc = JsonDocument.Parse(jsonOutput);
		var root = jsonDoc.RootElement;
		var items = root.GetProperty("items");
		// New
		if (items.TryGetProperty("new", out var newArr) && newArr.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in newArr.EnumerateArray())
			{
				string path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
				if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
				{
					fileStatus[path] = "added";
					if (path.EndsWith(".meta"))
					{
						string assetPath = path.Substring(0, path.Length - 5);
						if (!fileStatus.ContainsKey(assetPath))
							fileStatus[assetPath] = "added";
					}
				}
			}
		}
		// Modified
		if (items.TryGetProperty("modified", out var modArr) && modArr.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in modArr.EnumerateArray())
			{
				string path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
				string prevPath = item.TryGetProperty("prev_path", out var prev) ? prev.GetString() : null;
				string status = (!string.IsNullOrEmpty(prevPath) && prevPath != path) ? "moved" : "modified";
				if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
				{
					fileStatus[path] = status;
					if (path.EndsWith(".meta"))
					{
						string assetPath = path.Substring(0, path.Length - 5);
						if (!fileStatus.ContainsKey(assetPath))
							fileStatus[assetPath] = status;
					}
				}
			}
		}
		// Deleted
		if (items.TryGetProperty("deleted", out var delArr) && delArr.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in delArr.EnumerateArray())
			{
				string path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
				if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
				{
					fileStatus[path] = "deleted";
					if (path.EndsWith(".meta"))
					{
						string assetPath = path.Substring(0, path.Length - 5);
						if (!fileStatus.ContainsKey(assetPath))
							fileStatus[assetPath] = "deleted";
					}
				}
			}
		}
		if (DiversionStatusOverlay.DebugLogsEnabled) Debug.Log($"Diversion Overlay: Parsed {fileStatus.Count} file statuses from API.");
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
		Debug.Log("Diversion Overlay: Manual status refresh triggered.");
		UpdateStatusAsync();
		EditorApplication.RepaintProjectWindow();
	}

	public static void FetchRepoAndWorkspaceIds()
	{
		string diversionCLIPath = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionCLIPathKey), "");
		if (string.IsNullOrEmpty(diversionCLIPath) || !System.IO.File.Exists(diversionCLIPath))
		{
			diversionCLIPath = AutoDetectDiversionCLIPath();
			if (!string.IsNullOrEmpty(diversionCLIPath))
				EditorPrefs.SetString(ProjectScopedKey(DiversionStatusOverlay.DiversionCLIPathKey), diversionCLIPath);
		}
		if (string.IsNullOrEmpty(diversionCLIPath) || !System.IO.File.Exists(diversionCLIPath))
		{
			if (DiversionStatusOverlay.DebugLogsEnabled) Debug.LogWarning("Diversion Overlay: Could not find Diversion CLI (dv). Please set the path in Project Settings.");
			return;
		}
		System.Threading.Tasks.Task.Run(() => {
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
					if (!process.WaitForExit(2000)) // 2 second timeout
					{
						process.Kill();
						EditorApplication.delayCall += () => {
							EditorUtility.DisplayDialog("Diversion CLI Timeout", "Auto-detecting IDs from CLI timed out. Please check your Diversion CLI installation and try again.\n\nYou can also enter the Repo ID and Workspace ID manually in the Diversion settings.", "OK");
						};
						return;
					}
					string output = process.StandardOutput.ReadToEnd();
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
						EditorApplication.delayCall += () => {
							EditorPrefs.SetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), repoId);
							EditorPrefs.SetString(ProjectScopedKey(DiversionStatusOverlay.DiversionWorkspaceIdKey), workspaceId);
							if (DiversionStatusOverlay.DebugLogsEnabled) Debug.Log($"Diversion Overlay: Auto-fetched Repo ID: {repoId}, Workspace ID: {workspaceId}");
						};
					}
					else
					{
						EditorApplication.delayCall += () => {
							if (DiversionStatusOverlay.DebugLogsEnabled) Debug.LogWarning("Diversion Overlay: Could not auto-detect Repo ID or Workspace ID.");
						};
					}
				}
			}
			catch (System.Exception ex)
			{
				EditorApplication.delayCall += () => {
					Debug.LogError("Diversion Overlay: Error fetching repo/workspace IDs: " + ex.Message);
				};
			}
		});
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

		string accessToken = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), "");
		if (string.IsNullOrEmpty(accessToken))
		{
			EditorUtility.DisplayDialog("Diversion Reset", "Missing access token, repo ID, or workspace ID. Please enter your Diversion token in the settings before using this feature.", "OK");
			return;
		}
		string repoId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), "");
		string workspaceId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionWorkspaceIdKey), "");
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;
		if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(workspaceId))
		{
			EditorUtility.DisplayDialog("Diversion Reset", "Missing access token, repo ID, or workspace ID.", "OK");
			return;
		}

		// Always fetch the latest status from Diversion before proceeding
		string statusApiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/status?detail_items=true&recurse=true&limit=1000";
		UnityWebRequest statusRequest = UnityWebRequest.Get(statusApiUrl);
		statusRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
		await statusRequest.SendWebRequest();

		// Gather all selected files and folders (no need to enumerate inside folders)
		HashSet<string> selectedPaths = new HashSet<string>();
		foreach (var obj in selected)
		{
			string path = AssetDatabase.GetAssetPath(obj);
			if (!string.IsNullOrEmpty(path))
			{
				selectedPaths.Add(path);
				if (!path.EndsWith(".meta"))
				{
					string metaPath = path + ".meta";
					if (System.IO.File.Exists(metaPath))
						selectedPaths.Add(metaPath);
				}
			}
		}
		if (selectedPaths.Count == 0)
		{
			EditorUtility.DisplayDialog("Diversion Reset", "No valid files or folders selected.", "OK");
			return;
		}

		List<string> newFiles = new List<string>();
		if (statusRequest.result == UnityWebRequest.Result.Success)
		{
			using var jsonDoc = JsonDocument.Parse(statusRequest.downloadHandler.text);
			var root = jsonDoc.RootElement;
			if (root.TryGetProperty("items", out var items))
			{
				if (items.TryGetProperty("new", out var arr) && arr.ValueKind == JsonValueKind.Array)
				{
					foreach (var entry in arr.EnumerateArray())
					{
						string path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
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
		else
		{
			EditorUtility.DisplayDialog("Diversion Reset", $"Failed to fetch status from Diversion: {statusRequest.error}", "OK");
			return;
		}

		// If all changes are selected, offer to use theall for efficiency
		bool canUseTheAll = false;
		{
			// If the user selected the root Assets folder, treat as 'reset all'
			if (selectedPaths.Contains("Assets"))
				canUseTheAll = true;
		}

		// For user popup: count unique assets, treating asset and .meta as one
		HashSet<string> uniqueAssetRoots = new HashSet<string>();
		foreach (var path in selectedPaths)
		{
			if (path.EndsWith(".meta"))
				uniqueAssetRoots.Add(path.Substring(0, path.Length - 5));
			else
				uniqueAssetRoots.Add(path);
		}
		foreach (var newFile in newFiles)
		{
			if (newFile.EndsWith(".meta"))
				uniqueAssetRoots.Add(newFile.Substring(0, newFile.Length - 5));
			else
				uniqueAssetRoots.Add(newFile);
		}

		// Confirmation dialog
		bool deleteNewFiles = false;
		string message = $"You are about to discard changes on {uniqueAssetRoots.Count} file(s) or folder(s) in the selection.";
		if (newFiles.Count > 0)
			message += $"\n\n{newFiles.Count} new file(s) will be deleted locally if you choose to delete new files.";
		if (canUseTheAll)
			message += "\n\nYou have selected the root folder. All changes will be reset.";
		message += "\n\nDo you want to proceed?";

		if (newFiles.Count > 0)
		{
			int result = EditorUtility.DisplayDialogComplex(
				"Discard",
				message,
				"Proceed and Delete New Files",
				"Cancel",
				"Proceed Without Deleting New Files"
			);
			if (result == 1) // Cancel
				return;
			deleteNewFiles = (result == 0);
		}
		else
		{
			if (!EditorUtility.DisplayDialog("Discard", message, "Proceed", "Cancel"))
				return;
		}

		// Prepare API call
		string apiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/reset";
		var payload = new Dictionary<string, object>();
		if (canUseTheAll)
		{
			payload["theall"] = true;
		}
		else
		{
			payload["paths"] = selectedPaths.ToList();
		}
		if (deleteNewFiles)
		{
			payload["delete_added"] = true;
		}
		using (UnityWebRequest webRequest = new UnityWebRequest(apiUrl, "POST"))
		{
			string json = System.Text.Json.JsonSerializer.Serialize(payload);
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
				// Schedule delayed check to delete empty selected folders after Unity refreshes
				var foldersToCheck = selectedPaths.Where(AssetDatabase.IsValidFolder).ToList();
				if (foldersToCheck.Count > 0)
				{
					EditorApplication.delayCall += () => {
						bool anyDeleted = false;
						foreach (var path in foldersToCheck)
						{
							if (!System.IO.Directory.Exists(path))
								continue;
							// Check for non-meta and non-hidden files
							var files = System.IO.Directory.GetFiles(path).Where(f => !f.EndsWith(".meta") && !System.IO.Path.GetFileName(f).StartsWith(".")).ToArray();
							var dirs = System.IO.Directory.GetDirectories(path);
							if (files.Length == 0 && dirs.Length == 0)
							{
								AssetDatabase.DeleteAsset(path);
								anyDeleted = true;
							}
							else if (DiversionStatusOverlay.DebugLogsEnabled)
							{
								Debug.Log($"Diversion Overlay: Folder '{path}' not deleted because it is not empty. Files: [{string.Join(", ", files.Select(System.IO.Path.GetFileName))}], Subfolders: [{string.Join(", ", dirs.Select(System.IO.Path.GetFileName))}]");
							}
						}
						if (anyDeleted)
						{
							AssetDatabase.Refresh();
						}
					};
				}
			}
			else
			{
				EditorUtility.DisplayDialog("Diversion Reset Failed", $"Error: {webRequest.error}\n{webRequest.downloadHandler.text}", "OK");
			}
		}
	}

	static void OnEditorUpdate()
	{
		refreshDelay = EditorPrefs.GetFloat(ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshDelayKey), 1.0f);
		if (pendingRefresh && (EditorApplication.timeSinceStartup - lastAssetChangeTime > refreshDelay))
		{
			pendingRefresh = false;
			UpdateStatusAsync();
		}
		// Auto refresh logic
		bool autoRefreshEnabled = EditorPrefs.GetBool(ProjectScopedKey(DiversionStatusOverlay.DiversionAutoRefreshEnabledKey), false);
		float minAutoRefresh = 5f;
		float autoRefreshInterval = EditorPrefs.GetFloat(ProjectScopedKey(DiversionStatusOverlay.DiversionAutoRefreshIntervalKey), minAutoRefresh);
		if (autoRefreshEnabled && (EditorApplication.timeSinceStartup - lastAutoRefreshTime > autoRefreshInterval))
		{
			lastAutoRefreshTime = EditorApplication.timeSinceStartup;
			UpdateStatusAsync();
		}
		// Check if access token needs to be refreshed
		CheckAndRefreshAccessTokenIfNeeded();
	}

	static void CheckAndRefreshAccessTokenIfNeeded()
	{
		double lastRefresh = EditorPrefs.GetFloat(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenLastRefreshKey), 0f);
		double now = EditorApplication.timeSinceStartup;
		if (now - lastRefresh > AccessTokenRefreshIntervalSeconds)
		{
			string refreshToken = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshTokenKey), "");
			if (!string.IsNullOrEmpty(refreshToken))
			{
				_ = ExchangeRefreshTokenForAccessToken(refreshToken);
				EditorPrefs.SetFloat(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenLastRefreshKey), (float)now);
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

	// Recursively delete empty folders
	private static void DeleteEmptyFoldersRecursive(string folderPath)
	{
		if (!AssetDatabase.IsValidFolder(folderPath))
			return;

		// Recurse into subfolders first
		var subfolders = System.IO.Directory.GetDirectories(folderPath);
		foreach (var sub in subfolders)
		{
			DeleteEmptyFoldersRecursive(sub.Replace('\\', '/'));
		}

		// After deleting subfolders, check if this folder is now empty
		var files = System.IO.Directory.GetFiles(folderPath).Where(f => !f.EndsWith(".meta")).ToArray();
		var dirs = System.IO.Directory.GetDirectories(folderPath);
		if (files.Length == 0 && dirs.Length == 0)
		{
			AssetDatabase.DeleteAsset(folderPath);
		}
	}

	private static void OnEditorFocusChanged(bool hasFocus)
	{
		if (hasFocus)
		{
			pendingRefresh = true;
			lastAssetChangeTime = EditorApplication.timeSinceStartup;
		}
	}

	public static bool DebugLogsEnabled => EditorPrefs.GetBool(ProjectScopedKey(DiversionStatusOverlay.DiversionDebugLogsKey), false);

	[MenuItem("Assets/Diversion/Compare in Diff Tool with Commit", true, 2100)]
	private static bool ValidateCompareInDiffToolWithCommit()
	{
		var selected = Selection.activeObject;
		if (selected == null) return false;
		string path = AssetDatabase.GetAssetPath(selected);
		// Only allow for files, not folders
		return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
	}

	// Helper to get Diversion agent port from ~/.diversion/.port
	private static string GetDiversionAgentPort()
	{
		string homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
		string portFile = System.IO.Path.Combine(homeDir, ".diversion", ".port");
		if (System.IO.File.Exists(portFile))
		{
			string portStr = System.IO.File.ReadAllText(portFile).Trim();
			if (int.TryParse(portStr, out int port))
				return portStr;
		}
		return null;
	}

	// Helper to get agent API base URL, or null if not available
	private static string GetAgentApiBaseUrl()
	{
		string port = GetDiversionAgentPort();
		if (!string.IsNullOrEmpty(port))
			return $"http://localhost:{port}";
		return null;
	}

	// Helper to get workspace config for a given path using the agent, now returns branch id (e.g., dv.branch.1)
	private static async Task<(string repoId, string workspaceId, string branchId)> GetWorkspaceConfigForPath(string assetPath)
	{
		string apiBase = GetAgentApiBaseUrl();
		if (string.IsNullOrEmpty(apiBase))
		{
			Debug.LogError("Diversion Overlay: No access token found in EditorPrefs. Please enter your Diversion token in the settings before using this feature.");
			EditorUtility.DisplayDialog("Diversion Agent Not Found", "No access token found in EditorPrefs. Please enter your Diversion token in the settings before using this feature.", "OK");
			return (null, null, null);
		}
		string repoRoot = FindRepoRoot(assetPath);
		if (repoRoot == null)
			return (null, null, null);
		if (DebugLogsEnabled) Debug.Log($"Diversion Overlay: Looking up workspace config for repoRoot={repoRoot}, abs_path={UnityWebRequest.EscapeURL(repoRoot).Replace("+", "%20")}");
		string url = $"{apiBase}/v0/workspaces/by-path?abs_path={UnityWebRequest.EscapeURL(repoRoot).Replace("+", "%20")}";
		using (UnityWebRequest req = UnityWebRequest.Get(url))
		{
			await req.SendWebRequest();
			if (req.result == UnityWebRequest.Result.Success)
			{
				using var jsonDoc = JsonDocument.Parse(req.downloadHandler.text);
				var root = jsonDoc.RootElement;
				if (DebugLogsEnabled) Debug.Log($"Diversion Overlay: Workspace config JSON: {root.GetRawText()}");
				if (DebugLogsEnabled && root.ValueKind != JsonValueKind.Object)
				{
					Debug.Log("Diversion Overlay: Workspace config JSON keys: " + string.Join(", ", root.EnumerateObject().Select(p => p.Name)));
				}
				if (root.ValueKind == JsonValueKind.Object)
				{
					string repoId = root.TryGetProperty("repo_id", out var repoIdProp) ? repoIdProp.GetString() : (root.TryGetProperty("RepoID", out var repoIdProp2) ? repoIdProp2.GetString() : null);
					string workspaceId = root.TryGetProperty("workspace_id", out var wsIdProp) ? wsIdProp.GetString() : (root.TryGetProperty("WorkspaceID", out var wsIdProp2) ? wsIdProp2.GetString() : null);
					string branchId = root.TryGetProperty("branch_id", out var branchIdProp) ? branchIdProp.GetString() : (root.TryGetProperty("BranchID", out var branchIdProp2) ? branchIdProp2.GetString() : null);
					string branch = root.TryGetProperty("branch", out var branchProp) ? branchProp.GetString() : (root.TryGetProperty("BranchName", out var branchProp2) ? branchProp2.GetString() : null);
					string refField = root.TryGetProperty("ref", out var refProp) ? refProp.GetString() : (root.TryGetProperty("CommitID", out var refProp2) ? refProp2.GetString() : null);
					if (DebugLogsEnabled)
					{
						Debug.Log($"Diversion Overlay: branch_id={branchId}, branch={branch}, ref={refField}");
					}
					branchId = branchId ?? branch ?? refField;
					if (string.IsNullOrEmpty(branchId))
					{
						Debug.LogError("Diversion Overlay: No branch_id, branch, or ref found in workspace config. Cannot proceed.");
						return (repoId, workspaceId, null);
					}
					if (DebugLogsEnabled) Debug.Log($"Diversion Overlay: Using branchId={branchId}");
					return (repoId, workspaceId, branchId);
				}
			}
		}
		return (null, null, null);
	}

	// Helper to fetch commit history for a file, refId can be branch or commit
	private static async Task<List<(string id, string message)>> GetFileCommitHistory(string repoId, string refId, string relPath, string accessToken)
	{
		var commits = new List<(string, string)>();
		string url = $"https://api.diversion.dev/v0/repos/{repoId}/object-history/{refId}/{UnityWebRequest.EscapeURL(relPath).Replace("+", "%20")}";
		if (DebugLogsEnabled)
			Debug.Log($"Diversion Overlay: Commit history API: {url}");
		using (UnityWebRequest req = UnityWebRequest.Get(url))
		{
			req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			await req.SendWebRequest();
			if (req.result == UnityWebRequest.Result.Success)
			{
				using var jsonDoc = JsonDocument.Parse(req.downloadHandler.text);
				var root = jsonDoc.RootElement;
				if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
				{
					foreach (var entry in entries.EnumerateArray())
					{
						string commitId = entry.TryGetProperty("commit_id", out var commitIdProp) ? commitIdProp.GetString() : null;
						string msg = entry.TryGetProperty("commit_message", out var msgProp) ? msgProp.GetString() : null;
						commits.Add((commitId, msg));
					}
				}
			}
		}
		return commits;
	}

	// Show a popup to select a commit from history
	private static int ShowCommitSelectionPopup(List<(string id, string message)> commits)
	{
		int selected = 0;
		string[] options = commits.Select(c => $"{c.id}: {c.message}").ToArray();
		selected = EditorUtility.DisplayDialogComplex("Select Commit to Diff Against", "Choose a commit:", options.Length > 0 ? options[0] : "", options.Length > 1 ? options[1] : "", "Cancel");
		return selected;
	}

	[MenuItem("Assets/Diversion/Compare in Diff Tool with Commit", false, 2100)]
	public static async void CompareInDiffToolWithCommit()
	{
		var selected = Selection.activeObject;
		if (selected == null) return;
		string path = AssetDatabase.GetAssetPath(selected);
		if (string.IsNullOrEmpty(path)) return;

		string accessToken = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), "");
		if (string.IsNullOrEmpty(accessToken))
		{
			EditorUtility.DisplayDialog("Diversion API Error", "No access token found in EditorPrefs. Please enter your Diversion token in the settings before using this feature.", "OK");
			return;
		}
		string apiBase = GetAgentApiBaseUrl(); // always cloud for downloads
		string repoId = null, workspaceId = null, branchId = null;
		(repoId, workspaceId, branchId) = await GetWorkspaceConfigForPath(path);
		if (string.IsNullOrEmpty(repoId))
		{
			repoId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), "");
		}
		if (string.IsNullOrEmpty(workspaceId))
		{
			workspaceId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionWorkspaceIdKey), "");
		}
		if (string.IsNullOrEmpty(branchId))
		{
			branchId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionBranchRefKey), "dv.branch.1");
		}
		// Ensure prefixes
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;
		if (!string.IsNullOrEmpty(branchId) && !branchId.StartsWith("dv.branch."))
			branchId = "dv.branch." + branchId;
		if (string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(branchId))
		{
			if (DebugLogsEnabled)
			{
				Debug.LogError($"Diversion Overlay: accessToken={accessToken}, repoId={repoId}, workspaceId={workspaceId}, branchId={branchId}");
			}
			EditorUtility.DisplayDialog("Diversion API Error", "Missing access token, repo ID, workspace ID, or branch ID.", "OK");
			return;
		}

		string diffToolName = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolNameKey), "Meld");
		string diffToolPath = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolPathKey), "");
		if (string.IsNullOrEmpty(diffToolPath) || !System.IO.File.Exists(diffToolPath))
		{
			EditorUtility.DisplayDialog("Diff Tool Not Found", $"Diff tool was not found at: {diffToolPath}\nPlease set the correct path in Diversion Project Settings.", "OK");
			return;
		}
		var toolInfo = GetPlatformDiffTools().FirstOrDefault(t => t.Name == diffToolName);
		string argFormat = toolInfo != null ? toolInfo.ArgumentsFormat : "\"{0}\" \"{1}\"";

		// Find repo root and get relative path
		string repoRoot = FindRepoRoot(path);
		if (repoRoot == null)
		{
			EditorUtility.DisplayDialog("Diversion Error", "Could not find Diversion repo root for this file.", "OK");
			return;
		}
		string relPath = ToRelativePath(path, repoRoot);

		// Download the latest tracked version from the branch
		string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiversionCompare");
		System.IO.Directory.CreateDirectory(tempDir);
		string tempPath = System.IO.Path.Combine(tempDir, System.IO.Path.GetFileName(path));

		bool success = await DownloadBlob(repoId, branchId, relPath, accessToken, tempPath);
		if (!success)
		{
			EditorUtility.DisplayDialog("Download Failed", $"Failed to download file for comparison from Diversion.", "OK");
			return;
		}

		// Launch Meld
		var diffPsi = new System.Diagnostics.ProcessStartInfo
		{
			FileName = diffToolPath,
			Arguments = string.Format(argFormat, System.IO.Path.GetFullPath(path), tempPath),
			UseShellExecute = false,
			CreateNoWindow = true
		};
		System.Diagnostics.Process.Start(diffPsi);

		if (DebugLogsEnabled)
		{
			Debug.Log($"Diversion Compare: Downloaded and diffed {relPath}");
		}
	}

	// Helper to download a blob (file at a specific ref)
	private static async Task<bool> DownloadBlob(string repoId, string refId, string relPath, string accessToken, string outputPath)
	{
		string url = $"https://api.diversion.dev/v0/repos/{repoId}/blobs/{refId}/{UnityWebRequest.EscapeURL(relPath).Replace("+", "%20")}";
		using (UnityWebRequest req = UnityWebRequest.Get(url))
		{
			req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			await req.SendWebRequest();

			// Direct download
			if (req.responseCode == 200)
			{
				System.IO.File.WriteAllBytes(outputPath, req.downloadHandler.data);
				if (DebugLogsEnabled) Debug.Log($"Downloaded {relPath} from {repoId} at {refId}");
				return true;
			}
			// Diversion's redirect via 204 + Location
			else if (req.responseCode == 204)
			{
				string redirectUrl = req.GetResponseHeader("Location");
				if (!string.IsNullOrEmpty(redirectUrl))
				{
					using (UnityWebRequest redirectReq = UnityWebRequest.Get(redirectUrl))
					{
						await redirectReq.SendWebRequest();
						if (redirectReq.responseCode == 200)
						{
							System.IO.File.WriteAllBytes(outputPath, redirectReq.downloadHandler.data);
							if (DebugLogsEnabled) Debug.Log($"Downloaded {relPath} from redirector link");
							return true;
						}
						else
						{
							Debug.LogError($"Failed to download from redirector: {redirectReq.responseCode} - {redirectReq.error}");
						}
					}
				}
				else
				{
					Debug.LogError("No redirect URL found in response headers.");
				}
			}
			// Fallback for HTTP 3xx (just in case)
			else if (req.responseCode >= 300 && req.responseCode < 400)
			{
				string redirectUrl = req.GetResponseHeader("Location");
				if (!string.IsNullOrEmpty(redirectUrl))
				{
					using (UnityWebRequest redirectReq = UnityWebRequest.Get(redirectUrl))
					{
						await redirectReq.SendWebRequest();
						if (redirectReq.responseCode == 200)
						{
							System.IO.File.WriteAllBytes(outputPath, redirectReq.downloadHandler.data);
							if (DebugLogsEnabled) Debug.Log($"Downloaded {relPath} from HTTP 3xx redirect");
							return true;
						}
						else
						{
							Debug.LogError($"Failed to download from HTTP 3xx redirect: {redirectReq.responseCode} - {redirectReq.error}");
						}
					}
				}
				else
				{
					Debug.LogError("No redirect URL found in 3xx response headers.");
				}
			}
			else
			{
				Debug.LogError($"Failed to download {relPath}: {req.responseCode} - {req.error}\n{req.downloadHandler.text}");
			}
		}
		return false;
	}

	// Helper to find repo root by searching for .diversion folder
	private static string FindRepoRoot(string assetPath)
	{
		string fullPath = System.IO.Path.GetFullPath(assetPath);
		string dir = System.IO.Path.GetDirectoryName(fullPath);
		while (!string.IsNullOrEmpty(dir) && dir != System.IO.Path.GetPathRoot(dir))
		{
			if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, ".diversion")))
				return dir;
			dir = System.IO.Path.GetDirectoryName(dir);
		}
		return null;
	}

	// Helper to get path relative to repo root
	private static string ToRelativePath(string assetPath, string repoRoot)
	{
		string fullPath = System.IO.Path.GetFullPath(assetPath).Replace("\\", "/");
		string repoRootNorm = repoRoot.Replace("\\", "/");
		if (fullPath.StartsWith(repoRootNorm))
			return fullPath.Substring(repoRootNorm.Length).TrimStart('/');
		return assetPath;
	}

	[MenuItem("Assets/Diversion/Download Latest From Diversion", false, 2150)]
	public static async void DownloadLatestFromDiversionImmediate()
	{
		var selected = Selection.objects;
		List<string> selectedPaths = new List<string>();
		if (selected != null && selected.Length > 0)
		{
			foreach (var obj in selected)
			{
				string path = AssetDatabase.GetAssetPath(obj);
				if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
					selectedPaths.Add(path);
			}
		}
		if (selectedPaths.Count == 0)
		{
			EditorUtility.DisplayDialog("Diversion Download", "No files selected.", "OK");
			return;
		}
		string repoId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), "");
		string branchId = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionBranchRefKey), "dv.branch.1");
		string accessToken = EditorPrefs.GetString(ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), "");
		if (string.IsNullOrEmpty(accessToken))
		{
			EditorUtility.DisplayDialog("Diversion Download", "No access token found in EditorPrefs. Please enter your Diversion token in the settings before using this feature.", "OK");
			return;
		}
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(branchId) && !branchId.StartsWith("dv.branch."))
			branchId = "dv.branch." + branchId;
		string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiversionDownload");
		System.IO.Directory.CreateDirectory(tempDir);
		int successCount = 0;
		List<string> failedFiles = new List<string>();
		foreach (var relPath in selectedPaths)
		{
			string outputPath = System.IO.Path.Combine(tempDir, System.IO.Path.GetFileName(relPath));
			bool success = await DownloadDiversionFile(repoId, branchId, relPath, accessToken, outputPath);
			if (success)
				successCount++;
			else
				failedFiles.Add(relPath);
		}
		if (successCount > 0)
		{
			EditorUtility.RevealInFinder(tempDir);
		}
		var window = ScriptableObject.CreateInstance<DiversionDownloadResultWindow>();
		window.titleContent = new GUIContent("Diversion Download Results");
		window.minSize = new Vector2(700, 340);
		window.downloadedFiles = selectedPaths.Except(failedFiles).ToList();
		window.failedFiles = failedFiles;
		window.tempDir = tempDir;
		window.ShowModalUtility();
	}

	public static async Task<bool> DownloadDiversionFile(string repoId, string branchId, string relPath, string accessToken, string outputPath)
	{
		string url = $"https://api.diversion.dev/v0/repos/{repoId}/blobs/{branchId}/{UnityWebRequest.EscapeURL(relPath).Replace("+", "%20")}";
		using (UnityWebRequest req = UnityWebRequest.Get(url))
		{
			req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			await req.SendWebRequest();

			if (req.responseCode == 200)
			{
				System.IO.File.WriteAllBytes(outputPath, req.downloadHandler.data);
				Debug.Log($"Downloaded {relPath} from {repoId} at {branchId}");
				return true;
			}
			else if (req.responseCode == 204)
			{
				string redirectUrl = req.GetResponseHeader("Location");
				if (!string.IsNullOrEmpty(redirectUrl))
				{
					using (UnityWebRequest redirectReq = UnityWebRequest.Get(redirectUrl))
					{
						await redirectReq.SendWebRequest();
						if (redirectReq.responseCode == 200)
						{
							System.IO.File.WriteAllBytes(outputPath, redirectReq.downloadHandler.data);
							Debug.Log($"Downloaded {relPath} from redirector link");
							return true;
						}
					}
				}
			}
			Debug.LogError($"Failed to download {relPath}: {req.responseCode} - {req.error}\n{req.downloadHandler.text}");
		}
		return false;
	}
}

public class DiversionOverlaySettingsProvider : SettingsProvider
{
	private string refreshToken;
	private string accessToken;
	private string apiKey; // deprecated, but kept for backward compatibility
	private string repoId;
	private string workspaceId;
	private string cliPath;
	private float refreshIntervalSetting;
	private int maxFilesSetting;
	private bool autoRefreshEnabled;
	private float autoRefreshInterval;
	private List<DiversionStatusOverlay.DiffToolInfo> diffTools;
	private string selectedDiffToolName;
	private string selectedDiffToolPath;

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
		refreshToken = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshTokenKey), "");
		accessToken = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), "");
		apiKey = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAPIKey), "");
		repoId = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), "");
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		workspaceId = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionWorkspaceIdKey), "");
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;
		cliPath = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionCLIPathKey), "");
		if (string.IsNullOrEmpty(cliPath) || !System.IO.File.Exists(cliPath))
			cliPath = DiversionStatusOverlay.AutoDetectDiversionCLIPath() ?? "";
		if (!string.IsNullOrEmpty(cliPath))
			EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionCLIPathKey), cliPath);
		maxFilesSetting = EditorPrefs.GetInt(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionMaxFilesKey), 1000);
		autoRefreshEnabled = EditorPrefs.GetBool(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAutoRefreshEnabledKey), false);
		float minAutoRefresh = 5f;
		autoRefreshInterval = EditorPrefs.GetFloat(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAutoRefreshIntervalKey), minAutoRefresh);
		diffTools = DiversionStatusOverlay.GetPlatformDiffTools();
		DiversionStatusOverlay.AutoDetectDiffTools(diffTools);
		selectedDiffToolName = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolNameKey), diffTools.FirstOrDefault(t => t.IsAvailable)?.Name ?? "Meld");
		selectedDiffToolPath = EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolPathKey), "");
	}

	public override void OnGUI(string searchContext)
	{
		EditorGUILayout.Space();
		EditorGUILayout.Space();
		GUILayout.BeginHorizontal();
		GUILayout.Space(20); // Left margin
		GUILayout.BeginVertical(GUIStyle.none);

		EditorGUILayout.LabelField("Diversion Settings", EditorStyles.boldLabel);
		EditorGUILayout.Space();

		// CLI Path at the top
		EditorGUILayout.LabelField("Diversion CLI Path");
		EditorGUILayout.SelectableLabel(cliPath, EditorStyles.textField, GUILayout.Height(18));
		if (GUILayout.Button("Auto-detect Diversion CLI Path"))
		{
			cliPath = DiversionStatusOverlay.AutoDetectDiversionCLIPath() ?? "";
			if (!string.IsNullOrEmpty(cliPath))
				EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionCLIPathKey), cliPath);
			ReloadFields();
		}
		EditorGUILayout.Space();

		EditorGUI.BeginChangeCheck();
		string newRefreshToken = EditorGUILayout.TextField("Integration Token", refreshToken);
		if (newRefreshToken != refreshToken)
		{
			refreshToken = newRefreshToken;
			EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshTokenKey), refreshToken);
		}
		EditorGUILayout.Space();
		EditorGUILayout.Space();
		string newRepoId = EditorGUILayout.TextField("Repo ID", repoId);
		if (newRepoId != repoId)
		{
			repoId = newRepoId;
			EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRepoIdKey), repoId);
		}
		string newWorkspaceId = EditorGUILayout.TextField("Workspace ID", workspaceId);
		if (newWorkspaceId != workspaceId)
		{
			workspaceId = newWorkspaceId;
			EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionWorkspaceIdKey), workspaceId);
		}

		EditorGUILayout.Space();
		if (GUILayout.Button("Auto-detect IDs from CLI"))
		{
			DiversionStatusOverlay.FetchRepoAndWorkspaceIds();
			ReloadFields();
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Access Token (read-only):");
		EditorGUILayout.SelectableLabel(EditorPrefs.GetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAccessTokenKey), ""), EditorStyles.textField, GUILayout.Height(40));

		EditorGUILayout.Space();
		if (GUILayout.Button("Refresh Access Token"))
		{
			if (!string.IsNullOrEmpty(refreshToken))
			{
				_ = DiversionStatusOverlay.ExchangeRefreshTokenForAccessToken(refreshToken);
			}
			else
			{
				if (DiversionStatusOverlay.DebugLogsEnabled) Debug.LogWarning("Diversion Overlay: Please enter a refresh token first.");
			}
		}

		EditorGUILayout.Space();
		// Sliders group
		EditorGUILayout.LabelField("Status Refresh Delay After Change (seconds)");
		EditorGUI.BeginChangeCheck();
		float delaySetting = EditorPrefs.GetFloat(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshDelayKey), 1.0f);
		delaySetting = EditorGUILayout.Slider(delaySetting, 0.1f, 10.0f);
		if (EditorGUI.EndChangeCheck())
		{
			EditorPrefs.SetFloat(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionRefreshDelayKey), delaySetting);
		}

		EditorGUILayout.LabelField("Max Files to Check per API Call");
		EditorGUI.BeginChangeCheck();
		maxFilesSetting = EditorGUILayout.IntSlider(maxFilesSetting, 100, 5000);
		if (EditorGUI.EndChangeCheck())
		{
			EditorPrefs.SetInt(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionMaxFilesKey), maxFilesSetting);
		}

		EditorGUILayout.Space();
		// Auto Refresh Option (moved here)
		EditorGUILayout.LabelField("Auto Refresh Status Icons", EditorStyles.boldLabel);
		EditorGUILayout.BeginHorizontal();
		bool newAutoRefreshEnabled = EditorGUILayout.ToggleLeft("Enable Auto Refresh", autoRefreshEnabled, GUILayout.Width(160));
		if (newAutoRefreshEnabled)
		{
			Texture2D refreshIcon = EditorGUIUtility.FindTexture("d_Refresh");
			if (refreshIcon != null)
			{
				GUILayout.Label(refreshIcon, GUILayout.Width(20), GUILayout.Height(20));
			}
		}
		EditorGUILayout.EndHorizontal();
		float minAutoRefresh = 5f;
		float newAutoRefreshInterval = EditorGUILayout.Slider("Interval (seconds)", autoRefreshInterval, minAutoRefresh, 60f);
		if (newAutoRefreshEnabled != autoRefreshEnabled)
		{
			EditorPrefs.SetBool(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAutoRefreshEnabledKey), newAutoRefreshEnabled);
			autoRefreshEnabled = newAutoRefreshEnabled;
		}
		if (Mathf.Abs(newAutoRefreshInterval - autoRefreshInterval) > 0.01f)
		{
			EditorPrefs.SetFloat(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionAutoRefreshIntervalKey), newAutoRefreshInterval);
			autoRefreshInterval = newAutoRefreshInterval;
		}
		EditorGUILayout.Space();

		// Restore the following sections:
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

		EditorGUILayout.Space();
		bool debugLogs = EditorPrefs.GetBool(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDebugLogsKey), false);
		bool newDebugLogs = EditorGUILayout.Toggle("Enable Debug Logs", debugLogs);
		if (newDebugLogs != debugLogs)
		{
			EditorPrefs.SetBool(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDebugLogsKey), newDebugLogs);
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Diff Tool");
		var availableToolNames = diffTools.Where(t => t.IsAvailable).Select(t => t.Name).ToList();
		availableToolNames.Add("Custom");
		int selectedIndex = Mathf.Max(0, availableToolNames.IndexOf(selectedDiffToolName));
		string[] toolNamesArray = availableToolNames.ToArray();
		int newSelectedIndex = EditorGUILayout.Popup("Select Diff Tool", selectedIndex, toolNamesArray);
		string newSelectedName = toolNamesArray[newSelectedIndex];
		if (newSelectedName != selectedDiffToolName)
		{
			selectedDiffToolName = newSelectedName;
			EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolNameKey), selectedDiffToolName);
			// If not custom, auto-fill path
			if (selectedDiffToolName != "Custom")
			{
				var tool = diffTools.FirstOrDefault(t => t.Name == selectedDiffToolName);
				if (tool != null)
				{
					string foundPath = tool.PossiblePaths.FirstOrDefault(p => System.IO.File.Exists(p));
					if (!string.IsNullOrEmpty(foundPath))
					{
						selectedDiffToolPath = foundPath;
						EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolPathKey), selectedDiffToolPath);
					}
				}
			}
		}
		string newDiffToolPath = selectedDiffToolPath;
		if (selectedDiffToolName == "Custom")
		{
			newDiffToolPath = EditorGUILayout.TextField("Diff Tool Path", selectedDiffToolPath);
		}
		else
		{
			EditorGUILayout.LabelField("Diff Tool Path", selectedDiffToolPath);
		}
		if (newDiffToolPath != selectedDiffToolPath)
		{
			selectedDiffToolPath = newDiffToolPath;
			EditorPrefs.SetString(DiversionStatusOverlay.ProjectScopedKey(DiversionStatusOverlay.DiversionDiffToolPathKey), selectedDiffToolPath);
		}

		GUILayout.EndVertical();
		GUILayout.Space(8);
		GUILayout.EndHorizontal();
	}
}

public class DiversionDownloadResultWindow : EditorWindow
{
	public List<string> downloadedFiles;
	public List<string> failedFiles;
	public string tempDir;

	public static void ShowResults(List<string> downloaded, List<string> failed, string tempDir)
	{
		var window = ScriptableObject.CreateInstance<DiversionDownloadResultWindow>();
		window.titleContent = new GUIContent("Diversion Download Results");
		window.minSize = new Vector2(700, 340);
		window.downloadedFiles = downloaded.Except(failed).ToList();
		window.failedFiles = failed;
		window.tempDir = tempDir;
		window.ShowModalUtility();
	}

	private void OnGUI()
	{
		// Set window background color to rgba(96, 96, 96, 0.2039216)
		Color bgCol = new Color32(0x29, 0x29, 0x29, 0xFF); // #292929
		Rect bgRect = new Rect(0, 0, position.width, position.height);
		EditorGUI.DrawRect(bgRect, bgCol);

		// Padding
		GUILayout.BeginVertical();
		GUILayout.Space(20);
		GUILayout.BeginHorizontal();
		GUILayout.Space(20);
		GUILayout.BeginVertical();

		// Use default Unity styles for all text except the button
		EditorGUILayout.LabelField("Diversion Download Results", EditorStyles.boldLabel);
		GUILayout.Space(20); // Extra margin below the icon/title
		if (!string.IsNullOrEmpty(tempDir))
		{
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("Download Directory:", EditorStyles.label, GUILayout.Width(140));
			if (GUILayout.Button(tempDir, EditorStyles.linkLabel))
			{
				EditorUtility.RevealInFinder(tempDir);
			}
			EditorGUILayout.EndHorizontal();
		}
		EditorGUILayout.Space();
		EditorGUILayout.LabelField($"Downloaded Files ({downloadedFiles.Count}):", EditorStyles.boldLabel);
		foreach (var file in downloadedFiles)
		{
			if (GUILayout.Button(file, EditorStyles.linkLabel))
			{
				var asset = AssetDatabase.LoadAssetAtPath<Object>(file);
				if (asset != null)
					EditorGUIUtility.PingObject(asset);
				else
				{
					string tempFile = System.IO.Path.Combine(tempDir, System.IO.Path.GetFileName(file));
					EditorUtility.RevealInFinder(tempFile);
				}
			}
		}
		if (failedFiles != null && failedFiles.Count > 0)
		{
			EditorGUILayout.Space();
			EditorGUILayout.LabelField($"Failed to Download ({failedFiles.Count}):", EditorStyles.boldLabel);
			foreach (var file in failedFiles)
			{
				EditorGUILayout.LabelField(file, EditorStyles.wordWrappedLabel);
			}
		}
		EditorGUILayout.Space();
		GUILayout.FlexibleSpace();
		var okButtonStyle = new GUIStyle(GUI.skin.button);
		okButtonStyle.normal.textColor = Color.white;
		okButtonStyle.fontSize = 16;
		okButtonStyle.fontStyle = FontStyle.Bold;
		okButtonStyle.fixedHeight = 25;
		Color okBlue = new Color32(0x3A, 0x79, 0xBB, 0xFF); // #3A79BB
		okButtonStyle.normal.background = MakeRoundedTex(200, 25, okBlue, 5);
		okButtonStyle.hover.background = MakeRoundedTex(200, 25, okBlue * 0.9f, 5);
		okButtonStyle.border = new RectOffset(5, 5, 5, 5);
		GUILayout.BeginHorizontal();
		GUILayout.FlexibleSpace();
		GUI.SetNextControlName("OKButton");
		if (GUILayout.Button("OK", okButtonStyle, GUILayout.Width(200), GUILayout.Height(25)))
		{
			this.Close();
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.Space(20);
		if (Event.current.type == EventType.Repaint)
		{
			EditorGUI.FocusTextInControl("OKButton");
		}
		if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
		{
			this.Close();
			GUIUtility.ExitGUI();
		}
		GUILayout.EndVertical();
	}

	// Helper for blue button background
	Texture2D MakeRoundedTex(int width, int height, Color col, int radius)
	{
		Texture2D tex = new Texture2D(width, height);
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				bool inside = (x >= radius && x < width - radius) || (y >= radius && y < height - radius);
				if (!inside)
				{
					// Check distance to nearest corner
					int dx = x < radius ? radius - x : x - (width - 1 - radius);
					int dy = y < radius ? radius - y : y - (height - 1 - radius);
					float dist = Mathf.Sqrt(dx * dx + dy * dy);
					if (dist > radius)
					{
						tex.SetPixel(x, y, Color.clear);
						continue;
					}
				}
				tex.SetPixel(x, y, col);
			}
		}
		tex.Apply();
		return tex;
	}
}
#endif
