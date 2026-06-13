using System;
using MySql.Data.MySqlClient;
using TCAdmin.SDK.Objects;
using Service = TCAdmin.GameHosting.SDK.Objects.Service;

namespace MySqlModule.Helpers
{
    // Resolved MySQL root connection details for a service's server/datacenter.
    internal sealed class MySqlHostConfig
    {
        public string Host;
        public string RootUser;
        public string RootPassword;

        public bool IsConfigured => !string.IsNullOrEmpty(Host);

        public string GetConnectionString() =>
            $"server={Host};user={RootUser};password={RootPassword};SSL Mode=None;";

        public string GetConnectionString(string database) =>
            $"server={Host};user={RootUser};password={RootPassword};database={database};SSL Mode=None;";
    }

    // One row from the module's authoritative metadata store.
    internal sealed class MySqlDatabaseRecord
    {
        public string Username;
        public string Database;
        public string Password;
        public string Host;
    }

    // Shared logic for the MySQL plugin: resolving the root host for a service, and the
    // authoritative metadata store the module keeps on the plugin host itself.
    //
    // TCAdmin rewrites the service variable XML when a service is edited, moved, reinstalled
    // or updated from its game profile, which can silently drop the _MySQLPlugin::* variables
    // and make the customer's database "disappear" from the manager. The metadata store is an
    // independent copy of every database the module creates, used to detect and repair that.
    internal static class MySqlStore
    {
        public const string ModuleGuid = "d3b2aa93-7e2b-4e0d-8080-67d14b2fa8a9";

        public const string HostVariable = "_MySQLPlugin::Host";
        public const string UsernameVariable = "_MySQLPlugin::Username";
        public const string PasswordVariable = "_MySQLPlugin::Password";
        public const string DatabaseVariable = "_MySQLPlugin::Database";

        private const string MetaDb = "tcadmin_mysql_module";
        private const string MetaTable = "service_databases";

        public static MySqlHostConfig GetHostConfig(Server server, Datacenter datacenter)
        {
            if (server.MySqlPluginUseDatacenter && !string.IsNullOrEmpty(datacenter.MySqlPluginIp))
            {
                return new MySqlHostConfig
                {
                    Host = datacenter.MySqlPluginIp,
                    RootUser = datacenter.MySqlPluginRoot,
                    RootPassword = datacenter.MySqlPluginPassword
                };
            }

            if (!server.MySqlPluginUseDatacenter && !string.IsNullOrEmpty(server.MySqlPluginIp))
            {
                return new MySqlHostConfig
                {
                    Host = server.MySqlPluginIp,
                    RootUser = server.MySqlPluginRoot,
                    RootPassword = server.MySqlPluginPassword
                };
            }

            return new MySqlHostConfig();
        }

        public static MySqlHostConfig GetHostConfig(Service service)
        {
            var server = new Server(service.ServerId);
            var datacenter = new Datacenter(server.DatacenterId);
            return GetHostConfig(server, datacenter);
        }

        public static bool HasDatabase(Service service)
        {
            // Require all four variables so a partially wiped service is treated as having no
            // database (and healed from metadata) instead of crashing the grid with nulls.
            return !string.IsNullOrEmpty(service.Variables[HostVariable]?.ToString())
                   && !string.IsNullOrEmpty(service.Variables[UsernameVariable]?.ToString())
                   && !string.IsNullOrEmpty(service.Variables[PasswordVariable]?.ToString())
                   && !string.IsNullOrEmpty(service.Variables[DatabaseVariable]?.ToString());
        }

        public static void ApplyVariables(Service service, string host, string dbUser, string dbPass, string dbName)
        {
            service.Variables[HostVariable] = host;
            service.Variables[UsernameVariable] = dbUser;
            service.Variables[PasswordVariable] = dbPass;
            service.Variables[DatabaseVariable] = dbName;
            service.Save();
        }

        public static void ClearVariables(Service service)
        {
            service.Variables[HostVariable] = string.Empty;
            service.Variables[UsernameVariable] = string.Empty;
            service.Variables[PasswordVariable] = string.Empty;
            service.Variables[DatabaseVariable] = string.Empty;
            service.Save();
        }

        public static void EnsureStore(MySqlConnection conn)
        {
            Execute(conn, $"CREATE DATABASE IF NOT EXISTS `{MetaDb}`;");
            Execute(conn, $"CREATE TABLE IF NOT EXISTS `{MetaDb}`.`{MetaTable}` (" +
                          "`service_id` INT NOT NULL PRIMARY KEY, " +
                          "`db_user` VARCHAR(128) NOT NULL, " +
                          "`db_name` VARCHAR(128) NOT NULL, " +
                          "`db_pass` VARCHAR(128) NOT NULL, " +
                          "`db_host` VARCHAR(255) NOT NULL DEFAULT '', " +
                          "`updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);");

            // Add db_host to tables created by an earlier version that lacked the column.
            EnsureColumn(conn, "db_host", "VARCHAR(255) NOT NULL DEFAULT ''");
        }

