using System;
using System.Threading;
using SeaNav.Ros2;
using SeaNav.Core.Ros;

// Publishes SEANAV's own message type, seanav_msgs/msg/VesselState.
//
// This is the end-to-end check that a CUSTOM type works, which needs two
// separate things to be true and is why it earns its own demo:
//
//   1. The C# class exists      - tools/ros_msggen.py generates it
//   2. The native type support exists - colcon builds it
//
// Miss the second and the C# class still compiles and still serialises
// perfectly; only creating a publisher fails, with
// "Could not open libseanav_msgs__rosidl_typesupport_c.so".
//
// Check it with:
//   ros2 topic echo /seanav/vessel_state seanav_msgs/msg/VesselState
class Program
{
    static int Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 10.0;

        using (var ros = new Ros2Context())
        using (var node = ros.CreateNode("seanav_custom_type_demo"))
        {
            Ros2Publisher pub;
            try
            {
                pub = node.CreatePublisher("seanav_msgs/msg/VesselState",
                                           "/seanav/vessel_state", QosProfile.Default);
            }
            catch (DllNotFoundException e)
            {
                Console.Error.WriteLine("FAILED: " + e.Message.Split('\n')[0]);
                Console.Error.WriteLine();
                Console.Error.WriteLine("seanav_msgs has not been built. From the SEANAV repo:");
                Console.Error.WriteLine("    cd ros2 && colcon build --packages-select seanav_msgs");
                Console.Error.WriteLine("    source install/setup.bash");
                return 3;
            }

            var state = new seanav_msgs.msg.VesselState();
            state.Header.FrameId = "map";

            Console.WriteLine("READY publishing /seanav/vessel_state");

            var start = DateTime.UtcNow;
            int sent = 0;

            while ((DateTime.UtcNow - start).TotalSeconds < seconds && ros.Ok)
            {
                double t = (DateTime.UtcNow - start).TotalSeconds;

                // Values chosen to be individually recognisable in the echo
                // output, so a field landing in the wrong slot is obvious
                // rather than plausible.
                state.Header.Stamp.Sec = 1000 + (int)t;
                state.Header.Stamp.Nanosec = 250000000;
                state.Pose.Position.X = 12.5;
                state.Pose.Position.Y = -3.25;
                state.Twist.Linear.X = 4.125;
                state.RudderAngle = 0.35;
                state.PropellerRps = 7.5;
                state.Heave = -0.125;
                state.Roll = 0.0625;
                state.Pitch = -0.03125;
                state.FroudeNumber = 0.142;
                state.WaterDepth = 18.75;

                pub.Publish(RosCdr.Serialise(state));
                sent++;
                Thread.Sleep(100);
            }

            Console.WriteLine("SENT " + sent);
            pub.Dispose();
        }
        return 0;
    }
}
