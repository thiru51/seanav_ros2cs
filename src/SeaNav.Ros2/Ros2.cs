using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SeaNav.Ros2.Native;

namespace SeaNav.Ros2
{
    // The friendly layer. Everything below this file is pointers and structs;
    // everything here is ordinary C# you can read.
    //
    // The shape mirrors ROS itself:
    //
    //     Ros2Context   ->  starts and stops ROS for this process (one of these)
    //       Ros2Node    ->  a named participant other nodes can see
    //         Ros2Publisher  ->  sends messages on one topic
    //
    // Dispose the context and it cleans up everything under it, so a `using`
    // block is usually all the lifetime management you need.

    /// <summary>
    /// Starts ROS 2 for this process and shuts it down again. Make one of these
    /// before anything else, and keep it alive for as long as you're publishing.
    /// </summary>
    public sealed class Ros2Context : IDisposable
    {
        private RclInterop.Context _context;
        private readonly List<Ros2Node> _nodes = new List<Ros2Node>();
        private bool _disposed;

        /// <summary>
        /// ROS's default memory allocator. Everything we create shares it, and
        /// we never build one ourselves - we just pass this one along.
        /// </summary>
        public RclInterop.Allocator Allocator { get; }

        /// <summary>
        /// Starts ROS 2.
        /// </summary>
        /// <param name="rosLibraryFolder">
        /// Where the ROS libraries live, e.g. "/opt/ros/jazzy/lib". You can leave
        /// this out when you've already run "source /opt/ros/jazzy/setup.bash".
        /// Inside Unity you almost certainly need to pass it, because the game
        /// doesn't get to fix its own library search path after it has started.
        /// </param>
        public Ros2Context(string rosLibraryFolder = null)
        {
            if (!string.IsNullOrEmpty(rosLibraryFolder))
                NativeLoader.SearchPath = rosLibraryFolder;

            Allocator = RclInterop.UtilsFn<RclInterop.GetDefaultAllocatorFn>(
                "rcutils_get_default_allocator")();

            // ROS wants a blank context and a blank options struct, then fills
            // them in. Asking for the blank ones rather than zeroing our own
            // matters: rcl checks for its own sentinel values.
            _context = RclInterop.Fn<RclInterop.GetZeroInitializedContextFn>(
                "rcl_get_zero_initialized_context")();

            RclInterop.InitOptions options = RclInterop.Fn<RclInterop.GetZeroInitializedInitOptionsFn>(
                "rcl_get_zero_initialized_init_options")();

            RclInterop.Check(
                RclInterop.Fn<RclInterop.InitOptionsInitFn>("rcl_init_options_init")(ref options, Allocator),
                "Setting up ROS init options");

            try
            {
                // argc/argv would let ROS parse command line arguments like
                // __ns:= remappings. We pass none; add them here if you need them.
                RclInterop.Check(
                    RclInterop.Fn<RclInterop.InitFn>("rcl_init")(0, IntPtr.Zero, ref options, ref _context),
                    "Starting ROS 2");
            }
            finally
            {
                // rcl_init copies what it needs, so the options are ours to free
                // either way - including when init failed.
                RclInterop.Fn<RclInterop.InitOptionsFiniFn>("rcl_init_options_fini")(ref options);
            }
        }

        /// <summary>
        /// True while ROS is running. Goes false after Dispose, or if something
        /// else shut ROS down (Ctrl+C, for instance).
        /// </summary>
        public bool Ok
        {
            get
            {
                if (_disposed) return false;
                return RclInterop.Fn<RclInterop.ContextIsValidFn>("rcl_context_is_valid")(ref _context);
            }
        }

