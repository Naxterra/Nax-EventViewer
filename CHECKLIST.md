# Must never happen

This list names what Nax Event Viewer must never do. Every release report marks each item as verified by a named test or as not verified. The owner approved this list on 2026-10-30 and the current wording of item 9 on 2026-10-31.

1. The app never clears, deletes or changes an event log, never writes an event and never changes a log's settings.
2. Reading a log or a saved .evtx file never changes that file.
3. A failed save never leaves a half-written file at the chosen path and never damages a file that was already there. A temporary file left by a crash during a save is hidden and is removed by the next save to the same path.
4. A save never reports success when the saved file is missing events or is empty because the source could not be read.
5. Text from events never runs as a formula when a saved CSV file is opened in a spreadsheet.
6. The list never shows an event that does not match the filter, and never hides an event that does.
7. The detail pane never shows the message, XML or fields of an event other than the one that is selected.
8. Reading and formatting events never freezes the window, however large the log is.
9. The app never asks for administrator rights for anything other than reading logs that Windows only lets administrators read, such as Security.
10. The app never sends events or any other data about the PC to a server.
11. Uninstalling never leaves program files, settings, file associations or other system changes behind, except the crash log if the user chooses to keep it.
