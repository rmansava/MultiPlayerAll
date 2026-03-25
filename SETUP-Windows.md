# MultiPlayerAll - Windows Setup Guide

## Quick Start
1. Extract `MultiPlayerAll-Windows-x64.zip` to a folder
2. Double-click `MultiPlayerAll.exe`

Everything is included in the zip - no additional installation needed.

## Troubleshooting

### App crashes immediately or shows "Missing Dependencies"

**libmpv-2.dll not found**
- Make sure you extracted the FULL zip contents (not just the .exe)
- `libmpv-2.dll` must be in the SAME folder as `MultiPlayerAll.exe`
- If running from inside the zip without extracting, this will fail

**Visual C++ Runtime not found**
- Download and install from: https://aka.ms/vs/17/release/vc_redist.x64.exe
- This is a Microsoft component that libmpv requires
- After installing, restart the app

### App opens but video is black (audio works)

**Try ANGLE mode:**
- Open Command Prompt
- Navigate to the app folder: `cd C:\path\to\MultiPlayerAll`
- Run: `MultiPlayerAll.exe --angle`
- This uses a different graphics backend that works on some systems

**Update GPU drivers:**
- NVIDIA: https://www.nvidia.com/Download/index.aspx
- AMD: https://www.amd.com/en/support
- Intel: https://www.intel.com/content/www/us/en/download-center/home.html

### Video plays but seeking doesn't work (all windows show same time)
- Switch to "Download" mode (radio button in the app)
- This downloads the full video locally before playing
- Seeking works reliably on local files

### App crashes when switching number of windows
- This is fixed in the latest version
- Make sure you have the latest build

## System Requirements
- Windows 10 or later (64-bit)
- Any modern GPU (NVIDIA, AMD, or Intel)
- Internet connection to search and stream videos

## Log File Location
If something goes wrong, check the log at:
```
%TEMP%\MultiPlayerAll\crash.log
```
