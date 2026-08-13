<p align="center">
  <img src="title.png">
</p>

# BetterJoy v7.2.1
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
    1. Optional: if other programs (e.g. Steam) fight BetterJoy over your controller, run *Drivers/HidHide_1.5.230_x64.exe* and enable "UseHidHide" in the settings panel - this hides the controller from every other program entirely.
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
Check out the [wiki](https://github.com/Davidobot/BetterJoy/wiki)! There, you'll find all sorts of goodness such as the changelog, description of app settings, and the FAQ and Problems page. If Steam (or another program) fights BetterJoy over your controller, see the optional HidHide driver mentioned above - it hides the controller from everything except BetterJoy.

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
   an installer at *Installer\Output\BetterJoy-Setup-VERSION.exe*. If not, this step is skipped and you still
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

# Acknowledgements
A massive thanks goes out to [rajkosto](https://github.com/rajkosto/) for putting up with 17 emails and replying very quickly to my silly queries. The UDP server is also mostly taken from his [ScpToolkit](https://github.com/rajkosto/ScpToolkit) repo.

Also I am very grateful to [mfosse](https://github.com/mfosse/JoyCon-Driver) for pointing me in the right direction and to [Looking-Glass](https://github.com/Looking-Glass/JoyconLib) without whom I would not be able to figure anything out. (being honest here - the joycon code is his)

Many thanks to [nefarius](https://github.com/nefarius) for his ViGEm and [HidHide](https://github.com/nefarius/HidHide) projects! Apologies and appreciation go out to [epigramx](https://github.com/epigramx), creator of *WiimoteHook*, for giving me the driver idea and for letting me keep using his installation batch script even though I took it without permission. Thanks go out to [MTCKC](https://github.com/MTCKC/ProconXInput) for inspiration and batch files.

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!

Massive *thank you* to **all** code contributors!

Icons (modified): "[Switch Pro Controller](https://thenounproject.com/term/nintendo-switch/930119/)", "[
Switch Detachable Controller Left](https://thenounproject.com/remsing/uploads/?i=930115)", "[Switch Detachable Controller Right](https://thenounproject.com/remsing/uploads/?i=930121)" icons by Chad Remsing from [the Noun Project](http://thenounproject.com/). [Super Nintendo Controller](https://thenounproject.com/themizarkshow/collection/vectogram/?i=193592) icon by Mark Davis from the [the Noun Project](http://thenounproject.com/); icon modified by [Amy Alexander](https://www.linkedin.com/in/-amy-alexander/). [Nintendo 64 Controller](https://thenounproject.com/icon/game-controller-193588/) icon by Mark Davis from the [the Noun Project](http://thenounproject.com/); icon modified by [Gino Moena](https://www.github.com/GinoMoena).
