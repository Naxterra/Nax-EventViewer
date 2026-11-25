# Nax Event Viewer

Nax Event Viewer is a fast replacement for the Windows Event Viewer. It shows every log on the PC, opens saved .evtx files and never changes a log.

## Why it is faster

The Windows Event Viewer formats the message of every event before it shows the list, and formatting is the slow part because each source's message resources must be loaded. Nax Event Viewer reads events in two passes. The first pass reads only the fixed fields (level, time, source, ID), which takes a fraction of a second even for tens of thousands of events, so the whole list appears at once. The second pass fills in the messages in the background, newest first, while the list is already usable.

Filtering and sorting run on the events in memory, so changing a filter is instant.

## Features

- All logs on the PC in a resizable tree with a filter box, grouped like the Windows Event Viewer, with the number of events in each log. Empty, analytic and debug logs are hidden unless you ask for them.
- "Problems in all logs" lists critical events, errors, warnings and Security audit failures across administrative logs for a configurable time window (1 h, 24 h, 7 d or 30 d), with a Log column showing which log each event came from.
- Search across messages, sources, computers, task categories and IDs (supporting `"quoted phrases"` and `-word` exclusions), an event ID filter in the Windows Event Viewer syntax (such as `4624, 1000-1010, -4634`), relative and custom From/To date ranges, source filtering, level buttons with counts, and a one-click Reset filters button.
- New events appear as soon as Windows writes them (Live), with automatic memory compaction for multi-day live sessions.
- A resizable detail pane with the full message, all system fields, structured EventData/UserData fields, the user account (resolved without blocking) and the event XML, plus Copy full details, Copy message and Copy XML actions.
- Saved .evtx files can be opened via Ctrl+O or drag-and-drop onto the window, the shown events can be saved as CSV, and a whole log can be saved as .evtx. In a CSV file, event text that would start a spreadsheet formula, at the start of a cell or after a semicolon, tab or line break, gets an apostrophe in front, so opening the file in Excel never runs it.
- Ctrl+C copies the selected events. F5 reads the log again. Resizable panes, column widths and window size are persisted across restarts.
- "Clear log" deletes all events of the open log after a confirmation whose default answer is Cancel. "Save and clear" saves the events to a .evtx file first, in the same step in which Windows empties the log. Right-clicking a folder in the log tree clears all of its logs the same way, and "Save and clear" then saves each log to its own file in a chosen folder. If moving a cleared log's backup fails, the app logs and shows every local recovery path in a modal dialog so no backup path is lost. The app never clears a log on its own, and Windows only lets administrators clear most logs.
- Right-click menus: an event can be copied (row, full details, message or XML), and its event ID or source can be shown alone or hidden. A log can be opened, saved, cleared or have its name copied. A saved file can be removed from the list, have its path copied or be shown in its folder, and a folder can be expanded or collapsed with everything below it.
- The interface is in English and German. It follows the Windows display language, and Settings > Language can choose one.
- Settings > Replace Windows Event Viewer makes Windows open Nax Event Viewer in place of the Windows Event Viewer, from the Win+X menu and the `eventvwr` command. The Start menu entry under Windows Tools opens `eventvwr.msc` in the Microsoft Management Console and keeps opening the Windows Event Viewer. The option needs an installation for all users and asks once for administrator rights, and turning it off or uninstalling the app restores the Windows Event Viewer.

The Security log and a few others can only be read by administrators. The app runs without administrator rights and offers to restart with them when a log needs it.

## Building

The project needs the .NET 11 SDK on Windows 11.

```
dotnet build NaxEventViewer.slnx
dotnet run --project tests/NaxEventViewer.Core.Tests
```

The test runner prints every test with its scenario and result. Its exit code is the number of failed tests.

## Installer

The installer needs Inno Setup 7 (`winget install JRSoftware.InnoSetup.7`). The build script publishes the app as a self-contained x64 program, which brings its own .NET runtime and Windows App SDK, and writes the installer to `artifacts\installer`.

```
.\build-installer.ps1
.\tests\installer\InstallTests.ps1
```

The installer installs for the current user without administrator rights by default, and it can install for all users instead. It adds a Start menu shortcut, an optional desktop shortcut and, if chosen, an entry in the "Open with" menu of .evtx files, while the Windows Event Viewer stays the default program for them. Setup and uninstalling wait until the app is closed. Uninstalling removes all of this, removes log copies that an interrupted .evtx save left in the temporary folder, and offers to delete the crash log as well. Both of these live in the profile of the user who runs the uninstaller, so after an installation for all users, other users' crash logs in `%LOCALAPPDATA%\Nax-EventViewer` stay until they delete them.

The installer test installs the app silently into a temporary folder, checks what was installed, uninstalls it and checks that nothing is left behind. It refuses to run while the app is installed for the current user.

## Command line

`Nax-EventViewer.exe --log "Microsoft-Windows-PowerShell/Operational"` opens a log, `--file path.evtx` (or just the path) opens a saved file, and `--overview` opens the problems overview.

## License

This software is under the Nax Source-Available License 1.0. You may install and use the official release builds, privately and at work, and you may read the source code. Modifying the software, redistributing it and selling it need the written permission of the owner. See [LICENSE](LICENSE).

Versions published before this license, up to and including commit 2b65fb2, were published under the GNU General Public License, version 3, and remain available under it. Components from other authors that are bundled with the app, such as the .NET runtime and the Windows App SDK, keep their own licenses, which [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) lists in full.

