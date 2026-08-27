#!/usr/bin/env bash
# Checks that we actually talk to ROS 2, in both directions.
#
#     ./tools/interop_test.sh [path-to-SEANAV]
#
# Unlike SEANAV's verify.sh, this needs a real ROS 2 install, because the whole
# point is to test against ROS's own tools rather than against ourselves:
#
#   1. our publisher  -> ros2 topic echo     (does ROS understand our bytes?)
#   2. ros2 topic pub -> our subscriber      (do we understand ROS's bytes?)
#
# Exit code 0 means both worked.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SEANAV="${1:-${SEANAV_ROOT:-$HOME/SEANAV}}"

# A domain of our own, so a busy ROS network doesn't wander into the test.
export ROS_DOMAIN_ID="${ROS_DOMAIN_ID:-77}"

if [ -z "${ROS_DISTRO:-}" ]; then
    echo "SKIPPED: no ROS 2 sourced. Run 'source /opt/ros/jazzy/setup.bash' first." >&2
    exit 3
fi

echo "ROS_DISTRO=$ROS_DISTRO  ROS_DOMAIN_ID=$ROS_DOMAIN_ID"
echo

pass=0
fail=0

check () {  # check <name> <condition-exit-code> <detail>
    if [ "$2" -eq 0 ]; then
        echo "  [PASS] $1: $3"
        pass=$((pass+1))
    else
        echo "  [FAIL] $1: $3"
        fail=$((fail+1))
    fi
}

echo "Building..."
dotnet build -c Release "$HERE/examples/Talker/Talker.csproj"   -p:SeaNavCore="$SEANAV/unity/Assets/SEANAV/Core/Runtime" >/dev/null || exit 1
dotnet build -c Release "$HERE/examples/Listener/Listener.csproj" -p:SeaNavCore="$SEANAV/unity/Assets/SEANAV/Core/Runtime" >/dev/null || exit 1
echo

# ---------------------------------------------------------------------------
echo "[1] Our publisher -> ros2 topic echo"
echo "----------------------------------------------------------------------"

OUT=$(mktemp)
( timeout 25 ros2 topic echo --once /seanav/imu sensor_msgs/msg/Imu > "$OUT" 2>&1 ) &
ECHO_PID=$!
sleep 4
timeout 40 dotnet "$HERE/examples/Talker/bin/Release/net8.0/Talker.dll" 40 >/dev/null 2>&1
wait $ECHO_PID 2>/dev/null

grep -q "frame_id: seanav_imu" "$OUT"
check "ROS decodes our header" $? "frame_id came through"

# The talker sends g*cos(roll), so this is 9.80 something rather than exactly
# 9.80665. Match the part that is actually fixed.
grep -qE "z: 9\.80[0-9]*" "$OUT"
check "ROS decodes our float64s" $? "linear_acceleration.z is gravity, to 3 figures"

grep -q -- "- -1.0" "$OUT"
check "ROS decodes our fixed arrays" $? "the -1 'no orientation estimate' marker survived"

# ---------------------------------------------------------------------------
echo
echo "[2] ros2 topic pub -> our subscriber"
echo "----------------------------------------------------------------------"

GOT=$(mktemp)
( timeout 30 dotnet "$HERE/examples/Listener/bin/Release/net8.0/Listener.dll" 1 25 > "$GOT" 2>&1 ) &
LISTEN_PID=$!
sleep 4
timeout 20 ros2 topic pub -r 5 /seanav/cmd_vel geometry_msgs/msg/Twist \
    "{linear: {x: 6.17, y: 0.12, z: 0.0}, angular: {x: 0.0, y: 0.0, z: -0.015}}" >/dev/null 2>&1 &
PUB_PID=$!
wait $LISTEN_PID 2>/dev/null
LISTEN_RC=$?
kill $PUB_PID 2>/dev/null

[ "$LISTEN_RC" -eq 0 ]
check "we received a message ROS published" $? "exit code $LISTEN_RC"

grep -q "linear=(6.17, 0.12, 0)" "$GOT"
check "the linear velocity decoded correctly" $? "6.17, 0.12, 0"

grep -q -- "angular=(0, 0, -0.015)" "$GOT"
check "the angular velocity decoded correctly" $? "including the negative yaw rate"

# ---------------------------------------------------------------------------
echo
echo "[3] ROS sees us as a normal node"
echo "----------------------------------------------------------------------"

NODES=$(mktemp)
( timeout 25 dotnet "$HERE/examples/Talker/bin/Release/net8.0/Talker.dll" 200 >/dev/null 2>&1 ) &
TALK_PID=$!
sleep 5
ros2 node list > "$NODES" 2>&1
ros2 topic info /seanav/imu >> "$NODES" 2>&1
kill $TALK_PID 2>/dev/null
wait $TALK_PID 2>/dev/null

grep -q "/seanav_talker" "$NODES"
check "our node appears in 'ros2 node list'" $? "discovery works"

grep -q "Type: sensor_msgs/msg/Imu" "$NODES"
check "'ros2 topic info' reports the right type" $? "the type support lookup was correct"

grep -q "Publisher count: 1" "$NODES"
check "ROS counts one publisher" $? "the endpoint is registered properly"

rm -f "$OUT" "$GOT" "$NODES"

echo
echo "======================================================================"
if [ "$fail" -eq 0 ]; then
    echo "All $pass interop checks passed."
    exit 0
else
    echo "$fail of $((pass+fail)) interop checks FAILED."
    exit 1
fi
