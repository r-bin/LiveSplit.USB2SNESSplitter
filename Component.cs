using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.Options;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using USB2SnesW;
using System.Drawing;
using System.Collections;
using LiveSplit.ASL;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LiveSplit.USB2SNESSplitterTests")]

namespace LiveSplit.UI.Components
{
    public static class MyExtensions
    {
        public static uint ToWramAddress(this uint i)
        {
            if (i > 0xffff)
            {
                throw new Exception($"Invalid WRAM address: {i} > 0xffff");
            }

            return USB2SNESComponent.SD2SNES_WRAM_BASE_ADDRESS + (i & 0xffff);
        }
        public static uint ToFormattedUInt32(this String numberString)
        {
            return Convert.ToUInt32(numberString, numberString.StartsWith("0x") ? 16 : 10);
        }
    }

    public class USB2SNESComponent : IComponent
    {
        #region Init

        public const uint SD2SNES_WRAM_BASE_ADDRESS = 0xF50000;
        public const uint DEFAULT_READ_SIZE = 64;

        enum MyState
        {
            NONE,
            ERROR,
            CONNECTING,
            READY,
        };
        enum ProtocolState // Only when attached we are good
        {
            NONE,
            CONNECTED,
            ATTACHED
        }

        internal class Split
        {
            public Split Parent { get; set; }
            public List<Split> Children { get; set; }

            public bool Active { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Tooltip { get; set; }
            public string Address { get; set; }
            public string Value { get; set; }
            public string Type { get; set; }
            public string Operator { get; set; }
            public int Repeat { get; set; } = 0;
            public List<Split> More { get; set; }
            public List<Split> Next { get; set; }
            public int NextIndex { get; set; } = 0;

            public uint AddressInt { get { return Address.ToFormattedUInt32(); } }
            public uint ValueInt { get { return Value.ToFormattedUInt32(); } }

            public uint? PreviousValueInt { get; set; }

            public bool IsCategory()
            {
                return Children?.Count > 0;
            }

            public bool Check(byte[] data, bool debug)
            {
                var types = new Dictionary<string, Func<byte[], uint>>()
                {
                    { "byte", array => (uint)array[0] },
                    { "short", array => (uint)(array[0] + (array[1] << 8)) },
                };
                var operators = new Dictionary<string, Func<uint, Split, int?, bool>>()
                {
                    { "&", (v, s, d) => (v & s.ValueInt) == v },
                    { "==", (v, s, d) => v == s.ValueInt },
                    { ">", (v, s, d) => v > s.ValueInt },
                    { "<", (v, s, d) => v < s.ValueInt },
                    { ">=", (v, s, d) => v >= s.ValueInt },
                    { "<=", (v, s, d) => v <= s.ValueInt },
                    { "delta==", (v, s, d) => d.HasValue && d == s.ValueInt },
                    { "delta===", (v, s, d) => d.HasValue && (((s.PreviousValueInt + s.ValueInt) & 0xffff) == v) },
                };

                if (this.Type == null || !types.TryGetValue(this.Type, out Func<byte[], uint> type))
                {
                    type = types["short"];
                }
                uint value = type(data);

                int? delta = null;
                if (this.PreviousValueInt.HasValue)
                {
                    delta = (int)value - (int)this.PreviousValueInt;
                }

                bool result = operators[this.Operator](value, this, delta);

                if (debug)
                {
                    var nextString = "-";
                    if (this.Next?.Count() > 0)
                    {
                        nextString = $"{this.NextIndex + 1}/{this.Next?.Count() + 1}";
                    }
                    else if (this.Parent != null)
                    {
                        nextString = $"{this.Parent.NextIndex + 1}/{this.Parent.Next?.Count() + 1}";
                    }
                    Log.Info($"split[{(this.Parent != null ? this.Parent.Name : this.Name)}][{nextString}] {this.Address } = ({value}{this.Operator}{this.ValueInt}) == {result} (delta={delta}, prev={this.PreviousValueInt})");
                }

                this.PreviousValueInt = value;

                return result;
            }

            internal void Reset()
            {
                this.NextIndex = 0;
            }
        }

        class Settings
        {
            public bool Debug { get; set; }
            public bool ResetHardware { get; set; }
            public bool HideConnectionBar { get; set; }
        }

