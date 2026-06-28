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

namespace JellySeedr.Api;


public class SeedrManager
{
    public ILogger? Logger;

    // [HttpPost]
    // [Route("fetch")]
    // public async Task<IActionResult> FetchSelection([FromBody] SeedrSelectionRequest request)
    // {
    //     var client = await GetSeedrClientAsync();
    //     if (client == null)
    //     {
    //         return Unauthorized(new { message = "Not logged in to Seedr" });
    //     }

    //     try
    //     {
    //         var items = NormalizeSelection(request);
    //         var seenFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    //         var results = new List<SeedrFetchResultDto>();

    //         foreach (var item in items)
    //         {
    //             if (IsFolder(item.Kind))
    //             {
    //                 var files = await CollectFilesAsync(client, item.Id, seenFileIds);
    //                 foreach (var file in files)
    //                 {
    //                     var fileId = file.Id;
    //                     if (string.IsNullOrWhiteSpace(fileId) || !seenFileIds.Add(fileId))
    //                     {
    //                         continue;
    //                     }

    //                     var fetched = await client.FetchFileAsync(fileId);
    //                     results.Add(new SeedrFetchResultDto
    //                     {
    //                         Id = fileId,
    //                         Name = string.IsNullOrWhiteSpace(file.Name) ? fetched.Name ?? string.Empty : file.Name,
    //                         Url = fetched.Url ?? string.Empty,
    //                         Size = file.Size
    //                     });
    //                 }

    //                 continue;
    //             }

    //             if (seenFileIds.Add(item.Id))
    //             {
    //                 var fetched = await client.FetchFileAsync(item.Id);
    //                 results.Add(new SeedrFetchResultDto
    //                 {
    //                     Id = item.Id,
    //                     Name = string.IsNullOrWhiteSpace(item.Name) ? fetched.Name ?? string.Empty : item.Name,
    //                     Url = fetched.Url ?? string.Empty,
    //                     Size = item.Size
    //                 });
    //             }
    //         }

    //         return Ok(new
    //         {
    //             message = results.Count == 1 ? "Fetched 1 file." : $"Fetched {results.Count} files.",
    //             files = results
    //         });
    //     }
    //     catch (Exception ex)
    //     {
    //         return BadRequest(new { message = ex.Message });
    //     }
    // }

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


    public async Task<(int code, string message)> FetchFiles(SeedrClient client, SeedrSelectionRequest request)
    {
        Logger?.LogInformation("Starting FetchFiles operation." + $" DestinationPath: {request.DestinationPath}, Items count: {request.Items.Count}");
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


                    var task = new JellySeedrTask
                    {
                        Id = TaskIdCounter++,
                        Type = JellySeedrTaskType.Fetch,
                        Status = JellySeedrTaskStatus.Pending,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        FetchTask = fileFetchTask
                    };

                    ActiveTasks[task.Id] = task;

                    _ = FetchFileAsync(task,fileFetchTask);

                    fetchedFiles++;
                }
            }


            return (200, $"Started downloading {fetchedFiles} file(s).");
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Error in FetchFiles operation.");
            return (400, ex.Message);
        }
    }

    private async Task FetchFileAsync(JellySeedrTask task, JellySeedrFetchTask fetchTask, FetchNameClashResolution clashResolution = FetchNameClashResolution.Rename)
    {
        Logger?.LogInformation($"Starting FetchFileAsync for task {task.Id} with source URL: {fetchTask.SourceUrl} and destination path: {fetchTask.DestinationPath}");
        // Handle the fetched file URL as needed
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
                switch (clashResolution)
                {
                    case FetchNameClashResolution.Skip:
                        task.Status = JellySeedrTaskStatus.Completed;
                        task.UpdatedAt = DateTime.UtcNow;
                        return;
                    case FetchNameClashResolution.Rename:
                        var directory = Path.GetDirectoryName(fetchTask.DestinationPath) ?? string.Empty;
                        var firstDotIndex = fetchTask.SourceFileName.IndexOf('.');
                        var filenameWithoutExt = firstDotIndex != -1 ? fetchTask.SourceFileName.Substring(0, firstDotIndex) : fetchTask.SourceFileName;
                        var extension = firstDotIndex != -1 ? fetchTask.SourceFileName.Substring(firstDotIndex) : string.Empty;
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
            Logger?.LogError(ex, $"Error checking destination file for task {task.Id}: {ex.Message}");
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
            Logger?.LogError(ex, $"Error downloading file for task {task.Id}: {ex.Message}");
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
    WaitingForDependencies
}

public enum JellySeedrTaskType
{
    Torrent,
    Fetch
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
    public string TorrentId { get; set; } = string.Empty;

    public string TorrentName { get; set; } = string.Empty;

    public long TotalSize { get; set; }

    public double Progress { get; set; }
}


public sealed class JellySeedrTask
{
    public uint Id { get; set; } = 0;

    public JellySeedrTaskType Type { get; set; }

    public JellySeedrTaskStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public JellySeedrFetchTask? FetchTask { get; set; }

    public JellySeedrTorrentTask? TorrentTask{ get; set; }

    public List<uint> DependentTaskIds { get; set; } = [];


    public string? ErrorMessage { get; set; }
}

public enum FetchNameClashResolution
{
    Overwrite,
    Skip,
    Rename
}

