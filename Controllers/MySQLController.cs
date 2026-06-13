using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using Kendo.Mvc.Extensions;
using Kendo.Mvc.UI;
using MySql.Data.MySqlClient;
using MySqlModule.Helpers;
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
            RestoreMissingVariables();

            var databases = GetUserDatabases();
            var model = new MySqlModel
            {
                Databases = databases,
                CurrentDatabases = databases.Count,
                MaxDatabases = GetUserServicesCount(),
                CreationServiceIds = GetUserServices(),
                EligibleLocations = GetLocations(),
                CreationUsername = GetDbUsername(),
                DeletionUsernames = GetDbDeletionUsernames(),
                ResetUsernames = GetDbResetUsernames()
            };

            return View(model);
        }

        [HttpPost]
        public ActionResult CreateDatabase(int requestServiceId1, string requestDbName)
        {
            if (GetUserDatabases().Count >= GetUserServicesCount())
            {
                return JavaScript(
                    "TCAdmin.Ajax.ShowBasicDialog('Error', 'You have reached your database limit!');$('body').css('cursor', 'default');");
            }

            requestDbName = (requestDbName ?? string.Empty).Trim();

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
                var config = MySqlStore.GetHostConfig(service);

                // Sanitise the TCAdmin username for use in MySQL identifiers — replace any
                // character that isn't alphanumeric or underscore (e.g. hyphens) with an
                // underscore rather than rejecting the whole request.
                var safeUsername = Regex.Replace(user.UserName, "[^_a-zA-Z0-9]", "_");
                var dbUser = $"{safeUsername}_{service.ServiceId}";
                var dbName = $"{safeUsername}_{requestDbName.Replace(" ", "_")}";
                var dbPass = MySqlStore.GeneratePassword();

                if (!config.IsConfigured)
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'An Administrator has not configured the location of this service for the MySql Module!');$('body').css('cursor', 'default');");
                }

                if (dbName.Length > 64 || dbUser.Length > 32)
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'That database name is too long. Please choose a shorter name!');$('body').css('cursor', 'default');");
                }

                try
                {
                    using (var conn = new MySqlConnection(config.GetConnectionString()))
                    {
                        conn.Open();

                        // If this service already has a database on record, its variables were
                        // wiped externally - restore them instead of creating a duplicate.
                        var existing = MySqlStore.TryGetRecord(conn, service.ServiceId);
                        if (existing != null)
                        {
                            MySqlStore.ApplyVariables(service, config.Host, existing.Username, existing.Password, existing.Database);
                            return JavaScript("window.location.reload(false);");
                        }

                        MySqlStore.Execute(conn, $"CREATE DATABASE `{dbName}`;");
                        var createUserCmd = conn.CreateCommand();
                        createUserCmd.CommandText = $"CREATE USER '{dbUser}'@'%' IDENTIFIED BY @pass;";
                        createUserCmd.Parameters.AddWithValue("@pass", dbPass);
                        createUserCmd.ExecuteNonQuery();
                        MySqlStore.Execute(conn, $"GRANT ALL PRIVILEGES ON `{dbName}`.* TO '{dbUser}'@'%';");
                        MySqlStore.Execute(conn, "FLUSH PRIVILEGES;");

                        MySqlStore.TryUpsert(conn, service.ServiceId, dbUser, dbName, dbPass, config.Host);
                    }
                }
                catch
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'Unable to connect to the remote MySQL host. Please contact an Administrator!');$('body').css('cursor', 'default');");
                }

                MySqlStore.ApplyVariables(service, config.Host, dbUser, dbPass, dbName);
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
                var config = MySqlStore.GetHostConfig(service);

                var dbUser = service.Variables[MySqlStore.UsernameVariable]?.ToString();
                var dbName = service.Variables[MySqlStore.DatabaseVariable]?.ToString();

                if (string.IsNullOrEmpty(dbUser) || string.IsNullOrEmpty(dbName))
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'No database is configured for this service!');$('body').css('cursor', 'default');");
                }

                if (!config.IsConfigured)
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'An Administrator has not configured the location of this service for the MySql Module!');$('body').css('cursor', 'default');");
                }

                try
                {
                    using (var conn = new MySqlConnection(config.GetConnectionString()))
                    {
                        conn.Open();
                        MySqlStore.Execute(conn, $"DROP USER IF EXISTS '{dbUser}'@'%';");
                        MySqlStore.Execute(conn, $"DROP DATABASE IF EXISTS `{dbName}`;");
                        MySqlStore.TryDelete(conn, service.ServiceId);
                    }
                }
                catch
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'Unable to connect to the remote MySQL host. Please contact an Administrator!');$('body').css('cursor', 'default');");
                }

                MySqlStore.ClearVariables(service);
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
                var config = MySqlStore.GetHostConfig(service);

                var dbUser = service.Variables[MySqlStore.UsernameVariable]?.ToString();
                var dbName = service.Variables[MySqlStore.DatabaseVariable]?.ToString();
                var dbPass = MySqlStore.GeneratePassword();

                if (string.IsNullOrEmpty(dbUser))
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'No database is configured for this service!');$('body').css('cursor', 'default');");
                }

                if (!config.IsConfigured)
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'An Administrator has not configured the location of this service for the MySql Module!');$('body').css('cursor', 'default');");
                }

                try
                {
                    using (var conn = new MySqlConnection(config.GetConnectionString()))
                    {
                        conn.Open();
                        var alterCmd = conn.CreateCommand();
                        alterCmd.CommandText = $"ALTER USER '{dbUser}'@'%' IDENTIFIED BY @pass;";
                        alterCmd.Parameters.AddWithValue("@pass", dbPass);
                        alterCmd.ExecuteNonQuery();
                        MySqlStore.TryUpsert(conn, service.ServiceId, dbUser, dbName, dbPass, config.Host);
                    }
                }
                catch
                {
                    return JavaScript(
                        "TCAdmin.Ajax.ShowBasicDialog('Error', 'Unable to connect to the remote MySQL host. Please contact an Administrator!');$('body').css('cursor', 'default');");
                }

                service.Variables[MySqlStore.PasswordVariable] = dbPass;
                service.Save();
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }

            return JavaScript("window.location.reload(false);");
        }

        // Repairs services whose _MySQLPlugin::* variables were wiped externally (e.g. by a
        // TCAdmin reinstall) by writing them back from the metadata store, and backfills the
        // store for databases that predate it. Best-effort: never breaks the page.
        private static void RestoreMissingVariables()
        {
            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var user = TCAdmin.SDK.Session.GetCurrentUser();
                var services = Service.GetServices(user, false).Cast<Service>().ToList();

                var groups = new Dictionary<string, KeyValuePair<MySqlHostConfig, List<Service>>>();
                foreach (var service in services)
                {
                    MySqlHostConfig config;
                    try
                    {
                        config = MySqlStore.GetHostConfig(service);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!config.IsConfigured)
                        continue;

                    if (!groups.ContainsKey(config.Host))
                        groups.Add(config.Host, new KeyValuePair<MySqlHostConfig, List<Service>>(config, new List<Service>()));

                    groups[config.Host].Value.Add(service);
                }

                foreach (var group in groups.Values)
                {
                    var config = group.Key;

                    try
                    {
                        using (var conn = new MySqlConnection(config.GetConnectionString()))
                        {
                            conn.Open();
                            MySqlStore.EnsureStore(conn);

                            foreach (var service in group.Value)
                            {
                                var record = MySqlStore.GetRecord(conn, service.ServiceId);

                                if (MySqlStore.HasDatabase(service))
                                {
                                    // Protect databases created before the metadata store existed.
                                    if (record == null)
                                    {
                                        MySqlStore.Upsert(conn, service.ServiceId,
                                            service.Variables[MySqlStore.UsernameVariable].ToString(),
                                            service.Variables[MySqlStore.DatabaseVariable].ToString(),
                                            service.Variables[MySqlStore.PasswordVariable].ToString(),
                                            config.Host);
                                    }
                                }
                                else if (record != null)
                                {
                                    // Variables were wiped externally - write them back.
                                    MySqlStore.ApplyVariables(new Service(service.ServiceId), config.Host,
                                        record.Username, record.Password, record.Database);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Host unreachable - the heal pass must never break the page.
                    }
                }
            }
            catch
            {
                // The heal pass must never break the page.
            }
            finally
            {
                ObjectBase.GlobalSkipSecurityCheck = false;
            }
        }

        private static string GetDbUsername()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            return Regex.Replace(user.UserName, "[^_a-zA-Z0-9]", "_") + "_";
        }

        private static List<SelectListItem> GetDbDeletionUsernames()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            return (from service in services
                where MySqlStore.HasDatabase(service)
                let text = service.Variables[MySqlStore.DatabaseVariable].ToString()
                select new SelectListItem { Text = text, Value = service.ServiceId.ToString() }).ToList();
        }

        private static List<SelectListItem> GetDbResetUsernames()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            return (from service in services
                where MySqlStore.HasDatabase(service)
                let text = service.Variables[MySqlStore.DatabaseVariable].ToString()
                select new SelectListItem { Text = text, Value = service.ServiceId.ToString() }).ToList();
        }

        private static List<MySqlGridViewModel> GetUserDatabases()
        {
            var user = TCAdmin.SDK.Session.GetCurrentUser();
            var services = Service.GetServices(user, false).Cast<Service>().ToList();

            try
            {
                ObjectBase.GlobalSkipSecurityCheck = true;

                var rows = new List<MySqlGridViewModel>();
                foreach (var service in services)
                {
                    if (!MySqlStore.HasDatabase(service))
                        continue;

                    try
                    {
                        var datacenter = new Datacenter(new Server(service.ServerId).DatacenterId);
                        rows.Add(new MySqlGridViewModel(
                            service.Variables[MySqlStore.HostVariable].ToString(),
                            service.Variables[MySqlStore.DatabaseVariable].ToString(),
                            service.Variables[MySqlStore.UsernameVariable].ToString(),
                            service.Variables[MySqlStore.PasswordVariable].ToString(),
                            datacenter.Location, datacenter.MySqlPluginPhpMyAdmin, service.ServiceId.ToString()));
                    }
                    catch
                    {
                        // A service whose server/datacenter no longer exists must not break the
                        // whole grid - skip just that row.
                    }
                }

                return rows;
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
                where !MySqlStore.HasDatabase(service)
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
                foreach (var service in services)
                {
                    try
                    {
                        var datacenter = new Datacenter(new Server(service.ServerId).DatacenterId);
                        if (datacenters.All(x => x.DatacenterId != datacenter.DatacenterId))
                            datacenters.Add(datacenter);
                    }
                    catch
                    {
                        // Skip services with a missing server/datacenter.
                    }
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
