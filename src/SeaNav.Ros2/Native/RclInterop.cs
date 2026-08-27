using System;
using System.Runtime.InteropServices;

namespace SeaNav.Ros2.Native
{
    // The bridge between C# and ROS 2's C library, "rcl".
    //
    // Everything ROS does underneath - creating nodes, publishing, discovery -
    // is C functions in librcl.so. To call them from C# we need two things:
    //
    //   1. C# copies of the structs those functions expect, laid out in memory
    //      exactly the way the C compiler laid out the originals.
    //   2. A delegate for each function, so C# knows the argument types.
    //
    // Point 1 is where this file earns its keep. If a struct is even one byte
    // wrong, you don't get a nice error - you get a crash, or worse, code that
    // works today and corrupts memory next Tuesday. So none of these sizes were
    // worked out by counting fields on paper. A small C program was compiled
    // against the real ROS headers and asked to print sizeof() and offsetof()
    // for every struct here. On Jazzy / x86-64 the answers were:
    //
    //     context 24    node 16      publisher 8    subscription 8
    //     init options 8     node options 152       publisher options 152
    //     allocator 40       qos profile 88         serialized message 64
    //
    // If you port this to another ROS distribution, re-run that check. Don't
    // assume.
    public static class RclInterop
    {
        // =====================================================================
        // Structs
        // =====================================================================
        //
        // A warning that cost an afternoon: the small structs below MUST list
        // their fields. It is tempting to write
        //
        //     [StructLayout(LayoutKind.Sequential, Size = 8)]
        //     public struct Publisher { }
        //
        // since all we do is pass it around. That compiles and it is the right
        // size, but it is still wrong. On 64-bit Linux, a struct of 16 bytes or
        // less is handed back from a C function *in CPU registers*, and which
        // registers depends on what the fields are. An empty struct doesn't
        // describe any fields, so .NET falls back to returning it through
        // memory, and you get garbage.
        //
        // The symptom was rcl_get_zero_initialized_init_options() returning
        // junk, so the next call reported ALREADY_INIT. The layout was correct
        // the whole time; only the calling convention was wrong, and no error
        // code can tell you that.

        /// <summary>rcl_context_t. One per process. Created by rcl_init.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Context
        {
            public IntPtr GlobalArguments;
            public IntPtr Impl;
            public IntPtr InstanceId;
        }

        /// <summary>rcl_node_t. Knows its context and hides the rest behind Impl.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Node
        {
            public IntPtr Context;
            public IntPtr Impl;
        }

        /// <summary>rcl_publisher_t. Just a pointer to rcl's private data.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Publisher
        {
            public IntPtr Impl;
        }

        /// <summary>rcl_subscription_t. Same idea as Publisher.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Subscription
        {
            public IntPtr Impl;
        }

        /// <summary>rcl_init_options_t. Short-lived; used only during startup.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct InitOptions
        {
            public IntPtr Impl;
        }

        /// <summary>
        /// rcutils_allocator_t. Four function pointers (malloc, free, realloc,
        /// calloc) plus a spare pointer. We never build one by hand - we ask ROS
        /// for its default and pass that straight back in.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Allocator
        {
            public IntPtr Allocate;
            public IntPtr Deallocate;
            public IntPtr Reallocate;
            public IntPtr ZeroAllocate;
            public IntPtr State;
        }

        /// <summary>
        /// rcl_node_options_t. 152 bytes that we get from rcl and hand straight
        /// back without looking inside, so there is no reason to spell it out.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 152)]
        public struct NodeOptions
        {
            private IntPtr _opaque;
        }

        /// <summary>rmw_time_t. A duration, split into seconds and nanoseconds.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Duration
        {
            public ulong Seconds;
            public ulong Nanoseconds;
        }

        /// <summary>
        /// rmw_qos_profile_t - the quality-of-service settings. 88 bytes.
        /// The field order here is the order in the C header, and the compiler
        /// puts them at 0, 8, 16, 20, 24, 40, 56, 64 and 80. We checked.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct QosProfileNative
        {
            public int History;
            public UIntPtr Depth;
            public int Reliability;
            public int Durability;
            public Duration Deadline;
            public Duration Lifespan;
            public int Liveliness;
            public Duration LivelinessLeaseDuration;

            [MarshalAs(UnmanagedType.I1)]
            public bool AvoidRosNamespaceConventions;
        }

        /// <summary>
        /// rcl_publisher_options_t. Unlike the node options we do need to reach
        /// inside this one, because the QoS settings live at the front of it.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 152)]
        public struct PublisherOptions
        {
            public QosProfileNative Qos;           // bytes 0-87
            public Allocator Allocator;            // bytes 88-127
            public IntPtr RmwSpecificPayload;      // bytes 128-135
            public int RequireUniqueNetworkFlow;   // bytes 136-139

            [MarshalAs(UnmanagedType.I1)]
            public bool DisableLoanedMessage;
        }

