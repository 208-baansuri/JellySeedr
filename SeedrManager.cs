using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Threading.Tasks;
using JellySeedr.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Seedrcc;
using MediaBrowser.Model.System;
using System.Threading;

namespace JellySeedr.Api;


public class SeedrManager
{
    public ILogger? _logger;

    private Dictionary<uint, JellySeedrTask> ActiveTasks = new Dictionary<uint, JellySeedrTask>();
    private uint TaskIdCounter = 0;

    private readonly List<QueuedTorrent> _torrentQueue = new();
    private readonly object _queueLock = new();
    private bool _queueProcessorRunning = false;
    private SeedrClient? _queueClient;
    private uint _queueIdCounter = 0;

    public async Task<(int code, string message)> DeleteSelection(SeedrClient client, SeedrSelectionRequest request)
    {

        try
        {
            var items = NormalizeSelection(request);
            var deletedFiles = 0;
            var deletedFolders = 0;
            var deletedTorrents = 0;

            var tasks = new List<Task>();
            foreach (var item in items)
            {
                if (IsTorrent(item.Kind))
                {
                    tasks.Add(client.DeleteTorrentAsync(item.Id));
                    deletedTorrents++;
                }
                else if (IsFolder(item.Kind))
                {
                    tasks.Add(client.DeleteFolderAsync(item.Id));
                    deletedFolders++;
                }
                else
                {
                    tasks.Add(client.DeleteFileAsync(item.Id));
                    deletedFiles++;
                }
            }

            await Task.WhenAll(tasks);

            var msgParts = new List<string>();
            if (deletedTorrents > 0) msgParts.Add($"{deletedTorrents} torrent(s)");
            if (deletedFiles > 0) msgParts.Add($"{deletedFiles} file(s)");
            if (deletedFolders > 0) msgParts.Add($"{deletedFolders} folder(s)");

            return (200, $"Deleted {string.Join(", ", msgParts)}.");
        }
        catch (Exception ex)
        {
            return (400, ex.Message);
        }
    }


    public async Task<(int code, string message)> FetchFiles(SeedrClient client, SeedrSelectionRequest request, FetchNameClashResolution clashResolution, List<Task>? tasksCollection = null, List<JellySeedrTask>? jellySeedrTaskCollection = null, QueuedTorrent? queueItem = null)
    {
        try
        {
            var items = NormalizeSelection(request);
            var fetchedFiles = 0;

            foreach (var item in items)
            {
                if (IsTorrent(item.Kind))
                {
                    continue;
                }
                if (!IsFolder(item.Kind))
                {
                    var fileUrl = await client.FetchFileAsync(item.Id);
                    var fileFetchTask = new JellySeedrFetchTask
                    {
                        SourceUrl = fileUrl.Url ?? string.Empty,
                        SourceFileId = item.Id,
                        SourceFileName = item.Name,
                        SourceFilePath = item.Path,
                        DestinationPath = Path.Combine(request.DestinationPath, item.Name),
                        FileSize = item.Size,
                        BytesCopied = 0
                    };


                    var newTask = CreateNewJellySeedrTask(JellySeedrTaskType.Fetch, fileFetchTask);
                    if (queueItem == null)
                    {
                        // Manual fetches (file browser) are tracked in the Task Status panel;
                        // queue-driven fetches are shown in the Torrent Queue panel instead.
                        lock (ActiveTasks)
                        {
                            ActiveTasks[newTask.Id] = newTask;
                        }
                    }
                    jellySeedrTaskCollection?.Add(newTask);
                    var createdTask = FetchFileAsync(newTask, fileFetchTask, clashResolution, queueItem);
                    tasksCollection?.Add(createdTask);

                    fetchedFiles++;
                }
            }


            return (200, $"Started downloading {fetchedFiles} file(s).");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in FetchFiles operation.");
            return (400, ex.Message);
        }
    }

