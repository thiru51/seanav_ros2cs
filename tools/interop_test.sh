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

# ---------------------------------------------------------------- custom type
#
# SEANAV's own message type, seanav_msgs/msg/VesselState. This is a separate
# check from the ones above because a custom type can fail in a way a standard
# one cannot: the C# class is generated and compiles, but the native type
# support library only exists if somebody ran colcon. Publishing is the only
# thing that notices.
# Collected into a variable first, deliberately. Writing this as
#
#     if ros2 interface list | grep -q seanav_msgs/msg/VesselState
#
# looks obviously right and is broken under `set -o pipefail`: grep -q exits
# the moment it finds a match, ros2 gets SIGPIPE and dies with 141, and
# pipefail then reports the whole pipeline as FAILED - precisely because the
# match succeeded. The check skipped itself on a working install.
INTERFACES="$(ros2 interface list 2>/dev/null || true)"

if printf '%s\n' "$INTERFACES" | grep -q "seanav_msgs/msg/VesselState"; then
    dotnet build -c Release "$HERE/examples/CustomTypeDemo/CustomTypeDemo.csproj" \
        -p:SeaNavCore="$SEANAV/unity/Assets/SEANAV/Core/Runtime" >/dev/null 2>&1

    CUSTOM="$(mktemp)"
    dotnet run -c Release --no-build --project "$HERE/examples/CustomTypeDemo" -- 12 \
        > /dev/null 2>&1 &
    CUSTOM_PID=$!
    sleep 4

    timeout 10 ros2 topic echo --once /seanav/vessel_state > "$CUSTOM" 2>&1

    kill $CUSTOM_PID 2>/dev/null
    wait $CUSTOM_PID 2>/dev/null

    grep -q "rudder_angle: 0.35" "$CUSTOM"
    check "a CUSTOM type survives the round trip" $? "ros2 topic echo decoded seanav_msgs/msg/VesselState"

    # Field order is where a hand-written serialiser goes wrong, and it goes
    # wrong quietly - the message still decodes, with values in the wrong slots.
    grep -q "water_depth: 18.75" "$CUSTOM"
    check "custom type field ORDER is right" $? "the last field landed in the last slot"

    rm -f "$CUSTOM"
else
    echo "  [SKIP] custom type: seanav_msgs is not built or not sourced."
    echo "         cd \$SEANAV/ros2 && colcon build --packages-select seanav_msgs"
    echo "         source \$SEANAV/ros2/install/setup.bash"
fi

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
