using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;
using Vrox.Equipment;

namespace Vrox
{
    /// <summary>
    /// A region that keeps itself populated. Drop it in the scene and size it.
    /// </summary>
    /// <remarks>
    /// This component spawns nothing. It is authoring: its settings are pushed to
    /// the server, which owns the enemies.
    ///
    /// It carries only *placement* — where and how big. What actually appears
    /// comes from the <see cref="PopulationConfigItem"/>, so several spawners can
    /// share one population and stay consistent.
    ///
    /// It has to work that way. A client that spawned its own would see a
    /// different world from every other player, and could make as many as it
    /// liked. The same split already applies to weapons and enemy archetypes —
    /// the asset or scene object describes it, the server's row is the thing that
    /// runs.
    ///
    /// Position comes from the transform and the radius is drawn as a gizmo, so
    /// placing one is dragging it where you want it.
    /// </remarks>
    public sealed class VroxSpawner : MonoBehaviour
    {
        [Tooltip("What populates this area — the enemy mix, the cap and the rate. " +
                 "Leave empty to disable this spawner.")]
        [FormerlySerializedAs("Area")]
        public PopulationConfigItem? Population;

        [Tooltip("Enemies appear at a random point within this radius, in tiles.")]
        [Range(0f, 20f)]
        public float Radius = 4f;

        private void OnDrawGizmos()
        {
            // Tinted by the commonest enemy in the mix, so populations are
            // distinguishable at a glance in a scene with several.
            var colour = Color.grey;
            if (Population != null)
            {
                var main = Population.Usable.OrderByDescending(e => e.Weight).FirstOrDefault();
                if (main.Enemy != null)
                {
                    colour = main.Enemy.Tint;
                }
            }

            Gizmos.color = new Color(colour.r, colour.g, colour.b, 0.15f);
            Gizmos.DrawSphere(transform.position, Radius);

            Gizmos.color = new Color(colour.r, colour.g, colour.b, 0.9f);
            Gizmos.DrawWireSphere(transform.position, Radius);

            // A disabled spawner looks identical to a working one otherwise, and
            // the only symptom is enemies that never arrive.
            if (Population == null || !Population.Usable.Any())
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(transform.position + Vector3.left, transform.position + Vector3.right);
                Gizmos.DrawLine(transform.position + Vector3.down, transform.position + Vector3.up);
            }
        }
    }
}