    private async Task FetchFileAsync(JellySeedrTask task, JellySeedrFetchTask fetchTask, FetchNameClashResolution clashResolution = FetchNameClashResolution.Rename, QueuedTorrent? queueItem = null)
    {
        // Handle the fetched file URL as needed
        if (string.IsNullOrWhiteSpace(fetchTask.SourceUrl))
        {
            _logger?.LogWarning("FetchFileAsync task {TaskId} failed due to empty/null source URL.", task.Id);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = "Invalid source URL.";
            task.UpdatedAt = DateTime.UtcNow;
            return;
        }

        try
        {
            if (System.IO.File.Exists(fetchTask.DestinationPath))
            {
                switch (clashResolution)
                {
                    case FetchNameClashResolution.Skip:
                        task.Status = JellySeedrTaskStatus.Completed;
                        task.UpdatedAt = DateTime.UtcNow;
                        if (queueItem != null)
                        {
                            // Count skipped files as done so aggregate progress can reach 100%.
                            queueItem.FetchCopiedBytes += fetchTask.FileSize;
                        }
                        return;
                    case FetchNameClashResolution.Rename:
                        var directory = Path.GetDirectoryName(fetchTask.DestinationPath) ?? string.Empty;
                        var filenameWithoutExt = Path.GetFileNameWithoutExtension(fetchTask.SourceFileName);
                        var extension = Path.GetExtension(fetchTask.SourceFileName);
                        uint counter = 1;
                        var newDestinationPath = Path.Combine(directory, $"{filenameWithoutExt}-({counter}){extension}");
                        while (System.IO.File.Exists(newDestinationPath))
                        {
                            counter++;
                            newDestinationPath = Path.Combine(directory, $"{filenameWithoutExt}-({counter}){extension}");
                        }
                        fetchTask.DestinationPath = newDestinationPath;
                        break;
                }
            }

            task.Status = JellySeedrTaskStatus.InProgress;
            DownloadFile(task, fetchTask, queueItem);

        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error checking destination file for task {TaskId}: {Message}", task.Id, ex.Message);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error checking destination file: {ex.Message}";
            task.UpdatedAt = DateTime.UtcNow;
            return;
        }

    }

