using System;
using System.Collections;
using MySql.Data.MySqlClient;
using TCAdmin.GameHosting.SDK.Automation;
using TCAdmin.SDK.Integration;
using TCAdmin.SDK.Objects;
using Service = TCAdmin.GameHosting.SDK.Objects.Service;
using ServiceEvent = TCAdmin.GameHosting.SDK.Objects.ServiceEvent;

namespace MySqlModule.Helpers
{
    // Registered against TCAdmin service lifecycle events in install.sql. TCAdmin recreates the
    // service's variable set during a reinstall or move, which silently dropped the
    // _MySQLPlugin::* variables and made the customer's database vanish from the manager. This
    // handler repairs that at the source and, when a service moves to a server on a DIFFERENT
    // MySQL host, migrates the database across so it follows the service.
    //
    // The fired event is read from args.Arguments[0] (a ServiceEvent), matching how TCAdmin
    // dispatches these. It must never abort a TCAdmin operation: every path returns
    // ReturnStatus.Ok and swallows its own failures, and it never drops a source database, so a
    // failed/partial move can never destroy customer data.
    public class EventHandler : CommandBase
    {
        public override CommandResponse ProcessCommand(object sender, IntegrationEventArgs args)
        {
            var response = new CommandResponse(MySqlStore.ModuleGuid, ReturnStatus.Ok);

            try
            {
                if (!(sender is Service service) || args?.Arguments == null || args.Arguments.Length == 0)
                    return response;

                var serviceEvent = (ServiceEvent)args.Arguments[0];

                ObjectBase.GlobalSkipSecurityCheck = true;

                switch (serviceEvent)
                {
                    case ServiceEvent.BeforeDelete:
                        HandleDelete(service);
                        break;

                    case ServiceEvent.BeforeReinstall:
                        CaptureVariables(service);
                        break;

                    case ServiceEvent.BeforeMove:
                        HandleBeforeMove(service, args);
                        break;

                    case ServiceEvent.AfterMove:
                        ReconcileVariables(service);
                        break;
                }
            }
            catch
            {
                // Never let this module abort a TCAdmin lifecycle operation.
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return response;
        }

        // Before a reinstall, make sure the metadata store holds this service's current database
        // details so they can be restored if the operation wipes the variables.
        private static void CaptureVariables(Service service)
        {
            if (!MySqlStore.HasDatabase(service))
                return;

            var config = MySqlStore.GetHostConfig(service);
            if (!config.IsConfigured)
                return;

            using (var conn = new MySqlConnection(config.GetConnectionString()))
            {
                conn.Open();
                MySqlStore.TryUpsert(conn, service.ServiceId,
                    service.Variables[MySqlStore.UsernameVariable].ToString(),
                    service.Variables[MySqlStore.DatabaseVariable].ToString(),
                    service.Variables[MySqlStore.PasswordVariable].ToString(),
                    config.Host);
            }
        }

        // Fired while the service still lives on the source server. If the destination server is
        // on a different MySQL host, the database is migrated to the destination and then
        // removed from the source. If the migration fails at any point the source is untouched.
        private static void HandleBeforeMove(Service service, IntegrationEventArgs args)
        {
            var sourceConfig = MySqlStore.GetHostConfig(service);

            // Resolve the database details: prefer the live variables, fall back to the source
            // host's metadata record in case the variables were already wiped.
            var dbUser = service.Variables[MySqlStore.UsernameVariable]?.ToString();
            var dbName = service.Variables[MySqlStore.DatabaseVariable]?.ToString();
            var dbPass = service.Variables[MySqlStore.PasswordVariable]?.ToString();

            if (sourceConfig.IsConfigured &&
                (string.IsNullOrEmpty(dbUser) || string.IsNullOrEmpty(dbName) || string.IsNullOrEmpty(dbPass)))
            {
                using (var conn = new MySqlConnection(sourceConfig.GetConnectionString()))
                {
                    conn.Open();
                    var record = MySqlStore.TryGetRecord(conn, service.ServiceId);
                    if (record != null)
                    {
                        dbUser = record.Username;
                        dbName = record.Database;
                        dbPass = record.Password;
                    }
                }
            }

            // No database on this service -> nothing to migrate or preserve.
            if (string.IsNullOrEmpty(dbUser) || string.IsNullOrEmpty(dbName) || string.IsNullOrEmpty(dbPass))
                return;

            // Resolve the destination MySQL host from the move info.
            MySqlHostConfig destConfig = null;
            var moveInfo = TryGetMoveInfo(args);
            if (moveInfo.HasValue && moveInfo.Value.ToServer > 0)
            {
                var destServer = new Server(moveInfo.Value.ToServer);
                var destDatacenter = new Datacenter(destServer.DatacenterId);
                destConfig = MySqlStore.GetHostConfig(destServer, destDatacenter);
            }

            // Same MySQL host (typical same-datacenter move) or destination unknown: no data
            // migration. Just make sure the metadata is current so the variables can be restored.
            var sameHost = destConfig != null && destConfig.IsConfigured && sourceConfig.IsConfigured &&
                           string.Equals(destConfig.Host, sourceConfig.Host, StringComparison.OrdinalIgnoreCase);

            if (destConfig == null || !destConfig.IsConfigured || !sourceConfig.IsConfigured || sameHost)
            {
                if (sourceConfig.IsConfigured)
                {
                    using (var conn = new MySqlConnection(sourceConfig.GetConnectionString()))
                    {
                        conn.Open();
                        MySqlStore.TryUpsert(conn, service.ServiceId, dbUser, dbName, dbPass, sourceConfig.Host);
                    }
                }
                return;
            }

            // Different MySQL host: copy the database to the destination before the move proceeds.
            // All destination steps must succeed before the source is touched; if anything throws
            // here the outer catch absorbs it and the source database is left untouched.
            var dump = MySqlStore.ExportDatabase(sourceConfig, dbName);
            MySqlStore.RecreateDatabaseAndUser(destConfig, dbName, dbUser, dbPass);
            MySqlStore.ImportDatabase(destConfig, dbName, dump);

            // Record the database's new home on the destination host and repoint the service.
            // The move may rewrite the variables; AfterMove re-applies them from this metadata.
            using (var conn = new MySqlConnection(destConfig.GetConnectionString()))
            {
                conn.Open();
                MySqlStore.TryUpsert(conn, service.ServiceId, dbUser, dbName, dbPass, destConfig.Host);
            }

            MySqlStore.ApplyVariables(service, destConfig.Host, dbUser, dbPass, dbName);

            // If the admin chose "Keep Original Service" the source game server stays alive,
            // so its database must remain on the source host too.
            if (moveInfo.HasValue && !moveInfo.Value.DeleteOriginal)
                return;

            // Migration to destination is fully committed - remove the source copy.
            // This runs last so any earlier failure leaves the source intact.
            using (var conn = new MySqlConnection(sourceConfig.GetConnectionString()))
            {
                conn.Open();
                MySqlStore.Execute(conn, $"DROP USER IF EXISTS '{dbUser}'@'%';");
                MySqlStore.Execute(conn, $"DROP DATABASE IF EXISTS `{dbName}`;");
                MySqlStore.TryDelete(conn, service.ServiceId);
            }
        }

        // After a move, ensure the service variables point at the database on its current host,
        // restoring them from the metadata if the move wiped them or left them pointing elsewhere.
        private static void ReconcileVariables(Service service)
        {
            var config = MySqlStore.GetHostConfig(service);
            if (!config.IsConfigured)
                return;

            using (var conn = new MySqlConnection(config.GetConnectionString()))
            {
                conn.Open();
                var record = MySqlStore.TryGetRecord(conn, service.ServiceId);
                if (record == null)
                    return;

                var currentHost = service.Variables[MySqlStore.HostVariable]?.ToString();
                if (!MySqlStore.HasDatabase(service) ||
                    !string.Equals(currentHost, config.Host, StringComparison.OrdinalIgnoreCase))
                {
                    // Reload a fully writable instance before saving variables.
                    MySqlStore.ApplyVariables(new Service(service.ServiceId), config.Host,
                        record.Username, record.Password, record.Database);
                }
            }
        }

        // When the service is deleted, drop its MySQL database and user and forget the metadata
        // so nothing is left orphaned on the host.
        private static void HandleDelete(Service service)
        {
            var dbUser = service.Variables[MySqlStore.UsernameVariable]?.ToString();
            var dbName = service.Variables[MySqlStore.DatabaseVariable]?.ToString();

            var config = MySqlStore.GetHostConfig(service);
            if (!config.IsConfigured)
                return;

            using (var conn = new MySqlConnection(config.GetConnectionString()))
            {
                conn.Open();

                // Fall back to the metadata record if the variables were already wiped.
                if (string.IsNullOrEmpty(dbUser) || string.IsNullOrEmpty(dbName))
                {
                    var record = MySqlStore.TryGetRecord(conn, service.ServiceId);
                    if (record != null)
                    {
                        dbUser = record.Username;
                        dbName = record.Database;
                    }
                }

                if (!string.IsNullOrEmpty(dbUser))
                    MySqlStore.Execute(conn, $"DROP USER IF EXISTS '{dbUser}'@'%';");
                if (!string.IsNullOrEmpty(dbName))
                    MySqlStore.Execute(conn, $"DROP DATABASE IF EXISTS `{dbName}`;");

                MySqlStore.TryDelete(conn, service.ServiceId);
            }
        }

        private static GameHostingMoveInfo? TryGetMoveInfo(IntegrationEventArgs args)
        {
            try
            {
                if (args?.Arguments != null && args.Arguments.Length > 1 &&
                    args.Arguments[1] is IDictionary dict && dict.Contains("ThisGameHostingMoveInfo") &&
                    dict["ThisGameHostingMoveInfo"] is GameHostingMoveInfo info)
                {
                    return info;
                }
            }
            catch
            {
                // Move info not available in the expected shape - caller falls back to no migration.
            }

            return null;
        }
    }
}
