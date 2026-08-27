// Exposes some settings through ros2 param.
//
//     ros2 param list /seanav_params
//     ros2 param get  /seanav_params sea_state.significant_wave_height
//     ros2 param set  /seanav_params sea_state.significant_wave_height 2.5
//     ros2 param describe /seanav_params vessel.name

using System;
using System.Threading;
using SeaNav.Ros2;
using SeaNav.Ros2.Unity;

internal static class Program
{
    private static void Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 60.0;

        using (Ros2Context ros = new Ros2Context())
        using (Ros2Node node = ros.CreateNode("seanav_params"))
        using (var parameters = new Ros2ParameterServer(node, new Ros2ParameterCodec()))
        {
            parameters.Declare(new Parameter("sea_state.significant_wave_height", 1.5)
            { Description = "Hs in metres" });
            parameters.Declare(new Parameter("sea_state.peak_period", 8.0)
            { Description = "Tp in seconds" });
            parameters.Declare(new Parameter("vessel.name", "KVLCC2")
            { Description = "Which hull is loaded", ReadOnly = true });
            parameters.Declare(new Parameter("sensors.imu_rate_hz", 100L)
            { Description = "IMU sample rate" });
            parameters.Declare(new Parameter("sensors.gnss_enabled", true));

            parameters.Changed += p => Console.WriteLine("changed: " + p);

            Console.WriteLine("Node /seanav_params offering " + CountOf(parameters) + " parameters.");
            Console.WriteLine("  ros2 param list /seanav_params");
            Console.WriteLine();

            DateTime giveUp = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < giveUp && ros.Ok)
            {
                parameters.Spin();
                Thread.Sleep(5);
            }

            Console.WriteLine("Applied " + parameters.Applied + " change(s).");
        }
    }

    private static int CountOf(Ros2ParameterServer s)
    {
        int n = 0;
        foreach (var unused in s.All) n++;
        return n;
    }
}
