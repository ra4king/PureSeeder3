﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PureSeeder.Core.Annotations;
using System.Linq;
using PureSeeder.Core.Configuration;
using PureSeeder.Core.Monitoring;
using PureSeeder.Core.ServerManagement;
using PureSeeder.Core.Settings;
using Server = PureSeeder.Core.Settings.Server;

//using Server = PureSeeder.Core.Configuration.Server;

namespace PureSeeder.Core.Context
{
    public delegate void ContextUpdatedHandler(object sender, EventArgs e);
    public delegate void HangProtectionInvokedHandler(object sender, EventArgs e);
    
    public class SeederContext : IDataContext
    {
        private readonly SessionData _sessionData;
        private readonly BindableSettings _bindableSettings;
        private readonly IDataContextUpdater[] _updaters;
        private readonly IServerStatusUpdater _serverStatusUpdater;
        private readonly IPlayerStatusGetter _playerStatusGetter;

        public SeederContext(SessionData sessionData, BindableSettings bindableSettings, IDataContextUpdater[] updaters,
            [NotNull] IServerStatusUpdater serverStatusUpdater,
            [NotNull] IPlayerStatusGetter playerStatusGetter)
        {
            if (sessionData == null) throw new ArgumentNullException("sessionData");
            if (bindableSettings == null) throw new ArgumentNullException("bindableSettings");
            if (updaters == null) throw new ArgumentNullException("updaters");
            if (serverStatusUpdater == null) throw new ArgumentNullException("serverStatusUpdater");
            if (playerStatusGetter == null) throw new ArgumentNullException("playerStatusGetter");

            _sessionData = sessionData;
            _bindableSettings = bindableSettings;
            _updaters = updaters;
            _serverStatusUpdater = serverStatusUpdater;
            _playerStatusGetter = playerStatusGetter;

            _sessionData.ServerStatuses.SetInnerServerCollection(_bindableSettings.Servers);
        }

        public SessionData Session { get { return _sessionData; } }
        public BindableSettings Settings { get { return _bindableSettings; }}

        public void ExportSettings(string filename)
        {
            var json = JsonConvert.SerializeObject(_bindableSettings, Formatting.Indented);
            File.WriteAllText(filename, json);
        }

        public void ImportSettings(string filename)
        {
            var jsonText = File.ReadAllText(filename);
            var newSettings = PartialObject<BindableSettings>.Create(jsonText);

            // Todo: This probably isn't the best way of doing this
            //  - should probably have some method of denoting if a setting is importable, then it should run automatically

            newSettings.MergeItem((BindableSettings x) => x.RefreshInterval, _bindableSettings);

            // Note: This is a little hacky but I'm not sure how to trigger a refresh on the binding when replacing the entire list
            var servers = new Servers();
            newSettings.MergeItem(x => x.Servers, ref servers);

            if (servers.Any())
            {
                _bindableSettings.Servers.Clear();
                _bindableSettings.CurrentServer = 0;
                foreach (var server in servers)
                {
                    _bindableSettings.Servers.Add(server);
                }
            }
        }

        public Task UpdateServerStatuses()
        {
            return _serverStatusUpdater.UpdateServerStatuses(this);
        }

        public void UpdateStatus(string pageData)
        {
            foreach (var updater in _updaters)
            {
                updater.UpdateContextData(this, pageData);
            }

            OnContextUpdated();
        }

        // Todo: This should be abstracted and injected
        public bool BfIsRunning()
        {
            // Todo: The process name should be injected so it can work with BF3
            var bfProcess = Process.GetProcessesByName("bf4"); // Process name is bf4.exe (in Details tab of Task Manager)
            
            return bfProcess.Length != 0;
        }

        

        public void StopGame()
        {
            if (!BfIsRunning())
                return;

            var process = Process.GetProcessesByName(_sessionData.CurrentGame.ProcessName).FirstOrDefault();

            if(process != null)
                process.Close();
        }

        public PlayerStatus GetPlayerStatus()
        {
            return _playerStatusGetter.GetPlayerStatus(this);
        }

        public Server CurrentServer 
        { 
            get
            {
                if (_bindableSettings.Servers.Count == 0)
                    return null;
                if (_bindableSettings.CurrentServer < 0)
                    _bindableSettings.CurrentServer = 0;

                return _bindableSettings.Servers[_bindableSettings.CurrentServer];
            }
        }


