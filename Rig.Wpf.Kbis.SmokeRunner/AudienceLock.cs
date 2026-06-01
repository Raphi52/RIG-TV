using System;
using System.Threading;

namespace Rig.Wpf.Kbis.SmokeRunner
{
    /// <summary>
    /// Verrou cross-process par audience. Un run pose un Mutex Windows nommé
    /// Global\RigSmokeAud_{id} avant snapshot, le libère après restore. Runs sur
    /// audiences différentes -> parallèles ; même audience -> sérialisés.
    ///
    /// ⚠ Mutex est thread-affine : Acquire et Dispose DOIVENT se faire sur le même
    /// thread. Le flux snapshot/apply/restore de Program.cs est synchrone sur un
    /// seul thread -> contrainte respectée par construction.
    /// </summary>
    public sealed class AudienceLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _acquired;
        private bool _disposed;

        private AudienceLock(Mutex mutex)
        {
            _mutex = mutex;
            _acquired = true;
        }

        /// <summary>
        /// Acquiert le verrou de l'audience. Bloque jusqu'à obtention ou timeout.
        /// </summary>
        /// <exception cref="TimeoutException">si le verrou n'est pas obtenu dans le délai.</exception>
        public static AudienceLock Acquire(int audienceId, TimeSpan timeout)
        {
            var name = "Global\\RigSmokeAud_" + audienceId;
            var mutex = new Mutex(false, name);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // Run précédent mort sans libérer -> on hérite du verrou.
                acquired = true;
            }
            if (!acquired)
            {
                mutex.Dispose();
                throw new TimeoutException(
                    $"Audience {audienceId} verrouillée par un autre run depuis plus de " +
                    $"{timeout.TotalMinutes:F0} min.");
            }
            return new AudienceLock(mutex);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_acquired)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"      ⚠ AudienceLock.Dispose : ReleaseMutex a échoué : {ex.GetType().Name}: {ex.Message}");
                }
                _acquired = false;
            }
            _mutex.Dispose();
        }
    }
}