    private void DownloadFile(JellySeedrTask task, JellySeedrFetchTask fetchTask, QueuedTorrent? queueItem = null)
    {
        try
        {
            using (var httpClient = new HttpClient())
            using (var response = httpClient.GetAsync(fetchTask.SourceUrl, HttpCompletionOption.ResponseHeadersRead).Result)
            {
                response.EnsureSuccessStatusCode();

                using (var stream = response.Content.ReadAsStreamAsync().Result)
                using (var fileStream = new FileStream(fetchTask.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[8096]; // 8KB Buffer
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fileStream.Write(buffer, 0, bytesRead);
                        fetchTask.BytesCopied += bytesRead;
                        if (queueItem != null)
                        {
                            queueItem.FetchCopiedBytes += bytesRead;
                        }
                        task.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            task.Status = JellySeedrTaskStatus.Completed;
            task.UpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error downloading file for task {TaskId}: {Message}", task.Id, ex.Message);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error downloading file: {ex.Message}";
            task.UpdatedAt = DateTime.UtcNow;
        }
    }


    private List<SeedrSelectionItem> NormalizeSelection(SeedrSelectionRequest? request)
    {
        return (request?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToList();
    }

    private bool IsFolder(string? kind)
    {
        return string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTorrent(string? kind)
    {
        return string.Equals(kind, "torrent", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SeedrFolderDto> LoadFolderNodeAsync(SeedrClient client, string folderId, string currentFolderName = "", string currentFolderPath = "")
    {
        var folderObject = new SeedrFolderDto();
        var listing = await client.ListContentsAsync(folderId);

        folderObject.size = listing.Size;
        folderObject.id = listing.Id.ToString() ?? folderId;
        folderObject.parentId = listing.Parent.ToString() ?? string.Empty;
        folderObject.name = currentFolderName;
        folderObject.path = currentFolderPath;

        if (listing.Torrents != null)
        {
            foreach (var torrent in listing.Torrents)
            {
                folderObject.torrents.Add(new SeedrTorrentDto
                {
                    id = torrent.Id.ToString() ?? string.Empty,
                    name = torrent.Name ?? string.Empty,
                    size = torrent.Size,
                    progress = torrent.Progress
                });
            }
        }

        foreach (var file in listing.Files)
        {
            folderObject.files.Add(new SeedrFileDto
            {
                id = file.FolderFileId.ToString() ?? string.Empty,
                folderId = folderObject.id,
                name = file.Name ?? string.Empty,
                size = file.Size,
                hash = file.Hash ?? string.Empty,
                path = currentFolderPath + "/" + (file.Name ?? string.Empty)
            });
        }

        foreach (var childFolder in listing.Folders)
        {
            var childFolderId = childFolder.Id.ToString() ?? string.Empty;
            var childFolderName = childFolder.Name.Remove(0, currentFolderPath.Length).TrimStart('/');

            var childNode = await LoadFolderNodeAsync(client, childFolderId, childFolderName, childFolder.Name);
            folderObject.children.Add(childNode);
        }

        return folderObject;
    }

    public async Task<(int code, string message)> HandleTorrentTask(SeedrClient client, SeedrTorrentAddParam param, QueuedTorrent queueItem)
    {
        try
        {
            if (queueItem.Status == QueuedTorrentStatus.Cancelled)
            {
                return (200, "Torrent was cancelled before it started.");
            }

            queueItem.Stage = QueuedTorrentStage.Torrenting;

            AddTorrentResult? result = null;
            switch (param.InputType)
            {
                case SeedrInputType.TorrentFile:
                    {
                        result = await client.AddTorrentAsync(torrentBytes: param.TorrentBytes);
                        break;
                    }
                case SeedrInputType.TorrentUrl:
                    {
                        result = await client.AddTorrentAsync(torrentFile: param.Source);
                        break;
                    }
                case SeedrInputType.MagnetLink:
                    {
                        result = await client.AddTorrentAsync(magnetLink: param.Source);
                        break;
                    }
            }

            if (result == null || !result.Result)
            {
                _logger?.LogWarning("Failed to add torrent to Seedr. Source: '{Source}', InputType: {InputType}", param.Source, param.InputType);
                return (500, "Error adding torrent: Make sure url/magnet is valid and you have enough storage space. Also for free account only one concurrent torrent download is allowed.");
            }

            queueItem.SeedrTorrentId = result.UserTorrentId ?? 0;
            if (!string.IsNullOrWhiteSpace(result.Title))
            {
                queueItem.DisplayName = result.Title;
            }

            var torrentTask = new JellySeedrTorrentTask
            {
                TorrentId = result.UserTorrentId ?? 0,
                TorrentName = result.Title ?? string.Empty,
                TotalSize = -1,
                Progress = 0,
            };

            // Queue-driven work is shown in the Torrent Queue panel, so the task is not
            // registered in ActiveTasks (which backs the manual-downloads Task Status panel).
            var newTask = CreateNewJellySeedrTask(JellySeedrTaskType.Torrent, torrentTask);
            newTask.Status = JellySeedrTaskStatus.InProgress;

            await HandleTorrentCompletion(client, newTask, param, queueItem);

            // The queue item may already be Failed/Cancelled by the pipeline; map the task outcome otherwise.
            if (queueItem.Status == QueuedTorrentStatus.Active)
            {
                switch (newTask.Status)
                {
                    case JellySeedrTaskStatus.Completed:
                        queueItem.Status = QueuedTorrentStatus.Completed;
                        break;
                    case JellySeedrTaskStatus.Cancelled:
                        queueItem.Status = QueuedTorrentStatus.Cancelled;
                        queueItem.ErrorMessage ??= newTask.ErrorMessage;
                        break;
                    default:
                        queueItem.Status = QueuedTorrentStatus.Failed;
                        queueItem.ErrorMessage = newTask.ErrorMessage ?? "Torrent processing failed.";
                        break;
                }
            }

            return (200, $"Torrent added to Seedr: '{result.Title}'.");
        }
        catch (AuthenticationException ex)
        {
            _logger?.LogError(ex, "Seedr authentication failed while adding torrent from source '{Source}'", param.Source);
            return (401, "Seedr session expired or invalid. Please log out and log in again.");
        }
        catch (ApiException ex)
        {
            _logger?.LogError(ex, "Seedr API rejected torrent from source '{Source}'. ErrorType: {ErrorType}, Code: {Code}", param.Source, ex.ErrorType, ex.Code);
            return (500, DescribeSeedrApiError(ex));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in HandleTorrentTask for source '{Source}'", param.Source);
            return (500, $"Error adding torrent: {ex.Message}");
        }
    }

    private static string DescribeSeedrApiError(ApiException ex)
    {
        switch (ex.ErrorType)
        {
            case "not_enough_space_added_to_wishlist":
            case "not_enough_space_wishlist_full":
                return "Seedr rejected the torrent: not enough free space in your Seedr account. Delete leftover files/folders on Seedr and try again.";
            case "queue_full_added_to_wishlist":
                return "Seedr rejected the torrent: your Seedr transfer slot is already in use (free accounts allow one active torrent). Wait for or delete the active transfer on Seedr.";
            case "invalid_torrent":
                return "Seedr rejected the torrent: the torrent/magnet is invalid.";
        }

        var details = ex.Message;
        if (!string.IsNullOrEmpty(ex.ErrorType)) details += $" (result: {ex.ErrorType})";
        if (ex.Code.HasValue) details += $" (code: {ex.Code})";
        return $"Seedr rejected the torrent: {details}";
    }


    private async Task HandleTorrentCompletion(SeedrClient client, JellySeedrTask task, SeedrTorrentAddParam param, QueuedTorrent queueItem)
    {
        var torrentTask = task.TorrentTask;
        if (torrentTask == null)
        {
            _logger?.LogWarning("HandleTorrentCompletion failed: Torrent task data is null for Task {TaskId}.", task.Id);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = "Torrent task is null.";
            return;
        }

        try
        {
            Folder? seedrFolder = null;
            var torrentMissingPolls = 0;
            while (task.Status == JellySeedrTaskStatus.InProgress)
            {
                if (queueItem.Status == QueuedTorrentStatus.Cancelled)
                {
                    task.Status = JellySeedrTaskStatus.Cancelled;
                    task.ErrorMessage = "Cancelled by user.";
                    return;
                }

                var seedrContent = await client.ListContentsAsync();
                var seedrTorrentTask = seedrContent.Torrents.FirstOrDefault(x => x.Id == torrentTask.TorrentId);
                if (seedrTorrentTask == null)
                {
                    seedrFolder = seedrContent.Folders.FirstOrDefault(x => x.Name.Trim() == torrentTask.TorrentName.Trim());
                    if (seedrFolder != null)
                    {
                        torrentTask.Progress = 100;
                        queueItem.TorrentProgress = 100;
                        task.Status = JellySeedrTaskStatus.Completed;
                        break;
                    }

                    // The completed folder can appear a few polls after the torrent leaves the
                    // transfer list; only give up once it stays missing.
                    torrentMissingPolls++;
                    if (torrentMissingPolls >= 3)
                    {
                        _logger?.LogWarning("Torrent task {TaskId} (TorrentId: {TorrentId}) is no longer in Seedr list and no completed folder with name '{TorrentName}' was found. Task cancelled.",
                            task.Id, torrentTask.TorrentId, torrentTask.TorrentName);
                        task.Status = JellySeedrTaskStatus.Cancelled;
                        task.ErrorMessage = "Torrent cancelled, it is not found in seedr.";
                        return;
                    }
                }
                else
                {
                    torrentMissingPolls = 0;

                    if (torrentTask.TotalSize == -1)
                    {
                        torrentTask.TotalSize = seedrTorrentTask.Size;
                    }

                    torrentTask.Progress = seedrTorrentTask.Progress;
                    queueItem.TorrentProgress = seedrTorrentTask.Progress;
                    queueItem.TorrentTotalBytes = seedrTorrentTask.Size;
                    if (!string.IsNullOrWhiteSpace(seedrTorrentTask.Name))
                    {
                        queueItem.DisplayName = seedrTorrentTask.Name;
                    }
                    task.UpdatedAt = DateTime.Now;
                }

                await Task.Delay(2000);
            }

            if (seedrFolder != null && task.Status == JellySeedrTaskStatus.Completed && queueItem.Status != QueuedTorrentStatus.Cancelled)
            {
                var autoDownload = Plugin.Instance?.Configuration?.AutoDownload ?? true;
                if (autoDownload)
                {
                    await ScanAndFetchMatchingFilesAsync(client, seedrFolder, param, queueItem);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error occurred during HandleTorrentCompletion for task {TaskId}: {Message}", task.Id, ex.Message);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error during monitoring: {ex.Message}";
        }
    }

    private async Task ScanAndFetchMatchingFilesAsync(SeedrClient client, Folder seedrFolder, SeedrTorrentAddParam param, QueuedTorrent queueItem)
    {
        queueItem.Stage = QueuedTorrentStage.Fetching;

        var selectionRequest = new SeedrSelectionRequest
        {
            DestinationPath = param.DestinationPath
        };

        var stack = new Stack<string>();
        stack.Push(seedrFolder.Id.ToString());

        while (stack.Count > 0)
        {
            var currentFolderId = stack.Pop();
            var listing = await client.ListContentsAsync(currentFolderId);

            if (listing != null)
            {
                if (listing.Files != null)
                {
                    foreach (var file in listing.Files)
                    {
                        var fileName = file.Name ?? string.Empty;
                        var extensionWithPeriod = Path.GetExtension(fileName);
                        var extension = extensionWithPeriod.Length > 1 ? extensionWithPeriod.Substring(1) : string.Empty;

                        if (param.DownloadExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                        {
                            var fileSeedrPath = listing.Fullname + "/" + fileName;

                            selectionRequest.Items.Add(new SeedrSelectionItem
                            {
                                Id = file.FolderFileId.ToString() ?? string.Empty,
                                Kind = "file",
                                Name = fileName,
                                Size = file.Size,
                                Path = fileSeedrPath
                            });
                        }
                    }
                }

                if (listing.Folders != null)
                {
                    foreach (var childFolder in listing.Folders)
                    {
                        stack.Push(childFolder.Id.ToString());
                    }
                }
            }
        }

        queueItem.FetchTotalBytes = selectionRequest.Items.Sum(i => i.Size);
        queueItem.FetchCopiedBytes = 0;

        if (selectionRequest.Items.Count == 0)
        {
            _logger?.LogWarning("No files matching the allowed download extensions were found in folder '{FolderName}'.", seedrFolder.Name);
            // Keep the folder on Seedr so the user can inspect/fetch it manually via the file browser.
            queueItem.ErrorMessage = "No files matched the allowed download extensions. The folder was kept on Seedr.";
            return;
        }

        List<JellySeedrTask> jellySeedrTasks = [];
        List<Task> fetchTasks = [];
        await FetchFiles(client, selectionRequest, param.ClashResolution, fetchTasks, jellySeedrTasks, queueItem);
        await Task.WhenAll(fetchTasks);

        var allCompleted = jellySeedrTasks.All(t => t.Status == JellySeedrTaskStatus.Completed);

        if (allCompleted)
        {
            var deleteAfterDownload = Plugin.Instance?.Configuration?.DeleteAfterDownload ?? true;
            if (deleteAfterDownload)
            {
                queueItem.Stage = QueuedTorrentStage.CleaningUp;

                var jellySeedrDeleteTask = new JellySeedrDeleteTask
                {
                    Id = seedrFolder.Id.ToString(),
                    Path = seedrFolder.Fullname,
                    Kind = "folder"
                };

                var newTask = CreateNewJellySeedrTask(JellySeedrTaskType.Delete, jellySeedrDeleteTask);
                newTask.Status = JellySeedrTaskStatus.Pending;

                await HandleDeleteTask(client, newTask);
            }
        }
        else
        {
            queueItem.Status = QueuedTorrentStatus.Failed;
            queueItem.ErrorMessage = "Some files failed to download. The folder was kept on Seedr.";
        }
    }


    public async Task<(int code, string message)> HandleDeleteTask(SeedrClient client, JellySeedrTask deleteTask)
    {
        if (deleteTask.Type != JellySeedrTaskType.Delete)
        {
            _logger?.LogWarning("HandleDeleteTask aborted. Invalid task type: {Type}", deleteTask.Type);
            return (400, "Invalid task type");
        }

        var task = deleteTask.DeleteTask;

        if (deleteTask.Status != JellySeedrTaskStatus.Pending)
        {
            _logger?.LogWarning("HandleDeleteTask aborted. Task {TaskId} is not Pending (Status: {Status}).", deleteTask.Id, deleteTask.Status);
            return (400, "Invalid task status");
        }

        if (task == null || string.IsNullOrEmpty(task.Id))
        {
            _logger?.LogWarning("HandleDeleteTask aborted. Task {TaskId} contains invalid delete task payload.", deleteTask.Id);
            return (400, "Invalid task");
        }

        try
        {
            deleteTask.Status = JellySeedrTaskStatus.InProgress;
            APIResult res;
            if (IsFolder(task.Kind))
            {
                res = await client.DeleteFolderAsync(task.Id);
            }
            else
            {
                res = await client.DeleteFileAsync(task.Id);
            }

            if (!res.Result)
            {
                _logger?.LogWarning("Seedr delete request failed with code {Code}.", res.Code);
                return (400, res.Code?.ToString() ?? "Error deleting task");
            }

            deleteTask.Status = JellySeedrTaskStatus.Completed;
            return (200, "Task completed successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting task {TaskId} in Seedr: {Message}", deleteTask.Id, ex.Message);
            deleteTask.ErrorMessage = ex.Message;
            deleteTask.Status = JellySeedrTaskStatus.Failed;
            return (500, $"Error deleting task: {ex.Message}");
        }
    }

    public IReadOnlyCollection<JellySeedrTask> GetActiveTasks()
    {
        lock (ActiveTasks)
        {
            return ActiveTasks.Values.OrderBy(t => t.Id).ToList();
        }
    }

    public bool RemoveTask(uint taskId)
    {
        lock (ActiveTasks)
        {
            return ActiveTasks.Remove(taskId);
        }
    }

    public void ClearCompletedTasks()
    {
        lock (ActiveTasks)
        {
            var keysToRemove = ActiveTasks.Where(kvp =>
                kvp.Value.Status == JellySeedrTaskStatus.Completed ||
                kvp.Value.Status == JellySeedrTaskStatus.Failed ||
                kvp.Value.Status == JellySeedrTaskStatus.Cancelled
            ).Select(kvp => kvp.Key).ToList();

            foreach (var key in keysToRemove)
            {
                ActiveTasks.Remove(key);
            }
        }
    }

    public async Task<bool> CancelTaskAsync(SeedrClient? client, uint taskId)
    {
        JellySeedrTask? task;
        lock (ActiveTasks)
        {
            ActiveTasks.TryGetValue(taskId, out task);
        }

        if (task == null || task.Status != JellySeedrTaskStatus.InProgress || task.Type != JellySeedrTaskType.Torrent)
        {
            return false;
        }

        if (task.TorrentTask != null && client != null)
        {
            try
            {
                var result = await client.DeleteTorrentAsync(task.TorrentTask.TorrentId.ToString());
                _logger?.LogInformation("Deleted torrent {TorrentId} from Seedr: {Success}", task.TorrentTask.TorrentId, result.Result);
                if (result == null || !result.Result)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error deleting torrent {TorrentId} from Seedr: {Message}", task.TorrentTask.TorrentId, ex.Message);
                return false;
            }
        }

        task.Status = JellySeedrTaskStatus.Cancelled;
        task.UpdatedAt = DateTime.UtcNow;

        return true;
    }

    private JellySeedrTask CreateNewJellySeedrTask(JellySeedrTaskType type, object taskData)
    {
        var id = Interlocked.Increment(ref TaskIdCounter);

        var now = DateTime.Now;
        var task = new JellySeedrTask
        {
            Id = id,
            Type = type,
            Status = JellySeedrTaskStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        switch (type)
        {
            case JellySeedrTaskType.Torrent:
                task.TorrentTask = (JellySeedrTorrentTask)taskData;
                break;
            case JellySeedrTaskType.Fetch:
                task.FetchTask = (JellySeedrFetchTask)taskData;
                break;
            case JellySeedrTaskType.Delete:
                task.DeleteTask = (JellySeedrDeleteTask)taskData;
                break;
        }

        return task;
    }

    public (int position, uint queueId, string message) EnqueueTorrent(SeedrClient client, SeedrTorrentAddParam param, string displayName)
    {
        _queueClient = client;
        var queueId = Interlocked.Increment(ref _queueIdCounter);
        var item = new QueuedTorrent
        {
            QueueId = queueId,
            Param = param,
            DisplayName = displayName,
            QueuedAt = DateTime.UtcNow,
            Status = QueuedTorrentStatus.Queued
        };

        int position;
        lock (_queueLock)
        {
            _torrentQueue.Add(item);
            position = _torrentQueue.Count(q => q.Status == QueuedTorrentStatus.Queued || q.Status == QueuedTorrentStatus.Active);
        }

        EnsureQueueProcessorRunning();

        if (position == 1)
            return (position, queueId, $"Torrent '{displayName}' is being processed.");
        else
            return (position, queueId, $"Torrent '{displayName}' queued at position #{position}.");
    }

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
                        if (nextItem != null)
                        {
                            nextItem.Status = QueuedTorrentStatus.Active;
                        }
                    }

                    if (nextItem == null && running.Count == 0)
                    {
                        _queueProcessorRunning = false;
                        return;
                    }
                }

                if (nextItem != null)
                {
                    running.Add(ProcessQueueItemAsync(nextItem));
                }
                else
                {
                    // At capacity or nothing left queued: wait for a slot to free up, but wake
                    // periodically so newly enqueued items (and config changes) get picked up.
                    await Task.WhenAny(Task.WhenAny(running), Task.Delay(1000));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Queue processor encountered a fatal error.");
            lock (_queueLock)
            {
                _queueProcessorRunning = false;
            }
        }
    }

    private async Task ProcessQueueItemAsync(QueuedTorrent item)
    {
        var client = _queueClient;
        if (client == null)
        {
            item.Status = QueuedTorrentStatus.Failed;
            item.ErrorMessage = "Not logged in to Seedr.";
            return;
        }

        try
        {
            _logger?.LogInformation("Queue processor: starting torrent '{Name}' (QueueId: {QueueId})", item.DisplayName, item.QueueId);
            var (code, message) = await HandleTorrentTask(client, item.Param, item);
            if (code != 200 && item.Status == QueuedTorrentStatus.Active)
            {
                item.Status = QueuedTorrentStatus.Failed;
                item.ErrorMessage = message;
            }
            _logger?.LogInformation("Queue processor: torrent '{Name}' finished with status {Status}.", item.DisplayName, item.Status);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Queue processor: error processing torrent '{Name}'", item.DisplayName);
            item.Status = QueuedTorrentStatus.Failed;
            item.ErrorMessage = ex.Message;
        }
        finally
        {
            // Never leave an item stuck in Active; it would block the queue forever.
            if (item.Status == QueuedTorrentStatus.Active)
            {
                item.Status = QueuedTorrentStatus.Failed;
                item.ErrorMessage ??= "Torrent processing ended unexpectedly.";
            }
        }
    }

    public IReadOnlyList<QueuedTorrent> GetQueue()
    {
        lock (_queueLock)
        {
            return _torrentQueue.ToList();
        }
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
                return true;
            }
        }

        // Active items can only be cancelled while still torrenting; once files are being
        // copied to the library, let the item finish.
        if (item.Status == QueuedTorrentStatus.Active &&
            (item.Stage == QueuedTorrentStage.Waiting || item.Stage == QueuedTorrentStage.Torrenting))
        {
            item.Status = QueuedTorrentStatus.Cancelled;

            if (item.SeedrTorrentId > 0 && _queueClient != null)
            {
                try
                {
                    await _queueClient.DeleteTorrentAsync(item.SeedrTorrentId.ToString());
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete cancelled torrent {TorrentId} from Seedr.", item.SeedrTorrentId);
                }
            }
            return true;
        }

        return false;
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
            var clampedPosition = Math.Max(0, Math.Min(newPosition, _torrentQueue.Count));
            _torrentQueue.Insert(clampedPosition, item);
            return true;
        }
    }

    public void ClearCompletedQueueItems()
    {
        lock (_queueLock)
        {
            _torrentQueue.RemoveAll(q =>
                q.Status == QueuedTorrentStatus.Completed ||
                q.Status == QueuedTorrentStatus.Failed ||
                q.Status == QueuedTorrentStatus.Cancelled);
        }
    }

}

public enum QueuedTorrentStatus
{
    Queued,
    Active,
    Completed,
    Failed,
    Cancelled
}

public enum QueuedTorrentStage
{
    Waiting,
    Torrenting,
    Fetching,
    CleaningUp
}

public sealed class QueuedTorrent
{
    public uint QueueId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public SeedrTorrentAddParam Param { get; set; } = new();

    public string DisplayName { get; set; } = string.Empty;

    public DateTime QueuedAt { get; set; }

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

public enum JellySeedrTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

public enum JellySeedrTaskType
{
    Torrent,
    Fetch,
    Delete
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

public sealed class JellySeedrTask
{
    public uint Id { get; set; } = 0;

    public JellySeedrTaskType Type { get; set; }

    public JellySeedrTaskStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public JellySeedrFetchTask? FetchTask { get; set; }

    public JellySeedrTorrentTask? TorrentTask { get; set; }

    public JellySeedrDeleteTask? DeleteTask { get; set; }

    public string? ErrorMessage { get; set; }
}

public enum FetchNameClashResolution
{
    Overwrite,
    Skip,
    Rename
}


public enum SeedrInputType { Unknown, TorrentFile, TorrentUrl, MagnetLink }

public sealed class SeedrTorrentAddParam
{
    public SeedrInputType InputType { get; set; }

    public string Source { get; set; } = string.Empty;

    public byte[] TorrentBytes { get; set; } = [];

    public HashSet<string> DownloadExtensions { get; set; } = [];

    public string DestinationPath { get; set; } = string.Empty;

    public bool DeleteAfterDownload { get; set; } = false;

    public FetchNameClashResolution ClashResolution { get; set; } = FetchNameClashResolution.Rename;

}

