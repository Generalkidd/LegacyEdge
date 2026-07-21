using LegacyEdge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windows.Data.Json;
using Windows.Storage;

namespace LegacyEdge.Services
{
    internal static class BrowserDataStore
    {
        private static readonly ApplicationDataContainer Settings = ApplicationData.Current.LocalSettings;
        private static readonly object StorageSync = new object();

        public static string HomePage
        {
            get => ReadString("HomePage", "https://en.wikipedia.org/wiki/Main_Page");
            set => Settings.Values["HomePage"] = value;
        }

        public static string StartupPage
        {
            get => ReadString("StartupPage", HomePage);
            set => Settings.Values["StartupPage"] = value;
        }

        public static string StartupMode
        {
            get => ReadString("StartupMode", RestorePreviousSession ? "PreviousPages" : "SpecificPage");
            set => Settings.Values["StartupMode"] = value;
        }

        public static string SearchTemplate
        {
            get => ReadString("SearchTemplate", "https://www.bing.com/search?q={0}");
            set => Settings.Values["SearchTemplate"] = value;
        }

        public static string NewTabMode
        {
            get => ReadString("NewTabMode", "TopSitesAndSuggestedContent");
            set => Settings.Values["NewTabMode"] = value;
        }

        public static string AppTheme
        {
            get => ReadString("AppTheme", "Light");
            set => Settings.Values["AppTheme"] = value == "Dark" ? "Dark" : "Light";
        }

        public static bool ShowFavoritesBar
        {
            get => ReadBool("ShowFavoritesBar", false);
            set => Settings.Values["ShowFavoritesBar"] = value;
        }

        public static bool ShowHomeButton
        {
            get => ReadBool("ShowHomeButton", true);
            set => Settings.Values["ShowHomeButton"] = value;
        }

        public static void RecoverHomeButtonAfterChromeFix()
        {
            const string migrationKey = "ChromeStabilityHomeRecoveryV1";
            if (ReadBool(migrationKey, false)) return;
            // Earlier builds persisted the new value immediately before a
            // native XAML fail-fast, which could strand Home in the hidden
            // state. Restore the legacy default once; subsequent choices are
            // preserved normally.
            Settings.Values["ShowHomeButton"] = true;
            Settings.Values[migrationKey] = true;
        }

        public static bool RestorePreviousSession
        {
            get => ReadBool("RestorePreviousSession", true);
            set => Settings.Values["RestorePreviousSession"] = value;
        }

        public static bool ShowLegacySupportNotice
        {
            get => ReadBool("ShowLegacySupportNotice", true);
            set => Settings.Values["ShowLegacySupportNotice"] = value;
        }

        public static bool ShowSearchSuggestions
        {
            get => ReadBool("ShowSearchSuggestions", true);
            set => Settings.Values["ShowSearchSuggestions"] = value;
        }

        public static bool AskWhatToDoWithEachDownload
        {
            get => ReadBool("AskWhatToDoWithEachDownload", true);
            set => Settings.Values["AskWhatToDoWithEachDownload"] = value;
        }

        private static bool DownloadRecordsMigrated
        {
            get => ReadBool("DownloadRecordsMigrated", false);
            set => Settings.Values["DownloadRecordsMigrated"] = value;
        }

        public static List<BrowserItem> LoadFavorites() => LoadItems("Favorites");
        public static void SaveFavorites(IEnumerable<BrowserItem> items) => SaveItems("Favorites", items);
        public static List<BrowserItem> LoadReadingList() => LoadItems("ReadingList");
        public static void SaveReadingList(IEnumerable<BrowserItem> items) => SaveItems("ReadingList", items);
        public static List<BrowserItem> LoadHistory() => LoadItems("History");
        public static void SaveHistory(IEnumerable<BrowserItem> items) => SaveItems("History", items.Take(250));
        public static List<BrowserItem> LoadDownloads() => DownloadRecordsMigrated ? new List<BrowserItem>() : LoadItems("Downloads");
        public static void SaveDownloads(IEnumerable<BrowserItem> items) => SaveItems("Downloads", items.Take(100));
        public static void SaveDownloads(IEnumerable<DownloadItem> items) => SaveDownloadRecords(items);
        public static List<BrowserItem> LoadSetAsideTabs() => LoadItems("SetAsideTabs");
        public static void SaveSetAsideTabs(IEnumerable<BrowserItem> items) => SaveItems("SetAsideTabs", items.Take(100));

