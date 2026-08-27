// A minimal example: publish an IMU message on /seanav/imu.
//
// Run it, then open another terminal, source ROS, and watch the messages:
//
//     ros2 topic echo /seanav/imu sensor_msgs/msg/Imu
//
// You can also check it's a proper ROS node like any other:
//
//     ros2 node list          ->  /seanav_talker
//     ros2 topic info /seanav/imu
//
// Nothing was generated to make this work. No message classes were compiled,
// no colcon workspace, no extra .so files. The message type is looked up by
// name at runtime, and the bytes come from SeaNav's CDR writer.

using System;
using System.Threading;
using SeaNav.Core.Ros;
using SeaNav.Ros2;

internal static class Program
{
    private static void Main(string[] args)
    {
        // How many messages to send before quitting. Handy when testing.
        int howMany = 50;
        if (args.Length > 0)
            int.TryParse(args[0], out howMany);

        // Ros2Context starts ROS; Ros2Node is what appears on the network.
        // Disposing the context tidies up everything underneath it, so a
        // `using` block is all the cleanup you need.
        using (Ros2Context ros = new Ros2Context())
        using (Ros2Node node = ros.CreateNode("seanav_talker"))
        {
            // SensorData rather than the default QoS. IMU data at this rate is
            // better dropped than resent - see the notes in QosProfile.cs.
            Ros2Publisher publisher = node.CreatePublisher(
                "sensor_msgs/msg/Imu", "/seanav/imu", QosProfile.SensorData);

            Console.WriteLine("Node /" + node.Name + " is publishing " + publisher.MessageType);
            Console.WriteLine("Topic: " + publisher.Topic);
            Console.WriteLine("QoS:   " + QosProfile.SensorData);
            Console.WriteLine();

            // Build the message once and change the numbers each time round the
            // loop, rather than allocating a new one every message.
            RosImu imu = new RosImu();
            imu.Header.FrameId = "seanav_imu";

            // -1 in the first slot of the orientation covariance is how a ROS
            // IMU says "I don't estimate orientation". That convention is
            // written into the message definition's own comments.
            imu.OrientationCovariance[0] = RosImu.NoEstimate;

            for (int i = 0; i < howMany && ros.Ok; i++)
            {
                // Pretend this is a vessel turning slowly while rolling in a
                // beam sea. The numbers are made up, but they change every
                // message - so if the publisher ever gets stuck, you'll see it.
                double seconds = i * 0.05;
                double rollPeriod = 8.0;

                double roll = 0.05 * Math.Sin(2.0 * Math.PI * seconds / rollPeriod);
                double rollRate = 0.05 * (2.0 * Math.PI / rollPeriod)
                                       * Math.Cos(2.0 * Math.PI * seconds / rollPeriod);
                double yaw = 0.02 * seconds;

                imu.Header.Stamp = RosTime.FromSeconds(seconds);
                imu.Orientation = RosQuaternion.FromRollPitchYaw(roll, 0.0, yaw);
                imu.AngularVelocity = new RosVector3(rollRate, 0.0, 0.02);

                // What an accelerometer really reads when the vessel is rolling
                // but not accelerating: gravity, rotated into the body frame.
                const double g = 9.80665;
                imu.LinearAcceleration = new RosVector3(0.0, g * Math.Sin(roll), g * Math.Cos(roll));

                // Encode to CDR bytes and hand them to ROS.
                publisher.Publish(RosCdr.Serialise(imu));

                Thread.Sleep(50);   // 20 Hz
            }

            Console.WriteLine("Sent " + howMany + " messages.");
        }
    }
}
