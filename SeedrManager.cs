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

    public async Task<(int code, string message)> DeleteSelection(SeedrClient client, SeedrSelectionRequest request)
    {

        try
        {
            var items = NormalizeSelection(request);
            var deletedFiles = 0;
            var deletedFolders = 0;

            var tasks = new List<Task>();
            foreach (var item in items)
            {
                if (IsFolder(item.Kind))
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

            return (200, $"Deleted {deletedFiles} file(s) and {deletedFolders} folder(s).");
        }
        catch (Exception ex)
        {
            return (400, ex.Message);
        }
    }


    public async Task<(int code, string message)> FetchFiles(SeedrClient client, SeedrSelectionRequest request, List<Task>? tasksCollection = null, List<uint>? jellySeedrTaskCollection = null)
    {
        try
        {
            var items = NormalizeSelection(request);
            var fetchedFiles = 0;

            foreach (var item in items)
            {
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
                    ActiveTasks[newTask.Id] = newTask;
                    jellySeedrTaskCollection?.Add(newTask.Id);
                    var createdTask = FetchFileAsync(newTask, fileFetchTask);
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

    private async Task FetchFileAsync(JellySeedrTask task, JellySeedrFetchTask fetchTask, FetchNameClashResolution clashResolution = FetchNameClashResolution.Rename)
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
            DownloadFile(task, fetchTask);

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

    private void DownloadFile(JellySeedrTask task, JellySeedrFetchTask fetchTask)
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

    public async Task<SeedrFolderDto> LoadFolderNodeAsync(SeedrClient client, string folderId, string currentFolderName = "", string currentFolderPath = "")
    {
        var folderObject = new SeedrFolderDto();
        var listing = await client.ListContentsAsync(folderId);

        folderObject.size = listing.Size;
        folderObject.id = listing.Id.ToString() ?? folderId;
        folderObject.parentId = listing.Parent.ToString() ?? string.Empty;
        folderObject.name = currentFolderName;
        folderObject.path = currentFolderPath;

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

    public async Task<(int code, string message)> HandleTorrentTask(SeedrClient client, SeedrTorrentAddParam param)
    {
        try
        {
            AddTorrentResult? result = null;
            switch (param.InputType)
            {
                case SeedrInputType.TorrentFile:
                    {
                        result = await client.AddTorrentAsync(torrentFile: param.Source);
                        break;
                    }
                case SeedrInputType.MagnetLink:
                    {
                        result = await client.AddTorrentAsync(magnetLink: param.Source);
                        break;
                    }
                case SeedrInputType.TorrentUrl:
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

            var torrentTask = new JellySeedrTorrentTask
            {
                TorrentId = result.UserTorrentId ?? 0,
                TorrentName = result.Title ?? string.Empty,
                TotalSize = -1,
                Progress = 0,
            };

            var newTask = CreateNewJellySeedrTask(JellySeedrTaskType.Torrent, torrentTask);
            newTask.Status = JellySeedrTaskStatus.InProgress;
            ActiveTasks[newTask.Id] = newTask;

            _ = HandleTorrentCompletion(client, newTask, param);

            return (200, $"Torrent added to Seedr: '{result.Title}'.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in HandleTorrentTask for source '{Source}'", param.Source);
            return (500, $"Error adding torrent: {ex.Message}");
        }
    }


    private async Task HandleTorrentCompletion(SeedrClient client, JellySeedrTask task, SeedrTorrentAddParam param)
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
            while (task.Status == JellySeedrTaskStatus.InProgress)
            {
                var seedrContent = await client.ListContentsAsync();
                var seedrTorrentTask = seedrContent.Torrents.FirstOrDefault(x => x.Id == torrentTask.TorrentId);
                if (seedrTorrentTask == null)
                {
                    seedrFolder = seedrContent.Folders.FirstOrDefault(x => x.Name == torrentTask.TorrentName);
                    if (seedrFolder == null)
                    {
                        _logger?.LogWarning("Torrent task {TaskId} (TorrentId: {TorrentId}) is no longer in Seedr list and no completed folder with name '{TorrentName}' was found. Task cancelled.",
                            task.Id, torrentTask.TorrentId, torrentTask.TorrentName);
                        task.Status = JellySeedrTaskStatus.Cancelled;
                        task.ErrorMessage = "Torrent cancelled, it is not found in seedr.";
                        return;
                    }
                    else
                    {
                        task.Status = JellySeedrTaskStatus.Completed;
                        break;
                    }
                }

                if (torrentTask.TotalSize == -1)
                {
                    torrentTask.TotalSize = seedrTorrentTask.Size;
                }


                torrentTask.Progress = seedrTorrentTask.Progress;
                task.UpdatedAt = DateTime.Now;

                await Task.Delay(500);
            }

            if (seedrFolder != null)
            {
                await ScanAndFetchMatchingFilesAsync(client, seedrFolder, param);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error occurred during HandleTorrentCompletion for task {TaskId}: {Message}", task.Id, ex.Message);
            task.Status = JellySeedrTaskStatus.Failed;
            task.ErrorMessage = $"Error during monitoring: {ex.Message}";
        }
    }

    private async Task ScanAndFetchMatchingFilesAsync(SeedrClient client, Folder seedrFolder, SeedrTorrentAddParam param)
    {
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

                        if (param.DownloadExtensions.Contains(extension))
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

        List<uint> jellySeedrTaskIds = [];
        if (selectionRequest.Items.Count > 0)
        {
            List<Task> fetchTasks = [];
            await FetchFiles(client, selectionRequest, fetchTasks, jellySeedrTaskIds);
            await Task.WhenAll(fetchTasks);
        }
        else
        {
            _logger?.LogWarning("No files matching the allowed download extensions were found in folder '{FolderName}'.", seedrFolder.Name);
        }

        var allCompleted = jellySeedrTaskIds.All(id => ActiveTasks[id].Status == JellySeedrTaskStatus.Completed);

        if (allCompleted)
        {
            var jellySeedrDeleteTask = new JellySeedrDeleteTask
            {
                Id = seedrFolder.Id.ToString(),
                Path = seedrFolder.Fullname,
                Kind = "folder"
            };

            var newTask = CreateNewJellySeedrTask(JellySeedrTaskType.Delete, jellySeedrDeleteTask);
            newTask.Status = JellySeedrTaskStatus.Pending;
            ActiveTasks[newTask.Id] = newTask;

            await HandleDeleteTask(client, newTask);
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
            return (500, $"Error deleting task: {ex.Message}");
        }
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

    public HashSet<string> DownloadExtensions { get; set; } = [];

    public string DestinationPath { get; set; } = string.Empty;

    public bool DeleteAfterDownload { get; set; } = false;

}

