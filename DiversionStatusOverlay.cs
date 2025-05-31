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
	static Dictionary<string, Texture2D> statusIcons = new();
	static Dictionary<string, string> fileStatus = new();
	static Dictionary<string, string> folderStatus = new();

	public const string DiversionRepoIdKey = "DiversionOverlay.RepoId";
	public const string DiversionWorkspaceIdKey = "DiversionOverlay.WorkspaceId";
	public const string DiversionAPIKey = "DiversionOverlay.APIKey";
	public const string DiversionRefreshTokenKey = "DiversionOverlay.RefreshToken";
	public const string DiversionAccessTokenKey = "DiversionOverlay.AccessToken";

	static DiversionStatusOverlay()
	{
		LoadIcons();
		EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
		EditorApplication.update += OnEditorUpdate;
		UpdateStatusAsync();
	}

	static void LoadIcons()
	{
		statusIcons["A"] = EditorGUIUtility.FindTexture("PackageBadgeNew");
		statusIcons["M"] = EditorGUIUtility.FindTexture("d_CollabEdit Icon");
		statusIcons["D"] = EditorGUIUtility.FindTexture("Collab.FileDeleted");
		statusIcons["C"] = EditorGUIUtility.FindTexture("d_CollabConflict Icon");
		statusIcons["U"] = EditorGUIUtility.FindTexture("Collab");
		statusIcons["moved"] = EditorGUIUtility.FindTexture("CollabMoved Icon");
		statusIcons["added"] = statusIcons["A"];
		statusIcons["deleted"] = statusIcons["D"];
		statusIcons["modified"] = statusIcons["M"];
		statusIcons["conflicted"] = statusIcons["C"];
		statusIcons["uptodate"] = statusIcons["U"];
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

		// Auto-add Diversion prefixes if missing
		if (!string.IsNullOrEmpty(repoId) && !repoId.StartsWith("dv.repo."))
			repoId = "dv.repo." + repoId;
		if (!string.IsNullOrEmpty(workspaceId) && !workspaceId.StartsWith("dv.ws."))
			workspaceId = "dv.ws." + workspaceId;

		if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(workspaceId))
		{
			Debug.LogWarning("Diversion Overlay: Missing access token, repo ID, or workspace ID.");
			return;
		}

		string apiUrl = $"https://api.diversion.dev/v0/repos/{repoId}/workspaces/{workspaceId}/status?detail_items=true&recurse=true&limit=1000";
		Debug.Log($"Diversion Overlay: Using API URL: {apiUrl}");
		Debug.Log($"Repo ID: {repoId}, Workspace ID: {workspaceId}");
		Debug.Log("Diversion Overlay: Fetching status from API...");

		using (UnityWebRequest webRequest = UnityWebRequest.Get(apiUrl))
		{
			webRequest.SetRequestHeader("Authorization", $"Bearer {accessToken}");
			await webRequest.SendWebRequest();

			if (webRequest.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError("Diversion Overlay: API Request Failed: " + webRequest.error);
				return;
			}

			ParseDiversionAPIOutput(webRequest.downloadHandler.text);
			EditorApplication.RepaintProjectWindow();
		}
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

	static void OnEditorUpdate()
	{
		// Optionally, schedule periodic refreshes here
	}

	[MenuItem("Tools/Diversion/Refresh Status")]
	public static void ManualRefreshStatus()
	{
		UpdateStatusAsync();
		EditorApplication.RepaintProjectWindow();
	}

	public static void FetchRepoAndWorkspaceIds()
	{
		string diversionCLIPath = "/Users/macstudio/.diversion/bin/dv"; // Update if needed
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
}

public class DiversionOverlaySettingsProvider : SettingsProvider
{
	private string refreshToken;
	private string accessToken;
	private string apiKey; // deprecated, but kept for backward compatibility
	private string repoId;
	private string workspaceId;
	private bool accessTokenDirty = false;

	public DiversionOverlaySettingsProvider(string path, SettingsScope scope = SettingsScope.Project)
		: base(path, scope) { }

	[SettingsProvider]
	public static SettingsProvider CreateSettingsProvider()
	{
		return new DiversionOverlaySettingsProvider("Project/Diversion Overlay", SettingsScope.Project);
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
	}

	public override void OnGUI(string searchContext)
	{
		EditorGUILayout.LabelField("Diversion Overlay Settings", EditorStyles.boldLabel);
		EditorGUILayout.Space();

		EditorGUI.BeginChangeCheck();
		refreshToken = EditorGUILayout.TextField("Refresh Token", refreshToken);
		EditorGUILayout.LabelField("Repo ID");
		EditorGUILayout.SelectableLabel(repoId, EditorStyles.textField, GUILayout.Height(18));
		EditorGUILayout.LabelField("Workspace ID");
		EditorGUILayout.SelectableLabel(workspaceId, EditorStyles.textField, GUILayout.Height(18));
		if (EditorGUI.EndChangeCheck())
		{
			EditorPrefs.SetString(DiversionStatusOverlay.DiversionRefreshTokenKey, refreshToken);
			// When refresh token changes, get new access token
			if (!string.IsNullOrEmpty(refreshToken))
			{
				_ = DiversionStatusOverlay.ExchangeRefreshTokenForAccessToken(refreshToken);
			}
		}

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
	}
}
#endif
