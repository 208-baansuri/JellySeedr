# JellySeedr

JellySeedr is a Jellyfin plugin that integrates with [Seedr.cc](https://www.seedr.cc) to manage, download, and synchronize torrents and magnet links straight into your Jellyfin media libraries.

## Features
- **Automatic Downloads**: Automatically downloads torrents on Seedr and fetches files from Seedr to your server.
- **Direct Uploads**: Paste magnet links, torrent URLs, or upload `.torrent` files directly from your browser.
- **Embedded File Browser**: Open a visual dialog to browse your Seedr cloud storage directory tree, list active torrent downloads with progress, download selected files/folders to local libraries, and delete items from your Seedr account.

## Installation

1. Add `https://raw.githubusercontent.com/208-baansuri/JellySeedr/main/manifest.json` as a repository in Jellyfin
2. Install latest `JellySeedr` plugin
3. Reboot Jellyfin Server

## Dependencies

This project depends on the `Seedrcc` NuGet package, which is hosted on GitHub Packages.

To build the project, you must register the NuGet source:
```bash
dotnet nuget add source https://nuget.pkg.github.com/208-baansuri/index.json --name github
```