Driver installer provided for convenience.

FakerInput (https://github.com/Ryochan7/FakerInput) is an optional signed virtual
keyboard/mouse driver. BetterJoy uses its mouse interface when available so gyro mouse
movement and controller clicks continue to work in elevated applications and on the UAC
desktop, where Windows blocks ordinary SendInput injection. Select it in BetterJoy Setup,
or run FakerInput_Setup_0.1.1_x64.msi manually. Set UseFakerInput=false to keep using the
legacy input path even when the driver is installed. Its MIT license is included in
FakerInput-LICENSE.txt.
The bundled v0.1.1 MSI is the official Ryochan7 release (signed by Ryodigi Solutions
LLC), SHA-256 4c0aefb7340051a91d606776243298b5cd1143ef5508bbae6800c474f9ed0840.

ViGEmBus was archived by its author in November 2023 (trademark dispute, unrelated to
code quality) - v1.22.0, bundled here, is the final release and will not receive further
updates: https://github.com/nefarius/ViGEmBus/releases

If you're on Win7, please read the instructions on the page.

HidHide (https://github.com/nefarius/HidHide) hides the Pro Controller/Joycons from
other programs entirely (they won't even see the device), which avoids conflicts with
programs like Steam that fight BetterJoy over the raw HID device the moment they start.
It's optional - enable it with the "UseHidHide" setting (off by default) and run
HidHide_1.5.230_x64.exe in this folder.

Note: this replaces HidGuardian, which used to serve the same purpose. HidGuardian was
archived/deprecated by its own author, superseded by HidHide, and is no longer bundled
or supported here.
