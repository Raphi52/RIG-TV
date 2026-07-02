using System;
using System.Threading;
using System.Threading.Tasks;
using Rig.Wpf.Kbis.SmokeRunner;
using Xunit;

namespace Rig.Rapture.Tests
{
    // Plage d'IDs réservée aux tests unitaires AudienceLock : 990001–990099.
    // Ne pas utiliser ces IDs dans d'autres tests pour éviter les collisions
    // de Mutex nommés (Global\RigSmokeAud_<id>) en exécution parallèle.
    public class AudienceLockTests
    {
        // CausalHypothesis (vérifiée) : l'ancien corps `await Task.Run(...)` DANS le `using` faisait reprendre
        // le Dispose (ReleaseMutex) sur un thread ≠ thread acquéreur (Mutex thread-affine, cf. AudienceLock.cs:11).
        // ReleaseMutex hors-thread échoue silencieusement → mutex ABANDONNÉ → au run suivant WaitOne lève
        // AbandonedMutexException, traité comme "acquis" (AudienceLock.cs:40-43) → le 2e acquire réussit →
        // "No exception thrown" (flaky sous charge/répétition). Fix : méthode SYNCHRONE + thread dédié pour le
        // acquire concurrent → release garanti sur le thread acquéreur, déterministe.
        [Fact]
        public void Acquire_meme_audience_pendant_qu_un_lock_est_tenu_throw_TimeoutException()
        {
            int aud = 990001; // ID dédié au test, jamais utilisé en base
            using (AudienceLock.Acquire(aud, TimeSpan.FromSeconds(5)))
            {
                TimeoutException caught = null;
                var contender = new Thread(() =>
                {
                    try { using (AudienceLock.Acquire(aud, TimeSpan.FromMilliseconds(300))) { } }
                    catch (TimeoutException ex) { caught = ex; }
                });
                contender.Start();
                contender.Join();
                Assert.NotNull(caught); // le 2e acquire (autre thread, verrou tenu) DOIT timeout
            }
        }

        [Fact]
        public void Acquire_audiences_differentes_ne_se_bloquent_pas()
        {
            using (AudienceLock.Acquire(990002, TimeSpan.FromSeconds(2)))
            using (AudienceLock.Acquire(990003, TimeSpan.FromSeconds(2)))
            {
                Assert.True(true);
            }
        }

        [Fact]
        public void Acquire_apres_release_reussit()
        {
            int aud = 990004;
            using (AudienceLock.Acquire(aud, TimeSpan.FromSeconds(2))) { }
            using (AudienceLock.Acquire(aud, TimeSpan.FromMilliseconds(500))) { }
            Assert.True(true);
        }
    }
}