        /// <summary>
        /// Creates a node. This is what shows up in "ros2 node list".
        /// </summary>
        /// <param name="name">Node name, no leading slash. Letters, digits and underscores.</param>
        /// <param name="nameSpace">Optional namespace to group nodes under.</param>
        public Ros2Node CreateNode(string name, string nameSpace = "")
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Context));

            Ros2Node node = new Ros2Node(this, name, nameSpace);
            _nodes.Add(node);
            return node;
        }

        // Handed to rcl functions that need to write into our context.
        internal ref RclInterop.Context Handle => ref _context;

        internal void NodeWasDisposed(Ros2Node node)
        {
            _nodes.Remove(node);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Nodes hold handles that point into this context, so they have to go
            // first. We copy the list because disposing a node removes it from ours
            // and you can't modify a list while walking it.
            foreach (Ros2Node node in _nodes.ToArray())
                node.Dispose();
            _nodes.Clear();

            RclInterop.Fn<RclInterop.ShutdownFn>("rcl_shutdown")(ref _context);
            RclInterop.Fn<RclInterop.ContextFiniFn>("rcl_context_fini")(ref _context);
        }
    }

    /// <summary>
    /// A ROS 2 node. Nodes own publishers and subscriptions, and they're the
    /// thing other programs on the network discover.
    /// </summary>
    public sealed class Ros2Node : IDisposable
    {
        private readonly Ros2Context _context;
        private RclInterop.Node _node;
        private readonly List<IDisposable> _children = new List<IDisposable>();
        private bool _disposed;

        /// <summary>The name you gave it.</summary>
        public string Name { get; }

        /// <summary>Its namespace, or empty string for none.</summary>
        public string Namespace { get; }

        internal Ros2Node(Ros2Context context, string name, string nameSpace)
        {
            _context = context;
            Name = name;
            Namespace = nameSpace ?? string.Empty;

            _node = RclInterop.Fn<RclInterop.GetZeroInitializedNodeFn>("rcl_get_zero_initialized_node")();
            RclInterop.NodeOptions options =
                RclInterop.Fn<RclInterop.NodeGetDefaultOptionsFn>("rcl_node_get_default_options")();

            RclInterop.Check(
                RclInterop.Fn<RclInterop.NodeInitFn>("rcl_node_init")(
                    ref _node, name, Namespace, ref context.Handle, ref options),
                "Creating node '" + name + "'");
        }

        /// <summary>
        /// Creates a publisher for one topic.
        /// </summary>
        /// <param name="messageType">Full ROS type name, e.g. "sensor_msgs/msg/Imu".</param>
        /// <param name="topic">Topic name. Start it with "/" to ignore the namespace.</param>
        /// <param name="qos">
        /// Delivery settings. Leave null for ROS defaults, or use QosProfile.SensorData
        /// for anything high-rate. Worth getting right - see the notes on QosProfile.
        /// </param>
        public Ros2Publisher CreatePublisher(string messageType, string topic, QosProfile qos = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Node));

            Ros2Publisher publisher = new Ros2Publisher(this, messageType, topic, qos);
            _children.Add(publisher);
            return publisher;
        }

        internal ref RclInterop.Node Handle => ref _node;

        internal Ros2Context Context => _context;

        internal void ChildWasDisposed(IDisposable child)
        {
            _children.Remove(child);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (IDisposable child in _children.ToArray())
                child.Dispose();
            _children.Clear();

            RclInterop.Fn<RclInterop.NodeFiniFn>("rcl_node_fini")(ref _node);
            _context.NodeWasDisposed(this);
        }
    }

    /// <summary>
    /// Publishes messages on a topic.
    /// </summary>
    /// <remarks>
    /// You hand this class bytes, not objects. The bytes are CDR - the format
    /// ROS uses on the wire - and SeaNav.Core.Ros.CdrWriter produces them.
    ///
    /// Most C# ROS libraries work the other way round: you fill in a generated
    /// message class, and the library copies it field by field into a C struct,
    /// which the DDS layer then walks again to produce those same CDR bytes.
    /// That works fine and it isn't slow, but it needs a code generator and a
    /// small compiled library for every message type in existence.
    ///
    /// Publishing the bytes directly skips all of that. The trade-off is that
    /// this route covers topics only. Services can't work this way, because rcl
    /// has no "send this request as raw bytes" function - a service call needs
    /// the real C struct.
    /// </remarks>
    public sealed class Ros2Publisher : IDisposable
    {
        private readonly Ros2Node _node;
        private RclInterop.Publisher _publisher;
        private RclInterop.SerializedMessage _message;

        // We keep one unmanaged buffer and reuse it. Publishing at 200 Hz should
        // not allocate, so the buffer only grows and never shrinks.
        private IntPtr _buffer;
        private int _bufferSize;

        private bool _disposed;

        /// <summary>The message type, e.g. "sensor_msgs/msg/Imu".</summary>
        public string MessageType { get; }

        /// <summary>The topic name, as you gave it.</summary>
        public string Topic { get; }

        internal Ros2Publisher(Ros2Node node, string messageType, string topic, QosProfile qos)
        {
            _node = node;
            MessageType = messageType;
            Topic = topic;

            IntPtr typeSupport = RclInterop.MessageTypeSupport(messageType);

            _publisher = RclInterop.Fn<RclInterop.GetZeroInitializedPublisherFn>(
                "rcl_get_zero_initialized_publisher")();

            RclInterop.PublisherOptions options =
                RclInterop.Fn<RclInterop.PublisherGetDefaultOptionsFn>("rcl_publisher_get_default_options")();

            // Start from ROS's defaults and change only what the caller asked for.
            if (qos != null)
                qos.CopyInto(ref options);

            RclInterop.Check(
                RclInterop.Fn<RclInterop.PublisherInitFn>("rcl_publisher_init")(
                    ref _publisher, ref node.Handle, typeSupport, topic, ref options),
                "Creating publisher on '" + topic + "' for " + messageType);

            _message = new RclInterop.SerializedMessage { Allocator = node.Context.Allocator };
        }

        /// <summary>Sends one message.</summary>
        /// <param name="cdrBytes">
        /// The encoded message, including its four-byte encapsulation header.
        /// RosCdr.Serialise gives you exactly this.
        /// </param>
        public void Publish(byte[] cdrBytes)
        {
            if (cdrBytes == null) throw new ArgumentNullException(nameof(cdrBytes));
            Publish(cdrBytes, cdrBytes.Length);
        }

        /// <summary>
        /// Sends the first <paramref name="length"/> bytes of a buffer. Useful if
        /// you encode into a bigger scratch array and don't want to trim it.
        /// </summary>
        public void Publish(byte[] cdrBytes, int length)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Publisher));
            if (cdrBytes == null) throw new ArgumentNullException(nameof(cdrBytes));

            if (length < 4 || length > cdrBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length),
                    "A CDR message is at least 4 bytes - that's the encapsulation header on its own.");
            }

            MakeRoomFor(length);
            Marshal.Copy(cdrBytes, 0, _buffer, length);
            _message.Length = (UIntPtr)length;

            RclInterop.Check(
                RclInterop.Fn<RclInterop.PublishSerializedFn>("rcl_publish_serialized_message")(
                    ref _publisher, ref _message, IntPtr.Zero),
                "Publishing on '" + Topic + "'");
        }

        // Grows the unmanaged buffer by doubling, so repeated publishing settles
        // on one allocation rather than reallocating every time.
        private void MakeRoomFor(int bytes)
        {
            if (bytes <= _bufferSize) return;

            int newSize = _bufferSize == 0 ? Math.Max(bytes, 256) : _bufferSize;
            while (newSize < bytes)
                newSize *= 2;

            IntPtr bigger = Marshal.AllocHGlobal(newSize);
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);

            _buffer = bigger;
            _bufferSize = newSize;

            _message.Buffer = _buffer;
            _message.Capacity = (UIntPtr)newSize;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RclInterop.Fn<RclInterop.PublisherFiniFn>("rcl_publisher_fini")(ref _publisher, ref _node.Handle);

            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            _node.ChildWasDisposed(this);
        }
    }
}
