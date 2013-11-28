﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PureSeeder.Core.Annotations;
using PureSeeder.Core.Configuration;
using System.Linq;

namespace PureSeeder.Core.Context
{
    public interface IDataContext
    {
        IList<Server> Servers { get; }
        int? CurrentPlayers { get; set; }
        int? ServerMaxPlayers { get; set; }
        bool? HangProtectionStatus { get; set; }
        bool SeedStatus { get; set; }
        string Username { get; set; }

        void UpdateStatus(string pageData);

        event PropertyChangedEventHandler PropertyChanged;
    }

    public class CurrentContext : BindableBase, IDataContext
    {
        private readonly IPureConfigHelper _configHelper;
        private readonly IDataContextUpdater[] _updaters;

        public CurrentContext(IPureConfigHelper configHelper, [NotNull] IDataContextUpdater[] updaters)
        {
            if (configHelper == null) throw new ArgumentNullException("configHelper");
            if (updaters == null) throw new ArgumentNullException("updaters");
            _configHelper = configHelper;
            _updaters = updaters;

            LoadFromConfig();
        }

        public IList<Server> Servers
        {
            get { return _servers; }
            private set { SetProperty(ref _servers, value); }
        }
         
        private IList<Server> _servers = null; 
        private int? _currentPlayers = null;
        private int? _serverMaxPlayers = null;
        private string _username = null;
        private bool? _hangProtectionStatuis = null;

        public int? CurrentPlayers
        {
            get { return this._currentPlayers; }
            set { SetProperty(ref _currentPlayers, value); }
        }
        public int? ServerMaxPlayers
        {
            get { return this._serverMaxPlayers; }
            set { SetProperty(ref _serverMaxPlayers, value); }
        }

        public string Username
        {
            get { return this._username; }
            set { SetProperty(ref _username, value); }
        }
        public bool? HangProtectionStatus
        {
            get { return this._hangProtectionStatuis; }
            set { SetProperty(ref _hangProtectionStatuis, value); }
        }

        public bool SeedStatus { get; set; }


        public void UpdateStatus(string pageData)
        {
            foreach (var updater in _updaters)
            {
                updater.UpdateContextData(this, pageData);
            }
        }

        private void LoadFromConfig()
        {
            Servers = _configHelper.GetServers();
            Username = _configHelper.GetSetting<string>(Constants.SettingNames.Username);
            HangProtectionStatus = _configHelper.GetSetting<bool?>(Constants.SettingNames.EnableGameHangProtection);
        }
    }

    public interface IDataContextUpdater
    {
        void UpdateContextData(IDataContext context, string pageData);
    }

    class PlayerCountsUpdater : IDataContextUpdater
    {
        public void UpdateContextData(IDataContext context, string pageData)
        {
            //""slots"".*?""2"":{""current"":(.*?),""max"":(.*?)}
            var curPlayersRegEx = new Regex(@"""slots"".*?""2"":{""current"":(.*?),""max"":(.*?)}");

            var curPlayers = curPlayersRegEx.Match(pageData);

            if (!curPlayers.Success)
            {
                context.CurrentPlayers = null;
                context.ServerMaxPlayers = null;
                return;
            }

            int currentPlayers, maxPlayers;

            int.TryParse(curPlayers.Groups[1].Value, out currentPlayers);
            int.TryParse(curPlayers.Groups[2].Value, out maxPlayers);

            context.CurrentPlayers = currentPlayers;
            context.ServerMaxPlayers = maxPlayers;
        }
    }

    public class CurrentPlayerCountException : ContextUpdateException
    {
        
    }

    public class ContextUpdateException : Exception
    {
        
    }

    
    public abstract class BindableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = this.PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] String propertyName = null)
        {
            if (object.Equals(storage, value)) return false;

            storage = value;
            this.OnPropertyChanged(propertyName);
            return true;
        }
    }
}