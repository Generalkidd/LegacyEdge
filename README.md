# Legacy Edge

A UWP recreation of the legacy Microsoft Edge browser using the classic
EdgeHTML `WebView` control.

Open `LegacyEdge.sln` in Visual Studio 2022 with the UWP workload and Windows
10 SDK 10.0.19041 installed. ARM64 and x64 Debug/Release configurations are
included.

Current browser features include tabs and tab reordering, favorites, reading
list, history, downloads, settings, PDF viewing, Find on page, View Source, and
Windows printing. Find highlights matches in the loaded document and supports
Ctrl+F, Enter/Shift+Enter, and F3/Shift+F3. View Source prefers the loaded DOM
so it also works for signed-in pages. Standard web-page printing produces a
single printable page from the currently visible viewport; PDFs continue to
use their installed default application for printing.
