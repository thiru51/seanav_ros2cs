#!/usr/bin/env bash
# Build SeaNav.Ros2 and the examples.
#
#   ./tools/build.sh [path-to-SEANAV]
#
# Needs: .NET SDK 8, and a ROS 2 installation sourced (or reachable) at run time.
# Does NOT need colcon, a ROS workspace, or any native compilation — that is the
# whole point of the serialized-publish design. See README.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SEANAV="${1:-${SEANAV_ROOT:-$HOME/SEANAV}}"
CORE="$SEANAV/unity/Assets/SEANAV/Core/Runtime"

if [ ! -f "$CORE/Ros/Cdr.cs" ]; then
    echo "ERROR: cannot find SEANAV's CDR sources at $CORE/Ros/Cdr.cs" >&2
    echo "Pass the SEANAV checkout as the first argument, or set SEANAV_ROOT." >&2
    exit 2
fi

echo "SEANAV core : $CORE"
dotnet build -c Release "$HERE/src/SeaNav.Ros2/SeaNav.Ros2.csproj" -p:SeaNavCore="$CORE"
dotnet build -c Release "$HERE/examples/Talker/Talker.csproj" -p:SeaNavCore="$CORE"

echo
echo "Built. Try it with ROS 2 sourced:"
echo "    dotnet $HERE/examples/Talker/bin/Release/net8.0/Talker.dll"
echo "    ros2 topic echo /seanav/imu sensor_msgs/msg/Imu"
