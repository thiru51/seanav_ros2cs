using System;
using SeaNav.Ros2.Native;

namespace SeaNav.Ros2
{
    // Quality of Service, which is DDS-speak for "how hard should we try to
    // deliver this?".
    //
    // Read this bit even if you skip the rest of the file, because it catches
    // people out: if a publisher and a subscriber disagree about QoS, they
    // simply don't connect. No error, no warning, no messages. Your publisher
    // says it published, "ros2 topic list" shows the topic, and the subscriber
    // sits there forever.
    //
    // The usual version of this: sensors publish best-effort, someone subscribes
    // with the default (reliable), and nothing arrives.

    /// <summary>How many old messages to keep around. Matches rmw_qos_history_policy_e.</summary>
    public enum HistoryPolicy
    {
        SystemDefault = 0,

        /// <summary>Keep the last N, throw away the rest. What you almost always want.</summary>
        KeepLast = 1,

        /// <summary>Keep everything. Uses unbounded memory if a subscriber falls behind.</summary>
        KeepAll = 2
    }

    /// <summary>Whether to chase up lost messages. Matches rmw_qos_reliability_policy_e.</summary>
    public enum ReliabilityPolicy
    {
        SystemDefault = 0,

        /// <summary>Resend until it arrives. Right for commands, waypoints, anything you can't drop.</summary>
        Reliable = 1,

        /// <summary>Send once, don't look back. Right for sensor streams.</summary>
        BestEffort = 2
    }

    /// <summary>Whether someone joining late gets old messages. Matches rmw_qos_durability_policy_e.</summary>
    public enum DurabilityPolicy
    {
        SystemDefault = 0,

        /// <summary>Hold the last message for whoever subscribes next. ROS 1 called this "latched".</summary>
        TransientLocal = 1,

        /// <summary>Late subscribers only see what comes after they arrive.</summary>
        Volatile = 2
    }

    /// <summary>How a node proves it's still alive. Matches rmw_qos_liveliness_policy_e.</summary>
    public enum LivelinessPolicy
    {
        SystemDefault = 0,

        /// <summary>ROS handles it. Fine for nearly everything.</summary>
        Automatic = 1,

        /// <summary>You promise to assert liveliness yourself, per topic.</summary>
        ManualByTopic = 3
    }

    /// <summary>
    /// Delivery settings for a publisher. Start from one of the presets below
    /// rather than building one from scratch - the presets match the profiles
    /// ROS itself ships, so subscribers written the normal way will connect.
    /// </summary>
    public sealed class QosProfile
    {
        /// <summary>Keep the last few messages, or all of them.</summary>
        public HistoryPolicy History = HistoryPolicy.KeepLast;

        /// <summary>How many to keep, when History is KeepLast.</summary>
        public int Depth = 10;

        /// <summary>Resend lost messages, or don't.</summary>
        public ReliabilityPolicy Reliability = ReliabilityPolicy.Reliable;

        /// <summary>Whether late subscribers get anything that was sent before they joined.</summary>
        public DurabilityPolicy Durability = DurabilityPolicy.Volatile;

        /// <summary>Liveliness assertion. The default is nearly always right.</summary>
        public LivelinessPolicy Liveliness = LivelinessPolicy.SystemDefault;

        /// <summary>
        /// What ROS uses if you don't say otherwise: keep the last 10, reliable,
        /// nothing for late joiners.
        /// </summary>
        public static QosProfile Default
        {
            get { return new QosProfile(); }
        }

        /// <summary>
        /// For sensors: IMU, GPS, LiDAR, cameras. Best effort with a short queue.
        /// </summary>
        /// <remarks>
        /// Dropping the odd sample is better than resending a stale one. At 200 Hz
        /// a message that needed retransmitting is already out of date by the time
        /// it lands, and you've paid for the retransmit as well. This matches ROS's
        /// own rmw_qos_profile_sensor_data.
        /// </remarks>
        public static QosProfile SensorData
        {
            get
            {
                return new QosProfile
                {
                    History = HistoryPolicy.KeepLast,
                    Depth = 5,
                    Reliability = ReliabilityPolicy.BestEffort,
                    Durability = DurabilityPolicy.Volatile
                };
            }
        }

        /// <summary>
        /// For things published once and needed by everyone afterwards: a map, a
        /// vessel description, a static transform. Whoever subscribes later still
        /// gets the last value.
        /// </summary>
        public static QosProfile Latched
        {
            get
            {
                return new QosProfile
                {
                    History = HistoryPolicy.KeepLast,
                    Depth = 1,
                    Reliability = ReliabilityPolicy.Reliable,
                    Durability = DurabilityPolicy.TransientLocal
                };
            }
        }

        /// <summary>
        /// Copies these settings into the options struct rcl handed us.
        /// </summary>
        /// <remarks>
        /// We only touch the fields this class knows about. The deadline,
        /// lifespan and lease durations keep whatever ROS put there, which is a
        /// special "unspecified" value. Writing zeros into them would not mean
        /// "no limit" - it would mean "a limit of zero", which is much stricter
        /// and would quietly stop things matching.
        /// </remarks>
        internal void CopyInto(ref RclInterop.PublisherOptions options)
        {
            if (Depth < 0)
                throw new ArgumentOutOfRangeException(nameof(Depth), "Queue depth can't be negative.");

            options.Qos.History = (int)History;
            options.Qos.Depth = (UIntPtr)Depth;
            options.Qos.Reliability = (int)Reliability;
            options.Qos.Durability = (int)Durability;
            options.Qos.Liveliness = (int)Liveliness;
        }

        public override string ToString()
        {
            return History + "(" + Depth + ") " + Reliability + " " + Durability;
        }
    }
}
