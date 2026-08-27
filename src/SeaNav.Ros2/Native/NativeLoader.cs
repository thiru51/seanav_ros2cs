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

                if (handle == IntPtr.Zero)
                {
                    string wherever = string.IsNullOrEmpty(SearchPath)
                        ? "the system library path"
                        : SearchPath + " or the system library path";

                    throw new DllNotFoundException(
                        "Could not open " + fileName + ". Looked in " + wherever + ".\n\n" +
                        "SOURCE ROS 2 BEFORE STARTING THIS PROCESS:\n" +
                        "    source /opt/ros/<distro>/setup.bash\n\n" +
                        "Setting a library folder gets some of the way and is not enough on its " +
                        "own. ROS 2 is built without an RPATH and opens libraries by bare name " +
                        "while it runs, so it needs LD_LIBRARY_PATH - which Linux fixes when a " +
                        "process starts and nothing can change afterwards.\n" +
                        "For Unity: launch the editor from a terminal that has sourced ROS, or " +
                        "add the source line to ~/.profile and log out and back in.\n\n" +
                        LastError());
                }

                Opened[name] = handle;
                return handle;
            }
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
            return IsWindows ? LoadLibrary(path) : dlopen(path, RTLD_NOW | RTLD_GLOBAL);
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
            if (IsWindows)
                return "Windows error code: " + Marshal.GetLastWin32Error();

            IntPtr message = dlerror();
            return message == IntPtr.Zero ? "" : "dlerror says: " + Marshal.PtrToStringAnsi(message);
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

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
