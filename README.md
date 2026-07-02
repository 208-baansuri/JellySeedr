# JellySeedr

JellySeedr is a Jellyfin plugin that integrates with [Seedr.cc](https://www.seedr.cc) to manage, download, and synchronize torrents and magnet links straight into your Jellyfin media libraries.

## Installation & Build

### Prerequisites
* .NET SDK 8.0+
* A Jellyfin server installation

### Building
1. Build the project using `dotnet`:
   ```bash
   dotnet build
   ```
2. Copy the compiled DLL from `bin/Debug/net8.0/JellySeedr.dll` into your Jellyfin server plugins directory (e.g., `<Jellyfin_Data>/plugins/JellySeedr_1.0.0.0/`).
3. Restart your Jellyfin server.
