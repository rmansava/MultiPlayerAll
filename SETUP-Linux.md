# MultiPlayerAll - Linux Setup Guide

## Prerequisites

### Install libmpv (provides the video engine)

**Ubuntu / Debian:**
```bash
sudo apt install libmpv-dev
```

**Fedora:**
```bash
sudo dnf install mpv-libs-devel
```

**Arch Linux:**
```bash
sudo pacman -S mpv
```

**openSUSE:**
```bash
sudo zypper install libmpv-devel
```

## Installation

1. Extract `MultiPlayerAll-Linux-x64.zip`
2. Make executable and run:
```bash
chmod +x MultiPlayerAll
./MultiPlayerAll
```

## Troubleshooting

### "libmpv not found" error
Install libmpv using the commands above for your distro. Verify:
```bash
ldconfig -p | grep libmpv
```
You should see `libmpv.so.2` or `libmpv.so`. If not, the package didn't install correctly.

### "Permission denied"
```bash
chmod +x MultiPlayerAll
```

### App won't start - no error message
Run from terminal to see errors:
```bash
./MultiPlayerAll 2>&1
```

### Video is black but audio works
Your GPU might not support OpenGL properly. Check your OpenGL version:
```bash
glxinfo | grep "OpenGL version"
```
You need at least OpenGL 3.0. Install proper GPU drivers:

**NVIDIA:**
```bash
sudo apt install nvidia-driver-535   # Ubuntu
```

**AMD/Intel (Mesa):**
```bash
sudo apt install mesa-utils
```

### App crashes on Wayland
Try running with X11:
```bash
GDK_BACKEND=x11 ./MultiPlayerAll
```

Or set the Avalonia rendering backend:
```bash
AVALONIA_SCREEN_SCALE_FACTORS="" ./MultiPlayerAll
```

## System Requirements
- Linux x86_64 (64-bit)
- libmpv (mpv media player library)
- OpenGL 3.0+ capable GPU
- X11 or Wayland display server

## Log File Location
```
/tmp/MultiPlayerAll/crash.log
```
