Driver installer provided for convenience.

ViGEmBus was archived by its author in November 2023 (trademark dispute, unrelated to
code quality) - v1.22.0, bundled here, is the final release and will not receive further
updates: https://github.com/nefarius/ViGEmBus/releases

If you're on Win7, please read the instructions on the page.

HIDGuardian is not installed by default anymore because it caused a lot of users a lot of headaches because they didn't know what to do with it. If you require the drivers for it and know what to do with it (eg: use the controllers with Steam games in Big Picture, if you have a Pro Controller + 2 Joycons), look into the HIDGuardian folder.
Details on using HIDGuardian are on the main README.

Note: HIDGuardian itself is also archived/deprecated upstream, superseded by HidHide
(https://github.com/nefarius/HidHide). It's kept here because BetterJoy's HidGuardian
integration (Program.cs) talks to HidGuardian's specific HidCerberus.Srv REST API, which
HidHide doesn't have - migrating is a real code change, not a driver swap.