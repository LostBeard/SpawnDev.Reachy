"""Torso rotation tool for Rose.

The stock move_head tool cannot do this, and worse, it actively undoes it:
it passes target_body_yaw=0 on every call, so any torso rotation is snapped
back to centre the moment the character looks anywhere. That is why Rose
appears unable to turn to face you.

It also reads start_body_yaw from current_antennas[0], but body_yaw was
discarded into `_` on the line above, so that index is antenna 0's position,
not the body yaw. This tool reads body_yaw from the correct element.

Drop this in a profile folder and add `turn_body` to that profile's tools.txt.
"""

import logging
from typing import Any, Dict, Literal

from reachy_mini_conversation_app.tools.core_tools import Tool, ToolDependencies
from reachy_mini_conversation_app.dance_emotion_moves import GotoQueueMove

logger = logging.getLogger(__name__)

Direction = Literal["left", "right", "front", "around"]


class TurnBody(Tool):
    """Rotate the torso to face a direction, without disturbing the head."""

    name = "turn_body"
    description = (
        "Turn your whole body to face a direction: left, right, front, or around "
        "(to look behind you). Use this when someone is beside you or behind you, "
        "or when you want to face someone you are talking to. This turns your torso, "
        "which is different from move_head - that only turns your head."
    )
    parameters_schema = {
        "type": "object",
        "properties": {
            "direction": {
                "type": "string",
                "enum": ["left", "right", "front", "around"],
            },
        },
        "required": ["direction"],
    }

    # Absolute body_yaw targets in radians. The daemon clamps body_yaw to
    # [-pi, pi] and additionally constrains |body_yaw - head_yaw| to about
    # 65 degrees, so these stay well inside a single safe move.
    TARGETS: Dict[str, float] = {
        "left": 1.0,      # ~57 deg
        "right": -1.0,
        "front": 0.0,
        "around": 2.6,    # ~149 deg, as far round as is comfortable
    }

    async def __call__(self, deps: ToolDependencies, **kwargs: Any) -> Dict[str, Any]:
        direction_raw = kwargs.get("direction")
        if not isinstance(direction_raw, str):
            return {"error": "direction must be a string"}
        direction: Direction = direction_raw  # type: ignore[assignment]
        logger.info("Tool call: turn_body direction=%s", direction)

        target_body_yaw = self.TARGETS.get(direction, 0.0)

        try:
            movement_manager = deps.movement_manager

            current_head_pose = deps.reachy_mini.get_current_head_pose()

            # get_current_joint_positions() -> (head_joints[7], antennas[2]).
            # The 7 head joints are [body_yaw, stewart1..6] - body yaw is motor 10
            # ("foot"), the Stewart platform is 11-16. So body yaw IS index 0, but
            # of the FIRST list. The stock move_head tool discards that list into
            # `_` and then reads current_antennas[0], which is antenna 0.
            head_joints, current_antennas = (
                deps.reachy_mini.get_current_joint_positions()
            )
            current_body_yaw = head_joints[0]

            goto_move = GotoQueueMove(
                # Hold the head where it is. Rotating the torso should not yank
                # her gaze off whoever she is looking at.
                target_head_pose=current_head_pose,
                start_head_pose=current_head_pose,
                target_antennas=(current_antennas[0], current_antennas[1]),
                start_antennas=(current_antennas[0], current_antennas[1]),
                target_body_yaw=target_body_yaw,
                start_body_yaw=current_body_yaw,
                # Torso rotation carries more mass than a head turn, so give it
                # longer than the default motion duration or it looks snappy.
                duration=max(deps.motion_duration_s, 1.2),
            )

            movement_manager.queue_move(goto_move)
            movement_manager.set_moving_state(max(deps.motion_duration_s, 1.2))

            return {"status": f"turned body {direction}"}

        except Exception as e:
            logger.error("turn_body failed")
            return {"error": f"turn_body failed: {type(e).__name__}: {e}"}
