# OpenShiina.nsp

The player installed as a title of its own: one install, and OpenShiina on the HOME menu opens the
game list at once, with the console's memory (an installed application has about 3 GB). No
OpenShiina.nro and no hbmenu are needed; − does nothing in the game list, as there is no loader to
go back to. Title id `01ff004f70659000`, beside the OpenShiina Loader's (`..\forwarder`), which stays
the way to run builds sent with nxlink.

```
native\tools\nsp\build.ps1 -Keys <prod.keys> -Work <folder> -Version 0.8.2
```

It packs the Switch build of `native/build-switch` (player.elf and its romfs, so build that first);
the npdm, the NACP's changes and the logos are the Loader's, and so is what the title needs to run:
signature patches (sys-patch) on the system it is installed on, and an install with DBI. The keys
are the console's own and never belong in the repository.

Two things differ from the Loader, because no hbloader runs before the player:

- No automatic video capture in the NACP (0x3035 = 0). With it the system takes 96 MB of the
  title's memory once it runs; hbloader leaves them free, the player (its heap is all the memory)
  does not, and am aborts (2128-0007): the whole console stops with a fatal error.
- The player asks the kernel for its own process handle (as hbloader does), which the 4 GB of the
  scripts are mapped with; without a loader, libnx has none to give. It does so first thing in
  main, closing libnx's sm: meanwhile: an application may own only one session it made itself,
  and sm: is that one (hbloader has closed it by then).
