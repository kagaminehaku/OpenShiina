# OpenShiina Loader

The title on the HOME menu that starts the player. hbmenu opened from the album runs as an applet
with about 394 MB of memory, too little for the player; an installed application has the
console's, about 3 GB. This is nx-hbloader (v2.4.5) built as an application: it starts
`sdmc:/switch/openshiina/OpenShiina.nro` first (hbmenu when that is missing), and hbmenu after
it; the player's game list opens hbmenu with −.

```
native\tools\forwarder\build.ps1 -Keys <prod.keys> -Work <folder>
```

The keys are the console's own (Lockpick_RCM writes them to `sd:/switch/prod.keys`); they are read
only to pack the NSP and never belong in the repository. The NSP is installed with DBI. It runs only
with signature patches (sys-patch) on the system it is installed on; with `patch_sysmmc=0` its icon
stays a loading circle and the console asks for an update.

What the title must have, found the hard way:

- npdm `application_type` 1 (an application). As an applet (2, nx-hbloader's own) hbloader tells
  hbmenu so, and hbmenu cannot open the applet service: 2345-0031.
- npdm debug flag `force_debug_prod`, not `force_debug`: on 19.0.0 and later the kernel refuses the
  latter on a retail console, and the title fails to start ("an error has occurred", no crash report).
- A new title id after an install that could not start: the console keeps the broken record of the
  first one.
- The SD card mounted before the player is looked for (the patch moves hbloader's mount up).
- The NACP: no startup account, no save data; a logo section and a romfs, as forwarders that work
  have them.

`npdm.json` is nx-hbloader's `hbl.json` with those changes; `icon.jpg`, `NintendoLogo.png` and
`StartupMovie.gif` are the title's own pictures (no Nintendo logo).
