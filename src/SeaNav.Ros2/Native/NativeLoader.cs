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

                string fileName = IsWindows ? name + ".dll" : "lib" + name + ".so";

                // Try our folder first, then let the OS look wherever it normally would.
                IntPtr handle = IntPtr.Zero;
                if (!string.IsNullOrEmpty(SearchPath))
                    handle = Open(Path.Combine(SearchPath, fileName));
                if (handle == IntPtr.Zero)
                    handle = Open(fileName);

                if (handle == IntPtr.Zero)
                {
                    string wherever = string.IsNullOrEmpty(SearchPath)
                        ? "the system library path"
                        : SearchPath + " or the system library path";

                    throw new DllNotFoundException(
                        "Could not open " + fileName + ". Looked in " + wherever + ".\n" +
                        "Either source your ROS 2 install before starting this program " +
                        "(e.g. 'source /opt/ros/jazzy/setup.bash'), or set " +
                        "NativeLoader.SearchPath to the folder that holds the ROS libraries.\n" +
                        LastError());
                }

                Opened[name] = handle;
                return handle;
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

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
