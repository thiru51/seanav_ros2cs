# Running this on Windows

**Status: written and reasoned, not yet run on a Windows machine.** Everything below is
implemented and compiles, and the design follows Microsoft's documented loader contract — but
nobody has executed it on Windows yet. If you are the first, please open an issue either way. This
page will say "verified" when that has happened and not before.

## Contents

- [The short version](#the-short-version)
- [Installing ROS 2 — the easy way](#installing-ros-2--the-easy-way)
- [Why pixi rather than the official installer](#why-pixi-rather-than-the-official-installer)
- [Pointing SeaNav at it](#pointing-seanav-at-it)
- [Why Windows needed different code](#why-windows-needed-different-code)
- [When it goes wrong](#when-it-goes-wrong)

---

## The short version

```powershell
winget install prefix-dev.pixi
# restart the terminal, then:
pixi init ros_ws --channel https://prefix.dev/robostack-jazzy
cd ros_ws
pixi add ros-jazzy-desktop
pixi shell
```

Then set the library folder to `<your env>\Library\bin` — the folder with `rcl.dll` in it — or
leave it empty if you launched from `pixi shell`.

You need **Visual Studio 2022 with C++ support** installed. That is RoboStack's requirement, not
ours.

---

## Installing ROS 2 — the easy way

ROS 2 on Windows has a reputation, and it is deserved: the official route involves a manual
dependency hunt, a specific Python version, and a `local_setup.bat` that has to be run in every
terminal.

**There is now a much better way, and it is what this page recommends.** [RoboStack](https://robostack.github.io/)
packages ROS 2 as ordinary conda packages, and [pixi](https://pixi.prefix.dev/) installs them into
a project folder. The same three commands work on Windows, Linux and macOS, nothing is installed
system-wide, and nothing is compiled — the packages are pre-built.

**Step 1 — install pixi.**

```powershell
winget install prefix-dev.pixi
```

Then close and reopen the terminal, so it picks up the new `PATH`.

**Step 2 — make a workspace and add ROS.**

```powershell
pixi init ros_ws --channel https://prefix.dev/robostack-jazzy
cd ros_ws
pixi add ros-jazzy-desktop
```

`ros-jazzy-desktop` is the full set. If you want a smaller download, `ros-jazzy-ros-core` is enough
for everything this library does.

**Step 3 — enter the environment.**

```powershell
pixi shell
```

You are now in a shell where `ros2 topic list` works. Check it:

```powershell
ros2 topic list
```

Two things worth knowing:

- **`pixi init` writes a `pixi.toml` and a `pixi.lock`.** The lock file pins exact versions, so a
  colleague who runs `pixi install` in that folder gets byte-identical packages. For a project whose
  whole argument is reproducibility, that is a better story than "install ROS and hope".
- **Do not source an apt-installed ROS into a pixi shell.** RoboStack warns about this explicitly —
  the `PYTHONPATH` from the apt setup script conflicts with the conda environment. On Windows this
  is unlikely to come up; on Linux it is a real trap.

---

## Why pixi rather than the official installer

Both work. The official binary release from [docs.ros.org](https://docs.ros.org/en/jazzy/Installation.html)
is supported and fine if you already have it. This library does not care which you use — it wants a
folder with `rcl.dll` in it.

The reason to prefer pixi:

| | Official binary zip | pixi + RoboStack |
|---|---|---|
| Install | download, unzip, chase dependencies | three commands |
| System-wide? | yes | no — lives in the project folder |
| Reproducible for a colleague | "install ROS 2 Jazzy" | `pixi install` against a lock file |
| Same commands on Linux/macOS | no | yes |
| Uninstall | manual | delete the folder |

---

## Pointing SeaNav at it

**If you started from `pixi shell`**, the environment is already correct and you can leave the
library folder empty.

**Otherwise** — and this is the normal case for a Unity build someone double-clicks — set it
explicitly:

```csharp
NativeLoader.SearchPath = @"C:\path\to\ros_ws\.pixi\envs\default\Library\bin";
```

In Unity, that is the **Ros Library Folder** field on the ROS 2 Manager component.

**The folder you want is the one containing `rcl.dll`.** For conda and pixi that is
`<env>\Library\bin` — *not* `<env>\lib`, which is the natural guess and is wrong. `Library\bin` is
just where conda puts native binaries on Windows.

Not sure where it is? Ask:

```csharp
Debug.Log(NativeLoader.FindRosLibraryFolder() ?? "nothing found");
```

That checks an active conda/pixi environment, then `AMENT_PREFIX_PATH`, then the conventional
install locations, and returns the first folder that actually contains `rcl.dll`. It is a
convenience and a guess — it never overrides a folder you set yourself.

---

## Why Windows needed different code

Worth reading if something misbehaves, because the failure looks like nothing at all.

ROS 2's libraries depend on each other **by bare name** and are built without an embedded search
path. So when you load `rcl.dll`, Windows then has to find the eleven-odd DLLs it needs, and it
looks along `PATH` — which a program started from an icon never inherited.

**The Linux answer does not port.** On Linux, `dlerror` names the dependency it could not find, so
the loader can fetch that one and retry. Windows reports "the specified module could not be found"
and does not say which module, so there is nothing to retry on. That mechanism was silently doing
nothing on Windows.

**Windows has its own answer, and it is better.** `AddDllDirectory` registers a folder, and
`LoadLibraryExW` with `LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR` tells
the loader to use it — for the DLL *and its dependencies*. The OS does the work.

One deliberate choice: we do **not** call `SetDefaultDllDirectories`. Without it, the directories we
add are used only by our own `LoadLibraryEx` calls, so the process-wide DLL search order is
untouched. Inside Unity — which loads a great many native plugins of its own — quietly changing that
for the whole process would be an unfriendly thing to do.

---

## When it goes wrong

### "Windows error 126: The specified module could not be found"

The most common one, and **it is ambiguous by design**: Windows reports 126 both when the DLL you
asked for is missing *and* when the DLL was found but one of its dependencies was not. It does not
tell you which.

In order of likelihood:

1. **The folder is `<env>\lib` instead of `<env>\Library\bin`.** Check that `rcl.dll` is literally
   in the folder you named.
2. **The environment was never created.** `pixi shell` in the workspace and try `ros2 topic list`.
3. **Wrong distribution.** A `ros-humble-*` environment does not export the Jazzy symbols.

### "Windows error 193: %1 is not a valid Win32 application"

A 32-bit / 64-bit mismatch. ROS 2 is 64-bit, so the process loading it must be too. In Unity, check
that the build target architecture is x86_64.

### Unity finds it in the editor but the build cannot

The editor probably inherited a good environment from the terminal you launched it from, and the
build did not. Set **Ros Library Folder** explicitly rather than relying on inheritance — it works
in both cases and is the reason the field exists.

### Nothing appears in `ros2 topic list`

If both sides run but cannot see each other, this is a discovery problem rather than a loading one.
Check that both are using the same `ROS_DOMAIN_ID`, and that Windows Defender Firewall is not
blocking the process — ROS 2 discovery uses UDP multicast, and the first-run firewall prompt is very
easy to dismiss without reading.
