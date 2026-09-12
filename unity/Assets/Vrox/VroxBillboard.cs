using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Keeps a transform facing the player as the view turns.
    /// </summary>
    /// <remarks>
    /// For things drawn as real GameObjects — the player's own sprite, and any
    /// prop placed by hand in the scene. The renderers that build their own
    /// meshes do this by laying quads out along
    /// <see cref="VroxCamera.ScreenAxes"/> instead, which is the same rotation
    /// without a transform to pay for.
    ///
    /// The player needs it despite never rotating: the camera is a *child* of the
    /// player, so turning the camera leaves the player's sprite standing still in
    /// the world and therefore spinning on screen. That the fix belongs on the
    /// thing that is not rotating is the confusing part, and the reason this is a
    /// component rather than something folded into the camera.
    ///
    /// World rotation, not local, so a billboard stays upright whatever it is
    /// parented to.
    /// </remarks>
    public sealed class VroxBillboard : MonoBehaviour
    {
        /// <summary>
        /// Runs after the camera has turned, so there is no frame where the two
        /// disagree.
        /// </summary>
        /// <remarks>
        /// <c>VroxCamera</c> turns in <c>Update</c>. Doing this in <c>Update</c>
        /// too would make the result depend on script execution order, which is a
        /// setting nobody looks at when a sprite is one frame behind.
        /// </remarks>
        private void LateUpdate()
        {
            if (VroxCamera.Instance is not { } camera)
            {
                return;
            }
            transform.rotation = Quaternion.Euler(0f, 0f, camera.Angle * Mathf.Rad2Deg);
        }
    }
}
