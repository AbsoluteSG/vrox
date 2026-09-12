using System;
using SpacetimeDB.Types;
using UnityEditor;
using UnityEngine;

namespace Vrox.Editor
{
    /// <summary>
    /// Runs editor work against the server, whether or not the game is playing.
    /// </summary>
    /// <remarks>
    /// Every catalogue upload goes through a reducer, so it needs a connection.
    /// Requiring play mode for that made pushing a three-step ritual — press
    /// Play, wait for the subscription, push, stop — and the most common way to
    /// get a stale catalogue was forgetting one of those steps.
    ///
    /// The live connection is preferred when there is one. Opening a second
    /// connection while the game is running would push as a different identity
    /// and leave a stray player row behind, which is exactly the kind of debris
    /// that later looks like a bug in the player table.
    ///
    /// Outside play mode this opens its own connection, drives it from
    /// <see cref="EditorApplication.update"/> — the SDK has no thread of its own,
    /// so something has to call <c>FrameTick</c> — and closes it when the work is
    /// done. The whole thing is one operation with a timeout rather than a
    /// long-lived editor connection, because a connection that outlives a domain
    /// reload becomes a socket nothing owns.
    /// </remarks>
    public static class EditorConnection
    {
        /// <summary>How long to wait for a connection before giving up, in seconds.</summary>
        private const double TimeoutSeconds = 10.0;

        /// <summary>
        /// The realm row is the only table the editor pushes actually read
        /// (<c>PushRealm.IsGenerated</c>), so it is the only one worth waiting for.
        /// </summary>
        private static readonly string[] Queries = { "SELECT * FROM realm" };

        /// <summary>
        /// Runs <paramref name="work"/> with a connected client.
        /// </summary>
        /// <remarks>
        /// Synchronous from the caller's point of view when the game is playing,
        /// and deferred when it is not — there is no way to block the editor while
        /// a socket connects without hanging Unity. Callers therefore must not
        /// assume the work has finished when this returns.
        /// </remarks>
        public static void Run(string label, Action<DbConnection> work)
        {
            if (VroxNet.Instance is { } net && net.Conn is { } live && net.Ready)
            {
                work(live);
                return;
            }

            string uri = "http://127.0.0.1:3000";
            string database = "vrox";

            // Taken from the scene's own component when there is one, so the
            // editor cannot quietly push to a different server than the game
            // connects to. Inactive objects included: a disabled VroxNet is still
            // the scene's statement about which server this project talks to.
            var scene = UnityEngine.Object.FindObjectsByType<VroxNet>(
                FindObjectsInactive.Include);
            if (scene.Length > 0)
            {
                uri = scene[0].Uri;
                database = scene[0].Database;
            }

            DbConnection? conn = null;
            EditorApplication.CallbackFunction? pump = null;
            double deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            bool finished = false;

            void Finish(string? error)
            {
                if (finished)
                {
                    return;
                }
                finished = true;
                EditorApplication.update -= pump;

                if (error != null)
                {
                    Debug.LogError($"Vrox: {label} failed — {error}");
                }

                try
                {
                    conn?.Disconnect();
                }
                catch (Exception)
                {
                    // Already gone. Nothing here outlives the operation, so a
                    // failure to close cleanly costs nothing worth reporting.
                }
            }

            conn = DbConnection.Builder()
                .WithUri(uri)
                .WithDatabaseName(database)
                .OnConnect((c, _, _) =>
                    c.SubscriptionBuilder()
                        .OnApplied(_ =>
                        {
                            try
                            {
                                work(c);
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"Vrox: {label} threw — {e}");
                            }
                            Finish(null);
                        })
                        .Subscribe(Queries))
                .OnConnectError(e => Finish($"could not reach {uri} ({e.Message}). "
                                          + "Is `spacetime start` running?"))
                .OnDisconnect((_, e) =>
                {
                    if (!finished)
                    {
                        Finish($"disconnected before finishing ({e?.Message ?? "clean"})");
                    }
                })
                .Build();

            pump = () =>
            {
                if (finished)
                {
                    return;
                }
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    Finish($"timed out after {TimeoutSeconds:0}s waiting for {uri}");
                    return;
                }
                conn?.FrameTick();
            };
            EditorApplication.update += pump;
        }
    }
}
