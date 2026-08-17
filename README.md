<p align="center">
  <img src="title.png">
</p>

# BetterJoy2 v7.2.1
Allows the Nintendo Switch Pro Controller, Joycons, and Switch SNES controller to be used with [Cemu](http://cemu.info/) using [Cemuhook](https://sshnuke.net/cemuhook/), [Citra](https://citra-emu.org/), [Dolphin](https://dolphin-emu.org/), [Yuzu](https://yuzu-emu.org/), and system-wide with generic XInput support.

It also allows using the gyro to control your mouse and remap the special buttons (SL, SR, Capture) to key bindings of your choice.

If anyone would like to donate (for whatever reason), [you can do so here](https://www.paypal.me/DavidKhachaturov/5). 

#### Personal note
Thank you for using my software and all the constructive feedback I've been getting about it. I started writing this project a while back and have since then learnt a lot more about programming and software development in general. I don't have too much time to work on this project, but I will try to fix bugs when and if they arise. Thank you for your patience in that regard too!

It's been quite a wild ride, with nearly **590k** (!!) official download on GitHub and probably many more through the nightlies. I think this project was responsible for both software jobs I landed so far, so I am quite proud of it.

### Screenshot
![Example](https://raw.githubusercontent.com/Geofferey/BetterJoy2/b1378869a53dfe976f1677d887a6298f6e84b334/screenshots/BetterJoy_Screenshot_Main_UI.png)

# Downloads
Go to the [Releases tab](https://github.com/Geofferey/BetterJoy/releases/)!

# How to use
1. Install drivers
    1. Read the READMEs (they're there for a reason!)
    1. Run *Drivers/ViGEmBus_1.22.0_x64_x86_arm64.exe*
    1. Restart your computer
    1. Optional: if other programs (e.g. Steam) fight BetterJoy2 over your controller, run *Drivers/HidHide_1.5.230_x64.exe* and enable "UseHidHide" in the settings panel, this hides the controller from every other program entirely.
2. Run *BetterJoyForCemu.exe* 
    1. Run as Administrator if your keyboard/mouse button mappings don't work
3. Connect your controllers.
4. Start Cemu and ensure CemuHook has the controller selected.
    1. If using Joycons, CemuHook will detect two controllers - each will give all buttons, but choosing one over the other just chooses preference for which hand to use for gyro controls.
5. Go into *Input Settings*, choose XInput as a source and assign buttons normally.
    1. If you don't want to do this for some reason, just have one input profile set up with *Wii U Gamepad* as the controller and enable "Also use for buttons/axes" under *GamePad motion source*. **This is no longer required as of version 3**
    2. Turn rumble up to 70-80% if you want rumble.

* As of version 3, you can use the pro controller and Joycons as normal xbox controllers on your PC - try it with Steam!

# More Info
Check out the [wiki](https://github.com/Geofferey/BetterJoy2/wiki)! There, you'll find all sorts of goodness such as the changelog, description of app settings, and the FAQ and Problems page. If Steam (or another program) fights BetterJoy over your controller, see the optional HidHide driver mentioned above - it hides the controller from everything except BetterJoy.

# Connecting and Disconnecting the Controller
## Bluetooth Mode
 * Hold down the small button (sync) on the top of the controller for 5 seconds - this puts the controller into broadcasting mode.
 * Search for it in your bluetooth settings and pair normally.
 * To disconnect the controller - hold the home button (or capture button) down for 2 seconds (or press the sync button). To reconnect - press any button on your controller.

## USB Mode
 * Plug the controller into your computer.
 
## Disconnecting \[Windows 10]
1. Go into "Bluetooth and other devices settings"
1. Under the first category "Mouse, keyboard, & pen", there should be the pro controller.
1. Click on it and a "Remove" button will be revealed.
1. Press the "Remove" button

# Building

## One-click (Windows)
1. Install **Visual Studio** (Community edition is fine) with the **.NET desktop development** workload -
   [official guide](https://docs.microsoft.com/en-us/visualstudio/install/install-visual-studio).
2. Get the code via Git or the *Download ZIP* button.
3. Run **`build.bat`** in the repo root. It locates MSBuild, restores NuGet packages, and builds Release|x64.
4. If [Inno Setup](https://jrsoftware.org/isdl.php) is also installed, it additionally packages the build into
   an installer at *Installer\Output\BetterJoy-Setup-vVERSION.exe*. If not, this step is skipped and you still
   get a working build.

## Visual Studio (IDE)

1. If you didn't already, install **Visual Studio Community** via
   [the official guide](https://docs.microsoft.com/en-us/visualstudio/install/install-visual-studio).
   When asked about the workloads, select **.NET Desktop Development**.
2. Get the code project via Git or by using the *Download ZIP* button.
3. Open Visual Studio Community and open the solution file (*BetterJoy.sln*).
4. Open the NuGet manager via *Tools > NuGet Package Manager > Package Manager Settings*.
5. You should have a warning mentioning *restoring your packages*. Click on the **Restore** button.
6. You can now run and build BetterJoy.

## Visual Studio Build Tools (CLI)
1. Download **Visual Studio Build Tools** via
   [the official link](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio).
2. Install **NuGet** by following
   [the official guide](https://docs.microsoft.com/en-us/nuget/install-nuget-client-tools#nugetexe-cli).
   You should follow the section for ***nuget.exe***.
   Verify that you can run `nuget` from your favourite terminal.
3. Get the code project via Git or by using the *Download ZIP* button.
4. Open a terminal (*cmd*, *PowerShell*, ...) and enter the folder with the source code.
5. Restore the NuGet dependencies by running: `nuget restore`
6. Now build the app with MSBuild:
   ```
   msbuild .\BetterJoy.sln -p:Configuration=CONFIGURATION -p:Platform=PLATFORM -t:Rebuild
   ```
   The available values for **CONFIGURATION** are *Release* and *Debug*.
   The available values for **PLATFORM** are *x86* and *x64* (you want the latter 99.99% of the time).
7. You have now built the app. See the next section for locating the binaries.

## Binaries location
The built binaries are located under

*BetterJoyForCemu\bin\PLATFORM\CONFIGURATION*

where `PLATFORM` and `CONFIGURATION` are the one provided at build time. 

# AI, Authorship, and My Role

There is A LOT of love and hate around AI right now, with legitimate concerns about code quality, livelihoods, authorship, identity, and what any of this means for skilled work. Until recently, I was reluctant to use AI for even mundane tasks, let alone something this complex. I also deal with plenty of imposter syndrome, or maybe just an uncomfortable awareness of exactly where my own knowledge ends.

I'm not going to pretend that AI helping me write code suddenly makes me a computer scientist or gives me decades of low-level Windows input experience. It doesn't. There are developers who understand and can write this code at a level I cannot, and I respect the hell out of that. What AI has done is remove a massive implementation barrier between understanding a problem, having a vision for how it should behave, and being able to test that vision in working software.

My role in this project is closer to a technical director than a traditional programmer. I define how the system should behave, identify the problems and edge cases that matter, direct the implementation, test it against real hardware, analyze failures, and decide whether the result is actually good enough to ship. I may not understand every subsystem from first principles, but I have a very specific vision for the system's surface behavior. That is not superficial. It is the reason the underlying engineering exists.

A technically sophisticated implementation is still wrong if a controller reconnects under the wrong player number, a Joy-Con cannot transition cleanly between solo and paired operation, the GUI fights with an already-running service, or the gyro mathematically works but feels like shit in your hands. The implementation has to serve the behavior.

AI has dramatically expanded what I can build, but it has not eliminated the need for expertise or judgment. If anything, faster implementation makes judgment more important: **bad ideas can become working code just as quickly as good ones.** Code can compile, look convincing, and survive a quick test while still being fundamentally wrong once it meets real hardware and real-world edge cases. I still have to know what to ask for, what to test, which assumptions to challenge, what to throw away, and when something that looks correct in the source clearly is not.

So yeah, call it AI-assisted, vibe coded, AI-written, or whatever you want. I'm not going to hide the toolchain or claim expertise I do not have. AI helps produce and analyze implementations at a speed I could never achieve manually. The vision, requirements, hardware validation, interpretation, judgment, and decision to ship remain mine.

Judge the project by what it actually does, how reliably it does it, whether the work of others is properly credited, whether its problems are documented honestly, and whether the software keeps getting better.

# Acknowledgements

## Implementation lineage and adapted work

BetterJoy2 is built on a long chain of open-source controller work. The following projects
contributed code, algorithms, protocol knowledge, or concrete implementation patterns used by
this repository:

* [BetterJoy / BetterJoyForCemu](https://github.com/Davidobot/BetterJoy) by David Khachaturov
  (Davidobot) is the original project and the foundation of this fork. Its Joy-Con protocol,
  controller lifecycle, CemuHook motion server, input mapping, and ViGEm output work remain at
  the core of this codebase.
* [JoyconLib](https://github.com/Looking-Glass/JoyconLib) by Looking-Glass and
  [JoyCon-Driver](https://github.com/mfosse/JoyCon-Driver) by mfosse provided the early Joy-Con
  HID/protocol implementations from which BetterJoy's controller code was derived.
* [GamepadMotionHelpers](https://github.com/JibbSmart/GamepadMotionHelpers) by Julian "Jibb"
  Smart is the principal reference for the current filtered gyro-mouse and gyro-stick motion
  math. `GyroMousePlayerSpace` adapts its Y-up coordinate convention, gyro-propagated gravity
  tracking, shakiness-aware accelerometer correction, world-space yaw/pitch projection, and
  side-on singularity reduction. BetterJoy adds Joy-Con-specific axis normalization, grip
  recentering, diagnostics, and hardware-tested drift compensation around that foundation.
* [JoyShockMapper](https://github.com/JibbSmart/JoyShockMapper) and the actively developed
  [Electronicks fork](https://github.com/Electronicks/JoyShockMapper) were reference
  implementations for gyro-space selection and practical pointer behavior. BetterJoy's mapped
  2D smoothing, low-speed tightening, real-world traversal/sensitivity control, and separation
  of gravity reference from gyro-produced cursor displacement were informed by this work.
* [JoyShockLibrary](https://github.com/JibbSmart/JoyShockLibrary), also by JibbSmart, informed
  the canonical Nintendo-to-Y-up sensor frame and reference mouse behavior. Its v3.0 release is
  additionally used by the standalone controller timing harness under `tools/JoyShockTiming`.
* Sebastian Madgwick's IMU/AHRS algorithm and the C# implementation published by
  [x-io Technologies](https://github.com/xioTechnologies/Open-Source-AHRS-With-x-IMU) are the
  source of `MadgwickAHRS.cs`, subsequently hardened and extended in BetterJoy with reset and
  recenter behavior.
* [FakerInput](https://github.com/Ryochan7/FakerInput) by Ryochan7 supplies the optional signed
  virtual HID input driver. BetterJoy's FakerInput backend implements its HID control protocol
  for relative/absolute mouse movement, wheel reports, and mouse-button state so gyro mouse can
  work across elevated windows, the Windows sign-in screen, and service/session boundaries. The
  bundled installer and license remain attributable to the upstream project.
* The UDP server is largely derived from rajkosto's
  [ScpToolkit](https://github.com/rajkosto/ScpToolkit). ViGEmBus, ViGEmClient, HidHide, and their
  management libraries come from [Nefarius](https://github.com/nefarius).

## Implementations studied during the motion-control rework

The following projects were reviewed as independent comparisons for gyro mouse, gyro-to-stick,
calibration, smoothing, sensitivity, remapping, and Windows virtual-controller behavior. Their
source was not copied directly into BetterJoy, but their approaches materially informed design
decisions and hardware tests:

* [Yamakaky/gyromouse](https://github.com/Yamakaky/gyromouse)
* [ascarrambad/gyromouse](https://github.com/ascarrambad/gyromouse)
* [Handheld Companion](https://github.com/Valkirie/HandheldCompanion)
* [DS4Windows](https://github.com/Ryochan7/DS4Windows)
* [GyroWiki](https://gyrowiki.jibbsmart.com/) for the documented player-space, world-space,
  sensitivity, calibration, and gyro-aiming principles implemented by the projects above

Third-party copyright and license notices for code and binaries distributed with BetterJoy are
also retained in [LICENSE](LICENSE) and alongside bundled driver packages.

## Original BetterJoy acknowledgements

A massive thanks goes out to [rajkosto](https://github.com/rajkosto/) for putting up with 17 emails and replying very quickly to my silly queries. The UDP server is also mostly taken from his [ScpToolkit](https://github.com/rajkosto/ScpToolkit) repo.

Also I am very grateful to [mfosse](https://github.com/mfosse/JoyCon-Driver) for pointing me in the right direction and to [Looking-Glass](https://github.com/Looking-Glass/JoyconLib) without whom I would not be able to figure anything out. (being honest here - the joycon code is his)

Many thanks to [nefarius](https://github.com/nefarius) for his ViGEm and [HidHide](https://github.com/nefarius/HidHide) projects! Apologies and appreciation go out to [epigramx](https://github.com/epigramx), creator of *WiimoteHook*, for giving me the driver idea and for letting me keep using his installation batch script even though I took it without permission. Thanks go out to [MTCKC](https://github.com/MTCKC/ProconXInput) for inspiration and batch files.

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!

Massive *thank you* to **all** code contributors!

Icons (modified): "[Switch Pro Controller](https://thenounproject.com/term/nintendo-switch/930119/)", "[
Switch Detachable Controller Left](https://thenounproject.com/remsing/uploads/?i=930115)", "[Switch Detachable Controller Right](https://thenounproject.com/remsing/uploads/?i=930121)" icons by Chad Remsing from [the Noun Project](http://thenounproject.com/). [Super Nintendo Controller](https://thenounproject.com/themizarkshow/collection/vectogram/?i=193592) icon by Mark Davis from the [the Noun Project](http://thenounproject.com/); icon modified by [Amy Alexander](https://www.linkedin.com/in/-amy-alexander/). [Nintendo 64 Controller](https://thenounproject.com/icon/game-controller-193588/) icon by Mark Davis from the [the Noun Project](http://thenounproject.com/); icon modified by [Gino Moena](https://www.github.com/GinoMoena).
