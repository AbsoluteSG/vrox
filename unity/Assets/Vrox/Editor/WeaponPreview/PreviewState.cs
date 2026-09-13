using UnityEditor;
using UnityEngine;

namespace Vrox.Editor
{
    /// <summary>What a weapon preview is showing: its clock, aim, zoom and toggles.</summary>
    /// <remarks>
    /// One per preview surface, so the inspector pane and the designer window can
    /// be paused, aimed and zoomed independently while drawing the same weapon.
    /// </remarks>
    [System.Serializable]
    public sealed class PreviewState
    {
        public enum ViewMode
        {
            /// <summary>Volleys fired on the weapon's fire rate, spin building up between them.</summary>
            Stream,

            /// <summary>One volley, with every projectile's whole path drawn.</summary>
            Volley,
        }

        /// <summary>The clock wraps here, so a preview left running never loses float precision.</summary>
        public const float Loop = 60f;

        public ViewMode Mode = ViewMode.Stream;
        public bool Playing = true;
        public float TimeScale = 1f;
        public double Time;

        /// <summary>Aim direction in degrees, counter-clockwise from +x. 90 fires up the screen.</summary>
        public float AimDegrees = 90f;

        /// <summary>Half the view's shorter side, in tiles. 0 fits the weapon's reach.</summary>
        public float ZoomTiles;

        public bool Trails = true;
        public bool SlotNumbers;

        /// <summary>
        /// Draw it as an enemy fires it.
        /// </summary>
        /// <remarks>
        /// Enemies always go through <c>FireVolley</c>, so a weapon with a Range is
        /// hitscan in a player's hands and projectiles in an enemy's.
        /// </remarks>
        public bool AsEnemy;

        [System.NonSerialized] private double _last = -1;

        public Vector2 Aim => new(Mathf.Cos(AimDegrees * Mathf.Deg2Rad),
                                  Mathf.Sin(AimDegrees * Mathf.Deg2Rad));

        /// <summary>Advances the clock by real time elapsed. Safe to call more than once a frame.</summary>
        public void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_last >= 0 && Playing)
            {
                // Capped so a preview that was hidden for a minute resumes where
                // it was rather than jumping a minute ahead.
                Time += System.Math.Min(now - _last, 0.1) * TimeScale;
                if (Time >= Loop)
                {
                    Time -= Loop;
                }
            }
            _last = now;
        }
    }
}
