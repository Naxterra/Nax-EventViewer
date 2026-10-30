# Nax Event Viewer

Nax Event Viewer is a fast replacement for the Windows Event Viewer. It shows every log on the PC, opens saved .evtx files and never changes a log.

## Why it is faster

The Windows Event Viewer formats the message of every event before it shows the list, and formatting is the slow part because each source's message resources must be loaded. Nax Event Viewer reads events in two passes. The first pass reads only the fixed fields (level, time, source, ID), which takes a fraction of a second even for tens of thousands of events, so the whole list appears at once. The second pass fills in the messages in the background, newest first, while the list is already usable.

Filtering and sorting run on the events in memory, so changing a filter is instant.

## Features

- All logs on the PC in a tree, grouped like the Windows Event Viewer, with the number of events in each log. Empty, analytic and debug logs are hidden unless you ask for them.
- "Problems in all logs" lists the critical events, errors and warnings of the last 24 hours from every administrative log.
- Search across messages, sources, task categories and IDs, an event ID filter in the Windows Event Viewer syntax (such as `4624, 1000-1010, -4634`), a time filter and level buttons with counts.
- New events appear as soon as Windows writes them (Live).
- A detail pane with the full message, all fields, the user account and the event XML.
- Saved .evtx files can be opened, the shown events can be saved as CSV, and a whole log can be saved as .evtx.
- Ctrl+C copies the selected events. F5 reads the log again.

The Security log and a few others can only be read by administrators. The app runs without administrator rights and offers to restart with them when a log needs it.

## Building

The project needs the .NET 11 SDK on Windows 11.

```
dotnet build NaxEventViewer.slnx
dotnet run --project tests/NaxEventViewer.Core.Tests
```

The test runner prints every test with its scenario and result. Its exit code is the number of failed tests.

## Command line

`Nax-EventViewer.exe --log "Microsoft-Windows-PowerShell/Operational"` opens a log, `--file path.evtx` (or just the path) opens a saved file, and `--overview` opens the problems overview.