        /// <summary>
        /// rcl_subscription_options_t. Note the size: 160, NOT 152 like the
        /// publisher options. They look like the same struct and they are not,
        /// and getting that wrong writes past the end of the buffer rcl handed
        /// you. Measured, like everything else here.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 160)]
        public struct SubscriptionOptions
        {
            public QosProfileNative Qos;      // bytes 0-87, same place as the publisher
            public Allocator Allocator;       // bytes 88-127
        }

        /// <summary>
        /// rcutils_error_string_t. A fixed 1024-byte buffer, returned by value.
        /// RCUTILS_ERROR_MESSAGE_MAX_LENGTH in rcutils/error_handling.h.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct ErrorString
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
            public string Message;
        }

        /// <summary>
        /// rcutils_uint8_array_t - a plain byte buffer with a length, a capacity
        /// and the allocator that owns it. This is what we hand to ROS when we
        /// publish an already-encoded message.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SerializedMessage
        {
            public IntPtr Buffer;
            public UIntPtr Length;
            public UIntPtr Capacity;
            public Allocator Allocator;
        }

        // =====================================================================
        // Function signatures
        // =====================================================================
        // One delegate per C function we call. The names match the C names so
        // you can look them up in the ROS documentation without translating.

        public delegate Allocator GetDefaultAllocatorFn();

        public delegate Context GetZeroInitializedContextFn();
        public delegate InitOptions GetZeroInitializedInitOptionsFn();
        public delegate int InitOptionsInitFn(ref InitOptions options, Allocator allocator);
        public delegate int InitOptionsFiniFn(ref InitOptions options);
        public delegate int InitFn(int argc, IntPtr argv, ref InitOptions options, ref Context context);
        public delegate int ShutdownFn(ref Context context);
        public delegate int ContextFiniFn(ref Context context);
        public delegate bool ContextIsValidFn(ref Context context);

        public delegate Node GetZeroInitializedNodeFn();
        public delegate NodeOptions NodeGetDefaultOptionsFn();
        public delegate int NodeInitFn(ref Node node,
                                       [MarshalAs(UnmanagedType.LPStr)] string name,
                                       [MarshalAs(UnmanagedType.LPStr)] string nameSpace,
                                       ref Context context,
                                       ref NodeOptions options);
        public delegate int NodeFiniFn(ref Node node);

        public delegate Publisher GetZeroInitializedPublisherFn();
        public delegate PublisherOptions PublisherGetDefaultOptionsFn();
        public delegate int PublisherInitFn(ref Publisher publisher,
                                            ref Node node,
                                            IntPtr typeSupport,
                                            [MarshalAs(UnmanagedType.LPStr)] string topic,
                                            ref PublisherOptions options);
        public delegate int PublisherFiniFn(ref Publisher publisher, ref Node node);
        public delegate int PublishSerializedFn(ref Publisher publisher,
                                                ref SerializedMessage message,
                                                IntPtr allocation);

        public delegate Subscription GetZeroInitializedSubscriptionFn();
        public delegate SubscriptionOptions SubscriptionGetDefaultOptionsFn();
        public delegate int SubscriptionInitFn(ref Subscription subscription,
                                               ref Node node,
                                               IntPtr typeSupport,
                                               [MarshalAs(UnmanagedType.LPStr)] string topic,
                                               ref SubscriptionOptions options);
        public delegate int SubscriptionFiniFn(ref Subscription subscription, ref Node node);
        public delegate int TakeSerializedFn(ref Subscription subscription,
                                             ref SerializedMessage message,
                                             IntPtr messageInfo,
                                             IntPtr allocation);

        // Buffer management for the message rcl fills in when we take one.
        public delegate int Uint8ArrayInitFn(ref SerializedMessage message,
                                             UIntPtr capacity,
                                             ref Allocator allocator);
        public delegate int Uint8ArrayFiniFn(ref SerializedMessage message);

        // Careful with these two. In the ROS headers, rcl_get_error_string and
        // rcl_reset_error are #defines pointing at rcutils_get_error_string and
        // rcutils_reset_error, which live in librcutils, not librcl. Looking
        // for "rcl_reset_error" as a symbol finds nothing and throws.
        //
        // And rcutils_get_error_string does not return a char* - it returns a
        // struct holding a fixed 1024-byte char array, by value. Declaring it as
        // IntPtr compiles and gives nonsense.
        public delegate ErrorString GetErrorStringFn();
        public delegate void ResetErrorFn();
        public delegate IntPtr GetTypeSupportFn();

        // =====================================================================
        // Getting hold of the libraries
        // =====================================================================

        private static IntPtr _rcl;
        private static IntPtr _rcutils;

        /// <summary>librcl - the main ROS 2 client library.</summary>
        public static IntPtr Rcl
        {
            get
            {
                if (_rcl == IntPtr.Zero) _rcl = NativeLoader.Load("rcl");
                return _rcl;
            }
        }

        /// <summary>librcutils - small helpers, mainly the allocator.</summary>
        public static IntPtr Rcutils
        {
            get
            {
                if (_rcutils == IntPtr.Zero) _rcutils = NativeLoader.Load("rcutils");
                return _rcutils;
            }
        }