        class Game
        {
            public string Name { get; set; }
            public Settings Settings { get; set; }
            public Split Autostart { get; set; }
            public List<Split> Splits { get; set; }
        }

        public string ComponentName => "USB2SNES Auto Splitter";

        public float HorizontalWidth { get; set; }

        public float MinimumHeight => 3;

        public float VerticalHeight { get; set; }

        public float MinimumWidth => 3;

        public float PaddingTop => 1;

        public float PaddingBottom => 1;

        public float PaddingLeft => 1;

        public float PaddingRight => 1;

        public IDictionary<string, Action> ContextMenuControls => null;

        private USB2SnesW.USB2SnesW _usb2snes;
        private MyState _mystate;
        private ProtocolState _proto_state;
        private bool _stateChanged;

        private LiveSplitState _state;
        private ComponentSettings _settings;
        private ASLSettings _aslSettings;
        private TimerModel _model;
        private Timer _update_timer;
        private FileSystemWatcher _fs_watcher;
        private bool _do_reload = true;
        private string _old_script_path;

        private Game _game;
        private Split _autostart;
        private List<Split> _splits;
        private bool _inTimer;

        private Color _ok_color = Color.FromArgb(0, 128, 0);
        private Color _error_color = Color.FromArgb(128, 0, 0);
        private Color _connecting_color = Color.FromArgb(128, 128, 0);

        private void Init(LiveSplitState state, USB2SnesW.USB2SnesW usb2snesw)
        {
            _usb2snes = usb2snesw;
            _mystate = MyState.NONE;
            _proto_state = ProtocolState.NONE;
            _stateChanged = false;

            _state = state;
            _settings = new ComponentSettings();
            _model = new TimerModel() { CurrentState = _state };
            _state.RegisterTimerModel(_model);
            _state.OnReset += _state_OnReset;
            _state.OnStart += _state_OnStart;

            _update_timer = new Timer() { Interval = 1000 };
            _update_timer.Tick += (sender, args) => UpdateSplitsWrapper();
            _update_timer.Enabled = true;
            _fs_watcher = new FileSystemWatcher();
            _fs_watcher.Changed += async (sender, args) => {
                await Task.Delay(200);
                _do_reload = true;
            };
            _splits = new List<Split>();
            _inTimer = false;

            HorizontalWidth = 3;
            VerticalHeight = 3;
        }

        public USB2SNESComponent(LiveSplitState state)
        {
            Init(state, new USB2SnesW.USB2SnesW());
        }

        internal USB2SNESComponent(LiveSplitState state, USB2SnesW.USB2SnesW usb2snesw)
        {
            Init(state, usb2snesw);

        }

        #endregion

        #region Helpers

        private void ShowMessage(String msg)
        {
            MessageBox.Show(msg, "USB2Snes AutoSplitter");
        }
        private void SetState(MyState state)
        {
            if (_mystate == state)
            {
                return;
            }
            Log.Info($"USB2SNES state = {state}");
            _stateChanged = true;
            _mystate = state;
        }

        private async void WsAttach(ProtocolState prevState)
        {
            List<String> devices;
            try
            {
                devices = await _usb2snes.GetDevices();
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception getting devices: " + e);
                devices = new List<String>();
            }

            if (!devices.Contains(_settings.Device))
            {
                if (prevState == ProtocolState.NONE)
                    Log.Info($"Could not find the device '{_settings.Device}'. Check your configuration or activate your device.");
                return;
            }
            _usb2snes.Attach(_settings.Device);
            var info = await _usb2snes.Info(); // Info is the only neutral way to know if we are attached to the device
            if (info.version == "")
            {
                SetState(MyState.ERROR);
            } else {
                SetState(MyState.READY);
                _proto_state = ProtocolState.ATTACHED;
            }
        }

        private void Connect()
        {
            ProtocolState prevState = _proto_state;
            var connected = _usb2snes.Connected();
            if (_proto_state != ProtocolState.CONNECTED || !connected)
            {
                SetState(MyState.CONNECTING);
                Task<bool> t = _usb2snes.Connect();
                t.ContinueWith((t1) =>
                {
                    if (!t1.Result)
                    {
                        SetState(MyState.NONE);
                        _proto_state = ProtocolState.NONE;
                        return;
                    }
                    _usb2snes.SetName("LiveSplit AutoSplitter");
                    _proto_state = ProtocolState.CONNECTED;
                    WsAttach(prevState);
                });
            } else {
                if (connected)
                    WsAttach(prevState);
            }
        }

