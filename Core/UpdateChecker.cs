using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Checks for application updates from GitHub releases
    /// </summary>
    public class UpdateChecker
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/{OWNER}/{REPO}/releases/latest";
        private const string CURRENT_VERSION = "2.0.0"; // Update this with each release
        
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders = { { "User-Agent", "WalletRecoveryTool" } }
        };
        
        public class UpdateInfo
        {
            public bool UpdateAvailable { get; set; }
            public string LatestVersion { get; set; }
            public string CurrentVersion { get; set; }
            public string ReleaseNotes { get; set; }
            public string DownloadUrl { get; set; }
            public DateTime PublishedAt { get; set; }
        }

        public static async Task<UpdateInfo> CheckForUpdatesAsync(string owner = "yourusername", string repo = "wallet-recovery-tool")
        {
            try
            {
                // Skip remote check when defaults are still placeholders
                if (string.Equals(owner, "yourusername", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(owner) ||
                    string.IsNullOrWhiteSpace(repo))
                {
                    return new UpdateInfo
                    {
                        UpdateAvailable = false,
                        CurrentVersion = CURRENT_VERSION,
                        LatestVersion = CURRENT_VERSION
                    };
                }

                var url = GITHUB_API_URL.Replace("{OWNER}", owner).Replace("{REPO}", repo);
                var response = await _httpClient.GetStringAsync(url);
                
                using (var doc = JsonDocument.Parse(response))
                {
                    var root = doc.RootElement;
                    
                    string latestVersion = null;
                    string releaseNotes = null;
                    DateTime publishedAt = DateTime.MinValue;
                    
                    if (root.TryGetProperty("tag_name", out var tagName))
                        latestVersion = tagName.GetString()?.TrimStart('v');
                    if (root.TryGetProperty("body", out var body))
                        releaseNotes = body.GetString();
                    if (root.TryGetProperty("published_at", out var published))
                        publishedAt = published.GetDateTime();
                    
                    // Get download URL (first asset)
                    string downloadUrl = null;
                    if (root.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0)
                    {
                        if (assets[0].TryGetProperty("browser_download_url", out var dlUrl))
                            downloadUrl = dlUrl.GetString();
                    }

                    var updateAvailable = !string.IsNullOrEmpty(latestVersion) 
                        && CompareVersions(CURRENT_VERSION, latestVersion) < 0;

                    return new UpdateInfo
                    {
                        UpdateAvailable = updateAvailable,
                        LatestVersion = latestVersion ?? CURRENT_VERSION,
                        CurrentVersion = CURRENT_VERSION,
                        ReleaseNotes = releaseNotes,
                        DownloadUrl = downloadUrl ?? $"https://github.com/{owner}/{repo}/releases/latest",
                        PublishedAt = publishedAt
                    };
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"Update check failed: {ex.Message}");
                return new UpdateInfo
                {
                    UpdateAvailable = false,
                    CurrentVersion = CURRENT_VERSION,
                    LatestVersion = CURRENT_VERSION
                };
            }
        }

        private static int CompareVersions(string current, string latest)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(latest))
                return 0;

            try
            {
                // Strip pre-release suffixes (e.g., "2.0.0-beta.1" -> "2.0.0")
                var currentClean = current.Split('-')[0];
                var latestClean = latest.Split('-')[0];
                
                var currentParts = currentClean.Split('.');
                var latestParts = latestClean.Split('.');
                
                for (int i = 0; i < Math.Max(currentParts.Length, latestParts.Length); i++)
                {
                    int currentPart = i < currentParts.Length && int.TryParse(currentParts[i], out var cp) ? cp : 0;
                    int latestPart = i < latestParts.Length && int.TryParse(latestParts[i], out var lp) ? lp : 0;
                    
                    if (currentPart < latestPart) return -1;
                    if (currentPart > latestPart) return 1;
                }
                
                return 0;
            }
            catch
            {
                return 0; // If parsing fails, assume equal
            }
        }

        public static string GetCurrentVersion()
        {
            return CURRENT_VERSION;
        }
    }
}
