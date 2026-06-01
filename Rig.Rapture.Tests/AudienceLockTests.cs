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
        [Fact]
        public async Task Acquire_meme_audience_pendant_qu_un_lock_est_tenu_throw_TimeoutException()
        {
            int aud = 990001; // ID dédié au test, jamais utilisé en base
            using (AudienceLock.Acquire(aud, TimeSpan.FromSeconds(5)))
            {
                await Task.Run(() =>
                    Assert.Throws<TimeoutException>(() =>
                        AudienceLock.Acquire(aud, TimeSpan.FromMilliseconds(300))));
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