        private void _state_OnStart(object sender, EventArgs e)
        {
            foreach (Split split in _splits)
            {
                split.Reset();
            }
        }

        private void _state_OnReset(object sender, TimerPhase value)
        {
            foreach (Split split in _splits)
            {
                split.Reset();
            }

            if (_usb2snes.Connected())
            {
                if (_game?.Settings?.ResetHardware ?? false)
                {
                    _usb2snes.Reset();
                }
            }
        }

        public void Dispose()
        {
            _update_timer?.Dispose();
            _fs_watcher?.Dispose();
            if (_usb2snes.Connected())
            {
                _usb2snes.Disconnect();
            }
            _state.OnStart -= _state_OnStart;
            _state.OnReset -= _state_OnReset;
        }

        public Control GetSettingsControl(LayoutMode mode)
        {
            return _settings;
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            return _settings.GetSettings(document);
        }

        public void SetSettings(XmlNode settings)
        {
            _settings.SetSettings(settings);
        }

        #endregion

        #region Update Timer

        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height,
            LayoutMode mode)
        {
            if (invalidator != null && _stateChanged)
            {
                _stateChanged = false;
                invalidator.Invalidate(0, 0, width, height);
            }
        }

        private bool IsConnectionReady()
        {
            if (_proto_state == ProtocolState.ATTACHED)
            {
                return true;
            }

            Log.Info("Connection failed, trying again...");

            // this method actually does a BLOCKING request-response cycle (!!)
            if (!_usb2snes.Connected())
            {
                SetState(MyState.NONE);
                _proto_state = ProtocolState.NONE;
            }

            Connect();
            return false;
        }

        private async void UpdateSplitsWrapper()
        {
            // "_inTimer" is a very questionable attempt at locking, but it's probably fine here.
            if (_inTimer)
            {
                return;
            }
            _inTimer = true;
            try
            {
                await UpdateSplits();
            }  catch (Exception e)
            {
                Log.Error($"Something bad happened: {e}");
                Connect();
            } finally {
                _inTimer = false;
            }
        }

        public async Task UpdateSplits()
        {
            if (!IsConnectionReady() || !IsConfigReady())
            {
                _update_timer.Interval = 1000;
                return;
            }
            else
            {
                _update_timer.Interval = 33;
            }
                        
            await CheckSplits();
        }

        private bool IsConfigReady()
        {
            if (RequireConfigFileUpdate())
            {
                try
                {
                    var jsonString = File.ReadAllText(_settings.ScriptPath);
                    _game = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Game>(jsonString);
                }
                catch (Exception e)
                {
                    Log.Error($"Could not open split config file, check config file settings: {e.Message}");
                    _settings.ResetASLSettings();
                    return false;
                }

                try
                {
                    _autostart = _game.Autostart;
                    SetSplitList();

                    CheckRunnableSetting();

                    Log.Info($"{_splits.Count} splits detected:");
                    foreach (var split in _splits)
                    {
                        Log.Info($"- {split.Name}");
                    }
                } catch (Exception e)
                {
                    Log.Error($"Splits could not be parsed: {e}");
                    _settings.ResetASLSettings();
                    return false;
                }

                _fs_watcher.Path = Path.GetDirectoryName(_settings.ScriptPath);
                _fs_watcher.Filter = Path.GetFileName(_settings.ScriptPath);
                _fs_watcher.EnableRaisingEvents = true;

                _do_reload = false;
            }

            return !_do_reload;
        }

        private void SetSplitList()
        {
            var aslSettings = new ASLSettings();
            if (_autostart != null)
            {
                aslSettings.AddBasicSetting("start");
            }
            if (_game.Splits.Count > 0)
            {
                aslSettings.AddBasicSetting("split");
            }

            _splits.Clear();
            foreach (Split split in _game.Splits)
            {
                aslSettings.AddSetting(split.Name, split.Active || split.IsCategory(), split.Description, null);
                if (!split.IsCategory())
                {
                    _splits.AddRange(Enumerable.Repeat(split, split.Repeat + 1).ToList());
                }
                else
                {
                    foreach (Split s in split.Children)
                    {
                        _splits.AddRange(Enumerable.Repeat(s, s.Repeat + 1).ToList());
                        aslSettings.AddSetting(s.Name, s.Active, s.Description, split.Name);
                    }
                }
            }

            _aslSettings = aslSettings;
            _settings.SetASLSettings(aslSettings);
        }