        /// <summary>Grabs a function out of librcl.</summary>
        public static T Fn<T>(string name) where T : class
        {
            return NativeLoader.Function<T>(Rcl, name);
        }

        /// <summary>Grabs a function out of librcutils.</summary>
        public static T UtilsFn<T>(string name) where T : class
        {
            return NativeLoader.Function<T>(Rcutils, name);
        }

        /// <summary>
        /// Looks up the "type support" for a message type, e.g. "sensor_msgs/msg/Imu".
        /// </summary>
        /// <remarks>
        /// Type support is a little description ROS keeps about a message: its
        /// name, its fields, how to pack it. A publisher needs one so ROS can
        /// tell other nodes what's on the topic.
        ///
        /// The nice part: every message package installed on your machine
        /// already exports this as a normal C function, and the function name is
        /// completely predictable from the message name. For sensor_msgs/msg/Imu
        /// it is
        ///
        ///     rosidl_typesupport_c__get_message_type_support_handle__sensor_msgs__msg__Imu
        ///
        /// So we build that string, look it up, and we're done. One lookup per
        /// message type, once, when the publisher is created.
        ///
        /// This is the single reason this library doesn't need a code generator.
        /// The usual approach generates a C# class and a small C library for
        /// every message type you might ever send. On a normal Jazzy install
        /// that comes to about 1,500 extra shared libraries.
        ///
        /// Small caveat: this naming pattern is a convention of the ROS code
        /// generator, not something the ROS team promises to keep forever. It
        /// has held for every release so far. If it ever changes, this method is
        /// the only place that needs fixing.
        /// </remarks>
        public static IntPtr MessageTypeSupport(string messageType)
        {
            if (string.IsNullOrEmpty(messageType))
                throw new ArgumentNullException(nameof(messageType));

            // "sensor_msgs/msg/Imu" -> package "sensor_msgs", kind "msg", name "Imu"
            string[] parts = messageType.Split('/');
            if (parts.Length != 3)
            {
                throw new ArgumentException(
                    "Message types look like 'package/msg/Name'. Got: " + messageType,
                    nameof(messageType));
            }

            string package = parts[0];
            string kind = parts[1];
            string name = parts[2];

            IntPtr library = NativeLoader.Load(package + "__rosidl_typesupport_c");
            string function = "rosidl_typesupport_c__get_message_type_support_handle__"
                              + package + "__" + kind + "__" + name;

            GetTypeSupportFn get = NativeLoader.Function<GetTypeSupportFn>(library, function);
            IntPtr handle = get();

            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Type support for " + messageType + " came back null.");

            return handle;
        }

        /// <summary>
        /// What rcl_take_serialized_message returns when the queue is empty.
        /// Not an error - just "nothing has arrived yet".
        /// </summary>
        public const int SubscriptionTakeFailed = 401;

        /// <summary>
        /// Every rcl function returns a number: 0 means it worked. This turns
        /// anything else into an exception, with ROS's own explanation attached.
        /// </summary>
        public static void Check(int result, string whatWeTried)
        {
            if (result == 0) return;

            string rosSays = "";
            try
            {
                ErrorString text = UtilsFn<GetErrorStringFn>("rcutils_get_error_string")();
                if (!string.IsNullOrEmpty(text.Message))
                    rosSays = "\nROS says: " + text.Message;

                ClearError();
            }
            catch
            {
                // Asking for the error message must never replace the real error.
            }

            throw new Ros2Exception(
                whatWeTried + " failed: " + Explain(result) + rosSays, result);
        }

        /// <summary>
        /// Clears the error rcl recorded. There is one slot per thread, so if you
        /// leave a stale message in it the next genuine failure reports the old
        /// one instead of its own.
        /// </summary>
        public static void ClearError()
        {
            UtilsFn<ResetErrorFn>("rcutils_reset_error")();
        }

        // The return codes you are most likely to hit, in plain words.
        private static string Explain(int code)
        {
            switch (code)
            {
                case 1: return "generic error";
                case 2: return "timed out";
                case 10: return "out of memory";
                case 11: return "invalid argument";
                case 12: return "not supported";
                case 100: return "already initialised";
                case 101: return "not initialised";
                case 102: return "the DDS implementation doesn't match";
                case 103: return "that topic name isn't valid";
                case 200: return "the node is invalid";
                case 300: return "the publisher is invalid";
                case 400: return "the subscription is invalid";
                case 401: return "nothing to take";
                default: return "error code " + code;
            }
        }
    }

    /// <summary>Something in ROS 2 went wrong. Carries rcl's own return code.</summary>
    public sealed class Ros2Exception : Exception
    {
        /// <summary>The raw rcl_ret_t value, in case you want to switch on it.</summary>
        public int ReturnCode { get; }

        public Ros2Exception(string message, int returnCode) : base(message)
        {
            ReturnCode = returnCode;
        }
    }
}