        public static List<BrowserItem> UpsertFavorite(BrowserItem value)
        {
            lock (StorageSync)
            {
                var items = LoadItems("Favorites");
                var existing = items.FirstOrDefault(item => string.Equals(item.Url, value?.Url, StringComparison.OrdinalIgnoreCase));
                if (existing == null && value != null) items.Insert(0, value);
                else if (existing != null && value != null)
                {
                    existing.Title = value.Title;
                    existing.IconUrl = value.IconUrl;
                    existing.Folder = value.Folder;
                }
                SaveItems("Favorites", items);
                return items;
            }
        }

        public static List<BrowserItem> RemoveFavorite(string url)
        {
            lock (StorageSync)
            {
                var items = LoadItems("Favorites");
                items.RemoveAll(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
                SaveItems("Favorites", items);
                return items;
            }
        }

        public static List<BrowserItem> SetReadingListItem(BrowserItem value, bool present)
        {
            lock (StorageSync)
            {
                var items = LoadItems("ReadingList");
                items.RemoveAll(item => string.Equals(item.Url, value?.Url, StringComparison.OrdinalIgnoreCase));
                if (present && value != null) items.Insert(0, value);
                SaveItems("ReadingList", items);
                return items;
            }
        }

        public static List<BrowserItem> RecordHistory(string title, string url, string iconUrl)
        {
            lock (StorageSync)
            {
                var items = LoadItems("History");
                if (items.Count > 0 && string.Equals(items[0].Url, url, StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.Now - items[0].Added < TimeSpan.FromMinutes(2))
                {
                    items[0].Title = title;
                    items[0].IconUrl = iconUrl;
                }
                else
                {
                    items.Insert(0, new BrowserItem { Title = title, Url = url, IconUrl = iconUrl, Added = DateTimeOffset.Now });
                }
                if (items.Count > 250) items.RemoveRange(250, items.Count - 250);
                SaveItems("History", items);
                return items;
            }
        }

        public static List<BrowserItem> RemoveHistoryUrls(IEnumerable<string> urls)
        {
            lock (StorageSync)
            {
                var keys = new HashSet<string>(urls ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var items = LoadItems("History");
                items.RemoveAll(item => keys.Contains(item.Url));
                SaveItems("History", items);
                return items;
            }
        }

        public static List<BrowserItem> ClearHistory()
        {
            lock (StorageSync)
            {
                var items = new List<BrowserItem>();
                SaveItems("History", items);
                return items;
            }
        }

        public static List<BrowserItem> AddSetAsideTabs(IEnumerable<BrowserItem> values)
        {
            lock (StorageSync)
            {
                var items = LoadItems("SetAsideTabs");
                items.InsertRange(0, (values ?? Enumerable.Empty<BrowserItem>()).ToList());
                if (items.Count > 100) items.RemoveRange(100, items.Count - 100);
                SaveItems("SetAsideTabs", items);
                return items;
            }
        }

        public static List<BrowserItem> RemoveSetAsideTab(BrowserItem value)
        {
            lock (StorageSync)
            {
                var items = LoadItems("SetAsideTabs");
                var added = value?.Added.ToUnixTimeSeconds() ?? long.MinValue;
                var match = items.FirstOrDefault(item => string.Equals(item.Url, value?.Url, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Title, value?.Title, StringComparison.Ordinal) && item.Added.ToUnixTimeSeconds() == added);
                if (match != null) items.Remove(match);
                SaveItems("SetAsideTabs", items);
                return items;
            }
        }

        public static List<BrowserItem> ClearSetAsideTabs()
        {
            lock (StorageSync)
            {
                var items = new List<BrowserItem>();
                SaveItems("SetAsideTabs", items);
                return items;
            }
        }

        public static List<DownloadItem> LoadDownloadRecords()
        {
            lock (StorageSync)
            {
                var result = ParseDownloadRecords(ReadJson("DownloadRecords"));

                // Import the BrowserItem-era download history exactly once. Clearing the
                // new history must not make these old rows reappear at the next launch.
                if (!DownloadRecordsMigrated)
                {
                    if (result.Count == 0)
                    {
                        foreach (var legacy in LoadItems("Downloads"))
                        {
                            result.Add(new DownloadItem
                            {
                                Id = Guid.NewGuid().ToString("D"),
                                Title = legacy.Title,
                                SourceUrl = legacy.Url,
                                FileToken = legacy.Token,
                                Added = legacy.Added,
                                CompletedAt = legacy.Added,
                                State = LegacyDownloadState.Completed
                            });
                        }
                    }

                    // Save first: if the process exits during migration, the old
                    // rows remain eligible for another attempt instead of being
                    // hidden by a prematurely committed migration marker.
                    SaveDownloadRecords(result);
                    RemoveJson("Downloads");
                }

                return NormalizeDownloadRecords(result);
            }
        }

        public static void SaveDownloadRecords(IEnumerable<DownloadItem> items)
        {
            var array = new JsonArray();
            foreach (var item in NormalizeDownloadRecords(items))
            {
                array.Add(new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue(item.Id ?? Guid.NewGuid().ToString("D")),
                    ["title"] = JsonValue.CreateStringValue(item.Title ?? "download"),
                    ["url"] = JsonValue.CreateStringValue(item.SourceUrl ?? ""),
                    ["token"] = JsonValue.CreateStringValue(item.FileToken ?? ""),
                    ["path"] = JsonValue.CreateStringValue(item.FilePath ?? ""),
                    ["operation"] = JsonValue.CreateStringValue(item.OperationId ?? ""),
                    ["state"] = JsonValue.CreateStringValue(item.State.ToString()),
                    ["received"] = JsonValue.CreateNumberValue(item.BytesReceived),
                    ["total"] = JsonValue.CreateNumberValue(item.TotalBytes),
                    ["error"] = JsonValue.CreateStringValue(item.ErrorMessage ?? ""),
                    ["added"] = JsonValue.CreateNumberValue(item.Added.ToUnixTimeSeconds()),
                    ["completed"] = JsonValue.CreateNumberValue(item.CompletedAt?.ToUnixTimeSeconds() ?? 0)
                });
            }
            WriteJson("DownloadRecords", array.Stringify());
            DownloadRecordsMigrated = true;
        }

        public static void UpsertDownloadRecord(DownloadItem record)
        {
            if (record == null) return;
            lock (StorageSync)
            {
                var items = LoadDownloadRecords();
                var existing = items.FirstOrDefault(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
                if (existing != null) items.Remove(existing);
                items.Insert(0, record);
                SaveDownloadRecords(items);
            }
        }

        public static void RemoveDownloadRecord(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            lock (StorageSync)
            {
                var items = LoadDownloadRecords();
                items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                SaveDownloadRecords(items);
            }
        }

        public static void ReplaceDownloadRecords(IEnumerable<DownloadItem> items)
            => SaveDownloadRecords(items ?? Enumerable.Empty<DownloadItem>());

        private static List<DownloadItem> ParseDownloadRecords(string json)
        {
            var result = new List<DownloadItem>();
            if (!JsonArray.TryParse(json, out JsonArray array)) return result;

            foreach (var value in array)
            {
                try
                {
                    var item = value.GetObject();
                    var stateName = item.GetNamedString("state", LegacyDownloadState.Completed.ToString());
                    if (!Enum.TryParse(stateName, out LegacyDownloadState state)) state = LegacyDownloadState.Failed;
                    var addedSeconds = (long)item.GetNamedNumber("added", 0);
                    var completedSeconds = (long)item.GetNamedNumber("completed", 0);
                    result.Add(new DownloadItem
                    {
                        Id = item.GetNamedString("id", Guid.NewGuid().ToString("D")),
                        Title = item.GetNamedString("title", "download"),
                        SourceUrl = item.GetNamedString("url", ""),
                        FileToken = item.GetNamedString("token", ""),
                        FilePath = item.GetNamedString("path", ""),
                        OperationId = item.GetNamedString("operation", ""),
                        State = state,
                        BytesReceived = (ulong)Math.Max(0, item.GetNamedNumber("received", 0)),
                        TotalBytes = (ulong)Math.Max(0, item.GetNamedNumber("total", 0)),
                        ErrorMessage = item.GetNamedString("error", ""),
                        Added = addedSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(addedSeconds) : DateTimeOffset.Now,
                        CompletedAt = completedSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(completedSeconds) : (DateTimeOffset?)null
                    });
                }
                catch { }
            }
            return result;
        }

        private static List<DownloadItem> NormalizeDownloadRecords(IEnumerable<DownloadItem> items)
        {
            var unique = (items ?? Enumerable.Empty<DownloadItem>())
                .Where(item => item != null)
                .GroupBy(item => item.Id ?? string.Empty, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.Added).First())
                .ToList();

            var active = unique.Where(item => item.IsActive);
            var terminal = unique.Where(item => item.IsTerminal)
                .OrderByDescending(item => item.CompletedAt ?? item.Added)
                .Take(100);
            return active.Concat(terminal)
                .OrderByDescending(item => item.Added)
                .ToList();
        }

        public static List<string> LoadSession()
        {
            var raw = ReadJson("Session");
            if (!JsonArray.TryParse(raw, out JsonArray array)) return new List<string>();
            return array.Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        }

        public static void SaveSession(IEnumerable<string> addresses)
        {
            var array = new JsonArray();
            foreach (var address in addresses.Where(value => !string.IsNullOrWhiteSpace(value)).Take(20))
                array.Add(JsonValue.CreateStringValue(address));
            WriteJson("Session", array.Stringify());
        }

        public static void ClearBrowsingData()
        {
            RemoveJson("History");
            RemoveJson("Session");
        }

        private static List<BrowserItem> LoadItems(string key)
        {
            var result = new List<BrowserItem>();
            if (!JsonArray.TryParse(ReadJson(key), out JsonArray array)) return result;

            foreach (var value in array)
            {
                try
                {
                    var item = value.GetObject();
                    result.Add(new BrowserItem
                    {
                        Title = item.GetNamedString("title", "Untitled"),
                        Url = item.GetNamedString("url", ""),
                        IconUrl = item.GetNamedString("icon", ""),
                        Token = item.GetNamedString("token", ""),
                        Folder = item.GetNamedString("folder", key == "Favorites" ? "FavoritesBar" : ""),
                        Added = DateTimeOffset.FromUnixTimeSeconds((long)item.GetNamedNumber("added", 0))
                    });
                }
                catch { }
            }
            return result;
        }

        private static void SaveItems(string key, IEnumerable<BrowserItem> items)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                var value = new JsonObject
                {
                    ["title"] = JsonValue.CreateStringValue(item.Title ?? "Untitled"),
                    ["url"] = JsonValue.CreateStringValue(item.Url ?? ""),
                    ["icon"] = JsonValue.CreateStringValue(item.IconUrl ?? ""),
                    ["token"] = JsonValue.CreateStringValue(item.Token ?? ""),
                    ["folder"] = JsonValue.CreateStringValue(item.Folder ?? ""),
                    ["added"] = JsonValue.CreateNumberValue(item.Added.ToUnixTimeSeconds())
                };
                array.Add(value);
            }
            WriteJson(key, array.Stringify());
        }

