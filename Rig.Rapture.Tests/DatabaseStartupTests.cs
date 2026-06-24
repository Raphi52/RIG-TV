using System;
using Rig.Wpf.Kbis.TestViewer.Services;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Côté TestViewer du sélecteur de DB : résolution de la connexion EFFECTIVE
    /// (précédence CLI &gt; persisté &gt; défaut) et pose de RIG_LEGACY_CONNECTION sur
    /// l'env var du process — le lien entre le choix UI/CLI et les workers smoke
    /// (qui lisent cette env var ; prouvé out-of-model par le run --reset-smoke-db).
    /// </summary>
    public class DatabaseStartupTests
    {
        private static void ResetCli()
        {
            DatabaseStartup.CliServer = null;
            DatabaseStartup.CliDatabase = null;
            DatabaseStartup.CliConnection = null;
        }

        [Fact]
        public void Resolve_uses_persisted_when_no_cli()
        {
            ResetCli();
            var s = new GlobalSettings { DatabaseServer = @"SRV\X", DatabaseName = "DBPERSIST" };
            var cs = DatabaseStartup.Resolve(s);
            Assert.Contains(@"Server=SRV\X", cs);
            Assert.Contains("Database=DBPERSIST", cs);
            Assert.Contains("Integrated Security=True", cs);
        }

        [Fact]
        public void Resolve_cli_server_db_override_persisted()
        {
            ResetCli();
            DatabaseStartup.CliServer = @"CLISRV\Y";
            DatabaseStartup.CliDatabase = "DBCLI";
            try
            {
                var s = new GlobalSettings { DatabaseServer = @"SRV\X", DatabaseName = "DBPERSIST" };
                var cs = DatabaseStartup.Resolve(s);
                Assert.Contains(@"Server=CLISRV\Y", cs);   // CLI gagne sur le serveur persisté
                Assert.Contains("Database=DBCLI", cs);      // CLI gagne sur la base persistée
            }
            finally { ResetCli(); }
        }

        [Fact]
        public void Resolve_full_connection_beats_all()
        {
            ResetCli();
            DatabaseStartup.CliConnection = "Server=Z;Database=FULL;";
            try
            {
                var s = new GlobalSettings { DatabaseServer = @"SRV\X", DatabaseName = "DBPERSIST" };
                Assert.Equal("Server=Z;Database=FULL;", DatabaseStartup.Resolve(s));
            }
            finally { ResetCli(); }
        }

        [Fact]
        public void Apply_sets_process_env_var()
        {
            ResetCli();
            var prev = Environment.GetEnvironmentVariable(DatabaseStartup.EnvVar);
            try
            {
                var s = new GlobalSettings { DatabaseServer = @"APPLYSRV\Z", DatabaseName = "DBAPPLY" };
                DatabaseStartup.Apply(s);
                var got = Environment.GetEnvironmentVariable(DatabaseStartup.EnvVar);
                Assert.NotNull(got);
                Assert.Contains(@"Server=APPLYSRV\Z", got);
                Assert.Contains("Database=DBAPPLY", got);
            }
            finally
            {
                Environment.SetEnvironmentVariable(DatabaseStartup.EnvVar, prev, EnvironmentVariableTarget.Process);
                ResetCli();
            }
        }
    }
}
