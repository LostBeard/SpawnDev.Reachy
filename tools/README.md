# tools

Standalone .NET 10 file-based scripts. Run directly: `dotnet run <file>.cs [args]`.

**File-based apps run with reflection-based JSON disabled.** Anonymous types cannot be
serialised and `JsonSerializer.Serialize(new { ... })` throws at runtime. Use a
source-generated `JsonSerializerContext`, or build the JSON by hand with
`JsonEncodedText.Encode`. Both patterns are used below.

| script | what it does |
|---|---|
| `rose_ssh.cs` | Run any command on the robot over SSH. The daemon's `/logs` endpoint is a dead HTML page, so this is how you reach `journalctl -u reachy-mini-daemon`. |
| `rose_body_track.cs` | Body-follows-face tracking. The stock daemon tracks with the HEAD only, so Rose can glance but not turn. Fixed gain -1.5 rad per unit face-x, 0.35 correction, 0.08 deadband, 3-sample persistence, runaway guard. **TJ-confirmed working.** |
| `rose_volume_fix.cs` | Inspects (and with `--apply` sets) the robot's ALSA mixer over SSH. Read-only by default. Outcome: everything already sits at 0.00 dB - the volume problem was mechanical, not electrical. |
| `model_bench.cs` | Benchmarks Ollama models for the N persona: TTFT, tok/s, reasoning-model detection. **Runs each model twice and keeps the second** - the first call is pure VRAM load time and is ~60x wrong. |
| `doa_calibrate.cs` | Logs direction-of-arrival while you speak from known positions. Kept for reference; DOA proved unusable (see STATUS.md dead ends). |

## Why gains are fixed constants, not calibrated

`rose_body_track.cs` deliberately hardcodes the face-x gain. Auto-calibration is
impossible on this hardware in both directions: with face tracking ON the head cancels
the probe mid-measurement (an identical 0.20 rad probe returned dx 0.119 then 0.047,
swinging the derived gain from -1.67 to -4.28 and driving a visible limit cycle), and
turning tracking OFF also disables face DETECTION, so there is nothing left to measure.
The runaway guard covers the case where a different unit disagrees on sign.