        private static void EnsureColumn(MySqlConnection conn, string column, string definition)
        {
            var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM information_schema.columns " +
                                "WHERE table_schema = @db AND table_name = @tbl AND column_name = @col;";
            check.Parameters.AddWithValue("@db", MetaDb);
            check.Parameters.AddWithValue("@tbl", MetaTable);
            check.Parameters.AddWithValue("@col", column);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                Execute(conn, $"ALTER TABLE `{MetaDb}`.`{MetaTable}` ADD COLUMN `{column}` {definition};");
        }

        public static MySqlDatabaseRecord GetRecord(MySqlConnection conn, int serviceId)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT db_user, db_name, db_pass, db_host FROM `{MetaDb}`.`{MetaTable}` WHERE service_id = @id;";
            cmd.Parameters.AddWithValue("@id", serviceId);
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    return null;

                return new MySqlDatabaseRecord
                {
                    Username = reader.GetString(0),
                    Database = reader.GetString(1),
                    Password = reader.GetString(2),
                    Host = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
                };
            }
        }

        public static MySqlDatabaseRecord TryGetRecord(MySqlConnection conn, int serviceId)
        {
            try
            {
                EnsureStore(conn);
                return GetRecord(conn, serviceId);
            }
            catch
            {
                return null;
            }
        }

        public static void Upsert(MySqlConnection conn, int serviceId, string dbUser, string dbName, string dbPass, string dbHost)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO `{MetaDb}`.`{MetaTable}` (service_id, db_user, db_name, db_pass, db_host) " +
                              "VALUES (@id, @user, @name, @pass, @host) " +
                              "ON DUPLICATE KEY UPDATE db_user = @user, db_name = @name, db_pass = @pass, db_host = @host;";
            cmd.Parameters.AddWithValue("@id", serviceId);
            cmd.Parameters.AddWithValue("@user", dbUser);
            cmd.Parameters.AddWithValue("@name", dbName);
            cmd.Parameters.AddWithValue("@pass", dbPass);
            cmd.Parameters.AddWithValue("@host", dbHost ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public static void TryUpsert(MySqlConnection conn, int serviceId, string dbUser, string dbName, string dbPass, string dbHost)
        {
            if (string.IsNullOrEmpty(dbUser) || string.IsNullOrEmpty(dbName) || string.IsNullOrEmpty(dbPass))
                return;

            try
            {
                EnsureStore(conn);
                Upsert(conn, serviceId, dbUser, dbName, dbPass, dbHost);
            }
            catch
            {
                // Metadata is a safety net - never fail the user action over it.
            }
        }

        public static void TryDelete(MySqlConnection conn, int serviceId)
        {
            try
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM `{MetaDb}`.`{MetaTable}` WHERE service_id = @id;";
                cmd.Parameters.AddWithValue("@id", serviceId);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Table may not exist yet on hosts with databases created by older versions.
            }
        }

        public static void Execute(MySqlConnection conn, string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // Cryptographically random alphanumeric password — no special characters so it works
        // safely in connection strings and all MySQL client tools including phpMyAdmin.
        public static string GeneratePassword()
        {
            const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
            {
                var data = new byte[16];
                rng.GetBytes(data);
                var result = new char[16];
                for (var i = 0; i < 16; i++)
                    result[i] = chars[data[i] % chars.Length];
                return new string(result);
            }
        }

        // --- Cross-host migration primitives (used when a service moves to a different MySQL host) ---

        // Full logical dump (schema + data, including views/routines/triggers) of a database.
        public static string ExportDatabase(MySqlHostConfig config, string dbName)
        {
            using (var conn = new MySqlConnection(config.GetConnectionString(dbName)))
            using (var cmd = new MySqlCommand { Connection = conn, CommandTimeout = 0 })
            using (var backup = new MySqlBackup(cmd))
            {
                conn.Open();
                return backup.ExportToString();
            }
        }

        // Creates the database + user (with the supplied password) on a host, idempotently.
        public static void RecreateDatabaseAndUser(MySqlHostConfig config, string dbName, string dbUser, string dbPass)
        {
            using (var conn = new MySqlConnection(config.GetConnectionString()))
            {
                conn.Open();
                Execute(conn, $"CREATE DATABASE IF NOT EXISTS `{dbName}`;");
                Execute(conn, $"DROP USER IF EXISTS '{dbUser}'@'%';");
                var create = conn.CreateCommand();
                create.CommandText = $"CREATE USER '{dbUser}'@'%' IDENTIFIED BY @pass;";
                create.Parameters.AddWithValue("@pass", dbPass);
                create.ExecuteNonQuery();
                Execute(conn, $"GRANT ALL PRIVILEGES ON `{dbName}`.* TO '{dbUser}'@'%';");
                Execute(conn, "FLUSH PRIVILEGES;");
            }
        }

        // Imports a dump produced by ExportDatabase into an existing database on a host.
        public static void ImportDatabase(MySqlHostConfig config, string dbName, string sql)
        {
            using (var conn = new MySqlConnection(config.GetConnectionString(dbName)))
            using (var cmd = new MySqlCommand { Connection = conn, CommandTimeout = 0 })
            using (var backup = new MySqlBackup(cmd))
            {
                conn.Open();
                backup.ImportFromString(sql);
            }
        }
    }
}
