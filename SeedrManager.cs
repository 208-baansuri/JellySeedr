using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seedrcc;

namespace JellySeedr.Api;

public class SeedrManager
{
    public static readonly SeedrManager Instance = new();

    private static readonly string[] MediaExtensions =
    [
        "webm", "mkv", "flv", "vob", "ogv", "ogg", "rrc", "gifv", "mng", "mov",
        "avi", "qt", "wmv", "yuv", "rm", "asf", "amv", "mp4", "m4p", "m4v",
        "mpg", "mp2", "mpeg", "mpe", "mpv", "svi", "3gp", "3g2", "mxf", "roq",
        "nsv", "f4v", "f4p", "f4a", "f4b", "mod"
    ];

    public ILogger? _logger;
    public SeedrClient? Client { get; set; }
    private IHttpClientFactory? _httpClientFactory;

    // Manual-download task tracking (File Browser panel)
    private readonly Dictionary<uint, JellySeedrTask> _activeTasks = new();
    private uint _taskIdCounter = 0;

    // Torrent queue (Mock Transmission / Radarr/Sonarr panel)
    private readonly List<QueuedTorrent> _torrentQueue = new();
    private readonly object _queueLock = new();
    private bool _queueProcessorRunning = false;
    private SeedrClient? _queueClient;
    private uint _queueIdCounter = 0;

    // Torrents that failed tracking after 5 consecutive poll errors.
    // On the next torrent-add the active torrent (by id) or completed folder (by name)
    // will be cleaned up from Seedr before the new torrent is submitted.
    private readonly List<PendingDelete> _pendingDeletes = new();
    private readonly object _pendingDeletesLock = new();

    // -------------------------------------------------------------------------
    // Client initialisation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the existing client if available; otherwise re-initialises from a
    /// stored encrypted token or saved credentials.
    /// </summary>
    public async Task<SeedrClient?> EnsureClientAsync(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        if (Client != null) return Client;

        var tokenStr = Plugin.Instance!.GetSeedrToken();
        if (!string.IsNullOrEmpty(tokenStr))
        {
            try
            {
                var token = Token.FromBase64(tokenStr);
                Client = new SeedrClient(token, t => Plugin.Instance!.SaveSeedrToken(t.ToBase64()), httpClientFactory.CreateClient());
                return Client;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "EnsureClientAsync: Stored token failed; trying saved credentials.");
            }
        }

