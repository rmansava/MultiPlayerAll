# MultiPlayerAll - Mac Setup Guide

## Prerequisites

### Step 1: Install Homebrew (if you don't have it)
Open Terminal and run:
```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

### Step 2: Install mpv (provides the video engine)
```bash
brew install mpv
```

## Installation

### Option A: Using the .app bundle
1. Extract `MultiPlayerAll-Mac.zip`
2. Double-click `MultiPlayerAll.app`
3. If macOS blocks it: **Right-click** the app > **Open** > **Open**
   (This is normal for unsigned apps, only needed the first time)

### Option B: Using the command line
1. Extract `MultiPlayerAll-mac-arm64.zip`
2. Open Terminal and navigate to the folder
3. Make it executable and run:
```bash
chmod +x MultiPlayerAll
./MultiPlayerAll
```

**Important:** You MUST run `chmod +x` first or the app won't launch.

## Troubleshooting

### "MultiPlayerAll is damaged and can't be opened"
This is macOS Gatekeeper blocking unsigned apps. Fix:
```bash
xattr -cr /path/to/MultiPlayerAll.app
```
Or for the raw binary:
```bash
xattr -cr /path/to/MultiPlayerAll
chmod +x /path/to/MultiPlayerAll
```

### App opens but shows "libmpv not found"
mpv is not installed. Run:
```bash
brew install mpv
```
Then verify it's installed:
```bash
ls /opt/homebrew/lib/libmpv*
```
You should see `libmpv.dylib`. If not, try:
```bash
ls /usr/local/lib/libmpv*
```

### App opens but video is black or shows 00:00:00
- Make sure mpv is installed (step above)
- Check the log file:
```bash
cat /tmp/MultiPlayerAll/crash.log
```

### "Permission denied" when running
```bash
chmod +x MultiPlayerAll
```

### Nothing happens when double-clicking .app
Try running from Terminal to see error messages:
```bash
/path/to/MultiPlayerAll.app/Contents/MacOS/MultiPlayerAll
```

## Notes
- This build is for Apple Silicon (M1/M2/M3/M4)
- If you have an Intel Mac, you need a different build (ask for osx-x64)
- Do NOT use `dotnet MultiPlayerAll.dll` - run the binary directly

## Log File Location
```
/tmp/MultiPlayerAll/crash.log
```
