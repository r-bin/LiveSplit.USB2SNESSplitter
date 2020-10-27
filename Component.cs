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
        public const uint DEFAULT_READ_SIZE = 512;

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
            public List<Split> Splits { get; set; }
            public List<Check> Checks { get; set; }

            public string Name { get; set; }
            public string Description { get; set; }
            public string Tooltip { get; set; }
            public bool Active { get; set; } = false;
            public int Repeat { get; set; } = 0;

            public bool IsCategory()
            {
                return Splits?.Count > 0;
            }

            public bool Check(byte[] wram, byte[] data, bool debug)
            {
                if (debug)
                {
                    Log.Info($"split {this.Name}");
                }

                bool valid = true;

                foreach (Check check in Checks)
                {
                    valid &= check.Perform(wram, data, debug);
                }

                return valid;
            }

            public void Reset()
            {
            }

            public (uint Min, uint Max, uint Size) GetCheckRange()
            {
                var min = Checks.Min(c => c.AddressInt);
                var max = Checks.Max(c => c.AddressInt + c.Size());

                var size = max - min;

                return (min, max, size);
            }

            public String DebugDescription()
            {
                return $"{ this.Description ?? this.Name ?? "<Missing Name>"} ({ this.Checks.Count} checks, { this.GetCheckRange().Size} bytes)";
            }
        }

        internal class Check
        {
            public Split Parent { get; set; }

            public string Address { get; set; }
            public string Value { get; set; }
            public string OldValue { get; set; }
            public string Type { get; set; }
            public string Operator { get; set; }

            public uint AddressInt { get { return Address.ToFormattedUInt32(); } }
            public uint ValueInt { get { return Value.ToFormattedUInt32(); } }
            public uint OldValueInt { get { return OldValue.ToFormattedUInt32(); } }

            private uint? _currentValue;
            private uint? _oldValue;
            private int? _delta;

            public bool Perform(byte[] wram, byte[] data, bool debug)
            {
                var types = new Dictionary<string, Func<byte[], uint>>()
                {
                    { "byte", array => (uint)array[0] },
                    { "short", array => (uint)(array[0] + (array[1] << 8)) },
                };
                var operators = new Dictionary<string, Func<uint?, uint?, bool>>()
                {
                    { "&", (value1, value2) => (value1 & value2) == value2 },
                    { "==", (value1, value2) => value1 == value2 },
                    { "!=", (value1, value2) => value1 != value2 },
                    { ">", (value1, value2) => value1 > value2 },
                    { "<", (value1, value2) => value1 < value2 },
                    { ">=", (value1, value2) => value1 >= value2 },
                    { "<=", (value1, value2) => value1 <= value2 },
                    { "delta", (value1, value2) => _delta.HasValue && _delta == value2 },
                    { "o-delta", (value1, value2) => _oldValue.HasValue && (((int)_oldValue + (int)ValueInt) & 0xffff) == _currentValue },
                };

                if (this.Type == null || !types.TryGetValue(this.Type, out Func<byte[], uint> type))
                {
                    type = types["short"];
                }
                _currentValue = type(data.Skip((int) this.AddressInt).ToArray());
                if(wram != null)
                {
                    _oldValue = type(wram.Skip((int)this.AddressInt).ToArray());
                }

                if (_oldValue.HasValue)
                {
                    _delta = (int)_currentValue - (int)_oldValue;
                }

                bool result = true;
                bool resultValid = false;

                String debugInfo = "";
                if (Value != null)
                {
                    result &= operators[this.Operator](_currentValue, ValueInt);
                    resultValid = true;
                    debugInfo += $"{_currentValue}{this.Operator}{this.ValueInt} == {result}";
                }
                if (OldValue != null && _oldValue.HasValue)
                {
                    result &= operators[this.Operator](_oldValue, OldValueInt);
                    resultValid = true;
                    if (debugInfo.Length > 0)
                    {
                        debugInfo += " // ";
                    }
                    debugInfo += $"{_oldValue}{this.Operator}{this.OldValueInt} == {result}";
                }

                if(!resultValid)
                {
                    return false;
                }

                if (debug)
                {
                    Log.Info($"check {this.Address } = ({debugInfo}) == {result} (delta={_delta})");
                }

                return result;
            }

            internal uint Size()
            {
                switch (this.Type)
                {
                    case "byte": return 1;
                    case "short":
                    default: return 2;
                }
            }
        }

        class Game
        {
            public string Name { get; set; }
            public Settings Settings { get; set; }
            public Split Autostart { get; set; }
            public List<Split> Splits { get; set; }
            public String MinVersion { get; set; } = "1.0.0";
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
        private byte[] _wram;

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

            if (devices.Count > 0)
            {
                var device = devices.Contains(_settings.Device) ? _settings.Device : devices.Last();

                _usb2snes.Attach(device);
            }
            
            var info = await _usb2snes.Info(); // Info is the only neutral way to know if we are attached to the device
            if (!String.IsNullOrEmpty(info?.version))
            {
                SetState(MyState.READY);
                _proto_state = ProtocolState.ATTACHED;
            } else
            {
                Disconnect();
            }
        }

        private void Connect()
        {
            if(_proto_state != ProtocolState.NONE)
            {
                return;
            }

            ProtocolState prevState = _proto_state;
            if (_mystate != MyState.CONNECTING && _mystate != MyState.READY)
            {
                SetState(MyState.CONNECTING);
                Task<bool> connectTask = _usb2snes.Connect();
                connectTask.ContinueWith((connectResultTask) =>
                {
                    if (connectResultTask.Result)
                    {
                        _usb2snes.SetName("LiveSplit AutoSplitter");
                        _proto_state = ProtocolState.CONNECTED;
                        WsAttach(prevState);
                    } else
                    {
                        SetState(MyState.NONE);
                        _proto_state = ProtocolState.NONE;
                    }
                });
            }
        }

        private void Disconnect()
        {
            if(_proto_state != ProtocolState.NONE)
            {
                _mystate = MyState.NONE;
                _proto_state = ProtocolState.NONE;
                _usb2snes.Disconnect(); 
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
                if (_aslSettings.GetBasicSettingValue("resethardware"))
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
                await UpdateState();
            }  catch (Exception e)
            {
                Log.Error($"Something bad happened: {e}");
            } finally {
                _inTimer = false;
            }
        }

        public async Task UpdateState()
        {
            if (!IsConfigReady())
            {
                Disconnect();
                _update_timer.Interval = 1000;
            }
            else if (_mystate != MyState.READY)
            {
                _update_timer.Interval = 1000;
                Connect();
            }
            else
            {
                _update_timer.Interval = 33;
                List<Split> splits = GenerateSplitList();

                if (splits == null)
                {
                    return;
                }

                if (await CheckSplits(splits, _state.CurrentSplitIndex))
                {
                    if (splits.Contains(_autostart))
                    {
                        _model.Start();
                    }
                    else
                    {
                        _model.Split();
                    }
                }
            }
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
                    if (Version.Parse(_game.MinVersion).CompareTo(Factory.CURRENT_VERSION) < 0)
                    {
                        throw new Exception($"Newer version of the auto splitter is required");
                    }

                    _autostart = _game.Autostart;
                    SetSplitList();

                    CheckRunnableSetting();

                    if (_autostart != null)
                    {
                        Log.Info($"auto start split detected: {_autostart.DebugDescription()}");
                    }
                    Log.Info($"{_splits.Count} splits detected:");
                    foreach (var split in _splits)
                    {
                        Log.Info($"- {split.DebugDescription()}");
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
            aslSettings.AddBasicSetting("resethardware");
            aslSettings.AddBasicSetting("debug");
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
                    foreach (Split s in split.Splits)
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

        #endregion

        private List<Split> GenerateSplitList()
        {
            if (_aslSettings.GetBasicSettingValue("start") && _state.CurrentPhase == TimerPhase.NotRunning && _autostart != null)
            {
                return new List<Split>() { _autostart };
            }
            else if (_aslSettings.GetBasicSettingValue("split") && _state.CurrentPhase == TimerPhase.Running)
            {
                return _splits.Where(split => _aslSettings.OrderedSettings
                    .Where(setting => split.Name == setting.Id)
                    .Any(setting => setting.Value && (setting.Parent == null || (_aslSettings.OrderedSettings.Where(s3 => s3.Id == setting.Parent).First()?.Value ?? false)))
                    ).ToList();
            }

            return null;
        }

        private async Task<bool> CheckSplits(List<Split> splits, int currentSplitIndex)
        {
            if(currentSplitIndex < 0)
            {
                currentSplitIndex = 0;
            }

            if(splits.Count <= currentSplitIndex)
            {
                return false;
            }

            var split = splits[currentSplitIndex];

            bool shouldSplit = await CheckSplit(split.Checks);

            if (shouldSplit)
            {
                Log.Info($"split[{(split.Name)}] {shouldSplit}");
            }

            return shouldSplit;
        }
        async Task<bool> CheckSplit(List<Check> checks)
        {
            byte[] data = null;

            var min = checks.Min(c => c.AddressInt);
            var max = checks.Max(c => c.AddressInt + c.Size());
            var size = max - min;

            try
            {
                data = await _usb2snes.GetAddress(min.ToWramAddress(), size);
            }
            catch
            {
                Console.WriteLine("GetAddress failed to return result");
                Disconnect();
                return false;
            }
            if (data?.Count() != size)
            {
                Console.WriteLine($"Get address returned ${data.Count()} instead of ${size} bytes");
                Disconnect();
                return false;
            }

            byte[] dataWithOffset = new byte[0x1ffff];
            data.CopyTo(dataWithOffset, min);

            bool result = true;

            foreach (Check check in checks)
            {
                result &= check.Perform(_wram, dataWithOffset, _aslSettings.GetBasicSettingValue("debug"));
            }

            if (_wram == null)
            {
                _wram = new byte[0x1ffff];
            }
            data.CopyTo(_wram, min);

            return result;
        }

        #region Connection Bar Drawing

        public void DrawHorizontal(Graphics graphics, LiveSplitState state, float height, Region clipRegion)
        {
            VerticalHeight = height;
            HorizontalWidth = 3;
        }

        public void DrawVertical(Graphics graphics, LiveSplitState state, float width, Region clipRegion)
        {
            VerticalHeight = 3 + PaddingTop + PaddingBottom;
            HorizontalWidth = width;
            Color color;
            switch (_mystate)
            {
                case MyState.READY: color = _ok_color; break;
                case MyState.CONNECTING: color = _connecting_color; break;
                case MyState.ERROR: color = _error_color; break;
                default: color = _connecting_color; break;
            }
            Brush brush = new SolidBrush(color);
            graphics.FillRectangle(brush, 0, 0, width, 3);
        }

        #endregion
    }
}