        var creds = Plugin.Instance!.LoadCredentials();
        if (creds != null)
        {
            try
            {
                Client = await SeedrClient.FromPasswordAsync(
                    creds.Value.username, creds.Value.password,
                    t => Plugin.Instance!.SaveSeedrToken(t.ToBase64()),
                    httpClientFactory.CreateClient());
                return Client;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "EnsureClientAsync: Login with saved credentials failed.");
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Seedr file operations (File Browser)
    // -------------------------------------------------------------------------

    public async Task<(int code, string message)> DeleteSelection(SeedrClient client, SeedrSelectionRequest request)
    {
        try
        {
            var items = NormalizeSelection(request);
            int files = 0, folders = 0, torrents = 0;
            var tasks = new List<Task>();

            foreach (var item in items)
            {
                if (IsTorrent(item.Kind)) { tasks.Add(client.DeleteTorrentAsync(item.Id)); torrents++; }
                else if (IsFolder(item.Kind)) { tasks.Add(client.DeleteFolderAsync(item.Id)); folders++; }
                else { tasks.Add(client.DeleteFileAsync(item.Id)); files++; }
            }

            await Task.WhenAll(tasks);

            var parts = new List<string>();
            if (torrents > 0) parts.Add($"{torrents} torrent(s)");
            if (files > 0) parts.Add($"{files} file(s)");
            if (folders > 0) parts.Add($"{folders} folder(s)");
            return (200, $"Deleted {string.Join(", ", parts)}.");
        }
        catch (Exception ex) { return (400, ex.Message); }
    }

    public async Task<(int code, string message)> FetchFiles(
        SeedrClient client, SeedrSelectionRequest request, FetchNameClashResolution clashResolution,
        List<Task>? tasksCollection = null, List<JellySeedrTask>? jellySeedrTaskCollection = null,
        QueuedTorrent? queueItem = null)
    {
        try
        {
            var items = NormalizeSelection(request);
            int fetchedFiles = 0;

            foreach (var item in items)
            {
                if (IsTorrent(item.Kind) || IsFolder(item.Kind)) continue;

                string sourceUrl = string.Empty;
                try
                {
                    var fileUrl = await client.FetchFileAsync(item.Id);
                    sourceUrl = fileUrl.Url ?? string.Empty;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to create download link for '{Name}'; skipping.", item.Name);
                }

                var fetchTask = new JellySeedrFetchTask
                {
                    SourceUrl = sourceUrl,
                    SourceFileId = item.Id,
                    SourceFileName = item.Name,
                    SourceFilePath = item.Path,
                    DestinationPath = Path.Combine(request.DestinationPath, item.Name),
                    FileSize = item.Size
                };

                var newTask = CreateTask(JellySeedrTaskType.Fetch, fetchTask);
                if (queueItem == null)
                {
                    // Manual fetches are shown in the Task Status panel.
                    lock (_activeTasks) { _activeTasks[newTask.Id] = newTask; }
                }
                jellySeedrTaskCollection?.Add(newTask);
                tasksCollection?.Add(FetchFileAsync(newTask, fetchTask, clashResolution, queueItem));
                fetchedFiles++;
            }

            return (200, $"Started downloading {fetchedFiles} file(s).");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in FetchFiles.");
            return (400, ex.Message);
        }
    }

    private async Task FetchFileAsync(JellySeedrTask task, JellySeedrFetchTask fetchTask,
        FetchNameClashResolution clashResolution = FetchNameClashResolution.Rename,
        QueuedTorrent? queueItem = null)
    {
        if (string.IsNullOrWhiteSpace(fetchTask.SourceUrl))
        {
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = "Invalid source URL.";
            task.UpdatedAt = DateTime.UtcNow;
            return;
        }

        try
        {
            if (System.IO.File.Exists(fetchTask.DestinationPath))
            {
                if (clashResolution == FetchNameClashResolution.Skip)
                {
                    task.Status = JellySeedrTaskStatus.Completed;
                    task.UpdatedAt = DateTime.UtcNow;
                    if (queueItem != null) queueItem.FetchCopiedBytes += fetchTask.FileSize;
                    return;
                }
                else if (clashResolution == FetchNameClashResolution.Rename)
                {
                    fetchTask.DestinationPath = GetUniqueFilePath(fetchTask.DestinationPath, fetchTask.SourceFileName);
                }
            }

            task.Status = JellySeedrTaskStatus.InProgress;
            DownloadFile(task, fetchTask, queueItem);
        }
        catch (Exception ex)
        {
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error checking destination file: {ex.Message}";
            task.UpdatedAt = DateTime.UtcNow;
        }
    }

    private void DownloadFile(JellySeedrTask task, JellySeedrFetchTask fetchTask, QueuedTorrent? queueItem = null)
    {
        try
        {
            using var httpClient = new HttpClient();
            using var response = httpClient.GetAsync(fetchTask.SourceUrl, HttpCompletionOption.ResponseHeadersRead).Result;
            response.EnsureSuccessStatusCode();

            var destinationDir = Path.GetDirectoryName(fetchTask.DestinationPath);
            if (!string.IsNullOrEmpty(destinationDir)) Directory.CreateDirectory(destinationDir);

            using var stream = response.Content.ReadAsStreamAsync().Result;
            using var fileStream = new FileStream(fetchTask.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; // 80 KB buffer
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                fileStream.Write(buffer, 0, bytesRead);
                fetchTask.BytesCopied += bytesRead;
                if (queueItem != null) queueItem.FetchCopiedBytes += bytesRead;
                task.UpdatedAt = DateTime.UtcNow;
            }

            task.Status = JellySeedrTaskStatus.Completed;
            task.UpdatedAt = DateTime.UtcNow;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger?.LogWarning("File not found on Seedr (404) for task {TaskId}.", task.Id);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = "404 Not Found";
            task.UpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error downloading file for task {TaskId}.", task.Id);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error downloading file: {ex.Message}";
            task.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task<SeedrFolderDto> LoadFolderNodeAsync(SeedrClient client, string folderId,
        string currentFolderName = "", string currentFolderPath = "")
    {
        var listing = await client.ListContentsAsync(folderId);
        var folderObject = new SeedrFolderDto
        {
            size = listing.Size,
            id = listing.Id.ToString() ?? folderId,
            parentId = listing.Parent.ToString() ?? string.Empty,
            name = currentFolderName,
            path = currentFolderPath
        };

        if (listing.Torrents != null)
        {
            foreach (var t in listing.Torrents)
                folderObject.torrents.Add(new SeedrTorrentDto { id = t.Id.ToString(), name = t.Name ?? string.Empty, size = t.Size, progress = t.Progress });
        }

        foreach (var f in listing.Files)
            folderObject.files.Add(new SeedrFileDto
            {
                id = f.FolderFileId.ToString(),
                folderId = folderObject.id,
                name = f.Name ?? string.Empty,
                size = f.Size,
                hash = f.Hash ?? string.Empty,
                path = currentFolderPath + "/" + (f.Name ?? string.Empty)
            });

        foreach (var child in listing.Folders)
        {
            var childName = child.Name.Remove(0, currentFolderPath.Length).TrimStart('/');
            folderObject.children.Add(await LoadFolderNodeAsync(client, child.Id.ToString(), childName, child.Name));
        }

        return folderObject;
    }

    // -------------------------------------------------------------------------
    // Torrent pipeline
    // -------------------------------------------------------------------------

    public async Task<(int code, string message)> HandleTorrentTask(SeedrClient client, SeedrTorrentAddParam param, QueuedTorrent queueItem)
    {
        try
        {
            if (queueItem.Status == QueuedTorrentStatus.Cancelled)
                return (200, "Torrent was cancelled before it started.");

            // Clean up any stale torrents/folders left behind by previously failed
            // tracking loops before submitting the new torrent to Seedr.
            await CleanupPendingDeletesAsync(client);

            queueItem.Stage = QueuedTorrentStage.Torrenting;

            AddTorrentResult? result;
            try
            {
                result = param.InputType switch
                {
                    SeedrInputType.TorrentFile => await client.AddTorrentAsync(torrentBytes: param.TorrentBytes),
                    SeedrInputType.TorrentUrl  => await client.AddTorrentAsync(torrentFile: param.Source),
                    SeedrInputType.MagnetLink  => await client.AddTorrentAsync(magnetLink: param.Source),
                    _                          => null
                };
            }
            catch (NotEnoughStorageException) when (Plugin.Instance?.Configuration?.ClearSeedrOnStorageFull ?? true)
            {
                _logger?.LogWarning("Seedr storage full while adding torrent '{Source}'. Attempting to free up space and retry.", param.Source);
                var freed = await FreeUpStorageAsync(client);
                if (!freed)
                {
                    _logger?.LogWarning("FreeUpStorage found nothing to delete; cannot recover from storage full.");
                    return (500, "Not enough storage on Seedr and no deletable items found to free up space.");
                }

                // Retry once after clearing storage.
                result = param.InputType switch
                {
                    SeedrInputType.TorrentFile => await client.AddTorrentAsync(torrentBytes: param.TorrentBytes),
                    SeedrInputType.TorrentUrl  => await client.AddTorrentAsync(torrentFile: param.Source),
                    SeedrInputType.MagnetLink  => await client.AddTorrentAsync(magnetLink: param.Source),
                    _                          => null
                };
            }

            if (result == null || !result.Result)
            {
                _logger?.LogWarning("Failed to add torrent to Seedr. Source: '{Source}', Type: {Type}", param.Source, param.InputType);
                return (500, "Error adding torrent: Make sure url/magnet is valid and you have enough storage space. Also for free account only one concurrent torrent download is allowed.");
            }

            queueItem.SeedrTorrentId = result.UserTorrentId ?? 0;

            var torrentTask = new JellySeedrTorrentTask
            {
                TorrentId = result.UserTorrentId ?? 0,
                TorrentName = result.Title ?? string.Empty,
                TotalSize = -1
            };

            var newTask = CreateTask(JellySeedrTaskType.Torrent, torrentTask);
            newTask.Status = JellySeedrTaskStatus.InProgress;

            await HandleTorrentCompletion(client, newTask, param, queueItem);

            // Sync the queue item status from the task outcome if the pipeline didn't set it.
            if (queueItem.Status == QueuedTorrentStatus.Active)
            {
                queueItem.Status = newTask.Status switch
                {
                    JellySeedrTaskStatus.Completed => QueuedTorrentStatus.Completed,
                    JellySeedrTaskStatus.Cancelled => QueuedTorrentStatus.Cancelled,
                    _ => QueuedTorrentStatus.Failed
                };
                queueItem.ErrorMessage ??= newTask.ErrorMessage;
            }

            return (200, $"Torrent added to Seedr: '{result.Title}'.");
        }
        catch (AuthenticationException ex)
        {
            _logger?.LogError(ex, "Seedr authentication failed for source '{Source}'.", param.Source);
            return (401, "Seedr session expired or invalid. Please log out and log in again.");
        }
        catch (ApiException ex)
        {
            _logger?.LogError(ex, "Seedr API rejected torrent from source '{Source}'.", param.Source);
            return (500, DescribeSeedrApiError(ex));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in HandleTorrentTask for source '{Source}'.", param.Source);
            return (500, $"Error adding torrent: {ex.Message}");
        }
    }

    private async Task HandleTorrentCompletion(SeedrClient client, JellySeedrTask task, SeedrTorrentAddParam param, QueuedTorrent queueItem)
    {
        var torrentTask = task.TorrentTask;
        if (torrentTask == null)
        {
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = "Torrent task is null.";
            return;
        }

        try
        {
            Folder? seedrFolder = null;
            int missingPolls = 0;
            int consecutiveFailures = 0;

            while (task.Status == JellySeedrTaskStatus.InProgress)
            {
                if (queueItem.Status == QueuedTorrentStatus.Cancelled)
                {
                    task.Status = JellySeedrTaskStatus.Cancelled;
                    task.ErrorMessage = "Cancelled by user.";
                    return;
                }

                try
                {
                    var content = await client.ListContentsAsync();
                    var activeTorrent = content.Torrents.FirstOrDefault(x => x.Id == torrentTask.TorrentId);

                    if (activeTorrent == null)
                    {
                        seedrFolder = content.Folders.FirstOrDefault(x => x.Name.Trim() == torrentTask.TorrentName.Trim());
                        if (seedrFolder != null)
                        {
                            torrentTask.Progress = 100;
                            queueItem.TorrentProgress = 100;
                            task.Status = JellySeedrTaskStatus.Completed;
                            break;
                        }

                        // The folder may appear a few polls after the torrent leaves the transfer list.
                        if (++missingPolls >= 3)
                        {
                            var msg = $"Torrent no longer in Seedr and no completed folder found (name='{torrentTask.TorrentName}').";
                            _logger?.LogWarning(msg);
                            task.Status = JellySeedrTaskStatus.Cancelled;
                            task.ErrorMessage = msg;
                            queueItem.Status = QueuedTorrentStatus.Cancelled;
                            queueItem.ErrorMessage = msg;
                            _ = DeleteFromArrAsync(queueItem, false);
                            return;
                        }
                    }
                    else
                    {
                        missingPolls = 0;
                        consecutiveFailures = 0;
                        if (torrentTask.TotalSize == -1) torrentTask.TotalSize = activeTorrent.Size;
                        torrentTask.Progress = activeTorrent.Progress;
                        queueItem.TorrentProgress = activeTorrent.Progress;
                        queueItem.TorrentTotalBytes = activeTorrent.Size;
                        task.UpdatedAt = DateTime.Now;
                    }
                }
                catch (Exception ex)
                {
                    // Count consecutive poll failures regardless of exception type.
                    // Network blips are expected and skipped; after 5 in a row we give up
                    // and schedule the stale torrent/folder for cleanup on the next add.
                    consecutiveFailures++;
                    var isNetwork = ex is NetworkException;
                    _logger?.LogWarning(
                        "Seedr poll {ExType} for task {TaskId} ({Message}); consecutive failures: {Count}/5.",
                        isNetwork ? "network error" : "error", task.Id, ex.Message, consecutiveFailures);

                    if (consecutiveFailures >= 5)
                    {
                        var msg = $"Torrent tracking aborted after 5 consecutive poll failures (name='{torrentTask.TorrentName}', id={torrentTask.TorrentId}).";
                        _logger?.LogError(msg);
                        task.Status = JellySeedrTaskStatus.Failed;
                        task.ErrorMessage = msg;
                        queueItem.Status = QueuedTorrentStatus.Failed;
                        queueItem.ErrorMessage = msg;

                        lock (_pendingDeletesLock)
                            _pendingDeletes.Add(new PendingDelete(torrentTask.TorrentId, torrentTask.TorrentName.Trim()));

                        _ = DeleteFromArrAsync(queueItem, false);
                        return;
                    }
                }

                await Task.Delay(2000);
            }

            if (seedrFolder != null && task.Status == JellySeedrTaskStatus.Completed
                && queueItem.Status != QueuedTorrentStatus.Cancelled
                && (Plugin.Instance?.Configuration?.AutoDownload ?? true))
            {
                await ScanAndFetchMatchingFilesAsync(client, seedrFolder, param, queueItem);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in HandleTorrentCompletion for task {TaskId}.", task.Id);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error during monitoring: {ex.Message}";
            queueItem.Status = QueuedTorrentStatus.Failed;
            queueItem.ErrorMessage = task.ErrorMessage;
            _ = DeleteFromArrAsync(queueItem, false);
        }
    }

    private async Task ScanAndFetchMatchingFilesAsync(SeedrClient client, Folder seedrFolder, SeedrTorrentAddParam param, QueuedTorrent queueItem)
    {
        queueItem.Stage = QueuedTorrentStage.Fetching;

        // Files are placed under DisplayName (the parsed torrent name) so Sonarr/Radarr
        // can locate them at the expected path regardless of seedrFolder.Name.
        var destinationBase = param.UseSubfolderStructure
            ? Path.Combine(param.DestinationPath, queueItem.DisplayName)
            : param.DestinationPath;

        var selectionRequest = new SeedrSelectionRequest { DestinationPath = destinationBase };
        var stack = new Stack<string>();
        stack.Push(seedrFolder.Id.ToString());

        while (stack.Count > 0)
        {
            var listing = await client.ListContentsAsync(stack.Pop());
            if (listing == null) continue;

            foreach (var file in listing.Files ?? [])
            {
                var name = file.Name ?? string.Empty;
                var ext = Path.GetExtension(name) is { Length: > 1 } e ? e[1..] : string.Empty;
                if (!param.DownloadAll && !param.DownloadExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                selectionRequest.Items.Add(new SeedrSelectionItem
                {
                    Id = file.FolderFileId.ToString(),
                    Kind = "file",
                    Name = name,
                    Size = file.Size,
                    Path = listing.Fullname + "/" + name
                });
            }

            foreach (var child in listing.Folders ?? [])
                stack.Push(child.Id.ToString());
        }

        queueItem.FetchTotalBytes = selectionRequest.Items.Sum(i => i.Size);
        queueItem.FetchCopiedBytes = 0;

        if (selectionRequest.Items.Count == 0)
        {
            var reason = param.DownloadAll
                ? "No files found in the Seedr folder."
                : "No files matched the allowed download extensions. The folder was kept on Seedr.";
            _logger?.LogWarning("No downloadable files in '{Folder}': {Reason}", seedrFolder.Name, reason);
            queueItem.ErrorMessage = reason;
            return;
        }

        List<JellySeedrTask> jellySeedrTasks = [];
        List<Task> fetchTasks = [];
        await FetchFiles(client, selectionRequest, param.ClashResolution, fetchTasks, jellySeedrTasks, queueItem);
        await Task.WhenAll(fetchTasks);

        var failedTasks = jellySeedrTasks.Where(t => t.Status != JellySeedrTaskStatus.Completed).ToList();

        if (failedTasks.Count > 0)
        {
            var failedNames = failedTasks.Select(t => t.FetchTask?.SourceFileName ?? "unknown").ToList();
            var shownNames = string.Join(", ", failedNames.Take(3).Select(n => $"'{n}'"));
            if (failedNames.Count > 3) shownNames += $" and {failedNames.Count - 3} more";

            var failed404MediaFiles = failedTasks
                .Where(t => t.ErrorMessage == "404 Not Found" &&
                            t.FetchTask != null &&
                            MediaExtensions.Contains(Path.GetExtension(t.FetchTask.SourceFileName).TrimStart('.').ToLowerInvariant()))
                .ToList();

            if (failed404MediaFiles.Count > 0 && (queueItem.AddedBy == "radarr" || queueItem.AddedBy == "sonarr"))
            {
                var mediaNames = string.Join(", ", failed404MediaFiles.Select(t => $"'{t.FetchTask?.SourceFileName}'"));
                queueItem.ErrorMessage = $"Failed to download media file(s) due to 404 Not Found: {mediaNames}. Triggering Arr blacklist.";
                _logger?.LogWarning("Queue {QueueId}: 404 Not Found for media files ({Files}). Triggering blacklist.",
                    queueItem.QueueId, mediaNames);
                queueItem.Status = QueuedTorrentStatus.Failed;
                _ = DeleteFromArrAsync(queueItem, true);
                return;
            }

            if (failedTasks.Any(t => t.ErrorMessage == "404 Not Found"))
            {
                // 404 means the file never existed; skip and continue to cleanup.
                queueItem.ErrorMessage = $"Skipped {failedTasks.Count} file(s) due to 404 Not Found — {shownNames}.";
                _logger?.LogWarning("Queue {QueueId}: {Count} file(s) returned 404, continuing cleanup ({Files}).",
                    queueItem.QueueId, failedTasks.Count, string.Join(", ", failedNames));
            }
            else
            {
                queueItem.ErrorMessage = $"Failed to download {failedTasks.Count} file(s) — {shownNames}. The folder was kept on Seedr.";
                _logger?.LogWarning("Queue {QueueId}: {Count}/{Total} file(s) failed, aborting cleanup ({Files}).",
                    queueItem.QueueId, failedTasks.Count, jellySeedrTasks.Count, string.Join(", ", failedNames));
                queueItem.Status = QueuedTorrentStatus.Failed;
                _ = DeleteFromArrAsync(queueItem, false);
                return;
            }
        }

        if (Plugin.Instance?.Configuration?.DeleteAfterDownload ?? true)
        {
            queueItem.Stage = QueuedTorrentStage.CleaningUp;
            var deleteTask = CreateTask(JellySeedrTaskType.Delete, new JellySeedrDeleteTask
            {
                Id = seedrFolder.Id.ToString(),
                Path = seedrFolder.Fullname,
                Kind = "folder"
            });
            deleteTask.Status = JellySeedrTaskStatus.Pending;
            await HandleDeleteTask(client, deleteTask);
        }
    }

    /// <summary>
    /// Deletes all folders and active torrents from Seedr that are not currently being
    /// fetched by any in-progress task, freeing up storage for a new torrent.
    /// Returns true if at least one item was deleted.
    /// </summary>
    private async Task<bool> FreeUpStorageAsync(SeedrClient client)
    {
        var content = await client.ListContentsAsync();

        // Collect folder names that are actively being fetched so we don't delete them.
        var protectedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<JellySeedrTask> activeFetchTasks;
        lock (_activeTasks)
            activeFetchTasks = _activeTasks.Values
                .Where(t => t.Type == JellySeedrTaskType.Fetch && t.Status == JellySeedrTaskStatus.InProgress)
                .ToList();

        foreach (var fetchTask in activeFetchTasks)
        {
            var path = fetchTask.FetchTask?.SourceFilePath;
            if (string.IsNullOrEmpty(path)) continue;
            // SourceFilePath is "FolderName/FileName" — the first segment is the folder name.
            var folderName = path.Split('/', '\\')[0].Trim();
            if (!string.IsNullOrEmpty(folderName))
                protectedFolderNames.Add(folderName);
        }

        // Also protect folders belonging to any queue item currently in the Fetching stage.
        lock (_queueLock)
        {
            foreach (var q in _torrentQueue.Where(q => q.Stage == QueuedTorrentStage.Fetching))
                protectedFolderNames.Add(q.DisplayName.Trim());
        }

        var deleteTasks = new List<Task>();
        int deleted = 0;

        foreach (var file in content.Files ?? [])
        {
            _logger?.LogInformation("FreeUpStorage: deleting loose file id={Id} name='{Name}'.", file.FolderFileId, file.Name);
            deleteTasks.Add(client.DeleteFileAsync(file.FolderFileId.ToString()));
            deleted++;
        }

        foreach (var file in content.Files ?? [])
        {
            _logger?.LogInformation("FreeUpStorage: deleting loose file id={Id} name='{Name}'.", file.FolderFileId, file.Name);
            deleteTasks.Add(client.DeleteFileAsync(file.FolderFileId.ToString()));
            deleted++;
        }

        if (deleteTasks.Count > 0)
            await Task.WhenAll(deleteTasks);

        return deleted > 0;
    }

    public async Task<(int code, string message)> HandleDeleteTask(SeedrClient client, JellySeedrTask deleteTask)
    {
        if (deleteTask.Type != JellySeedrTaskType.Delete) return (400, "Invalid task type");
        if (deleteTask.Status != JellySeedrTaskStatus.Pending) return (400, "Invalid task status");

        var task = deleteTask.DeleteTask;
        if (task == null || string.IsNullOrEmpty(task.Id)) return (400, "Invalid task");

        try
        {
            deleteTask.Status = JellySeedrTaskStatus.InProgress;
            var res = IsFolder(task.Kind)
                ? await client.DeleteFolderAsync(task.Id)
                : await client.DeleteFileAsync(task.Id);

            if (!res.Result)
            {
                _logger?.LogWarning("Seedr delete failed (code={Code}).", res.Code);
                return (400, res.Code?.ToString() ?? "Error deleting task");
            }

            deleteTask.Status = JellySeedrTaskStatus.Completed;
            return (200, "Task completed successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting task {TaskId} in Seedr.", deleteTask.Id);
            deleteTask.ErrorMessage = ex.Message;
            deleteTask.Status = JellySeedrTaskStatus.Failed;
            return (500, $"Error deleting task: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Active tasks (File Browser / manual downloads)
    // -------------------------------------------------------------------------

    public IReadOnlyCollection<JellySeedrTask> GetActiveTasks()
    {
        lock (_activeTasks) { return _activeTasks.Values.OrderBy(t => t.Id).ToList(); }
    }

    public bool RemoveTask(uint taskId)
    {
        lock (_activeTasks) { return _activeTasks.Remove(taskId); }
    }

    public void ClearCompletedTasks()
    {
        lock (_activeTasks)
        {
            var done = _activeTasks
                .Where(kvp => kvp.Value.Status is JellySeedrTaskStatus.Completed or JellySeedrTaskStatus.Failed or JellySeedrTaskStatus.Cancelled)
                .Select(kvp => kvp.Key).ToList();
            foreach (var k in done) _activeTasks.Remove(k);
        }
    }

    public async Task<bool> CancelTaskAsync(SeedrClient? client, uint taskId)
    {
        JellySeedrTask? task;
        lock (_activeTasks) { _activeTasks.TryGetValue(taskId, out task); }

        if (task == null || task.Status != JellySeedrTaskStatus.InProgress || task.Type != JellySeedrTaskType.Torrent)
            return false;

        if (task.TorrentTask != null && client != null)
        {
            try
            {
                var result = await client.DeleteTorrentAsync(task.TorrentTask.TorrentId.ToString());
                if (result == null || !result.Result) return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error deleting torrent {TorrentId} from Seedr.", task.TorrentTask.TorrentId);
                return false;
            }
        }

        task.Status = JellySeedrTaskStatus.Cancelled;
        task.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    // -------------------------------------------------------------------------
    // Torrent queue
    // -------------------------------------------------------------------------

    public (int position, uint queueId, string message) EnqueueTorrent(SeedrClient client, SeedrTorrentAddParam param, string displayName, string addedBy = "", string hashString = "")
    {
        _queueClient = client;
        var queueId = Interlocked.Increment(ref _queueIdCounter);
        var item = new QueuedTorrent
        {
            QueueId = queueId,
            Param = param,
            DisplayName = displayName,
            QueuedAt = DateTime.UtcNow,
            AddedBy = addedBy,
            HashString = hashString,
            Status = QueuedTorrentStatus.Queued
        };

        int position;
        lock (_queueLock)
        {
            _torrentQueue.Add(item);
            position = _torrentQueue.Count(q => q.Status is QueuedTorrentStatus.Queued or QueuedTorrentStatus.Active);
        }

        EnsureQueueProcessorRunning();

        return position == 1
            ? (position, queueId, $"Torrent '{displayName}' is being processed.")
            : (position, queueId, $"Torrent '{displayName}' queued at position #{position}.");
    }

    public IReadOnlyList<QueuedTorrent> GetQueue()
    {
        lock (_queueLock) { return _torrentQueue.ToList(); }
    }

    public async Task<bool> CancelQueueItemAsync(uint queueId)
    {
        QueuedTorrent? item;
        lock (_queueLock)
        {
            item = _torrentQueue.FirstOrDefault(q => q.QueueId == queueId);
            if (item == null) return false;
            if (item.Status == QueuedTorrentStatus.Queued)
            {
                item.Status = QueuedTorrentStatus.Cancelled;
                _ = DeleteFromArrAsync(item, false);
                return true;
            }
        }

        // Active items can be cancelled only while still torrenting.
        if (item.Status == QueuedTorrentStatus.Active &&
            item.Stage is QueuedTorrentStage.Waiting or QueuedTorrentStage.Torrenting)
        {
            item.Status = QueuedTorrentStatus.Cancelled;
            _ = DeleteFromArrAsync(item, false);
            if (item.SeedrTorrentId > 0 && _queueClient != null)
            {
                try { await _queueClient.DeleteTorrentAsync(item.SeedrTorrentId.ToString()); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete cancelled torrent {Id} from Seedr.", item.SeedrTorrentId); }
            }
            return true;
        }

        return false;
    }

    private async Task DeleteFromArrAsync(QueuedTorrent item, bool blacklist)
    {
        if (string.IsNullOrEmpty(item.AddedBy) || _httpClientFactory == null) return;

        var config = Plugin.Instance!.Configuration;
        string url = string.Empty;
        string apiKey = string.Empty;
        bool autoDelete = false;

        if (item.AddedBy == "radarr")
        {
            url = config.RadarrUrl;
            apiKey = config.RadarrApiKey;
            autoDelete = config.AutoDeleteFailedRadarrDownloads;
        }
        else if (item.AddedBy == "sonarr")
        {
            url = config.SonarrUrl;
            apiKey = config.SonarrApiKey;
            autoDelete = config.AutoDeleteFailedSonarrDownloads;
        }

        if (!autoDelete || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey)) return;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // 1. Get queue to find ID
            var queueReqUrl = url.TrimEnd('/') + "/api/v3/queue";
            var queueReq = new HttpRequestMessage(HttpMethod.Get, queueReqUrl);
            queueReq.Headers.Add("X-Api-Key", apiKey.Trim());

            var queueRes = await client.SendAsync(queueReq);
            if (!queueRes.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to fetch queue from {AddedBy}. Status: {Status}", item.AddedBy, queueRes.StatusCode);
                return;
            }

            var queueJson = await queueRes.Content.ReadAsStringAsync();
            using var queueData = System.Text.Json.JsonDocument.Parse(queueJson);
            var records = queueData.RootElement.GetProperty("records").EnumerateArray();
            int arrId = -1;

            foreach (var record in records)
            {
                if (record.TryGetProperty("downloadId", out var dlIdProp) &&
                    dlIdProp.GetString()?.Equals(item.HashString, StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (record.TryGetProperty("id", out var idProp))
                    {
                        arrId = idProp.GetInt32();
                        break;
                    }
                }
            }

            if (arrId == -1)
            {
                _logger?.LogDebug("Torrent {Hash} not found in {AddedBy} queue. It might have already been removed.", item.HashString, item.AddedBy);
                return;
            }

            // 2. Delete item
            var deleteReqUrl = $"{url.TrimEnd('/')}/api/v3/queue/{arrId}?removeFromClient=true&blocklist={blacklist.ToString().ToLowerInvariant()}&skipRedownload=false";
            var deleteReq = new HttpRequestMessage(HttpMethod.Delete, deleteReqUrl);
            deleteReq.Headers.Add("X-Api-Key", apiKey.Trim());

            var deleteRes = await client.SendAsync(deleteReq);
            if (!deleteRes.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to delete item from {AddedBy}. Status: {Status}", item.AddedBy, deleteRes.StatusCode);
            }
            else
            {
                _logger?.LogInformation("Successfully requested {AddedBy} to delete (and blocklist={Blacklist}) torrent {Hash}", item.AddedBy, blacklist, item.HashString);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting item from {AddedBy}", item.AddedBy);
        }
    }

    public bool RemoveFromQueue(uint queueId)
    {
        lock (_queueLock)
        {
            var item = _torrentQueue.FirstOrDefault(q => q.QueueId == queueId);
            if (item == null || item.Status == QueuedTorrentStatus.Active) return false;
            return _torrentQueue.Remove(item);
        }
    }

    public bool ReorderQueue(uint queueId, int newPosition)
    {
        lock (_queueLock)
        {
            var item = _torrentQueue.FirstOrDefault(q => q.QueueId == queueId);
            if (item == null || item.Status != QueuedTorrentStatus.Queued) return false;
            _torrentQueue.Remove(item);
            _torrentQueue.Insert(Math.Clamp(newPosition, 0, _torrentQueue.Count), item);
            return true;
        }
    }

    /// <summary>
    /// Drains the pending-delete list and cleans up each stale entry from Seedr.
    /// For each entry: if a live torrent with the recorded id exists it is deleted;
    /// otherwise if a completed folder with the recorded name exists it is deleted.
    /// If neither is found the entry is silently dropped.
    /// Called at the start of every torrent-add so stale data never accumulates.
    /// </summary>
    public async Task CleanupPendingDeletesAsync(SeedrClient client)
    {
        List<PendingDelete> snapshot;
        lock (_pendingDeletesLock)
        {
            if (_pendingDeletes.Count == 0) return;
            snapshot = new List<PendingDelete>(_pendingDeletes);
            _pendingDeletes.Clear();
        }

        ListContentsResult? content = null;
        try { content = await client.ListContentsAsync(); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CleanupPendingDeletes: failed to list Seedr contents; will retry next add.");
            // Put them back so we retry on the next torrent-add.
            lock (_pendingDeletesLock) _pendingDeletes.AddRange(snapshot);
            return;
        }

        foreach (var entry in snapshot)
        {
            try
            {
                // 1. Active torrent still present — delete it.
                var liveTorrent = content.Torrents.FirstOrDefault(t => t.Id == entry.TorrentId);
                if (liveTorrent != null)
                {
                    _logger?.LogInformation("CleanupPendingDeletes: deleting stale active torrent id={Id} name='{Name}'.", entry.TorrentId, entry.TrimmedName);
                    await client.DeleteTorrentAsync(entry.TorrentId.ToString());
                    continue;
                }

                // 2. Completed folder present — delete it.
                var folder = content.Folders.FirstOrDefault(f => f.Name.Trim() == entry.TrimmedName);
                if (folder != null)
                {
                    _logger?.LogInformation("CleanupPendingDeletes: deleting stale folder id={Id} name='{Name}'.", folder.Id, entry.TrimmedName);
                    await client.DeleteFolderAsync(folder.Id.ToString());
                    continue;
                }

                // 3. Nothing found — already gone, nothing to do.
                _logger?.LogInformation("CleanupPendingDeletes: stale entry name='{Name}' id={Id} not found in Seedr; skipping.", entry.TrimmedName, entry.TorrentId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "CleanupPendingDeletes: error cleaning up entry name='{Name}' id={Id}.", entry.TrimmedName, entry.TorrentId);
            }
        }
    }

    public void UntrackAndRemoveIfHidden(uint queueId)
    {
        lock (_queueLock)
        {
            var item = _torrentQueue.FirstOrDefault(q => q.QueueId == queueId);
            if (item != null)
            {
                if (item.IsHidden)
                {
                    _torrentQueue.Remove(item);
                }
                else
                {
                    item.AddedBy = ""; // No longer tracked by arr
                }
            }
        }
    }

    public void ClearCompletedQueueItems()
    {
        lock (_queueLock)
        {
            foreach (var item in _torrentQueue.ToList())
            {
                if (item.Status is QueuedTorrentStatus.Completed or QueuedTorrentStatus.Failed or QueuedTorrentStatus.Cancelled)
                {
                    if (item.AddedBy == "radarr" || item.AddedBy == "sonarr")
                    {
                        item.IsHidden = true;
                    }
                    else
                    {
                        _torrentQueue.Remove(item);
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Queue processor
    // -------------------------------------------------------------------------

    private void EnsureQueueProcessorRunning()
    {
        lock (_queueLock)
        {
            if (_queueProcessorRunning) return;
            _queueProcessorRunning = true;
        }
        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        var running = new List<Task>();
        try
        {
            while (true)
            {
                var maxConcurrent = Math.Max(1, Plugin.Instance?.Configuration?.MaxConcurrentTorrents ?? 1);
                running.RemoveAll(t => t.IsCompleted);

                QueuedTorrent? nextItem = null;
                lock (_queueLock)
                {
                    if (running.Count < maxConcurrent)
                    {
                        nextItem = _torrentQueue.FirstOrDefault(q => q.Status == QueuedTorrentStatus.Queued);
                        if (nextItem != null) nextItem.Status = QueuedTorrentStatus.Active;
                    }

                    if (nextItem == null && running.Count == 0) { _queueProcessorRunning = false; return; }
                }

                if (nextItem != null)
                    running.Add(ProcessQueueItemAsync(nextItem));
                else
                    await Task.WhenAny(Task.WhenAny(running), Task.Delay(1000));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Queue processor encountered a fatal error.");
            lock (_queueLock) { _queueProcessorRunning = false; }
        }
    }

    private async Task ProcessQueueItemAsync(QueuedTorrent item)
    {
        if (_queueClient == null)
        {
            item.Status = QueuedTorrentStatus.Failed;
            item.ErrorMessage = "Not logged in to Seedr.";
            return;
        }

        try
        {
            var (code, message) = await HandleTorrentTask(_queueClient, item.Param, item);
            if (code != 200 && item.Status == QueuedTorrentStatus.Active)
            {
                item.Status = QueuedTorrentStatus.Failed;
                item.ErrorMessage = message;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Queue: error processing '{Name}'.", item.DisplayName);
            item.Status = QueuedTorrentStatus.Failed;
            item.ErrorMessage = ex.Message;
        }
        finally
        {
            if (item.Status == QueuedTorrentStatus.Active)
            {
                item.Status = QueuedTorrentStatus.Failed;
                item.ErrorMessage ??= "Torrent processing ended unexpectedly.";
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private JellySeedrTask CreateTask(JellySeedrTaskType type, object taskData)
    {
        var id = Interlocked.Increment(ref _taskIdCounter);
        var now = DateTime.Now;
        var task = new JellySeedrTask { Id = id, Type = type, Status = JellySeedrTaskStatus.Pending, CreatedAt = now, UpdatedAt = now };
        switch (type)
        {
            case JellySeedrTaskType.Torrent: task.TorrentTask = (JellySeedrTorrentTask)taskData; break;
            case JellySeedrTaskType.Fetch: task.FetchTask = (JellySeedrFetchTask)taskData; break;
            case JellySeedrTaskType.Delete: task.DeleteTask = (JellySeedrDeleteTask)taskData; break;
        }
        return task;
    }

    private static string GetUniqueFilePath(string existingPath, string sourceFileName)
    {
        var dir = Path.GetDirectoryName(existingPath) ?? string.Empty;
        var nameNoExt = Path.GetFileNameWithoutExtension(sourceFileName);
        var ext = Path.GetExtension(sourceFileName);
        uint counter = 1;
        string candidate;
        do { candidate = Path.Combine(dir, $"{nameNoExt}-({counter++}){ext}"); }
        while (System.IO.File.Exists(candidate));
        return candidate;
    }

    private static List<SeedrSelectionItem> NormalizeSelection(SeedrSelectionRequest? request) =>
        (request?.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Id)).ToList();

    private static bool IsFolder(string? kind) => string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase);
    private static bool IsTorrent(string? kind) => string.Equals(kind, "torrent", StringComparison.OrdinalIgnoreCase);

    private static string DescribeSeedrApiError(ApiException ex) => ex.ErrorType switch
    {
        "not_enough_space_added_to_wishlist" or "not_enough_space_wishlist_full" =>
            "Seedr rejected the torrent: not enough free space. Delete leftover files/folders on Seedr and try again.",
        "queue_full_added_to_wishlist" =>
            "Seedr rejected the torrent: transfer slot already in use (free accounts allow one active torrent).",
        "invalid_torrent" =>
            "Seedr rejected the torrent: the torrent/magnet is invalid.",
        _ => $"Seedr rejected the torrent: {ex.Message}" +
             (string.IsNullOrEmpty(ex.ErrorType) ? "" : $" (result: {ex.ErrorType})") +
             (ex.Code.HasValue ? $" (code: {ex.Code})" : "")
    };
}

// =============================================================================
// Enums
// =============================================================================

public enum QueuedTorrentStatus { Queued, Active, Completed, Failed, Cancelled }
public enum QueuedTorrentStage { Waiting, Torrenting, Fetching, CleaningUp }
public enum JellySeedrTaskStatus { Pending, InProgress, Completed, Failed, Cancelled }
public enum JellySeedrTaskType { Torrent, Fetch, Delete }
public enum FetchNameClashResolution { Overwrite, Skip, Rename }
public enum SeedrInputType { Unknown, TorrentFile, TorrentUrl, MagnetLink }

// =============================================================================
// Data models
// =============================================================================

public sealed class QueuedTorrent
{
    public uint QueueId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public SeedrTorrentAddParam Param { get; set; } = new();
    public string DisplayName { get; set; } = string.Empty;
    public DateTime QueuedAt { get; set; }
    public string AddedBy { get; set; } = string.Empty;
    public string HashString { get; set; } = string.Empty;
    public bool IsHidden { get; set; } = false;
    public QueuedTorrentStatus Status { get; set; } = QueuedTorrentStatus.Queued;
    public QueuedTorrentStage Stage { get; set; } = QueuedTorrentStage.Waiting;
    public double TorrentProgress { get; set; }
    public long TorrentTotalBytes { get; set; }
    public long FetchTotalBytes { get; set; }
    public long FetchCopiedBytes { get; set; }
    public string? ErrorMessage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int SeedrTorrentId { get; set; }
}

public sealed class SeedrTorrentAddParam
{
    public SeedrInputType InputType { get; set; }
    public string Source { get; set; } = string.Empty;
    public byte[] TorrentBytes { get; set; } = [];
    public HashSet<string> DownloadExtensions { get; set; } = [];
    public string DestinationPath { get; set; } = string.Empty;
    public bool DeleteAfterDownload { get; set; } = false;
    public FetchNameClashResolution ClashResolution { get; set; } = FetchNameClashResolution.Rename;
    /// <summary>When true, all files are downloaded regardless of extension (used by Mock Transmission).</summary>
    public bool DownloadAll { get; set; } = false;
    /// <summary>When true, files land at DestinationPath/DisplayName/FileName (preserves torrent folder structure).</summary>
    public bool UseSubfolderStructure { get; set; } = false;
}

public sealed class SeedrSelectionRequest
{
    public List<SeedrSelectionItem> Items { get; set; } = [];
    public string DestinationPath { get; set; } = string.Empty;
}

public sealed class SeedrSelectionItem
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Path { get; set; } = string.Empty;
}

public sealed class JellySeedrTask
{
    public uint Id { get; set; }
    public JellySeedrTaskType Type { get; set; }
    public JellySeedrTaskStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public JellySeedrFetchTask? FetchTask { get; set; }
    public JellySeedrTorrentTask? TorrentTask { get; set; }
    public JellySeedrDeleteTask? DeleteTask { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class JellySeedrFetchTask
{
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceFileId { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFilePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long BytesCopied { get; set; }
}

public sealed class JellySeedrTorrentTask
{
    public int TorrentId { get; set; }
    public string TorrentName { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public double Progress { get; set; }
}

public sealed class JellySeedrDeleteTask
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
}

// ---- DTOs (for File Browser API responses) ----

public sealed class SeedrFetchResultDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class SeedrFolderDto
{
    public string id { get; set; } = string.Empty;
    public string parentId { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
    public long size { get; set; }
    public List<SeedrFolderDto> children { get; set; } = [];
    public List<SeedrFileDto> files { get; set; } = [];
    public List<SeedrTorrentDto> torrents { get; set; } = [];
}

public sealed class SeedrTorrentDto
{
    public string id { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public long size { get; set; }
    public double progress { get; set; }
}

public sealed class SeedrFileDto
{
    public string id { get; set; } = string.Empty;
    public string folderId { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
    public long size { get; set; }
    public string hash { get; set; } = string.Empty;
}

/// <summary>
/// Represents a torrent whose tracking loop failed after 5 consecutive poll errors.
/// Stored until the next torrent-add, at which point the stale entry is cleaned
/// from Seedr (active torrent by id, or completed folder by trimmed name).
/// </summary>
public sealed record PendingDelete(int TorrentId, string TrimmedName);