        private bool CheckRunnableSetting()
        {
            if (_game == null)
            {
                return false;
            }

            if (_game.Splits.Count == 0)
            {
                Log.Error("The config file contains no splits.");
                return false;
            }

            return true;
        }

        private bool RequireConfigFileUpdate()
        {
            if (_old_script_path == null || _settings.ScriptPath != _old_script_path)
            {
                _old_script_path = _settings.ScriptPath;
                _do_reload = true;
            }

            if (string.IsNullOrEmpty(_settings.ScriptPath))
            {
                _fs_watcher.EnableRaisingEvents = false;
            }

            return _do_reload;
        }

        private async Task CheckSplits()
        {
            Split split = null;
            if (_aslSettings.GetBasicSettingValue("start") && _state.CurrentPhase == TimerPhase.NotRunning && _autostart != null)
            {
                split = _autostart;
            }
            else if (_aslSettings.GetBasicSettingValue("split") && _state.CurrentPhase == TimerPhase.Running)
            {
                var splits = _splits.Where(s => _aslSettings.OrderedSettings.Where(s2 => s.Name == s2.Id).Any(s2 => s2.Value && (s2.Parent == null || (_aslSettings.OrderedSettings.Where(s3 => s3.Id == s2.Parent).First()?.Value ?? false))));
                if (splits.Count() > _state.CurrentSplitIndex)
                {
                    split = splits.ToArray()[_state.CurrentSplitIndex];
                }
            }

            if (split == null)
            {
                return;
            }

            var orignSplit = split;
            if (split.Next != null && split.NextIndex != 0)
            {
                split = split.Next[split.NextIndex - 1];
                split.Parent = orignSplit;
            }
            bool ok = await CheckSplit(split);
            if (orignSplit.Next != null && ok)
            {
                if (orignSplit.NextIndex < orignSplit.Next.Count())
                {
                    orignSplit.NextIndex++;
                    ok = false;
                }
                else
                {
                    orignSplit.NextIndex = 0;
                }
            }
            if (split.More != null)
            {
                foreach (var moreSplit in split.More)
                {
                    if (!ok)
                    {
                        break;
                    }
                    ok = ok && await CheckSplit(moreSplit);
                }
            }

            if (ok)
            {
                Log.Info($"split[{(orignSplit.Name)}] {orignSplit.Address}{orignSplit.Operator}{orignSplit.ValueInt}");

                if (orignSplit == _autostart)
                {
                    _model.Start();
                } else {
                    _model.Split();
                }
            }
        }

        async Task<bool> CheckSplit(Split split)
        {
            byte[] data;
            try
            {
                data = await _usb2snes.GetAddress(split.AddressInt.ToWramAddress(), DEFAULT_READ_SIZE);
            }
            catch
            {
                return false;
            }
            if (data.Count() == 0)
            {
                Console.WriteLine("Get address failed to return result");
                return false;
            }
            return split.Check(data, _game?.Settings?.Debug ?? false);
        }

        #endregion

        #region Connection Bar Drawing

        public void DrawHorizontal(Graphics graphics, LiveSplitState state, float height, Region clipRegion)
        {
            VerticalHeight = height;
            HorizontalWidth = 3;
        }

        public void DrawVertical(Graphics graphics, LiveSplitState state, float width, Region clipRegion)
        {
            if(_game?.Settings?.HideConnectionBar ?? false)
            {
                return;
            }

            VerticalHeight = 3 + PaddingTop + PaddingBottom;
            HorizontalWidth = width;
            Color color;
            switch (_mystate)
            {
                case MyState.READY: color = _ok_color; break;
                case MyState.CONNECTING: color = _connecting_color; break;
                default: color = _error_color; break;
            }
            Brush brush = new SolidBrush(color);
            graphics.FillRectangle(brush, 0, 0, width, 3);
        }

        #endregion
    }
}
