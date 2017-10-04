﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using DML.AppCore.Classes;
using DML.Application.Dialogs;
using DML.Client;
using LiteDB;
using SweetLib.Utils;
using SweetLib.Utils.Logger;
using SweetLib.Utils.Logger.Memory;
using Logger = SweetLib.Utils.Logger.Logger;

namespace DML.Application.Classes
{
    public static class Core
    {
        //internal static DiscordSocketClient Client { get; set; }
        internal static LiteDatabase Database { get; set; }
        internal static Settings Settings { get; set; }
        internal static JobScheduler Scheduler { get; } = new JobScheduler();

        internal static string DataDirectory
           => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Serraniel\Discord Media Loader");

        public static async Task Run(string[] paramStrings)
        {
            try
            {
                var splash = new FrmInternalSplash();
                splash.Show();
                System.Windows.Forms.Application.DoEvents();

                Logger.Info("Starting up Discord Media Loader application...");
                var useTrace = false;
#if DEBUG
                //temporary add debug log level if debugging...
                Logger.GlobalLogLevel |= LogLevel.Debug;
                Logger.Debug("Running in debug configuartion. Added log level debug.");
#endif

                Logger.Debug($"Parameters: {string.Join(", ", paramStrings)}");
                if (paramStrings.Contains("--trace") || paramStrings.Contains("-t"))
                {
                    useTrace = true;
                    Logger.GlobalLogLevel |= LogLevel.Trace;
                    Logger.Trace("Trace parameter found. Added log level trace.");
                }

                Logger.Debug($"Application data folder: {DataDirectory}");

                Logger.Trace("Checking application data folder...");
                if (!Directory.Exists(DataDirectory))
                {
                    Logger.Debug("Creating application data folder...");
                    Directory.CreateDirectory(DataDirectory);
                    Logger.Trace("Creating application data folder.");
                }

                Logger.Trace("Initializing profile optimizations...");
                ProfileOptimization.SetProfileRoot(System.Windows.Forms.Application.UserAppDataPath);
                ProfileOptimization.StartProfile("profile.opt");
                Logger.Trace("Finished initializing profile optimizations.");

                Logger.Trace("Trying to identify log memory...");
                var logMemory = Logger.DefaultLogMemory as ArchivableConsoleLogMemory;
                if (logMemory != null)
                {
                    var logFolder = Path.Combine(DataDirectory, "logs");
                    if (!Directory.Exists(logFolder))
                    {
                        Logger.Debug("Creating log folder...");
                        Directory.CreateDirectory(logFolder);
                        Logger.Trace("Created log folder.");
                    }


                    var logFile = Path.Combine(logFolder,
                        SweetUtils.LegalizeFilename($"{DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.SortableDateTimePattern)}.log.zip"));

                    Logger.Trace($"Setting log file: {logFile}");
                    logMemory.AutoArchiveOnDispose = true;
                    logMemory.ArchiveFile = logFile;
                }

                Logger.Debug("Loading database...");
                Database = new LiteDatabase(Path.Combine(DataDirectory, "config.db"));
                Database.Log.Logging += (message) => Logger.Trace($"LiteDB: {message}");

                Logger.Debug("Loading settings collection out of database...");
                var settingsDB = Database.GetCollection<Settings>("settings");
                if (settingsDB.Count() > 1)
                {
                    Logger.Warn("Found more than one setting. Loading first one...");
                }
                Settings = settingsDB.FindAll().FirstOrDefault();
                if (Settings == null)
                {
                    Logger.Warn("Settings not found. Creating new one. This is normal on first start up...");
                    Settings = new Settings();
                    Settings.Store();
                }

                Logger.Debug("Loading jobs collection out of database...");
                Scheduler.JobList = Job.RestoreJobs().ToList();

                Logger.Info("Loaded settings.");
                Logger.Debug(
                    $"Settings: Email: {Settings.Email}, password: {(string.IsNullOrEmpty(Settings.Password) ? "not set" : "is set")}, use username: {Settings.UseUserData}, loginToken: {Settings.LoginToken}");

                Logger.Trace("Updating log level...");
                Logger.GlobalLogLevel = Settings.ApplicactionLogLevel;
#if DEBUG
                //temporary add debug log level if debugging...
                Logger.GlobalLogLevel |= LogLevel.Debug;
                Logger.Debug("Running in debug configuartion. Added log level debug.");
#endif
                if (useTrace)
                {
                    Logger.GlobalLogLevel |= LogLevel.Trace;
                    Logger.Trace("Creating application data folder.");
                }

                Logger.Debug("Creating discord client...");

                var config = new DiscordSocketConfig()
                {
                    DefaultRetryMode = RetryMode.AlwaysRetry,
                };

                //Client = new DiscordSocketClient(config);
                DMLClient.Client.Log += (arg) =>
                {
                    var logMessage = $"DiscordClient: {arg.Message}";
                    switch (arg.Severity)
                    {
                        case LogSeverity.Verbose:
                            Logger.Trace(logMessage);
                            break;
                        case LogSeverity.Debug:
                            Logger.Trace(logMessage);
                            break;
                        case LogSeverity.Info:
                            Logger.Info(logMessage);
                            break;
                        case LogSeverity.Warning:
                            Logger.Warn(logMessage);
                            break;
                        case LogSeverity.Error:
                            Logger.Error(logMessage);
                            break;
                    }

                    return Task.CompletedTask;
                };


                Logger.Info("Trying to log into discord...");
                var abort = false;

                DMLClient.Client.Connected += Client_Connected;

                var loggedIn = false;

                while (!loggedIn)
                {
                    if (!string.IsNullOrEmpty(Settings.LoginToken))
                    {
                        Logger.Debug("Trying to login with last known token...");
                        loggedIn= await DMLClient.Login(Settings.LoginToken);
                    }

                    if (!loggedIn)
                    {
                        Logger.Debug("Showing dialog for username and password...");
                        var loginDlg = new LoginDialog();
                        loginDlg.ShowDialog();
                    }
                }

                /*while ((Client.LoginState != LoginState.LoggedIn || Client.ConnectionState!=ConnectionState.Connected) && !abort)
                {
                    Logger.Debug(Client.ConnectionState.ToString());
                    Logger.Debug(Client.LoginState.ToString());

                    Logger.Trace("Entering login loop.");

                    try
                    {
                        if (Client.ConnectionState == ConnectionState.Connecting)
                            continue;

                        if (!string.IsNullOrEmpty(Settings.LoginToken))
                        {
                            Logger.Debug("Trying to login with last known token...");
                            await Client.LoginAsync(TokenType.User, Settings.LoginToken);
                            await Client.StartAsync();
                            await Task.Delay(1000);
                        }

                    }
                    catch (HttpException ex)
                    {
                        Logger.Warn($"Login seems to have failed or gone wrong: {ex.GetType().Name} - {ex.Message}");
                    }

                    if (Client.LoginState == LoginState.LoggedOut)
                    {
                        Settings.Password = string.Empty;
                        Logger.Debug("Showing dialog for username and password...");
                        var loginDlg = new LoginDialog();
                        loginDlg.ShowDialog();
                        Logger.Trace("Dialog closed.");
                    }
                }*/

                Logger.Debug("Start checking for invalid jobs...");

                //Client

                while (DMLClient.Client.Guilds.Count == 0)
                {
                    // wait until guilds are loaded
                }

                for (var i = Scheduler.JobList.Count - 1; i >= 0; i--)
                {
                    var job = Scheduler.JobList[i];
                    var isError = false;
                    var guild = FindServerById(job.GuildId);
                    if (guild == null)
                        isError = true;
                    else
                    {
                        var channel = FindChannelById(guild, job.ChannelId);
                        if (channel == null)
                            isError = true;
                    }

                    if (isError)
                    {
                        MessageBox.Show($"Invalid job for guild {job.GuildId}, channel {job.ChannelId} found. Guild or channel may not exist any more. This job will be deleted...", "Invalid job",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);

                        Scheduler.JobList.Remove(job);
                        Scheduler.RunningJobs.Remove(job.Id);
                        job.Stop();
                        job.Delete();
                    }
                }

                splash.Close();

                Logger.Info("Starting scheduler...");
                Scheduler.Start();

                System.Windows.Forms.Application.Run(new MainForm());

                Logger.Info("Stopping scheduler...");
                Scheduler.Stop();
            }
            catch (Exception ex)
            {
                Logger.Error($"{ex.Message} occured at: {ex.StackTrace}");
            }
        }

        private static Task Client_Connected()
        {
            Logger.Debug("Connected");
            return Task.CompletedTask;
        }

        //TODO: this is thrid time we implement this.....this has to be fixed!!!
        private static SocketGuild FindServerById(ulong id)
        {
            Logger.Trace($"Trying to find server by Id: {id}");
            return (from s in DMLClient.Client.Guilds where s.Id == id select s).FirstOrDefault();
        }

        private static SocketTextChannel FindChannelById(SocketGuild server, ulong id)
        {
            Logger.Trace($"Trying to find channel in {server} by id: {id}");
            return (from c in server.TextChannels where c.Id == id select c).FirstOrDefault();
        }
    }
}
