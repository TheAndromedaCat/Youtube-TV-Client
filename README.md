# <img src="https://raw.githubusercontent.com/marticliment/YouTube-TV-Client/main/Assets/StoreLogo.png" height="40">YouTube TV Client for PC [UNOFFICIAL]
A **UNIOFFICIAL** TubeTV: a YouTube TV Client for devices running Windows.
This UWP application is a simple WebView2 frame that uses a custom User Agent String, emulating a Smart TV.

**DISCLAIMER: This project is not related with Google at all. All YouTube logos and other symbols are their property, and should be used according to their terms. I am not affiliated with Google.**

![image](https://github.com/marticliment/Youtube-TV-Client/assets/53119851/76d179b3-c5cf-4aca-b217-88de48122e13)

# Installation

### Windows Desktop Installer (`.exe` — Recommended, no sideloading certificate required)
- Push to `main`, push a version tag (e.g. `v2.0.0`), or trigger the **Build Windows Installer** workflow in GitHub Actions to generate:
  - `TubeTV-Setup-<version>-x64.exe` — self-contained Windows installer (no .NET install required; uses Windows' built-in Edge WebView2 Runtime)
  - `TubeTV-Portable-<version>-x64.zip` — portable build for launchers like Armoury Crate SE, Playnite, or Steam

### Legacy UWP Package (`.msixbundle`)
Download the original UWP release from here: https://github.com/marticliment/Youtube-TV-Client/releases/download/1.1.0.0/9932MartCliment.YoutubeTVClient_1.1.0.0_neutral_._g91dtg5srk15g.Msixbundle
<br>

# Usage
Use it as you would use YouTube TV. Use the arrows on your keyboard to navigate across the whole interface, use <kbd>Enter</kbd> as the Ok button, and press <kbd>F11</kbd> or <kbd>Alt</kbd>+<kbd>Enter</kbd> to toggle fullscreen.

Navigation can also be done with the mouse cursor and scroll wheel, but it is trickier.

You can also link YouTube TV to your phone as if it was a regular TV app, and then cast videos to it.

When searching for videos, typing with your keyboard will work, but note that **pressing the enter key will type the selected character**. Results are loaded automatically, you just need to scroll down in order to see them.

# Controller Support
Built-in gamepad support works out of the box for couch PCs and Windows handhelds (such as **ASUS ROG Ally / ROG Xbox Ally X**, **Lenovo Legion Go**, and **Steam Deck on Windows**), supporting Xbox/XInput controllers natively (`XInput` + `Windows.Gaming.Input` on Desktop, `Windows.Gaming.Input` on UWP) as well as direct USB/Bluetooth PlayStation (DualSense / DualShock 4) and Nintendo Switch Pro controllers via the built-in Web Gamepad bridge. Multiple controllers can be used simultaneously (e.g., a docked handheld and a wireless couch controller), and controller input automatically pauses when the app is not in focus.

| Controller Input | Action |
| --- | --- |
| **D-Pad / Left Stick** | Navigate (hold to repeat) |
| **A / Cross** | Select / OK (hold for YouTube TV options menu) |
| **B / Circle** | Back / Close overlay / Exit video |
| **X / Square / Menu (Start)** | Play / Pause |
| **Y / Triangle** | Open Search |
| **LT / RT** | Rewind / Fast Forward (hold to repeat) |
| **LB / RB** | Previous / Next Video |
| **Right Stick Up / Down** | Volume Up / Down |
| **R3 (Right Stick Click)** | Mute / Unmute |
| **View / Select** | Toggle Fullscreen |
| **L3 (Left Stick Click)** | Show / Hide Controller Controls Overlay |

> **ROG Ally / ROG Xbox Ally X (Xbox Mode) tip:** In Armoury Crate Command Center, set **Control Mode** to **Gamepad** (Armoury Crate's `Auto` mode can sometimes leave non-Xbox desktop apps in mouse/Desktop controls while in Xbox Full Screen Experience). Also ensure **Xbox Game Bar → Settings → Gamepad Cursor** is disabled while using TubeTV.

# FAQ
**Q: Maximum resolution is limited to a lower resulution**<br>
A: The maximum resolution will be determined by the window size on launch, therefore, by your device's screen resolution.

**Q: YouTube TV has different languages, but this application shows in english only.**<br>
You can change the language on the in-app preferences.

# Screenshots
![image](https://store-images.s-microsoft.com/image/apps.7965.14046630558528880.fefadf9c-4a92-40c2-81ad-e464a1a4ee67.c0328ec6-5d0a-407e-9938-937d2f0fd0f8?h=768)
![image](https://store-images.s-microsoft.com/image/apps.46083.14046630558528880.fefadf9c-4a92-40c2-81ad-e464a1a4ee67.d5075204-582e-4908-9f51-9974e7a2e254?h=768)
![image](https://store-images.s-microsoft.com/image/apps.9493.14046630558528880.fefadf9c-4a92-40c2-81ad-e464a1a4ee67.460bbcd3-6944-4b89-892c-673ff64b67e1?h=768)
![image](https://store-images.s-microsoft.com/image/apps.3944.14046630558528880.fefadf9c-4a92-40c2-81ad-e464a1a4ee67.1885686a-cbe7-4d47-ac02-c7b6c886fe9f?h=768)
