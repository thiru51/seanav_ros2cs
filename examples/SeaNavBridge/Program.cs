// Runs an actual SEANAV scenario and publishes it to ROS 2.
//
// This is the whole chain end to end: the MMG solver moves the vessel, the
// sensor suite produces IMU and GNSS readings at their own rates, the bridge
// turns those into ROS messages, and the binding puts them on real topics.
//
// Watch it from another terminal:
//
//     ros2 topic echo /seanav/odom nav_msgs/msg/Odometry
//     ros2 topic echo /seanav/imu sensor_msgs/msg/Imu
//     ros2 topic hz /seanav/imu

using System;
using System.Collections.Generic;
using System.Threading;
using SeaNav.Core;
using SeaNav.Core.Ros;
using SeaNav.Core.Scenario;
using SeaNav.Core.Sensors;
using SeaNav.Ros2;

internal static class Program
{
    private static void Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 20.0;
        const double dt = 0.01;   // 100 Hz, matching the physics

        // A vessel making way, with a rudder over so the track actually curves.
        // A straight line would hide any error in the heading conversion.
        var vessel = new VesselRuntime
        {
            Id = "own",
            State = new ShipState(6.17, 0.0, 0.0, 0.0, 0.0, 0.0),
            Solver = new MmgSolver(VesselParameters.Kvlcc2Model110()),
            Sensors = new SensorSuite(seed: 20260827, imuRateHz: 100.0, gnssRateHz: 5.0)
        };

        var bridge = new SeaNavRosBridge();

        using (Ros2Context ros = new Ros2Context())
        using (Ros2Node node = ros.CreateNode("seanav_sim"))
        {
            // One publisher per topic the bridge might use. Created up front so
            // the first message doesn't pay for discovery.
            var publishers = new Dictionary<string, Ros2Publisher>
            {
                { bridge.OdometryTopic, node.CreatePublisher("nav_msgs/msg/Odometry", bridge.OdometryTopic) },
                { bridge.ImuTopic, node.CreatePublisher("sensor_msgs/msg/Imu", bridge.ImuTopic, QosProfile.SensorData) },
                { bridge.GnssTopic, node.CreatePublisher("sensor_msgs/msg/NavSatFix", bridge.GnssTopic, QosProfile.SensorData) },
                { bridge.DvlTopic, node.CreatePublisher("geometry_msgs/msg/TwistWithCovarianceStamped", bridge.DvlTopic, QosProfile.SensorData) }
            };

            Console.WriteLine("Node /seanav_sim publishing:");
            foreach (string topic in publishers.Keys) Console.WriteLine("  " + topic);
            Console.WriteLine();

            var counts = new Dictionary<string, int>();
            int steps = (int)(seconds / dt);
            double time = 0;

            for (int i = 0; i < steps && ros.Ok; i++)
            {
                // 20 degrees of starboard rudder after the first two seconds, so
                // the vessel turns and the heading conversion gets exercised.
                double rudder = time > 2.0 ? 20.0 * Math.PI / 180.0 : 0.0;
                vessel.State = vessel.Solver.Step(vessel.State, new ControlInput(rudder, 10.4), dt);
                time += dt;

                // Feed the sensors the truth and collect what they produced.
                var kinematics = VesselKinematics.FromShipState(
                    vessel.State.X, vessel.State.Y, vessel.State.Psi,
                    vessel.State.U, vessel.State.V, vessel.State.R);
                vessel.LatestSensors = vessel.Sensors.Step(dt, kinematics);

                foreach (RosOutput output in bridge.Collect(vessel, time))
                {
                    if (!publishers.TryGetValue(output.Topic, out Ros2Publisher pub)) continue;
                    pub.Publish(RosCdr.Serialise(output.Message));

                    counts.TryGetValue(output.Topic, out int n);
                    counts[output.Topic] = n + 1;
                }

                // Run roughly in real time so 'ros2 topic hz' shows a sane rate.
                if (i % 10 == 0) Thread.Sleep(10);
            }

            Console.WriteLine("After " + seconds + " s of simulation:");
            foreach (var pair in counts)
                Console.WriteLine("  " + pair.Key + "  " + pair.Value + " messages");

            Console.WriteLine();
            Console.WriteLine("Vessel ended at north " + vessel.State.X.ToString("F1") +
                              " m, east " + vessel.State.Y.ToString("F1") +
                              " m, heading " + (vessel.State.Psi * 180.0 / Math.PI).ToString("F1") + " deg");
        }
    }
}
