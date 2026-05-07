using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Mvc;
using Kendo.Mvc.Extensions;
using Kendo.Mvc.UI;
using MySql.Data.MySqlClient;
using MySqlModule.Models.MySql;
using TCAdmin.SDK.Objects;
using TCAdmin.SDK.Web.MVC.Controllers;
using Service = TCAdmin.GameHosting.SDK.Objects.Service;

namespace MySqlModule.Controllers
{
    [Authorize]
    public class MySqlController : BaseController
    {
        [HttpGet]
        public ActionResult Index()
        {
            var model = new MySqlModel
            {
                CurrentDatabases = GetUserDatabases().Count,
                MaxDatabases = GetUserServicesCount(),
                CreationServiceIds = GetUserServices(),
                EligibleLocations = GetLocations(),
                CreationUsername = GetDbUsername(),
                DeletionUsernames = GetDbDeletionUsernames(),
                ResetUsernames = GetDbResetUsernames()
            };

            return View(model);
        }

        [HttpGet]
        public ActionResult DatabasesGrid()
        {
            return PartialView("_Databases");
        }

        [HttpPost]
        [ParentAction("MySql", "Index")]
        public ActionResult DatabasesByUserRead([DataSourceRequest] DataSourceRequest request)
        {
            var databases = GetUserDatabases();
            return Json(databases.ToDataSourceResult(request), JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public ActionResult CreateDatabase(int requestServiceId1, string requestDbName)
        {
            if (GetUserDatabases().Count >= GetUserServicesCount())
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You have reached your database limit!');$('body').css('cursor', 'default');");
            }

            if (string.IsNullOrEmpty(requestDbName))
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You did not enter a database name into the textbox!');$('body').css('cursor', 'default');");
            }

            if (!Regex.IsMatch(requestDbName, "^[_a-zA-Z0-9 ]*$"))
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'That database name is not allowed due to illegal characters!');$('body').css('cursor', 'default');");
            }

            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            if (services.Find(x => x.ServiceId == requestServiceId1) == null)
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You don\\'t own this service');$('body').css('cursor', 'default');");
            }

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var service = new Service(requestServiceId1);
                var server = new Server(service.ServerId);
                var datacenter = new Datacenter(server.DatacenterId);

                var dbUser = $"{user.UserName}_{service.ServiceId}";
                var dbName = $"{user.UserName}_{requestDbName.Replace(" ", "_")}";
                var dbPass = System.Web.Security.Membership.GeneratePassword(12, 2);

                string host, rootUser, rootPass;

                if (server.MySqlPluginUseDatacenter && !string.IsNullOrEmpty(datacenter.MySqlPluginIp))
                {
                    host = datacenter.MySqlPluginIp;
                    rootUser = datacenter.MySqlPluginRoot;
                    rootPass = datacenter.MySqlPluginPassword;
                }
                else if (!server.MySqlPluginUseDatacenter && !string.IsNullOrEmpty(server.MySqlPluginIp))
                {
                    host = server.MySqlPluginIp;
                    rootUser = server.MySqlPluginRoot;
                    rootPass = server.MySqlPluginPassword;
                }
                else
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'An Administrator has not configured the location of this service for the MySql Module!');$('body').css('cursor', 'default');");
                }

                try
                {
                    using (var conn = new MySqlConnection($"server={host};user={rootUser};password={rootPass};SSL Mode=None;"))
                    {
                        conn.Open();
                        Execute(conn, $"CREATE DATABASE `{dbName}`;");
                        var createUserCmd = conn.CreateCommand();
                        createUserCmd.CommandText = $"CREATE USER '{dbUser}'@'%' IDENTIFIED BY @pass;";
                        createUserCmd.Parameters.AddWithValue("@pass", dbPass);
                        createUserCmd.ExecuteNonQuery();
                        Execute(conn, $"GRANT ALL PRIVILEGES ON `{dbName}`.* TO '{dbUser}'@'%';");
                        Execute(conn, "FLUSH PRIVILEGES;");
                    }
                }
                catch
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'Unable to connect to the remote MySQL host. Please contact an Administrator!');$('body').css('cursor', 'default');");
                }

                service.Variables["_MySQLPlugin::Host"] = host;
                service.Variables["_MySQLPlugin::Username"] = dbUser;
                service.Variables["_MySQLPlugin::Password"] = dbPass;
                service.Variables["_MySQLPlugin::Database"] = dbName;
                service.Save();
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return JavaScript("window.location.reload(false);");
        }

        [HttpPost]
        public ActionResult DeleteDatabase(int requestServiceId2)
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            if (services.Find(x => x.ServiceId == requestServiceId2) == null)
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You don\\'t own this service');$('body').css('cursor', 'default');");
            }

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var service = new Service(requestServiceId2);
                var server = new Server(service.ServerId);
                var datacenter = new Datacenter(server.DatacenterId);

                var dbUser = service.Variables["_MySQLPlugin::Username"]?.ToString();
                var dbName = service.Variables["_MySQLPlugin::Database"]?.ToString();

                if (string.IsNullOrEmpty(dbUser) || string.IsNullOrEmpty(dbName))
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'No database is configured for this service!');$('body').css('cursor', 'default');");
                }

                string host, rootUser, rootPass;

                if (server.MySqlPluginUseDatacenter)
                {
                    host = datacenter.MySqlPluginIp;
                    rootUser = datacenter.MySqlPluginRoot;
                    rootPass = datacenter.MySqlPluginPassword;
                }
                else
                {
                    host = server.MySqlPluginIp;
                    rootUser = server.MySqlPluginRoot;
                    rootPass = server.MySqlPluginPassword;
                }

                try
                {
                    using (var conn = new MySqlConnection($"server={host};user={rootUser};password={rootPass};SSL Mode=None;"))
                    {
                        conn.Open();
                        Execute(conn, $"DROP USER IF EXISTS '{dbUser}'@'%';");
                        Execute(conn, $"DROP DATABASE IF EXISTS `{dbName}`;");
                    }
                }
                catch
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'Unable to connect to the remote MySQL host. Please contact an Administrator!');$('body').css('cursor', 'default');");
                }

                service.Variables["_MySQLPlugin::Host"] = string.Empty;
                service.Variables["_MySQLPlugin::Username"] = string.Empty;
                service.Variables["_MySQLPlugin::Password"] = string.Empty;
                service.Variables["_MySQLPlugin::Database"] = string.Empty;
                service.Save();
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return JavaScript("window.location.reload(false);");
        }

        [HttpPost]
        public ActionResult ResetPassword(int requestServiceId3)
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            if (services.Find(x => x.ServiceId == requestServiceId3) == null)
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You don\\'t own this service');$('body').css('cursor', 'default');");
            }

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var service = new Service(requestServiceId3);
                var server = new Server(service.ServerId);
                var datacenter = new Datacenter(server.DatacenterId);

                var dbUser = service.Variables["_MySQLPlugin::Username"]?.ToString();
                var dbPass = System.Web.Security.Membership.GeneratePassword(12, 2);

                if (string.IsNullOrEmpty(dbUser))
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'No database is configured for this service!');$('body').css('cursor', 'default');");
                }

                string host, rootUser, rootPass;

                if (server.MySqlPluginUseDatacenter)
                {
                    host = datacenter.MySqlPluginIp;
                    rootUser = datacenter.MySqlPluginRoot;
                    rootPass = datacenter.MySqlPluginPassword;
                }
                else
                {
                    host = server.MySqlPluginIp;
                    rootUser = server.MySqlPluginRoot;
                    rootPass = server.MySqlPluginPassword;
                }

                try
                {
                    using (var conn = new MySqlConnection($"server={host};user={rootUser};password={rootPass};SSL Mode=None;"))
                    {
                        conn.Open();
                        var alterCmd = conn.CreateCommand();
                        alterCmd.CommandText = $"ALTER USER '{dbUser}'@'%' IDENTIFIED BY @pass;";
                        alterCmd.Parameters.AddWithValue("@pass", dbPass);
                        alterCmd.ExecuteNonQuery();
                    }
                }
                catch
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'Unable to connect to the remote MySQL host. Please contact an Administrator!');$('body').css('cursor', 'default');");
                }

                service.Variables["_MySQLPlugin::Password"] = dbPass;
                service.Save();
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return JavaScript("window.location.reload(false);");
        }

        [HttpGet]
        public ActionResult DownloadBackup(int requestServiceId)
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            if (services.Find(x => x.ServiceId == requestServiceId) == null)
                return HttpNotFound();

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var service = new Service(requestServiceId);
                var server = new Server(service.ServerId);
                var datacenter = new Datacenter(server.DatacenterId);

                var dbName = service.Variables["_MySQLPlugin::Database"]?.ToString();
                if (string.IsNullOrEmpty(dbName))
                    return HttpNotFound();

                string host, rootUser, rootPass;

                if (server.MySqlPluginUseDatacenter)
                {
                    host = datacenter.MySqlPluginIp;
                    rootUser = datacenter.MySqlPluginRoot;
                    rootPass = datacenter.MySqlPluginPassword;
                }
                else
                {
                    host = server.MySqlPluginIp;
                    rootUser = server.MySqlPluginRoot;
                    rootPass = server.MySqlPluginPassword;
                }

                string sqlDump;
                using (var conn = new MySqlConnection($"server={host};user={rootUser};password={rootPass};database={dbName};SSL Mode=None;"))
                {
                    conn.Open();
                    sqlDump = GenerateSqlDump(conn, dbName);
                }

                var fileName = $"{dbName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sql";
                return File(Encoding.UTF8.GetBytes(sqlDump), "application/octet-stream", fileName);
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }
        }

        [HttpPost]
        public ActionResult RestoreBackup(int requestServiceId, HttpPostedFileBase backupFile)
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            if (services.Find(x => x.ServiceId == requestServiceId) == null)
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You don\\'t own this service');$('body').css('cursor', 'default');");
            }

            if (backupFile == null || backupFile.ContentLength == 0)
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'No file selected!');$('body').css('cursor', 'default');");
            }

            if (backupFile.ContentLength > 50 * 1024 * 1024)
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'Backup file must be under 50MB!');$('body').css('cursor', 'default');");
            }

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var service = new Service(requestServiceId);
                var server = new Server(service.ServerId);
                var datacenter = new Datacenter(server.DatacenterId);

                var dbName = service.Variables["_MySQLPlugin::Database"]?.ToString();
                if (string.IsNullOrEmpty(dbName))
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'No database is configured for this service!');$('body').css('cursor', 'default');");
                }

                string host, rootUser, rootPass;

                if (server.MySqlPluginUseDatacenter)
                {
                    host = datacenter.MySqlPluginIp;
                    rootUser = datacenter.MySqlPluginRoot;
                    rootPass = datacenter.MySqlPluginPassword;
                }
                else
                {
                    host = server.MySqlPluginIp;
                    rootUser = server.MySqlPluginRoot;
                    rootPass = server.MySqlPluginPassword;
                }

                string sql;
                using (var reader = new StreamReader(backupFile.InputStream, Encoding.UTF8))
                    sql = reader.ReadToEnd();

                try
                {
                    using (var conn = new MySqlConnection($"server={host};user={rootUser};password={rootPass};database={dbName};SSL Mode=None;"))
                    {
                        conn.Open();
                        var script = new MySqlScript(conn, sql);
                        script.Execute();
                    }
                }
                catch (Exception ex)
                {
                    var msg = ex.Message.Replace("'", "\\'").Replace("\r\n", " ").Replace("\n", " ");
                    return JavaScript(
                        $"TCAdmin.Ajax.ShowBasicDialog('Error', 'Restore failed: {msg}');$('body').css('cursor', 'default');");
                }
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return JavaScript(
                "TCAdmin.Ajax.ShowBasicDialog('Success', 'Database restored successfully!');setTimeout(function(){ window.location.reload(false); }, 2000);");
        }

        private static void Execute(MySqlConnection conn, string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static bool HasDatabase(Service service)
        {
            var username = service.Variables["_MySQLPlugin::Username"];
            return username != null && !string.IsNullOrEmpty(username.ToString());
        }

        private static string GenerateSqlDump(MySqlConnection conn, string dbName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- TCAdmin MySQL Module Backup");
            sb.AppendLine($"-- Database: {dbName}");
            sb.AppendLine($"-- Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();
            sb.AppendLine("SET FOREIGN_KEY_CHECKS=0;");
            sb.AppendLine("SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';");
            sb.AppendLine();

            var tables = new List<string>();
            var tablesCmd = conn.CreateCommand();
            tablesCmd.CommandText = "SHOW TABLES;";
            using (var reader = tablesCmd.ExecuteReader())
                while (reader.Read())
                    tables.Add(reader.GetString(0));

            foreach (var table in tables)
            {
                sb.AppendLine($"-- Table: `{table}`");

                var createCmd = conn.CreateCommand();
                createCmd.CommandText = $"SHOW CREATE TABLE `{table}`;";
                string createSql;
                using (var reader = createCmd.ExecuteReader())
                {
                    reader.Read();
                    createSql = reader.GetString(1);
                }

                sb.AppendLine($"DROP TABLE IF EXISTS `{table}`;");
                sb.AppendLine(createSql + ";");
                sb.AppendLine();

                var dataCmd = conn.CreateCommand();
                dataCmd.CommandText = $"SELECT * FROM `{table}`;";
                using (var reader = dataCmd.ExecuteReader())
                {
                    if (!reader.HasRows) continue;

                    var cols = string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                        .Select(i => $"`{reader.GetName(i)}`"));

                    while (reader.Read())
                    {
                        var vals = string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                            .Select(i => FormatSqlValue(reader, i)));

                        // Building SQL text for a downloadable dump file, not executing a query.
                        // table/cols = server metadata from SHOW TABLES/SHOW CREATE TABLE;
                        // vals = output of FormatSqlValue which applies MySqlHelper.EscapeString to all strings.
                        sb.Append("INSERT INTO `").Append(table).Append("` (").Append(cols)
                          .Append(") VALUES (").Append(vals).AppendLine(");");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("SET FOREIGN_KEY_CHECKS=1;");
            return sb.ToString();
        }

        private static string FormatSqlValue(MySqlDataReader reader, int i)
        {
            if (reader.IsDBNull(i)) return "NULL";
            var type = reader.GetFieldType(i);
            var value = reader.GetValue(i);

            if (type == typeof(byte[]))
                return "0x" + BitConverter.ToString((byte[])value).Replace("-", "");
            if (type == typeof(bool))
                return (bool)value ? "1" : "0";
            if (type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) ||
                type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(float) ||
                type == typeof(double) || type == typeof(decimal))
                return value.ToString();
            if (type == typeof(DateTime))
                return $"'{((DateTime)value):yyyy-MM-dd HH:mm:ss}'";

            return $"'{MySqlHelper.EscapeString(value.ToString())}'";
        }

        private static string GetDbUsername()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            return user.UserName + "_";
        }

        private static List<SelectListItem> GetDbDeletionUsernames()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            return (from service in services
                where HasDatabase(service)
                let text = service.Variables["_MySQLPlugin::Database"].ToString()
                select new SelectListItem { Text = text, Value = service.ServiceId.ToString() }).ToList();
        }

        private static List<SelectListItem> GetDbResetUsernames()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            return (from service in services
                where HasDatabase(service)
                let text = service.Variables["_MySQLPlugin::Database"].ToString()
                select new SelectListItem { Text = text, Value = service.ServiceId.ToString() }).ToList();
        }

        private static List<MySqlGridViewModel> GetUserDatabases()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;
                return (from service in services
                    where HasDatabase(service)
                    let mysqlHost = service.Variables["_MySQLPlugin::Host"].ToString()
                    let mysqlUser = service.Variables["_MySQLPlugin::Username"].ToString()
                    let mysqlPass = service.Variables["_MySQLPlugin::Password"].ToString()
                    let mysqlDatabase = service.Variables["_MySQLPlugin::Database"].ToString()
                    let datacenter = new Datacenter(new Server(service.ServerId).DatacenterId)
                    select new MySqlGridViewModel(mysqlHost, mysqlDatabase, mysqlUser, mysqlPass,
                        datacenter.Location, datacenter.MySqlPluginPhpMyAdmin, service.ServiceId.ToString())).ToList();
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }
        }

        private static int GetUserServicesCount()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            return Service.GetServices(user, false).Count;
        }

        private static List<SelectListItem> GetUserServices()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            return (from service in services
                where !HasDatabase(service)
                select new SelectListItem
                    { Text = service.ConnectionInfo + " - " + service.Name, Value = service.ServiceId.ToString() })
                .ToList();
        }

        private static MySqlEligibleLocations GetLocations()
        {
            var datacenters = new List<Datacenter>();
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;
                foreach (var datacenter in services
                    .Select(service => new Datacenter(new Server(service.ServerId).DatacenterId))
                    .Where(datacenter => !datacenters.Any(x => x.DatacenterId == datacenter.DatacenterId)))
                {
                    datacenters.Add(datacenter);
                }
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return new MySqlEligibleLocations(datacenters);
        }
    }
}