        public event ContextUpdatedHandler OnContextUpdate;
        
        public void JoinServer()
        {
            SpinUpMinimizer();
        }

        private async void SpinUpMinimizer()
        {
            if (!this._bindableSettings.AutoMinimizeGame)
                return;

            var cts = new CancellationTokenSource();
            cts.CancelAfter(300 * 1000);  // Cancel the background task after 5 minutes.

            var minimizerCt = cts.Token;
            await new GameMinimizer().MinimizeGameOnce(minimizerCt, () => Session.CurrentGame);
        }

        private void OnContextUpdated()
        {
            var handler = OnContextUpdate;
            if (handler != null)
                handler(this, new EventArgs());
        }


        public UserStatus GetUserStatus()
        {
            if(String.Equals(_sessionData.CurrentLoggedInUser, _bindableSettings.Username, StringComparison.InvariantCultureIgnoreCase))
                return UserStatus.Correct;
            if(String.Equals(_sessionData.CurrentLoggedInUser, Constants.NotLoggedInUsername, StringComparison.InvariantCultureIgnoreCase))
                return UserStatus.None;

            return UserStatus.Incorrect;
        }

        public ResultReason<ShouldNotSeedReason> ShouldSeed()
        {
            if(_bindableSettings.Servers.Count == 0)
                return new ResultReason<ShouldNotSeedReason>(false, ShouldNotSeedReason.NoServerDefined);

            if (!Session.SeedingEnabled)
                return new ResultReason<ShouldNotSeedReason>(false, ShouldNotSeedReason.SeedingDisabled);
            
            if(GetUserStatus() == UserStatus.None)
                return new ResultReason<ShouldNotSeedReason>(false, ShouldNotSeedReason.NotLoggedIn);

            if(GetUserStatus() == UserStatus.Incorrect)
                return new ResultReason<ShouldNotSeedReason>(false, ShouldNotSeedReason.IncorrectUser);

            if(BfIsRunning())
                return new ResultReason<ShouldNotSeedReason>(false, ShouldNotSeedReason.GameAlreadyRunning);

            // Deprecated
//            if(_sessionData.CurrentPlayers > CurrentServer.MinPlayers)
//                return new ResultReason<ShouldNotSeedReason>(false, ShouldNotSeedReason.NotInRange);

            return new ResultReason<ShouldNotSeedReason>(true);
        }

        public ResultReason<KickReason> ShouldKick()
        {
            if(!BfIsRunning())
                return new ResultReason<KickReason>(false, KickReason.GameNotRunning);

            if(_bindableSettings.Servers.Count == 0)
                return new ResultReason<KickReason>(false, KickReason.NoServerDefined);

            // Deprecated
//            if(_sessionData.CurrentPlayers > CurrentServer.MaxPlayers)
//                return new ResultReason<KickReason>(true, KickReason.AboveSeedingRange);

            return new ResultReason<KickReason>(false);
        }
    }

    class GetPlayerStatusFromBrowser : IPlayerStatusGetter
    {
        public PlayerStatus GetPlayerStatus(IDataContext context)
        {
            throw new NotImplementedException();
        }
    }

    class PlayerStatusGetter : IPlayerStatusGetter
    {
        public PlayerStatus GetPlayerStatus(IDataContext context)
        {
            var player = context.Session.CurrentLoggedInUser;
            if (player == Constants.NotLoggedInUsername)
                return new PlayerStatus(null, null);

            var httpClient = new HttpClient();

            var response = httpClient.GetStringAsync("http://battlelog.battlefield.com/bf4/").Result;

            var jsonRegex = new Regex(@"Surface\.globalContext = (.*)", RegexOptions.Singleline);

            var jsonMatch = jsonRegex.Match(response);

            if(!jsonMatch.Success)
                return new PlayerStatus(null, null);

            var json = jsonMatch.Groups[1].Value;

            var jObject = JObject.Parse(json);

            if(jObject == null)
                return new PlayerStatus(null, null);

            var user = jObject["session"]["user"];

            if(user == null)
                return new PlayerStatus(null, null);

            var username = user["username"].Value<string>();
            var currentServer = user["presence"]["playingMP"]["serverGuid"].Value<string>();

            return new PlayerStatus(username, currentServer);
        }
    }
}
