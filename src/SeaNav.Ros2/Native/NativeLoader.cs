using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SeaNav.Ros2.Native
{
    // Loads ROS 2's shared libraries and finds functions inside them.
    //
    // Normally in C# you'd just write [DllImport("rcl")] and let .NET find the
    // library for you. We can't do that here, and the reason is worth knowing
    // because it bites everyone once:
    //
    // On Linux, the list of folders searched for .so files (LD_LIBRARY_PATH) is
    // read by the operating system when the process starts. Not when you call
    // the function - when the process starts. So if you launch Unity without
    // sourcing ROS first, there is nothing your C# code can do about it later.
    // Setting the environment variable from inside the program is too late.
    //
    // The fix is to load the library ourselves, from a folder we decide at
    // runtime. That works whether you started from a terminal with ROS sourced
    // or double-clicked a game.
    //
    // (You might reach for System.Runtime.InteropServices.NativeLibrary, which
    // does exactly this and is much nicer. Unity's older C# runtime doesn't have
    // it, and we want one DLL that works in both Unity and normal .NET.)
    public static class NativeLoader
    {
        // Libraries we've already opened, so we don't dlopen the same file twice.
        private static readonly Dictionary<string, IntPtr> Opened = new Dictionary<string, IntPtr>();
        private static readonly object Lock = new object();

        /// <summary>
        /// Folder to look in for ROS 2 libraries, for example /opt/ros/jazzy/lib.
        /// Leave it empty if ROS is already sourced in your shell - then the
        /// operating system finds them on its own.
        /// </summary>
        public static string SearchPath = string.Empty;

        // See Prime() below.
        private static bool _primed;

        /// <summary>
        /// Makes one throwaway call to dlerror so that the P/Invoke stub is
        /// resolved before we need it for real.
        /// </summary>
        /// <remarks>
        /// This looks like superstition and is not. dlerror reports the last
        /// loader error and CLEARS it, and .NET resolves a P/Invoke stub the
        /// first time you call it - which means loading libdl and looking up the
        /// symbol, using the dynamic loader, which clears the very error we were
        /// about to read.
        ///
        /// The result is that the first dlerror after a failed dlopen comes back
        /// empty, and only the first. Calling it once up front, when there is no
        /// error to lose, moves that cost somewhere harmless.
        /// </remarks>
        private static void Prime()
        {
            if (_primed || IsWindows) return;
            _primed = true;
            try { dlerror(); } catch { /* nothing to lose if this is unavailable */ }
        }

        /// <summary>
        /// Opens a library by its short name. Pass "rcl", not "librcl.so" - the
        /// file name is different on Windows and we add the right decoration here.
        /// </summary>
        public static IntPtr Load(string name)
        {
            lock (Lock)
            {
                if (Opened.TryGetValue(name, out IntPtr already))
                    return already;

                Prime();

                string fileName = IsWindows ? name + ".dll" : "lib" + name + ".so";

                // Try our folder first, then let the OS look wherever it normally would.
                IntPtr handle = IntPtr.Zero;
                if (!string.IsNullOrEmpty(SearchPath))
                    handle = OpenWithDependencies(Path.Combine(SearchPath, fileName), 0);
                if (handle == IntPtr.Zero)
                    handle = Open(fileName);

                if (handle == IntPtr.Zero) throw NotFound(fileName);

                Opened[name] = handle;
                return handle;
            }
        }

        /// <summary>
        /// Builds the exception for a library we could not open, with advice
        /// that matches the platform the reader is actually on.
        /// </summary>
        /// <remarks>
        /// This used to print the Linux advice on both platforms, which sent
        /// Windows users looking for a setup.bash that does not exist there.
        /// </remarks>
        private static DllNotFoundException NotFound(string fileName)
        {
            string wherever = string.IsNullOrEmpty(SearchPath)
                ? "the system library path"
                : SearchPath + " or the system library path";

            string advice;
            if (IsWindows)
            {
                advice =
                    "SET THE LIBRARY FOLDER, or start this process from a ROS shell.\n\n" +
                    "The folder wanted is the one containing rcl.dll:\n" +
                    "    RoboStack / conda   <env>\\Library\\bin\n" +
                    "    ROS 2 binary zip    C:\\dev\\ros2_jazzy\\bin\n\n" +
                    "Windows searches PATH for a DLL's dependencies, and a program started " +
                    "from an icon never inherited the ROS PATH. Naming the folder lets us hand " +
                    "it to the loader directly, which works either way.\n\n" +
                    "If you have no ROS on this machine yet, the least painful route is pixi:\n" +
                    "    winget install prefix-dev.pixi\n" +
                    "    pixi init ros_ws --channel https://prefix.dev/robostack-jazzy\n" +
                    "    cd ros_ws && pixi add ros-jazzy-desktop\n";
            }
            else
            {
                advice =
                    "SOURCE ROS 2 BEFORE STARTING THIS PROCESS:\n" +
                    "    source /opt/ros/<distro>/setup.bash\n\n" +
                    "Setting a library folder gets some of the way and is not enough on its " +
                    "own. ROS 2 is built without an RPATH and opens libraries by bare name " +
                    "while it runs, so it needs LD_LIBRARY_PATH - which Linux fixes when a " +
                    "process starts and nothing can change afterwards.\n" +
                    "For Unity: launch the editor from a terminal that has sourced ROS, or " +
                    "add the source line to ~/.profile and log out and back in.\n";
            }

            string guess = FindRosLibraryFolder();
            if (!string.IsNullOrEmpty(guess) && !string.Equals(guess, SearchPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                advice += "\nA ROS install appears to be at:\n    " + guess +
                          "\nTry setting the library folder to that.\n";
            }

            return new DllNotFoundException(
                "Could not open " + fileName + ". Looked in " + wherever + ".\n\n" +
                advice + "\n" + LastError());
        }

        /// <summary>
        /// Makes a reasonable guess at where ROS 2's libraries are on this
        /// machine, so a user does not have to type a path they may not know.
        /// </summary>
        /// <remarks>
        /// A guess, and treated as one - it is used to improve an error message
        /// and as an optional convenience, never silently instead of what the
        /// caller asked for. Returns null when nothing plausible is found.
        ///
        /// The order matters: an activated environment is checked before any
        /// fixed location, because someone who has activated one means it.
        /// </remarks>
        public static string FindRosLibraryFolder()
        {
            var candidates = new List<string>();

            // An active conda / RoboStack / pixi environment. On Windows conda
            // puts DLLs under Library\bin; on Linux and macOS in lib.
            string conda = Environment.GetEnvironmentVariable("CONDA_PREFIX");
            if (!string.IsNullOrEmpty(conda))
            {
                candidates.Add(IsWindows ? Path.Combine(conda, "Library", "bin")
                                         : Path.Combine(conda, "lib"));
            }

            // A sourced ROS install names its own prefix.
            string ament = Environment.GetEnvironmentVariable("AMENT_PREFIX_PATH");
            if (!string.IsNullOrEmpty(ament))
            {
                foreach (string prefix in ament.Split(IsWindows ? ';' : ':'))
                {
                    if (string.IsNullOrEmpty(prefix)) continue;
                    candidates.Add(Path.Combine(prefix, IsWindows ? "bin" : "lib"));
                }
            }

            string distro = Environment.GetEnvironmentVariable("ROS_DISTRO");

            if (IsWindows)
            {
                foreach (string root in new[] { @"C:\dev", @"C:\opt", @"C:\" })
                {
                    if (!string.IsNullOrEmpty(distro))
                        candidates.Add(Path.Combine(root, "ros2_" + distro, "bin"));
                    candidates.Add(Path.Combine(root, "ros2", "bin"));
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(distro))
                    candidates.Add("/opt/ros/" + distro + "/lib");

                // Newest first, so a machine with two distributions gets the one
                // most likely to be current rather than whichever sorts first.
                foreach (string d in new[] { "kilted", "jazzy", "iron", "humble" })
                    candidates.Add("/opt/ros/" + d + "/lib");
            }

            string wanted = IsWindows ? "rcl.dll" : "librcl.so";
            foreach (string folder in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(folder, wanted))) return folder;
                }
                catch { /* an unreadable candidate is simply not the answer */ }
            }

            return null;
        }

        /// <summary>
        /// Sets an environment variable so that NATIVE code can see it.
        /// </summary>
        /// <remarks>
        /// Not the same thing as Environment.SetEnvironmentVariable. On Unix
        /// that only updates .NET's own copy of the environment - a C library
        /// calling getenv() sees nothing at all. ROS calls getenv, so we have to
        /// call setenv.
        ///
        /// This works where the LD_LIBRARY_PATH trick does not, and the
        /// difference is worth understanding: the dynamic loader reads its search
        /// path once when the process starts, so changing it later is pointless.
        /// AMENT_PREFIX_PATH is read by ordinary code whenever it is needed, so
        /// setting it late is fine.
        /// </remarks>
        public static void SetNativeEnvironment(string name, string value)
        {
            if (IsWindows)
            {
                Environment.SetEnvironmentVariable(name, value);
                return;
            }

            Environment.SetEnvironmentVariable(name, value);   // keep .NET in step
            try { setenv(name, value, 1); } catch { /* nothing we can do about it */ }
        }

        /// <summary>Reads a variable as native code would see it.</summary>
        public static string GetNativeEnvironment(string name)
        {
            if (IsWindows) return Environment.GetEnvironmentVariable(name);
            try
            {
                IntPtr p = getenv(name);
                return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
            }
            catch
            {
                return Environment.GetEnvironmentVariable(name);
            }
        }

        /// <summary>
        /// Finds a function inside an already-opened library. Throws if it isn't there.
        /// </summary>
        public static IntPtr Symbol(IntPtr library, string functionName)
        {
            IntPtr address = FindSymbol(library, functionName);
            if (address == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException(
                    "Could not find '" + functionName + "' in the library.\n" +
                    "If you were looking up a message type, the package that defines it " +
                    "probably isn't installed for this ROS distribution.\n" + LastError());
            }
            return address;
        }

        /// <summary>
        /// Wraps a C function up as a C# delegate you can call normally.
        /// </summary>
        public static T Function<T>(IntPtr library, string functionName) where T : class
        {
            IntPtr address = Symbol(library, functionName);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        // ---------------------------------------------------------------------
        // Below here is just the per-platform plumbing.
        // ---------------------------------------------------------------------

        private static bool IsWindows
        {
            get
            {
                PlatformID id = Environment.OSVersion.Platform;
                return id != PlatformID.Unix && id != PlatformID.MacOSX;
            }
        }

        private static IntPtr Open(string path)
        {
            return IsWindows ? OpenWindows(path) : dlopen(path, RTLD_NOW | RTLD_GLOBAL);
        }

        /// <summary>
        /// Opens a DLL on Windows, telling the loader to resolve the DLL's own
        /// dependencies from the folder it came from and from SearchPath.
        /// </summary>
        /// <remarks>
        /// Windows has the problem Linux has - ROS's DLLs depend on each other by
        /// bare name, and the loader normally looks along PATH, which a
        /// double-clicked game never had - but unlike Linux it has a built-in
        /// answer, so we do not need the dlerror recursion here.
        ///
        /// Two flags do the work, and the documentation is explicit that both
        /// cover DEPENDENCIES and not merely the named file:
        ///
        ///   LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR  the folder holding this DLL is
        ///       searched for its dependencies. Requires a full path, which is
        ///       why the caller must not pass a bare file name with this flag.
        ///
        ///   LOAD_LIBRARY_SEARCH_DEFAULT_DIRS  the application folder, System32,
        ///       and anything handed to AddDllDirectory.
        ///
        /// AddDllDirectory is what makes SearchPath mean something. Note we do
        /// NOT call SetDefaultDllDirectories: without it, directories added this
        /// way are used only by LoadLibraryEx calls that ask for them, so the
        /// process-wide search order is left alone. That matters inside Unity,
        /// which loads plenty of native plugins of its own and would be within
        /// its rights to break if we quietly changed the rules underneath it.
        ///
        /// These flags need KB2533623 on Windows 7 and are simply present from
        /// Windows 8 on. The documented way to test for them is to look up
        /// AddDllDirectory itself, which is what Ready() does; if it is missing
        /// we fall back to the old altered-search-path behaviour, which cannot be
        /// combined with any LOAD_LIBRARY_SEARCH flag.
        /// </remarks>
        private static IntPtr OpenWindows(string path)
        {
            bool rooted = Path.IsPathRooted(path);

            if (WindowsSearchFlagsAvailable)
            {
                RegisterWindowsSearchPath();

                int flags = LOAD_LIBRARY_SEARCH_DEFAULT_DIRS;
                if (rooted) flags |= LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR;

                IntPtr handle = LoadLibraryExW(path, IntPtr.Zero, flags);
                if (handle != IntPtr.Zero) return handle;

                // A bare name may still be resolvable the ordinary way - for
                // instance when the process really was started from a ROS shell
                // and PATH is already correct.
                if (!rooted) return LoadLibraryExW(path, IntPtr.Zero, 0);
                return IntPtr.Zero;
            }

            return LoadLibraryExW(path, IntPtr.Zero,
                                  rooted ? LOAD_WITH_ALTERED_SEARCH_PATH : 0);
        }

        private static bool? _windowsSearchFlags;

        /// <summary>
        /// True when this Windows supports the LOAD_LIBRARY_SEARCH_* flags.
        /// </summary>
        /// <remarks>
        /// Probed the way Microsoft documents it: look up AddDllDirectory in
        /// kernel32. If it resolves, the flags work.
        /// </remarks>
        private static bool WindowsSearchFlagsAvailable
        {
            get
            {
                if (_windowsSearchFlags.HasValue) return _windowsSearchFlags.Value;

                bool ok = false;
                try
                {
                    IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
                    ok = kernel32 != IntPtr.Zero &&
                         GetProcAddress(kernel32, "AddDllDirectory") != IntPtr.Zero;
                }
                catch { ok = false; }

                _windowsSearchFlags = ok;
                return ok;
            }
        }

        private static string _registeredSearchPath;

        /// <summary>
        /// Hands SearchPath to the Windows loader, once, and only if it changed.
        /// </summary>
        /// <remarks>
        /// Also registers the sibling "lib" folder. A conda or RoboStack install
        /// puts the DLLs in Library\bin but leaves some support files under
        /// Library\lib, and a user who points us at either one should not have to
        /// know which.
        /// </remarks>
        private static void RegisterWindowsSearchPath()
        {
            string path = SearchPath;
            if (string.IsNullOrEmpty(path)) return;
            if (string.Equals(path, _registeredSearchPath, StringComparison.OrdinalIgnoreCase))
                return;

            _registeredSearchPath = path;

            try
            {
                AddDllDirectory(Path.GetFullPath(path));

                string parent = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(parent))
                {
                    foreach (string sibling in new[] { "bin", "lib" })
                    {
                        string folder = Path.Combine(parent, sibling);
                        if (Directory.Exists(folder)) AddDllDirectory(folder);
                    }
                }
            }
            catch { /* the load below will report the real problem */ }
        }

        /// <summary>
        /// Opens a library, pulling in any dependencies it cannot find on its own.
        /// </summary>
        /// <remarks>
        /// This is the part that makes SearchPath actually mean something, and it
        /// took a failure in the Unity editor to notice it was needed.
        ///
        /// The ROS libraries are built without an RPATH. librcl.so alone lists
        /// eleven NEEDED entries - librmw.so, librcutils.so and the rest - and
        /// when the loader goes looking for those it uses the system search path,
        /// NOT the folder the library it is loading came from. So opening
        /// /opt/ros/jazzy/lib/librcl.so by absolute path still fails with
        ///
        ///     librcl_yaml_param_parser.so: cannot open shared object file
        ///
        /// unless LD_LIBRARY_PATH already covers /opt/ros/jazzy/lib. Which is
        /// exactly what we cannot arrange, because Linux reads that variable when
        /// the process starts and a game launched from an icon never had it.
        ///
        /// The way out: dlerror names the dependency it could not find. Load that
        /// one from our folder - recursively, since it will have dependencies of
        /// its own - and try again. Once a library is loaded with RTLD_GLOBAL the
        /// loader matches it by SONAME and stops searching the filesystem for it.
        ///
        /// From a terminal with ROS sourced none of this happens; the very first
        /// dlopen succeeds. It only matters when the environment is not set up
        /// for us, which is the normal case inside an editor.
        /// </remarks>
        private static IntPtr OpenWithDependencies(string path, int depth)
        {
            // On Windows the loader resolves dependencies for us once it is told
            // where to look - see OpenWindows. Recursing here would be pointless
            // anyway: LoadLibraryEx reports "module not found" without naming the
            // dependency it wanted, so there is nothing to recurse ON.
            if (IsWindows) return Open(path);

            // A library needing more than this many levels is not a library we
            // want to be loading, and the guard stops a cycle spinning forever.
            if (depth > 32) return IntPtr.Zero;

            string folder = Path.GetDirectoryName(path);

            for (int attempt = 0; attempt < 256; attempt++)
            {
                IntPtr handle = Open(path);
                if (handle != IntPtr.Zero) return handle;

                string missing = MissingDependency(LastError());
                if (missing == null) return IntPtr.Zero;   // a real failure, not a missing dep


                // Recurse, because the dependency has dependencies too.
                if (OpenWithDependencies(Path.Combine(folder, missing), depth + 1) == IntPtr.Zero)
                    return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Pulls the library name out of a dlerror like
        /// "librmw.so: cannot open shared object file: No such file or directory".
        /// Returns null when the error is about something else.
        /// </summary>
        private static string MissingDependency(string dlerror)
        {
            if (string.IsNullOrEmpty(dlerror)) return null;

            const string marker = ": cannot open shared object file";
            int at = dlerror.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;

            // Everything after the last "dlerror says: " prefix we added, up to
            // the marker, is the file name the loader wanted.
            string head = dlerror.Substring(0, at);
            int colon = head.LastIndexOf(": ", StringComparison.Ordinal);
            if (colon >= 0) head = head.Substring(colon + 2);

            head = head.Trim();

            // Only ever a bare file name. If it has a path separator it is the
            // library we asked for by name, not a dependency, and re-loading it
            // would loop.
            if (head.Length == 0 || head.IndexOf('/') >= 0) return null;
            if (head.IndexOf(".so", StringComparison.Ordinal) < 0) return null;

            return head;
        }

        private static IntPtr FindSymbol(IntPtr library, string name)
        {
            return IsWindows ? GetProcAddress(library, name) : dlsym(library, name);
        }

        private static string LastError()
        {
            if (IsWindows) return WindowsError(Marshal.GetLastWin32Error());

            IntPtr message = dlerror();
            return message == IntPtr.Zero ? "" : "dlerror says: " + Marshal.PtrToStringAnsi(message);
        }

        /// <summary>
        /// Turns a Windows error number into the sentence Windows would print.
        /// </summary>
        /// <remarks>
        /// This used to report the bare number, which for the only failure that
        /// actually happens here - 126, "the specified module could not be
        /// found" - told the reader nothing at all. Error 126 is also
        /// notoriously misleading: it is reported both when the DLL itself is
        /// missing AND when the DLL was found but one of its dependencies was
        /// not, so the extra sentence is added below.
        /// </remarks>
        private static string WindowsError(int code)
        {
            string text = null;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                int written = FormatMessageW(
                    FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                    IntPtr.Zero, code, 0, buffer, buffer.Capacity, IntPtr.Zero);
                if (written > 0) text = buffer.ToString().Trim();
            }
            catch { /* fall through to the bare number */ }

            string result = "Windows error " + code +
                            (string.IsNullOrEmpty(text) ? "" : ": " + text);

            if (code == ERROR_MOD_NOT_FOUND)
            {
                result += "\n\nError 126 means the DLL itself was missing OR one of the DLLs " +
                          "it depends on was. Windows does not say which, so check that the " +
                          "library folder is the one holding rcl.dll - for a conda or RoboStack " +
                          "install that is <env>\\Library\\bin, not <env>\\lib.";
            }
            else if (code == ERROR_BAD_EXE_FORMAT)
            {
                result += "\n\nError 193 almost always means a 32-bit / 64-bit mismatch. " +
                          "ROS 2 is 64-bit, so the process loading it must be too.";
            }

            return result;
        }

        // RTLD_NOW  = resolve everything immediately, so we fail here rather than
        //             halfway through the first publish.
        // RTLD_GLOBAL = let libraries loaded later see the symbols from this one.
        //             rcl loads the DDS implementation behind our back and that
        //             lookup fails without this flag.
        private const int RTLD_NOW = 2;
        private const int RTLD_GLOBAL = 0x100;

        // Since glibc 2.34 these actually live in libc, but libdl.so.2 is still
        // shipped as a small forwarding stub, so naming it works on old and new
        // systems alike.
        [DllImport("libdl.so.2", SetLastError = true)]
        private static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl.so.2", SetLastError = true)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libc", SetLastError = true)]
        private static extern int setenv(string name, string value, int overwrite);

        [DllImport("libc")]
        private static extern IntPtr getenv(string name);

        // Search flags, from libloaderapi.h. DEFAULT_DIRS is itself the union of
        // APPLICATION_DIR, SYSTEM32 and USER_DIRS, so registering a folder with
        // AddDllDirectory is enough to have it searched.
        private const int LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
        private const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        private const int LOAD_WITH_ALTERED_SEARCH_PATH    = 0x00000008;

        private const int ERROR_MOD_NOT_FOUND  = 126;
        private const int ERROR_BAD_EXE_FORMAT = 193;

        private const int FORMAT_MESSAGE_FROM_SYSTEM    = 0x00001000;
        private const int FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;

        // Unicode throughout. The ANSI entry point cannot express a path
        // containing characters outside the system code page, and ROS on Windows
        // usually lives under C:\Users\<name>, where the name is whatever the
        // person is actually called.
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, int flags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AddDllDirectory(string directory);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int FormatMessageW(int flags, IntPtr source, int messageId,
                                                 int languageId, System.Text.StringBuilder buffer,
                                                 int size, IntPtr arguments);

        // GetProcAddress is ANSI-only by design - symbol names are always ASCII.
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
