using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.Reachy.Browser;

/// <summary>
/// The payload of the SDK's <c>videoTrack</c> event: the robot's live media stream.
/// </summary>
/// <remarks>
/// ⚠️ A LIVE OBJECT, so a wrapper and not a POCO. The stream is a handle to something the browser owns
/// and keeps mutating - tracks are added, ended and stopped underneath it - and copying its state into a
/// plain class would capture one instant of something whose whole purpose is to keep changing. POCOs are
/// for data crossing the boundary; wrappers are for objects that stay on the other side of it.
/// </remarks>
public class ReachyMediaDetail : SpawnJSObject
{
    /// <summary>Deserialization constructor.</summary>
    public ReachyMediaDetail(SpawnJSObjectReference _ref) : base(_ref) { }

    /// <summary>
    /// The robot's outbound media: the camera AND the four-microphone array, on one stream.
    /// </summary>
    /// <remarks>
    /// The array has hardware echo cancellation in its XVF3800, which is why the robot does not transcribe
    /// its own speech - a property worth relying on rather than re-solving in software.
    /// </remarks>
    public MediaStream? Stream => JSRef!.Get<MediaStream?>("stream");
}