        private static string JsonPath(string key) => Path.Combine(ApplicationData.Current.LocalFolder.Path, key + ".json");

        private static string ReadJson(string key)
        {
            lock (StorageSync)
            {
                try
                {
                    var path = JsonPath(key);
                    if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);

                    foreach (var recoveryPath in new[] { path + ".bak", path + ".tmp" })
                    {
                        if (!File.Exists(recoveryPath)) continue;
                        var recovered = File.ReadAllText(recoveryPath, Encoding.UTF8);
                        File.Copy(recoveryPath, path, true);
                        return recovered;
                    }

                    var legacy = ReadString(key, "");
                    if (!string.IsNullOrWhiteSpace(legacy))
                    {
                        WriteJsonCore(key, legacy);
                        Settings.Values.Remove(key);
                    }
                    return legacy;
                }
                catch { return string.Empty; }
            }
        }

        private static void WriteJson(string key, string json)
        {
            lock (StorageSync)
            {
                try
                {
                    WriteJsonCore(key, json ?? string.Empty);
                    Settings.Values.Remove(key);
                }
                catch
                {
                    // LocalSettings has a small per-value limit; only use it as a safe fallback for short data.
                    try
                    {
                        var fallback = json ?? string.Empty;
                        if (Encoding.UTF8.GetByteCount(fallback) < 6000) Settings.Values[key] = fallback;
                    }
                    catch { }
                }
            }
        }

        private static void WriteJsonCore(string key, string json)
        {
            var path = JsonPath(key);
            var temporaryPath = path + ".tmp";
            var backupPath = path + ".bak";
            File.WriteAllText(temporaryPath, json ?? string.Empty, Encoding.UTF8);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporaryPath, path, backupPath, true);
                }
                catch
                {
                    File.Copy(path, backupPath, true);
                    File.Copy(temporaryPath, path, true);
                    File.Delete(temporaryPath);
                }
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }

        private static void RemoveJson(string key)
        {
            lock (StorageSync)
            {
                try
                {
                    var path = JsonPath(key);
                    var temporaryPath = path + ".tmp";
                    var backupPath = path + ".bak";
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    Settings.Values.Remove(key);
                }
                catch { }
            }
        }

        private static string ReadString(string key, string fallback) => Settings.Values.TryGetValue(key, out object value) ? value as string ?? fallback : fallback;
        private static bool ReadBool(string key, bool fallback) => Settings.Values.TryGetValue(key, out object value) && value is bool enabled ? enabled : fallback;
    }
}
