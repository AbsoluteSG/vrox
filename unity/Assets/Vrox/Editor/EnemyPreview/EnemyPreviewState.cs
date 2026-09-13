using UnityEditor;
using UnityEngine;

namespace Vrox.Editor
{
    /// <summary>
    /// Everything about a simulated fight that is not the enemy asset itself: the
    /// stand-in player, the damage it deals, where the fight starts, and the clock.
    /// </summary>
    [System.Serializable]
    public sealed class EnemyPreviewState
    {
        public enum DummyScript
        {
            /// <summary>Stands where it was put.</summary>
            Hold,

            /// <summary>Circles the enemy's spawn point at the distance it was put.</summary>
            CircleStrafe,

            /// <summary>Walks straight in toward the spawn point and back out, over and over.</summary>
            ApproachRetreat,

            /// <summary>Backs away or closes in to hold its starting distance from the enemy.</summary>
            Kite,
        }

        public bool Playing = true;
        public float TimeScale = 1f;
        public double Time;

        /// <summary>Half the view's shorter side, in tiles. 0 fits the fight.</summary>
        public float ZoomTiles;

        public DummyScript Script = DummyScript.Hold;

        /// <summary>Where the dummy starts, relative to the enemy's spawn point at the origin.</summary>
        public Vector2 DummyAnchor = new(0f, 10f);

        /// <summary>Tiles per second. The server's default player speed is 5.</summary>
        public float DummySpeed = 5f;

        /// <summary>How close Approach and Retreat comes before turning back.</summary>
        public float ApproachNear = 2f;

        /// <summary>The spawn-time random <c>Enemy.Phase</c> the server rolls, which steers wandering.</summary>
        public float WanderSeed;

        /// <summary>The ×0.5 speed a slowing debuff applies.</summary>
        public bool Slowed;

        /// <summary>Damage per second the dummy deals while within <see cref="PlayerRange"/>.</summary>
        public float Dps = 100f;

        /// <summary>How close the dummy has to be to deal damage, in tiles.</summary>
        public float PlayerRange = 12f;

        public int StartPhase;
        public float StartHpPercent = 100f;
        public float FightSeconds = 120f;

        public int SelectedPhase;
        public bool Ranges = true;
        public bool Bullets = true;
        public bool Trails = true;

        [System.NonSerialized] private double _last = -1;

        /// <summary>Advances the clock by real time elapsed, wrapping at <paramref name="loopSeconds"/>.</summary>
        public void Tick(double loopSeconds)
        {
            double now = EditorApplication.timeSinceStartup;
            if (_last >= 0 && Playing)
            {
                Time += System.Math.Min(now - _last, 0.1) * TimeScale;
            }
            _last = now;

            if (loopSeconds > 0 && Time > loopSeconds)
            {
                Time = Playing ? 0 : loopSeconds;
            }
        }

        public void Restart(float dps)
        {
            Time = 0;
            ZoomTiles = 0f;
            SelectedPhase = 0;
            StartPhase = 0;
            StartHpPercent = 100f;
            Dps = dps;
        }
    }
}
