// Listens on /seanav/cmd_vel and prints what arrives.
//
// Try it with ROS's own publisher, which knows nothing about this code:
//
//     ros2 topic pub /seanav/cmd_vel geometry_msgs/msg/Twist \
//       "{linear: {x: 6.17}, angular: {z: -0.015}}"
//
// If the numbers below match what you typed, then our CDR decoder agrees with
// the reference implementation - which is the point of the exercise.

using System;
using System.Threading;
using SeaNav.Core.Ros;
using SeaNav.Ros2;

internal static class Program
{
    private static void Main(string[] args)
    {
        // Stop after this many messages, or after the timeout, so the script
        // that runs this in CI doesn't hang forever.
        int wanted = args.Length > 0 ? int.Parse(args[0]) : 1;
        double timeoutSeconds = args.Length > 1 ? double.Parse(args[1]) : 30.0;

        using (Ros2Context ros = new Ros2Context())
        using (Ros2Node node = ros.CreateNode("seanav_listener"))
        {
            Ros2Subscription sub = node.CreateSubscription(
                "geometry_msgs/msg/Twist", "/seanav/cmd_vel");

            Console.WriteLine("Listening on " + sub.Topic + " for " + sub.MessageType);

            DateTime giveUpAt = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            RosTwist twist = new RosTwist();

            while (sub.Received < wanted && DateTime.UtcNow < giveUpAt && ros.Ok)
            {
                byte[] bytes;
                if (!sub.TryTake(out bytes))
                {
                    // Nothing waiting. Sleep a little rather than spinning the CPU.
                    Thread.Sleep(10);
                    continue;
                }

                RosCdr.Deserialise(bytes, twist);

                Console.WriteLine(
                    "got " + bytes.Length + " bytes  " +
                    "linear=(" + twist.Linear.X + ", " + twist.Linear.Y + ", " + twist.Linear.Z + ")  " +
                    "angular=(" + twist.Angular.X + ", " + twist.Angular.Y + ", " + twist.Angular.Z + ")");
            }

            if (sub.Received == 0)
            {
                Console.Error.WriteLine("Nothing arrived before the timeout.");
                Environment.Exit(1);
            }

            Console.WriteLine("Received " + sub.Received + " message(s).");
        }
    }
}
